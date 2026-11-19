using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Cultivate;

/// <summary>
/// One worktree as <c>cultivate worktrees</c> prints it; size and last activity only with <c>--sizes</c>.
/// <see cref="Safe"/>: not protected, and deleting it would lose nothing (see <see cref="TidyLoss"/>).
/// </summary>
internal sealed record TidyEntry(string Repository, string Branch, string? Ticket, string Path, bool Detached, bool Locked, string? Protected,
    DateTimeOffset? LastCommit, string Status, int? Uncommitted, int? Unpushed, bool Safe, string? StatusError,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastActivity);

/// <summary>Lists every repository's worktrees with their size and age, and deletes the ones chosen.</summary>
internal sealed class WorktreeTidy(Settings settings, GitRepository git)
{
    // Measuring is disk-bound and gained nothing past 4 at once on 165 real worktrees; git status is mostly CPU, so it scales with cores.
    private const int MeasureWorkers = 4;
    private static readonly int StatusWorkers = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>The home screen's Tidy worktrees action.</summary>
    internal async Task Run(DeploymentState? state)
    {
        var checkouts = await AnsiConsole.Status().StartAsync("Finding every repository's worktrees...", _ => WorktreeCatalog.Discover(settings, git));
        var list = new TidyList(checkouts, TidyGuard.Current(state), () => DateTimeOffset.Now);
        var scans = TidySizes.Load(TidySizes.CachePath);
        foreach (var row in list.Rows)
            if (scans.TryGetValue(row.Path, out var scan)) list.SetScan(scan);
        await Scan(list, scans, Unscanned(list.Rows, scans));
        while (true)
        {
            switch (list.Show(AnsiConsole.Console, KeyScreen.ReadKey))
            {
                case TidyCommand.Rescan:
                    await Scan(list, scans, list.Rows.ToList());
                    break;
                case TidyCommand.Review:
                    var removed = await Delete(list.Selected.ToArray(), state);
                    foreach (var path in removed) scans.Remove(path);
                    list.Remove(removed);
                    TidySizes.Save(TidySizes.CachePath, scans.Values);
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>The worktrees a visit scans: ones with no saved scan, and ones whose HEAD has moved since. F5 scans every one again.</summary>
    internal static List<TidyRow> Unscanned(IEnumerable<TidyRow> rows, IReadOnlyDictionary<string, TidyScan> scans) =>
        rows.Where(row => !scans.TryGetValue(row.Path, out var scan) || scan.Head != row.Checkout.Head).ToList();

    /// <summary>
    /// The loading screen: measures and checks <paramref name="rows"/> all at once, shows the results in the list, and saves them
    /// so the next visit only scans worktrees that are new or have moved. A worktree that could not be fully read is not saved.
    /// </summary>
    private async Task Scan(TidyList list, Dictionary<string, TidyScan> scans, IReadOnlyList<TidyRow> rows)
    {
        if (rows.Count == 0) return;
        AnsiConsole.Clear();
        AnsiConsole.Write(TerminalStyle.Header("TIDY WORKTREES", rows.Count == list.Rows.Count
            ? "Scanning every worktree once; later visits only scan new ones."
            : $"Scanning {rows.Count} new or changed worktree{(rows.Count == 1 ? "" : "s")}."));
        var measured = new ConcurrentDictionary<string, TidyMeasurement>(Files.Comparer);
        var statuses = new ConcurrentDictionary<string, CheckoutStatus>(Files.Comparer);
        var sizeErrors = new ConcurrentDictionary<string, string>(Files.Comparer);
        var statusErrors = new ConcurrentDictionary<string, string>(Files.Comparer);
        int sized = 0, read = 0;
        string Progress() => $"Scanning worktrees: {Volatile.Read(ref sized)} of {rows.Count} measured, {Volatile.Read(ref read)} of {rows.Count} checked...";
        await AnsiConsole.Status().StartAsync(Progress(), context =>
        {
            void Sized()
            {
                Interlocked.Increment(ref sized);
                context.Status(Progress());
            }
            void Checked()
            {
                Interlocked.Increment(ref read);
                context.Status(Progress());
            }
            return Task.WhenAll(
                Measure(rows.Select(row => row.Path).ToList(), (path, measurement) => { measured[path] = measurement; Sized(); },
                    (path, error) => { sizeErrors[path] = error; Sized(); }),
                Check(git, rows.Select(row => row.Checkout).ToList(), (path, status) => { statuses[path] = status; Checked(); },
                    (path, error) => { statusErrors[path] = error; Checked(); }));
        });
        var now = DateTimeOffset.Now;
        foreach (var row in rows)
        {
            if (measured.TryGetValue(row.Path, out var measurement) && statuses.TryGetValue(row.Path, out var status))
            {
                scans[row.Path] = new(row.Path, row.Checkout.Head, measurement.Bytes, measurement.LastWrite, status, now);
                list.SetScan(scans[row.Path]);
                continue;
            }
            scans.Remove(row.Path);
            if (measured.TryGetValue(row.Path, out measurement)) list.SetSize(row.Path, measurement, now);
            else list.SetSizeError(row.Path, sizeErrors.GetValueOrDefault(row.Path, ""));
            if (statuses.TryGetValue(row.Path, out status)) list.SetStatus(row.Path, status);
            else list.SetStatusError(row.Path, statusErrors.GetValueOrDefault(row.Path, ""));
        }
        TidySizes.Save(TidySizes.CachePath, scans.Values);
    }

    internal static Task Measure(IReadOnlyList<string> paths, Action<string, TidyMeasurement> measured, Action<string, string> failed) =>
        Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = MeasureWorkers }, (path, _) =>
        {
            try { measured(path, TidySizes.Measure(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed(path, exception.Message); }
            return ValueTask.CompletedTask;
        });

    internal static Task Check(GitRepository git, IReadOnlyList<Checkout> checkouts, Action<string, CheckoutStatus> done, Action<string, string> failed) =>
        Parallel.ForEachAsync(checkouts, new ParallelOptions { MaxDegreeOfParallelism = StatusWorkers }, async (checkout, _) =>
        {
            try { done(checkout.Path, await WorktreeCatalog.Status(git, checkout)); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or FormatException or System.ComponentModel.Win32Exception)
            {
                failed(checkout.Path, exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "");
            }
        });

    /// <summary>Reviews the chosen worktrees, asks for confirmation, and deletes them. Returns the paths that were removed.</summary>
    private async Task<List<string>> Delete(TidyRow[] chosen, DeploymentState? state)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(TerminalStyle.Header("TIDY WORKTREES", "Review what will be deleted. Branches are always kept."));
        var candidates = await AnsiConsole.Status().StartAsync("Checking the selected worktrees for uncommitted changes and unpushed commits...",
            _ => Task.WhenAll(chosen.Select(Candidate)));
        var review = new TidyReview(candidates);
        AnsiConsole.Write(review.Render());
        var (targets, kept) = TidyRemoval.Confirm(review, question => AnsiConsole.Confirm(Terminal.Escape(question), false));
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{TerminalStyle.Muted}]Nothing was deleted.[/]");
            Terminal.Pause();
            return [];
        }
        // A deployment may have started in another window since the home screen read its state.
        try { state = await new Bridge(settings).Status() ?? state; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException) { }
        var guard = TidyGuard.Current(state);
        var results = await AnsiConsole.Status().StartAsync("Removing worktrees...", context => TidyRemoval.RemoveAll(git, targets, guard,
            (index, target) => context.Status($"Removing {index + 1} of {targets.Count}: {Terminal.Escape(TidyRow.NameOf(target.Checkout))}...")));
        AnsiConsole.Write(TidyRemoval.Summary(results, kept));
        Terminal.Pause();
        return results.Where(result => result.Removed).Select(result => result.Checkout.Path).ToList();
    }

