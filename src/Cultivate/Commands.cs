using System.Diagnostics;
using System.Text;

namespace Cultivate;

internal static class Commands
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, string? cwd = null,
        IReadOnlyDictionary<string, string>? environment = null, string? log = null, FailureContext? context = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd ?? Files.Home,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var argumentList = arguments.ToList();
        foreach (var argument in argumentList) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment) start.Environment[item.Key] = item.Value;
        context ??= new(Path.GetFileNameWithoutExtension(executable).Equals("git", StringComparison.OrdinalIgnoreCase) ? "Git" : "Command");
        int? exitCode = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            exitCode = process.ExitCode;
            var failure = process.ExitCode == 0 ? null : new InvalidOperationException(
                $"{Path.GetFileName(executable)} failed ({process.ExitCode}).\n{error}\n{output}" +
                (log is null ? "" : $"\nFull log: {log}"));
            if (log is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    await File.AppendAllTextAsync(log, $"\n[{DateTime.UtcNow:O}] {context.Stage} | {context.Project ?? "(no project)"}\n" +
                        $"Directory: {start.WorkingDirectory}\nCommand: {executable}\n" +
                        $"Arguments (JSON): {System.Text.Json.JsonSerializer.Serialize(argumentList)}\n" +
                        $"Exit code: {exitCode}; elapsed: {elapsed.Elapsed}\nSTDOUT:\n{output}\nSTDERR:\n{error}\n");
                }
                catch (Exception logging) when (failure is not null && logging is IOException or UnauthorizedAccessException)
                {
                    context = context with { LoggingError = "Could not save command log: " + logging.Message };
                }
            }
            if (failure is not null) throw failure;
            return output.TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Diagnostics.Attach(exception, context with { Tool = executable, ExitCode = exitCode, Log = log ?? context.Log });
            throw;
        }
    }
}
