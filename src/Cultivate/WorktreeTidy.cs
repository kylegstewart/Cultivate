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
        var sizes = new ConcurrentDictionary<string, TidySize>(TidySizes.Load(TidySizes.CachePath), Files.Comparer);
        foreach (var row in list.Rows)
            if (sizes.TryGetValue(row.Path, out var size)) list.SetCachedSize(size);
        var work = Start(list, sizes);
        try
        {
            while (true)
            {
                var chosen = list.Show(AnsiConsole.Console, ReadKey);
                if (chosen is null) return;
                // A worktree being measured or checked holds open handles that would make its removal fail.
                await work.Stop();
                var removed = await Delete(chosen, state);
                foreach (var path in removed) sizes.TryRemove(path, out _);
                list.Remove(removed);
                TidySizes.Save(TidySizes.CachePath, sizes.Values);
                work = Start(list, sizes);
            }
        }
        finally
        {
            await work.Stop();
            TidySizes.Save(TidySizes.CachePath, sizes.Values);
        }
    }

    private static ConsoleKeyInfo? ReadKey()
    {
        for (var waited = 0; waited < 200; waited += 20)
        {
            if (Console.KeyAvailable) return Console.ReadKey(true);
            Thread.Sleep(20);
        }
        return null;
    }

    private sealed class Work(Task task, CancellationTokenSource cancel)
    {
        private bool stopped;

        public async Task Stop()
        {
            if (stopped) return;
            stopped = true;
            cancel.Cancel();
            await task;
            cancel.Dispose();
        }
    }

    /// <summary>Measures sizes (worktrees with no size first) and reads states in the background, posting each result to the screen.</summary>
    private Work Start(TidyList list, ConcurrentDictionary<string, TidySize> sizes)
    {
        var cancel = new CancellationTokenSource();
        var rows = list.Rows.ToList();
        var measuring = Measure(rows.Where(row => row.Measuring).OrderBy(row => row.Bytes is not null).Select(row => row.Path).ToList(),
            (path, measurement) =>
            {
                var measured = DateTimeOffset.Now;
                sizes[path] = new(path, measurement.Bytes, measured);
                list.Post(screen => screen.SetSize(path, measurement, measured));
            },
            (path, error) => list.Post(screen => screen.SetSizeError(path, error)), cancel.Token);
        var checking = Check(git, rows.Where(row => row.Checking).Select(row => row.Checkout).ToList(),
            (path, status) => list.Post(screen => screen.SetStatus(path, status)),
            (path, error) => list.Post(screen => screen.SetStatusError(path, error)), cancel.Token);
        return new(Task.WhenAll(measuring.ContinueWith(_ => TidySizes.Save(TidySizes.CachePath, sizes.Values), TaskScheduler.Default), checking), cancel);
    }

    internal static async Task Measure(IReadOnlyList<string> paths, Action<string, TidyMeasurement> measured, Action<string, string> failed, CancellationToken cancel)
    {
        try
        {
            await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = MeasureWorkers, CancellationToken = cancel }, (path, token) =>
            {
                try { measured(path, TidySizes.Measure(path, token)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed(path, exception.Message); }
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
    }

    internal static async Task Check(GitRepository git, IReadOnlyList<Checkout> checkouts, Action<string, CheckoutStatus> done,
        Action<string, string> failed, CancellationToken cancel)
    {
        try
        {
            await Parallel.ForEachAsync(checkouts, new ParallelOptions { MaxDegreeOfParallelism = StatusWorkers, CancellationToken = cancel }, async (checkout, _) =>
            {
                try { done(checkout.Path, await WorktreeCatalog.Status(git, checkout)); }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or FormatException or System.ComponentModel.Win32Exception)
                {
                    failed(checkout.Path, exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "");
                }
            });
        }
        catch (OperationCanceledException) { }
    }

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
        await Task.WhenAll(Check(git, linked, (path, status) => statuses[path] = status, (path, error) => errors[path] = error, CancellationToken.None),
            sizes ? Measure(linked.Select(checkout => checkout.Path).ToList(), (path, measurement) => measured[path] = measurement,
                (_, _) => { }, CancellationToken.None) : Task.CompletedTask);
        if (sizes)
        {
            var cache = TidySizes.Load(cachePath ??= TidySizes.CachePath);
            var now = DateTimeOffset.Now;
            foreach (var (path, measurement) in measured) cache[path] = new(path, measurement.Bytes, now);
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
