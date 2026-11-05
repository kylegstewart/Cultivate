using System.Diagnostics;
using System.Text;

namespace Cultivate;

internal static class Commands
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, string? cwd = null,
        IReadOnlyDictionary<string, string>? environment = null, string? log = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd ?? Files.Home,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (log is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            await File.AppendAllTextAsync(log, $"{executable} {string.Join(" ", arguments)}\n{output}\n{error}\n");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}).\n{error}\n{output}" +
                (log is null ? "" : $"\nFull log: {log}"));
        return output.TrimEnd('\r', '\n');
    }
}
