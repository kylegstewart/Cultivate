using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Cultivate;

internal static partial class SelfTest
{
    // Mirrors a ticket that changes BusinessLogic and MyMeyer together: the library worktree's build is deployed with the MyMeyer change.
    private static async Task CheckLibraryLoop(string home, string msbuild)
    {
        Check(LibraryLoop.SiteFramework(["net471", "netstandard2.0"]) == "net471" && LibraryLoop.SiteFramework(["net462", "net47"]) == "net47" &&
            LibraryLoop.SiteFramework(["netstandard2.0", "net8.0"]) == "netstandard2.0" && LibraryLoop.SiteFramework(["net8.0"]) is null,
            "a library builds for the newest .NET Framework target, else netstandard2.0, else not at all");
        Check(LibraryLoop.Requested(["plan", "--worktree", "w", "--library", @"C:\a", "--library", @"C:\b", "--libraries"])
            .SequenceEqual([@"C:\a", @"C:\b", LibraryLoop.Every]) && LibraryLoop.Requested(["plan", "--worktree", "w"]).Count == 0,
            "--library is repeatable and --libraries asks for every detected match");
        await Reject(() => Task.Run(() => LibraryLoop.Requested(["activate", "--library", "--yes"])), "--library without a path is rejected");

        var root = Path.Combine(home, "ll");
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(root, "gh", "MyMeyer"), WebfarmRoot = Path.Combine(root, "farm"),
            StateRoot = Path.Combine(root, "state"), MSBuild = msbuild
        };
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var pipeline = new Pipeline(settings, git, bridge);
        void Save(string folder, string path, string content)
        {
            var full = Files.Child(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        async Task Commit(string folder, string message)
        {
            await git.Git(folder, "add", "-A");
            await git.Git(folder, "commit", "-qm", message);
        }

        // Farm copies of the library, built outside any MyMeyer.sln so the per-user hook never applies.
        var farmBuilds = new Dictionary<string, string>();
        foreach (var version in new[] { "1.0.0", "1.2.0" })
        {
            var source = Path.Combine(root, "farm build " + version);
            Save(source, "Library.csproj", """
                <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <OutputType>Library</OutputType><TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                    <OutputPath>out\</OutputPath><AssemblyName>Loop.Library</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Value.cs" /><Reference Include="System" /></ItemGroup>
                  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
                </Project>
                """);
            Save(source, "Value.cs", $"[assembly: System.Reflection.AssemblyVersion(\"{version}.0\")] public class LibraryValue {{ public static int Value => 1; }}");
            await Commands.Run(msbuild, [Path.Combine(source, "Library.csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], source);
            farmBuilds[version] = Path.Combine(source, "out", "Loop.Library.dll");
        }
        var farmLibrary = Path.Combine(settings.WebfarmRoot, "bin", "Loop.Library.dll");
        var farmHelper = Path.Combine(settings.WebfarmRoot, "bin", "Loop.Helper.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(farmLibrary)!);
        File.Copy(farmBuilds["1.0.0"], farmLibrary);
        File.WriteAllText(farmHelper, "farm helper");
        var originalLibrary = Files.Hash(farmLibrary);

        // The library repository: SDK-style net471 projects that restore offline with the installed targeting pack.
        var loop = await FixtureRepository(git, Path.Combine(root, "gh", "Libraries--Loop"), "master");
        string SdkProject(string version, string extra = "") => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net471</TargetFrameworks>
                <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
                <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
                <Version>{version}</Version>
              </PropertyGroup>
              {extra}
            </Project>
            """;
        var libraryProject = SdkProject("1.0.0");
        Save(loop, "NuGet.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        Save(loop, ".gitignore", "bin/\nobj/\n");
        Save(loop, @"Library\Loop.Library.csproj", libraryProject);
        Save(loop, @"Library\Value.cs", "public class LibraryValue { public static int Value => 1; }");
        Save(loop, @"Helper\Loop.Helper.csproj", SdkProject("1.0.0"));
        Save(loop, @"Helper\Value.cs", "public class HelperValue { public static int Value => 40; }");
        Save(loop, @"Extra\Loop.Extra.csproj", SdkProject("1.0.0"));
        Save(loop, @"Extra\Value.cs", "public class ExtraValue { public static int Value => 1; }");
        await Commit(loop, "library baseline");
        var trees = Path.Combine(root, "trees");
        var loopTicket = Path.Combine(trees, "Libraries--Loop", "4242-worktree");
        var loopFollowUp = Path.Combine(trees, "Libraries--Loop", "4242-2-worktree");
        var loopFeature = Path.Combine(trees, "Libraries--Loop", "feature-x");
        await git.Git(loop, "worktree", "add", "-q", "-b", "4242", loopTicket);
        await git.Git(loop, "worktree", "add", "-q", "-b", "4242-2", loopFollowUp);
        await git.Git(loop, "worktree", "add", "-q", "-b", "feature-x", loopFeature);
        await git.Git(loop, "worktree", "add", "-q", "--detach", Path.Combine(trees, "Libraries--Loop", "detached"));
        // The ticket adds a member MyMeyer uses, a project reference the farm may lack, and changes a project the farm does not have.
        Save(loopTicket, @"Library\Loop.Library.csproj", SdkProject("1.1.0",
            """<ItemGroup><ProjectReference Include="..\Helper\Loop.Helper.csproj" /></ItemGroup>"""));
        Save(loopTicket, @"Extra\Value.cs", "public class ExtraValue { public static int Value => 2; }");
        await Commit(loopTicket, "library ticket");
        Save(loopTicket, @"Library\Value.cs", "public class LibraryValue { public static int Value => 1; public static int NewMember => HelperValue.Value + 2; }");
        // A second library repository whose primary checkout is on the ticket branch without code changes.
        var other = await FixtureRepository(git, Path.Combine(root, "gh", "Libraries--Other"));
        await git.Git(other, "checkout", "-qb", "4242");

        // MyMeyer: shape (a) references an unpublished library version; shape (c) a bin DLL by simple name.
        var myMeyer = await FixtureRepository(git, settings.RepositoryRoot, "master");
        string Consumer(string name, string reference) => Project(name).Replace("<Reference Include=\"System\" />", "<Reference Include=\"System\" />" + reference);
        string ShapeA(string version) => $"""
            <Reference Include="Loop.Library, Version={version}.0, Culture=neutral, processorArchitecture=MSIL">
              <HintPath>..\packages\Loop.Library.{version}\lib\net471\Loop.Library.dll</HintPath>
            </Reference>
            """;
        const string shapeC = """<Reference Include="Loop.Library"><HintPath>..\bin\Loop.Library.dll</HintPath></Reference>""";
        Save(myMeyer, ".gitignore", "bin/\nobj/\npackages/\n.cultivate-artifacts/\n");
        Save(myMeyer, "MyMeyer.sln", "");
        Save(myMeyer, @"Consumer\LoopConsumer.csproj", Consumer("LoopConsumer", ShapeA("1.0.0")));
        Save(myMeyer, @"Consumer\App.config", "<configuration />");
        Save(myMeyer, @"Consumer\Value.cs", "public class ConsumerValue { public static int Value => LibraryValue.Value; }");
        Save(myMeyer, @"Plain\LoopPlain.csproj", Consumer("LoopPlain", shapeC));
        Save(myMeyer, @"Plain\App.config", "<configuration />");
        Save(myMeyer, @"Plain\Value.cs", "public class PlainValue { public static int Value => LibraryValue.Value; }");
        await Commit(myMeyer, "consumers on library 1.0.0");
        var ticket = Path.Combine(trees, "MyMeyer", "4242-worktree");
        await git.Git(myMeyer, "worktree", "add", "-q", "-b", "4242", ticket);
        Save(ticket, @"Consumer\LoopConsumer.csproj", Consumer("LoopConsumer", ShapeA("1.5.0")));
        Save(ticket, @"Consumer\Value.cs", "public class ConsumerValue { public static int Value => LibraryValue.NewMember; }");
        Save(ticket, @"Plain\Value.cs", "public class PlainValue { public static int Value => LibraryValue.NewMember + 1; }");
        await Commit(ticket, "use the new library member");
        var target = new Target("4242", ticket, "refs/heads/4242");

        var discovered = new Settings { RepositoryRoot = settings.RepositoryRoot, MSBuild = msbuild, WebfarmRoot = settings.WebfarmRoot, StateRoot = settings.StateRoot };
        await discovered.FillDefaults();
        var none = new Settings { RepositoryRoot = settings.RepositoryRoot, MSBuild = msbuild, WebfarmRoot = settings.WebfarmRoot, StateRoot = settings.StateRoot, LibraryRepositories = [] };
        await none.FillDefaults();
        Check(discovered.LibraryRepositories?.SequenceEqual([Files.Full(loop), Files.Full(other)], Files.Comparer) == true && none.LibraryRepositories?.Count == 0,
            "library repositories default to the Libraries-- clones beside MyMeyer, and a deliberately empty list stays empty");
        Check(LibraryLoop.ParseRepositories("none").Count == 0 && LibraryLoop.ParseRepositories($"{loop}; {other}").Count == 2 &&
            LibraryLoop.RepositoriesProblem("relative") is not null && LibraryLoop.RepositoriesProblem(root) is not null,
            "setup accepts library checkouts or none, and rejects relative paths and folders that are not Git checkouts");
        settings.LibraryRepositories = discovered.LibraryRepositories;

        async Task<List<LibraryChange>> Detect(Target selected, params string[] requested)
        {
            var errors = new List<string>();
            var found = await new LibraryLoop(settings, git).Detect(selected, requested, errors);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
            return found;
        }
        var matches = await Detect(target);
        Check(matches.Select(library => (library.Repository, library.Branch, library.SameBranch)).SequenceEqual(
                [("Libraries--Loop", "4242", true), ("Libraries--Other", "4242", true), ("Libraries--Loop", "4242-2", false)]) &&
            Files.Comparer.Equals(matches[1].Worktree, Files.Full(other)) && matches.All(library => !library.Included),
            "a ticket worktree matches same-ticket library worktrees and primary checkouts, the same branch first, none included by default");
        Check((await Detect(target with { Branch = "4242-2", Ref = "refs/heads/4242-2" })).First().Branch == "4242-2",
            "a follow-up branch prefers the library worktree with its own name");
        Check((await Detect(target with { Branch = "feature-x", Ref = "refs/heads/feature-x" })).Select(library => library.Branch).SequenceEqual(["feature-x"]) &&
            (await Detect(target with { Branch = "unmatched", Ref = "refs/heads/unmatched" })).Count == 0,
            "a non-ticket branch matches only the same branch name, and an unmatched branch has no library");
        var head = await git.Git(ticket, "rev-parse", "HEAD");
        Check((await Detect(target with { Ref = head })).Count == 0 && (await Detect(target with { Worktree = null })).Count == 0,
            "detached worktrees and committed branches get no library");
        await Reject(() => Detect(target with { Worktree = null }, loopTicket), "asking for a library with a committed branch is refused");
        await Reject(() => Detect(target, loopFeature), "a library worktree on another branch cannot be included");
        await Reject(() => Detect(target, loopTicket, loopFollowUp), "two worktrees of one library repository cannot both be included");
        Check((await Detect(target, LibraryLoop.Every)).Where(library => library.Included).Select(library => library.Worktree)
            .SequenceEqual([Files.Full(loopTicket), Files.Full(other)], Files.Comparer), "--libraries includes the preferred worktree of every matching repository");

        var planner = new Planner(settings, git, bridge);
        var unconfigured = await new Planner(new Settings
        {
            RepositoryRoot = settings.RepositoryRoot, WebfarmRoot = settings.WebfarmRoot, StateRoot = settings.StateRoot, MSBuild = msbuild, LibraryRepositories = []
        }, git, bridge).Create(target);
        var without = await planner.Create(target);
        Check(without.Fingerprint == (await git.Inspect(target)).Fingerprint && without.Fingerprint == unconfigured.Fingerprint &&
            without.BuildFingerprint == unconfigured.BuildFingerprint && without.RuntimePaths().SetEquals(unconfigured.RuntimePaths()),
            "detected but left-out libraries leave the plan's fingerprints and runtime files exactly as without the feature");
        Check(without.Libraries.Count == 3 && without.Notes.Any(note => note.Contains("left out")) &&
            without.Projects.Single(project => project.Assembly == "LoopConsumer").PackageDependencies.Count == 1,
            "a left-out library is shown and the changed package reference keeps its usual package dependency");
        var plan = await planner.Create(target, libraries: [loopTicket]);
        var library = plan.Libraries.Single(change => change.Included);
        Check(plan.CanDeploy && plan.Fingerprint != without.Fingerprint && plan.BuildFingerprint != without.BuildFingerprint &&
            plan.RuntimePaths().SetEquals([@"bin\LoopConsumer.dll", @"bin\LoopPlain.dll", @"bin\Loop.Library.dll"]) &&
            plan.Projects.All(project => project.PackageDependencies.Count == 0),
            "an included library is built and deployed instead of a package, and its changes join both fingerprints");
        var deployable = library.Projects.Single(project => project.Reason is null);
        Check(deployable is { Assembly: "Loop.Library", TargetFramework: "net471", Version: "1.1.0", BaselineVersion: "1.0.0", WebfarmVersion: "1.0.0.0" },
            "the preview shows the library DLL, its framework, the version it builds, and the webfarm's version");
        Check(library.Projects.Single(project => project.Reason is not null) is { Assembly: "Loop.Extra" } extra && extra.Reason!.Contains(@"no bin\Loop.Extra.dll") &&
            library.Projects.All(project => project.Assembly != "Loop.Helper"),
            "a changed project the webfarm does not have is explained, and an unchanged project is not listed");
        Check(library.Warnings.Count == 1 && library.Warnings[0].Contains("+Loop.Helper (project)"),
            "a changed project reference warns that the webfarm may lack that DLL, and an older webfarm version does not warn");
        File.Copy(farmBuilds["1.2.0"], farmLibrary, true);
        var newerFarm = await planner.Create(target, libraries: [loopTicket]);
        File.Copy(farmBuilds["1.0.0"], farmLibrary, true);
        Check(newerFarm.CanDeploy && newerFarm.Libraries.Single(change => change.Included).Warnings.Any(warning => warning.Contains("newer than 1.0.0")),
            "a webfarm version newer than the library branch's starting version warns without blocking");

        var loopListing = await git.Git(loop, "worktree", "list", "--porcelain");
        var loopStatus = await git.Git(loopTicket, "status", "--porcelain");
        var progress = new List<string>();
        var state = await pipeline.Activate(plan, progress.Add, watch: false);
        var run = Path.GetDirectoryName(state.WorkspaceRoot)!;
        try
        {
            Check(state.Updated.Select(file => (file.RelativePath, file.Change)).SequenceEqual(
                    [(@"bin\Loop.Library.dll", UpdatedFile.Library), (@"bin\LoopConsumer.dll", UpdatedFile.Built), (@"bin\LoopPlain.dll", UpdatedFile.Built)]),
                "activation deploys the library DLL with its own label beside the MyMeyer DLLs");
            Check(System.Reflection.AssemblyName.GetAssemblyName(farmLibrary).Version?.ToString() == "1.1.0.0" && Properties(farmLibrary).Contains("NewMember"),
                "the deployed library is the worktree's saved, uncommitted code built from a snapshot");
            Check(References(Path.Combine(settings.WebfarmRoot, "bin", "LoopConsumer.dll"))["Loop.Library"] == "1.1.0.0" &&
                References(Path.Combine(settings.WebfarmRoot, "bin", "LoopPlain.dll"))["Loop.Library"] == "1.1.0.0" &&
                File.ReadAllText(Path.Combine(run, "build.log")).Contains("Loop.Library compiles against the library build"),
                "MyMeyer projects compile against the library build through a versioned package reference and a simple bin reference");
            Check(File.ReadAllText(farmHelper) == "farm helper" && progress.Contains("Building library Loop.Library for net471"),
                "a referenced library project is built as needed but not deployed");
            Check(!File.ReadAllText(Path.Combine(run, "restore.log")).Contains("RestorePackagesConfig") &&
                !File.ReadAllText(Path.Combine(run, "build.log")).Contains(".nupkg", StringComparison.OrdinalIgnoreCase),
                "the unpublished package folder is never restored and the library build creates no package");
            Check(await git.Git(loop, "worktree", "list", "--porcelain") == loopListing && await git.Git(loopTicket, "status", "--porcelain") == loopStatus &&
                !Directory.Exists(Path.Combine(loopTicket, "Library", "bin")) && !Directory.Exists(Path.Combine(loopTicket, "Library", "obj")),
                "the library checkout is left untouched, with no build output and no leftover snapshot worktree");
            Check(state.Libraries.Single() is { } source && Files.Comparer.Equals(source.Worktree, Files.Full(loopTicket)) && source.Ref == "refs/heads/4242" &&
                LibraryLoop.Kept(state, target).Count == 1 && LibraryLoop.Kept(state, target with { Ref = "refs/heads/other" }).Count == 0,
                "the journal keeps the library choice, and previewing the active version again keeps it");
            progress.Clear();
            state = await pipeline.Activate(await planner.Create(target, libraries: LibraryLoop.Kept(state, target)), progress.Add, watch: false, workerUpdate: true);
            Check(progress.Any(message => message.StartsWith("Frontend-only update")) && !progress.Any(message => message.StartsWith("Building library")) &&
                state.Updated.Any(file => file.RelativePath == @"bin\Loop.Library.dll" && file.Change == UpdatedFile.Reused),
                "an unchanged library build is reused from the verified cache");
            await bridge.Call("Restore");
            Check(Files.Hash(farmLibrary) == originalLibrary && !File.Exists(Path.Combine(settings.WebfarmRoot, "bin", "LoopConsumer.dll")),
                "restore returns the webfarm's original library DLL");

            var stale = await planner.Create(target, libraries: [loopTicket]);
            var saved = Path.Combine(loopTicket, @"Library\Value.cs");
            var content = File.ReadAllText(saved);
            File.AppendAllText(saved, "\n// saved after the preview");
            try
            {
                await Reject(() => pipeline.Activate(stale, _ => { }, watch: false), "a library file saved after the preview stops the activation");
            }
            finally { File.WriteAllText(saved, content); }
            Check(Files.Hash(farmLibrary) == originalLibrary && (await bridge.Status())?.Active != true,
                "a stopped library activation changes nothing on the webfarm");
        }
        finally
        {
            if ((await bridge.Status())?.Active == true) await bridge.Call("Restore");
        }

        System.Diagnostics.Process? worker = null;
        try
        {
            var watched = await pipeline.Activate(await planner.Create(target, libraries: [loopTicket]), _ => { }, watch: true);
            worker = System.Diagnostics.Process.GetProcessById((await bridge.Status())!.WatcherPid);
            Save(loopTicket, @"Library\Value.cs",
                "public class LibraryValue { public static int Value => 1; public static int NewMember => HelperValue.Value + 2; public static int Later => 3; }");
            DeploymentState? updated = null;
            for (var deadline = DateTime.UtcNow.AddSeconds(90); DateTime.UtcNow < deadline; await Task.Delay(500))
            {
                updated = await bridge.Status();
                if (updated?.Active == true && updated.Fingerprint != watched.Fingerprint) break;
            }
            Check(updated?.Active == true && updated.Fingerprint != watched.Fingerprint && Properties(farmLibrary).Contains("Later") &&
                updated.Libraries.Count == 1, "saving a library file makes the worker rebuild and redeploy the library and keep it in the loop");
        }
        finally
        {
            await bridge.Call("Restore");
            if (worker is not null)
            {
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                worker.Dispose();
            }
            await git.Git(myMeyer, "worktree", "remove", "--force", ticket);
        }
        Check(Files.Hash(farmLibrary) == originalLibrary, "restore after automatic library updates returns the original library DLL");
    }

    private static HashSet<string> Properties(string assembly)
    {
        using var reader = new PEReader(new MemoryStream(File.ReadAllBytes(assembly)));
        var metadata = reader.GetMetadataReader();
        return metadata.PropertyDefinitions.Select(handle => metadata.GetString(metadata.GetPropertyDefinition(handle).Name)).ToHashSet();
    }

    private static Dictionary<string, string> References(string assembly)
    {
        using var reader = new PEReader(new MemoryStream(File.ReadAllBytes(assembly)));
        var metadata = reader.GetMetadataReader();
        return metadata.AssemblyReferences.Select(metadata.GetAssemblyReference)
            .ToDictionary(reference => metadata.GetString(reference.Name), reference => reference.Version.ToString());
    }
}
