using System.Reflection;
using System.Text.Json;

namespace Cultivate;

internal sealed class Pipeline(Settings settings, GitRepository git, Bridge bridge)
{
    public async Task<DeploymentState> Activate(Plan plan, Action<string> progress, bool watch = true, bool workerUpdate = false,
        CancellationToken cancel = default)
    {
        var run = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        string? snapshot = null;
        var stage = "Deployment preflight";
        try
        {
            if (!plan.CanDeploy) throw new InvalidOperationException(string.Join("\n", plan.Errors));
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Capture.targets")))
                throw new FileNotFoundException("Cultivate's build capture target is missing. Reinstall Cultivate.");
            await bridge.Call("Check");
            var previous = await bridge.Status();
            Directory.CreateDirectory(run);
            Files.WriteJson(Path.Combine(run, "plan.json"), plan);
            stage = "Snapshot";
            progress("Snapshotting the selected version (your checkout stays untouched)");
            snapshot = await git.Snapshot(plan, run);
            var files = plan.Frontend.Select(x => x with { Source = Files.Child(snapshot, x.Source) }).ToList();
            var runtimeFiles = new List<RuntimeFile>();
            var runtimePaths = plan.RuntimePaths();
            var reuse = workerUpdate && previous?.Active == true && previous.BuildFingerprint == plan.BuildFingerprint &&
                previous.RuntimeFiles.Count == runtimePaths.Count &&
                previous.RuntimeFiles.All(file => runtimePaths.Contains(file.RelativePath)) &&
                previous.SourceRef == plan.Target.Ref && Files.Comparer.Equals(previous.SourceRoot, plan.Target.Worktree ?? "");
            if (reuse)
            {
                stage = "Runtime cache validation";
                progress("Frontend-only update: keeping the active project DLLs");
                runtimeFiles.AddRange(previous!.RuntimeFiles);
                foreach (var artifact in runtimeFiles)
                {
                    if (!File.Exists(artifact.Source) || Files.Hash(artifact.Source) != artifact.Hash)
                        throw new InvalidDataException("A verified runtime cache file changed. Activate this version again to rebuild it.");
                    var destination = Files.Child(snapshot, Path.Combine(".cultivate-artifacts", artifact.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(artifact.Source, destination, true);
                    files.Add(new(destination, artifact.RelativePath));
                }
            }
            else if (runtimePaths.Count != 0)
            {
                stage = "Build";
                files.AddRange(await Build(plan, snapshot, run, progress));
                stage = "Artifact verification";
                foreach (var artifact in files.Where(x => x.RelativePath.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)))
                {
                    var cached = Files.Child(Path.Combine(run, "verified"), artifact.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    File.Copy(artifact.Source, cached, true);
                    runtimeFiles.Add(new(cached, artifact.RelativePath, Files.Hash(cached)));
                }
            }
            stage = "Source validation";
            if (await LibraryLoop.Fingerprint(git, plan.Target, LibraryLoop.Sources(plan)) != plan.Fingerprint)
                throw new InvalidOperationException("The selected source changed during the build. Nothing was deployed. Preview again.");
            // Cancelling stops only before the journaled deploy starts; once it starts, the activation always finishes.
            cancel.ThrowIfCancellationRequested();
            stage = "Deploy";
            progress("Deploying the verified set; journaling originals for restoration");
            var result = await bridge.Call("Deploy", new
            {
                Snapshot = snapshot, SourceRoot = plan.Target.Worktree ?? "", SourceRef = plan.Target.Ref,
                plan.Target.Branch, plan.Fingerprint, ExpectedToken = previous?.Token ?? "",
                WatchEnabled = watch || workerUpdate,
                plan.BuildFingerprint, plan.ExcludedPaths, RuntimeFiles = runtimeFiles, WorkerUpdate = workerUpdate,
                Libraries = LibraryLoop.Sources(plan),
                Files = files.Select(x => new { x.Source, x.RelativePath, x.Delete, Hash = x.Delete ? null : Files.Hash(x.Source) }).ToList()
            });
            var state = result.Deserialize<DeploymentState>(Files.Json) ?? throw new InvalidDataException("Deployment did not return its state.");
            var deployed = new HashSet<string>(files.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase);
            var projectFiles = plan.Projects.Select(project => @"bin\" + project.Assembly + ".dll").ToHashSet(Files.Comparer);
            var libraryFiles = LibraryLoop.Assemblies(plan).Select(assembly => @"bin\" + assembly + ".dll").ToHashSet(Files.Comparer);
            state.Updated = files.Select(file => new UpdatedFile(file.RelativePath,
                    file.Delete ? UpdatedFile.Deleted :
                    !runtimeFiles.Any(x => Files.Comparer.Equals(x.RelativePath, file.RelativePath)) ? UpdatedFile.Synced :
                    libraryFiles.Contains(file.RelativePath) ? reuse ? UpdatedFile.Reused : UpdatedFile.Library :
                    !projectFiles.Contains(file.RelativePath) ? UpdatedFile.Package :
                    reuse ? UpdatedFile.Reused : UpdatedFile.Built))
                // The bridge restores the previous version's files that this one no longer deploys, unless they changed outside Cultivate.
                .Concat((previous?.Active == true ? previous.Files : []).Where(file => !deployed.Contains(file.RelativePath))
                    .Select(file => new UpdatedFile(file.RelativePath,
                        state.Kept.Contains(file.RelativePath, Files.Comparer) ? UpdatedFile.Kept : UpdatedFile.Restored)))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            if (watch)
            {
                stage = "Automatic update startup";
                progress("Starting automatic saved-file updates");
                await Watcher.Start(settings, bridge, state.Token);
            }
            return state;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or System.Xml.XmlException or JsonException or TimeoutException)
        {
            Diagnostics.Attach(exception, new(stage)
            {
                Branch = plan.Target.Branch, Worktree = plan.Target.Worktree, Snapshot = snapshot, RunDirectory = run,
                Log = Path.Combine(run, "build.log")
            });
            throw;
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot))
                await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", snapshot);
        }
    }

    /// <param name="support">For a release: every project to build in dependency order, including the unchanged projects the plan's projects
    /// compile against, and a list that records each reference taken from outside the snapshot. Null builds only the plan's projects.</param>
    internal async Task<List<DeployFile>> Build(Plan plan, string snapshot, string run, Action<string> progress, SupportBuild? support = null)
    {
        var context = new FailureContext("Reference preparation");
        try
        {
            progress(support is null ? "Preparing references for changed projects only"
                : "Preparing references for the release projects and the unchanged projects they compile against");
            // Each project compiles against this run's built outputs (references\built), then its own resolved DLLs
            // (references\projects\<assembly>), then its HintPaths; never against another project's references.
            var references = Path.Combine(run, "references");
            var built = Path.Combine(references, "built");
            Directory.CreateDirectory(built);
            var projects = support?.Order ?? plan.Projects;
            var ledger = new Ledger(snapshot, projects, support is null ? null : Pristine(snapshot, projects));
            var common = new Dictionary<string, string>
            {
                ["WebfarmPath"] = Path.Combine(run, "intercepted") + "\\",
                ["CultivateRunDirectory"] = run,
                ["CultivateCaptureTargets"] = Path.Combine(AppContext.BaseDirectory, "Capture.targets")
            };
            context = new("Library build") { Log = Path.Combine(run, "build.log") };
            var libraryFiles = await new LibraryLoop(settings, git).Build(plan, snapshot, run, built, progress);
            // Capture.targets points every reference to a built library assembly at that build, whatever version the project declares.
            if (libraryFiles.Count != 0) common["CultivateLibraryDirectory"] = Path.Combine(run, "libraries") + "\\";
            var libraryNames = LibraryLoop.Assemblies(plan).ToHashSet(Files.Comparer);
            var internalNames = projects.Select(x => x.Assembly).Concat(libraryNames).ToHashSet(Files.Comparer);
            foreach (var project in projects)
            {
                context = new("Reference preparation") { Project = project.RelativePath, Log = Path.Combine(run, "build.log") };
                var own = Path.Combine(references, "projects", project.Assembly);
                Directory.CreateDirectory(own);
                var environment = new Dictionary<string, string>(common) { ["ReferencePath"] = built + ";" + own };
                await Prepare(project, snapshot, plan.Target.Worktree ?? settings.RepositoryRoot, own, internalNames, libraryNames, environment, run,
                    ledger, support?.Outside);
                context = context with { Stage = "Project evaluation" };
                var full = Files.Child(snapshot, project.RelativePath);
                var metadataText = await Commands.Run(settings.MSBuild,
                    [full, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath,_CultivateCaptureReady",
                     "-getItem:Compile,EmbeddedResource,ProjectReference"], snapshot, environment, Path.Combine(run, "build.log"), context);
                using var metadata = JsonDocument.Parse(metadataText);
                var properties = metadata.RootElement.GetProperty("Properties");
                if (properties.GetProperty("_CultivateCaptureReady").GetString() != "true")
                    throw new InvalidOperationException("Cultivate's per-user MSBuild hook was not imported. Run install.ps1 again, then check with `cultivate doctor`.");
                var targetPath = properties.GetProperty("TargetPath").GetString() ?? throw new InvalidDataException("Missing TargetPath.");
                Files.Child(snapshot, Path.GetRelativePath(snapshot, targetPath));
                var declared = metadata.RootElement.GetProperty("Items").GetProperty("ProjectReference");
                foreach (var reference in declared.EnumerateArray())
                {
                    if (reference.TryGetProperty("ReferenceOutputAssembly", out var outputAssembly) &&
                        outputAssembly.GetString()?.Equals("false", StringComparison.OrdinalIgnoreCase) == true) continue;
                    var relative = Path.GetRelativePath(snapshot, reference.GetProperty("FullPath").GetString()!);
                    if (projects.Any(x => Files.Comparer.Equals(x.RelativePath, relative))) continue;
                    Files.Child(snapshot, relative);
                    var referenceMetadata = await Commands.Run(settings.MSBuild,
                        [Files.Child(snapshot, relative), "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"],
                        snapshot, environment, Path.Combine(run, "build.log"), new("Dependency evaluation") { Project = relative });
                    var existingTarget = Files.Full(referenceMetadata.Trim());
                    var outputRelative = Path.GetRelativePath(snapshot, existingTarget);
                    Files.Child(snapshot, outputRelative);
                    if (File.Exists(existingTarget)) continue;
                    var source = Files.Child(plan.Target.Worktree ?? settings.RepositoryRoot, outputRelative);
                    if (!File.Exists(source)) source = Files.Child(settings.RepositoryRoot, outputRelative);
                    if (!File.Exists(source))
                        throw new FileNotFoundException($"An existing compiled DLL for unchanged project {relative} is required. Build that dependency separately; Cultivate does not automatically rebuild unchanged projects.", source);
                    Directory.CreateDirectory(Path.GetDirectoryName(existingTarget)!);
                    File.Copy(source, existingTarget);
                    support?.Outside.Add(OutsideReference.Of(source, project.RelativePath));
                }
                context = context with { Stage = "Build input validation" };
                var evaluatedInputs = new HashSet<string>(Files.Comparer);
                foreach (var itemType in new[] { "Compile", "EmbeddedResource" })
                    foreach (var item in metadata.RootElement.GetProperty("Items").GetProperty(itemType).EnumerateArray())
                    {
                        var relative = Path.GetRelativePath(snapshot, item.GetProperty("FullPath").GetString()!);
                        Files.Child(snapshot, relative);
                        evaluatedInputs.Add(relative);
                    }
                foreach (var change in plan.Changes.Where(x => project.Owns(x.Path) && new[] { ".cs", ".resx" }.Contains(Path.GetExtension(x.Path), Files.Comparer)))
                    if (!evaluatedInputs.Contains(change.Path.Replace('/', '\\')))
                        throw new InvalidOperationException($"MSBuild does not compile {change.Path} in Debug. Deployment stopped.");
                var position = $"{projects.IndexOf(project) + 1}/{projects.Count}";
                progress(support is null ? $"Building {project.Assembly} ({position})"
                    : plan.Projects.Any(x => Files.Comparer.Equals(x.RelativePath, project.RelativePath)) ? $"Building {project.Assembly} ({position}, release file)"
                    : $"Building {project.Assembly} ({position}, dependency: needed to compile, not released)");
                context = context with { Stage = "Build", BinaryLog = Path.Combine(run, project.Assembly + ".binlog") };
                var arguments = new List<string>
                {
                    full, "-nologo", "-t:Build", "-p:Configuration=Debug", "-p:BuildProjectReferences=false",
                    "-m:1", "-v:minimal",
                    $"-bl:{Path.Combine(run, project.Assembly + ".binlog")}"
                };
                await Commands.Run(settings.MSBuild, arguments, snapshot, environment, Path.Combine(run, "build.log"), context);
                context = context with { Stage = "Artifact verification" };
                if (!File.Exists(targetPath)) throw new FileNotFoundException("Build succeeded without its expected assembly.", targetPath);
                var identity = AssemblyName.GetAssemblyName(targetPath);
                if (!Files.Comparer.Equals(identity.Name, project.Assembly))
                    throw new InvalidDataException($"Unexpected assembly identity for {targetPath}.");
                LibraryLoop.VerifyReferences(targetPath, plan);
                File.Copy(targetPath, Path.Combine(built, Path.GetFileName(targetPath)), true);
                ledger.Built(project, Path.GetDirectoryName(targetPath)!);
                // Projects can share an output folder, so keep each changed package's DLL before a later build replaces it.
                foreach (var hint in project.PackageDependencies)
                {
                    var name = Path.GetFileName(hint);
                    var copied = Path.Combine(Path.GetDirectoryName(targetPath)!, name);
                    project.HintVersions.TryGetValue(hint, out var packageVersion);
                    var version = VersionOf(copied);
                    if (version is null || packageVersion is not null && version != packageVersion ||
                        !Files.Comparer.Equals(AssemblyName.GetAssemblyName(copied).Name, Path.GetFileNameWithoutExtension(name)))
                        throw new InvalidDataException($"The build did not copy {name} {packageVersion} from {project.Assembly}'s changed package reference {hint}.");
                    var captured = Path.Combine(run, "dependencies", project.Assembly, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(captured)!);
                    File.Copy(copied, captured, true);
                }
            }
            context = new("Artifact verification") { Log = Path.Combine(run, "build.log") };
            progress(support is null ? "Verifying only the changed projects' own DLLs and changed packages"
                : "Verifying the release projects' DLLs and changed packages; dependency builds are not released");
            var collected = Artifacts.Collect(run, snapshot, plan.Projects);
            if (libraryFiles.FirstOrDefault(library => collected.Any(file => Files.Comparer.Equals(file.RelativePath, library.RelativePath))) is { } duplicate)
                throw new InvalidDataException($"A library build and a changed MyMeyer project both produce {duplicate.RelativePath}.");
            return collected.Concat(libraryFiles).ToList();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Attach(exception, context with { RunDirectory = run, Snapshot = snapshot, Branch = plan.Target.Branch, Worktree = plan.Target.Worktree });
            throw;
        }
    }

    /// <summary>A reference DLL a project compiles against, as <see cref="Prepare"/> resolved it.</summary>
    /// <param name="Source">Where it was found; this is what <c>build.log</c> and the outside references name.</param>
    /// <param name="Copy">The same file where no later build overwrites it: the project's own reference folder, or the package.</param>
    /// <param name="FromSnapshot">The snapshot's own copy (see <see cref="Ledger.FromSnapshot"/>). Only a release tells.</param>
    /// <param name="Outside">Taken from outside the snapshot.</param>
    private sealed record Resolution(ProjectInfo Project, string Name, string Source, string Copy, string Hash, bool FromSnapshot, bool Outside);

    /// <summary>What one build knows about the references its projects compile against.</summary>
    /// <param name="pristine">For a release: see <see cref="Pristine"/>. Null for the dev farm, which keeps its lookups.</param>
    private sealed class Ledger(string snapshot, List<ProjectInfo> projects, Dictionary<string, string>? pristine)
    {
        private readonly string packages = Path.Combine(Files.Full(snapshot), "packages") + "\\";
        /// <summary>A build's copy of a resolved reference in its output folder, by full path, and the resolution it copied.</summary>
        private readonly Dictionary<string, Resolution> produced = new(Files.Comparer);

        /// <summary>Each prepared project's resolved references by file name, including the HintPaths found where they point.</summary>
        public Dictionary<ProjectInfo, Dictionary<string, Resolution>> Resolved { get; } = [];
        public bool Release => pristine is not null;

        /// <summary>A file Git tracks in the snapshot, unchanged since the snapshot was taken.</summary>
        public bool Tracked(string path) => pristine?.GetValueOrDefault(path) is { } hash && File.Exists(path) && Files.Hash(path) == hash;

        /// <summary>
        /// Whether a file is the snapshot's own: in its packages folder, tracked by Git and unchanged, or a build's copy of a reference that was
        /// itself the snapshot's own. A DLL a build copied in from outside the snapshot is not, though it now sits in the snapshot's bin.
        /// </summary>
        public bool FromSnapshot(string path, string hash) => path.StartsWith(packages, StringComparison.OrdinalIgnoreCase) ||
            pristine?.GetValueOrDefault(path) == hash || produced.TryGetValue(path, out var origin) && origin.Hash == hash && origin.FromSnapshot;

        /// <summary>Records the copies <paramref name="project"/>'s build made of its resolved references in its <paramref name="output"/> folder.</summary>
        public void Built(ProjectInfo project, string output)
        {
            foreach (var resolution in Resolved[project].Values)
            {
                var copied = Path.GetFullPath(Path.Combine(output, resolution.Name));
                if (File.Exists(copied) && new FileInfo(copied).Length == new FileInfo(resolution.Copy).Length && Files.Hash(copied) == resolution.Hash)
                    produced[copied] = resolution;
            }
        }

        /// <summary>What the projects prepared before <paramref name="project"/> resolved for <paramref name="name"/>: the projects it depends on
        /// (by ProjectReference or by assembly name), nearest first, then the others in build order.</summary>
        public List<(Resolution Resolution, bool Dependency)> Earlier(ProjectInfo project, string name)
        {
            var order = new List<ProjectInfo>();
            var queue = new Queue<ProjectInfo>([project]);
            while (queue.TryDequeue(out var current))
                foreach (var next in projects.Where(other => other != project && !order.Contains(other) &&
                    (current.ProjectReferences.Contains(other.RelativePath, Files.Comparer) || current.AssemblyReferences.Contains(other.Assembly, Files.Comparer))))
                {
                    order.Add(next);
                    queue.Enqueue(next);
                }
            var dependencies = order.Count;
            order.AddRange(projects.Where(other => other != project && !order.Contains(other)));
            return order.Select((other, index) => (Resolution: Resolved.GetValueOrDefault(other)?.GetValueOrDefault(name), Dependency: index < dependencies))
                .Where(match => match.Resolution is not null).Select(match => (match.Resolution!, match.Dependency)).ToList();
        }
    }

    /// <summary>
    /// For a release: each snapshot file a project's HintPath or the tracked-bin lookup can name, with its hash before anything is built, when the
    /// snapshot holds only what Git tracks. Builds later copy their references into the snapshot's bin, beside or over these.
    /// </summary>
    private static Dictionary<string, string> Pristine(string snapshot, List<ProjectInfo> projects)
    {
        var root = Files.Full(snapshot) + "\\";
        return projects.SelectMany(project => project.Hints.Where(hint => !hint.Contains('$')).SelectMany(hint => new[]
            {
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Files.Child(snapshot, project.RelativePath))!, hint)),
                Path.GetFullPath(Path.Combine(snapshot, "bin", Path.GetFileName(hint)))
            }))
            .Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)).Distinct(Files.Comparer)
            .ToDictionary(path => path, Files.Hash, Files.Comparer);
    }

    /// <param name="own">This project's reference folder, which only its own build searches.</param>
    /// <param name="ledger">What earlier projects in this build resolved. For a release it also tells the snapshot's own files from copies of
    /// outside DLLs, so the tracked bin and the projects this one depends on are tried before the developer's checkout.</param>
    /// <param name="outside">For a release: records each reference taken from outside the snapshot. Null for the dev farm.</param>
    private async Task Prepare(ProjectInfo project, string snapshot, string sourceRoot, string own, HashSet<string> internalNames,
        HashSet<string> libraryNames, Dictionary<string, string> environment, string run, Ledger ledger, List<OutsideReference>? outside = null)
    {
        var directory = Path.GetDirectoryName(Files.Child(snapshot, project.RelativePath))!;
        var originalDirectory = Path.GetDirectoryName(Files.Child(sourceRoot, project.RelativePath))!;
        var restoreNeeded = false;
        foreach (var relative in project.Hints.Concat(project.Imports))
        {
            if (relative.Contains('$')) continue;
            // A library this run builds replaces its package, whose version may not be published yet.
            if (project.Hints.Contains(relative) && libraryNames.Contains(Path.GetFileNameWithoutExtension(relative))) continue;
            var destination = Path.GetFullPath(Path.Combine(directory, relative));
            var packageRoot = Path.Combine(snapshot, "packages") + "\\";
            if (!destination.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var package = Path.GetRelativePath(Path.Combine(snapshot, "packages"), destination).Split('\\')[0];
            var source = Path.Combine(sourceRoot, "packages", package);
            if (!Directory.Exists(source)) source = Path.Combine(settings.RepositoryRoot, "packages", package);
            if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(snapshot, "packages", package));
            else if (!File.Exists(destination)) restoreNeeded = true;
        }
        if (restoreNeeded)
        {
            await Commands.Run(settings.MSBuild,
                [Files.Child(snapshot, project.RelativePath), "-nologo", "-t:Restore", "-p:Configuration=Debug",
                 "-p:RestorePackagesConfig=true", $"-p:SolutionDir={snapshot}\\", "-v:minimal",
                 $"-bl:{Path.Combine(run, project.Assembly + ".restore.binlog")}"],
                snapshot, environment, Path.Combine(run, "restore.log"), new("Package restore")
                {
                    Project = project.RelativePath, BinaryLog = Path.Combine(run, project.Assembly + ".restore.binlog")
                });
        }
        var log = Path.Combine(run, "build.log");
        var resolved = ledger.Resolved[project] = new(Files.Comparer);
        foreach (var hint in project.Hints)
        {
            if (hint.Contains('$')) continue;
            var name = Path.GetFileName(hint);
            if (internalNames.Contains(Path.GetFileNameWithoutExtension(name))) continue;
            var desired = Path.GetFullPath(Path.Combine(directory, hint));
            var package = desired.StartsWith(Path.Combine(snapshot, "packages") + "\\", StringComparison.OrdinalIgnoreCase);
            if (package && File.Exists(desired))
            {
                resolved.TryAdd(name, new(project, name, desired, desired, Files.Hash(desired), true, false));
                continue;
            }
            // A HintPath can name a package version packages.config never restores. Visual Studio then quietly falls back
            // to the checkout's bin folder; accept that copy only when it is exactly the version the Reference declares.
            project.HintVersions.TryGetValue(hint, out var declared);
            bool Usable(string path) => File.Exists(path) && (!package || declared is not null && VersionOf(path) == declared);
            // MyMeyer tracks its third-party DLLs in bin, so a release looks for them in its own snapshot first.
            var snapshotBin = Path.GetFullPath(Path.Combine(snapshot, "bin", name));
            var source = !package && File.Exists(desired) ? desired : ledger.Tracked(snapshotBin) && Usable(snapshotBin) ? snapshotBin : null;
            var earlier = source is null ? ledger.Earlier(project, name).Where(match => Usable(match.Resolution.Copy)).ToList() : [];
            Resolution? borrowed = null;
            var dependency = false;
            // Then a release compiles against the snapshot's own copy that a project it depends on resolved, as master's own build of that
            // project does, before any DLL from outside the snapshot.
            if (ledger.Release) (borrowed, dependency) = earlier.FirstOrDefault(match => match.Resolution.FromSnapshot);
            string[] fallbacks = package ? [Path.Combine(sourceRoot, "bin", name), Path.Combine(settings.RepositoryRoot, "bin", name)] :
            [
                Path.GetFullPath(Path.Combine(originalDirectory, hint)),
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Files.Child(settings.RepositoryRoot, project.RelativePath))!, hint)),
                Path.Combine(sourceRoot, "bin", name), Path.Combine(settings.RepositoryRoot, "bin", name)
            ];
            if (source is null && borrowed is null) source = fallbacks.FirstOrDefault(Usable);
            // A last resort only: the copy an earlier project in this build resolved, which one shared reference folder used to supply.
            var lastResort = source is null && borrowed is null && earlier.Count != 0;
            if (lastResort) (borrowed, dependency) = earlier[0];
            var from = borrowed?.Copy ?? source;
            if (from is null)
            {
                // Visual Studio builds with such a reference unresolved and fails only if the code uses it; MSBuild does the same here.
                File.AppendAllText(log, $"Unresolved reference {name} for {project.Assembly}: {hint} was not found.\n");
                continue;
            }
            var copy = Path.Combine(own, name);
            var hash = Files.Hash(from);
            if (File.Exists(copy) && Files.Hash(copy) != hash)
                throw new InvalidOperationException($"Incompatible input DLLs named {name}: two of {project.RelativePath}'s references resolve to different files. " +
                    "Resolve references before deploying.");
            File.Copy(from, copy, true);
            var origin = borrowed?.Source ?? from;
            var isOutside = borrowed?.Outside ?? !origin.StartsWith(Files.Full(snapshot) + "\\", StringComparison.OrdinalIgnoreCase);
            resolved.TryAdd(name, new(project, name, origin, copy, hash, borrowed?.FromSnapshot ?? ledger.FromSnapshot(origin, hash), isOutside));
            File.AppendAllText(log, borrowed is null ? $"Reference {name}: {origin}\n"
                : lastResort ? $"Reference {name}: {origin} (last resort: {project.Assembly} cannot resolve its HintPath {hint}, so it uses the copy " +
                    $"{borrowed.Project.Assembly} resolved earlier in this build)\n"
                : $"Reference {name}: {origin} ({borrowed.Project.Assembly}'s copy: {project.Assembly}'s HintPath {hint} is not in the snapshot, and " +
                    (dependency ? $"{project.Assembly} depends on {borrowed.Project.Assembly}, which" : $"{borrowed.Project.Assembly}, built earlier in this release,") +
                    " resolved this copy from the snapshot, so it is used instead of a DLL from outside the snapshot)\n");
            if (isOutside) outside?.Add(OutsideReference.Of(origin, project.RelativePath));
        }
    }

    internal static string? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
        catch (BadImageFormatException) { return null; }
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Dependency cache contains a link: {path}");
            var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyDirectory(path, target);
            else if (!File.Exists(target)) File.Copy(path, target);
        }
    }
}
