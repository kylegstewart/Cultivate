using System.Text.RegularExpressions;

namespace Cultivate;

/// <summary>
/// A local clone release files can be built from: its GitHub repository name (the folder name when origin is not on GitHub), its main
/// checkout, and the branch its releases are built from. <see cref="Main"/> is the configured MyMeyer checkout.
/// </summary>
internal sealed record ReleaseRepository(string Name, string? GitHub, string Root, string Branch, bool Main)
{
    /// <summary>"owner/name", or the checkout when origin is not on GitHub.</summary>
    public string Label => GitHub ?? Root;
}

/// <summary>A GitHub pull request named by its repository ("owner/name") and number.</summary>
internal sealed record PullRequestLink(string GitHub, int Number)
{
    public string Url => $"https://github.com/{GitHub}/pull/{Number}";
    public string Key => $"{GitHub.ToLowerInvariant()}#{Number}";
}

/// <summary>
/// Finds local clones by their GitHub repository: the MyMeyer checkout, the Git checkouts directly beside it, and the library repositories.
/// Each one's origin is read from its <c>.git\config</c> file, so listing about forty clones starts no process. Only main checkouts count.
/// </summary>
internal static partial class ReleaseRepositories
{
    /// <summary>"owner/name" from an https, ssh or scp-style GitHub URL, with or without .git; null for anything else.</summary>
    internal static string? GitHub(string? url)
    {
        if (url is null || GitHubUrl().Match(url.Trim()) is not { Success: true } match) return null;
        return match.Groups["owner"].Value + "/" + match.Groups["name"].Value;
    }

    /// <summary>A pull request URL such as https://github.com/owner/name/pull/12 (anything after the number is ignored), or null.</summary>
    internal static PullRequestLink? Link(string? text) =>
        text is not null && PullUrl().Match(text.Trim()) is { Success: true } match && int.TryParse(match.Groups["number"].Value, out var number) && number > 0
            ? new(match.Groups["owner"].Value + "/" + match.Groups["name"].Value, number) : null;

