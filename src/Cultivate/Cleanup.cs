using System.Collections.Concurrent;
using System.Text.Json;

namespace Cultivate;

/// <summary>One of Cultivate's own files or folders whose retention period has passed.</summary>
internal sealed record CleanupItem(string Category, string Path, long Bytes);

/// <summary>A path and why it was kept, or why removing it failed.</summary>
internal sealed record CleanupNote(string Path, string Reason);

internal sealed record CleanupCategory(string Name, string Rule, int Count, long Bytes);

/// <summary>
/// What a cleanup of Cultivate's data folder found (<see cref="DryRun"/>) or removed, by category; what was kept although old enough, and why;
/// what failed; and how many registrations of snapshot worktrees whose folder is gone were (or would be) pruned.
/// </summary>
internal sealed record CleanupReport(bool DryRun, DateTimeOffset At, List<CleanupCategory> Categories, List<CleanupNote> Kept, List<CleanupNote> Failed,
    int SnapshotWorktrees)
{
    public int Count => Categories.Sum(category => category.Count);
    public long Bytes => Categories.Sum(category => category.Bytes);
}

internal sealed record CleanupStamp(DateTimeOffset LastRun, CleanupReport? Report);

/// <summary>The deployment state a cleanup protects; <see cref="Known"/> is false when it could not be read, and then no run or worker file is removed.</summary>
internal sealed record CleanupDeployment(DeploymentState? State, bool Known)
{
    public static async Task<CleanupDeployment> Read(Bridge bridge)
    {
        try { return new(await bridge.Status(), true); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            return new(null, false);
        }
    }
}

