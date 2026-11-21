using System.Text.RegularExpressions;

namespace Cultivate;

/// <summary>
/// Pulls one repository's branch to a commit fetched from origin, as <c>git pull --ff-only</c> would, without ever failing the caller: origin's
/// remote-tracking branch moves to the fetched tip, and the local branch follows when Git can fast-forward it without touching local changes;
/// otherwise it stays where it is, with a warning. It never resets, checks out, stashes, switches branches or forces anything.
/// </summary>
/// <param name="root">Any checkout of the repository.</param>
/// <param name="reason">What the reflog entries of moved refs start with, such as <c>cultivate release</c>.</param>
internal sealed class BasePull(string root, GitRepository git, string reason)
{
    private Task<string> G(params string[] arguments) => git.Git(root, arguments);

    internal static bool PullFailed(Exception exception) => exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or
        UnauthorizedAccessException or FormatException;

    internal static ReleasePull Off(string branch, bool built) => new(branch, null, "", "", 0, ReleasePullOutcome.Off, built
        ? $"Left {branch} and origin/{branch} where they were (--no-pull)." : $"Building leaves {branch} and origin/{branch} where they are (--no-pull).");

    /// <summary>The local <paramref name="branch"/>'s commit, the worktree it is checked out in and whether that folder is missing, and the commits
    /// it has that <paramref name="tip"/> lacks and lacks that <paramref name="tip"/> has; null when there is no such local branch.</summary>
    internal async Task<(string Commit, string? Worktree, bool Missing, int Ahead, int Behind)?> Local(string branch, string tip)
    {
        var commit = await G("for-each-ref", "--format=%(objectname)", "refs/heads/" + branch);
        if (commit.Length == 0) return null;
        var fields = (await G("worktree", "list", "--porcelain", "-z")).Split("\0\0", StringSplitOptions.RemoveEmptyEntries).Select(block => block.Split('\0'))
            .FirstOrDefault(block => block.Contains("branch refs/heads/" + branch));
        var worktree = fields?.FirstOrDefault(field => field.StartsWith("worktree ", StringComparison.Ordinal))?[9..].Replace('/', '\\');
        var missing = fields is not null && (worktree is null || !Directory.Exists(worktree) || fields.Any(field => field.StartsWith("prunable", StringComparison.Ordinal)));
        var counts = (await G("rev-list", "--left-right", "--count", $"{commit}...{tip}")).Split('\t');
        return (commit, worktree, missing, int.Parse(counts[0]), int.Parse(counts[1]));
    }

    /// <summary>What building would do to the local base branch, for the preview; it changes nothing.</summary>
    internal async Task<ReleasePull> Pending(string branch, string tip)
    {
        try
        {
            if (await Local(branch, tip) is not { } at)
                return new(branch, null, "", "", 0, ReleasePullOutcome.NoBranch, $"You have no local {branch} branch, so building pulls nothing.");
            var (commit, worktree, missing, ahead, behind) = at;
            var where = worktree is null ? "" : " in " + worktree;
            ReleasePull Pull(string outcome, int commits, string detail) => new(branch, worktree, commit, commit, commits, outcome, detail);
            return ahead != 0 ? Pull(ReleasePullOutcome.LocalCommits, ahead, $"{branch}{where} has {ahead} local commit(s) not on origin, so building leaves it as it is.")
                : behind == 0 ? Pull(ReleasePullOutcome.UpToDate, 0, $"{branch}{where} is up to date with origin.")
                : missing ? Pull(ReleasePullOutcome.NotUpdated, behind, $"{branch} is {behind} commit(s) behind origin, but it is checked out in {worktree}, " +
                    "which is missing, so building cannot pull it.")
                : Pull(ReleasePullOutcome.Behind, behind, $"{branch}{where} is {behind} commit(s) behind origin; building pulls it.");
        }
        catch (Exception exception) when (PullFailed(exception))
        {
            return new(branch, null, "", "", 0, ReleasePullOutcome.NotUpdated, $"Could not tell where your local {branch} is: {GitProblem(exception)}.");
        }
    }

