using System.Text.RegularExpressions;

namespace Cultivate;

/// <summary>One checkout of a repository: its primary clone or a linked worktree.</summary>
internal sealed record Checkout(string Repository, string RepositoryRoot, string Path, string Branch, string Head, bool Primary, bool Detached, bool Locked)
{
    /// <summary>The ticket a branch belongs to: 12345 for both 12345 and 12345-2, or null for other names.</summary>
    public string? Ticket => WorktreeCatalog.TicketOf(Branch);

    public Target ToTarget() => new(Detached ? $"(detached {Head[..Math.Min(7, Head.Length)]})" : Branch, Path, Detached ? Head : "refs/heads/" + Branch);
}

/// <summary>What a checkout holds that exists nowhere else: saved changes that are not committed, and commits on no remote.</summary>
internal sealed record CheckoutStatus(int Uncommitted, int Unpushed, DateTimeOffset? LastCommit, string Subject)
{
    public bool Clean => Uncommitted == 0 && Unpushed == 0;
}

/// <summary>Finds every checkout of the repositories a developer works in, whichever folder its worktrees live in.</summary>
internal static class WorktreeCatalog
{
    public static string? TicketOf(string branch) =>
        Regex.Match(branch, @"^(\d{3,})(-\d+)?$") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Reads a checkout's state without taking Git's optional locks, so it never blocks work going on in that checkout.</summary>
    public static async Task<CheckoutStatus> Status(GitRepository git, Checkout checkout)
    {
        // With core.fsmonitor on, status would start a file-watching daemon in every checkout it reads, and that daemon keeps the folder in use.
        var changes = await git.Git(checkout.Path, "-c", "core.fsmonitor=false", "--no-optional-locks", "status", "--porcelain", "--no-renames", "-z");
        var unpushed = await git.Git(checkout.Path, "rev-list", "--count", "HEAD", "--not", "--remotes");
        var commit = (await git.Git(checkout.Path, "log", "-1", "--format=%cI%x00%s")).Split('\0', 2);
        return new(changes.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length, int.Parse(unpushed),
            DateTimeOffset.TryParse(commit[0], out var date) ? date : null, commit.Length > 1 ? commit[1] : "");
    }

    /// <summary>
    /// Repositories are found from the MyMeyer checkout, the clones beside it, and the checkouts under the worktrees folder.
    /// Each repository then lists its own worktrees, so one that lives in another folder is still included.
    /// </summary>
    public static async Task<List<Checkout>> Discover(Settings settings, GitRepository git)
    {
        var seeds = new List<string>();
        if (Directory.Exists(settings.RepositoryRoot))
        {
            seeds.Add(Files.Full(settings.RepositoryRoot));
            if (Path.GetDirectoryName(Files.Full(settings.RepositoryRoot)) is { } clones) Find(clones, 1, seeds);
        }
        if (settings.WorktreesRoot.Length != 0 && Directory.Exists(settings.WorktreesRoot)) Find(Files.Full(settings.WorktreesRoot), 3, seeds);
        var runs = Path.Combine(Files.Home, "Runs") + "\\";
        // Starting Git costs far more than reading .git, and a hundred worktrees often share a handful of repositories.
        var listings = await Task.WhenAll(seeds.Distinct(Files.Comparer).Select(seed => (Seed: seed, Common: CommonDirectory(seed)))
            .DistinctBy(item => item.Common ?? item.Seed, Files.Comparer).Select(async item =>
        {
            var seed = item.Seed;
            try
            {
                var common = item.Common ?? Files.Full(await git.Git(seed, "rev-parse", "--path-format=absolute", "--git-common-dir"));
                return (Common: common, Listing: await git.Git(seed, "worktree", "list", "--porcelain", "-z"));
            }
            catch (InvalidOperationException) { return (Common: "", Listing: ""); } // Not a usable repository.
        }));
        var result = new List<Checkout>();
        foreach (var repository in listings.Where(item => item.Common.Length != 0).DistinctBy(item => item.Common, Files.Comparer))
        {
            var blocks = repository.Listing.Split("\0\0", StringSplitOptions.RemoveEmptyEntries).Select(block => block.Split('\0')).ToList();
            string? Field(string[] fields, string name) => fields.FirstOrDefault(field => field.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];
            // Git lists the primary checkout first. A bare repository has no checkout to name its worktrees after.
            if (blocks.Count == 0 || blocks[0].Contains("bare") || Field(blocks[0], "worktree") is not { } primary) continue;
            primary = Files.Full(primary);
            foreach (var fields in blocks)
            {
                if (Field(fields, "worktree") is not { } path || Field(fields, "HEAD") is not { } head) continue;
                path = Files.Full(path);
                if (!Directory.Exists(path) || path.StartsWith(runs, StringComparison.OrdinalIgnoreCase)) continue;
                var reference = Field(fields, "branch");
                result.Add(new(Path.GetFileName(primary), primary, path,
                    reference is null ? "" : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference,
                    head, Files.Comparer.Equals(path, primary), reference is null,
                    fields.Any(field => field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))));
            }
        }
        return result.OrderBy(checkout => checkout.Repository, Files.Comparer).ThenBy(checkout => checkout.Path, Files.Comparer).ToList();
    }

    /// <summary>The repository folder a checkout's .git points to, read without Git, or null when it cannot be read.</summary>
    internal static string? CommonDirectory(string checkout)
    {
        var dotGit = Path.Combine(checkout, ".git");
        try
        {
            if (Directory.Exists(dotGit)) return Files.Full(dotGit);
            var line = File.ReadLines(dotGit).FirstOrDefault();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
            var gitDirectory = Path.GetFullPath(Path.Combine(checkout, line["gitdir:".Length..].Trim()));
            var common = Path.Combine(gitDirectory, "commondir");
            return Files.Full(File.Exists(common) ? Path.Combine(gitDirectory, File.ReadAllText(common).Trim()) : gitDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static void Find(string folder, int depth, List<string> found)
    {
        if (Path.Exists(Path.Combine(folder, ".git")))
        {
            found.Add(folder);
            return;
        }
        if (depth == 0) return;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(folder)) Find(child, depth - 1, found);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }
}
