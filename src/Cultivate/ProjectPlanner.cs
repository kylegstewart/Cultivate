using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Cultivate;

internal sealed class ProjectInfo
{
    public required string RelativePath { get; init; }
    public required string Assembly { get; init; }
    public List<string> Inputs { get; init; } = [];
    public List<string> ProjectReferences { get; init; } = [];
    public List<string> AssemblyReferences { get; init; } = [];
    public List<string> Hints { get; init; } = [];
    public List<string> Imports { get; init; } = [];

    public static ProjectInfo Read(string root, string path)
    {
        var full = Files.Child(root, path);
        var directory = Path.GetDirectoryName(full)!;
        var document = XDocument.Load(full);
        IEnumerable<XElement> Items(string name) => document.Descendants().Where(x => x.Name.LocalName == name);
        string Relative(string include) => Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(directory, include)));
        return new()
        {
            RelativePath = path.Replace('/', '\\'),
            Assembly = Items("AssemblyName").Select(x => x.Value).FirstOrDefault(x => x.Length != 0) ?? Path.GetFileNameWithoutExtension(path),
            Inputs = Items("Compile").Concat(Items("EmbeddedResource")).Select(x => x.Attribute("Include")?.Value)
                .Concat(Items("Import").Select(x => x.Attribute("Project")?.Value))
                .Where(x => x is not null && !x.Contains('$')).Select(x => Relative(x!)).ToList(),
            ProjectReferences = Items("ProjectReference").Select(x => x.Attribute("Include")?.Value)
                .Where(x => x is not null).Select(x => Relative(x!)).ToList(),
            AssemblyReferences = Items("Reference").Select(x => (x.Attribute("Include")?.Value ?? "").Split(',')[0]).ToList(),
            Hints = Items("HintPath").Select(x => x.Value).ToList(),
            Imports = Items("Import").Select(x => x.Attribute("Project")?.Value ?? "").Where(x => !x.Contains('$')).ToList()
        };
    }

    public bool Owns(string path)
    {
        path = path.Replace('/', '\\');
        if (Files.Comparer.Equals(path, RelativePath)) return true;
        if (Path.GetFileName(path).Equals("packages.config", StringComparison.OrdinalIgnoreCase))
            return Files.Comparer.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(RelativePath));
        return Inputs.Any(input => Files.Comparer.Equals(input, path));
    }

    public bool Owns(Change change, IEnumerable<Change> changes) => Owns(change.Path) || change.Deleted &&
        changes.Any(item => Files.Comparer.Equals(item.Path.Replace('/', '\\'), RelativePath)) &&
        change.Path.Replace('/', '\\').StartsWith(Path.GetDirectoryName(RelativePath) + "\\", StringComparison.OrdinalIgnoreCase);
}

internal sealed partial class Planner(Settings settings, GitRepository git, Bridge bridge)
{
    internal static readonly HashSet<string> FrontendExtensions = new(
        [".ascx", ".aspx", ".css", ".js", ".html", ".htm", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".eot"], Files.Comparer);
    private static readonly HashSet<string> CompiledExtensions = new([".cs", ".resx", ".csproj", ".props", ".targets"], Files.Comparer);

    internal static bool IsCompiledInput(Change change) => CompiledExtensions.Contains(Path.GetExtension(change.Path)) ||
        Path.GetFileName(change.Path).Equals("packages.config", StringComparison.OrdinalIgnoreCase);

