using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>The home screen's Clean up action: Cultivate's own old files, and worktrees unused for weeks that lose nothing when deleted.</summary>
internal sealed class CleanupScreen(Settings settings, GitRepository git, Bridge bridge)
{
    internal const string RemoveFiles = "Remove Cultivate's old files", ReviewWorktrees = "Review unused worktrees", Back = "Back";

    internal Task Run() => Show(AnsiConsole.Console, Terminal.Select, question => Terminal.Confirm(Terminal.Escape(question), false), Terminal.Pause,
        Files.Home, () => DateTimeOffset.Now);

    internal async Task Show(IAnsiConsole console, Func<string, IEnumerable<MenuAction>, string> select, Func<string, bool> confirm, Action pause,
        string home, Func<DateTimeOffset> clock)
    {
        var header = TerminalStyle.Header("CLEAN UP", $"Cultivate's old files, and worktrees unused for {settings.CleanupDays} days that hold nothing to lose.");
        while (true)
        {
            console.Clear();
            console.Write(header);
            var now = clock();
            var scans = TidySizes.Load(TidySizes.CachePath);
            var (deployment, files, worktrees) = await console.Status().StartAsync("Measuring Cultivate's files and checking every worktree...", async _ =>
            {
                var deployment = await CleanupDeployment.Read(bridge);
                var files = await CultivateFiles.Clean(home, settings, git, deployment, now, dryRun: true, TimeSpan.Zero);
                var worktrees = await CleanupWorktrees.Find(git, await WorktreeCatalog.Discover(settings, git), TidyGuard.Current(deployment.State),
                    settings.CleanupDays, now, scans);
                return (deployment, files, worktrees);
            });
            TidySizes.Save(TidySizes.CachePath, scans.Values);
            console.Write(FilesCard(files));
            console.Write(WorktreesCard(worktrees, now));
            var actions = new List<MenuAction>();
            if (files.Count != 0)
                actions.Add(new(RemoveFiles, $"Remove {Things(files.Count, "old ")} and free {TidySizes.Format(files.Bytes)}. " +
                    "Cultivate also does this on its own once a day."));
            if (worktrees.Qualifying.Count != 0)
                actions.Add(new(ReviewWorktrees, "List them, then confirm before anything is deleted. Branches are always kept."));
            actions.Add(new(Back, "Return home without removing anything.", Back: true));
            switch (select("What would you like to clean up?", actions))
            {
                case RemoveFiles:
                    var report = await console.Status().StartAsync("Removing Cultivate's old files...",
                        _ => CultivateFiles.Clean(home, settings, git, deployment, now, dryRun: false, TimeSpan.FromSeconds(30)));
                    console.Write(Result(report));
                    pause();
                    break;
                case ReviewWorktrees:
                    console.Clear();
                    console.Write(header);
                    console.Write(Review(worktrees, now));
                    if (!confirm($"Delete {TidyReview.Plural(worktrees.Qualifying.Count, "worktree")} and free {TidySizes.Format(worktrees.Bytes)}?"))
                        break;
                    // A deployment may have started in another window since the state was read.
                    var guard = TidyGuard.Current((await CleanupDeployment.Read(bridge)).State ?? deployment.State);
                    var results = await console.Status().StartAsync("Removing worktrees...", context => CleanupWorktrees.Remove(git, worktrees.Qualifying, guard,
                        (index, target) => context.Status($"Removing {index + 1} of {worktrees.Qualifying.Count}: {Terminal.Escape(TidyRow.NameOf(target.Checkout))}...")));
                    foreach (var result in results.Where(result => result.Removed)) scans.Remove(result.Checkout.Path);
                    TidySizes.Save(TidySizes.CachePath, scans.Values);
                    console.Write(TidyRemoval.Summary(results, []));
                    pause();
                    break;
                default:
                    return;
            }
        }
    }

    private static string Muted(string text) => $"[{TerminalStyle.Muted}]{Terminal.Escape(text)}[/]";
    private static string Things(int count, string adjective = "") => count == 1 ? $"1 {adjective}file or folder" : $"{count} {adjective}files and folders";

    internal static IRenderable FilesCard(CleanupReport report)
    {
        var lines = new List<IRenderable>
        {
            new Markup(report.Count == 0 ? $"[{TerminalStyle.Accent}]Nothing to remove.[/] {Muted("Cultivate's files are within their retention periods.")}" :
                $"[bold {TerminalStyle.Accent}]{Things(report.Count, "old ")}, {TidySizes.Format(report.Bytes)}[/]"),
            new Markup(Muted("The deployment journal and its backups, the active deployment's run and worker, and settings are never removed."))
        };
        if (report.Count != 0)
        {
            var table = new Table().Border(TableBorder.None).Expand()
                .AddColumn(new TableColumn(Muted("FOLDER")) { NoWrap = true })
                .AddColumn(new TableColumn(Muted("OLD")) { NoWrap = true }).AddColumn(new TableColumn(Muted("SIZE")) { NoWrap = true })
                .AddColumn(new TableColumn(Muted("RULE")));
            foreach (var category in report.Categories.Where(category => category.Count != 0))
                table.AddRow(new Text(category.Name), new Text(category.Count.ToString()), new Text(TidySizes.Format(category.Bytes)), new Markup(Muted(category.Rule)));
            lines.Add(table);
        }
        lines.AddRange(Notes(report));
        return TerminalStyle.Card(new Rows(lines), "CULTIVATE FILES");
    }

