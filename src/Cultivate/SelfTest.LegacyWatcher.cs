using System.ComponentModel;
using System.Diagnostics;

namespace Cultivate;

internal static partial class SelfTest
{
    private static Task CheckLegacyWatcher(string home)
    {
        var root = Path.Combine(home, "legacy");
        var farm = Path.Combine(root, "farm");
        Directory.CreateDirectory(farm);
        var session = Process.GetCurrentProcess().SessionId;
        var started = new List<Process>();
        // Fixture watchers are copies of ping.exe under this test's folder. Every candidate list below is limited to the processes
        // this test started (their open handles keep those ids from being reused), so a real WebfarmFileSync.exe is never touched.
        Process Start(string name)
        {
            var executable = Path.Combine(root, name + ".exe");
            if (!File.Exists(executable)) File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executable);
            var process = Process.Start(new ProcessStartInfo(executable, "-n 300 127.0.0.1")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Cannot start the fixture watcher.");
            started.Add(process);
            return process;
        }
        List<Process> Candidates() => Process.GetProcessesByName("WebfarmFileSync")
            .Where(process => started.Any(fixture => fixture.Id == process.Id && !fixture.HasExited)).ToList();
        List<Process> Found(string? configured, string? webfarm = null, int? inSession = null) =>
            LegacyWatcher.Running(webfarm ?? farm, configured, Candidates(), inSession ?? session);
        bool FoundOnly(List<Process> found, Process expected) => found.Count == 1 && found[0].Id == expected.Id;
        string Failure(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) { return exception.Message; }
            return "";
        }

        try
        {
            var watcher = Start("WebfarmFileSync");
            var other = Start("WebfarmFileSyncHelper");
            Check(FoundOnly(LegacyWatcher.Running(farm, farm, [.. Candidates(), other], session), watcher),
                "the old watcher in this session is found when WebfarmPath names the webfarm, and other programs are not");
            Check(FoundOnly(Found(farm + "\\"), watcher) && FoundOnly(Found(farm.ToUpperInvariant()), watcher) &&
                FoundOnly(Found(Path.Combine(root, "bin", "..", "farm")), watcher) && FoundOnly(Found(farm.Replace('\\', '/')), watcher) &&
                FoundOnly(Found(farm, farm + "\\"), watcher),
                "WebfarmPath matches the webfarm despite a trailing slash, letter case, dot segments, or forward slashes, like the bridge");
            Check(Found(null).Count == 0 && Found("").Count == 0 && Found("  ").Count == 0 && Found("farm").Count == 0 &&
                Found(farm[..2] + "farm").Count == 0 && Found(Path.GetPathRoot(farm)).Count == 0,
                "no watcher blocks when WebfarmPath is unset, relative, or a drive root, which the bridge does not match either");
            Check(Found(Path.Combine(root, "other farm")).Count == 0 && Found(root).Count == 0 && Found(Path.Combine(farm, "bin")).Count == 0,
                "a watcher for a different webfarm root, its parent, or a folder inside it does not block");
            Check(Found(farm, inSession: session + 1).Count == 0, "a watcher in another Windows session does not block");

            Check(LegacyWatcher.Decide(0, stop: false, canAsk: true) == LegacyWatcherAction.None &&
                LegacyWatcher.Decide(0, stop: true, canAsk: false) == LegacyWatcherAction.None, "nothing happens when no old watcher runs");
            Check(LegacyWatcher.Decide(1, stop: false, canAsk: true) == LegacyWatcherAction.Ask, "interactive activation asks before stopping it");
            Check(LegacyWatcher.Decide(1, stop: true, canAsk: true) == LegacyWatcherAction.Stop &&
                LegacyWatcher.Decide(2, stop: true, canAsk: false) == LegacyWatcherAction.Stop,
                "the setting or --stop-legacy-watcher stops it without asking");
            Check(LegacyWatcher.Decide(1, stop: false, canAsk: false) == LegacyWatcherAction.Refuse,
                "an activation that cannot ask and was not told to stop it refuses");

            var refused = Failure(() => LegacyWatcher.ForCommand(new Settings(), requested: false, Found(farm)));
            Check(refused.Contains($"process {watcher.Id}") && refused.Contains("--stop-legacy-watcher") &&
                refused.Contains("cultivate setup --stop-legacy-watcher true") && !watcher.HasExited,
                "cultivate activate refuses with the flag and the setting that let it proceed, and leaves the watcher running");
            Check(LegacyWatcher.ForWorker(new Settings(), Found(farm)) is null && !watcher.HasExited,
                "the saved-file worker leaves the watcher to the bridge's refusal when the setting is off");
            Check(LegacyWatcher.ForCommand(new Settings(), requested: true, []) is null &&
                LegacyWatcher.ForWorker(new Settings { StopLegacyWatcher = true }, []) is null, "nothing is stopped when no old watcher runs");

            var stopped = LegacyWatcher.ForCommand(new Settings(), requested: true, Found(farm));
            Check(watcher.HasExited && !other.HasExited && stopped is not null && stopped.Contains($"process {watcher.Id}") &&
                Found(farm).Count == 0, "--stop-legacy-watcher ends exactly the watcher it found and says which process it stopped");

            var second = Start("WebfarmFileSync");
            var third = Start("WebfarmFileSync");
            stopped = LegacyWatcher.ForCommand(new Settings { StopLegacyWatcher = true }, requested: false, Found(farm));
            Check(second.HasExited && third.HasExited && stopped is not null && stopped.Contains("processes ") &&
                stopped.Contains(second.Id.ToString()) && stopped.Contains(third.Id.ToString()), "the saved setting stops every old watcher in the session");

            var workerWatcher = Start("WebfarmFileSync");
            var snapshot = Path.Combine(root, "worker.json");
            Files.WriteJson(snapshot, new Settings { StopLegacyWatcher = true });
            var workerSettings = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(snapshot), Files.Json)!;
            stopped = LegacyWatcher.ForWorker(workerSettings, Found(farm));
            Check(workerSettings.StopLegacyWatcher && workerWatcher.HasExited && stopped is not null && stopped.Contains($"process {workerWatcher.Id}"),
                "the saved-file worker's settings snapshot keeps the setting, and the worker then stops the watcher itself");

            var stubborn = Start("WebfarmFileSync");
            var timedOut = Failure(() => LegacyWatcher.Stop(Found(farm), TimeSpan.FromMilliseconds(300), _ => { }));
            Check(timedOut.Contains($"process {stubborn.Id}") && timedOut.Contains("still running") && !stubborn.HasExited,
                "a watcher still running after the wait fails with its process id");
            var denied = Failure(() => LegacyWatcher.Stop(Found(farm), kill: _ => throw new Win32Exception(5)));
            Check(denied.Contains($"process {stubborn.Id}") && denied.Contains(new Win32Exception(5).Message) && !stubborn.HasExited,
                "a watcher Cultivate is not allowed to end fails with its process id and the reason");
        }
        finally
        {
            foreach (var process in started)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit();
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
        return Task.CompletedTask;
    }
}
