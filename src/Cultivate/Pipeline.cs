using System.Reflection;
using System.Text.Json;

namespace Cultivate;

internal sealed class Pipeline(Settings settings, GitRepository git, Bridge bridge)
{
    public async Task<DeploymentState> Activate(Plan plan, Action<string> progress, bool watch = true, bool workerUpdate = false)
    {
        if (!plan.CanDeploy) throw new InvalidOperationException(string.Join("\n", plan.Errors));
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Capture.targets")))
            throw new FileNotFoundException("Cultivate's build capture target is missing. Reinstall Cultivate.");
        await bridge.Call("Check");
        var previous = await bridge.Status();
        var run = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        Files.WriteJson(Path.Combine(run, "plan.json"), plan);
        string? snapshot = null;
        try
        {
            progress("Snapshotting the selected version (your checkout stays untouched)");
            snapshot = await git.Snapshot(plan, run);
            var files = plan.Frontend.Select(x => x with { Source = Files.Child(snapshot, x.Source) }).ToList();
            var runtimeFiles = new List<RuntimeFile>();
            var reuse = workerUpdate && previous?.Active == true && previous.BuildFingerprint == plan.BuildFingerprint &&
                previous.RuntimeFiles.Count == plan.Projects.Count &&
                previous.RuntimeFiles.All(file => plan.Projects.Any(project =>
                    Files.Comparer.Equals(file.RelativePath, @"bin\" + project.Assembly + ".dll"))) &&
                previous.SourceRef == plan.Target.Ref && Files.Comparer.Equals(previous.SourceRoot, plan.Target.Worktree ?? "");
            if (reuse)
            {
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
            else if (plan.Projects.Count != 0)
            {
                files.AddRange(await Build(plan, snapshot, run, progress));
                foreach (var artifact in files.Where(x => x.RelativePath.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)))
                {
                    var cached = Files.Child(Path.Combine(run, "verified"), artifact.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    File.Copy(artifact.Source, cached, true);
                    runtimeFiles.Add(new(cached, artifact.RelativePath, Files.Hash(cached)));
                }
            }
            if ((await git.Inspect(plan.Target)).Fingerprint != plan.Fingerprint)
                throw new InvalidOperationException("The selected source changed during the build. Nothing was deployed. Preview again.");
            progress("Deploying the verified set; journaling originals for restoration");
            var result = await bridge.Call("Deploy", new
            {
                Snapshot = snapshot, SourceRoot = plan.Target.Worktree ?? "", SourceRef = plan.Target.Ref,
                plan.Target.Branch, plan.Fingerprint, ExpectedToken = previous?.Token ?? "",
                WatchEnabled = watch || workerUpdate,
                plan.BuildFingerprint, RuntimeFiles = runtimeFiles, WorkerUpdate = workerUpdate,
                Files = files.Select(x => new { x.Source, x.RelativePath, x.Delete, Hash = x.Delete ? null : Files.Hash(x.Source) }).ToList()
            });
            var state = result.Deserialize<DeploymentState>(Files.Json) ?? throw new InvalidDataException("Deployment did not return its state.");
            if (watch)
            {
                progress("Starting automatic saved-file updates");
                await Watcher.Start(settings, bridge, state.Token);
            }
            return state;
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot))
                await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", snapshot);
        }
    }

    internal async Task<List<DeployFile>> Build(Plan plan, string snapshot, string run, Action<string> progress)
    {
        progress("Preparing references for changed projects only");
        var seeds = Path.Combine(run, "references");
        Directory.CreateDirectory(seeds);
        var environment = new Dictionary<string, string>
        {
            ["WebfarmPath"] = Path.Combine(run, "intercepted") + "\\",
            ["CultivateRunDirectory"] = run,
            ["CultivateCaptureTargets"] = Path.Combine(AppContext.BaseDirectory, "Capture.targets"),
            ["ReferencePath"] = seeds
        };
        var internalNames = plan.Projects.Select(x => x.Assembly).ToHashSet(Files.Comparer);
        foreach (var project in plan.Projects)
        {
            await Prepare(project, snapshot, plan.Target.Worktree ?? settings.RepositoryRoot, seeds, internalNames, environment, run);
            var full = Files.Child(snapshot, project.RelativePath);
            var metadataText = await Commands.Run(settings.MSBuild,
                [full, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath,_CultivateCaptureReady",
                 "-getItem:Compile,EmbeddedResource,ProjectReference"], snapshot, environment, Path.Combine(run, "build.log"));
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
                if (plan.Projects.Any(x => Files.Comparer.Equals(x.RelativePath, relative))) continue;
                Files.Child(snapshot, relative);
                var referenceMetadata = await Commands.Run(settings.MSBuild,
                    [Files.Child(snapshot, relative), "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"],
                    snapshot, environment, Path.Combine(run, "build.log"));
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
            }
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
            progress($"Building {project.Assembly} ({plan.Projects.IndexOf(project) + 1}/{plan.Projects.Count})");
            var arguments = new List<string>
            {
                full, "-nologo", "-t:Build", "-p:Configuration=Debug", "-p:BuildProjectReferences=false",
                "-m:1", "-v:minimal",
                $"-bl:{Path.Combine(run, project.Assembly + ".binlog")}"
            };
            await Commands.Run(settings.MSBuild, arguments, snapshot, environment, Path.Combine(run, "build.log"));
            if (!File.Exists(targetPath)) throw new FileNotFoundException("Build succeeded without its expected assembly.", targetPath);
            var identity = AssemblyName.GetAssemblyName(targetPath);
            if (!Files.Comparer.Equals(identity.Name, project.Assembly))
                throw new InvalidDataException($"Unexpected assembly identity for {targetPath}.");
            File.Copy(targetPath, Path.Combine(seeds, Path.GetFileName(targetPath)), true);
        }
        progress("Verifying only the changed projects' own DLLs");
        return Artifacts.Collect(run, snapshot, plan.Projects);
    }

    private async Task Prepare(ProjectInfo project, string snapshot, string sourceRoot, string seeds, HashSet<string> internalNames,
        Dictionary<string, string> environment, string run)
    {
        var directory = Path.GetDirectoryName(Files.Child(snapshot, project.RelativePath))!;
        var originalDirectory = Path.GetDirectoryName(Files.Child(sourceRoot, project.RelativePath))!;
        var restoreNeeded = false;
        foreach (var relative in project.Hints.Concat(project.Imports))
        {
            if (relative.Contains('$')) continue;
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
                 "-p:RestorePackagesConfig=true", $"-p:SolutionDir={snapshot}\\", "-v:minimal"],
                snapshot, environment, Path.Combine(run, "restore.log"));
        }
        foreach (var hint in project.Hints)
        {
            if (hint.Contains('$')) continue;
            var name = Path.GetFileName(hint);
            if (internalNames.Contains(Path.GetFileNameWithoutExtension(name))) continue;
            var desired = Path.GetFullPath(Path.Combine(directory, hint));
            var packageRoot = Path.Combine(snapshot, "packages") + "\\";
            string source;
            if (desired.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(desired)) continue;
                // A HintPath can name a package version packages.config never restores. Visual Studio then quietly falls back
                // to the checkout's bin folder; accept that copy only when it is exactly the version the Reference declares.
                project.HintVersions.TryGetValue(hint, out var declared);
                source = new[] { Path.Combine(sourceRoot, "bin", name), Path.Combine(settings.RepositoryRoot, "bin", name) }
                    .FirstOrDefault(path => declared is not null && VersionOf(path) == declared) ??
                    throw new FileNotFoundException($"Declared package dependency {hint} for {project.Assembly} is missing after restore" +
                        (declared is null ? "" : $", and no bin folder has version {declared}") +
                        ". Align the project's HintPath with packages.config; Cultivate will not substitute a Visual Studio or GAC DLL.", desired);
            }
            else
            {
                source = File.Exists(desired) ? desired : Path.GetFullPath(Path.Combine(originalDirectory, hint));
                if (!File.Exists(source))
                    source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Files.Child(settings.RepositoryRoot, project.RelativePath))!, hint));
                if (!File.Exists(source)) source = Path.Combine(sourceRoot, "bin", name);
                if (!File.Exists(source)) source = Path.Combine(settings.RepositoryRoot, "bin", name);
                if (!File.Exists(source))
                    throw new FileNotFoundException($"Missing dependency {name} for {project.Assembly}. Restore the normal checkout's dependency or correct its reference; Cultivate will not guess another version.", source);
            }
            var seeded = Path.Combine(seeds, name);
            if (File.Exists(seeded) && Files.Hash(seeded) != Files.Hash(source))
                throw new InvalidOperationException($"Incompatible input DLLs named {name}. Resolve references before deploying.");
            File.AppendAllText(Path.Combine(run, "build.log"), $"Reference {name}: {source}\n");
            File.Copy(source, seeded, true);
        }
    }

    private static string? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
        catch (BadImageFormatException) { return null; }
    }

    private static void CopyDirectory(string source, string destination)
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