    /// <summary>
    /// Pulls <paramref name="branch"/> as <c>git pull --ff-only</c> would, without ever throwing for a Git failure: origin's remote-tracking branch
    /// moves to <paramref name="tip"/>, and the local branch follows when it is behind, by a fast-forward merge in the worktree it is checked out
    /// in, or by moving the ref when it is checked out nowhere. Local commits, local files the merge would overwrite, or any Git failure leave it
    /// as it was, with a warning in <paramref name="warnings"/> that goes on with <paramref name="kept"/>, what the caller did regardless.
    /// </summary>
    internal async Task<ReleasePull> PullBase(string branch, string tip, List<string> warnings, Action<string> progress, string kept)
    {
        var to = tip[..7];
        try { await G("update-ref", "-m", $"{reason}: fetch origin {branch}", $"refs/remotes/origin/{branch}", tip); }
        catch (Exception exception) when (PullFailed(exception)) { warnings.Add($"Could not move origin/{branch} to the fetched {to}: {GitProblem(exception)}."); }
        (string Commit, string? Worktree, bool Missing, int Ahead, int Behind)? local = null;
        try
        {
            local = await Local(branch, tip);
            if (local is not { } at)
                return new(branch, null, "", "", 0, ReleasePullOutcome.NoBranch, $"You have no local {branch} branch, so there was nothing to pull.");
            var (commit, worktree, missing, ahead, behind) = at;
            var where = worktree is null ? "" : " in " + worktree;
            ReleasePull Left(string outcome, int commits, string detail, string? warning = null)
            {
                if (warning is not null) warnings.Add(warning);
                return new(branch, worktree, commit, commit, commits, outcome, detail);
            }
            if (ahead != 0)
                return Left(ReleasePullOutcome.LocalCommits, ahead, $"Did not pull {branch}: it has {ahead} local commit(s) not on origin.",
                    $"Your local {branch}{where} has {ahead} commit(s) that are not on origin, so it was not updated to origin's {to}. {kept}; pull {branch} by hand.");
            if (behind == 0) return Left(ReleasePullOutcome.UpToDate, 0, $"{branch}{where} is already up to date with origin ({to}).");
            if (missing)
                return Left(ReleasePullOutcome.NotUpdated, behind, $"Did not pull {branch}: it is checked out in {worktree}, which is missing.",
                    $"{branch} is checked out in {worktree}, which is missing, so it was not pulled. {kept}; run `git worktree prune`, then pull {branch} by hand.");
            try
            {
                if (worktree is null) await G("update-ref", "-m", $"{reason}: fast-forward to origin/{branch}", $"refs/heads/{branch}", tip, commit);
                else
                {
                    progress($"Pulling {branch} in {worktree}");
                    // Git refuses the whole merge, changing nothing, when it would overwrite a local change; local changes it does not touch stay.
                    await git.Git(worktree, "merge", "--ff-only", "--no-autostash", "--quiet", tip);
                }
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("would be overwritten", StringComparison.Ordinal))
            {
                var files = exception.Message.Split('\n').Where(line => line.StartsWith('\t')).Select(line => line.Trim()).ToList();
                var named = string.Join(", ", files.Take(8)) + (files.Count > 8 ? $" and {files.Count - 8} more" : "");
                return Left(ReleasePullOutcome.NotUpdated, behind, $"Did not pull {branch}: it would overwrite local files in {worktree}.",
                    $"{branch} in {worktree} was not pulled, and nothing in it changed: pulling would overwrite your local changes to {named}. {kept}; " +
                    $"commit, stash or undo those changes, then pull {branch} by hand.");
            }
            return new(branch, worktree, commit, tip, behind, ReleasePullOutcome.Pulled,
                $"Pulled {branch}: {behind} new commit(s), {commit[..7]} → {to} ({worktree ?? "not checked out"})");
        }
        catch (Exception exception) when (PullFailed(exception))
        {
            warnings.Add($"Could not pull {branch}{(local?.Worktree is { } path ? " in " + path : "")}: {GitProblem(exception)}. {kept}; pull {branch} by hand.");
            return new(branch, local?.Worktree, local?.Commit ?? "", local?.Commit ?? "", local?.Behind ?? 0, ReleasePullOutcome.NotUpdated,
                $"Did not pull {branch}: {GitProblem(exception)}.");
        }
    }

