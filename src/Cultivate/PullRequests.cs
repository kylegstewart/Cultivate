using System.Text.Json;

namespace Cultivate;

/// <summary>A MyMeyer pull request. Only <see cref="Number"/> is certain: the rest comes from the GitHub CLI when it answers.</summary>
internal sealed record PullRequest(int Number, string Title = "", string Author = "", string HeadBranch = "", string BaseBranch = "",
    bool IsDraft = false, string State = "", string Commit = "", string? Problem = null)
{
    public string Label => Title.Length == 0 ? $"PR #{Number}" : $"PR #{Number}: {Title}";

    /// <summary>"#7 by alice  |  alice/fix into master  |  head 1a2b3c4".</summary>
    public string Source => string.Join("  |  ", new[]
    {
        $"#{Number}" + (Author.Length == 0 ? "" : " by " + Author),
        HeadBranch.Length == 0 ? "" : $"{HeadBranch} into {BaseBranch}",
        Commit.Length == 0 ? "" : "head " + Commit[..Math.Min(7, Commit.Length)]
    }.Where(part => part.Length != 0));

    public string Status => State switch { "MERGED" => "merged", "CLOSED" => "closed", "" => "", _ => IsDraft ? "draft" : "open" };
}

/// <summary>
/// Reviews a pull request without a branch or worktree: its head is fetched into a private ref beside the base it is compared with,
/// and deploys like any committed branch. Only the active deployment's pull request and the one being previewed keep their refs.
/// </summary>
internal static class PullRequests
{
    internal const string RefPrefix = "refs/cultivate/pr/";
    private const string Fields = "number,title,author,headRefName,baseRefName,isDraft,state";

    internal static string HeadRef(int number) => $"{RefPrefix}{number}/head";
    private static string BaseRef(int number) => $"{RefPrefix}{number}/base";

    /// <summary>The base a fetched pull request is compared with, or null for any other ref.</summary>
    internal static string? Base(string reference) => reference.StartsWith(RefPrefix, StringComparison.Ordinal) && reference.EndsWith("/head", StringComparison.Ordinal)
        ? reference[..^"head".Length] + "base" : null;

    private static int? NumberOf(string? reference) => reference is not null && reference.StartsWith(RefPrefix, StringComparison.Ordinal) &&
        int.TryParse(reference[RefPrefix.Length..].Split('/')[0], out var number) ? number : null;

    /// <summary>"7", "#7", or a pull request URL ending in its number.</summary>
    internal static int Number(string value) =>
        int.TryParse(value.Trim().TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out var number) && number > 0
            ? number : throw new InvalidOperationException($"'{value}' is not a pull request number. Use --pr 1234.");

    internal static Func<string[], Task<string>> GitHub(Settings settings) => arguments =>
        Commands.Run("gh.exe", arguments, settings.RepositoryRoot,
            new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["NO_COLOR"] = "1" }).WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>What the GitHub CLI could not provide, said so the user knows what to install or sign in to.</summary>
    internal static string Problem(Exception exception) => exception switch
    {
        System.ComponentModel.Win32Exception => "The GitHub CLI (gh) is not installed",
        TimeoutException => "The GitHub CLI did not answer within 30 seconds",
        _ when exception.Message.Contains("auth login", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("not logged in", StringComparison.OrdinalIgnoreCase) => "The GitHub CLI is not signed in (run gh auth login)",
        _ => "The GitHub CLI failed: " + exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.StartsWith("gh.exe failed", StringComparison.Ordinal))
    };

    /// <summary>The repository's open pull requests, or none and why.</summary>
    internal static async Task<(List<PullRequest> Open, string? Problem)> Open(Func<string[], Task<string>> gh)
    {
        try { return (Parse(await gh(["pr", "list", "--state", "open", "--limit", "200", "--json", Fields])), null); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return ([], Problem(exception)); }
    }

    internal static List<PullRequest> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
        static string Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        return items.Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("number", out var number) && number.TryGetInt32(out _))
            .Select(item => new PullRequest(item.GetProperty("number").GetInt32(), Text(item, "title"),
                item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Text(author, "login") : "",
                Text(item, "headRefName"), Text(item, "baseRefName"),
                item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True, Text(item, "state").ToUpperInvariant()))
            .OrderByDescending(request => request.Number).ToList();
    }

    /// <summary>
    /// Fetches the pull request's current head, and the branch it targets, from origin into private refs, and returns the version to plan.
    /// No branch, worktree, or HEAD changes. Without the GitHub CLI it is compared with the configured base branch.
    /// </summary>
    internal static async Task<Target> Fetch(Settings settings, GitRepository git, int number, DeploymentState? active,
        Func<string[], Task<string>>? gh = null)
    {
        PullRequest request;
        try
        {
            request = Parse(await (gh ?? GitHub(settings))(["pr", "view", number.ToString(), "--json", Fields])).FirstOrDefault(item => item.Number == number)
                ?? new(number, Problem: "The GitHub CLI returned no details");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { request = new(number, Problem: Problem(exception)); }
        var compared = request.BaseBranch.Length == 0 ? settings.BaseBranch : request.BaseBranch;
        await Prune(git, settings, active, number);
        try
        {
            // An empty --refmap stops Git from also moving origin's remote-tracking branches.
            await git.Git(settings.RepositoryRoot, "fetch", "--quiet", "--no-tags", "--refmap=", "origin",
                $"+refs/pull/{number}/head:{HeadRef(number)}", $"+refs/heads/{compared}:{BaseRef(number)}");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains($"refs/pull/{number}/head", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pull request #{number} was not found on origin. Check the number; nothing was fetched.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains($"refs/heads/{compared}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pull request #{number} is compared with {compared}, which origin does not have, so its changes " +
                "cannot be worked out safely. Nothing was fetched.");
        }
        request = request with { Commit = await git.Git(settings.RepositoryRoot, "rev-parse", HeadRef(number)), BaseBranch = compared };
        return new(request.Label, null, HeadRef(number), PullRequest: request);
    }

    /// <summary>What the preview says about the pull request beyond its changed files.</summary>
    internal static List<string> Notes(PullRequest request, Settings settings)
    {
        var notes = new List<string> { $"Pull request {request.Source}. Fetched from origin; no branch or worktree was created." };
        if (request.State is "MERGED" or "CLOSED")
            notes.Add($"This pull request is {request.Status}. Its last head is deployed as it was, compared with {request.BaseBranch} as it is now.");
        if (request.Problem is not null)
            notes.Add($"{request.Problem}, so the title, author, and state are unknown and it is compared with {request.BaseBranch}.");
        else if (request.BaseBranch != settings.BaseBranch)
            notes.Add($"Compared with {request.BaseBranch}, the branch it targets, instead of {settings.BaseBranch}.");
        return notes;
    }

    /// <summary>Deletes fetched pull request refs other than the active deployment's and <paramref name="keep"/>. Failures leave them for next time.</summary>
    internal static async Task Prune(GitRepository git, Settings settings, DeploymentState? active, int? keep = null)
    {
        var live = active?.Active == true ? NumberOf(active.SourceRef) : null;
        try
        {
            var references = await git.Git(settings.RepositoryRoot, "for-each-ref", "--format=%(refname)", RefPrefix);
            foreach (var reference in references.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (NumberOf(reference) is not { } number || number != keep && number != live)
                    await git.Git(settings.RepositoryRoot, "update-ref", "-d", reference);
        }
        catch (InvalidOperationException) { }
    }
}
