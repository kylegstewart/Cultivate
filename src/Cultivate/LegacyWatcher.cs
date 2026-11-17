using System.ComponentModel;
using System.Diagnostics;

namespace Cultivate;

internal enum LegacyWatcherAction { None, Ask, Stop, Refuse }

/// <summary>
/// The old WebfarmFileSync.exe watcher copies saved files straight to the webfarm in the user-scoped WebfarmPath, so it can overwrite
/// a Cultivate deployment there. Bridge.ps1 refuses to deploy while it runs; this finds the same processes and stops them.
/// </summary>
internal static class LegacyWatcher
{
    private const string ProcessName = "WebfarmFileSync";
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    /// <summary>The old watchers that Bridge.ps1 would refuse to deploy to <paramref name="webfarmRoot"/> beside.</summary>
    internal static List<Process> Running(string webfarmRoot) =>
        Running(webfarmRoot, Environment.GetEnvironmentVariable("WebfarmPath", EnvironmentVariableTarget.User),
            Process.GetProcessesByName(ProcessName), Process.GetCurrentProcess().SessionId);

    /// <summary>
    /// Matches the bridge's guard: the user-scoped WebfarmPath must name the same webfarm root, and only watchers in
    /// <paramref name="session"/> count.
    /// </summary>
    internal static List<Process> Running(string webfarmRoot, string? configured, IEnumerable<Process> candidates, int session)
    {
        if (Root(configured) is not { } root || !Files.Comparer.Equals(root, Root(webfarmRoot))) return [];
        var running = candidates.Where(process => Matches(process, session)).ToList();
        foreach (var process in running)
        {
            // Holding a handle keeps the process id from going to another process while the user decides.
            try { _ = process.Handle; }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
        }
        return running;
    }

    /// <summary>Whether to stop running watchers; <paramref name="stop"/> is the saved setting or a one-time request.</summary>
    internal static LegacyWatcherAction Decide(int running, bool stop, bool canAsk) =>
        running == 0 ? LegacyWatcherAction.None : stop ? LegacyWatcherAction.Stop : canAsk ? LegacyWatcherAction.Ask : LegacyWatcherAction.Refuse;

    /// <summary><c>cultivate activate</c>: stops the watchers when asked to or set to, and otherwise refuses with how to proceed.</summary>
    internal static string? ForCommand(Settings settings, bool requested, List<Process> running) =>
        Decide(running.Count, settings.StopLegacyWatcher || requested, canAsk: false) switch
        {
            LegacyWatcherAction.Stop => Stop(running),
            LegacyWatcherAction.Refuse => throw new InvalidOperationException(
                $"The old WebfarmFileSync watcher ({Ids(running)}) is running and could overwrite this deployment. " +
                "Activate again with --stop-legacy-watcher to stop it, or run `cultivate setup --stop-legacy-watcher true` to always stop it."),
            _ => null
        };

    /// <summary>
    /// The saved-file worker cannot ask, so it stops the watchers only when its activation saved the setting. Otherwise the bridge
    /// refuses the update and the worker reports that.
    /// </summary>
    internal static string? ForWorker(Settings settings, List<Process> running) =>
        Decide(running.Count, settings.StopLegacyWatcher, canAsk: false) == LegacyWatcherAction.Stop ? Stop(running) : null;

    /// <summary>Ends exactly <paramref name="processes"/>, waits for each to exit, and says what was stopped.</summary>
    internal static string Stop(List<Process> processes, TimeSpan? wait = null, Action<Process>? kill = null)
    {
        foreach (var process in processes)
        {
            try { (kill ?? (target => target.Kill()))(process); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                if (!Exited(process))
                    throw new InvalidOperationException($"Cultivate could not stop the old WebfarmFileSync watcher (process {process.Id}): " +
                        $"{exception.Message} End it in Task Manager, then activate again.", exception);
            }
        }
        var timeout = wait ?? ExitWait;
        foreach (var process in processes)
            if (!Exited(process, timeout))
                throw new InvalidOperationException($"The old WebfarmFileSync watcher (process {process.Id}) was still running " +
                    $"{timeout.TotalSeconds:0.#} seconds after Cultivate stopped it. End it in Task Manager, then activate again.");
        return $"Stopped the old WebfarmFileSync watcher ({Ids(processes)}) so it cannot overwrite this deployment.";
    }

    private static string Ids(List<Process> processes) =>
        (processes.Count == 1 ? "process " : "processes ") + string.Join(", ", processes.Select(process => process.Id));

    private static bool Matches(Process process, int session)
    {
        try { return process.SessionId == session && process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool Exited(Process process, TimeSpan? wait = null)
    {
        try { return wait is { } timeout ? process.WaitForExit(timeout) : process.HasExited; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Normalizes a webfarm root the way Get-WorkspacePath in Cultivate.Journal.psm1 does, or null where it would throw.</summary>
    private static string? Root(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path)!.EndsWith(':')) return null;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return full == Path.GetPathRoot(full)!.TrimEnd('\\') ? null : full;
    }
}
