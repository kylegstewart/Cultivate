using System.Text.Json;

namespace Cultivate;

internal enum TicketChecks { None, Passing, Failing, Pending }

internal sealed record TicketPullRequest(int Number, string Title, string State, bool IsDraft, string Url, TicketChecks Checks)
{
    /// <summary>"#2763 draft, checks passing", "#101 merged": checks matter only while the pull request is open.</summary>
    public string Summary
    {
        get
        {
            var state = State switch { "MERGED" => "merged", "CLOSED" => "closed", _ => IsDraft ? "draft" : "open" };
            var checks = State is "MERGED" or "CLOSED" ? TicketChecks.None : Checks;
            return $"#{Number} {state}" + (checks == TicketChecks.None ? "" : ", checks " + checks.ToString().ToLowerInvariant());
        }
    }
}

/// <summary>Pull requests from the GitHub CLI. They are optional: any failure means there is no pull request data.</summary>
internal static class TicketPullRequests
{
    private static readonly HashSet<string> FailedConclusions = new(
        ["FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR"], StringComparer.Ordinal);

    /// <summary>Runs <c>gh pr list</c> for the checkout's branch in the checkout's folder.</summary>
    internal static Task<string> GitHub(Checkout checkout) =>
        Commands.Run("gh.exe", ["pr", "list", "--head", checkout.Branch, "--state", "all",
            "--json", "number,title,state,isDraft,url,statusCheckRollup", "--limit", "5"], checkout.Path,
            new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["NO_COLOR"] = "1" }).WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>The checkout's pull requests, or none when the lookup fails for any reason.</summary>
    internal static async Task<List<TicketPullRequest>> Find(Func<Checkout, Task<string>> lookup, Checkout checkout)
    {
        try { return Parse(await lookup(checkout)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return []; }
    }

    internal static List<TicketPullRequest> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        static string Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => new TicketPullRequest(
            item.TryGetProperty("number", out var number) && number.TryGetInt32(out var value) ? value : 0,
            Text(item, "title"), Text(item, "state").ToUpperInvariant(),
            item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True, Text(item, "url"),
            item.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array ? Checks(rollup) : TicketChecks.None))
            .ToList();
    }

    /// <summary>Check runs report a status and conclusion; older commit statuses report one state.</summary>
    private static TicketChecks Checks(JsonElement rollup)
    {
        var any = false;
        var pending = false;
        foreach (var check in rollup.EnumerateArray().Where(check => check.ValueKind == JsonValueKind.Object))
        {
            any = true;
            var status = check.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
            var conclusion = check.TryGetProperty("conclusion", out value) ? value.GetString() ?? "" : "";
            var state = check.TryGetProperty("state", out value) ? value.GetString() ?? "" : "";
            if (FailedConclusions.Contains(conclusion) || FailedConclusions.Contains(state)) return TicketChecks.Failing;
            if (state is "PENDING" or "EXPECTED" || status.Length != 0 && status != "COMPLETED") pending = true;
        }
        return !any ? TicketChecks.None : pending ? TicketChecks.Pending : TicketChecks.Passing;
    }

    /// <summary>The pull request a checkout's row shows: an open one first, then the newest.</summary>
    internal static TicketPullRequest? Best(IEnumerable<TicketPullRequest> pullRequests) =>
        pullRequests.OrderBy(pullRequest => pullRequest.State switch { "OPEN" => 0, "MERGED" => 1, _ => 2 }).FirstOrDefault();
}
