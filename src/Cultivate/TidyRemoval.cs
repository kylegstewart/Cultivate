using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>A selected worktree as the review shows it: its state read just now, or why it could not be read.</summary>
internal sealed record TidyCandidate(Checkout Checkout, CheckoutStatus? Status, long? Bytes, string? Error = null)
{
    public string Name => TidyRow.NameOf(Checkout);
}

/// <summary>A worktree the developer confirmed deleting, with the state they confirmed; <see cref="Force"/> only when they accepted losing its work.</summary>
internal sealed record TidyTarget(Checkout Checkout, CheckoutStatus Confirmed, bool Force, long? Bytes);

internal sealed record TidyResult(Checkout Checkout, bool Removed, long? Bytes, string Message);

/// <summary>The selection split into clean worktrees and those holding work that exists nowhere else.</summary>
internal sealed class TidyReview(IEnumerable<TidyCandidate> candidates)
{
    private readonly List<TidyCandidate> all = candidates.ToList();
    public IReadOnlyList<TidyCandidate> Safe => all.Where(candidate => candidate is { Error: null, Status.Clean: true }).ToList();
    public IReadOnlyList<TidyCandidate> Risky => all.Where(candidate => candidate is { Error: null, Status.Clean: false }).ToList();
    public IReadOnlyList<TidyCandidate> Unreadable => all.Where(candidate => candidate.Error is not null || candidate.Status is null).ToList();

    internal static string Total(IEnumerable<TidyCandidate> candidates)
    {
        var list = candidates.ToList();
        var total = TidySizes.Format(list.Sum(candidate => candidate.Bytes ?? 0));
        return list.Any(candidate => candidate.Bytes is null) ? "at least " + total : total;
    }

    public IRenderable Render()
    {
        var deletable = Safe.Concat(Risky).ToList();
        var content = new List<IRenderable>
        {
            new Markup($"[bold {TerminalStyle.Accent}]Delete {Plural(deletable.Count, "worktree")} and free {Total(deletable)}.[/]"),
            new Markup($"[{TerminalStyle.Muted}]Each folder is removed with git worktree remove. Branches are always kept.[/]")
        };
        Table Rows(IEnumerable<TidyCandidate> items, Func<TidyCandidate, string> note)
        {
            var table = new Table().Border(TableBorder.None).Expand()
                .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]WORKTREE[/]"))
                .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]SIZE[/]") { NoWrap = true })
                .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]FOLDER[/]"));
            foreach (var item in items)
                table.AddRow(new Text(item.Name), new Text(item.Bytes is { } bytes ? TidySizes.Format(bytes) : "unknown"),
                    new Markup(Terminal.Escape(item.Checkout.Path) + note(item)));
            return table;
        }
        if (Safe.Count != 0)
        {
            content.Add(new Text(""));
            content.Add(new Markup($"[{TerminalStyle.Accent}]CLEAN ({Safe.Count})[/]  [{TerminalStyle.Muted}]No uncommitted changes and no unpushed commits.[/]"));
            content.Add(Rows(Safe, _ => ""));
        }
        if (Risky.Count != 0)
        {
            content.Add(new Text(""));
            content.Add(new Markup($"[bold {TerminalStyle.Gold}]WORK THAT WOULD BE LOST ({Risky.Count})[/]"));
            content.Add(new Markup($"[{TerminalStyle.Gold}]These worktrees hold changes that exist nowhere else. Deleting them loses that work for good; " +
                "it needs its own confirmation.[/]"));
            content.Add(Rows(Risky, item => $"\n[{TerminalStyle.Gold}]{Terminal.Escape(TidyList.Loss(item.Status!))} would be lost.[/]"));
        }
        if (Unreadable.Count != 0)
        {
            content.Add(new Text(""));
            content.Add(new Markup($"[{TerminalStyle.Gold}]NOT DELETED ({Unreadable.Count})[/]  [{TerminalStyle.Muted}]Their state could not be checked.[/]"));
            content.Add(Rows(Unreadable, item => $"\n[{TerminalStyle.Muted}]{Terminal.Escape(item.Error ?? "Unknown state.")}[/]"));
        }
        return TerminalStyle.Card(new Rows(content), "REVIEW DELETION", TerminalStyle.Gold);
    }

    internal static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

