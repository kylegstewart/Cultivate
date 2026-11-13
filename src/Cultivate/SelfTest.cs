using System.IO.Compression;
using System.Runtime.InteropServices;
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
            await CheckDiagnostics(home);
            await CheckVisualStudio();
            CheckSolutionFiles(home);
            CheckSolutionAutomation(home);
            await CheckSolutions(home);
            await CheckBaseline(home);
            CheckUpdater(home);
            // Fixtures need only MSBuild; they never read or write the user's saved settings.
            var settings = await Settings.Read();
            if (!File.Exists(settings.MSBuild))
                throw new FileNotFoundException("The self-test needs Visual Studio MSBuild and the .NET Framework 4.7.1 targeting pack.", settings.MSBuild);
            await CheckExclusions(home, settings.MSBuild);
            await CheckPackageDependencies(home, settings.MSBuild);
            await CheckBuildHooks(home, settings.MSBuild);
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
            File.WriteAllText(Path.Combine(relocated, @"Consumer\Value.cs"), "public class ConsumerValue { public static int Value => 1; }");
            await pipeline.Build(Relocated(), relocated, mismatchRun, _ => { });
            Check(!File.Exists(Path.Combine(mismatchRun, @"references\Core.dll")) &&
                File.ReadAllText(Path.Combine(mismatchRun, "build.log")).Contains("Unresolved reference Core.dll for Consumer"),
                "a missing package the code does not use builds like Visual Studio, and a bin DLL with another version is never substituted");
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
            var concurrentStatus = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                var pulse = await bridge.Call("Heartbeat", new { Token = state.Token, ProcessId = 0, Error = "" });
                return pulse.GetProperty("Current").GetBoolean() && (await bridge.Status())?.Token == state.Token;
            }));
            Check(concurrentStatus.All(current => current), "concurrent journal reads and heartbeats preserve consistent status");
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
            FailureReport? buildFailure = null;
            await Reject(async () =>
            {
                try { await pipeline.Activate(broken, _ => { }, watch: false); }
                catch (InvalidOperationException exception) { buildFailure = Diagnostics.Describe(exception); throw; }
            }, "a changed-project compiler failure blocks publication");
            Check(buildFailure?.Label == "BUILD FAILED" && buildFailure.Context.Project == @"Page\Page.csproj" &&
                buildFailure.Context.Worktree == target.Worktree && buildFailure.Context.Branch == target.Branch &&
                buildFailure.Context.ExitCode is not null and not 0 && buildFailure.Errors.Any(error => error.Contains("error CS")) &&
                buildFailure.Logs.Any(path => path.EndsWith(".binlog")) && buildFailure.Logs.Any(path => path.EndsWith("build.log")),
                "a real compiler failure identifies its project, selected source, errors, exit status, and existing build logs");
            Check((await bridge.Status())?.Token == state.Token && Files.Hash(pageDestination) == pageHash &&
                File.ReadAllText(frontendDestination) == "second saved markup", "failed builds preserve the last successful files");
            Save(@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value + 1; }");
            var backup = Directory.EnumerateFiles(settings.StateRoot, "*.backup", SearchOption.AllDirectories).First();
            var backupBytes = File.ReadAllBytes(backup);
            File.WriteAllText(backup, "damaged backup");
            try
            {
                await Reject(() => bridge.Call("Check"), "damaged backups stop Cultivate during preflight");
                Check((await bridge.Status())?.Token == state.Token, "damaged backup validation preserves the active journal");
            }
            finally { File.WriteAllBytes(backup, backupBytes); }
            await bridge.Call("Restore");
            Check(File.ReadAllText(frontendDestination) == "server baseline" && Files.Hash(pageDestination) == originalHashes[@"bin\Page.dll"],
                "restore returns original markup and the original target DLL");

            state = await pipeline.Activate(await planner.Create(target, Maps), _ => { }, watch: false);
            var originalPage = File.ReadAllBytes(Directory.EnumerateFiles(settings.StateRoot, "*.backup", SearchOption.AllDirectories)
                .First(file => Files.Hash(file) == originalHashes[@"bin\Page.dll"]));
            var activePageHash = Files.Hash(pageDestination);
            File.WriteAllText(pageDestination, "output from a manual build");
            var manualHash = Files.Hash(pageDestination);
            state = await pipeline.Activate(await planner.Create(target, Maps), _ => { }, watch: false, workerUpdate: true);
            Check(Files.Hash(pageDestination) == activePageHash && File.ReadAllText(frontendDestination) == "second saved markup",
                "a manual build of a deployed file does not block the next update, which publishes its file again");
            Check(Directory.EnumerateFiles(settings.StateRoot, "*.backup", SearchOption.AllDirectories).Any(file => Files.Hash(file) == manualHash),
                "a replaced manual build becomes the original that restore returns to");

            File.WriteAllText(pageDestination, "second manual build");
            Save(@"Page\View.ascx", "feature markup");
            await G("checkout", "-qb", "frontend-only", "master");
            Save(@"Page\View.ascx", "frontend only");
            var frontendTarget = new Target("frontend-only", settings.RepositoryRoot, "refs/heads/frontend-only");
            var frontendPlan = await planner.Create(frontendTarget, Maps);
            Check(frontendPlan.CanDeploy && frontendPlan.Projects.Count == 0, "markup-only selection never schedules compilation");
            var frontendState = await pipeline.Activate(frontendPlan, _ => { }, watch: false);
            Check(frontendState.RuntimeFiles.Count == 0 && File.ReadAllText(frontendDestination) == "frontend only" &&
                File.ReadAllText(pageDestination) == "second manual build", "markup-only activation builds and copies no DLLs");
            Check(frontendState.Updated.Select(file => (file.RelativePath, file.Change)).SequenceEqual(
                [(@"bin\Page.dll", UpdatedFile.Kept), (@"modules\View.ascx", UpdatedFile.Synced)]),
                "a manual build of a file the next activation omits is kept and reported instead of blocking it");
            File.WriteAllText(frontendDestination, "foreign edit");
            var restored = await bridge.Call("Restore");
            Check(File.ReadAllText(frontendDestination) == "foreign edit" &&
                restored.GetProperty("Kept").EnumerateArray().Select(file => file.GetString()).SequenceEqual([@"modules\View.ascx"]) &&
                (await bridge.Status())?.Files.Count == 0, "restore keeps files changed outside Cultivate and reports them");
            File.WriteAllText(frontendDestination, "server baseline");
            File.WriteAllBytes(pageDestination, originalPage);
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

    private static async Task CheckBuildHooks(string home, string msbuild)
    {
        var root = Path.Combine(home, "build hooks");
        var extensions = Path.Combine(root, "user extensions");
        var farm = Path.Combine(root, "farm");
        var run = Path.Combine(root, "run");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(root, "MyMeyer.sln"), "");
        var source = Path.Combine(root, @"bin\Fixture.dll");
        File.WriteAllText(source, "fixture output");
        var project = Path.Combine(root, "Fixture.csproj");
        File.WriteAllText(project, """
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(MSBuildToolsPath)\Microsoft.Common.targets" />
              <PropertyGroup><TargetPath>$(MSBuildProjectDirectory)\bin\Fixture.dll</TargetPath></PropertyGroup>
              <Target Name="Build" />
              <Target Name="CopyBuildOutputToWebfarm" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug'">
                <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(WebfarmPath)bin" />
              </Target>
            </Project>
            """);
        var runtime = Path.Combine(AppContext.BaseDirectory, Hooks.TargetsFileName);
        void Hook(string content)
        {
            foreach (var version in new[] { "Current", "15.0" })
            {
                var hook = Path.Combine(extensions, version, "Microsoft.Common.targets", "ImportAfter", Hooks.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
                File.WriteAllText(hook, content);
            }
        }
        var environment = new Dictionary<string, string>
        {
            ["MSBuildUserExtensionsPath"] = extensions,
            ["WebfarmPath"] = farm + "\\",
            ["CultivateRunDirectory"] = "",
            ["CultivateCaptureTargets"] = ""
        };
        Task<string> Build(Dictionary<string, string> variables, params string[] arguments) => Commands.Run(msbuild,
            [project, "-nologo", "-t:Build", "-p:Configuration=Debug", "-v:minimal", .. arguments], root, variables);
        var destination = Path.Combine(farm, @"bin\Fixture.dll");
        Hook(Hooks.Content(runtime));
        await Build(environment, "-p:WebfarmPath=" + farm + "\\");
        Check(File.ReadAllText(destination) == "fixture output" && !Directory.Exists(Path.Combine(run, "captured")),
            "ordinary builds keep their webfarm copy, including a global WebfarmPath");
        // Older wrappers import the runtime unconditionally; the runtime itself must also leave manual builds alone.
        Hook($"<Project><Import Project=\"{System.Security.SecurityElement.Escape(runtime)}\" /></Project>");
        File.WriteAllText(source, "next manual build");
        await Build(environment, "-p:WebfarmPath=" + farm + "\\");
        Check(File.ReadAllText(destination) == "next manual build", "a direct runtime import also leaves ordinary builds unchanged");
        Hook(Hooks.Content(Path.Combine(root, "missing runtime.targets")));
        await Build(environment);
        Check(File.ReadAllText(destination) == "next manual build", "missing Cultivate runtime files do not break ordinary builds");
        var managed = new Dictionary<string, string>(environment)
        {
            ["CultivateRunDirectory"] = run,
            ["CultivateCaptureTargets"] = Path.Combine(AppContext.BaseDirectory, "Capture.targets")
        };
        var metadata = await Commands.Run(msbuild, [project, "-nologo", "-getProperty:_CultivateCaptureReady"], root, managed);
        Check(string.IsNullOrWhiteSpace(metadata), "a missing runtime is detectable before Cultivate starts a build");
        Hook(Hooks.Content(runtime));
        File.WriteAllText(source, "Cultivate build");
        await Build(managed);
        Check(File.ReadAllText(destination) == "next manual build" &&
            File.ReadAllText(Path.Combine(run, @"captured\Fixture\Fixture.dll")) == "Cultivate build" &&
            File.ReadAllLines(Path.Combine(run, @"manifests\Fixture.artifacts")).Length == 1,
            "Cultivate builds capture their own output without copying to the real webfarm");
        Check(!Directory.EnumerateDirectories(Path.Combine(run, "intercepted")).Any(), "private build staging is cleaned up");
        managed["CultivateCaptureTargets"] = Path.Combine(root, "missing capture.targets");
        await Reject(() => Build(managed), "a missing capture target stops a Cultivate build before its webfarm copy");
        managed["CultivateCaptureTargets"] = Path.Combine(AppContext.BaseDirectory, "Capture.targets");
        managed["CultivateRunDirectory"] = Path.Combine(root, "missing run");
        await Reject(() => Build(managed), "a missing run directory stops a Cultivate build before its webfarm copy");
        managed["CultivateRunDirectory"] = "run";
        await Reject(() => Build(managed), "an existing relative run directory is rejected before the webfarm copy");
        var notDirectory = Path.Combine(root, "not a run directory");
        File.WriteAllText(notDirectory, "fixture file");
        managed["CultivateRunDirectory"] = notDirectory;
        await Reject(() => Build(managed), "a file cannot be used as a Cultivate run directory");
        managed["CultivateRunDirectory"] = run;
        await Reject(() => Build(managed, "-p:WebfarmPath=" + farm + "\\"),
            "an immutable destination cannot bypass Cultivate's private staging");
        Check(File.ReadAllText(destination) == "next manual build", "invalid Cultivate builds preserve existing webfarm files");
    }

    private static async Task CheckDiagnostics(string home)
    {
        var root = Path.Combine(home, "diagnostics [literal]");
        Directory.CreateDirectory(root);
        var buildLog = Path.Combine(root, "build.log");
        var binaryLog = Path.Combine(root, "Page.binlog");
        File.WriteAllText(buildLog, "full build output");
        File.WriteAllText(binaryLog, "binary log fixture");
        var snapshot = Path.Combine(root, "snapshot");
        var errorLine = Path.Combine(snapshot, @"Page\Value.cs") + "(3,4): error CS0246: Type 'Missing[red]' could not be found [Page.csproj]";
        var failure = new InvalidOperationException("MSBuild.exe failed (1).\nwarning MSB3245: Missing reference\n" + errorLine + "\n" + errorLine + "\nfull-output-sentinel");
        Diagnostics.Attach(failure, new("Build") { Project = @"Page\Page.csproj", Tool = "MSBuild.exe", ExitCode = 1, Log = buildLog, BinaryLog = binaryLog });
        Check(ReferenceEquals(Diagnostics.Attach(failure, new("Activate") { Branch = "12345", Worktree = root, Snapshot = snapshot }), failure),
            "diagnostic context preserves the original exception instance and type");
        var report = Diagnostics.Describe(failure);
        Check(report.Label == "BUILD FAILED" && report.Context.Project == @"Page\Page.csproj" && report.Context.Branch == "12345" && report.Context.Worktree == root,
            "build diagnostics preserve the specific phase, project, branch, and worktree through outer failures");
        Check(report.Errors.Count == 1 && report.Errors[0].StartsWith(@"Page\Value.cs(3,4)") && report.Errors[0].Contains("Missing[red]"),
            "diagnostics deduplicate compiler errors, preserve literal text, and show source locations outside the temporary snapshot");
        Check(report.Diagnosis == "Type or assembly reference error" && report.Hints.Any(x => x.Contains("unchanged project")) && !report.Errors.Any(x => x.Contains("warning MSB")),
            "missing type errors get reference guidance without labeling warnings as errors");
        Check(report.Logs.SequenceEqual(new[] { buildLog, binaryLog }) && report.Text.Contains("Exit code: 1"),
            "build diagnostics identify the text log, binary log, and process exit code");
        foreach (var item in new[]
        {
            (Code: "CS1002", Kind: "C# compiler error"), (Code: "CS1705", Kind: "Assembly version conflict"),
            (Code: "MSB3644", Kind: "Missing .NET Framework targeting pack"), (Code: "MSB4019", Kind: "Missing imported build targets"),
            (Code: "NU1301", Kind: "Package restore error"), (Code: "MSB3021", Kind: "File access or copy failure")
        })
            Check(Diagnostics.Describe(new InvalidOperationException($"project: error {item.Code}: fixture"), "Build").Diagnosis == item.Kind,
                $"diagnostics provide targeted guidance for {item.Code}");
        Check(Diagnostics.Describe(new InvalidOperationException("warning CS0246: fixture"), "Build").Diagnosis == "Operation failure",
            "a warning alone is not diagnosed as a compiler failure");
        Check(Diagnostics.Describe(new System.ComponentModel.Win32Exception(2), "Project evaluation").Diagnosis == "Tool or working directory not found",
            "missing executables are distinguished from compiler failures");
        Check(Diagnostics.Describe(new InvalidOperationException("Could not get dispatch ID for Count (error: 0x80010001))."), "Open solution").Diagnosis == "Visual Studio is busy",
            "busy Visual Studio failures identify the modal-dialog and loading checks");
        var many = new InvalidOperationException(string.Join("\n", Enumerable.Range(1, 20).Select(x => $"file{x}.cs: error CS1002: fixture")));
        Check(Diagnostics.Describe(many, "Build").Errors.Count == 6, "large failure output is bounded in the readable summary");
        var logs = Path.Combine(root, "logs");
        var first = Diagnostics.Record(failure, directory: logs);
        var second = Diagnostics.Record(many, "Build", logs);
        Check(first.Incident != second.Incident && Directory.GetFiles(logs, "failure-*.log").Length == 2 && File.ReadAllText(first.Logs[^1]).Contains("full-output-sentinel"),
            "dated incident reports preserve prior failures and the complete original output");
        Check(File.ReadAllText(Path.Combine(logs, "last-error.log")).Contains(second.Incident!) && first.Brief.Contains("CS0246") && first.Brief.Contains(first.Logs[^1]),
            "the latest report and automatic-update summary retain the diagnostic identity and full-report path");
        var blocked = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocked, "fixture");
        var unsaved = Diagnostics.Record(failure, directory: blocked);
        Check(unsaved.Label == "BUILD FAILED" && unsaved.LoggingError is not null && unsaved.Errors.Any(x => x.Contains("CS0246")),
            "an unwritable diagnostic directory does not hide the original failure");

        var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var commandLog = Path.Combine(root, "command.log");
        var command = "[Console]::Out.WriteLine('Value.cs(1,1): error CS1002: fixture'); [Console]::Error.WriteLine('stderr fixture'); exit 7";
        Exception? commandFailure = null;
        try { await Commands.Run(shell, ["-NoProfile", "-Command", command], root, log: commandLog, context: new("Build") { Project = "Page.csproj" }); }
        catch (InvalidOperationException exception) { commandFailure = exception; }
        var commandReport = Diagnostics.Describe(commandFailure ?? throw new InvalidOperationException("Expected a failing process."));
        var commandText = File.ReadAllText(commandLog);
        Check(commandReport.Label == "BUILD FAILED" && commandReport.Context.ExitCode == 7 && commandReport.Errors.Single().Contains("CS1002"),
            "real subprocess failures carry their phase, project, exit code, and compiler output");
        Check(commandText.Contains("Build | Page.csproj") && commandText.Contains("Directory: " + root) && commandText.Contains("STDOUT:") && commandText.Contains("STDERR:") && commandText.Contains("stderr fixture"),
            "command logs separate output streams and label command, directory, phase, project, and exit status");
        commandFailure = null;
        try { await Commands.Run(shell, ["-NoProfile", "-Command", command], root, log: Path.Combine(blocked, "command.log"), context: new("Build")); }
        catch (InvalidOperationException exception) { commandFailure = exception; }
        commandReport = Diagnostics.Describe(commandFailure ?? throw new InvalidOperationException("Expected a failing process."));
        Check(commandReport.Context.ExitCode == 7 && commandReport.Errors.Any(x => x.Contains("CS1002")) && commandReport.LoggingError is not null,
            "command-log write failures preserve the real process error and exit code");
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

    private static void CheckSolutionFiles(string home)
    {
        var root = Path.Combine(home, "document selection with spaces");
        var solution = Path.Combine(root, "MyMeyer.sln");
        string[] paths =
        [
            @"Order\Page.ascx", @"Order\Page.ascx.cs", @"Order\Freight.cs", @"Other\Freight.cs",
            @"Order\Page.ascx.designer.cs", @"Order\Generated.g.cs", @"Order\Generated.g.i.cs",
            @"Order\Unmapped.ascx", @"Order\Unmapped.ascx.designer.cs", @"Order\Unmapped.ascx.cs",
            @"Order\obj\Generated.cs", @"Order\bin\Copy.cs", @"packages\Copy.cs", @"Backup\Copy.cs",
            @"Order\Order.csproj", @"Order\packages.config", @"Order\Changes.resx",
            @"Order\style.css", @"Order\script.js", @"Order\image.png", @"bin\ClosedXML.dll",
            @"Order\obj\Debug\Interop.VBA.dll", "README.md", "schema.sql", "web.config", "Orphan.cs"
        ];
        foreach (var path in paths.Append("MyMeyer.sln"))
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "fixture");
        }
        var plan = new Plan
        {
            Target = new("fixture", root, "refs/heads/fixture"), Commit = "", Fingerprint = "",
            Changes = paths.Select(path => new Change(path, false)).ToList(),
            Projects = [new ProjectInfo
            {
                RelativePath = @"Order\Order.csproj", Assembly = "Order",
                Inputs = paths.Where(path => path.EndsWith(".cs") && path != "Orphan.cs").ToList()
            }],
            Frontend = new[] { @"Order\Page.ascx", @"Order\style.css", @"Order\script.js", @"Order\image.png" }
                .Select(path => new DeployFile(path, path)).ToList()
        };
        plan.Changes.Add(new("order/page.ascx.cs", false));
        plan.Changes.Add(new(@"Order\Deleted.cs", true));
        plan.Changes.Add(new(@"Order\Missing.cs", false));
        plan.Projects[0].Inputs.AddRange([@"Order\Deleted.cs", @"Order\Missing.cs"]);
        var files = SolutionOpener.FilesToOpen(root, solution, Terminal.ChangesToOpen(plan));
        var expected = new[]
        {
            @"Order\Freight.cs", @"Order\Page.ascx", @"Order\Page.ascx.cs", @"Order\script.js", @"Order\style.css",
            @"Order\Unmapped.ascx", @"Order\Unmapped.ascx.cs", @"Other\Freight.cs"
        }.Select(path => Path.Combine(root, path)).Order(Files.Comparer).ToList();
        Check(files.SequenceEqual(expected, Files.Comparer),
            "solution documents contain only actionable source and markup, excluding binaries, generated files, metadata, assets, and unowned code");
        Check(files.Contains(Path.Combine(root, @"Order\Unmapped.ascx"), Files.Comparer) &&
            !files.Any(path => path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)),
            "changed markup without a Code Release Manager mapping still opens, and designer files never do");
        Check(files.Count(path => path.EndsWith("Page.ascx.cs", StringComparison.OrdinalIgnoreCase)) == 1,
            "solution documents deduplicate case and separator variants after resolving their full paths");
        Check(files.Count(path => Path.GetFileName(path) == "Freight.cs") == 2,
            "different source files with the same filename remain distinct documents");
        var opened = new List<string> { expected[0].ToUpperInvariant().Replace('\\', '/'), "Untitled1" };
        var missing = SolutionOpener.MissingDocuments(files.Concat([files[0], files[0].ToUpperInvariant()]), opened);
        Check(missing.SequenceEqual(expected.Skip(1), Files.Comparer),
            "opening changed files skips existing document tabs and duplicate requests");
        opened.AddRange(missing);
        Check(SolutionOpener.MissingDocuments(files, opened).Count == 0,
            "reopening a solution does not open its changed documents a second time");
        Check(SolutionOpener.MissingDocuments([files[0]], [Path.Combine(home, "another checkout", Path.GetFileName(files[0]))]).Count == 1,
            "documents in another checkout do not hide the selected worktree's file");
    }
    private static void CheckSolutionAutomation(string home)
    {
        Exception[] busy =
        [
            new COMException("Call rejected", unchecked((int)0x80010001)),
            new COMException("Retry later", unchecked((int)0x8001010A)),
            new MissingMemberException("Could not get dispatch ID for Count (error: 0x80010001))."),
            new MissingMemberException("Could not get dispatch ID for Item (error: 0x8001010a)).")
        ];
        foreach (var failure in busy)
        {
            var attempts = 0;
            var result = SolutionOpener.Retry(() =>
            {
                if (++attempts == 1) throw failure;
                return 42;
            }, TimeSpan.FromSeconds(2));
            Check(result == 42 && attempts == 2,
                $"solution automation retries {failure.GetType().Name}: {failure.Message}");

            attempts = 0;
            Exception? caught = null;
            try { SolutionOpener.Retry<int>(() => { attempts++; throw failure; }, TimeSpan.Zero); }
            catch (Exception exception) { caught = exception; }
            Check(ReferenceEquals(caught, failure) && attempts == 1,
                $"solution automation preserves the busy error when its retry deadline expires: {failure.Message}");
        }

        Exception[] permanent =
        [
            new MissingMemberException("Count is not a member"),
            new MissingMemberException("Could not get dispatch ID for Count (error: 0x80020006))."),
            new COMException("Unspecified failure", unchecked((int)0x80004005)),
            new InvalidOperationException("An unrelated failure mentioning 0x80010001)")
        ];
        foreach (var failure in permanent)
        {
            var attempts = 0;
            Exception? caught = null;
            try { SolutionOpener.Retry<int>(() => { attempts++; throw failure; }, TimeSpan.FromSeconds(2)); }
            catch (Exception exception) { caught = exception; }
            Check(ReferenceEquals(caught, failure) && attempts == 1,
                $"solution automation reports non-busy failures immediately: {failure.Message}");
        }

        var requested = new[] { Path.Combine(home, "First.cs"), Path.Combine(home, "Second.cs") };
        var opened = new List<string>();
        var openingAttempts = 0;
        SolutionOpener.Retry(() =>
        {
            openingAttempts++;
            foreach (var file in SolutionOpener.MissingDocuments(requested, opened))
            {
                opened.Add(file);
                if (openingAttempts == 1) throw busy[2];
            }
            return true;
        }, TimeSpan.FromSeconds(2));
        Check(openingAttempts == 2 && opened.SequenceEqual(requested, Files.Comparer),
            "retrying a partially completed solution open preserves documents without opening duplicates");
    }

    private static async Task CheckSolutions(string home)
    {
        var root = Path.Combine(home, "solutions with spaces");
        Directory.CreateDirectory(root);
        var git = new GitRepository(new Settings { RepositoryRoot = root });
        async Task G(params string[] args) => _ = await git.Git(root, args);
        async Task<List<string>> ChangedFiles(string selectedSolution)
        {
            var selectedRoot = Path.GetDirectoryName(selectedSolution)!;
            var selected = new Target("fixture", selectedRoot, await git.Git(selectedRoot, "rev-parse", "HEAD"));
            return SolutionOpener.FilesToOpen(selectedRoot, selectedSolution, (await git.Inspect(selected)).Changes);
        }
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
            Check((await ChangedFiles(solution)).Count == 0,
                "an unchanged branch opens only the solution, not a duplicate solution document");
            var opened = await git.Solution(branch);
            Check(opened != solution && File.ReadAllText(opened) == "committed solution",
                "opening a local branch without a checkout creates its own solution worktree");
            var openedRoot = Path.GetDirectoryName(opened)!;
            Check(await git.Git(openedRoot, "symbolic-ref", "HEAD") == branch.Ref,
                "the new worktree checks out the selected local branch, not a detached snapshot");
            var changed = await ChangedFiles(opened);
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
            changed = await ChangedFiles(opened);
            Check(changed.Select(Path.GetFileName).SequenceEqual(
                ["Added file.cs", "Changed file.cs", "New local file.cs", "Renamed file.cs", "Staged file.cs", "Unchanged.cs"]),
                "solution documents include staged, unstaged, and untracked work while excluding ignored files");
            File.Delete(Path.Combine(openedRoot, "Added file.cs"));
            Check(!(await ChangedFiles(opened)).Any(path => Path.GetFileName(path) == "Added file.cs"),
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

        // The worktrees folder adds checkouts from other repositories, each opened against its own default branch.
        var worktrees = Path.Combine(home, "worktrees folder");
        var other = Path.Combine(home, "other repository");
        var otherWorktree = Path.Combine(worktrees, "Other", "feature work");
        var myMeyerWorktree = Path.Combine(worktrees, "MyMeyer", "listed already");
        var noSolution = Path.Combine(worktrees, "Database", "scripts");
        async Task Commit(string repository, params string[] files)
        {
            Directory.CreateDirectory(repository);
            await git.Git(repository, "init", "-q", "-b", "main");
            await git.Git(repository, "config", "user.email", "cultivate-test@localhost");
            await git.Git(repository, "config", "user.name", "Cultivate isolated tests");
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repository, file))!);
                File.WriteAllText(Path.Combine(repository, file), "baseline " + file);
            }
            await git.Git(repository, "add", ".");
            await git.Git(repository, "commit", "-qm", "fixture");
        }
        await Commit(other, @"App\App.sln", @"App\Program.cs", @"Lib\Lib.cs");
        await Commit(noSolution, "schema.sql");
        await git.Git(other, "worktree", "add", "-q", "-b", "feature", otherWorktree);
        await G("worktree", "add", "-q", "--detach", myMeyerWorktree);
        try
        {
            File.WriteAllText(Path.Combine(otherWorktree, @"App\Program.cs"), "feature edit");
            File.WriteAllText(Path.Combine(otherWorktree, @"App\New.cs"), "untracked feature file");
            File.WriteAllText(Path.Combine(otherWorktree, @"Lib\Lib.cs"), "edit outside the solution folder");
            var scoped = new GitRepository(new Settings { RepositoryRoot = root, WorktreesRoot = worktrees });
            var others = await scoped.OtherWorktrees(await scoped.Targets());
            Check(others.SequenceEqual([new Target("feature", otherWorktree, "refs/heads/feature")]),
                "the worktrees folder lists other repositories' checkouts once, skipping known MyMeyer worktrees and folders without a solution");
            File.WriteAllText(solution, "main checkout solution");
            await git.Git(other, "checkout", "-qb", "primary-work-in-progress");
            var fromLinkedCheckout = new GitRepository(new Settings { RepositoryRoot = myMeyerWorktree, WorktreesRoot = worktrees });
            var mainRoots = await fromLinkedCheckout.MainWorktrees((await scoped.Targets()).Concat(others).Concat(others));
            Check(mainRoots.SequenceEqual(new[] { root, other }.OrderBy(Path.GetFileName, Files.Comparer)),
                "main solutions resolve each repository's primary checkout once, even from linked worktrees and a non-default branch");
            Check((await new GitRepository(new Settings { RepositoryRoot = root }).MainWorktrees([])).SequenceEqual([root]),
                "the configured repository's main solution is available without a worktrees folder");
            File.Delete(Path.Combine(other, @"App\App.sln"));
            Check((await scoped.MainWorktrees(others)).SequenceEqual([root]),
                "a repository whose main checkout has no solution is omitted without creating or switching worktrees");
            Check(await git.Git(other, "branch", "--show-current") == "primary-work-in-progress" &&
                File.ReadAllText(Path.Combine(otherWorktree, @"App\Program.cs")) == "feature edit",
                "main solution discovery preserves primary branches and linked-checkout edits");
            Check(await scoped.DefaultBranch(otherWorktree) == "main" &&
                SolutionOpener.Solutions(otherWorktree).SequenceEqual([Path.Combine(otherWorktree, @"App\App.sln")]),
                "another repository uses its own default branch and finds a solution one folder down");
            var appSolution = Path.Combine(otherWorktree, @"App\App.sln");
            var opened = SolutionOpener.FilesToOpen(otherWorktree, appSolution,
                (await scoped.Inspect(others[0], await scoped.DefaultBranch(otherWorktree))).Changes);
            Check(opened.Select(path => Path.GetRelativePath(otherWorktree, path)).SequenceEqual([@"App\New.cs", @"App\Program.cs"]),
                "another repository opens its changed source files under the chosen solution's folder");
            Check((await new GitRepository(new Settings { RepositoryRoot = root }).OtherWorktrees([])).Count == 0,
                "without a worktrees folder, only MyMeyer's worktrees are offered");
        }
        finally
        {
            await git.Git(other, "worktree", "remove", "--force", otherWorktree);
            await G("worktree", "remove", "--force", myMeyerWorktree);
        }
    }

    private static async Task CheckBaseline(string home)
    {
        var root = Path.Combine(home, "baseline repository");
        Directory.CreateDirectory(root);
        var settings = new Settings { RepositoryRoot = root, WebfarmRoot = Path.Combine(home, "baseline farm") };
        var git = new GitRepository(settings);
        var planner = new Planner(settings, git, new Bridge(settings));
        async Task G(params string[] args) => _ = await git.Git(root, args);
        void Save(string name, string content) => File.WriteAllText(Path.Combine(root, name), content);
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        Save("UnprintedOrders.ascx", "baseline markup");
        Save("Unrelated.ascx", "old markup");
        Save("Unrelated.cs", "old compiled input");
        await G("add", ".");
        await G("commit", "-qm", "local master");
        var stale = await git.Git(root, "rev-parse", "HEAD");
        await G("checkout", "-qb", "feature");
        Save("Unrelated.ascx", "already merged markup");
        Save("Unrelated.cs", "already merged compiled input");
        await G("add", ".");
        await G("commit", "-qm", "advance remote master only");
        var upstream = await git.Git(root, "rev-parse", "HEAD");
        await G("remote", "add", "company", root);
        await G("update-ref", "refs/remotes/company/master", upstream);
        await G("branch", "--set-upstream-to=company/master", "master");
        Save("UnprintedOrders.ascx", "feature markup");
        await G("add", ".");
        await G("commit", "-qm", "markup-only feature");
        var target = new Target("feature", root, "refs/heads/feature");
        var branch = target with { Worktree = null };
        var mapsRequested = new List<string>();
        Task<Dictionary<string, string>> Maps(string names)
        {
            mapsRequested.Add(names);
            return Task.FromResult(new Dictionary<string, string> { ["UnprintedOrders.ascx"] = "DesktopModules/UnprintedOrders" });
        }
        foreach (var selected in new[] { target, branch })
        {
            var plan = await planner.Create(selected, Maps);
            Check(plan.CanDeploy && plan.Baseline == upstream && plan.Changes.Select(change => change.Path).SequenceEqual(["UnprintedOrders.ascx"]) &&
                plan.Frontend.Count == 1 && plan.Projects.Count == 0,
                $"{(selected.Worktree is null ? "branch" : "worktree")} preview uses the cached upstream, excluding merged files and DLL builds");
        }
        Check(mapsRequested.SequenceEqual(["UnprintedOrders.ascx", "UnprintedOrders.ascx"]),
            "stale local master does not request mappings for already merged frontend files");
        Check((await git.Inspect(branch, "company/master")).Baseline == upstream,
            "an explicitly configured remote baseline is used directly");
        Check((await git.Inspect(branch, stale)).Baseline == stale,
            "an explicitly configured commit baseline is preserved");

        Save("Unrelated.ascx", "saved frontend edit");
        Save("Staged.cs", "staged compiled input");
        await G("add", "Staged.cs");
        Save("Untracked.css", "untracked frontend");
        File.Delete(Path.Combine(root, "Unrelated.cs"));
        var dirty = await git.Inspect(target);
        Check(dirty.Changes.Select(change => change.Path).SequenceEqual(
            ["Staged.cs", "UnprintedOrders.ascx", "Unrelated.ascx", "Unrelated.cs", "Untracked.css"]) &&
            dirty.Changes.Single(change => change.Path == "Unrelated.cs").Deleted,
            "upstream comparison retains staged, saved, untracked, and deleted worktree files");
        Check((await git.Inspect(branch)).Changes.Select(change => change.Path).SequenceEqual(["UnprintedOrders.ascx"]),
            "branch inspection excludes another checkout's uncommitted work");
        Check(await git.Git(root, "rev-parse", "master") == stale && await git.Git(root, "branch", "--show-current") == "feature" &&
            File.ReadAllText(Path.Combine(root, "Unrelated.ascx")) == "saved frontend edit",
            "baseline inspection preserves the local master, selected branch, and saved edits");
        await G("branch", "--unset-upstream", "master");
        Check((await git.Inspect(branch)).Baseline == stale,
            "a local-only baseline still works without a configured upstream");
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
        Check(picker.Index == -1 && picker.Selected is null &&
            Render(picker, 100, 30).Contains("> Open main worktree solution"),
            "the solution picker initially selects its pinned main worktree action");
        Key(ConsoleKey.DownArrow);
        Check(picker.Selected == targets[0], "the pinned action navigates down to the first checkout");
        Key(ConsoleKey.UpArrow);
        Key(ConsoleKey.UpArrow);
        Check(picker.Selected == targets[36], "navigating up from the pinned action wraps to the last checkout");
        Check(Render(picker, 100, 30).Contains("Open main worktree solution"),
            "the main worktree action stays visible while paging through checkouts");
        Key(ConsoleKey.Home);
        Check(picker.Index == -1, "Home returns to the pinned main worktree action");
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
        Check(menuScreen.Contains("Open a repository's main solution") && !menuScreen.Contains("Browse versions and preview"),
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
        Check(Terminal.HomeActions.Select(action => action.Label).SequenceEqual(
            ["Update my webfarm", "Open solution", "Restore original webfarm files", "Settings", "Exit"]),
            "the homepage uses Update my webfarm and omits refresh, logs, and the guide");
        Check(Terminal.PreviewActions(false).Select(action => action.Label).SequenceEqual(["Show every changed file", "Back"]),
            "blocked previews retain inspection and back actions but cannot activate");
        Check(Terminal.PreviewActions(false, true).Any(action => action.Label == "Choose files to deploy"),
            "blocked previews allow excluding files with mapping or ownership errors");

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
            picker = new VersionPicker(targets, new DeploymentState { Active = true, SourceRoot = targets[17].Worktree! }, openSolution: true);
            var selected = picker.Show(LiveConsole(output), () => new('\r', ConsoleKey.Enter, false, false, false));
            Check(selected is null && picker.OpenMainWorktree,
                "Enter chooses the pinned main solution action even when another checkout is deployed");
        }
        using (var output = new StringWriter())
        {
            picker = new VersionPicker([], openSolution: true);
            Check(Render(picker, 60, 24).Contains("Open main worktree solution"),
                "the main solution action remains available without listed branches or worktrees");
            var selected = picker.Show(LiveConsole(output), () => new('\u001b', ConsoleKey.Escape, false, false, false));
            Check(selected is null && !picker.OpenMainWorktree, "Escape cancels without choosing a main solution");
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
        Check(Terminal.SelectableChanges(preview).Count == 2, "file selection offers frontend and compiled changes, not generated DLLs or documentation");
        preview.ExcludedPaths.Add("orderingsystem/order/ucorder.ascx.cs");
        Check(Terminal.ActionableChanges(preview).Count == 1 && Terminal.SelectableChanges(preview).Count == 2,
            "excluded files lose deployment actions but remain available to reinclude");
        preview.ExcludedPaths.Clear();
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

    private static async Task CheckExclusions(string home, string msbuild)
    {
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(home, "exclusions", "repository"),
            WebfarmRoot = Path.Combine(home, "exclusions", "farm"),
            StateRoot = Path.Combine(home, "exclusions", "state"), MSBuild = msbuild
        };
        Directory.CreateDirectory(settings.RepositoryRoot);
        Directory.CreateDirectory(settings.WebfarmRoot);
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var planner = new Planner(settings, git, bridge);
        async Task G(params string[] args) => _ = await git.Git(settings.RepositoryRoot, args);
        void Save(string path, string content)
        {
            var full = Files.Child(settings.RepositoryRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        var baselineProject = Project("ExclusionFixture").Replace("<Compile Include=\"Value.cs\" />",
            "<Compile Include=\"Value.cs\" /><Compile Include=\"Other.cs\" /><Compile Include=\"Gone.cs\" />");
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        Save(".gitignore", "bin/\nobj/\n");
        Save("MyMeyer.sln", "");
        Save("Fixture/ExclusionFixture.csproj", baselineProject);
        Save("Fixture/App.config", "<configuration />");
        Save("Fixture/Value.cs", "public class ExclusionValue { public static int Value => 1; }");
        Save("Fixture/Other.cs", "public class OtherValue { public static int Value => 10; }");
        Save("Fixture/Gone.cs", "public class GoneValue { }");
        Save("Fixture/View.ascx", "baseline markup");
        Save("Fixture/Removed.css", "baseline css");
        await G("add", ".");
        await G("commit", "-qm", "baseline");
        await G("checkout", "-qb", "feature");
        Save("Fixture/Value.cs", "public class ExclusionValue { public static int Value => 2; }");
        Save("Fixture/Other.cs", "excluded committed code cannot compile;");
        Save("Fixture/ExclusionFixture.csproj", baselineProject.Replace("Gone.cs", "New.cs"));
        File.Delete(Files.Child(settings.RepositoryRoot, "Fixture/Gone.cs"));
        Save("Fixture/New.cs", "excluded added code cannot compile;");
        Save("Fixture/View.ascx", "excluded markup");
        File.Delete(Files.Child(settings.RepositoryRoot, "Fixture/Removed.css"));
        await G("add", ".");
        await G("commit", "-qm", "mixed selected and excluded changes");
        Save("Fixture/Untracked.cs", "excluded untracked code;");
        var target = new Target("feature", settings.RepositoryRoot, "refs/heads/feature");
        string[] excluded = [@"fixture\other.cs", "Fixture/Gone.cs", "Fixture/New.cs", "Fixture/Untracked.cs",
            "Fixture/ExclusionFixture.csproj", "Fixture/View.ascx", "Fixture/Removed.css"];
        Task<Dictionary<string, string>> NoMaps(string _) => throw new InvalidOperationException("Excluded frontend must not request mappings.");
        var plan = await planner.Create(target, NoMaps, excluded);
        Check(plan.CanDeploy && plan.Frontend.Count == 0 && plan.Projects.Single().Assembly == "ExclusionFixture",
            "exclusions remove frontend mapping errors and retain only selected project actions");
        Check(plan.Projects[0].Inputs.Any(path => path.EndsWith("Gone.cs")) && !plan.Projects[0].Inputs.Any(path => path.EndsWith("New.cs")),
            "project ownership uses the baseline version of an excluded project file");
        var before = await git.Git(settings.RepositoryRoot, "status", "--porcelain");
        var snapshot = await git.Snapshot(plan, Path.Combine(home, "exclusions", "snapshot"));
        try
        {
            Check(File.ReadAllText(Files.Child(snapshot, "Fixture/Other.cs")).Contains("=> 10") &&
                File.Exists(Files.Child(snapshot, "Fixture/Gone.cs")) && !File.Exists(Files.Child(snapshot, "Fixture/New.cs")) &&
                !File.Exists(Files.Child(snapshot, "Fixture/Untracked.cs")),
                "snapshot restores excluded modifications and deletions and removes excluded additions");
            Check(File.ReadAllText(Files.Child(snapshot, "Fixture/Value.cs")).Contains("=> 2") &&
                File.ReadAllText(Files.Child(snapshot, "Fixture/View.ascx")) == "baseline markup",
                "snapshot preserves selected changes while excluded frontend uses baseline contents");
        }
        finally { await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", snapshot); }
        var branch = target with { Worktree = null };
        var branchPlan = await planner.Create(branch, NoMaps, excluded);
        Check(branchPlan.CanDeploy && branchPlan.Projects.Single().Inputs.Any(path => path.EndsWith("Gone.cs")),
            "committed branch previews respect excluded project changes too");
        Check(before == await git.Git(settings.RepositoryRoot, "status", "--porcelain") &&
            File.ReadAllText(Files.Child(settings.RepositoryRoot, "Fixture/Other.cs")) == "excluded committed code cannot compile;",
            "planning and snapshot exclusions never alter the selected source checkout");
        var allExcluded = await planner.Create(target, NoMaps, plan.Changes.Select(change => change.Path));
        Check(allExcluded.Frontend.Count == 0 && allExcluded.Projects.Count == 0,
            "excluding all changes schedules no frontend copies or DLL builds");
        var frontendOnly = await planner.Create(target, _ => Task.FromResult(
            new Dictionary<string, string> { ["View.ascx"] = "modules" }),
            excluded.Where(path => path != "Fixture/View.ascx").Append("Fixture/Value.cs"));
        Check(frontendOnly.CanDeploy && frontendOnly.Frontend.Count == 1 && frontendOnly.Projects.Count == 0,
            "excluding the last compiled change removes its DLL build while keeping selected frontend");
        var includedFrontend = await planner.Create(target, _ => Task.FromResult(new Dictionary<string, string>()),
            excluded.Where(path => path != "Fixture/View.ascx"));
        Check(!includedFrontend.CanDeploy, "reincluded unmapped files restore their validation errors");
        File.WriteAllText(Path.Combine(settings.WebfarmRoot, "View.ascx"), "server markup");
        File.WriteAllText(Path.Combine(settings.WebfarmRoot, "Removed.css"), "server css");
        System.Diagnostics.Process? worker = null;
        try
        {
            var state = await new Pipeline(settings, git, bridge).Activate(plan, _ => { }, watch: true);
            worker = System.Diagnostics.Process.GetProcessById((await bridge.Status())!.WatcherPid);
            Check(state.ExcludedPaths.SequenceEqual(plan.ExcludedPaths) && state.Files.Single().RelativePath == @"bin\ExclusionFixture.dll",
                "activation journals exclusions and publishes only the selected DLL");
            var built = System.Reflection.Assembly.Load(File.ReadAllBytes(state.RuntimeFiles.Single().Source));
            Check((int)built.GetType("ExclusionValue")!.GetProperty("Value")!.GetValue(null)! == 2 &&
                (int)built.GetType("OtherValue")!.GetProperty("Value")!.GetValue(null)! == 10,
                "the published DLL contains selected code and baseline excluded code from the same project");
            Save("Fixture/Other.cs", "excluded saved code still cannot compile;");
            var excludedSave = await planner.Create(target, NoMaps, state.ExcludedPaths);
            Check(excludedSave.BuildFingerprint == plan.BuildFingerprint,
                "saving excluded code does not invalidate the compiled artifact cache");
            Save("Fixture/Value.cs", "public class ExclusionValue { public static int Value => 3; }");
            DeploymentState? updated = null;
            var expected = (await git.Inspect(target)).Fingerprint;
            for (var deadline = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < deadline; await Task.Delay(500))
            {
                updated = await bridge.Status();
                if (updated?.Active == true && updated.Fingerprint == expected) break;
            }
            Check(updated?.Active == true && updated.Fingerprint == expected && updated.ExcludedPaths.SequenceEqual(plan.ExcludedPaths),
                "automatic updates preserve exclusions while publishing later selected saves");
            var rebuilt = System.Reflection.Assembly.Load(File.ReadAllBytes(updated!.RuntimeFiles.Single().Source));
            Check((int)rebuilt.GetType("ExclusionValue")!.GetProperty("Value")!.GetValue(null)! == 3 &&
                (int)rebuilt.GetType("OtherValue")!.GetProperty("Value")!.GetValue(null)! == 10,
                "automatic rebuilds include the selected save and keep excluded code at baseline");
            Check(File.ReadAllText(Path.Combine(settings.WebfarmRoot, "View.ascx")) == "server markup" &&
                File.ReadAllText(Path.Combine(settings.WebfarmRoot, "Removed.css")) == "server css",
                "excluded frontend files are neither copied nor deleted during activation or automatic updates");
        }
        finally
        {
            await bridge.Call("Restore");
            if (worker is not null)
            {
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                worker.Dispose();
            }
        }
    }

    // Mirrors a MeyerObjects BusinessLogic bump: the changed project file references a newer package the webfarm doesn't have yet.
    private static async Task CheckPackageDependencies(string home, string msbuild)
    {
        var root = Path.Combine(home, "package dependencies");
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(root, "repository"), WebfarmRoot = Path.Combine(root, "farm"),
            StateRoot = Path.Combine(root, "state"), MSBuild = msbuild
        };
        Directory.CreateDirectory(settings.RepositoryRoot);
        Directory.CreateDirectory(Path.Combine(settings.WebfarmRoot, "bin"));
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var planner = new Planner(settings, git, bridge);
        var pipeline = new Pipeline(settings, git, bridge);
        async Task G(params string[] args) => _ = await git.Git(settings.RepositoryRoot, args);
        void Save(string path, string content)
        {
            var full = Files.Child(settings.RepositoryRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        const string library = "CultivatePackageFixture";
        string Package(string version) => $@"packages\{library}.{version}\lib\net471\{library}.dll";
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            // Built outside any MyMeyer.sln and without a webfarm copy target, so the per-user hook never applies.
            var source = Path.Combine(root, "library " + version);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Library.csproj"), $$"""
                <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <OutputType>Library</OutputType><TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                    <OutputPath>out\</OutputPath><AssemblyName>{{library}}</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Value.cs" /><Reference Include="System" /></ItemGroup>
                  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
                </Project>
                """);
            File.WriteAllText(Path.Combine(source, "Value.cs"),
                $"[assembly: System.Reflection.AssemblyVersion(\"{version}.0\")] public class PackageValue {{ public static int Value => {version[0]}; }}");
            await Commands.Run(msbuild, [Path.Combine(source, "Library.csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], source);
            var package = Files.Child(settings.RepositoryRoot, Package(version));
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.Copy(Path.Combine(source, "out", library + ".dll"), package);
        }
        string Consumer(string name) => Project(name).Replace("<Reference Include=\"System\" />", $"""
            <Reference Include="System" />
            <Reference Include="{library}, Version=1.0.0.0, Culture=neutral, processorArchitecture=MSIL">
              <HintPath>..\{Package("1.0.0")}</HintPath>
            </Reference>
            """);
        var baselineProject = Consumer("PackageConsumer");
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        Save(".gitignore", "bin/\nobj/\npackages/\n");
        Save("MyMeyer.sln", "");
        Save(@"Consumer\PackageConsumer.csproj", baselineProject);
        Save(@"Consumer\App.config", "<configuration />");
        Save(@"Consumer\Value.cs", "public class ConsumerValue { public static int Value => PackageValue.Value; }");
        await G("add", ".");
        await G("commit", "-qm", "baseline on package 1.0.0");

        await G("checkout", "-qb", "same-package");
        Save(@"Consumer\PackageConsumer.csproj", baselineProject.Replace("<Compile Include=\"Value.cs\" />",
            "<Compile Include=\"Value.cs\" /><Compile Include=\"More.cs\" />"));
        Save(@"Consumer\More.cs", "public class MoreValue { }");
        await G("add", ".");
        await G("commit", "-qm", "project change that keeps the package");
        var same = await planner.Create(new("same-package", settings.RepositoryRoot, "refs/heads/same-package"));
        Check(same.CanDeploy && same.Projects.Single().PackageDependencies.Count == 0 &&
            same.RuntimePaths().SetEquals([@"bin\PackageConsumer.dll"]),
            "a changed project file that keeps its package references publishes only the project's own DLL");

        await G("checkout", "-qb", "package-bump", "master");
        Save(@"Consumer\PackageConsumer.csproj", baselineProject.Replace("1.0.0", "2.0.0"));
        await G("commit", "-qam", "bump the package to 2.0.0");
        var target = new Target("package-bump", settings.RepositoryRoot, "refs/heads/package-bump");
        var plan = await planner.Create(target);
        Check(plan.CanDeploy && plan.Projects.Single().PackageDependencies.SequenceEqual([@"..\" + Package("2.0.0")]) &&
            plan.RuntimePaths().SetEquals([@"bin\PackageConsumer.dll", $@"bin\{library}.dll"]) && plan.Notes.Any(note => note.Contains(library + ".dll")),
            "a bumped package reference schedules the package's DLL with its project and says so in the preview");
        var farmPackage = Files.Child(settings.WebfarmRoot, $@"bin\{library}.dll");
        File.Copy(Files.Child(settings.RepositoryRoot, Package("1.0.0")), farmPackage);
        try
        {
            var state = await pipeline.Activate(plan, _ => { }, watch: false);
            Check(state.Updated.Select(file => (file.RelativePath, file.Change)).SequenceEqual(
                    [($@"bin\{library}.dll", UpdatedFile.Package), (@"bin\PackageConsumer.dll", UpdatedFile.Built)]) &&
                Files.Hash(farmPackage) == Files.Hash(Files.Child(settings.RepositoryRoot, Package("2.0.0"))),
                "activation copies the bumped package DLL to the webfarm along with its project's DLL");
            var progress = new List<string>();
            state = await pipeline.Activate(await planner.Create(target), progress.Add, watch: false, workerUpdate: true);
            Check(progress.Any(message => message.StartsWith("Frontend-only update")) &&
                state.Updated.Any(file => file.RelativePath == $@"bin\{library}.dll" && file.Change == UpdatedFile.Package),
                "updates without compiled changes reuse the verified package DLL instead of rebuilding");
            await bridge.Call("Restore");
            Check(Files.Hash(farmPackage) == Files.Hash(Files.Child(settings.RepositoryRoot, Package("1.0.0"))) &&
                !File.Exists(Files.Child(settings.WebfarmRoot, @"bin\PackageConsumer.dll")),
                "restore returns the webfarm's original package DLL");
        }
        finally
        {
            if ((await bridge.Status())?.Active == true) await bridge.Call("Restore");
        }

        await G("checkout", "-qb", "package-conflict", "package-bump");
        Save(@"Second\Second.csproj", Consumer("Second"));
        Save(@"Second\App.config", "<configuration />");
        Save(@"Second\Value.cs", "public class SecondValue { public static int Value => PackageValue.Value; }");
        await G("add", ".");
        await G("commit", "-qm", "a new project on the old package");
        var conflict = await planner.Create(new("package-conflict", settings.RepositoryRoot, "refs/heads/package-conflict"));
        Check(!conflict.CanDeploy && conflict.Errors.Any(error => error.Contains($"different copies of {library}.dll")),
            "changed projects that need different copies of one package DLL block activation");
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
