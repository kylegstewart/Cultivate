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
            await Explain(exception, plan);
            throw;
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot))
                await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", snapshot);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex HunkHeader =
        new(@"^@@ -[0-9]+(?:,[0-9]+)? \+(?<start>[0-9]+)(?:,(?<count>[0-9]+))? @@", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// After a changed project fails to compile, records on the failure whether this version changed each located error's line (the
    /// baseline compared with the snapshot that was built) and the exclusions that would deploy the rest of the plan without that
    /// project. It runs only on failure and must be called while the snapshot exists; any problem here leaves the original report as it was.
    /// </summary>
    internal async Task Explain(Exception exception, Plan plan)
    {
        try
        {
            var context = Diagnostics.Describe(exception).Context;
            var project = plan.Projects.FirstOrDefault(x => Files.Comparer.Equals(x.RelativePath, context.Project));
            if (context.Stage != "Build" || project is null || context.Snapshot is not { } snapshot || !Directory.Exists(snapshot)) return;
            var messages = new List<string>();
            for (var current = exception; current is not null; current = current.InnerException) messages.Add(current.Message);
            var compileErrors = McpServer.CompileErrors(string.Join("\n", messages), snapshot)
                .Where(error => error.Code.StartsWith("CS", StringComparison.OrdinalIgnoreCase)).ToList();
            if (compileErrors.Count == 0) return;
            var origins = new List<ErrorOrigin>();
            if (plan.Baseline.Length != 0)
            {
                var hunks = new Dictionary<string, List<(int Start, int Count)>?>(Files.Comparer);
                foreach (var (file, line) in compileErrors.Where(error => error.File is not null && error.Line is not null && !Path.IsPathRooted(error.File))
                    .Select(error => (error.File!, error.Line!.Value)).Distinct())
                {
                    var change = plan.Changes.FirstOrDefault(change => Files.Comparer.Equals(change.Path, file.Replace('\\', '/')));
                    if (change is null || plan.ExcludedPaths.Contains(change.Path, Files.Comparer))
                    {
                        origins.Add(new(file, line, false));
                        continue;
                    }
                    if (!hunks.TryGetValue(change.Path, out var ranges))
                    {
                        var literal = ":(literal)" + change.Path;
                        // A file the baseline lacks is new, so every line of it is this version's.
                        ranges = (await git.Git(snapshot, "ls-tree", "--name-only", plan.Baseline, "--", literal)).Length == 0 ? null
                            : HunkHeader.Matches(await git.Git(snapshot, "diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", plan.Baseline, "--", literal))
                                .Select(match => (int.Parse(match.Groups["start"].Value), match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1)).ToList();
                        hunks[change.Path] = ranges;
                    }
                    // A pure deletion touches the lines on either side of it.
                    origins.Add(new(file, line, ranges is null || ranges.Any(range => range.Count == 0
                        ? line == range.Start || line == range.Start + 1 : line >= range.Start && line < range.Start + range.Count)));
                }
            }
            var selected = plan.Changes.Where(change => !plan.ExcludedPaths.Contains(change.Path, Files.Comparer)).ToList();
            var compiled = selected.Where(Planner.IsCompiledInput).ToList();
            var owned = compiled.Where(change => project.Owns(change, compiled)).ToList();
            var others = plan.Projects.Where(other => other != project).ToList();
            // Leaving the project out must not change another project's build or leave nothing to deploy, and a project the baseline
            // lacks has no DLL on the webfarm to fall back on.
            var skippable = owned.Count != 0 && !owned.Any(change => others.Any(other => other.Owns(change, compiled))) &&
                (others.Count != 0 || plan.Frontend.Count != 0 || plan.Libraries.Any(library => library.Included)) && plan.Baseline.Length != 0 &&
                (await git.Git(settings.RepositoryRoot, "ls-tree", "--name-only", plan.Baseline, "--", ":(literal)" + project.RelativePath.Replace('\\', '/'))).Length != 0;
            Diagnostics.Attach(exception, new(context.Stage)
            {
                Origins = origins.Count == 0 ? null : origins,
                BaseBranch = plan.Target.PullRequest?.BaseBranch is { Length: > 0 } baseBranch ? baseBranch : settings.BaseBranch,
                SkipExclusions = skippable ? plan.ExcludedPaths.Concat(owned.Select(change => change.Path)).Distinct(Files.Comparer).Order(Files.Comparer).ToList() : null
            });
        }
        catch (Exception analysis) when (analysis is not OutOfMemoryException) { }
    }

    /// <param name="support">For a release: every project to build in dependency order, including the unchanged projects the plan's projects
    /// compile against, and a list that records each reference taken from outside the snapshot. A project of MyMeyer's release that does not
    /// compile against a snapshot copy is built once more against the newer files its HintPaths name in the developer's checkout, if any; a
    /// second failure reports the first. Null builds only the plan's projects.</param>
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
            var main = support?.Main ?? true;
            var ledger = new Ledger(snapshot, projects, support is null ? null : Pristine(snapshot, projects, main), main);
            var common = new Dictionary<string, string>
            {
                ["WebfarmPath"] = Path.Combine(run, "intercepted") + "\\",
                ["CultivateRunDirectory"] = run,
                ["CultivateCaptureTargets"] = Path.Combine(AppContext.BaseDirectory, "Capture.targets")
            };
            // Another repository's projects have no webfarm copy target to capture after, so their output is captured after Build.
            if (!main) common["CultivateCaptureAfterBuild"] = "true";
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
                    ledger, support?.Outside, main, support?.Unresolved);
                context = context with { Stage = "Project evaluation" };
                var full = Files.Child(snapshot, project.RelativePath);
                var metadataText = await Commands.Run(settings.MSBuild,
                    [full, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath,_CultivateCaptureReady",
                     main ? "-getItem:Compile,EmbeddedResource,ProjectReference" : "-getItem:Compile,EmbeddedResource,ProjectReference,IntermediateAssembly"],
                    snapshot, environment, Path.Combine(run, "build.log"), context);
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
                var started = DateTime.UtcNow;
                try { await Commands.Run(settings.MSBuild, arguments, snapshot, environment, Path.Combine(run, "build.log"), context); }
                catch (InvalidOperationException failure) when (support is not null && Diagnostics.Describe(failure).Context.ExitCode is not (null or 0) &&
                    ledger.Alternates.GetValueOrDefault(project) is { Count: > 0 } alternates)
                {
                    var buildLog = Path.Combine(run, "build.log");
                    var retrying = $"{project.Assembly} did not compile against " + string.Join(", ", alternates.Select(alternate =>
                        $"{(alternate.Borrowed is null ? "the snapshot's" : alternate.Borrowed + "'s copy of")} {alternate.Name} {alternate.Replaced}")) +
                        "; trying your checkout's " + string.Join(", ", alternates.Select(alternate => $"{alternate.Name} {alternate.Version}"));
                    progress(retrying);
                    File.AppendAllText(buildLog, $"\n{retrying}: {string.Join("; ", alternates.Select(alternate => alternate.Path))}\n");
                    // The failed build stopped before copying references to the output folder, and this project's own reference folder comes
                    // first in ReferencePath, so replacing the copies there is what the second build compiles against.
                    var resolutions = ledger.Resolved[project];
                    foreach (var alternate in alternates)
                    {
                        var copy = resolutions[alternate.Name].Copy;
                        File.Copy(alternate.Path, copy, true);
                        // A copy keeps its source's time, which MSBuild's reference cache from the failed build may already hold for the old file.
                        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
                        resolutions[alternate.Name] = resolutions[alternate.Name] with { Source = alternate.Path, Hash = Files.Hash(copy), FromSnapshot = false, Outside = true };
                    }
                    var retryLog = Path.Combine(run, project.Assembly + ".retry.binlog");
                    arguments[^1] = $"-bl:{retryLog}";
                    started = DateTime.UtcNow;
                    try { await Commands.Run(settings.MSBuild, arguments, snapshot, environment, buildLog, context with { BinaryLog = retryLog }); }
                    catch (InvalidOperationException second) when (Diagnostics.Describe(second).Context.ExitCode is not (null or 0))
                    {
                        // The first attempt compiled against references a release can verify, so its errors are the ones to fix.
                        Diagnostics.Attach(failure, context with
                        {
                            Retry = "A second attempt against " + string.Join(", ", alternates.Select(alternate => $"{alternate.Path} ({alternate.Version})")) +
                                $", the newer {(alternates.Count == 1 ? "file" : "files")} its project names, also failed; build.log has both attempts."
                        });
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                    }
                    File.AppendAllText(buildLog, $"{project.Assembly} compiled on the second attempt, against {string.Join("; ", alternates.Select(alternate => alternate.Path))}\n");
                    foreach (var alternate in alternates)
                    {
                        support.Outside.Add(OutsideReference.Of(alternate.Path, project.RelativePath));
                        support.Substituted.Add(new(project.Assembly, alternate.Name, alternate.Path, alternate.Version, File.GetLastWriteTime(alternate.Path),
                            alternate.Replaced, alternate.Borrowed));
                    }
                }
                context = context with { Stage = "Artifact verification" };
                if (!File.Exists(targetPath)) throw new FileNotFoundException("Build succeeded without its expected assembly.", targetPath);
                var identity = AssemblyName.GetAssemblyName(targetPath);
                if (!Files.Comparer.Equals(identity.Name, project.Assembly))
                    throw new InvalidDataException($"Unexpected assembly identity for {targetPath}.");
                // Another repository may track its projects' own outputs; a copy Git checked out must never pass for this build's.
                if (!main)
                {
                    var intermediate = metadata.RootElement.GetProperty("Items").GetProperty("IntermediateAssembly").EnumerateArray()
                        .Select(item => item.GetProperty("FullPath").GetString()).FirstOrDefault();
                    if (intermediate is null || !File.Exists(intermediate) || File.GetLastWriteTimeUtc(intermediate) < started ||
                        File.GetLastWriteTimeUtc(targetPath) < started || Files.Hash(intermediate) != Files.Hash(targetPath))
                        throw new InvalidDataException($"{Path.GetFileName(targetPath)} in {Path.GetDirectoryName(targetPath)} was not written by this build of " +
                            $"{project.RelativePath}, so it may be a copy Git tracks rather than the code being released. Nothing was written to the release folder.");
                }
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
            var collected = Artifacts.Collect(run, snapshot, plan.Projects, main ? null : "Build");
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

    /// <summary>A reference DLL a project compiles against, as <see cref="Prepare"/> resolved it, or as <see cref="Build"/>'s one retry replaced it
    /// with its <see cref="Alternate"/>.</summary>
    /// <param name="Source">Where it was found; this is what <c>build.log</c> and the outside references name.</param>
    /// <param name="Copy">The same file where no later build overwrites it: the project's own reference folder, or the package.</param>
    /// <param name="FromSnapshot">The snapshot's own copy (see <see cref="Ledger.FromSnapshot"/>). Only a release tells.</param>
    /// <param name="Outside">Taken from outside the snapshot.</param>
    private sealed record Resolution(ProjectInfo Project, string Name, string Source, string Copy, string Hash, bool FromSnapshot, bool Outside);

    /// <summary>For a release of MyMeyer: the newer file a HintPath names in the developer's checkout, which replaces the snapshot copy
    /// <see cref="Prepare"/> chose (<see cref="Replaced"/>, its version) only if the project does not compile against that copy.</summary>
    /// <param name="Borrowed">The project whose snapshot copy was chosen, or null for the tracked <c>bin</c> copy.</param>
    private sealed record Alternate(string Name, string Path, string Version, string Replaced, string? Borrowed);

    /// <summary>What one build knows about the references its projects compile against.</summary>
    /// <param name="pristine">For a release: see <see cref="Pristine"/>. Null for the dev farm, which keeps its lookups.</param>
    /// <param name="main">False for a release of another repository, where any packages folder in the snapshot counts.</param>
    private sealed class Ledger(string snapshot, List<ProjectInfo> projects, Dictionary<string, string>? pristine, bool main = true)
    {
        /// <summary>A build's copy of a resolved reference in its output folder, by full path, and the resolution it copied.</summary>
        private readonly Dictionary<string, Resolution> produced = new(Files.Comparer);

        /// <summary>Each prepared project's resolved references by file name, including the HintPaths found where they point.</summary>
        public Dictionary<ProjectInfo, Dictionary<string, Resolution>> Resolved { get; } = [];
        /// <summary>For a release of MyMeyer: each project's references to retry with, should it not compile.</summary>
        public Dictionary<ProjectInfo, List<Alternate>> Alternates { get; } = [];
        public bool Release => pristine is not null;

        /// <summary>A file Git tracks in the snapshot, unchanged since the snapshot was taken.</summary>
        public bool Tracked(string path) => pristine?.GetValueOrDefault(path) is { } hash && File.Exists(path) && Files.Hash(path) == hash;

        /// <summary>
        /// Whether a file is the snapshot's own: in its packages folder, tracked by Git and unchanged, or a build's copy of a reference that was
        /// itself the snapshot's own. A DLL a build copied in from outside the snapshot is not, though it now sits in the snapshot's bin.
        /// </summary>
        public bool FromSnapshot(string path, string hash) => PackagesFolder(snapshot, path, main) is not null ||
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
    private static Dictionary<string, string> Pristine(string snapshot, List<ProjectInfo> projects, bool main)
    {
        var root = Files.Full(snapshot) + "\\";
        return projects.SelectMany(project => project.Hints.Where(hint => !hint.Contains('$')).SelectMany(hint =>
                new[] { Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Files.Child(snapshot, project.RelativePath))!, hint)) }
                    .Concat(SnapshotCopies(snapshot, project, Path.GetFileName(hint), main))))
            .Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)).Distinct(Files.Comparer)
            .ToDictionary(path => path, Files.Hash, Files.Comparer);
    }

    /// <summary>
    /// For a release: where the snapshot may track a reference its HintPath does not reach. MyMeyer tracks its third-party DLLs in its root bin.
    /// Another repository's only such place is the project's own output folder, which MSBuild searches last; none when it is outside the snapshot.
    /// </summary>
    private static IEnumerable<string> SnapshotCopies(string snapshot, ProjectInfo project, string name, bool main)
    {
        if (main) return [Path.GetFullPath(Path.Combine(snapshot, "bin", name))];
        var output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Files.Child(snapshot, project.RelativePath))!, project.OutputPath, name));
        return output.StartsWith(Files.Full(snapshot) + "\\", StringComparison.OrdinalIgnoreCase) ? [output] : [];
    }

    /// <summary>
    /// The packages folder <paramref name="path"/> is in, relative to the snapshot: the root packages folder, or for another repository
    /// (<paramref name="main"/> false) any folder named packages, as a repository with one per project has; null when it is in none.
    /// </summary>
    private static string? PackagesFolder(string snapshot, string path, bool main)
    {
        var root = Files.Full(snapshot) + "\\";
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = path[root.Length..].Split('\\');
        var index = main ? parts.Length > 1 && parts[0].Equals("packages", StringComparison.OrdinalIgnoreCase) ? 0 : -1
            : Array.FindIndex(parts, 0, parts.Length - 1, part => part.Equals("packages", StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : string.Join('\\', parts[..(index + 1)]);
    }

    /// <param name="own">This project's reference folder, which only its own build searches.</param>
    /// <param name="ledger">What earlier projects in this build resolved. For a release it also tells the snapshot's own files from copies of
    /// outside DLLs, so the tracked bin and the projects this one depends on are tried before the developer's checkout. When one of those is
    /// chosen for a HintPath the snapshot lacks, a newer file the HintPath names from the developer's project folder is recorded in
    /// <see cref="Ledger.Alternates"/>, which <see cref="Build"/> compiles against only if the project does not compile against the snapshot's.</param>
    /// <param name="outside">For a release: records each reference taken from outside the snapshot. Null for the dev farm.</param>
    /// <param name="main">False for a release of another repository, resolved as MSBuild does in the developer's checkout and never more
    /// strictly: the HintPath in the snapshot, the HintPath from the developer's project folder, then the output folder (the snapshot's tracked
    /// copy, what a project it depends on resolved from the snapshot, the developer's). Its packages are never restored, and a reference found
    /// nowhere is left to MSBuild, as Visual Studio does: build.log says where Cultivate looked.</param>
    /// <param name="unresolved">For another repository: records each reference left to MSBuild.</param>
    private async Task Prepare(ProjectInfo project, string snapshot, string sourceRoot, string own, HashSet<string> internalNames,
        HashSet<string> libraryNames, Dictionary<string, string> environment, string run, Ledger ledger, List<OutsideReference>? outside = null, bool main = true,
        List<string>? unresolved = null)
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
            if (PackagesFolder(snapshot, destination, main) is not { } packages) continue;
            var package = Path.GetRelativePath(Path.Combine(snapshot, packages), destination).Split('\\')[0];
            var source = Path.Combine(sourceRoot, packages, package);
            if (!Directory.Exists(source)) source = Path.Combine(settings.RepositoryRoot, packages, package);
            if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(snapshot, packages, package));
            else if (File.Exists(destination)) continue;
            else if (main) restoreNeeded = true;
            // Visual Studio builds without a package the checkout never restored; its references are looked up like any other, or left to MSBuild.
            else File.AppendAllText(Path.Combine(run, "build.log"),
                $"Package {package} for {project.Assembly} is in neither the snapshot nor your checkout ({source}); Cultivate does not restore it.\n");
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
            var package = PackagesFolder(snapshot, desired, main) is not null;
            if (package && File.Exists(desired))
            {
                resolved.TryAdd(name, new(project, name, desired, desired, Files.Hash(desired), true, false));
                continue;
            }
            // A HintPath can name a package version packages.config never restores. Visual Studio then quietly falls back
            // to the checkout's bin folder; accept that copy only when it is exactly the version the Reference declares.
            project.HintVersions.TryGetValue(hint, out var declared);
            bool Usable(string path) => File.Exists(path) && (!package || declared is not null && VersionOf(path) == declared);
            string? source;
            Resolution? borrowed = null;
            string? alternate = null;
            var (dependency, lastResort) = (false, false);
            if (main)
            {
                // MyMeyer tracks its third-party DLLs in bin, so a release looks for them in its own snapshot first.
                var snapshotBin = SnapshotCopies(snapshot, project, name, main).Single();
                source = !package && File.Exists(desired) ? desired : ledger.Tracked(snapshotBin) && Usable(snapshotBin) ? snapshotBin : null;
                var earlier = source is null ? ledger.Earlier(project, name).Where(match => Usable(match.Resolution.Copy)).ToList() : [];
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
                lastResort = source is null && borrowed is null && earlier.Count != 0;
                if (lastResort) (borrowed, dependency) = earlier[0];
                // Master's code can need a newer build of another repository's DLL than master tracks, so the file the HintPath names is kept to
                // retry with when it is newer; it is as often an older local build.
                if (ledger.Release && !package && (borrowed is null ? source is not null && Files.Comparer.Equals(source, snapshotBin) &&
                    !Files.Comparer.Equals(desired, snapshotBin) : !lastResort && borrowed.FromSnapshot))
                    alternate = fallbacks.Take(2).FirstOrDefault(File.Exists);
            }
            else
            {
                // Another repository resolves as MSBuild does in the developer's checkout: the HintPath, then the output folder, searched last.
                var tried = new List<string>();
                string? Try(string path, Func<string, bool> found, string? label = null)
                {
                    tried.Add(label ?? path);
                    return found(path) ? path : null;
                }
                var inside = desired.StartsWith(Files.Full(snapshot) + "\\", StringComparison.OrdinalIgnoreCase);
                var output = SnapshotCopies(snapshot, project, name, main).FirstOrDefault();
                source = (!package && inside ? Try(desired, File.Exists, "the snapshot's " + Path.GetRelativePath(snapshot, desired)) : null) ??
                    Try(Path.GetFullPath(Path.Combine(originalDirectory, hint)), File.Exists) ??
                    (output is null ? null : Try(output, path => ledger.Tracked(path) && Usable(path), "the snapshot's " + Path.GetRelativePath(snapshot, output)));
                // What a project it depends on resolved from the snapshot is what that project's build leaves where MSBuild looks last.
                if (source is null && ledger.Release)
                {
                    tried.Add("the snapshot copies of the projects it depends on");
                    (borrowed, dependency) = ledger.Earlier(project, name).FirstOrDefault(match => match.Dependency && match.Resolution.FromSnapshot && Usable(match.Resolution.Copy));
                }
                if (source is null && borrowed is null) source = Try(Path.GetFullPath(Path.Combine(originalDirectory, project.OutputPath, name)), Usable);
                if (source is null && borrowed is null)
                {
                    // Visual Studio builds with such a reference unresolved and fails only if the code uses it, so the compiler decides here too.
                    File.AppendAllText(log, $"Unresolved reference {name} for {project.Assembly}: {hint} was not found" +
                        (package && declared is not null ? $" in version {declared}" : "") + $". Looked in: {string.Join("; ", tried.Distinct(Files.Comparer))}. Left to MSBuild.\n");
                    unresolved?.Add($"{name} (HintPath {hint}) for {project.RelativePath}");
                    continue;
                }
            }
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
                : !main ? $"Reference {name}: {origin} ({borrowed.Project.Assembly}'s copy: neither {project.Assembly}'s HintPath {hint} nor its output folder " +
                    $"in the snapshot has it, and {project.Assembly} depends on {borrowed.Project.Assembly}, which resolved this copy from the snapshot, so it is " +
                    "used before the developer's output folder)\n"
                : $"Reference {name}: {origin} ({borrowed.Project.Assembly}'s copy: {project.Assembly}'s HintPath {hint} is not in the snapshot, and " +
                    (dependency ? $"{project.Assembly} depends on {borrowed.Project.Assembly}, which" : $"{borrowed.Project.Assembly}, built earlier in this release,") +
                    " resolved this copy from the snapshot, so it is used instead of a DLL from outside the snapshot)\n");
            if (isOutside) outside?.Add(OutsideReference.Of(origin, project.RelativePath));
            if (alternate is null || Files.Hash(alternate) == hash) continue;
            var (newer, current) = (VersionOf(alternate), VersionOf(from));
            var whose = borrowed is null ? "the snapshot's" : $"{borrowed.Project.Assembly}'s copy";
            if (Version.TryParse(newer, out var alternateVersion) && Version.TryParse(current, out var currentVersion) && alternateVersion > currentVersion)
            {
                if (!ledger.Alternates.TryGetValue(project, out var alternates)) ledger.Alternates[project] = alternates = [];
                if (!alternates.Any(known => Files.Comparer.Equals(known.Name, name))) alternates.Add(new(name, alternate, newer!, current!, borrowed?.Project.Assembly));
                File.AppendAllText(log, $"Alternate for {name}: {alternate} ({newer}), newer than {whose} {current}; tried only if {project.Assembly} does not compile\n");
            }
            else File.AppendAllText(log, $"Found {alternate} ({newer ?? "no readable version"}), the file {project.Assembly}'s HintPath {hint} names, but it is not " +
                $"newer than {whose} {current ?? "(no readable version)"}, so it is not used\n");
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