    private async Task<TidyCandidate> Candidate(TidyRow row)
    {
        var bytes = row.Bytes;
        if (bytes is null)
            try { bytes = (await Task.Run(() => TidySizes.Measure(row.Path))).Bytes; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        try { return new(row.Checkout, await WorktreeCatalog.Status(git, row.Checkout), bytes); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or FormatException or System.ComponentModel.Win32Exception)
        {
            return new(row.Checkout, null, bytes, "Its state could not be read: " +
                (exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? ""));
        }
    }

    /// <summary><c>cultivate worktrees [--sizes]</c></summary>
    internal static async Task<int> Command(Settings settings, GitRepository git, string[] args)
    {
        DeploymentState? state = null;
        try { state = await new Bridge(settings).Status(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException) { }
        var entries = await Entries(git, await WorktreeCatalog.Discover(settings, git), TidyGuard.Current(state), args.Contains("--sizes"));
        Console.WriteLine(JsonSerializer.Serialize(entries, Files.Json));
        return 0;
    }

    internal static async Task<List<TidyEntry>> Entries(GitRepository git, IReadOnlyList<Checkout> checkouts, TidyGuard guard, bool sizes,
        string? cachePath = null)
    {
        var linked = checkouts.Where(checkout => !checkout.Primary).ToList();
        var statuses = new ConcurrentDictionary<string, CheckoutStatus>(Files.Comparer);
        var errors = new ConcurrentDictionary<string, string>(Files.Comparer);
        var measured = new ConcurrentDictionary<string, TidyMeasurement>(Files.Comparer);
        await Task.WhenAll(Check(git, linked, (path, status) => statuses[path] = status, (path, error) => errors[path] = error),
            sizes ? Measure(linked.Select(checkout => checkout.Path).ToList(), (path, measurement) => measured[path] = measurement,
                (_, _) => { }) : Task.CompletedTask);
        if (sizes)
        {
            var cache = TidySizes.Load(cachePath ??= TidySizes.CachePath);
            var now = DateTimeOffset.Now;
            foreach (var checkout in linked)
                if (measured.TryGetValue(checkout.Path, out var measurement) && statuses.TryGetValue(checkout.Path, out var status))
                    cache[checkout.Path] = new(checkout.Path, checkout.Head, measurement.Bytes, measurement.LastWrite, status, now);
            TidySizes.Save(cachePath, cache.Values);
        }
        return linked.Select(checkout =>
        {
            var status = statuses.GetValueOrDefault(checkout.Path);
            var measurement = measured.GetValueOrDefault(checkout.Path);
            var row = new TidyRow(checkout, guard.Check(checkout)) { Status = status, LastWrite = measurement?.LastWrite };
            return new TidyEntry(checkout.Repository, checkout.Branch, checkout.Ticket, checkout.Path, checkout.Detached, checkout.Locked,
                row.Protection, status?.LastCommit, row.StateText == "…" ? "unknown" : row.StateText, status?.Uncommitted, status?.Unpushed, row.Safe,
                errors.GetValueOrDefault(checkout.Path), measurement?.Bytes, sizes ? row.LastActivity : null);
        }).ToList();
    }
}
