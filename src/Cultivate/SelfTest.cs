using System.IO.Compression;
using Spectre.Console;

namespace Cultivate;

internal static class SelfTest
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TEST FAILED: " + message);
        assertions++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException("TEST FAILED (expected rejection): " + message);
    }

    public static async Task<int> Run()
    {
        var home = Path.Combine(Files.Home, "Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Bridge? cleanupBridge = null;
        try
        {
            CheckTerminal();
            await CheckVisualStudio();
            await CheckSolutions(home);
            CheckUpdater(home);
            // Fixtures need only MSBuild; they never read or write the user's saved settings.
            var settings = await Settings.Read();
            if (!File.Exists(settings.MSBuild))
                throw new FileNotFoundException("The self-test needs Visual Studio MSBuild and the .NET Framework 4.7.1 targeting pack.", settings.MSBuild);
            settings.RepositoryRoot = Path.Combine(home, "repository with spaces");
            settings.WebfarmRoot = Path.Combine(home, "farm");
            settings.StateRoot = Path.Combine(home, "state");
            Directory.CreateDirectory(settings.RepositoryRoot);
            Directory.CreateDirectory(settings.WebfarmRoot);
            var git = new GitRepository(settings);
            var bridge = new Bridge(settings);
            cleanupBridge = bridge;
            var planner = new Planner(settings, git, bridge);
            var pipeline = new Pipeline(settings, git, bridge);
            async Task G(params string[] args) => _ = await git.Git(settings.RepositoryRoot, args);
            void Save(string relative, string content)
            {
                var path = Files.Child(settings.RepositoryRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
            await G("init", "-b", "master");
            await G("config", "user.email", "cultivate-test@localhost");
            await G("config", "user.name", "Cultivate isolated tests");
            Save(".gitignore", "bin/\nobj/\npackages/\n.cultivate-artifacts/\n");
            Save("MyMeyer.sln", "");
            var packageId = "Cultivate.RestoreFixture." + Guid.NewGuid().ToString("N");
            var packageName = packageId + ".1.0.0";
            var feed = Path.Combine(home, "feed");
            Directory.CreateDirectory(feed);
            using (var package = ZipFile.Open(Path.Combine(feed, packageName + ".nupkg"), ZipArchiveMode.Create))
            {
                void Entry(string name, string content)
                {
                    using var writer = new StreamWriter(package.CreateEntry(name).Open());
                    writer.Write(content);
                }
                Entry(packageId + ".nuspec", $$"""
                    <?xml version="1.0"?>
                    <package><metadata><id>{{packageId}}</id><version>1.0.0</version>
                      <authors>Cultivate tests</authors><description>Offline restore fixture</description>
                    </metadata></package>
                    """);
                Entry("build/Cultivate.RestoreFixture.props", """
                    <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                      <PropertyGroup><CultivateRestoreFixtureImported>true</CultivateRestoreFixtureImported></PropertyGroup>
                    </Project>
                    """);
            }
            Save("NuGet.Config", $$"""
                <configuration><packageSources><clear />
                  <add key="fixture" value="{{System.Security.SecurityElement.Escape(feed)}}" />
                </packageSources></configuration>
                """);
            Save(@"Core\packages.config", $$"""
                <packages><package id="{{packageId}}" version="1.0.0" targetFramework="net471" /></packages>
                """);
            var coreProject = Project("Core", restorePackage: packageName);
            Save(@"Core\Core.csproj", coreProject);
            Save(@"Core\Value.cs", "public class CoreValue { public static int Value => 1; }");
            Save(@"Page\Page.csproj", Project("Page", "Core"));
            Save(@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value; }");
            Save(@"Consumer\Consumer.csproj", Project("Consumer", "Page"));
            Save(@"Consumer\Value.cs", "public class ConsumerValue { public static int Value => PageValue.Value; }");
            foreach (var name in new[] { "Core", "Page", "Consumer" })
                Save(name + @"\App.config", "<configuration />");
            Save(@"Page\View.ascx", "original markup");

            var baseline = new Plan
            {
                Target = new("baseline fixture", settings.RepositoryRoot, "refs/heads/master"),
                Commit = "", Fingerprint = "",
                Projects = new[] { "Core", "Page", "Consumer" }
                    .Select(name => ProjectInfo.Read(settings.RepositoryRoot, name + @"\" + name + ".csproj")).ToList()
            };
            var setup = Path.Combine(home, "baseline-build");
            Directory.CreateDirectory(setup);
            var baselineFiles = await pipeline.Build(baseline, settings.RepositoryRoot, setup, _ => { });
            foreach (var file in baselineFiles)
            {
                var destination = Files.Child(settings.WebfarmRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file.Source, destination);
            }
            var originalHashes = baselineFiles.ToDictionary(file => file.RelativePath, file => Files.Hash(file.Source), Files.Comparer);
            var sibling = Path.Combine(home, "external-core", "bin");
            var sdkSnapshot = Path.Combine(home, "new-sdk", "source");
            var sdkRun = Path.Combine(home, "new-sdk", "build");
            Directory.CreateDirectory(Path.Combine(sdkSnapshot, "Core"));
            Directory.CreateDirectory(sdkRun);
            File.WriteAllText(Path.Combine(sdkSnapshot, "MyMeyer.sln"), "");
            File.WriteAllText(Path.Combine(sdkSnapshot, @"Core\Core.csproj"), Project("Core"));
            File.WriteAllText(Path.Combine(sdkSnapshot, @"Core\App.config"), "<configuration />");
            File.WriteAllText(Path.Combine(sdkSnapshot, @"Core\Value.cs"),
                "public class CoreValue { public static int Value => 1; public static int NewApi => 2; }");
            var sdkPlan = new Plan
            {
                Target = new("SDK fixture", sdkSnapshot, ""), Commit = "", Fingerprint = "",
                Projects = [ProjectInfo.Read(sdkSnapshot, @"Core\Core.csproj")]
            };
            var newSdk = (await pipeline.Build(sdkPlan, sdkSnapshot, sdkRun, _ => { })).Single();
            Directory.CreateDirectory(sibling);
            File.Copy(newSdk.Source, Path.Combine(sibling, "Core.dll"));
            var relocated = Path.Combine(home, "relocated", "source");
            var relocatedRun = Path.Combine(home, "relocated", "build");
            Directory.CreateDirectory(Path.Combine(relocated, "Consumer"));
            Directory.CreateDirectory(Path.Combine(relocated, "bin"));
            Directory.CreateDirectory(relocatedRun);
            File.WriteAllText(Path.Combine(relocated, "MyMeyer.sln"), "");
            File.Copy(baselineFiles.Single(file => file.RelativePath == @"bin\Core.dll").Source,
                Path.Combine(relocated, @"bin\Core.dll"));
            var externalProject = Project("Consumer").Replace("<Reference Include=\"System\" />", """
                <Reference Include="System" />
                <Reference Include="Core">
                  <HintPath>..\..\external-core\bin\Core.dll</HintPath>
                </Reference>
                """);
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Consumer.csproj"), externalProject);
            File.WriteAllText(Path.Combine(relocated, @"Consumer\App.config"), "<configuration />");
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Value.cs"),
                "public class ConsumerValue { public static int Value => CoreValue.NewApi; }");
            var externalPlan = new Plan
            {
                Target = new("relocated fixture", relocated, ""), Commit = "", Fingerprint = "",
                Projects = [ProjectInfo.Read(relocated, @"Consumer\Consumer.csproj")]
            };
            var externalOutputs = await pipeline.Build(externalPlan, relocated, relocatedRun, _ => { });
            Check(Files.Hash(Path.Combine(relocatedRun, @"references\Core.dll")) == Files.Hash(newSdk.Source),
                "a relocated worktree uses the declared sibling DLL, not its stale bin cache");
            Check(externalOutputs.Select(file => file.RelativePath).SequenceEqual([@"bin\Consumer.dll"]) &&
                Files.Hash(Files.Child(settings.WebfarmRoot, @"bin\Core.dll")) == originalHashes[@"bin\Core.dll"],
                "external build inputs are never rebuilt or included in the deployment output");
            Check(File.ReadAllText(Path.Combine(relocated, @"Consumer\Consumer.csproj")) == externalProject &&
                File.ReadAllText(Path.Combine(relocatedRun, "build.log")).Contains(Path.Combine(sibling, "Core.dll")),
                "reference resolution preserves project files and records the selected DLL path");

            // MyMeyer's HintPaths can name a package version packages.config never restores; Visual Studio then uses bin.
            var binCore = Path.Combine(relocated, @"bin\Core.dll");
            var binVersion = System.Reflection.AssemblyName.GetAssemblyName(binCore).Version!;
            string StaleHint(Version version) => Project("Consumer").Replace("<Reference Include=\"System\" />", $"""
                <Reference Include="System" />
                <Reference Include="Core, Version={version}, Culture=neutral, processorArchitecture=MSIL">
                  <HintPath>..\packages\Core.10.0.2\lib\net471\Core.dll</HintPath>
                </Reference>
                """);
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Value.cs"), "public class ConsumerValue { public static int Value => CoreValue.Value; }");
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Consumer.csproj"), StaleHint(binVersion));
            var staleRun = Path.Combine(home, "relocated", "stale-hint");
            Directory.CreateDirectory(staleRun);
            Plan Relocated() => new()
                { Target = externalPlan.Target, Commit = "", Fingerprint = "", Projects = [ProjectInfo.Read(relocated, @"Consumer\Consumer.csproj")] };
            await pipeline.Build(Relocated(), relocated, staleRun, _ => { });
            Check(Files.Hash(Path.Combine(staleRun, @"references\Core.dll")) == Files.Hash(binCore),
                "a package HintPath missing after restore uses the bin DLL carrying the declared version");
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Consumer.csproj"),
                StaleHint(new Version(binVersion.Major + 1, 0, 0, 0)));
            var mismatchRun = Path.Combine(home, "relocated", "stale-mismatch");
            Directory.CreateDirectory(mismatchRun);
            await Reject(() => pipeline.Build(Relocated(), relocated, mismatchRun, _ => { }),"a bin DLL with a different version never replaces a missing package");
            Save(@"Core\Core.csproj", coreProject.Replace("<Reference Include=\"System\" />", """
                <Reference Include="System" />
                <Reference Include="UnrelatedMissingPackage">
                  <HintPath>..\packages\UnrelatedMissingPackage.10.0.2\lib\net471\UnrelatedMissingPackage.dll</HintPath>
                </Reference>
                """));
            Save(@"Core\Value.cs", "this unchanged dependency deliberately cannot compile;");
            await G("add", ".");
            await G("commit", "-qm", "baseline with existing dependency DLLs");
            await G("checkout", "-qb", "feature");
            Save(@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value + 1; }");
            Save(@"Page\View.ascx", "feature markup");
            await G("add", ".");
            await G("commit", "-qm", "page code and markup");
            await G("branch", "branch-only");
            var target = new Target("feature", settings.RepositoryRoot, "refs/heads/feature");
            Task<Dictionary<string, string>> Maps(string _) => Task.FromResult(
                new Dictionary<string, string>(Files.Comparer) { ["View.ascx"] = "modules" });
            var targets = await git.Targets();
            Check(targets.Any(x => x.Branch == "feature" && x.Worktree == settings.RepositoryRoot), "discover saved worktrees");
            Check(targets.Any(x => x.Branch == "branch-only" && x.Worktree is null), "discover branches without switching a checkout");
            await Reject(() => Task.Run(() => Files.Child(settings.RepositoryRoot, @"..\escape")), "reject parent path escape");
            await Reject(() => Task.Run(() => Files.Child(settings.RepositoryRoot, @"C:\escape")), "reject absolute mapping");
            var plan = await planner.Create(target, Maps);
            Check(plan.CanDeploy && plan.Projects.Select(x => x.Assembly).SequenceEqual(["Page"]),
                "changed Page code builds Page only, not its Core dependency or Consumer");
            Check(plan.Frontend.Single().RelativePath == @"modules\View.ascx", "changed markup has exactly its mapped destination");
            var noMap = await planner.Create(target, _ => Task.FromResult(new Dictionary<string, string>()));
            Check(!noMap.CanDeploy, "unmapped frontend blocks an incomplete activation");
            var branchPlan = await planner.Create(targets.Single(x => x.Branch == "branch-only"), Maps);
            Check(branchPlan.CanDeploy && branchPlan.Projects.Select(x => x.Assembly).SequenceEqual(["Page"]),
                "committed branch planning uses only its changed owner");
            Check(await git.Git(settings.RepositoryRoot, "branch", "--show-current") == "feature", "planning does not switch the checkout");
            var before = (await git.Inspect(target)).Fingerprint;
            Save(@"Page\site.css", "body { color: red; }");
            var dirty = await git.Inspect(target);
            Check(before != dirty.Fingerprint && dirty.Changes.Any(x => x.Path.EndsWith("site.css")),
                "nonignored untracked saves participate in change detection");
            File.Delete(Files.Child(settings.RepositoryRoot, @"Page\site.css"));

            var frontendDestination = Files.Child(settings.WebfarmRoot, @"modules\View.ascx");
            Directory.CreateDirectory(Path.GetDirectoryName(frontendDestination)!);
            File.WriteAllText(frontendDestination, "server baseline");
            var coreDestination = Files.Child(settings.WebfarmRoot, @"bin\Core.dll");
            var coreTime = File.GetLastWriteTimeUtc(coreDestination);
            var progress = new List<string>();
            var state = await pipeline.Activate(plan, progress.Add, watch: false);
            Check(state.Updated.Select(file => (file.RelativePath, file.Change)).SequenceEqual(
                [(@"bin\Page.dll", UpdatedFile.Built), (@"modules\View.ascx", UpdatedFile.Synced)]),
                "activation lists every webfarm file it updated and how");
            Check(state.Active && !state.WatchEnabled && state.RuntimeFiles.Select(x => x.RelativePath).SequenceEqual([@"bin\Page.dll"]),
                "activation publishes exactly the changed project's own DLL");
            var deployed = (await bridge.Call("Status")).GetProperty("Files").EnumerateArray()
                .Select(file => file.GetProperty("RelativePath").GetString()).Order().ToList();
            Check(deployed.SequenceEqual(new[] { @"bin\Page.dll", @"modules\View.ascx" }.Order()),
                "the complete journaled payload contains exactly one frontend file and one target DLL");
            Check(progress.Where(x => x.StartsWith("Building ")).SequenceEqual(["Building Page (1/1)"]),
                "an unchanged dependency with missing packages and invalid source is never built");
            var run = Path.GetDirectoryName(state.WorkspaceRoot)!;
            Check(!File.Exists(Path.Combine(run, "restore.log")) && !File.Exists(Path.Combine(run, "Core.binlog")),
                "unchanged reference projects are not restored or built");
            Check(File.ReadAllLines(Path.Combine(run, "manifests", "Page.artifacts")).Count(x => !string.IsNullOrWhiteSpace(x)) == 1 &&
                new FileInfo(Path.Combine(run, "Page.binlog")).Length > 0,
                "the existing webfarm copy target executes and captures only the project DLL");
            Check(File.ReadAllText(frontendDestination) == "feature markup", "initial activation automatically synchronizes changed frontend");
            Check(Files.Hash(coreDestination) == originalHashes[@"bin\Core.dll"] && File.GetLastWriteTimeUtc(coreDestination) == coreTime &&
                Files.Hash(Files.Child(settings.WebfarmRoot, @"bin\Consumer.dll")) == originalHashes[@"bin\Consumer.dll"],
                "dependency and consumer DLL contents and timestamps remain untouched");
            Check((await git.Git(settings.RepositoryRoot, "status", "--porcelain")).Length == 0 && !Directory.Exists(state.WorkspaceRoot),
                "builds leave the source checkout unchanged and remove their private snapshot");

            var pageDestination = Files.Child(settings.WebfarmRoot, @"bin\Page.dll");
            var pageHash = Files.Hash(pageDestination);
            var pageTime = File.GetLastWriteTimeUtc(pageDestination);
            Save(@"Page\View.ascx", "second saved markup");
            var markupUpdate = await planner.Create(target, Maps);
            Check(markupUpdate.BuildFingerprint == plan.BuildFingerprint, "frontend saves preserve the compiled fingerprint");
            progress.Clear();
            state = await pipeline.Activate(markupUpdate, progress.Add, watch: false, workerUpdate: true);
            Check(progress.Any(x => x.StartsWith("Frontend-only update")) && !progress.Any(x => x.StartsWith("Building ")),
                "frontend-only updates reuse the target-only cache without rebuilding");
            Check(state.Updated.Any(file => file.RelativePath == @"bin\Page.dll" && file.Change == UpdatedFile.Reused),
                "frontend-only updates list the reused DLL as copied, not built");
            Check(File.ReadAllText(frontendDestination) == "second saved markup" && File.GetLastWriteTimeUtc(pageDestination) == pageTime,
                "frontend saves copy without recopying the target DLL");
            Save(@"Page\Value.cs", "this changed project deliberately cannot compile;");
            var broken = await planner.Create(target, Maps);
            await Reject(() => pipeline.Activate(broken, _ => { }, watch: false), "a changed-project compiler failure blocks publication");
            Check((await bridge.Status())?.Token == state.Token && Files.Hash(pageDestination) == pageHash &&
                File.ReadAllText(frontendDestination) == "second saved markup", "failed builds preserve the last successful files");
            Save(@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value + 1; }");
            File.WriteAllText(frontendDestination, "foreign edit");
            await Reject(() => bridge.Call("Restore"), "foreign server edits block restoration");
            Check(File.ReadAllText(frontendDestination) == "foreign edit", "foreign content is not overwritten");
            File.WriteAllText(frontendDestination, "second saved markup");
            await bridge.Call("Restore");
            Check(File.ReadAllText(frontendDestination) == "server baseline" && Files.Hash(pageDestination) == originalHashes[@"bin\Page.dll"],
                "restore returns original markup and the original target DLL");

            Save(@"Page\View.ascx", "feature markup");
            await G("checkout", "-qb", "frontend-only", "master");
            Save(@"Page\View.ascx", "frontend only");
            var frontendTarget = new Target("frontend-only", settings.RepositoryRoot, "refs/heads/frontend-only");
            var frontendPlan = await planner.Create(frontendTarget, Maps);
            Check(frontendPlan.CanDeploy && frontendPlan.Projects.Count == 0, "markup-only selection never schedules compilation");
            var frontendState = await pipeline.Activate(frontendPlan, _ => { }, watch: false);
            Check(frontendState.RuntimeFiles.Count == 0 && File.ReadAllText(frontendDestination) == "frontend only" &&
                Files.Hash(pageDestination) == originalHashes[@"bin\Page.dll"], "markup-only activation builds and copies no DLLs");
            await bridge.Call("Restore");
            File.Delete(Files.Child(settings.RepositoryRoot, @"Page\View.ascx"));
            var deleted = await planner.Create(frontendTarget, Maps);
            Check(deleted.Frontend.Single().Delete, "frontend deletions are explicitly planned");
            await pipeline.Activate(deleted, _ => { }, watch: false);
            Check(!File.Exists(frontendDestination), "frontend deletion is synchronized");
            await bridge.Call("Restore");
            Check(File.ReadAllText(frontendDestination) == "server baseline", "deleted frontend can be restored");
            Save("Unknown.cs", "public class Unknown {}");
            Check(!(await planner.Create(frontendTarget, Maps)).CanDeploy, "unowned compiled source is not silently ignored");
            File.Delete(Files.Child(settings.RepositoryRoot, "Unknown.cs"));

            Save(@"Page\View.ascx", "original markup");
            await G("checkout", "-qb", "worker-code", "master");
            Save(@"Core\Core.csproj", coreProject);
            Save(@"Core\Value.cs", "public class CoreValue { public static int Value => 7; }");
            Directory.Move(Path.Combine(settings.RepositoryRoot, "packages"), Path.Combine(home, "offline-cache"));
            var workerTarget = new Target("worker-code", settings.RepositoryRoot, "refs/heads/worker-code");
            var workerPlan = await planner.Create(workerTarget);
            Check(workerPlan.Projects.Select(x => x.Assembly).SequenceEqual(["Core"]), "a changed Core does not schedule Page or Consumer");
            var watched = await pipeline.Activate(workerPlan, _ => { }, watch: true);
            Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(watched.WorkspaceRoot)!, "restore.log")).Contains(packageId) &&
                !Directory.Exists(Path.Combine(settings.RepositoryRoot, "packages")),
                "a changed project's missing packages restore inside its snapshot only");
            var workerState = await bridge.Status() ?? throw new InvalidDataException("Worker state disappeared.");
            Check(workerState.WatcherPid > 0 && workerState.WatchEnabled, "built-in saved-file sync reports readiness");
            using var worker = System.Diagnostics.Process.GetProcessById(workerState.WatcherPid);
            Save(@"Core\Value.cs", "public class CoreValue { public static int Value => 8; }");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            DeploymentState? updated = null;
            while (DateTime.UtcNow < deadline)
            {
                updated = await bridge.Status();
                if (updated?.Active == true && updated.Token != watched.Token) break;
                await Task.Delay(500);
            }
            Check(updated?.Active == true && updated.Token != watched.Token &&
                updated.RuntimeFiles.Select(x => x.RelativePath).SequenceEqual([@"bin\Core.dll"]),
                "saving code automatically rebuilds and publishes only its owning project");
            Check(Files.Hash(pageDestination) == originalHashes[@"bin\Page.dll"] &&
                Files.Hash(Files.Child(settings.WebfarmRoot, @"bin\Consumer.dll")) == originalHashes[@"bin\Consumer.dll"],
                "automatic code saves leave unchanged consumers untouched");
            await bridge.Call("Restore");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(worker.HasExited, "restore cooperatively stops the known file-sync worker");

            if (Watcher.ExecutableStamp() is not null)
            {
                // `cultivate update` replaces Cultivate.exe under a running worker, which must hand over to the new file.
                var copy = Path.Combine(home, "installed copy");
                Directory.CreateDirectory(copy);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory).Where(path => !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                var oneTime = await pipeline.Activate(await planner.Create(workerTarget), _ => { }, watch: false);
                var workerFile = Path.Combine(home, "handoff-worker.json");
                Files.WriteJson(workerFile, settings);
                using var first = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(copy, "Cultivate.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "watch", "--token", oneTime.Token, "--settings", workerFile }
                }) ?? throw new InvalidOperationException("Cannot start the copied worker.");
                async Task<DeploymentState?> WaitFor(Func<DeploymentState, bool> condition)
                {
                    for (var until = DateTime.UtcNow.AddSeconds(45); DateTime.UtcNow < until; await Task.Delay(250))
                        if (await bridge.Status() is { } current && condition(current)) return current;
                    return null;
                }
                Check(await WaitFor(state => state.WatcherPid == first.Id) is not null, "a worker started from an installed copy reports readiness");
                var package = Path.Combine(home, "handoff-package");
                Directory.CreateDirectory(package);
                File.Copy(Path.Combine(copy, "Cultivate.exe"), Path.Combine(package, "Cultivate.exe"));
                Updater.Apply(package, copy);
                var handedOver = await WaitFor(state => state.Active && state.Token == oneTime.Token && state.WatcherPid > 0 && state.WatcherPid != first.Id);
                await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(handedOver is not null && first.HasExited, "an in-place update hands the running worker over to the new executable");
                using var successor = System.Diagnostics.Process.GetProcessById(handedOver!.WatcherPid);
                Check(Files.Comparer.Equals(successor.MainModule?.FileName, Path.Combine(copy, "Cultivate.exe")), "the successor worker runs from the updated installation");
                await bridge.Call("Restore");
                await successor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(successor.HasExited, "restore stops the handed-over worker");
            }

            var a = new ProjectInfo { RelativePath = @"A\A.csproj", Assembly = "A", ProjectReferences = [@"B\B.csproj"], Inputs = [@"A\A.cs"] };
            var b = new ProjectInfo { RelativePath = @"B\B.csproj", Assembly = "B", ProjectReferences = [@"A\A.csproj"] };
            Check(Planner.BuildOrder([a, b], [new(@"A\A.cs", false)], []).Select(x => x.Assembly).SequenceEqual(["A"]),
                "unmodified dependency cycles do not expand or block an owner-only plan");
            await Reject(() => Task.Run(() => Planner.BuildOrder([a, b], [new(@"A\A.cs", false), new(@"B\B.csproj", false)], [])),
                "cycles between changed projects are reported");
            a.ProjectReferences.Clear();
            var c = new ProjectInfo { RelativePath = @"C\C.csproj", Assembly = "C", AssemblyReferences = ["A"] };
            Check(Planner.BuildOrder([a, c], [new(@"A\A.cs", false), new(@"C\C.csproj", false)], [])
                .Select(x => x.Assembly).SequenceEqual(["A", "C"]), "references order only independently changed projects");
            Console.WriteLine($"\nCultivate: {assertions} assertions passed. Fixtures only; no real webfarm access.");
            return 0;
        }
        finally
        {
            if (cleanupBridge is not null && (await cleanupBridge.Status())?.Active == true)
                await cleanupBridge.Call("Restore");
            foreach (var file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task CheckVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return;
        // Check product metadata independently of file discovery to catch non-IDE products masking Visual Studio.
        var products = await Commands.Run(vswhere, ["-prerelease", "-utf8", "-property", "productPath"]);
        var installed = products.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Trim())
            .Where(path => Files.Comparer.Equals(Path.GetFileName(path), "devenv.exe") && File.Exists(path)).ToList();
        var found = await Settings.FindVisualStudio();
        Check(installed.Count == 0 ? found is null : found is not null && installed.Contains(found, Files.Comparer),
            "Visual Studio discovery matches installed IDEs even with newer non-IDE products, or returns none on Build Tools-only machines");
    }

    private static async Task CheckSolutions(string home)
    {
        var root = Path.Combine(home, "solutions with spaces");
        Directory.CreateDirectory(root);
        var git = new GitRepository(new Settings { RepositoryRoot = root });
        async Task G(params string[] args) => _ = await git.Git(root, args);
        var solution = Path.Combine(root, "MyMeyer.sln");
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        File.WriteAllText(solution, "committed solution");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.cs\n");
        foreach (var name in new[] { "Changed file.cs", "Rename source.cs", "Deleted.cs", "Unchanged.cs" })
            File.WriteAllText(Path.Combine(root, name), "baseline " + name);
        await G("add", ".");
        await G("commit", "-qm", "solution fixture");
        await G("checkout", "-qb", "feature/local");
        File.WriteAllText(Path.Combine(root, "Changed file.cs"), "committed branch change");
        File.WriteAllText(Path.Combine(root, "Added file.cs"), "committed new file");
        await G("mv", "Rename source.cs", "Renamed file.cs");
        await G("rm", "Deleted.cs");
        await G("add", ".");
        await G("commit", "-qm", "changed files fixture");
        await G("checkout", "master");
        File.WriteAllText(Path.Combine(root, "Unchanged.cs"), "later baseline change");
        await G("add", ".");
        await G("commit", "-qm", "advance the baseline independently");
        await G("checkout", "-qb", "no-solution");
        await G("rm", "MyMeyer.sln");
        await G("commit", "-qm", "missing solution fixture");
        await G("checkout", "master");
        File.WriteAllText(solution, "unsaved deployment edits");
        var targets = await git.Targets();
        var worktree = targets.Single(x => x.Worktree is not null);
        var branch = targets.Single(x => x.Branch == "feature/local");
        try
        {
            Check(await git.Solution(worktree) == solution, "opening a worktree resolves its own solution, including paths with spaces");
            Check(await git.Solution(targets.Single(x => x.Branch == "master" && x.Worktree is null)) == solution,
                "opening a checked-out local branch reuses its worktree");
            Check((await git.SolutionChanges(solution)).Count == 0,
                "an unchanged branch opens only the solution, not a duplicate solution document");
            var opened = await git.Solution(branch);
            Check(opened != solution && File.ReadAllText(opened) == "committed solution",
                "opening a local branch without a checkout creates its own solution worktree");
            var openedRoot = Path.GetDirectoryName(opened)!;
            Check(await git.Git(openedRoot, "symbolic-ref", "HEAD") == branch.Ref,
                "the new worktree checks out the selected local branch, not a detached snapshot");
            var changed = await git.SolutionChanges(opened);
            Check(changed.Select(Path.GetFileName).SequenceEqual(["Added file.cs", "Changed file.cs", "Renamed file.cs"]),
                "solution documents include committed additions, edits, and rename destinations, excluding deleted and unchanged files");
            Check(changed.All(path => path.StartsWith(openedRoot + "\\", StringComparison.OrdinalIgnoreCase)),
                "solution documents come from the selected branch worktree, not the main checkout");
            Check(!changed.Any(path => Path.GetFileName(path) == "Unchanged.cs"),
                "solution changes use the merge-base, excluding later changes made only on the baseline branch");
            File.WriteAllText(Path.Combine(openedRoot, "Unchanged.cs"), "saved worktree edit");
            File.WriteAllText(Path.Combine(openedRoot, "New local file.cs"), "untracked work");
            File.WriteAllText(Path.Combine(openedRoot, "Staged file.cs"), "staged work");
            await git.Git(openedRoot, "add", "Staged file.cs");
            File.WriteAllText(Path.Combine(openedRoot, "ignored.cs"), "ignored work");
            changed = await git.SolutionChanges(opened);
            Check(changed.Select(Path.GetFileName).SequenceEqual(
                ["Added file.cs", "Changed file.cs", "New local file.cs", "Renamed file.cs", "Staged file.cs", "Unchanged.cs"]),
                "solution documents include staged, unstaged, and untracked work while excluding ignored files");
            File.Delete(Path.Combine(openedRoot, "Added file.cs"));
            Check(!(await git.SolutionChanges(opened)).Any(path => Path.GetFileName(path) == "Added file.cs"),
                "solution documents skip files deleted in the selected worktree");
            File.WriteAllText(opened, "branch work in progress");
            Check(await git.Solution(branch) == opened && File.ReadAllText(opened) == "branch work in progress",
                "reopening a branch reuses its checkout and preserves edits");
            Check(await git.Git(root, "branch", "--show-current") == "master" && File.ReadAllText(solution) == "unsaved deployment edits",
                "opening another branch preserves the current branch and dirty files");
            Check((await git.Targets()).Any(x => x.Worktree == openedRoot && x.Ref == branch.Ref),
                "the opened branch remains available as a normal worktree");
            var before = await git.Git(root, "worktree", "list", "--porcelain");
            await Reject(() => git.Solution(targets.Single(x => x.Branch == "no-solution")),
                "a branch without a solution is rejected before creating a worktree");
            await Reject(() => git.Solution(new("origin/master", null, "refs/remotes/origin/master", true)),
                "opening a solution rejects remote branches");
            File.Delete(solution);
            await Reject(() => git.Solution(worktree), "a missing worktree solution reports an error");
            Check(await git.Git(root, "worktree", "list", "--porcelain") == before,
                "failed solution selections leave the worktree list unchanged");
        }
        finally
        {
            foreach (var target in (await git.Targets()).Where(x => x.Worktree is not null && x.Worktree != root))
                await G("worktree", "remove", "--force", target.Worktree!);
        }
    }

    private static void CheckUpdater(string home)
    {
        Check(Updater.Parse("1.2.3+abc1234") == new Version(1, 2, 3) && Updater.Parse("v1.10") == new Version(1, 10, 0) &&
              Updater.Parse("1.10.0") > Updater.Parse("1.9.9") && Updater.Parse("not a version") is null && Updater.Parse("1.2.3.4") is null,
            "release versions parse and compare numerically");
        var install = Path.Combine(home, "update-install");
        var package = Path.Combine(home, "update-package");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(install, "Bridge.ps1"), "old bridge");
        File.WriteAllText(Path.Combine(install, "installation.json"), "{}");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(install, "Cultivate.exe"));
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(install, "Cultivate.exe"), "-n 120 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new InvalidOperationException("Cannot start the fixture executable.");
        try
        {
            File.WriteAllText(Path.Combine(package, "Bridge.ps1"), "new bridge");
            File.WriteAllText(Path.Combine(package, "Capture.targets"), "new capture");
            File.WriteAllText(Path.Combine(package, "install.ps1"), "installer");
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(package, "Cultivate.exe"));
            Updater.Apply(package, install);
            Check(File.ReadAllText(Path.Combine(install, "Bridge.ps1")) == "new bridge" && File.Exists(Path.Combine(install, "Capture.targets")) &&
                  !File.Exists(Path.Combine(install, "install.ps1")) && File.ReadAllText(Path.Combine(install, "installation.json")) == "{}",
                "updates replace and add program files, keep installation.json, and skip the installer");
            Check(!running.HasExited && Files.Hash(Path.Combine(install, "Cultivate.exe")) == Files.Hash(Path.Combine(package, "Cultivate.exe")),
                "a running Cultivate.exe is replaced in place without stopping it");
            Check(Directory.GetFiles(install, "*.old").Length == 1 && Directory.GetFiles(install, "*.new").Length == 0,
                "only the still-running executable's replaced copy is left behind");

            File.WriteAllText(Path.Combine(package, "Bridge.ps1"), "newer bridge");
            File.WriteAllText(Path.Combine(package, "Zzz.targets"), "replacement");
            File.WriteAllText(Path.Combine(install, "Zzz.targets"), "in use");
            var rejected = false;
            using (new FileStream(Path.Combine(install, "Zzz.targets"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { Updater.Apply(package, install); }
                catch (IOException) { rejected = true; }
            }
            Check(rejected && File.ReadAllText(Path.Combine(install, "Bridge.ps1")) == "new bridge" &&
                  File.ReadAllText(Path.Combine(install, "Zzz.targets")) == "in use" && Directory.GetFiles(install, "*.new").Length == 0,
                "a failed update rolls every replaced file back");
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
        for (var until = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < until && Directory.GetFiles(install, "*.old").Length != 0; Thread.Sleep(200))
            Updater.CleanupStaleFiles(install);
        Check(Directory.GetFiles(install, "*.old").Length == 0, "replaced executables are cleaned up once they stop running");

        if (Watcher.ExecutableStamp() is null) return;
        // `cultivate update` hands the swap to the new version's executable through this command.
        var target = Path.Combine(home, "update-apply-install");
        var source = Path.Combine(home, "update-apply-package");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(target, "installation.json"), """{ "Hooks": [] }""");
        File.WriteAllText(Path.Combine(target, "Bridge.ps1"), "old bridge");
        File.WriteAllText(Path.Combine(source, "Bridge.ps1"), "new bridge");
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        File.Copy(where, Path.Combine(source, "Cultivate.exe"));
        using var apply = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "update-apply", source, target }
        }) ?? throw new InvalidOperationException("Cannot start update-apply.");
        var errors = apply.StandardError.ReadToEnd();
        apply.StandardOutput.ReadToEnd();
        apply.WaitForExit();
        var recorded = File.ReadAllText(Path.Combine(target, "installation.json"));
        Check(apply.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Bridge.ps1")) == "new bridge" && recorded.Contains("UpdatedAt") &&
              recorded.Contains(System.Diagnostics.FileVersionInfo.GetVersionInfo(where).ProductVersion ?? "?"),
            "update-apply swaps the files and records the new version" + (apply.ExitCode == 0 ? "" : ": " + errors.Trim()));
    }

    private static void CheckTerminal()
    {
        var targets = Enumerable.Range(0, 37).Select(index => new Target(
            $"ticket-{index:000}" + (index == 10 ? "-" + new string('x', 140) : ""),
            $@"C:\dev\MyMeyer\agent-{index:000}", $"refs/heads/ticket-{index:000}")).ToList();
        targets.Add(new("ticket-017", null, "refs/heads/ticket-017"));
        targets.Add(new("origin/ticket-017", null, "refs/remotes/origin/ticket-017", true));
        var picker = new VersionPicker(targets);
        void Key(ConsoleKey key, ConsoleModifiers modifiers = 0) =>
            picker.HandleKey(new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)), 7);
        void Type(string text)
        {
            foreach (var character in text) picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        }
        Check(picker.Kind == VersionKind.Worktrees && picker.Matches.Count == 37, "picker starts with saved worktrees");
        Key(ConsoleKey.End);
        Check(picker.Index == 36, "picker End reaches the final version");
        Key(ConsoleKey.PageUp);
        Check(picker.Index == 29, "picker pages backward by visible rows");
        Key(ConsoleKey.PageDown);
        Check(picker.Index == 36, "picker page navigation stays within results");
        Key(ConsoleKey.DownArrow);
        Check(picker.Index == 0, "picker down wraps at the end");
        Key(ConsoleKey.UpArrow);
        Check(picker.Index == 36, "picker up wraps at the beginning");
        Type("AGENT-017");
        Check(picker.Matches.Count == 1 && picker.Selected?.Worktree?.EndsWith("agent-017") == true,
            "picker searches hidden full paths case-insensitively");
        Key(ConsoleKey.Backspace);
        Check(picker.Query == "AGENT-01" && picker.Matches.Count == 10, "picker backspace updates matching results");
        Key(ConsoleKey.U, ConsoleModifiers.Control);
        Type("ticket-017");
        Key(ConsoleKey.Tab);
        Check(picker.Kind == VersionKind.Local && picker.Query == "ticket-017" && picker.Selected?.Worktree is null &&
            picker.Selected?.Remote == false, "picker preserves search across source tabs");
        Key(ConsoleKey.RightArrow);
        Check(picker.Kind == VersionKind.Remote && picker.Selected?.Remote == true, "picker separates cached remote branches");
        Key(ConsoleKey.Tab);
        Check(picker.Kind == VersionKind.Worktrees && picker.Selected?.Worktree is not null, "picker source tabs wrap");
        Key(ConsoleKey.Tab, ConsoleModifiers.Shift);
        Check(picker.Kind == VersionKind.Remote, "picker Shift+Tab switches sources backward");
        Key(ConsoleKey.U, ConsoleModifiers.Control);
        Type("no-such-version");
        Key(ConsoleKey.DownArrow);
        Key(ConsoleKey.PageDown);
        Check(picker.Matches.Count == 0 && picker.Selected is null && picker.Index == 0, "empty results cannot select a version");
        picker = new VersionPicker(targets, new DeploymentState { Active = true, SourceRoot = targets[17].Worktree!, SourceRef = targets[17].Ref });
        Check(picker.Selected == targets[17], "picker initially highlights the active worktree");
        static string Capture(Spectre.Console.Rendering.IRenderable renderable, int width, int height)
        {
            using var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No, Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(output),
                // CI enrichers (e.g. GITHUB_ACTIONS) would otherwise re-enable ANSI and change measured widths.
                Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
            });
            console.Profile.Width = width;
            console.Profile.Height = height;
            console.Write(renderable);
            return output.ToString().Replace("\r", "").TrimEnd('\n');
        }
        static string Render(VersionPicker model, int width, int height) => Capture(model.Render(width, height), width, height);
        Check(Render(picker, 100, 30).Contains("LIVE  agent-017"), "picker marks the actual deployed source");
        picker = new VersionPicker(targets, openSolution: true);
        Key(ConsoleKey.Tab);
        Check(picker.Kind == VersionKind.Local && picker.Matches.All(x => !x.Remote),
            "the solution picker offers local branches");
        Type("ticket-017");
        Key(ConsoleKey.Tab);
        Check(picker.Kind == VersionKind.Worktrees && picker.Selected == targets[17] && picker.Query == "ticket-017",
            "the solution picker skips remote branches while retaining its search");
        Key(ConsoleKey.Tab, ConsoleModifiers.Shift);
        Check(picker.Kind == VersionKind.Local, "the solution picker wraps backward between local sources");
        var solutionScreen = Render(picker, 100, 30);
        Check(solutionScreen.Contains("OPEN SOLUTION") && solutionScreen.Contains("Enter opens the solution") &&
            !solutionScreen.Contains("REMOTE") && !solutionScreen.Contains("preview"),
            "the solution picker shows opening instructions instead of deployment instructions");
        foreach (var width in new[] { 60, 80, 120 })
            foreach (var height in new[] { 24, 35 })
            {
                var openScreen = Render(new VersionPicker(targets, openSolution: true), width, height).Split('\n');
                Check(openScreen.Length <= height && openScreen.All(line => line.Length <= width) && openScreen.Any(line => line.Contains("Esc")),
                    $"the solution picker keeps navigation visible at {width}x{height}");
                picker = new VersionPicker(targets);
                var screen = Render(picker, width, height);
                var lines = screen.Split('\n');
                Check(lines.All(line => line.Length <= width), $"picker stays inside {width} columns");
                Check(lines.Length <= height, $"picker fits {width}x{height}");
                Check(screen.Contains("Enter") && screen.Contains("Esc") && screen.Contains("SELECTED SOURCE"),
                    $"picker keeps navigation visible at {width}x{height}");
                Check(lines.Count(line => line.Contains("agent-000")) == 2, $"picker rows do not wrap at {width}x{height}");
            }
        picker = new VersionPicker([new("literal[review]", @"C:\dev\folder[green]", "refs/heads/example")]);
        Check(Render(picker, 100, 30).Contains("literal[review]"), "picker treats markup characters literally");
        picker = new VersionPicker([]);
        Check(Render(picker, 80, 24).Contains("No matches") && picker.Selected is null, "picker renders an empty source safely");

        picker = new VersionPicker(targets);
        Check(!Render(picker, 100, 30).Contains("Saved files + uncommitted changes. Disk contents only."),
            "selected worktree details omit the redundant disk-contents message");
        picker.HandleKey(new('\t', ConsoleKey.Tab, false, false, false), 7);
        Check(!Render(picker, 100, 30).Contains("Committed files only; uncommitted edits are excluded."),
            "selected branch details omit the redundant committed-files message");

        Check(Banner.Sprite.Length % 2 == 0 && Banner.Sprite.All(row => row.Length == Banner.Sprite[0].Length) &&
            Banner.Wordmark.All(row => row.Length == Banner.Wordmark[0].Length), "banner art rows are rectangular");
        var bannerScreen = Capture(new Banner("DEV LAUNCHPAD", "Your code.", 20), 120, 30);
        var bannerLines = bannerScreen.Split('\n');
        Check(bannerLines.Length == Banner.Height && bannerLines.All(line => line.Length == 120) &&
            bannerLines.Any(line => line.Contains(Banner.Wordmark[0].Trim())) && bannerScreen.Contains("DEV LAUNCHPAD") &&
            bannerScreen.Contains('▀') && !bannerScreen.Contains("LOCAL CODE"), "the banner draws the seedling and wordmark at 120 columns");
        bannerScreen = Capture(new Banner("DEV LAUNCHPAD", "Your code.", 20), 84, 30);
        Check(bannerScreen.Contains(Banner.Wordmark[0].Trim()) && !bannerScreen.Contains('▀') &&
            bannerScreen.Split('\n').All(line => line.Length == 84), "a narrower banner keeps the wordmark and drops the seedling");
        Check(Capture(new Banner("DEV LAUNCHPAD", "Your code.", 20), 60, 30).Contains("C U L T I V A T E"),
            "a banner too wide for the terminal falls back to the compact header");
        Check(Capture(new Banner("DEV LAUNCHPAD", "Your code.", 20), 120, 24).Contains("C U L T I V A T E"),
            "a banner that would push the screen below it off the terminal falls back to the compact header");
        Check(Capture(new Banner("[setup]", "folder[green]", 0), 120, 30).Contains("[setup]  ·  folder[green]"),
            "banner taglines treat markup characters literally");
        Check(TerminalStyle.Lines(TerminalStyle.Card(new Text("one\ntwo"), "CARD")) == 4, "card line counts include their borders");

        var menu = new ActionPicker(Terminal.HomeActions);
        var menuScreen = Capture(menu.Render("Actions", 30), 100, 30);
        Check(menuScreen.Contains(menu.Selected!.Description) && !menuScreen.Contains("Type to filter actions"),
            "action menus show the highlighted explanation instead of generic instructions");
        menu.HandleKey(new('\0', ConsoleKey.DownArrow, false, false, false), 7);
        menuScreen = Capture(menu.Render("Actions", 30), 100, 30);
        Check(menuScreen.Contains("Reload the active version") && !menuScreen.Contains("Browse versions and preview"),
            "moving the action highlight replaces the footer explanation");
        foreach (var character in "RESTORE")
            menu.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        Check(menu.Matches.Count == 1 && menu.Selected?.Label == "Restore original webfarm files",
            "action filtering remains case-insensitive");
        Check(Capture(menu.Render("Actions", 24), 60, 24).Contains("Stop automatic updates"),
            "filtered actions retain their own footer explanation");
        menu.HandleKey(new('\0', ConsoleKey.U, false, false, true), 7);
        menu.HandleKey(new('\0', ConsoleKey.UpArrow, false, false, false), 7);
        Check(menu.Selected?.Label == "Exit", "action navigation wraps without losing the selected explanation");
        foreach (var character in "no-such-action")
            menu.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        Check(menu.Selected is null && Capture(menu.Render("Actions", 24), 60, 24).Contains("No matching actions"),
            "empty action searches cannot select a hidden action");
        menu.HandleKey(new('\0', ConsoleKey.Escape, false, false, false), 7);
        Check(menu.Query.Length == 0 && menu.Matches.Count == Terminal.HomeActions.Count,
            "Escape clears an action search without closing the app");
        Check(Terminal.HomeActions.Concat(Terminal.PreviewActions(true)).All(action => !string.IsNullOrWhiteSpace(action.Description)),
            "every home and preview action has contextual help");
        Check(Terminal.HomeActions.Any(action => action.Label == "Open solution"), "the homepage offers opening a solution");
        Check(Terminal.PreviewActions(false).Select(action => action.Label).SequenceEqual(["Show every changed file", "Back"]),
            "blocked previews retain inspection and back actions but cannot activate");

        static IAnsiConsole LiveConsole(StringWriter output)
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor,
                Out = new AnsiConsoleOutput(output), Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
            });
            console.Profile.Width = 100;
            console.Profile.Height = 30;
            return console;
        }
        using (var output = new StringWriter())
        {
            picker = new VersionPicker([targets[0]]);
            var selected = picker.Show(LiveConsole(output), () =>
            {
                Check(output.ToString().Contains("VERSION LIBRARY") && output.ToString().Contains("ticket-000"),
                    "the complete version picker renders before waiting for its first key");
                return new('\r', ConsoleKey.Enter, false, false, false);
            });
            Check(selected == targets[0], "the initial version can be selected without an arrow-key redraw");
        }
        using (var output = new StringWriter())
        {
            menu = new ActionPicker(Terminal.HomeActions);
            var selected = menu.Show("Actions", LiveConsole(output), () =>
            {
                Check(output.ToString().Contains("Browse versions and preview"),
                    "action menus render the initial explanation before waiting for input");
                Check(output.ToString().Contains("38;2;179;179;179"),
                    "contextual action help renders in light grey");
                return new('\r', ConsoleKey.Enter, false, false, false);
            });
            Check(selected == Terminal.HomeActions[0], "the initial action can be selected without a redraw");
        }

        var project = new ProjectInfo
        {
            RelativePath = @"OrderingSystem\OrderingSystem.csproj", Assembly = "OrderingSystem",
            Inputs = [@"OrderingSystem\Order\ucOrder.ascx.cs"]
        };
        var preview = new Plan
        {
            Target = targets[0], Commit = "", Fingerprint = "",
            Projects = [project],
            Frontend = [new(@"OrderingSystem\Order\ucOrder.ascx", @"DesktopModules\Order\ucOrder.ascx")],
            Changes =
            [
                new(@"OrderingSystem\Order\ucOrder.ascx", false), new(@"OrderingSystem\Order\ucOrder.ascx.cs", false),
                new(@"bin\OrderingSystem.dll", false), new(@"bin\Unrelated.dll", false),
                new("README.md", false), new("schema.sql", false), new("web.config", false)
            ]
        };
        var actionable = Terminal.ActionableChanges(preview);
        Check(actionable.Select(item => item.Change.Path).SequenceEqual(
            [@"OrderingSystem\Order\ucOrder.ascx", @"OrderingSystem\Order\ucOrder.ascx.cs"]),
            "default changed-file rows contain only the mapped frontend and changed backend");
        Check(actionable.Select(item => item.Action).SequenceEqual(
            [@"Sync DesktopModules\Order\ucOrder.ascx", "Build OrderingSystem.dll"]),
            "default rows show the exact frontend destination and owning DLL build");
        Check(preview.Changes.Count == 7, "filtered previews preserve all changes for Show every changed file");
        var multiFile = new ProjectInfo
        {
            RelativePath = project.RelativePath, Assembly = project.Assembly,
            Inputs = [@"OrderingSystem\Order\ucOrder.ascx.cs", @"OrderingSystem\Order\FreightProgramBreakdown.cs"]
        };
        Check(Planner.BuildOrder([multiFile], [new(@"OrderingSystem\Order\ucOrder.ascx.cs", false),
                new(@"OrderingSystem\Order\FreightProgramBreakdown.cs", false), new(project.RelativePath, false)], []).Count == 1,
            "a project with several changed files is scheduled to build once");
        var updatedScreen = Capture(Terminal.UpdatedFiles(new DeploymentState
        {
            Updated = [new(@"bin\OrderingSystem.dll", UpdatedFile.Built), new(@"DesktopModules\Order\ucOrder.ascx", UpdatedFile.Synced),
                new(@"DesktopModules\Old\Old.ascx", UpdatedFile.Restored)]
        }, @"\\webfarm-dev02\C$\inetpub\MyMeyer"), 120, 30);
        Check(updatedScreen.Contains(@"bin\OrderingSystem.dll") && updatedScreen.Contains(UpdatedFile.Built) &&
            updatedScreen.Contains(@"DesktopModules\Old\Old.ascx") && updatedScreen.Contains(UpdatedFile.Restored),
            "the deployment summary lists every updated webfarm file");
        preview.Changes.Add(new(@"OrderingSystem\Order\Removed.cs", true));
        preview.Changes.Add(new(project.RelativePath, false));
        Check(Terminal.ActionableChanges(preview).Any(item => item.Change.Path.EndsWith("Removed.cs") && item.Action == "Build OrderingSystem.dll"),
            "deleted backend files keep their owning-project action when project entries were removed");
        preview.Changes.Add(new(@"OrderingSystem\obsolete.sql", true));
        preview.Changes.Add(new(@"OrderingSystem\bin\Old.dll", true));
        Check(!Terminal.ActionableChanges(preview).Any(item => item.Change.Path.EndsWith("obsolete.sql") || item.Change.Path.EndsWith("Old.dll")),
            "a changed project does not give deleted SQL or generated DLLs a deployment action");
        preview.Frontend[0] = new(@"OrderingSystem\Order\ucOrder.ascx", @"DesktopModules\Order\ucOrder.ascx", true);
        Check(Terminal.ActionableChanges(preview)[0].Action == @"Delete DesktopModules\Order\ucOrder.ascx",
            "frontend deletion actions remain visible");
        preview.Projects.Clear();
        preview.Frontend.Clear();
        Check(Terminal.ActionableChanges(preview).Count == 0, "a preview with no deployment actions has no changed-file rows");
    }

    private static string Project(string name, string? dependency = null, string? restorePackage = null)
    {
        var reference = dependency is null ? "" : $$"""
            <ProjectReference Include="..\{{dependency}}\{{dependency}}.csproj">
              <Project>{{Guid.NewGuid().ToString("B")}}</Project><Name>{{dependency}}</Name>
            </ProjectReference>
            """;
        var restore = restorePackage is null ? "" : $$"""
            <Import Project="..\packages\{{restorePackage}}\build\Cultivate.RestoreFixture.props"
                    Condition="Exists('..\packages\{{restorePackage}}\build\Cultivate.RestoreFixture.props')" />
            <Target Name="RequireRestoreFixture" BeforeTargets="Build">
              <Error Condition="'$(CultivateRestoreFixtureImported)' != 'true'" Text="The offline package was not restored to the snapshot." />
            </Target>
            """;
        return $$"""
            <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              {{restore}}
              <PropertyGroup>
                <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
                <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
                <ProjectGuid>{{Guid.NewGuid().ToString("B")}}</ProjectGuid>
                <OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                <OutputPath>..\bin\</OutputPath>
                <AssemblyName>{{name}}</AssemblyName>
              </PropertyGroup>
              <ItemGroup><Compile Include="Value.cs" /><None Include="App.config" /><Reference Include="System" />{{reference}}</ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <Target Name="CopyBuildOutputToWebfarm" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug'">
                <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(WebfarmPath)bin" />
              </Target>
            </Project>
            """;
    }
}
