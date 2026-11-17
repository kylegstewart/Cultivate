using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    private static async Task CheckWorktreeTidy(string home)
    {
        var root = Path.Combine(home, "tidy");
        await CheckTidyMeasurement(root);
        CheckTidyList();
        CheckTidyReview();
        await CheckTidyRemoval(root);
    }

    private static async Task CheckTidyMeasurement(string root)
    {
        var worktree = Path.Combine(root, "measure");
        var outside = Path.Combine(root, "junction target");
        var old = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        void Write(string relative, int bytes, DateTime written)
        {
            var path = Path.Combine(worktree, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, written);
        }
        Write("readme.txt", 100, old.AddDays(-3));
        Write(@"src\Page.cs", 200, old);
        Write("hidden.txt", 300, old.AddDays(-1));
        File.SetAttributes(Path.Combine(worktree, "hidden.txt"), FileAttributes.Hidden | FileAttributes.System);
        Write(".git", 40, recent);
        Write(@"src\bin\Debug\Page.dll", 1000, recent);
        Write(@"src\obj\Page.cache", 2000, recent);
        Write(@"packages\Library\lib.dll", 4000, recent);
        Write(@"web\node_modules\tool\index.js", 8000, recent);
        Write(@".vs\state.bin", 16000, recent);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "large.bin"), new byte[50000]);
        var link = Path.Combine(worktree, "linked");
        await Commands.Run("cmd.exe", ["/c", "mklink", "/J", link, outside]);
        TidyMeasurement measured;
        try { measured = TidySizes.Measure(worktree); }
        finally { Directory.Delete(link); } // Removes only the link, so the fixture cleanup never meets it.
        Check(measured.Bytes == 100 + 200 + 300 + 40 + 1000 + 2000 + 4000 + 8000 + 16000,
            "a worktree's size counts hidden and system files, .git, and build output, but not the target of a junction inside it");
        Check(measured.LastWrite == new DateTimeOffset(old), "last activity ignores files under bin, obj, packages, node_modules, .vs and .git");
        Check(TidySizes.Measure(Path.Combine(root, "missing")).Bytes == 0, "measuring a folder that is gone counts nothing instead of failing");

        var cache = Path.Combine(root, "cache", "worktree-scans.json");
        var scannedAt = new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero);
        var state = new CheckoutStatus(2, 1, scannedAt.AddDays(-3), "Work in progress");
        TidySizes.Save(cache, [new(worktree, "abc123", 12345, new DateTimeOffset(old), state, scannedAt),
            new(Path.Combine(root, "deleted"), "abc123", 99, null, state, scannedAt)]);
        var loaded = TidySizes.Load(cache);
        Check(loaded.Count == 1 && loaded[worktree.ToUpperInvariant()] is { Bytes: 12345, Head: "abc123" } scan && scan.Scanned == scannedAt &&
            scan.LastWrite == new DateTimeOffset(old) && scan.Status == state,
            "the scan cache keeps each existing worktree's size, last write, state, HEAD and scan time, and drops worktrees that are gone");
        File.WriteAllText(cache, """[{ "Path": "C:\\old", "Bytes": 5, "Measured": "2026-11-16T08:30:00Z" }]""");
        Check(TidySizes.Load(cache).Count == 0, "an entry without a HEAD or state, like the old size-only cache, is scanned again");
        File.WriteAllText(cache, "{ not json");
        Check(TidySizes.Load(cache).Count == 0 && TidySizes.Load(Path.Combine(root, "no cache.json")).Count == 0,
            "a damaged or missing scan cache means worktrees are scanned again");
        Check(TidySizes.Format(1536L << 20) == "1.5 GB" && TidySizes.Format(600L << 20) == "600 MB" && TidySizes.Format(10) == "10 B",
            "sizes are shown in GB, MB, KB or bytes");
    }

    private static void CheckTidyList()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Checkout Worktree(string repository, string branch, string folder, bool primary = false, bool locked = false) =>
            new(repository, $@"C:\fixture\{repository}", primary ? $@"C:\fixture\{repository}" : $@"C:\fixture\worktrees\{folder}",
                branch, "0123456789abcdef", primary, false, locked);
        var main = Worktree("Alpha", "master", "", primary: true);
        var big = Worktree("Alpha", "big-work", "big");
        var medium = Worktree("Alpha", "medium-work", "medium");
        var small = Worktree("Beta", "12345", "small");
        var unmeasured = Worktree("Beta", "12345-2", "follow-up");
        var live = Worktree("Gamma", "live-work", "live");
        var here = Worktree("Gamma", "here-work", "here");
        var locked = Worktree("Gamma", "locked-work", "locked", locked: true);
        var markup = Worktree("Rep[o]", "fix[red]", "markup [x]");
        var guard = new TidyGuard(new DeploymentState { Active = true, SourceRoot = live.Path }, here.Path + @"\src", @"C:\elsewhere\Cultivate");
        var list = new TidyList([main, big, medium, small, unmeasured, live, here, locked, markup], guard, () => now);
        TidyRow Row(Checkout checkout) => list.Rows.Single(row => row.Path == checkout.Path);
        string Names() => string.Join(",", list.Matches.Select(row => row.Checkout.Branch));
        void Key(ConsoleKey key, char character = '\0', bool control = false, bool shift = false) =>
            list.HandleKey(new(character, key, shift, false, control), 5);
        void Type(string text)
        {
            foreach (var character in text) Key(ConsoleKey.A, character);
        }
        void Highlight(Checkout checkout)
        {
            Key(ConsoleKey.Home);
            while (list.Highlighted!.Path != checkout.Path) Key(ConsoleKey.DownArrow);
        }

        Check(list.Rows.Count == 8 && list.Rows.All(row => !row.Checkout.Primary), "primary checkouts are never listed");
        Check(Row(live).Protection!.Contains("active webfarm deployment") && Row(live).Tag == "live" &&
            Row(here).Protection!.Contains("current folder") && Row(here).Tag == "in use" &&
            Row(locked).Protection!.Contains("locked") && Row(locked).Tag == "locked" && Row(big).Protection is null,
            "the active deployment's worktree, the one holding the current folder, and a locked worktree are protected with their reason");
        Check(new TidyGuard(null, @"C:\", @"C:\fixture\worktrees\big\bin\Debug").Check(big)?.Reason.Contains("Cultivate is running") == true &&
            new TidyGuard(new DeploymentState { Active = false, SourceRoot = live.Path }, @"C:\", @"C:\").Check(live) is null,
            "a worktree holding the running program is protected, and an inactive deployment protects nothing");
        Check(list.Sort == TidySort.Largest && list.Rows.All(row => row.Bytes is null && row.Status is null) && list.Matches.Count == 8,
            "every worktree is listed before any size or state is known");

        var saved = new Dictionary<string, TidyScan>(Files.Comparer)
        {
            [small.Path] = new(small.Path, small.Head, 40L << 20, now.AddDays(-4), new(0, 0, now.AddDays(-5), "Saved"), now.AddDays(-2)),
            [medium.Path] = new(medium.Path, "moved since", 1, null, new(0, 0, null, ""), now)
        };
        Check(WorktreeTidy.Unscanned(list.Rows, saved).Select(row => row.Path)
                .SequenceEqual(list.Rows.Where(row => row.Path != small.Path).Select(row => row.Path)),
            "a visit scans only worktrees with no saved scan, or whose HEAD has moved since it was saved");
        list.SetScan(saved[small.Path]);
        Check(Row(small) is { Bytes: 40L << 20, Status.Subject: "Saved" } && Row(small).Scanned == now.AddDays(-2) &&
            Row(small).LastActivity == now.AddDays(-4), "a saved scan fills in a worktree's size, state, last activity and scan time");
        var sizes = new Dictionary<Checkout, long>
        {
            [big] = 6L << 30, [medium] = 600L << 20, [small] = 50L << 20, [live] = 2L << 30, [here] = 300L << 20, [locked] = 1L << 30, [markup] = 10L << 20
        };
        Highlight(medium);
        foreach (var (checkout, bytes) in sizes) list.SetSize(checkout.Path, new(bytes, null), now);
        Check(list.Highlighted!.Path == medium.Path && list.Index != 0,
            "the highlight stays on the same worktree when arriving sizes re-sort the list");
        Check(Names() == "big-work,live-work,locked-work,medium-work,here-work,12345,fix[red],12345-2",
            "largest first is the default, with worktrees not measured yet last");
        var statuses = new Dictionary<Checkout, CheckoutStatus>
        {
            [big] = new(0, 0, now.AddDays(-100), "Big"), [medium] = new(2, 0, now.AddDays(-20), "Medium"),
            [small] = new(0, 0, now.AddDays(-3), "Small"), [unmeasured] = new(0, 1, now.AddDays(-40), "Follow-up"),
            [live] = new(0, 0, now.AddDays(-1), "Live"), [here] = new(0, 0, now.AddDays(-50), "Here"),
            [locked] = new(0, 0, now.AddDays(-200), "Locked"), [markup] = new(0, 0, now.AddDays(-10), "Markup")
        };
        foreach (var (checkout, status) in statuses) list.SetStatus(checkout.Path, status);
        list.SetSize(medium.Path, new(600L << 20, now.AddDays(-2)), now);
        Check(Row(medium).LastActivity == now.AddDays(-2) && Row(big).LastActivity == now.AddDays(-100),
            "last activity is the later of the last commit and the newest file write");
        Check(Row(medium).StateText == "2 uncommitted" && Row(unmeasured).StateText == "1 unpushed" && Row(big).StateText == "clean" &&
            TidyRow.State(new(3, 2, null, "")) == "3 uncommitted, 2 unpushed", "each row's state names its uncommitted changes and unpushed commits");

        Key(ConsoleKey.Tab);
        Check(list.Sort == TidySort.Smallest && Names() == "fix[red],12345,here-work,medium-work,locked-work,live-work,big-work,12345-2",
            "Tab sorts smallest first");
        Key(ConsoleKey.Tab);
        Check(list.Sort == TidySort.Oldest && Names() == "locked-work,big-work,here-work,12345-2,fix[red],12345,medium-work,live-work",
            "the next sort is oldest first");
        Key(ConsoleKey.Tab);
        Check(list.Sort == TidySort.Newest && Names() == "live-work,medium-work,12345,fix[red],12345-2,here-work,big-work,locked-work",
            "the next sort is newest first");
        Key(ConsoleKey.Tab);
        Check(list.Sort == TidySort.Name && Names() == "big-work,medium-work,12345,12345-2,here-work,live-work,locked-work,fix[red]",
            "the next sort is by repository, then branch");
        Key(ConsoleKey.Tab, shift: true);
        Check(list.Sort == TidySort.Newest, "Shift+Tab goes back to the previous sort");
        Key(ConsoleKey.Tab);
        Key(ConsoleKey.Tab);
        Check(list.Sort == TidySort.Largest, "the sorts cycle back to largest first");

        Type("beta");
        Check(Names() == "12345,12345-2", "typing filters by repository");
        Key(ConsoleKey.U, control: true);
        Type("FOLLOW-UP");
        Check(Names() == "12345-2", "typing filters by folder, ignoring case");
        Key(ConsoleKey.Backspace);
        Check(list.Query == "FOLLOW-U", "Backspace edits the search");
        Key(ConsoleKey.U, control: true);
        Type("12345");
        Check(Names() == "12345,12345-2", "typing a ticket finds its worktrees");
        Key(ConsoleKey.U, control: true);
        Key(ConsoleKey.Spacebar, ' ');
        Check(list.Query == "", "Space is a command, not part of the search");
        Key(ConsoleKey.D, control: true);

        Key(ConsoleKey.F2);
        Check(list.Age == 1 && Names() == "big-work,locked-work,here-work,fix[red],12345-2",
            "F2 lists worktrees with no activity for more than 7 days");
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Check(list.Age == 3 && Names() == "big-work,locked-work,here-work,12345-2", "the age filter cycles to older than 30 days");
        Key(ConsoleKey.F2);
        Check(Names() == "big-work,locked-work", "the age filter cycles to older than 90 days");
        Key(ConsoleKey.F2);
        Check(list.Age == 0 && list.Matches.Count == 8, "the age filter cycles back to any age");
        Key(ConsoleKey.F3);
        Check(list.Size == 1 && Names() == "big-work,live-work,locked-work,medium-work,here-work",
            "F3 lists worktrees of at least 100 MB, leaving out those not measured yet");
        list.SetSize(unmeasured.Path, new(150L << 20, null), now);
        Check(Names() == "big-work,live-work,locked-work,medium-work,here-work,12345-2", "a worktree joins a size filter once it is measured");
        Key(ConsoleKey.F3);
        Key(ConsoleKey.F3);
        Check(list.Size == 3 && Names() == "big-work,live-work,locked-work", "the size filter cycles to 1 GB or more");
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Check(Names() == "big-work,locked-work", "age and size filters combine");
        Type("alpha");
        Check(Names() == "big-work", "the search combines with the age and size filters");
        Key(ConsoleKey.U, control: true);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F3);
        Key(ConsoleKey.F3);
        Check(list.Age == 0 && list.Size == 0, "the filters return to any");
        Key(ConsoleKey.F4);
        Check(list.SafeOnly && Names() == "big-work,12345-2,12345,fix[red]",
            "F4 lists only unprotected worktrees that would lose nothing, including a branch with unpushed commits");
        Key(ConsoleKey.F4);

        var loose = new Checkout("Alpha", @"C:\fixture\Alpha", @"C:\fixture\worktrees\loose", "", "fedcba9876543210", false, true, false);
        var losses = new TidyList([unmeasured, loose, medium], guard, () => now);
        losses.SetStatus(unmeasured.Path, new(0, 2, now, "Ahead"));
        losses.SetStatus(loose.Path, new(0, 2, now, "Loose"));
        losses.SetStatus(medium.Path, new(2, 1, now, "Medium"));
        string Card(int index)
        {
            losses.HandleKey(new('\0', ConsoleKey.Home, false, false, false), 5);
            for (var step = 0; step < index; step++) losses.HandleKey(new('\0', ConsoleKey.DownArrow, false, false, false), 5);
            return Capture(losses.Render(160, 30), 160, 30);
        }
        Check(losses.Matches.Select(row => row.Checkout.Branch).SequenceEqual(["", "medium-work", "12345-2"]) &&
            Card(0).Contains("Deleting it would lose 2 commits that are on no branch or remote.") &&
            Card(1).Contains("Deleting it would lose 2 uncommitted changes.  Its 1 unpushed commit stays on branch medium-work.") &&
            Card(2).Contains("Safe to delete: nothing would be lost.  Its 2 unpushed commits stay on branch 12345-2.") &&
            Card(2).Contains("2 unpushed") && Card(0).Contains("2 unpushed"),
            "the highlighted row says what deleting would lose and that a branch keeps its unpushed commits, while the state column shows both counts");
        losses.HandleKey(new('\0', ConsoleKey.F4, false, false, false), 5);
        Check(losses.Matches.Single().Checkout.Branch == "12345-2", "a detached worktree with unpushed commits is not safe to delete");
        losses.SetStatusError(unmeasured.Path, "fatal: unreadable");
        Check(losses.Rows.Single(row => row.Path == unmeasured.Path) is { Status: null, Safe: false, StateText: "unknown" } && losses.Matches.Count == 0,
            "a worktree whose state could not be read again is no longer shown as safe from its earlier state");

        Highlight(big);
        Key(ConsoleKey.Spacebar, ' ');
        Check(list.Selected.Select(row => row.Path).SequenceEqual([big.Path]), "Space selects the highlighted worktree");
        Highlight(live);
        Key(ConsoleKey.Spacebar, ' ');
        Check(list.Selected.Count == 1 && list.Notice.Contains("active webfarm deployment"),
            "a protected worktree cannot be selected, and Space explains why");
        Highlight(big);
        Key(ConsoleKey.Spacebar, ' ');
        Check(list.Selected.Count == 0, "Space again clears the highlighted worktree's selection");
        Check(list.HandleKey(new('\0', ConsoleKey.Delete, false, false, false), 5) == TidyCommand.None && list.Notice.Contains("Space"),
            "Delete with nothing selected explains how to select instead of reviewing");
        Type("gamma");
        Key(ConsoleKey.A, control: true);
        Check(list.Selected.Count == 0, "select-all skips protected worktrees");
        Key(ConsoleKey.U, control: true);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.A, control: true);
        Check(list.Selected.Select(row => row.Checkout.Branch).Order().SequenceEqual(["12345-2", "big-work"]),
            "select-all selects only the listed worktrees that can be deleted");
        Key(ConsoleKey.F2);
        Key(ConsoleKey.F2);
        Key(ConsoleKey.Tab);
        list.SetSize(markup.Path, new(9L << 30, null), now);
        Check(list.Selected.Count == 2 && list.Matches.Count == 8, "the selection survives filtering, sorting and sizes arriving");
        Type("alpha");
        Check(list.Selected.Count == 2 && list.Matches.Count == 2, "a selected worktree hidden by a filter stays selected");
        Check(Capture(list.Render(100, 30), 100, 30).Contains("2 selected (6.1 GB)"), "the footer shows the selected count and their total size");
        Check(list.HandleKey(new('\r', ConsoleKey.Enter, false, false, false), 5) == TidyCommand.Review &&
            list.HandleKey(new('\0', ConsoleKey.Delete, false, false, false), 5) == TidyCommand.Review,
            "Delete, or Enter with a selection, opens the review");
        Key(ConsoleKey.D, control: true);
        Check(list.Selected.Count == 0, "Ctrl+D clears the selection");
        Key(ConsoleKey.U, control: true);

        Highlight(markup);
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var screen = Capture(list.Render(width, height), width, height);
            var lines = screen.Split('\n');
            Check(lines.Length <= height && lines.All(line => line.Length <= width), $"the tidy screen fits {width}x{height} without wrapping");
            Check(screen.Contains("Esc") && screen.Contains("Space") && screen.Contains("F3") && screen.Contains("F5") && screen.Contains("smallest") &&
                screen.Contains("HIGHLIGHTED WORKTREE"), $"the tidy screen keeps its sort and keys visible at {width}x{height}");
            Check(lines.Count(line => line.Contains("Alpha / big-work")) == 1, $"tidy rows do not wrap at {width}x{height}");
        }
        var wide = Capture(list.Render(120, 35), 120, 35);
        Check(wide.Contains("Rep[o] / fix[red]") && wide.Contains(@"markup [x]") && wide.Contains("2026-11-07"),
            "names with markup characters are shown literally, and the highlighted row shows its exact date");
        Check(wide.Contains("locked") && wide.Contains("live") && wide.Contains("in use") && wide.Contains("1 unpushed") && wide.Contains("10 days"),
            "rows show protection labels, states and ages");
        var filtered = new TidyList([big], guard, () => now);
        filtered.HandleKey(new('\0', ConsoleKey.F3, false, false, false), 5);
        var empty = Capture(filtered.Render(80, 24), 80, 24);
        Check(empty.Contains("No worktrees match") && empty.Contains("100 MB or more"), "an empty result shows the active filter");

        using (var output = new StringWriter())
        {
            var screen = new TidyList([big, medium, live], guard, () => now);
            screen.SetSize(medium.Path, new(7L << 30, null), now);
            var keys = new Queue<ConsoleKeyInfo?>([null, new(' ', ConsoleKey.Spacebar, false, false, false), new('\0', ConsoleKey.Delete, false, false, false)]);
            Check(screen.Show(LiveConsole(output), () => keys.Dequeue()) == TidyCommand.Review &&
                screen.Selected.Select(row => row.Path).SequenceEqual([big.Path]) && output.ToString().Contains("7.0 GB"),
                "the screen shows saved sizes, waits for keys, and asks to review the selection");
        }
        using (var output = new StringWriter())
        {
            var screen = new TidyList([big], guard, () => now);
            Check(screen.Show(LiveConsole(output), () => new('\0', ConsoleKey.F5, false, false, false)) == TidyCommand.Rescan,
                "F5 asks to scan every worktree again");
            Check(screen.Show(LiveConsole(output), () => new('\u001b', ConsoleKey.Escape, false, false, false)) == TidyCommand.Back,
                "Esc leaves the tidy screen without deleting anything");
        }
    }

    private static void CheckTidyReview()
    {
        Checkout Worktree(string branch, bool detached = false) => new("Alpha", @"C:\fixture\Alpha", $@"C:\fixture\worktrees\{branch}",
            detached ? "" : branch, "0123456789", false, detached, false);
        var clean = new TidyCandidate(Worktree("clean"), new(0, 0, null, ""), 1L << 30);
        var ahead = new TidyCandidate(Worktree("ahead"), new(0, 2, null, ""), 256L << 20);
        var dirty = new TidyCandidate(Worktree("dirty[x]"), new(3, 1, null, ""), 512L << 20);
        var loose = new TidyCandidate(Worktree("loose", detached: true), new(0, 2, null, ""), 256L << 20);
        var unknown = new TidyCandidate(Worktree("broken"), null, null, "Its state could not be read: fatal");
        var review = new TidyReview([clean, ahead, dirty, loose, unknown]);
        Check(review.Safe.SequenceEqual([clean, ahead]) && review.Risky.SequenceEqual([dirty, loose]) && review.Unreadable.SequenceEqual([unknown]),
            "the review counts a branch worktree with unpushed commits as safe and a detached one with unpushed commits as risky");
        var screen = Capture(review.Render(), 200, 60);
        Check(screen.Contains("Delete 4 worktrees and free 2.0 GB") && screen.Contains("SAFE TO DELETE (2)") &&
            screen.Contains("Its 2 unpushed commits stay on branch ahead.") && screen.Contains("WORK THAT WOULD BE LOST (2)") &&
            screen.Contains("3 uncommitted changes would be lost.") && screen.Contains("Its 1 unpushed commit stays on branch dirty[x].") &&
            screen.Contains("2 commits that are on no branch or remote would be lost.") && !screen.Contains("unpushed commit would be lost") &&
            !screen.Contains("unpushed commits would be lost") && screen.Contains("NOT DELETED (1)") && screen.Contains("Branches are always kept"),
            "the review states only what is really lost, and says unpushed commits on a branch stay on it");

        var asked = new List<string>();
        Func<string, bool> Answers(params bool[] answers)
        {
            var queue = new Queue<bool>(answers);
            asked.Clear();
            return question => { asked.Add(question); return queue.Dequeue(); };
        }
        var (targets, kept) = TidyRemoval.Confirm(review, Answers(false));
        Check(targets.Count == 0 && kept.Count == 0 && asked.Count == 1, "declining the first confirmation deletes nothing");
        Check(asked.Single() == "Delete 4 worktrees and free 2.0 GB?", "the first confirmation names the count and the space freed");
        (targets, kept) = TidyRemoval.Confirm(review, Answers(true, false));
        Check(targets.Select(item => item.Checkout).SequenceEqual([clean.Checkout, ahead.Checkout]) && targets.All(item => !item.Force) &&
            kept.SequenceEqual([dirty, loose]) && asked.Count == 2,
            "declining the second confirmation still deletes the safe worktrees and keeps the risky ones");
        Check(asked[1] == "Also delete the 2 worktrees listed under work that would be lost? " +
            "Their uncommitted changes and commits that are on no branch or remote will be lost for good.",
            "the second confirmation names only what would really be lost");
        (targets, kept) = TidyRemoval.Confirm(review, Answers(true, true));
        Check(targets.Count == 4 && targets.Where(item => item.Force).Select(item => item.Checkout).SequenceEqual([dirty.Checkout, loose.Checkout]) &&
            kept.Count == 0, "only worktrees whose loss was confirmed are removed with --force");
        TidyRemoval.Confirm(new TidyReview([dirty]), Answers(true, false));
        Check(asked[1].EndsWith("Its uncommitted changes will be lost for good.", StringComparison.Ordinal),
            "a branch worktree's unpushed commits are not called lost in the second confirmation");
        (targets, _) = TidyRemoval.Confirm(new TidyReview([clean, ahead]), Answers(true));
        Check(targets.Count == 2 && targets.All(item => !item.Force) && asked.Count == 1,
            "safe worktrees, including a branch with unpushed commits, need one confirmation and no --force");
        var detached = Worktree("loose", detached: true);
        Check(TidyLoss.Of(detached, new(1, 2, null, "")).ToString() == "1 uncommitted change and 2 commits that are on no branch or remote" &&
            TidyLoss.Of(ahead.Checkout, new(0, 5, null, "")).None && TidyLoss.Of(ahead.Checkout, new(0, 5, null, "")).ToString() == "nothing" &&
            TidyLoss.Of(detached, new(0, 3, null, "")).Exceeds(TidyLoss.Of(detached, new(0, 2, null, ""))) &&
            !TidyLoss.Of(ahead.Checkout, new(0, 3, null, "")).Exceeds(TidyLoss.Of(ahead.Checkout, new(0, 0, null, ""))) &&
            TidyLoss.Of(ahead.Checkout, new(1, 0, null, "")).Exceeds(TidyLoss.Of(ahead.Checkout, new(0, 0, null, ""))),
            "only uncommitted changes, and a detached worktree's unpushed commits, count as lost or as becoming riskier");
        Check(TidyRemoval.Confirm(new TidyReview([unknown]), Answers()).Targets.Count == 0 && asked.Count == 0,
            "worktrees whose state cannot be read are never offered for deletion");
    }

    private static async Task CheckTidyRemoval(string root)
    {
        root = Path.Combine(root, "removal");
        try { await CheckTidyRemovalFixtures(root); }
        finally
        {
            // A failed check can leave the junction behind; remove only the link so the fixture cleanup never meets it.
            try { Directory.Delete(Path.Combine(root, "worktrees", "Alpha", "clean-work", "bin", "linked")); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task CheckTidyRemovalFixtures(string root)
    {
        var settings = new Settings { RepositoryRoot = Path.Combine(root, "clones", "Alpha"), WorktreesRoot = Path.Combine(root, "worktrees") };
        var git = new GitRepository(settings);
        var origin = await FixtureRepository(git, Path.Combine(root, "origin"), "master");
        File.WriteAllText(Path.Combine(origin, ".gitignore"), "bin/\n");
        await git.Git(origin, "add", "-A");
        await git.Git(origin, "commit", "-m", "Ignore build output");
        var alpha = settings.RepositoryRoot;
        await git.Git(root, "clone", "--quiet", origin, alpha);
        await git.Git(alpha, "config", "user.email", "cultivate-test@localhost");
        await git.Git(alpha, "config", "user.name", "Cultivate isolated tests");
        // Git then cannot delete the long path below itself, which exercises deleting the folder directly.
        await git.Git(alpha, "config", "core.longpaths", "false");
        string Folder(string name) => Path.Combine(settings.WorktreesRoot, "Alpha", name);
        foreach (var name in new[] { "clean-work", "dirty-work", "later-work", "locked-work", "long-work", "open-work", "ahead-work", "spare-work" })
            await git.Git(alpha, "worktree", "add", "-b", name, Folder(name));
        await git.Git(alpha, "worktree", "add", "--detach", Folder("detached-work"));
        await git.Git(alpha, "worktree", "lock", Folder("locked-work"));
        async Task<string> Commit(string name, string file)
        {
            File.WriteAllText(Path.Combine(Folder(name), file), file);
            await git.Git(Folder(name), "add", "-A");
            await git.Git(Folder(name), "commit", "-m", "Local " + file);
            return await git.Git(Folder(name), "rev-parse", "HEAD");
        }
        var aheadCommit = await Commit("ahead-work", "ahead.txt");
        await Commit("spare-work", "spare.txt");
        await Commit("detached-work", "loose.txt");
        var keep = Path.Combine(root, "shared packages");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "keep.txt"), "must survive");
        Directory.CreateDirectory(Path.Combine(Folder("clean-work"), "bin"));
        File.WriteAllText(Path.Combine(Folder("clean-work"), "bin", "ignored.dll"), "build output");
        await Commands.Run("cmd.exe", ["/c", "mklink", "/J", Path.Combine(Folder("clean-work"), "bin", "linked"), keep]);
        File.WriteAllText(Path.Combine(Folder("dirty-work"), "notes.txt"), "not committed");
        var deep = Path.Combine([Folder("long-work"), "bin", .. Enumerable.Repeat(new string('d', 60), 5)]);
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "long path");

        var checkouts = await WorktreeCatalog.Discover(settings, git);
        Checkout Find(string name) => name == "detached-work"
            ? checkouts.Single(checkout => Files.Comparer.Equals(checkout.Path, Files.Full(Folder(name))))
            : checkouts.Single(checkout => checkout.Branch == name);
        var guard = new TidyGuard(null, root, Path.Combine(root, "program"));
        var candidates = new List<TidyCandidate>();
        foreach (var name in new[] { "clean-work", "dirty-work", "later-work", "ahead-work", "detached-work" })
            candidates.Add(new(Find(name), await WorktreeCatalog.Status(git, Find(name)), TidySizes.Measure(Find(name).Path).Bytes));
        var review = new TidyReview(candidates);
        Check(candidates[3].Status is { Unpushed: 1, Uncommitted: 0 } && candidates[4].Status is { Unpushed: 1, Uncommitted: 0 } &&
            candidates[4].Checkout.Detached, "the fixture has a branch worktree and a detached worktree, each with one unpushed commit");
        Check(review.Safe.Select(candidate => candidate.Checkout.Path).SequenceEqual(new[] { "clean-work", "later-work", "ahead-work" }.Select(name => Find(name).Path)) &&
            review.Risky.Select(candidate => candidate.Checkout.Path).SequenceEqual(new[] { "dirty-work", "detached-work" }.Select(name => Find(name).Path)),
            "the review of real worktrees puts an untracked file and a detached worktree's commit apart, and a branch's unpushed commit with the safe ones");
        var answers = new Queue<bool>([true, false]);
        var (targets, kept) = TidyRemoval.Confirm(review, _ => answers.Dequeue());
        Check(answers.Count == 0 && targets.Count == 3 && targets.All(target => !target.Force),
            "the safe worktrees are confirmed once and removed without --force");
        File.WriteAllText(Path.Combine(Folder("later-work"), "late.txt"), "saved after the review");
        var results = await TidyRemoval.RemoveAll(git, targets, guard);
        var listing = await git.Git(alpha, "worktree", "list", "--porcelain");
        bool Listed(string name) => listing.Contains(Folder(name).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Check(results.Single(result => result.Checkout.Branch == "clean-work").Removed && !Directory.Exists(Folder("clean-work")) &&
            !Listed("clean-work") && (await git.Git(alpha, "rev-parse", "--verify", "refs/heads/clean-work")).Length != 0,
            "deleting a clean worktree removes its folder and its registration and keeps its branch");
        Check(File.ReadAllText(Path.Combine(keep, "keep.txt")) == "must survive", "deleting a worktree never follows a junction inside it");
        Check(results.Single(result => result.Checkout.Branch == "later-work") is { Removed: false } later && later.Message.Contains("1 uncommitted change") &&
            Directory.Exists(Folder("later-work")) && File.Exists(Path.Combine(Folder("later-work"), "late.txt")),
            "a worktree that gained changes after the review is skipped, with the reason");
        Check(results.Single(result => result.Checkout.Branch == "ahead-work").Removed && !Directory.Exists(Folder("ahead-work")) &&
            !Listed("ahead-work") && await git.Git(alpha, "rev-parse", "--verify", "refs/heads/ahead-work") == aheadCommit,
            "a branch worktree with an unpushed commit is removed, and its branch still holds that commit");
        Check(kept.Select(candidate => candidate.Checkout.Path).SequenceEqual([Find("dirty-work").Path, Find("detached-work").Path]) &&
            Directory.Exists(Folder("dirty-work")) && Listed("dirty-work") && Directory.Exists(Folder("detached-work")) && Listed("detached-work"),
            "a dirty worktree and a detached worktree with unpushed commits are kept when their loss is not confirmed");
        Check(Capture(TidyRemoval.Summary(results, kept), 160, 30) is var summary && summary.Contains("Removed 2 of 5 worktrees") &&
            summary.Contains("Every branch was kept with all its commits, pushed or not") && summary.Contains("Not removed") &&
            summary.Contains("you chose to keep its 1 uncommitted change.") &&
            summary.Contains("you chose to keep its 1 commit that is on no branch or remote."),
            "the summary counts removals and explains every worktree left in place");

        var detachedTarget = new TidyTarget(Find("detached-work"), candidates[4].Status!, true, null);
        await Commit("detached-work", "second.txt");
        var detachedResult = await TidyRemoval.Remove(git, detachedTarget, guard);
        Check(!detachedResult.Removed && detachedResult.Message.Contains("2 commits that are on no branch or remote") && Directory.Exists(Folder("detached-work")),
            "a detached worktree that gained a commit after its loss was confirmed is skipped");

        answers = new Queue<bool>([true, true]);
        (targets, _) = TidyRemoval.Confirm(new TidyReview([candidates[1]]), _ => answers.Dequeue());
        results = await TidyRemoval.RemoveAll(git, targets, guard);
        Check(results.Single().Removed && !Directory.Exists(Folder("dirty-work")) &&
            (await git.Git(alpha, "rev-parse", "--verify", "refs/heads/dirty-work")).Length != 0,
            "a dirty worktree is removed once its loss is confirmed, and its branch is kept");

        var lockedTarget = new TidyTarget(Find("locked-work") with { Locked = false }, new(0, 0, null, ""), true, null);
        var primaryTarget = new TidyTarget(checkouts.Single(checkout => checkout.Primary) with { Primary = false }, new(0, 0, null, ""), true, null);
        var inUse = new TidyTarget(Find("open-work"), new(0, 0, null, ""), true, null);
        results = await TidyRemoval.RemoveAll(git, [lockedTarget, primaryTarget], guard);
        Check(results.All(result => !result.Removed) && results[0].Message.Contains("locked") && results[1].Message.Contains("main checkout") &&
            Directory.Exists(Folder("locked-work")) && File.Exists(Path.Combine(alpha, "readme.txt")),
            "the deletion routine refuses a locked worktree and a primary checkout even when they are passed to it directly");
        Check(!(await TidyRemoval.Remove(git, inUse, new TidyGuard(null, Path.Combine(Folder("open-work"), "sub"), root))).Removed &&
            Directory.Exists(Folder("open-work")), "the deletion routine refuses the worktree holding the current folder");

        var longTarget = new TidyTarget(Find("long-work"), await WorktreeCatalog.Status(git, Find("long-work")), false, null);
        var longResult = await TidyRemoval.Remove(git, longTarget, guard);
        Check(longResult.Removed && !Directory.Exists(Folder("long-work")) && !(await git.Git(alpha, "worktree", "list")).Contains("long-work"),
            "a worktree Git cannot delete because of a long path is removed by deleting its folder directly" + (longResult.Removed ? "" : ": " + longResult.Message));
        var held = Path.Combine(Folder("open-work"), "bin", "held.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        using (new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var heldResult = await TidyRemoval.Remove(git, inUse with { Force = false }, guard);
            Check(!heldResult.Removed && heldResult.Message.Length != 0 && File.Exists(held),
                "a worktree with a file held open is reported as not removed instead of stopping the rest");
        }

        var cache = Path.Combine(root, "sizes.json");
        var entries = await WorktreeTidy.Entries(git, await WorktreeCatalog.Discover(settings, git), guard, sizes: true, cache);
        using var sized = JsonDocument.Parse(JsonSerializer.Serialize(entries, Files.Json));
        var first = sized.RootElement.EnumerateArray().First(entry => entry.GetProperty("Branch").GetString() == "later-work");
        Check(sized.RootElement.GetArrayLength() == entries.Count && entries.All(entry => entry.Path != Files.Full(alpha)) &&
            new[] { "Repository", "Branch", "Ticket", "Path", "Protected", "LastCommit", "Status", "Uncommitted", "Unpushed", "Safe", "Bytes", "LastActivity" }
                .All(name => first.TryGetProperty(name, out _)) && first.GetProperty("Status").GetString() == "1 uncommitted" &&
            !first.GetProperty("Safe").GetBoolean() && first.GetProperty("Bytes").GetInt64() > 0,
            "worktrees --sizes prints every linked worktree with its state, safety, size, and last activity");
        Check(entries.Single(entry => entry.Branch == "spare-work") is { Safe: true, Unpushed: 1, Uncommitted: 0, Status: "1 unpushed" } &&
            entries.Single(entry => entry.Detached) is { Safe: false, Unpushed: 2 } && entries.Single(entry => entry.Branch == "locked-work") is { Safe: false },
            "the JSON marks a branch with unpushed commits safe, and a detached worktree's commits or a locked worktree unsafe");
        Check(entries.Single(entry => entry.Branch == "locked-work").Protected?.Contains("locked") == true &&
            TidySizes.Load(cache).GetValueOrDefault(Folder("later-work")) is { Bytes: > 0, Status.Uncommitted: 1, Head.Length: 40 },
            "the JSON names protected worktrees, and each measured worktree's scan is saved for the Tidy screen");
        var quick = await WorktreeTidy.Entries(git, await WorktreeCatalog.Discover(settings, git), guard, sizes: false, cache);
        using var unsized = JsonDocument.Parse(JsonSerializer.Serialize(quick, Files.Json));
        Check(unsized.RootElement.EnumerateArray().All(entry => !entry.TryGetProperty("Bytes", out _) && !entry.TryGetProperty("LastActivity", out _) &&
            entry.TryGetProperty("LastCommit", out _)), "worktrees without --sizes leaves out the slow size and activity fields");
    }
}