internal static class TidyRemoval
{
    /// <summary>
    /// Asks to delete the whole selection (default No), then separately whether to lose the work in the risky worktrees (default No).
    /// Declining the second still deletes the clean ones. Returns what to delete and the risky worktrees that are kept.
    /// </summary>
    internal static (List<TidyTarget> Targets, List<TidyCandidate> Kept) Confirm(TidyReview review, Func<string, bool> confirm)
    {
        var deletable = review.Safe.Concat(review.Risky).ToList();
        if (deletable.Count == 0 || !confirm($"Delete {TidyReview.Plural(deletable.Count, "worktree")} and free {TidyReview.Total(deletable)}?"))
            return ([], []);
        var targets = review.Safe.Select(candidate => new TidyTarget(candidate.Checkout, candidate.Status!, false, candidate.Bytes)).ToList();
        if (review.Risky.Count == 0) return (targets, []);
        if (!confirm($"Also delete the {TidyReview.Plural(review.Risky.Count, "worktree")} with work that exists nowhere else? " +
                "Their uncommitted changes and unpushed commits will be lost for good."))
            return (targets, review.Risky.ToList());
        targets.AddRange(review.Risky.Select(candidate => new TidyTarget(candidate.Checkout, candidate.Status!, true, candidate.Bytes)));
        return (targets, []);
    }