    public async Task<Plan> Create(Target target, Func<string, Task<Dictionary<string, string>>>? mapping = null)
    {
        var inspected = await git.Inspect(target);
        var plan = new Plan { Target = target, Commit = inspected.Commit, Changes = inspected.Changes, Fingerprint = inspected.Fingerprint };
        if (target.Ref == "refs/heads/" + settings.BaseBranch || target.Branch == settings.BaseBranch ||
            target.Remote && target.Branch.EndsWith("/" + settings.BaseBranch, StringComparison.OrdinalIgnoreCase))
            plan.Errors.Add("The baseline branch is not a test deployment. Select a development branch.");
        var frontend = plan.Changes.Where(x => FrontendExtensions.Contains(Path.GetExtension(x.Path)) && !Excluded().IsMatch(x.Path)).ToList();
        if (frontend.Count != 0)
        {
            var names = frontend.Select(x => Path.GetFileName(x.Path)).Distinct(Files.Comparer).ToList();
            var mappings = mapping is null ? await bridge.Mappings(names) : await mapping(string.Join("\n", names));
            foreach (var change in frontend)
            {
                var name = Path.GetFileName(change.Path);
                if (!mappings.TryGetValue(name, out var folder) || string.IsNullOrWhiteSpace(folder))
                {
                    plan.Errors.Add($"No Code Release Manager destination for {change.Path}. Add the mapping before deploying.");
                    continue;
                }
                var destination = Path.Combine(folder.Replace('/', '\\'), name);
                Files.Child(settings.WebfarmRoot, destination);
                if (plan.Frontend.Any(x => Files.Comparer.Equals(x.RelativePath, destination)))
                    plan.Errors.Add($"Multiple source files map to {destination}.");
                plan.Frontend.Add(new(change.Path, destination, change.Deleted));
            }
        }
        var compiled = plan.Changes.Where(IsCompiledInput).ToList();
        using (var buildHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            buildHash.AppendData(Encoding.UTF8.GetBytes("changed-project-targets-v2\0" + plan.Commit + "\0" + settings.MSBuild + "\0"));
            foreach (var change in compiled)
            {
                var content = change.Deleted ? "(deleted)" : target.Worktree is null ?
                    await git.Git(settings.RepositoryRoot, "ls-tree", plan.Commit, "--", change.Path) :
                    Files.Hash(Files.Child(target.Worktree, change.Path));
                buildHash.AppendData(Encoding.UTF8.GetBytes(change.Path + "\0" + content + "\0"));
            }
            plan.BuildFingerprint = Convert.ToHexString(buildHash.GetHashAndReset());
        }
        if (compiled.Count != 0)
        {
            // Catalog the selected commit, not a different branch in the normal checkout.
            var root = target.Worktree;
            string? catalog = null;
            if (root is null)
            {
                catalog = Path.Combine(Files.Home, "Runs", Guid.NewGuid().ToString("N"), "catalog");
                Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
                await git.Git(settings.RepositoryRoot, "worktree", "add", "--detach", catalog, inspected.Commit);
                root = catalog;
            }
            try
            {
                var paths = (await git.Git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.csproj")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var projects = paths.Where(path => File.ReadAllText(Files.Child(root, path)).Contains("CopyBuildOutputToWebfarm", StringComparison.Ordinal))
                    .Select(path => ProjectInfo.Read(root, path)).ToList();
                plan.Projects.AddRange(BuildOrder(projects, compiled, plan.Errors));
            }
            finally
            {
                if (catalog is not null) await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", catalog);
            }
        }
        var ignored = plan.Changes.Count - frontend.Count - compiled.Count;
        if (ignored > 0) plan.Notes.Add($"{ignored} other changed file(s) are not deployed (documentation, SQL, config, etc.). No database scripts are executed.");
        if (plan.Projects.Count == 0) plan.Notes.Add("No changed compiled projects: no DLL builds or copies.");
        else plan.Notes.Add("Only projects owning changed compiled files are built. Unchanged dependencies and consumers are not rebuilt or deployed.");
        return plan;
    }

    internal static List<ProjectInfo> BuildOrder(List<ProjectInfo> projects, List<Change> changes, List<string> errors)
    {
        var byPath = projects.ToDictionary(x => x.RelativePath, Files.Comparer);
        var duplicates = projects.GroupBy(x => x.Assembly, Files.Comparer).Where(x => x.Count() > 1).ToList();
        if (duplicates.Count != 0) throw new InvalidOperationException("Ambiguous assembly projects: " + string.Join(", ", duplicates.Select(x => x.Key)));
        var byAssembly = projects.ToDictionary(x => x.Assembly, Files.Comparer);
        var dependencies = projects.ToDictionary(x => x.RelativePath,
            x => x.ProjectReferences.Where(byPath.ContainsKey).Concat(x.AssemblyReferences.Where(byAssembly.ContainsKey).Select(a => byAssembly[a].RelativePath))
                .Where(p => !Files.Comparer.Equals(p, x.RelativePath)).Distinct(Files.Comparer).ToList(), Files.Comparer);
        var affected = new HashSet<string>(Files.Comparer);
        foreach (var change in changes)
        {
            var owners = projects.Where(project => project.Owns(change, changes)).ToList();
            if (owners.Count == 0) errors.Add($"Cannot identify the compiled project for {change.Path}. Nothing will be deployed.");
            foreach (var project in owners) affected.Add(project.RelativePath);
        }
        var ordered = new List<ProjectInfo>();
        var visiting = new HashSet<string>(Files.Comparer);
        var visited = new HashSet<string>(Files.Comparer);
        void Visit(string path)
        {
            if (visited.Contains(path)) return;
            if (!visiting.Add(path)) throw new InvalidOperationException($"Project dependency cycle at {path}.");
            foreach (var dependency in dependencies[path].Where(affected.Contains)) Visit(dependency);
            visiting.Remove(path);
            visited.Add(path);
            ordered.Add(byPath[path]);
        }
        foreach (var path in affected.Order(Files.Comparer)) Visit(path);
        return ordered;
    }

    [GeneratedRegex(@"(^|[/\\])(bin|obj|packages|\.git|Backup)[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex Excluded();
}