    private static IEnumerable<IRenderable> Notes(CleanupReport report)
    {
        const int shown = 5;
        var notes = report.Failed.Select(note => $"[{TerminalStyle.Gold}]Not removed[/]  {Terminal.Escape(note.Path)}: {Terminal.Escape(note.Reason)}")
            .Concat(report.Kept.Select(note => $"{Muted("Kept")}  {Terminal.Escape(Path.GetFileName(note.Path))}: {Muted(note.Reason)}")).ToList();
        return notes.Take(shown).Append(notes.Count > shown ? Muted($"and {notes.Count - shown} more; cultivate cleanup lists them all.") : "")
            .Where(note => note.Length != 0).Select(note => new Markup(note));
    }

    internal static IRenderable Result(CleanupReport report)
    {
        var lines = new List<IRenderable>
        {
            new Markup($"[bold {TerminalStyle.Accent}]Removed {Things(report.Count)}[/]  " +
                Muted($"freed {TidySizes.Format(report.Bytes)}" +
                    (report.SnapshotWorktrees == 0 ? "" : $", and Git forgot {TidyReview.Plural(report.SnapshotWorktrees, "leftover snapshot worktree")}")))
        };
        lines.AddRange(report.Categories.Where(category => category.Count != 0).Select(category =>
            new Markup($"[{TerminalStyle.Accent}]Removed[/]  {Terminal.Escape(category.Name)}  {Muted($"{category.Count}, {TidySizes.Format(category.Bytes)}")}")));
        lines.AddRange(Notes(report));
        return TerminalStyle.Card(new Rows(lines), "CLEANED UP", report.Failed.Count == 0 ? TerminalStyle.Accent : TerminalStyle.Gold);
    }

    internal static IRenderable WorktreesCard(CleanupWorktreeScan scan, DateTimeOffset now)
    {
        var kept = string.Join(", ", new[]
        {
            scan.Protected == 0 ? "" : $"{scan.Protected} protected", scan.HoldingWork == 0 ? "" : $"{scan.HoldingWork} holding work",
            scan.Recent == 0 ? "" : $"{scan.Recent} used recently", scan.Unreadable == 0 ? "" : $"{scan.Unreadable} could not be read"
        }.Where(part => part.Length != 0));
        var lines = new List<IRenderable>
        {
            new Markup(scan.Qualifying.Count == 0 ? $"[{TerminalStyle.Accent}]No worktree has gone unused for {scan.Days} days with nothing to lose.[/]" :
                $"[bold {TerminalStyle.Accent}]{TidyReview.Plural(scan.Qualifying.Count, "worktree")}, {TidyReview.Total(scan.Qualifying.Select(Candidate))}[/]  " +
                Muted($"unused for {scan.Days} days or more")),
            new Markup(Muted("No uncommitted or untracked changes, and every commit stays on its branch. Nothing would be lost.")),
        };
        if (kept.Length != 0) lines.Add(new Markup(Muted("Kept: " + kept + ".")));
        if (scan.Qualifying.Count != 0) lines.Add(Rows(scan, now, 8));
        return TerminalStyle.Card(new Rows(lines), $"WORKTREES UNUSED FOR {scan.Days} DAYS");
    }

    private static IRenderable Review(CleanupWorktreeScan scan, DateTimeOffset now) => TerminalStyle.Card(new Rows(
        new Markup($"[bold {TerminalStyle.Accent}]Delete {TidyReview.Plural(scan.Qualifying.Count, "worktree")} and free {TidyReview.Total(scan.Qualifying.Select(Candidate))}.[/]"),
        new Markup(Muted("Each is checked again just before it is deleted and skipped if it has gained any work. Branches are always kept.")),
        Rows(scan, now, int.MaxValue)), "REVIEW DELETION", TerminalStyle.Gold);

    private static TidyCandidate Candidate(CleanupWorktree worktree) => new(worktree.Checkout, worktree.Status, worktree.Bytes);

    private static IRenderable Rows(CleanupWorktreeScan scan, DateTimeOffset now, int limit)
    {
        var table = new Table().Border(TableBorder.None).Expand()
            .AddColumn(new TableColumn(Muted("WORKTREE"))).AddColumn(new TableColumn(Muted("SIZE")) { NoWrap = true })
            .AddColumn(new TableColumn(Muted("UNUSED")) { NoWrap = true }).AddColumn(new TableColumn(Muted("FOLDER")));
        foreach (var worktree in scan.Qualifying.Take(limit))
            table.AddRow(new Text(worktree.Name), new Text(worktree.Bytes is { } bytes ? TidySizes.Format(bytes) : "unknown"),
                new Text(TidyList.AgeText(now - worktree.LastActivity)), new Text(worktree.Checkout.Path));
        if (scan.Qualifying.Count > limit) table.AddRow(new Markup(Muted($"and {scan.Qualifying.Count - limit} more")), new Text(""), new Text(""), new Text(""));
        return table;
    }
}