    internal static async Task<List<TidyResult>> RemoveAll(GitRepository git, IReadOnlyList<TidyTarget> targets, TidyGuard guard,
        Action<int, TidyTarget>? starting = null)
    {
        var results = new List<TidyResult>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            starting?.Invoke(index, target);
            try { results.Add(await Remove(git, target, guard)); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                results.Add(new(target.Checkout, false, target.Bytes, GitError(exception)));
            }
        }
        return results;
    }

    /// <summary>
    /// Deletes one worktree after checking, immediately before, that it is still a registered, unlocked, non-primary worktree of its repository
    /// at that path and holds no more work than was confirmed. Its branch is kept.
    /// </summary>
    internal static async Task<TidyResult> Remove(GitRepository git, TidyTarget target, TidyGuard guard)
    {
        var checkout = target.Checkout;
        TidyResult Failed(string message) => new(checkout, false, target.Bytes, message);
        if (await Recheck(git, target, guard) is { } reason) return Failed("Skipped: " + reason);
        var path = Files.Full(checkout.Path);
        // Git for Windows follows a junction when it deletes a worktree and empties the junction's target, so links go first.
        try { RemoveLinks(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("A link inside it could not be removed, so nothing was deleted: " + exception.Message);
        }
        try
        {
            await git.Git(checkout.RepositoryRoot, target.Force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path]);
        }
        catch (InvalidOperationException exception)
        {
            var error = GitError(exception);
            // Git refuses these before deleting anything; deleting the folder anyway would lose work that was not confirmed.
            if (error.Contains("modified or untracked", StringComparison.OrdinalIgnoreCase) || error.Contains("locked", StringComparison.OrdinalIgnoreCase))
                return Failed("Git refused: " + error);
            // Usually a path too long for Git or a file another program holds open. The checks above passed, so remove the folder directly.
            try
            {
                if (Directory.Exists(path)) DeleteFolder(path);
            }
            catch (Exception folder) when (folder is IOException or UnauthorizedAccessException)
            {
                return Failed($"Git could not remove it ({error}), and deleting the folder failed too: {folder.Message}");
            }
            // With the folder gone, Git only forgets the worktree; prune is the fallback if it cannot.
            try { await git.Git(checkout.RepositoryRoot, "worktree", "remove", "--force", path); }
            catch (InvalidOperationException)
            {
                try { await git.Git(checkout.RepositoryRoot, "worktree", "prune"); }
                catch (InvalidOperationException prune) { return Failed("The folder was deleted, but Git could not forget the worktree: " + GitError(prune)); }
            }
        }
        if (Directory.Exists(path)) return Failed("Git reported success, but the folder still exists.");
        if ((await Registered(git, checkout.RepositoryRoot)).Any(block => IsPath(block, path)))
            return Failed("The folder is gone, but Git still lists the worktree. Run git worktree prune in " + checkout.RepositoryRoot + ".");
        return new(checkout, true, target.Bytes, "Removed");
    }

    private static async Task<string?> Recheck(GitRepository git, TidyTarget target, TidyGuard guard)
    {
        var checkout = target.Checkout;
        if (guard.Check(checkout) is { } protection) return protection.Reason;
        var path = Files.Full(checkout.Path);
        List<string[]> blocks;
        try { blocks = await Registered(git, checkout.RepositoryRoot); }
        catch (InvalidOperationException) { return $"its repository at {checkout.RepositoryRoot} could not be read."; }
        if (blocks.Count == 0 || !IsPath(blocks[0], Files.Full(checkout.RepositoryRoot)))
            return $"{checkout.RepositoryRoot} is not that repository's main checkout.";
        if (IsPath(blocks[0], path)) return "it is the repository's main checkout, which is never deleted.";
        var fields = blocks.Skip(1).FirstOrDefault(block => IsPath(block, path));
        if (fields is null) return $"it is no longer a worktree of {checkout.Repository}.";
        if (fields.Any(field => field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))) return "Git has it locked.";
        var branch = Field(fields, "branch") is { } reference && reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..] : Field(fields, "branch");
        if (checkout.Detached ? branch is not null : branch != checkout.Branch)
            return $"it now has {(branch is null ? "a detached HEAD" : "branch " + branch)} checked out instead of what you confirmed.";
        if (!Directory.Exists(path)) return "its folder no longer exists.";
        if (new DirectoryInfo(path).LinkTarget is not null) return "its folder is a link to another folder.";
        CheckoutStatus now;
        try { now = await WorktreeCatalog.Status(git, checkout); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException) { return "its state could not be read: " + GitError(exception); }
        if (now.Uncommitted > target.Confirmed.Uncommitted || now.Unpushed > target.Confirmed.Unpushed)
            return $"it now has {TidyList.Loss(now)}, more than when you confirmed ({(target.Confirmed.Clean ? "clean" : TidyList.Loss(target.Confirmed))}).";
        return null;
    }

    private static async Task<List<string[]>> Registered(GitRepository git, string repository) =>
        (await git.Git(repository, "worktree", "list", "--porcelain", "-z")).Split("\0\0", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\0', StringSplitOptions.RemoveEmptyEntries)).ToList();

    private static string? Field(string[] fields, string name) =>
        fields.FirstOrDefault(field => field.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];

    private static bool IsPath(string[] fields, string path) => Field(fields, "worktree") is { } value && Files.Comparer.Equals(Files.Full(value), path);

    private static string GitError(Exception exception) =>
        exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("fatal:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))?
            .Split(':', 2)[1].Trim() ?? exception.Message.Trim();

    /// <summary>Removes every junction and directory symbolic link under <paramref name="folder"/>, leaving their targets untouched.</summary>
    internal static int RemoveLinks(string folder)
    {
        var removed = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            // Cloud placeholders are reparse points too, but they hold the folder's own files rather than pointing elsewhere.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null)
            {
                entry.Delete();
                removed++;
            }
            else removed += RemoveLinks(entry.FullName);
        }
        return removed;
    }

    /// <summary>Deletes a folder tree, removing links inside it without following them and clearing read-only attributes Git leaves on its objects.</summary>
    internal static void DeleteFolder(string folder)
    {
        var root = new DirectoryInfo(folder);
        if (root.LinkTarget is null)
            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
            {
                if (entry is DirectoryInfo directory && directory.LinkTarget is null)
                {
                    DeleteFolder(directory.FullName);
                    continue;
                }
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete(); // A linked folder is removed as a link; its target is untouched.
            }
        if (root.Attributes.HasFlag(FileAttributes.ReadOnly)) root.Attributes &= ~FileAttributes.ReadOnly;
        root.Delete();
    }

    internal static IRenderable Summary(IReadOnlyList<TidyResult> results, IReadOnlyList<TidyCandidate> kept)
    {
        var removed = results.Where(result => result.Removed).ToList();
        var failed = results.Where(result => !result.Removed).ToList();
        var freed = TidySizes.Format(removed.Sum(result => result.Bytes ?? 0));
        var lines = new List<IRenderable>
        {
            new Markup($"[bold {TerminalStyle.Accent}]Removed {removed.Count} of {TidyReview.Plural(results.Count + kept.Count, "worktree")}[/]" +
                $"  [{TerminalStyle.Muted}]freed {(removed.Any(result => result.Bytes is null) ? "at least " : "")}{freed}[/]"),
            new Markup($"[{TerminalStyle.Muted}]Every branch was kept. Delete one you no longer need with git branch -D <name> in its repository.[/]")
        };
        foreach (var result in removed)
            lines.Add(new Markup($"[{TerminalStyle.Accent}]Removed[/]  {Terminal.Escape(TidyRow.NameOf(result.Checkout))}  " +
                $"[{TerminalStyle.Muted}]{(result.Bytes is { } bytes ? TidySizes.Format(bytes) : "")}[/]"));
        foreach (var result in failed)
            lines.Add(new Markup($"[{TerminalStyle.Gold}]Not removed[/]  {Terminal.Escape(TidyRow.NameOf(result.Checkout))}: {Terminal.Escape(result.Message)}"));
        foreach (var candidate in kept)
            lines.Add(new Markup($"[{TerminalStyle.Muted}]Kept[/]  {Terminal.Escape(candidate.Name)}: you chose to keep its {Terminal.Escape(TidyList.Loss(candidate.Status!))}."));
        return TerminalStyle.Card(new Rows(lines), "TIDY COMPLETE", failed.Count == 0 ? TerminalStyle.Accent : TerminalStyle.Gold);
    }
}
