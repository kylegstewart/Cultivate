using System.Diagnostics;

namespace Cultivate;

internal static class Watcher
{
    public static async Task Start(Settings settings, Bridge bridge, string token)
    {
        var workerSettings = Path.Combine(Files.Home, "Workers", token + ".json");
        Files.WriteJson(workerSettings, settings);
        using var process = Spawn(token, workerSettings);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Saved-file worker exited. See {Path.Combine(Files.Home, "Logs", "watch.log")}.");
            var state = await bridge.Status();
            if (state?.Token == token && state.WatcherPid == process.Id && DateTime.TryParse(state.Heartbeat, out _)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Files were deployed, but the saved-file worker did not report readiness. Check Cultivate status before editing.");
    }

    private static Process Spawn(string token, string workerSettings)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Cultivate.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Files.Home };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Cultivate.dll"));
        start.ArgumentList.Add("watch");
        start.ArgumentList.Add("--token");
        start.ArgumentList.Add(token);
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add(workerSettings);
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start the saved-file worker.");
    }

    /// <summary>Identity of the executable file this process was started from; it changes when an update replaces it.</summary>
    internal static string? ExecutableStamp()
    {
        var path = Environment.ProcessPath;
        if (path is null || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}:{FileVersionInfo.GetVersionInfo(path).ProductVersion}" : null;
        }
        catch (IOException) { return null; }
    }

    public static async Task Run(Settings settings, string token, string? workerSettings = null)
    {
        var bridge = new Bridge(settings);
        var git = new GitRepository(settings);
        var planner = new Planner(settings, git, bridge);
        var pipeline = new Pipeline(settings, git, bridge);
        string? lastAttempt = null;
        var error = "";
        var log = Path.Combine(Files.Home, "Logs", "watch.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var launchedFrom = ExecutableStamp();
        while (true)
        {
            var state = await bridge.Status();
            if (state is null || !state.Active || state.Token != token || state.Mode != "Cultivate") return;
            var pulse = await bridge.Call("Heartbeat", new { Token = token, ProcessId = Environment.ProcessId, Error = error });
            if (!pulse.GetProperty("Current").GetBoolean()) return;
            // Cultivate was updated in place: hand over to the new executable between updates, never during one.
            if (workerSettings is not null && launchedFrom is not null && ExecutableStamp() is { } now && now != launchedFrom)
            {
                using var successor = Spawn(token, workerSettings);
                await File.AppendAllTextAsync(log, $"{DateTime.Now:O} Cultivate was updated; worker {Environment.ProcessId} handed over to {successor.Id}\n");
                return;
            }
            var target = new Target(state.Branch, string.IsNullOrEmpty(state.SourceRoot) ? null : state.SourceRoot, state.SourceRef);
            try
            {
                var inspected = await git.Inspect(target);
                if (inspected.Fingerprint != state.Fingerprint && inspected.Fingerprint != lastAttempt)
                {
                    await Task.Delay(800);
                    var stable = await git.Inspect(target);
                    if (stable.Fingerprint != inspected.Fingerprint) continue;
                    lastAttempt = stable.Fingerprint;
                    var plan = await planner.Create(target);
                    if (!plan.CanDeploy) throw new InvalidOperationException(string.Join("\n", plan.Errors));
                    var activation = pipeline.Activate(plan, message => File.AppendAllText(log, $"{DateTime.Now:O} {message}\n"), watch: false, workerUpdate: true);
                    while (!activation.IsCompleted)
                    {
                        await Task.WhenAny(activation, Task.Delay(2000));
                        if (!activation.IsCompleted)
                            await bridge.Call("Heartbeat", new { Token = token, ProcessId = Environment.ProcessId, Error = "" });
                    }
                    var updated = await activation;
                    token = updated.Token;
                    error = "";
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Xml.XmlException or System.Text.Json.JsonException)
            {
                var report = Diagnostics.Record(exception, "Automatic update");
                error = report.Brief;
                await File.AppendAllTextAsync(log, $"{DateTime.Now:O} {report.Label}\n{report.Text}\n{exception}\n");
                await bridge.Call("Heartbeat", new { Token = token, ProcessId = Environment.ProcessId, Error = error });
            }
            await Task.Delay(2000);
        }
    }
}
