using System.Text.RegularExpressions;

namespace Cultivate;

/// <summary>
/// Visual Studio resolves a solution's references from files Git never checks out: NuGet packages in an ignored packages folder and
/// DLLs built into an ignored bin folder, such as MyMeyer's bin\MeyerObjects.dll. A new worktree has none of them, so its projects
/// load with unresolved references. Before a solution opens, the ignored files the repository's main checkout already has are copied
/// in, and packages it lacks too are restored. A file Git tracks, or would list as untracked, is never created.
/// </summary>
internal static partial class SolutionDependencies
{
    internal sealed record Result(int CopiedFiles, int Packages, int Restored, List<string> Warnings)
    {
        public string? Summary => CopiedFiles + Packages + Restored == 0 ? null :
            $"Prepared dependencies: copied {CopiedFiles} file(s) and {Packages} NuGet package(s) from the main checkout" +
            (Restored == 0 ? "." : $", and restored packages for {Restored} project(s).");
    }

    /// <summary>Project files the solution lists that exist inside <paramref name="root"/>, relative to it.</summary>
    internal static List<string> Projects(string root, string solution) => SolutionProject().Matches(File.ReadAllText(solution))
        .Select(match => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, match.Groups[1].Value)))
        .Where(path => path.StartsWith(Files.Full(root) + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        .Select(path => Path.GetRelativePath(root, path)).Distinct(Files.Comparer).ToList();

    /// <summary>Referenced DLLs and imported files the solution's projects name that are missing from <paramref name="root"/>.</summary>
    internal static List<(string Project, string Path)> Missing(string root, string solution)
    {
        var missing = new List<(string, string)>();
        foreach (var project in Projects(root, solution))
        {
            ProjectInfo info;
            try { info = ProjectInfo.Read(root, project); }
            catch (System.Xml.XmlException) { continue; } // Visual Studio reports an unreadable project itself.
            var directory = Path.GetDirectoryName(Files.Child(root, project))!;
            foreach (var reference in info.Hints.Concat(info.Imports).Select(x => x.Trim()).Where(x => x.Length != 0 && !x.Contains('$')))
            {
                var full = Path.GetFullPath(Path.Combine(directory, reference));
                // Sibling repositories outside this checkout are not this checkout's to fill in.
                if (full.StartsWith(Files.Full(root) + "\\", StringComparison.OrdinalIgnoreCase) && !File.Exists(full))
                    missing.Add((project, Path.GetRelativePath(root, full)));
            }
        }
        return missing;
    }

    /// <summary>The package folder, such as packages\BusinessLogic.6.28.21, that holds a file restored by NuGet.</summary>
    internal static string? PackageFolder(string relative)
    {
        var parts = relative.Split('\\');
        var index = Array.FindIndex(parts, part => part.Equals("packages", StringComparison.OrdinalIgnoreCase));
        return index < 0 || index + 2 >= parts.Length ? null : string.Join('\\', parts[..(index + 2)]);
    }

    public static async Task<Result> Prepare(GitRepository git, string msbuild, string solution, Action<string>? progress = null)
    {
        var warnings = new List<string>();
        string root;
        try { root = Files.Full(await git.Git(Path.GetDirectoryName(solution)!, "rev-parse", "--show-toplevel")); }
        catch (InvalidOperationException) { return new(0, 0, 0, warnings); } // Not a Git checkout, so nothing was left out of it.
        var missing = Missing(root, solution);
        if (missing.Count == 0) return new(0, 0, 0, warnings);

        var files = 0;
        var copiedPackages = new HashSet<string>(Files.Comparer);
        var main = await git.MainWorktree(root);
        if (main is not null && !Files.Comparer.Equals(main, root))
        {
            progress?.Invoke("Copying ignored references from the main checkout");
            foreach (var relative in await Ignored(git, root, missing.Select(x => x.Path).Distinct(Files.Comparer)))
            {
                try
                {
                    // A package folder is named for its exact version, so the main checkout's copy is the same package.
                    if (PackageFolder(relative) is { } package)
                    {
                        var source = Path.Combine(main, package);
                        if (!copiedPackages.Contains(package) && Directory.Exists(source))
                        {
                            Pipeline.CopyDirectory(source, Files.Child(root, package));
                            copiedPackages.Add(package);
                        }
                    }
                    else
                    {
                        var source = Path.Combine(main, relative);
                        var destination = Files.Child(root, relative);
                        if (File.Exists(destination) || !File.Exists(source)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination);
                        files++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    warnings.Add($"Could not copy {relative} from the main checkout: {exception.Message}");
                }
            }
        }

        // Packages the main checkout doesn't have either, such as a version only this branch uses, come from the NuGet feeds.
        var restore = missing.Where(x => PackageFolder(x.Path) is not null && !File.Exists(Files.Child(root, x.Path)))
            .GroupBy(x => x.Project, Files.Comparer)
            .Where(group => File.Exists(Path.Combine(Path.GetDirectoryName(Files.Child(root, group.Key))!, "packages.config")))
            .ToList();
        var restored = 0;
        if (restore.Count != 0 && !File.Exists(msbuild))
            warnings.Add($"MSBuild was not found, so NuGet packages for {restore.Count} project(s) were not restored. Visual Studio restores them when you build.");
        else if (restore.Count != 0)
        {
            var log = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-open-" + Guid.NewGuid().ToString("N")[..8], "restore.log");
            foreach (var group in restore)
            {
                progress?.Invoke($"Restoring NuGet packages for {Path.GetFileNameWithoutExtension(group.Key)}");
                // packages.config restores into $(SolutionDir)packages, so point it at the folder the HintPaths expect.
                var solutionDir = Path.GetDirectoryName(Path.GetDirectoryName(Files.Child(root, PackageFolder(group.First().Path)!))!)!;
                try
                {
                    await Commands.Run(msbuild,
                        [Files.Child(root, group.Key), "-nologo", "-t:Restore", "-p:RestorePackagesConfig=true",
                         $"-p:SolutionDir={solutionDir}\\", "-v:minimal"],
                        root, null, log, new("Package restore") { Project = group.Key });
                    restored++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    warnings.Add($"NuGet restore failed for {group.Key}, so some of its references may show as missing until a build restores them. Log: {log}");
                }
            }
        }
        return new(files, copiedPackages.Count, restored, warnings);
    }

    /// <summary>The paths Git ignores. Copying only these keeps the checkout's status unchanged.</summary>
    private static async Task<HashSet<string>> Ignored(GitRepository git, string root, IEnumerable<string> paths)
    {
        var ignored = new HashSet<string>(Files.Comparer);
        foreach (var chunk in paths.Select(path => path.Replace('\\', '/')).Chunk(100))
        {
            string output;
            try { output = await git.Git(root, ["check-ignore", "--", .. chunk]); }
            catch (InvalidOperationException) { continue; } // Exit code 1: none of these paths are ignored.
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                ignored.Add(line.TrimEnd('\r').Replace('/', '\\'));
        }
        return ignored;
    }

    [GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.(?:cs|vb|fs)proj)""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProject();
}