    /// <summary>
    /// Brings the branch checked out in <paramref name="root"/>, a main worktree, up to date with origin before its solution opens: fetches the
    /// branch from origin, then pulls it with <see cref="PullBase"/>. Never throws for a Git failure: <see cref="CheckoutPull.Detail"/> says in
    /// one line what happened, and <see cref="CheckoutPull.Warning"/> is true when it could not pull, so the developer should read it.
    /// </summary>
    /// <param name="baseBranch">The repository's base branch, when the caller knows it; otherwise master when origin has one, else its default branch.</param>
    internal async Task<CheckoutPull> CheckedOut(string? baseBranch, Action<string> progress)
    {
        var name = Path.GetFileName(Files.Full(root));
        string branch;
        try { branch = await G("symbolic-ref", "--quiet", "--short", "HEAD"); }
        catch (InvalidOperationException) { return new($"{name} is on a detached HEAD, so it was not pulled.", false); }
        var other = await OtherBranch(branch, baseBranch) is { } expected ? $" {name} is on {branch}, not {expected}." : "";
        string tip;
        try
        {
            progress($"Fetching {branch} from origin");
            await G("fetch", "--quiet", "origin", branch);
            tip = await G("rev-parse", "--verify", "FETCH_HEAD^{commit}");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
        {
            return new($"Origin has no {branch} branch, so {name} was not pulled." + other, false);
        }
        catch (Exception exception) when (PullFailed(exception))
        {
            return new($"Could not fetch {branch} from origin: {GitProblem(exception)}. {name} opens as it is." + other, true);
        }
        var warnings = new List<string>();
        var pull = await PullBase(branch, tip, warnings, progress, $"The solution opens on {name} as it is");
        return new((warnings.Count == 0 ? pull.Detail : string.Join(" ", warnings)) + other, warnings.Count != 0, pull);
    }

    /// <summary>The base branch when <paramref name="branch"/> is not it, or null when it is or no base branch can be told.</summary>
    private async Task<string?> OtherBranch(string branch, string? baseBranch)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseBranch))
            {
                try { await G("rev-parse", "--verify", "--quiet", "refs/remotes/origin/master^{commit}"); baseBranch = "master"; }
                catch (InvalidOperationException) { baseBranch = await git.DefaultBranch(root); }
            }
            baseBranch = baseBranch.StartsWith("origin/", StringComparison.Ordinal) ? baseBranch["origin/".Length..] : baseBranch;
            return baseBranch == branch ? null : baseBranch;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The line of a Git failure that says what went wrong, without Git's exit code or its error: prefix.</summary>
    internal static string GitProblem(Exception exception)
    {
        var lines = exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(exception.Message.StartsWith("git.exe failed", StringComparison.Ordinal) ? 1 : 0).ToList();
        var line = lines.FirstOrDefault(text => text.StartsWith("fatal: ", StringComparison.Ordinal) || text.StartsWith("error: ", StringComparison.Ordinal)) ??
            lines.FirstOrDefault() ?? exception.Message;
        return Regex.Replace(line, "^(fatal|error): ", "").TrimEnd('.');
    }
}

/// <summary>What <see cref="BasePull.CheckedOut"/> did: one line to show, whether it is a warning, and the pull when it got that far.</summary>
internal sealed record CheckoutPull(string Detail, bool Warning, ReleasePull? Pull = null);
