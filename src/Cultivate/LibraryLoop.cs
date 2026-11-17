using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>A library checkout whose branch matches the MyMeyer worktree being deployed, and what building it would deploy.</summary>
internal sealed class LibraryChange
{
    public required string Repository { get; init; }
    public required string RepositoryRoot { get; init; }
    public required string Worktree { get; init; }
    public required string Branch { get; init; }
    /// <summary>The same branch name as the MyMeyer version; preferred over other branches of the same ticket.</summary>
    public bool SameBranch { get; init; }
    public bool Included { get; set; }
    public string BaseBranch { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Baseline { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? Error { get; set; }
    public List<Change> Changes { get; set; } = [];
    /// <summary>Projects owning changed code: deployable ones have no <see cref="LibraryProject.Reason"/>.</summary>
    public List<LibraryProject> Projects { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    [JsonIgnore] public string BuildInputs { get; set; } = "";
    [JsonIgnore] public Target Target => new(Branch, Worktree, "refs/heads/" + Branch);
    [JsonIgnore] public IEnumerable<LibraryProject> Deployed => Included && Error is null ? Projects.Where(project => project.Reason is null) : [];
    public LibrarySource Source() => new(Worktree, "refs/heads/" + Branch, BaseBranch);
}

internal sealed class LibraryProject
{
    public required string RelativePath { get; init; }
    public required string Assembly { get; init; }
    public string TargetFramework { get; init; } = "";
    /// <summary>The package version the project file sets, which the built assembly carries; empty when it sets none.</summary>
    public string Version { get; init; } = "";
    public string BaselineVersion { get; init; } = "";
    /// <summary>Assembly version of the webfarm's bin DLL; null when it has none or could not be read.</summary>
    public string? WebfarmVersion { get; init; }
    public string BuiltVersion { get; set; } = "";
    /// <summary>Why this changed project is not built and deployed; null when it is.</summary>
    public string? Reason { get; init; }
}

/// <summary>An included library worktree, as the journal stores it for the saved-file worker.</summary>
internal sealed record LibrarySource(string Worktree, string Ref, string BaseBranch)
{
    public Target ToTarget() => new(Ref["refs/heads/".Length..], Worktree, Ref);
}

/// <summary>Builds changed library projects from a matching library worktree and deploys their DLLs with the MyMeyer version.</summary>
internal sealed partial class LibraryLoop(Settings settings, GitRepository git)
{
    /// <summary>Requests every detected match: the preferred worktree of each library repository.</summary>
    public const string Every = "*";
    internal const string ChooseAction = "Choose library changes";
    private static readonly HashSet<string> SourceExtensions = new([".cs", ".resx"], Files.Comparer);

    /// <summary>Library clones beside the MyMeyer checkout: folders named Libraries--* that are Git checkouts.</summary>
    public static List<string> Discover(string repositoryRoot)
    {
        if (repositoryRoot.Length == 0 || Path.GetDirectoryName(Files.Full(repositoryRoot)) is not { } clones || !Directory.Exists(clones)) return [];
        try
        {
            return Directory.EnumerateDirectories(clones, "Libraries--*").Where(path => Path.Exists(Path.Combine(path, ".git")))
                .Select(Files.Full).Order(Files.Comparer).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Reads a ;-separated list of library checkouts; blank or "none" deliberately configures none.</summary>
    public static List<string> ParseRepositories(string value) =>
        RepositoriesProblem(value) is { } problem ? throw new InvalidOperationException(problem) : Split(value).Select(Files.Full).ToList();

    public static string? RepositoriesProblem(string value) => Split(value)
        .Select(path => !Path.IsPathFullyQualified(path) ? $"{path} is not an absolute path." :
            !Path.Exists(Path.Combine(path, ".git")) ? $"{path} is not a Git checkout." : null)
        .FirstOrDefault(problem => problem is not null);

    private static IEnumerable<string> Split(string value) => value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? [] :
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(path => path.Trim('"'));

    /// <summary>The libraries <c>plan</c> and <c>activate</c> include: each <c>--library PATH</c>, or every match with <c>--libraries</c>.</summary>
    public static List<string> Requested(string[] args)
    {
        var requested = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--libraries") requested.Add(Every);
            else if (args[index] == "--library")
                requested.Add(index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++index] : throw new InvalidOperationException("--library needs a library worktree path."));
        }
        return requested;
    }

    /// <summary>Library worktrees to keep when the active deployment's version is previewed again.</summary>
    public static List<string> Kept(DeploymentState? state, Target target) =>
        state?.Active == true && state.SourceRef == target.Ref && Files.Comparer.Equals(state.SourceRoot, target.Worktree ?? "")
            ? state.Libraries.Select(library => library.Worktree).ToList() : [];

    public static List<LibrarySource> Sources(Plan plan) => plan.Libraries.Where(library => library.Included).Select(library => library.Source()).ToList();

    public static IEnumerable<string> Assemblies(Plan plan) => plan.Libraries.SelectMany(library => library.Deployed).Select(project => project.Assembly);

    /// <summary>The selected source's fingerprint; with libraries included it also covers their saved changes. Without, it is Inspect's own.</summary>
    public static async Task<string> Fingerprint(GitRepository git, Target target, IReadOnlyCollection<LibrarySource> libraries)
    {
        var own = git.Inspect(target);
        if (libraries.Count == 0) return (await own).Fingerprint;
        var others = await Task.WhenAll(libraries.Select(async library =>
            (library.Worktree, (await git.Inspect(library.ToTarget(), library.BaseBranch)).Fingerprint)));
        return Combine((await own).Fingerprint, others);
    }

    internal static string Combine(string own, IEnumerable<(string Worktree, string Fingerprint)> libraries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("library-loop-v1\0" + own + string.Concat(libraries
            .Select(library => (Worktree: Files.Full(library.Worktree).ToUpperInvariant(), library.Fingerprint))
            .OrderBy(library => library.Worktree, StringComparer.Ordinal).Select(library => "\0" + library.Worktree + "\0" + library.Fingerprint)))));

    /// <summary>
    /// Finds library checkouts on the target's ticket (or, for other branches, the same branch) with one worktree listing per
    /// configured repository, and works out what each would build. Only <paramref name="requested"/> worktrees are included.
    /// </summary>
    public async Task<List<LibraryChange>> Detect(Target target, IReadOnlyCollection<string> requested, List<string> errors)
    {
        var repositories = (settings.LibraryRepositories ?? []).Where(path => path.Length != 0).Distinct(Files.Comparer).ToList();
        var eligible = target.Worktree is not null && target.Ref.StartsWith("refs/heads/", StringComparison.Ordinal);
        if (!eligible || repositories.Count == 0)
        {
            if (requested.Count != 0)
                errors.Add(eligible ? "No library repositories are configured. Add them with `cultivate setup`." :
                    "Library changes deploy only with a MyMeyer worktree on a branch, not with a committed branch or a detached worktree.");
            return [];
        }
        var branch = target.Ref["refs/heads/".Length..];
        var ticket = WorktreeCatalog.TicketOf(branch);
        var runs = Path.Combine(Files.Home, "Runs") + "\\";
        var listings = await Task.WhenAll(repositories.Select(async repository =>
        {
            try { return await git.Git(repository, "worktree", "list", "--porcelain", "-z"); }
            catch (InvalidOperationException) { return ""; } // A missing or unreadable repository has no matches.
        }));
        var candidates = new List<LibraryChange>();
        foreach (var listing in listings)
        {
            var blocks = listing.Split("\0\0", StringSplitOptions.RemoveEmptyEntries).Select(block => block.Split('\0')).ToList();
            string? Field(string[] fields, string name) => fields.FirstOrDefault(field => field.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];
            if (blocks.Count == 0 || blocks[0].Contains("bare") || Field(blocks[0], "worktree") is not { } primary) continue;
            primary = Files.Full(primary);
            foreach (var fields in blocks)
            {
                if (Field(fields, "worktree") is not { } path || Field(fields, "branch") is not { } reference ||
                    !reference.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                path = Files.Full(path);
                var name = reference["refs/heads/".Length..];
                if (!Directory.Exists(path) || path.StartsWith(runs, StringComparison.OrdinalIgnoreCase) ||
                    Files.Comparer.Equals(path, Files.Full(target.Worktree!)) || candidates.Any(other => Files.Comparer.Equals(other.Worktree, path)) ||
                    (ticket is null ? name != branch : WorktreeCatalog.TicketOf(name) != ticket)) continue;
                candidates.Add(new() { Repository = Path.GetFileName(primary), RepositoryRoot = primary, Worktree = path, Branch = name, SameBranch = name == branch });
            }
        }
        candidates = candidates.OrderByDescending(library => library.SameBranch).ThenBy(library => library.Repository, Files.Comparer)
            .ThenBy(library => library.Worktree, Files.Comparer).ToList();
        foreach (var path in requested.Where(path => path != Every))
        {
            var match = candidates.FirstOrDefault(library => Files.Comparer.Equals(library.Worktree, Files.Full(path)));
            if (match is null) errors.Add($"{path} is not a library worktree matching {branch}. The plan lists the matching ones.");
            else match.Included = true;
        }
        if (requested.Contains(Every))
            foreach (var repository in candidates.GroupBy(library => library.RepositoryRoot, Files.Comparer).Where(group => !group.Any(library => library.Included)))
                repository.First().Included = true;
        foreach (var repository in candidates.Where(library => library.Included).GroupBy(library => library.RepositoryRoot, Files.Comparer).Where(group => group.Count() > 1))
            errors.Add($"Include one worktree of {repository.First().Repository}, not {string.Join(" and ", repository.Select(library => library.Worktree))}.");
        await Task.WhenAll(candidates.Select(Analyze));
        foreach (var library in candidates.Where(library => library.Included && library.Error is not null))
            errors.Add($"The library worktree {library.Worktree} could not be inspected: {library.Error}");
        foreach (var name in candidates.SelectMany(library => library.Deployed).GroupBy(project => project.Assembly, Files.Comparer).Where(group => group.Count() > 1))
            errors.Add($"Several included libraries build {name.Key}.dll. Include only one of them.");
        return candidates;
    }

    /// <summary>Adds detected libraries to a new plan; an included library's saved changes become part of its source fingerprint.</summary>
    public static void Apply(Plan plan, List<LibraryChange> libraries, List<string> errors)
    {
        plan.Libraries.AddRange(libraries);
        plan.Errors.AddRange(errors);
        var included = libraries.Where(library => library.Included).ToList();
        if (included.Count != 0) plan.Fingerprint = Combine(plan.Fingerprint, included.Select(library => (library.Worktree, library.Fingerprint)));
    }

    /// <summary>Checks the included libraries against the MyMeyer projects the plan builds, and explains them in the plan's notes.</summary>
    public static void Check(Plan plan)
    {
        foreach (var assembly in Assemblies(plan).Where(name => plan.Projects.Any(project => Files.Comparer.Equals(project.Assembly, name))))
            plan.Errors.Add($"Both a MyMeyer project and an included library build {assembly}.dll.");
        var deployed = plan.Libraries.Where(library => library.Deployed.Any()).ToList();
        var leftOut = plan.Libraries.Where(library => !library.Included && library.Projects.Any(project => project.Reason is null)).ToList();
        if (deployed.Count != 0)
            plan.Notes.Add($"Library builds deploy {string.Join(", ", deployed.SelectMany(library => library.Deployed).Select(project => project.Assembly + ".dll"))}; " +
                "changed MyMeyer projects compile against them. Nothing is packed or published.");
        else if (plan.Libraries.Any(library => library.Included))
            plan.Notes.Add("The included library worktrees have no changed project to deploy yet.");
        if (leftOut.Count != 0)
            plan.Notes.Add($"Matching library changes in {string.Join(", ", leftOut.Select(library => library.Repository + " " + library.Branch))} are left out. " +
                "Choose library changes to build and deploy them with this version.");
    }

    private async Task Analyze(LibraryChange library)
    {
        try
        {
            library.BaseBranch = await git.DefaultBranch(library.Worktree);
            (library.Commit, library.Changes, library.Fingerprint, library.Baseline) = await git.Inspect(library.Target, library.BaseBranch);
            var compiled = library.Changes.Where(change => Planner.IsCompiledInput(change) && !Planner.IsExcluded(change.Path)).ToList();
            if (compiled.Count == 0)
            {
                library.Notes.Add($"No changed library code compared with {library.BaseBranch}.");
                return;
            }
            var projects = (await git.Git(library.Worktree, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.csproj"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('/', '\\')).ToList();
            var owners = new SortedSet<string>(Files.Comparer);
            var unowned = 0;
            foreach (var change in compiled)
            {
                if (Owner(projects, change.Path.Replace('/', '\\')) is { } owner) owners.Add(owner);
                else unowned++;
            }
            foreach (var path in owners) library.Projects.Add(await Project(library, path));
            if (unowned != 0)
                library.Notes.Add($"{unowned} changed build file(s) belong to no library project; they still apply when a changed project builds.");
            var inputs = new StringBuilder($"library\0{library.Worktree}\0{library.Commit}\0{library.Baseline}\0");
            foreach (var change in compiled)
                inputs.Append(change.Path + "\0" + (change.Deleted ? "(deleted)" : Files.Hash(Files.Child(library.Worktree, change.Path))) + "\0");
            foreach (var project in library.Projects.Where(project => project.Reason is null)) inputs.Append(project.RelativePath + "\0" + project.TargetFramework + "\0");
            library.BuildInputs = inputs.ToString();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or XmlException or UnauthorizedAccessException)
        {
            library.Error = exception.Message.Split('\n')[0];
            library.Projects.Clear();
        }
    }

    // An SDK-style project compiles the source files under its folder, so the nearest project folder owns a changed file.
    private static string? Owner(List<string> projects, string path) =>
        projects.FirstOrDefault(project => Files.Comparer.Equals(project, path)) ??
        (SourceExtensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).Equals("packages.config", StringComparison.OrdinalIgnoreCase) ? projects
            .Where(project => Path.GetDirectoryName(project) is var folder && (folder!.Length == 0 || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(project => Path.GetDirectoryName(project)!.Length).FirstOrDefault() : null);

    private async Task<LibraryProject> Project(LibraryChange library, string path)
    {
        var full = Files.Child(library.Worktree, path);
        if (!File.Exists(full)) return new() { RelativePath = path, Assembly = Path.GetFileNameWithoutExtension(path), Reason = "was deleted" };
        var current = XDocument.Load(full);
        var gitPath = path.Replace('\\', '/');
        var baseline = (await git.Git(library.Worktree, "ls-tree", "--name-only", library.Baseline, "--", ":(literal)" + gitPath)).Length == 0 ? null :
            XDocument.Parse((await git.Git(library.Worktree, "show", library.Baseline + ":" + gitPath)).TrimStart('\uFEFF'));
        var assembly = Value(current, "AssemblyName") ?? Path.GetFileNameWithoutExtension(path);
        var frameworks = (Value(current, "TargetFrameworks") ?? Value(current, "TargetFramework") ?? "")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var framework = SiteFramework(frameworks);
        var sdk = current.Root?.Attribute("Sdk") is not null || current.Root?.Elements().Any(element => element.Name.LocalName == "Sdk") == true;
        var (reachable, farmFile, farmVersion) = Webfarm(assembly);
        var version = Version(current);
        var baselineVersion = baseline is null ? "" : Version(baseline);
        var reason = !sdk ? "is not an SDK-style project; Cultivate builds SDK-style library projects only" :
            framework is null ? $"targets {(frameworks.Length == 0 ? "no framework" : string.Join(", ", frameworks))}; the site loads only a .NET Framework or netstandard2.0 build" :
            !reachable ? $"the webfarm could not be read to confirm it already has bin\\{assembly}.dll" :
            !farmFile ? $"the webfarm has no bin\\{assembly}.dll; Cultivate replaces library DLLs the site already has and never adds one" : null;
        if (reason is null)
        {
            if (NumericVersion(farmVersion) is { } farm && NumericVersion(baselineVersion) is { } start && farm > start)
                library.Warnings.Add($"The webfarm has {assembly} {farmVersion}, newer than {baselineVersion} this branch started from. " +
                    "The branch may lack members that other deployed DLLs use.");
            if (baseline is not null && References(current) is var now && References(baseline) is var before && !now.SetEquals(before))
                library.Warnings.Add($"{assembly}'s package or project references differ from {library.BaseBranch} " +
                    $"({string.Join(", ", now.Except(before).Select(item => "+" + item).Concat(before.Except(now).Select(item => "-" + item)))}). " +
                    $"The webfarm may lack those DLLs: Cultivate deploys only {assembly}.dll.");
        }
        return new()
        {
            RelativePath = path, Assembly = assembly, TargetFramework = framework ?? "", Version = version,
            BaselineVersion = baselineVersion, WebfarmVersion = farmVersion, Reason = reason
        };
    }

    private (bool Reachable, bool Exists, string? Version) Webfarm(string assembly)
    {
        try
        {
            var file = Path.Combine(settings.WebfarmRoot, "bin", assembly + ".dll");
            if (!File.Exists(file)) return (Directory.Exists(settings.WebfarmRoot), false, null);
            string? version = null;
            try { version = AssemblyName.GetAssemblyName(file).Version?.ToString(); }
            catch (BadImageFormatException) { }
            return (true, true, version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (false, false, null); }
    }

    private static string? Value(XDocument document, string name) => document.Descendants()
        .FirstOrDefault(element => element.Name.LocalName == name && !element.HasElements && element.Value.Trim().Length != 0)?.Value.Trim();

    private static string Version(XDocument document) => Value(document, "Version") ??
        (Value(document, "VersionPrefix") is { } prefix ? prefix + (Value(document, "VersionSuffix") is { } suffix ? "-" + suffix : "") : "");

    internal static Version? NumericVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || !System.Version.TryParse(value.Split('-', '+')[0], out var version)) return null;
        return new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    private static HashSet<string> References(XDocument document) => document.Descendants()
        .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference")
        .Select(element => element.Name.LocalName == "ProjectReference"
            ? Path.GetFileNameWithoutExtension(element.Attribute("Include")?.Value ?? "") + " (project)"
            : $"{element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value} " +
              $"{element.Attribute("Version")?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value}".Trim())
        .ToHashSet(Files.Comparer);

    /// <summary>The target framework whose build the site can load: the newest .NET Framework one, else netstandard2.0, else none.</summary>
    internal static string? SiteFramework(IEnumerable<string> frameworks)
    {
        var list = frameworks.ToList();
        return list.Where(framework => FrameworkName().IsMatch(framework))
            .OrderByDescending(framework => framework[3..].PadRight(3, '0'), StringComparer.Ordinal).FirstOrDefault()
            ?? list.FirstOrDefault(framework => framework.Equals("netstandard2.0", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds each included library's deployable projects in a throwaway snapshot of the library worktree, never packing them, and
    /// stages their DLLs. The DLLs also go to <c>Runs\&lt;run&gt;\libraries</c>, which the run's MyMeyer builds compile against.
    /// </summary>
    public async Task<List<DeployFile>> Build(Plan plan, string snapshot, string run, string seeds, Action<string> progress)
    {
        var files = new List<DeployFile>();
        var environment = new Dictionary<string, string> { ["WebfarmPath"] = Path.Combine(run, "intercepted") + "\\" };
        var log = Path.Combine(run, "build.log");
        var index = 0;
        foreach (var library in plan.Libraries.Where(library => library.Deployed.Any()))
        {
            var source = Path.Combine(run, "L" + index++);
            progress($"Snapshotting library {library.Repository} (your library checkout stays untouched)");
            await git.Git(library.Worktree, "worktree", "add", "--detach", source, library.Commit);
            try
            {
                await git.Overlay(library.Worktree, library.Commit, source);
                if ((await git.Inspect(library.Target, library.BaseBranch)).Fingerprint != library.Fingerprint)
                    throw new InvalidOperationException("Library files changed while taking the snapshot. Preview again; nothing has been deployed.");
                foreach (var project in library.Deployed)
                {
                    var full = Files.Child(source, project.RelativePath);
                    var binaryLog = Path.Combine(run, project.Assembly + ".library.binlog");
                    var context = new FailureContext("Library restore") { Project = library.Repository + "\\" + project.RelativePath, Snapshot = source, Log = log };
                    progress($"Restoring library {project.Assembly}");
                    await Commands.Run(settings.MSBuild, [full, "-nologo", "-t:Restore", "-p:Configuration=Debug", "-v:minimal",
                        $"-bl:{Path.Combine(run, project.Assembly + ".library-restore.binlog")}"], source, environment, Path.Combine(run, "restore.log"), context);
                    // Debug, the framework the site loads, and no package: the library is tested before anything is published.
                    string[] properties = ["-p:Configuration=Debug", $"-p:TargetFramework={project.TargetFramework}", "-p:GeneratePackageOnBuild=false"];
                    var targetPath = Files.Full((await Commands.Run(settings.MSBuild, [full, "-nologo", .. properties, "-getProperty:TargetPath"],
                        source, environment, log, context with { Stage = "Library evaluation" })).Trim());
                    Files.Child(source, Path.GetRelativePath(source, targetPath));
                    progress($"Building library {project.Assembly} for {project.TargetFramework}");
                    await Commands.Run(settings.MSBuild, [full, "-nologo", "-t:Build", .. properties, "-m:1", "-v:minimal", $"-bl:{binaryLog}"],
                        source, environment, log, context with { Stage = "Library build", BinaryLog = binaryLog });
                    if (!File.Exists(targetPath)) throw new FileNotFoundException("The library build succeeded without its expected assembly.", targetPath);
                    var identity = AssemblyName.GetAssemblyName(targetPath);
                    if (!Files.Comparer.Equals(identity.Name, project.Assembly))
                        throw new InvalidDataException($"Unexpected assembly identity for {targetPath}.");
                    project.BuiltVersion = identity.Version?.ToString() ?? "";
                    var built = Path.Combine(run, "libraries", project.Assembly + ".dll");
                    Directory.CreateDirectory(Path.GetDirectoryName(built)!);
                    File.Copy(targetPath, built, true);
                    File.Copy(targetPath, Path.Combine(seeds, project.Assembly + ".dll"), true);
                    var staged = Files.Child(snapshot, Path.Combine(".cultivate-artifacts", "bin", project.Assembly + ".dll"));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.Copy(targetPath, staged, true);
                    files.Add(new(staged, @"bin\" + project.Assembly + ".dll"));
                    File.AppendAllText(log, $"Library {project.Assembly} {project.BuiltVersion} ({project.TargetFramework}): built from {library.Worktree}\n");
                }
            }
            finally
            {
                await git.Git(library.Worktree, "worktree", "remove", "--force", source);
            }
        }
        return files;
    }

    /// <summary>Stops a MyMeyer DLL that was compiled against another version of a library assembly this run built.</summary>
    internal static void VerifyReferences(string assembly, Plan plan)
    {
        var built = plan.Libraries.SelectMany(library => library.Deployed).Where(project => project.BuiltVersion.Length != 0)
            .ToDictionary(project => project.Assembly, project => project.BuiltVersion, Files.Comparer);
        if (built.Count == 0) return;
        using var stream = File.OpenRead(assembly);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (built.TryGetValue(name, out var version) && reference.Version.ToString() != version)
                throw new InvalidDataException($"{Path.GetFileName(assembly)} was compiled against {name} {reference.Version}, not the library build {version}. Nothing was deployed.");
        }
    }

    /// <summary>The preview card listing each matching library worktree, what it would deploy, and why a changed project would not.</summary>
    internal static IRenderable Card(Plan plan)
    {
        var lines = new List<string>();
        foreach (var library in plan.Libraries)
        {
            lines.Add($"[{(library.Included ? TerminalStyle.Accent : TerminalStyle.Muted)}]{(library.Included ? "INCLUDED " : "LEFT OUT  ")}[/] " +
                $"[bold]{Terminal.Escape(library.Repository)}[/]  {Terminal.Escape(library.Branch)}  [{TerminalStyle.Muted}]{Terminal.Escape(library.Worktree)}[/]");
            if (library.Error is not null) lines.Add($"    [red]{Terminal.Escape(library.Error)}[/]");
            foreach (var project in library.Projects)
                lines.Add(project.Reason is null
                    ? $"    {Terminal.Escape(project.Assembly)}.dll  [{TerminalStyle.Muted}]{Terminal.Escape(project.TargetFramework)}  " +
                      $"builds {Terminal.Escape(project.Version.Length == 0 ? "the project's version" : project.Version)}  /  " +
                      $"webfarm has {Terminal.Escape(project.WebfarmVersion ?? "an unreadable version")}[/]"
                    : $"    [{TerminalStyle.Muted}]{Terminal.Escape(project.Assembly)}.dll not deployed: {Terminal.Escape(project.Reason)}[/]");
            foreach (var warning in library.Warnings) lines.Add($"    [{TerminalStyle.Gold}]{Terminal.Escape(warning)}[/]");
            foreach (var note in library.Notes) lines.Add($"    [{TerminalStyle.Muted}]{Terminal.Escape(note)}[/]");
        }
        return TerminalStyle.Card(new Markup(string.Join("\n", lines)), "MATCHING LIBRARY CHANGES");
    }

    /// <summary>Asks which matching library worktrees to include and returns their paths.</summary>
    internal static List<string> Choose(Plan plan)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(TerminalStyle.Header("SELECT LIBRARY CHANGES", "Checked library worktrees build and deploy with this version."));
        var prompt = new MultiSelectionPrompt<LibraryChange>().Title($"[{TerminalStyle.Accent}]Choose library changes[/]")
            .NotRequired().PageSize(10).HighlightStyle(TerminalStyle.Selection)
            .InstructionsText("[grey](Space toggles a library worktree; Enter applies the selection)[/]")
            .UseConverter(library => Terminal.Escape($"{library.Repository}  {library.Branch}  {library.Worktree}")).AddChoices(plan.Libraries);
        foreach (var library in plan.Libraries.Where(library => library.Included)) prompt.Select(library);
        return AnsiConsole.Prompt(prompt).Select(library => library.Worktree).ToList();
    }

    [GeneratedRegex(@"^net\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkName();
}