/// <summary>
/// Retention for Cultivate's data folder. Runs hold build output and logs for one activation, worker update, plan snapshot or package restore;
/// only the active deployment's run is reused later (its verified DLLs, for frontend-only worker updates). The deployment journal and its backups
/// live under <see cref="Settings.StateRoot"/> and are never touched.
/// </summary>
internal static class CultivateFiles
{
    internal static readonly TimeSpan RunAge = TimeSpan.FromDays(7), LogAge = TimeSpan.FromDays(14), LeftoverAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(23);
    internal const string Trash = ".deleting-";
    internal static string StampPath(string home) => Path.Combine(home, "cleanup.json");
    private static readonly HashSet<string> KeptLogs = new(["watch.log", "update.log", "last-error.log", "cleanup.log"], Files.Comparer);
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, ReturnSpecialDirectories = false
    };

    internal static readonly IReadOnlyList<(string Name, string Rule)> Categories =
    [
        ("Runs", "build and plan runs older than 7 days, except the active deployment's and those a recent error names"),
        ("Logs", "bridge logs and incident reports older than 14 days"),
        ("Workers", "worker settings older than 1 day that no running worker uses"),
        ("Requests", "bridge requests older than 1 day, left by an interrupted call"),
        ("Tests", "self-test fixtures older than 1 day, left by an interrupted self-test"),
        ("Updates", "update downloads older than 1 day, left by an interrupted update"),
        ("Staging", "an earlier version's leftovers older than 1 day"),
        ("Worktrees", "empty folders older than 1 day"),
        ("Other", "the old worktree-sizes.json and unfinished .tmp writes older than 1 day")
    ];

    /// <summary>The newest write of an entry or anything under it, and its size, without following links.</summary>
    internal static (long Bytes, DateTimeOffset LastWrite) Measure(FileSystemInfo entry)
    {
        var newest = entry.LastWriteTimeUtc;
        long bytes = 0;
        if (entry is FileInfo file) bytes = file.Length;
        else if (entry.LinkTarget is null)
            foreach (var item in ((DirectoryInfo)entry).EnumerateFileSystemInfos("*", Walk))
            {
                if (item is FileInfo inner) bytes += inner.Length;
                if (item.LastWriteTimeUtc > newest) newest = item.LastWriteTimeUtc;
            }
        return (bytes, new(newest, TimeSpan.Zero));
    }

    /// <summary>Everything whose retention period has passed, and what is kept although it is old enough.</summary>
    internal static (List<CleanupItem> Items, List<CleanupNote> Kept) Find(string home, Settings settings, CleanupDeployment deployment, DateTimeOffset now)
    {
        var items = new List<CleanupItem>();
        var kept = new List<CleanupNote>();
        var live = deployment.State?.Active == true ? deployment.State : null;
        var liveRuns = live is null ? [] : live.RuntimeFiles.Select(file => file.Source).Append(live.WorkspaceRoot).Where(path => path.Length != 0).ToList();
        var reports = Reports(home, now, deployment.State);
        IEnumerable<FileSystemInfo> Entries(string folder)
        {
            try { return new DirectoryInfo(Path.Combine(home, folder)).EnumerateFileSystemInfos().ToList(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        }
        void Consider(string category, FileSystemInfo entry, TimeSpan age, string? protection = null)
        {
            (long Bytes, DateTimeOffset LastWrite) measured;
            try { measured = Measure(entry); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; } // Removed while looking.
            if (!entry.Name.StartsWith(Trash, StringComparison.Ordinal) && now - measured.LastWrite < age) return;
            var reason = Journal(settings, entry.FullName) ?? protection ??
                (reports.Any(text => text.Contains(entry.FullName, StringComparison.OrdinalIgnoreCase)) ? "A recent error report names it." : null);
            if (reason is null) items.Add(new(category, entry.FullName, measured.Bytes));
            else kept.Add(new(entry.FullName, reason));
        }
        if (!deployment.Known)
            kept.Add(new(Path.Combine(home, "Runs"), "The deployment state could not be read, so no run or worker file was removed."));
        else
        {
            foreach (var run in Entries("Runs"))
                Consider("Runs", run, RunAge, liveRuns.Any(path => TidyGuard.Inside(run.FullName, path)) ? "The active deployment uses it." : null);
            var workers = Entries("Workers").OfType<FileInfo>().ToList();
            // The worker keeps the settings file it was started with across updates, while each update gives the deployment a new token.
            var newest = workers.Where(file => file.Extension == ".json").MaxBy(file => file.LastWriteTimeUtc);
            foreach (var worker in workers)
                Consider("Workers", worker, LeftoverAge,
                    deployment.State is { Token.Length: > 0 } state && Files.Comparer.Equals(worker.Name, state.Token + ".json") ||
                    live is not null && worker == newest ? "The automatic-update worker uses it." : null);
        }
        foreach (var log in Entries("Logs").OfType<FileInfo>().Where(file => !KeptLogs.Contains(file.Name))) Consider("Logs", log, LogAge);
        foreach (var name in new[] { "Requests", "Tests", "Staging" })
            foreach (var entry in Entries(name)) Consider(name, entry, LeftoverAge);
        foreach (var entry in Entries("Updates").Where(entry => !Files.Comparer.Equals(entry.Name, "update.lock"))) Consider("Updates", entry, LeftoverAge);
        bool Empty(DirectoryInfo folder)
        {
            try { return folder.LinkTarget is null && !folder.EnumerateFileSystemInfos().Any(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        }
        foreach (var folder in Entries("Worktrees").OfType<DirectoryInfo>().Where(Empty)) Consider("Worktrees", folder, LeftoverAge);
        foreach (var file in Entries("").OfType<FileInfo>().Where(file => file.Name == "worktree-sizes.json" || file.Extension == ".tmp"))
            Consider("Other", file, LeftoverAge);
        return (items, kept);
    }

    private static string? Journal(Settings settings, string path) =>
        new[] { settings.StateRoot, Files.LegacyWorkspace }.Any(root => TidyGuard.Inside(root, path) || TidyGuard.Inside(path, root))
            ? "It holds the deployment journal and its backups." : null;

    /// <summary>The latest error, the incident reports still kept, and the active deployment's error, which name the logs and runs they explain.</summary>
    private static List<string> Reports(string home, DateTimeOffset now, DeploymentState? state)
    {
        var texts = new List<string>();
        if (state?.LastError is { Length: > 0 } error) texts.Add(error);
        try
        {
            foreach (var file in new DirectoryInfo(Path.Combine(home, "Logs")).EnumerateFiles("*.log"))
                if (file.Name == "last-error.log" || file.Name.StartsWith("failure-", StringComparison.Ordinal) && now - file.LastWriteTimeUtc < LogAge)
                    texts.Add(File.ReadAllText(file.FullName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return texts;
    }

    private enum Outcome { Removed, Gone, InUse, Failed }

    /// <summary>
    /// Removes one item. A folder is first renamed, which Windows refuses while any file inside it is open, so a run another Cultivate process
    /// is using is left in place, and two cleanups never both claim it. A renamed folder that cannot be deleted completely is finished next time.
    /// </summary>
    private static (Outcome Outcome, string Reason) Remove(CleanupItem item)
    {
        var path = item.Path;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return (Outcome.Removed, "");
            }
            if (!Directory.Exists(path)) return (Outcome.Gone, "");
            var name = Path.GetFileName(path);
            if (!name.StartsWith(Trash, StringComparison.Ordinal))
            {
                var claimed = Path.Combine(Path.GetDirectoryName(path)!, Trash + name + "-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.Move(path, claimed);
                path = claimed;
            }
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException) { return (Outcome.Gone, ""); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (Outcome.InUse, "In use by another program, so it was left in place.");
        }
        try
        {
            TidyRemoval.DeleteFolder(path);
            return (Outcome.Removed, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (Outcome.Failed, $"Only partly removed ({exception.Message}); the rest is removed by the next cleanup.");
        }
    }

    /// <summary>Holds the data folder's cleanup lock, so two Cultivate processes never clean at once; null when another holds it for longer than <paramref name="wait"/>.</summary>
    private static async Task<FileStream?> Lock(string home, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        Directory.CreateDirectory(home);
        while (true)
        {
            try { return new FileStream(Path.Combine(home, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
            catch (IOException) { return null; }
        }
    }

    /// <summary>Lists (<paramref name="dryRun"/>) or removes what retention lets go, then prunes snapshot worktree registrations whose folder is gone.</summary>
    internal static async Task<CleanupReport> Clean(string home, Settings settings, GitRepository git, CleanupDeployment deployment, DateTimeOffset now,
        bool dryRun, TimeSpan wait)
    {
        if (dryRun) return await Run(home, settings, git, deployment, now, true);
        using var guard = await Lock(home, wait);
        if (guard is null) return new(false, now, Summarize([]), [], [new(home, "Another Cultivate cleanup is running. Try again when it finishes.")], 0);
        return await Run(home, settings, git, deployment, now, false);
    }

    private static async Task<CleanupReport> Run(string home, Settings settings, GitRepository git, CleanupDeployment deployment, DateTimeOffset now, bool dryRun)
    {
        var (items, kept) = Find(home, settings, deployment, now);
        var failed = new List<CleanupNote>();
        var removed = new List<CleanupItem>();
        if (dryRun) removed = items;
        else
            foreach (var item in items)
            {
                var (outcome, reason) = Remove(item);
                if (outcome == Outcome.Removed) removed.Add(item);
                else if (outcome == Outcome.InUse) kept.Add(new(item.Path, reason));
                else if (outcome == Outcome.Failed) failed.Add(new(item.Path, reason));
            }
        var snapshots = deployment.Known ? await Snapshots(home, settings, git, items, dryRun, failed) : 0;
        return new(dryRun, now, Summarize(removed), kept, failed, snapshots);
    }

    private static List<CleanupCategory> Summarize(List<CleanupItem> items) => Categories.Select(category =>
    {
        var matching = items.Where(item => item.Category == category.Name).ToList();
        return new CleanupCategory(category.Name, category.Rule, matching.Count, matching.Sum(item => item.Bytes));
    }).ToList();

    /// <summary>
    /// Counts the MyMeyer and library repositories' registered worktrees under Runs whose folder is gone (or, in a dry run, would be),
    /// and prunes them. These are snapshots a crashed run left behind; their contents were copies, and Git forgets only missing folders.
    /// </summary>
    private static async Task<int> Snapshots(string home, Settings settings, GitRepository git, List<CleanupItem> items, bool dryRun, List<CleanupNote> failed)
    {
        var runs = Path.Combine(home, "Runs") + "\\";
        var repositories = new[] { settings.RepositoryRoot }.Concat(settings.LibraryRepositories ?? [])
            .Where(path => path.Length != 0 && Directory.Exists(path)).Distinct(Files.Comparer).ToList();
        var count = 0;
        foreach (var repository in repositories)
        {
            string listing;
            try { listing = await git.Git(repository, "worktree", "list", "--porcelain", "-z"); }
            catch (InvalidOperationException) { continue; }
            var stale = listing.Split("\0\0", StringSplitOptions.RemoveEmptyEntries)
                .Select(block => block.Split('\0').FirstOrDefault(field => field.StartsWith("worktree ", StringComparison.Ordinal))?[9..])
                .OfType<string>().Select(Files.Full)
                .Count(path => path.StartsWith(runs, StringComparison.OrdinalIgnoreCase) &&
                    (!Directory.Exists(path) || dryRun && items.Any(item => TidyGuard.Inside(item.Path, path))));
            if (stale == 0) continue;
            count += stale;
            if (dryRun) continue;
            try { await git.Git(repository, "worktree", "prune"); }
            catch (InvalidOperationException exception) { failed.Add(new(repository, "git worktree prune failed: " + exception.Message.Trim())); }
        }
        return count;
    }

    /// <summary>
    /// The automatic pass, run in the background by the terminal UI and the saved-file worker. It runs at most once per <see cref="Interval"/>
    /// across every Cultivate process, skips at once if another cleanup is running, and never throws: a failure is logged to Logs\cleanup.log.
    /// </summary>
    internal static async Task<CleanupReport?> Automatic(Settings settings, string? home = null, Func<DateTimeOffset>? clock = null)
    {
        home ??= Files.Home;
        try
        {
            using var guard = await Lock(home, TimeSpan.Zero);
            if (guard is null) return null;
            var now = (clock ?? (() => DateTimeOffset.Now))();
            var stamp = StampPath(home);
            if (LastRun(stamp) is { } last && last <= now && now - last < Interval) return null;
            CleanupReport? report = null;
            try { report = await Run(home, settings, new GitRepository(settings), await CleanupDeployment.Read(new Bridge(settings)), now, false); }
            finally { Files.WriteJson(stamp, new CleanupStamp(now, report)); }
            return report;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(home, "Logs"));
                File.AppendAllText(Path.Combine(home, "Logs", "cleanup.log"), $"{DateTime.Now:O} Automatic cleanup failed: {exception}\n");
            }
            catch (Exception logging) when (logging is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    internal static DateTimeOffset? LastRun(string stamp)
    {
        try { return JsonSerializer.Deserialize<CleanupStamp>(File.ReadAllText(stamp), Files.Json)?.LastRun; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// <summary>A worktree unused for the chosen number of days whose deletion loses nothing (see <see cref="TidyLoss"/>).</summary>
internal sealed record CleanupWorktree(Checkout Checkout, CheckoutStatus Status, long? Bytes, DateTimeOffset LastActivity)
{
    public string Name => TidyRow.NameOf(Checkout);
}

/// <summary>The worktrees that qualify, and how many of the others were kept because they are protected, hold work, were used recently, or could not be read.</summary>
internal sealed record CleanupWorktreeScan(int Days, List<CleanupWorktree> Qualifying, int Protected, int HoldingWork, int Recent, int Unreadable)
{
    public long Bytes => Qualifying.Sum(worktree => worktree.Bytes ?? 0);
}

internal sealed record CleanupWorktreeEntry(string Repository, string Branch, string Path, long? Bytes, DateTimeOffset LastActivity, int DaysUnused,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Result);

internal sealed record CleanupWorktreesReport(bool DryRun, int Days, int Count, long Bytes, int Protected, int HoldingWork, int Recent, int Unreadable,
    int Removed, long Freed, List<CleanupWorktreeEntry> Worktrees);

/// <summary>
/// Old worktrees with nothing to lose: Tidy's "safe" (no uncommitted or untracked changes, and for a detached worktree no commits that exist
/// nowhere else) and Tidy's age (the later of the last commit and the newest file write outside build folders) of at least the chosen days.
/// They are only ever deleted through <see cref="TidyRemoval"/>, which checks each one again just before deleting it and keeps its branch.
/// </summary>
internal static class CleanupWorktrees
{
    internal static async Task<CleanupWorktreeScan> Find(GitRepository git, IReadOnlyList<Checkout> checkouts, TidyGuard guard, int days, DateTimeOffset now,
        Dictionary<string, TidyScan> scans)
    {
        var linked = checkouts.Where(checkout => !checkout.Primary).ToList();
        var open = linked.Where(checkout => guard.Check(checkout) is null).ToList();
        var statuses = new ConcurrentDictionary<string, CheckoutStatus>(Files.Comparer);
        var unreadable = 0;
        await WorktreeTidy.Check(git, open, (path, status) => statuses[path] = status, (_, _) => Interlocked.Increment(ref unreadable));
        var cutoff = now.AddDays(-days);
        var safe = open.Where(checkout => statuses.TryGetValue(checkout.Path, out var status) && TidyLoss.Of(checkout, status).None).ToList();
        // A commit after the cutoff already makes a worktree recent; only the others need their files measured, and a saved scan at the same HEAD is reused, as Tidy does.
        var old = safe.Where(checkout => !(statuses[checkout.Path].LastCommit > cutoff)).ToList();
        var measured = new ConcurrentDictionary<string, TidyMeasurement>(Files.Comparer);
        await WorktreeTidy.Measure(old.Where(checkout => !(scans.TryGetValue(checkout.Path, out var scan) && scan.Head == checkout.Head))
            .Select(checkout => checkout.Path).ToList(), (path, measurement) => measured[path] = measurement, (_, _) => { });
        var qualifying = new List<CleanupWorktree>();
        foreach (var checkout in old)
        {
            var status = statuses[checkout.Path];
            if (measured.TryGetValue(checkout.Path, out var measurement))
                scans[checkout.Path] = new(checkout.Path, checkout.Head, measurement.Bytes, measurement.LastWrite, status, now);
            else if (scans.TryGetValue(checkout.Path, out var saved) && saved.Head == checkout.Head) scans[checkout.Path] = saved with { Status = status };
            else
            {
                unreadable++;
                continue;
            }
            var scan = scans[checkout.Path];
            var row = new TidyRow(checkout, null) { Status = status, LastWrite = scan.LastWrite };
            if (row.LastActivity is { } activity && activity <= cutoff) qualifying.Add(new(checkout, status, scan.Bytes, activity));
        }
        var holding = open.Count(checkout => statuses.TryGetValue(checkout.Path, out var status) && !TidyLoss.Of(checkout, status).None);
        return new(days, qualifying.OrderByDescending(worktree => worktree.Bytes).ThenBy(worktree => worktree.Name, Files.Comparer).ToList(),
            linked.Count - open.Count, holding, open.Count - holding - unreadable - qualifying.Count, unreadable);
    }

    /// <summary>Deletes the worktrees through Tidy's guarded removal, never with --force: one that has gained any work since is skipped.</summary>
    internal static Task<List<TidyResult>> Remove(GitRepository git, IReadOnlyList<CleanupWorktree> worktrees, TidyGuard guard, Action<int, TidyTarget>? starting = null) =>
        TidyRemoval.RemoveAll(git, worktrees.Select(worktree => new TidyTarget(worktree.Checkout, worktree.Status, false, worktree.Bytes)).ToList(), guard, starting);

    internal static CleanupWorktreesReport Report(CleanupWorktreeScan scan, DateTimeOffset now, IReadOnlyList<TidyResult>? results = null)
    {
        string? Result(CleanupWorktree worktree) => results?.FirstOrDefault(result => Files.Comparer.Equals(result.Checkout.Path, worktree.Checkout.Path))?.Message;
        var removed = results?.Where(result => result.Removed).ToList() ?? [];
        return new(results is null, scan.Days, scan.Qualifying.Count, scan.Bytes, scan.Protected, scan.HoldingWork, scan.Recent, scan.Unreadable,
            removed.Count, removed.Sum(result => result.Bytes ?? 0), scan.Qualifying.Select(worktree => new CleanupWorktreeEntry(worktree.Checkout.Repository,
                TidyRow.BranchOf(worktree.Checkout), worktree.Checkout.Path, worktree.Bytes, worktree.LastActivity, (int)(now - worktree.LastActivity).TotalDays,
                Result(worktree))).ToList());
    }
}

/// <summary><c>cultivate cleanup [--yes] [--worktrees] [--days N]</c></summary>
internal static class CleanupCommand
{
    internal static async Task<int> Run(Settings settings, GitRepository git, Bridge bridge, string[] args, string? home = null, DateTimeOffset? at = null)
    {
        home ??= Files.Home;
        var yes = args.Contains("--yes");
        var days = Days(args) ?? settings.CleanupDays;
        var now = at ?? DateTimeOffset.Now;
        var deployment = await CleanupDeployment.Read(bridge);
        var files = await CultivateFiles.Clean(home, settings, git, deployment, now, dryRun: !yes, TimeSpan.FromSeconds(30));
        var guard = TidyGuard.Current(deployment.State);
        var scans = TidySizes.Load(TidySizes.CachePath);
        var scan = await CleanupWorktrees.Find(git, await WorktreeCatalog.Discover(settings, git), guard, days, now, scans);
        List<TidyResult>? results = null;
        if (yes && args.Contains("--worktrees"))
        {
            // A deployment may have started since the state was read.
            results = await CleanupWorktrees.Remove(git, scan.Qualifying, TidyGuard.Current((await CleanupDeployment.Read(bridge)).State ?? deployment.State));
            foreach (var result in results.Where(result => result.Removed)) scans.Remove(result.Checkout.Path);
        }
        if (yes) TidySizes.Save(TidySizes.CachePath, scans.Values);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Home = home, Files = files, Worktrees = CleanupWorktrees.Report(scan, now, results),
            Note = yes && results is null && scan.Qualifying.Count != 0 ? "Worktrees are only listed. Add --worktrees with --yes to delete them; branches are kept." :
                yes ? null : "Nothing was changed. Add --yes to remove Cultivate's files, and --worktrees with --yes to delete these worktrees."
        }, Files.Json));
        return files.Failed.Count == 0 && results?.Any(result => !result.Removed && !result.Message.StartsWith("Skipped", StringComparison.Ordinal)) != true ? 0 : 1;
    }

    internal static int? Days(string[] args)
    {
        var index = Array.IndexOf(args, "--days");
        if (index < 0) return null;
        return index + 1 < args.Length && int.TryParse(args[index + 1], out var days) && days >= 1 ? days
            : throw new InvalidOperationException("--days needs a whole number of days, 1 or more.");
    }
}