    /// <summary>Every pull request URL in the body's "Associated PRs" section, in order; none when the body has no such section.</summary>
    internal static List<PullRequestLink> Associated(string? body)
    {
        var links = new List<PullRequestLink>();
        if (body is null) return links;
        var inside = false;
        foreach (var line in body.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith('#'))
            {
                inside = AssociatedHeading().IsMatch(line);
                continue;
            }
            if (!inside) continue;
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }
            foreach (Match match in PullUrl().Matches(line))
                if (Link(match.Value) is { } link && !links.Any(other => other.Key == link.Key)) links.Add(link);
        }
        return links;
    }

    /// <summary>The origin URL a main checkout's <c>.git\config</c> names, or null when it has none or is not a main checkout.</summary>
    internal static string? Origin(string checkout)
    {
        var config = Path.Combine(checkout, ".git", "config");
        string[] lines;
        try
        {
            if (!File.Exists(config)) return null;
            lines = File.ReadAllLines(config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        var origin = false;
        foreach (var line in lines.Select(line => line.Trim()))
        {
            if (line.StartsWith('['))
            {
                origin = OriginSection().IsMatch(line);
                continue;
            }
            if (origin && ConfigUrl().Match(line) is { Success: true } url) return url.Groups[1].Value.Trim().Trim('"');
        }
        return null;
    }

    /// <summary>The clones to look in, the main checkout first: no process is started and no repository is touched.</summary>
    internal static List<ReleaseRepository> Clones(Settings settings)
    {
        var folders = new List<string>();
        if (settings.RepositoryRoot.Length != 0) folders.Add(Files.Full(settings.RepositoryRoot));
        if (settings.RepositoryRoot.Length != 0 && Path.GetDirectoryName(Files.Full(settings.RepositoryRoot)) is { } parent && Directory.Exists(parent))
        {
            try { folders.AddRange(Directory.EnumerateDirectories(parent).Where(path => Directory.Exists(Path.Combine(path, ".git"))).Select(Files.Full).Order(Files.Comparer)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        folders.AddRange((settings.LibraryRepositories ?? []).Where(path => Directory.Exists(Path.Combine(path, ".git"))).Select(Files.Full));
        return folders.Distinct(Files.Comparer).Select((folder, index) => Describe(settings, folder, index == 0 && settings.RepositoryRoot.Length != 0)).ToList();
    }

    private static ReleaseRepository Describe(Settings settings, string folder, bool main)
    {
        var github = GitHub(Origin(folder));
        return new(github?.Split('/')[1] ?? Path.GetFileName(folder), github, folder, main ? settings.BaseBranch : "", main);
    }

    /// <summary>The configured MyMeyer checkout as a release repository.</summary>
    internal static ReleaseRepository MainCheckout(Settings settings) => Describe(settings, Files.Full(settings.RepositoryRoot), true);

    /// <summary>The clone of a GitHub repository ("owner/name", any case), or null when none is cloned where Cultivate looks.</summary>
    internal static ReleaseRepository? Find(Settings settings, string github) =>
        Clones(settings).FirstOrDefault(clone => clone.GitHub is not null && clone.GitHub.Equals(github, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The clone <c>--repo</c> names: a checkout path, "owner/name", or a repository name. A name matches the GitHub name first, then the
    /// folder's; more than one match is refused.
    /// </summary>
    internal static ReleaseRepository Named(Settings settings, string value)
    {
        var clones = Clones(settings);
        var text = value.Trim().Trim('"');
        if (Path.IsPathFullyQualified(text))
        {
            var full = Files.Full(text);
            return clones.FirstOrDefault(clone => Files.Comparer.Equals(clone.Root, full)) ??
                (Directory.Exists(Path.Combine(full, ".git")) ? Describe(settings, full, false)
                    : throw new InvalidOperationException($"{full} is not the main checkout of a Git repository."));
        }
        if ((GitHub(text) ?? (text.Count(character => character == '/') == 1 ? text.TrimEnd('/') : null)) is { } github)
            return Find(settings, github) ?? throw NotCloned(settings, github);
        var matches = clones.Where(clone => clone.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = clones.Where(clone => Path.GetFileName(clone.Root).Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw NotCloned(settings, text),
            _ => throw new InvalidOperationException($"More than one clone is named {text}: {string.Join(", ", matches.Select(clone => clone.Root))}. Use --repo PATH.")
        };
    }

    /// <summary>Where Cultivate looks for clones, said when a repository is not among them.</summary>
    internal static string Where(Settings settings) => settings.RepositoryRoot.Length == 0 ? "beside your MyMeyer checkout"
        : Path.GetDirectoryName(Files.Full(settings.RepositoryRoot)) ?? settings.RepositoryRoot;

    internal static InvalidOperationException NotCloned(Settings settings, string repository) =>
        new($"{repository} is not cloned in {Where(settings)}. Clone it there to build its release files.");

    /// <summary>
    /// The branch a repository's releases are built from: the configured base branch for the MyMeyer checkout; otherwise master when origin has
    /// one, else origin's default branch. Asks origin, without fetching anything.
    /// </summary>
    internal static async Task<ReleaseRepository> WithBranch(GitRepository git, ReleaseRepository repository)
    {
        if (repository.Branch.Length != 0) return repository;
        string remote;
        try { remote = await git.Git(repository.Root, "ls-remote", "--symref", "origin", "HEAD", "refs/heads/master"); }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Could not ask origin of {repository.Label} for its branches: " +
                exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault() + ". Nothing was fetched or built.");
        }
        var lines = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var branch = lines.Any(line => line.EndsWith("\trefs/heads/master", StringComparison.Ordinal)) ? "master"
            : lines.Select(line => SymbolicHead().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value
              ?? throw new InvalidOperationException($"Origin of {repository.Label} has no master branch and names no default branch. Nothing was fetched or built.");
        return repository with { Branch = branch };
    }

    /// <summary>The settings a release in <paramref name="repository"/> runs with: its checkout and release branch, and no library repositories.</summary>
    internal static Settings For(Settings settings, ReleaseRepository repository) => new()
    {
        RepositoryRoot = repository.Root, BaseBranch = repository.Branch, WebfarmRoot = settings.WebfarmRoot, MSBuild = settings.MSBuild,
        MappingAssembly = settings.MappingAssembly, StateRoot = settings.StateRoot, WorktreesRoot = settings.WorktreesRoot, ReleaseRoot = settings.ReleaseRoot,
        SqlMirrorRoot = settings.SqlMirrorRoot, TicketUrl = settings.TicketUrl, LibraryRepositories = [], CleanupDays = settings.CleanupDays
    };

    [GeneratedRegex(@"^(?:https?://(?:[^@/]+@)?github\.com/|ssh://(?:[^@/]+@)?github\.com(?::\d+)?/|[^@/\s]+@github\.com:)(?<owner>[A-Za-z0-9_.-]+)/(?<name>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();

    [GeneratedRegex(@"https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<name>[A-Za-z0-9_.-]+)/pull/(?<number>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PullUrl();

    [GeneratedRegex(@"^#+\s*Associated\s+PRs?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AssociatedHeading();

    [GeneratedRegex(@"^\[\s*remote\s+""origin""\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginSection();

    [GeneratedRegex(@"^url\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigUrl();

    [GeneratedRegex(@"^ref:\s*refs/heads/(\S+)\s+HEAD$")]
    private static partial Regex SymbolicHead();
}
