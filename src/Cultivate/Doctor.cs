using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Cultivate;

/// <summary>Per-user MSBuild hook locations shared by the installer, doctor, and build pipeline.</summary>
internal static class Hooks
{
    public const string FileName = "Meyer.Cultivate.targets";
    public const string LegacyFileName = "Meyer.WebfarmWorkspace.targets";
    public const string TargetsFileName = "Cultivate.Build.targets";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "MSBuild");

    internal static string Content(string targets)
    {
        var escaped = System.Security.SecurityElement.Escape(targets);
        return "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            $"  <Import Project=\"{escaped}\"\r\n" +
            $"          Condition=\"'$(CultivateRunDirectory)' != '' and Exists('{escaped}')\" />\r\n</Project>\r\n";
    }

    public static IEnumerable<string> Paths(string fileName) =>
        new[] { "Current", "15.0" }.Select(version => Path.Combine(Root, version, "Microsoft.Common.targets", "ImportAfter", fileName));

    /// <summary>The targets file a hook imports, or null when the hook is missing or unrecognized.</summary>
    public static string? ImportedTargets(string hook)
    {
        if (!File.Exists(hook)) return null;
        var match = Regex.Match(File.ReadAllText(hook), "<Import\\s+Project=\"([^\"]+)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }
}

/// <summary><c>cultivate doctor</c>: checks prerequisites, installation, hooks, and settings without changing anything.</summary>
internal static class Doctor
{
    private static int failures;
    private static int warnings;

    private static void Report(string level, string message)
    {
        var (label, color) = level switch { "ok" => ("  OK  ", "green"), "warn" => (" WARN ", "yellow"), _ => (" FAIL ", "red") };
        if (level == "warn") warnings++;
        if (level == "fail") failures++;
        AnsiConsole.MarkupLine($"[{color}]{label}[/] {Markup.Escape(message)}");
    }

    public static async Task<int> Run()
    {
        var install = AppContext.BaseDirectory.TrimEnd('\\');
        AnsiConsole.MarkupLine($"[bold]Cultivate {Markup.Escape(Files.Version)}[/]");
        AnsiConsole.MarkupLine($"[grey]Program:  {Markup.Escape(Environment.ProcessPath ?? install)}[/]");
        AnsiConsole.MarkupLine($"[grey]Data:     {Markup.Escape(Files.Home)}[/]\n");

        var runtime = new[] { "Bridge.ps1", "Cultivate.Journal.psm1", "Capture.targets", Hooks.TargetsFileName };
        var missing = runtime.Where(name => !File.Exists(Path.Combine(install, name))).ToList();
        Report(missing.Count == 0 ? "ok" : "fail", missing.Count == 0 ? "Runtime files are installed beside Cultivate.exe" :
            "Missing runtime files (reinstall Cultivate): " + string.Join(", ", missing));

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => Files.Comparer.Equals(entry.TrimEnd('\\'), install));
        Report(onPath ? "ok" : "warn", onPath ? "The 'cultivate' command is on PATH" :
            "This copy is not on PATH. Run install.ps1, then open a new terminal, to use the 'cultivate' command.");

        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        Report(File.Exists(powershell) ? "ok" : "fail", File.Exists(powershell) ? "Windows PowerShell 5.1 is available" : "Windows PowerShell was not found: " + powershell);

        try { Report("ok", (await Commands.Run("git.exe", ["--version"])).Trim()); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Report("fail", "Git for Windows is required on PATH: " + exception.Message); }

        CheckHooks(install);

        if (!Updater.IsInstalled) Report("warn", "Not an installed copy, so 'cultivate update' is unavailable here.");
        else
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var latest = await Updater.Fetch(cancel: timeout.Token);
                Report(Updater.Parse(latest.Version) > Updater.Current ? "warn" : "ok", Updater.Parse(latest.Version) > Updater.Current
                    ? $"Cultivate {latest.Version} is available. Run 'cultivate update'."
                    : $"Up to date with the latest release ({latest.Version}) from {Updater.Repository}");
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            { Report("warn", "Couldn't check for updates: " + exception.Message.Split('\n')[0]); }
        }

        var settings = await Settings.Read();
        if (!File.Exists(Settings.FilePath)) Report("warn", $"No saved settings yet ({Settings.FilePath}). Run 'cultivate setup'.");
        var problems = settings.Problems();
        foreach (var problem in problems) Report("fail", problem);
        if (problems.Count == 0)
        {
            Report("ok", $"Repository: {settings.RepositoryRoot}");
            try
            {
                await Commands.Run("git.exe", ["-C", settings.RepositoryRoot, "rev-parse", "--verify", "--quiet", settings.BaseBranch + "^{commit}"]);
                Report("ok", $"Baseline branch '{settings.BaseBranch}' exists");
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Report("fail", $"Baseline branch '{settings.BaseBranch}' was not found in {settings.RepositoryRoot}"); }
            Report("ok", $"MSBuild: {settings.MSBuild}");
            Report(Directory.Exists(settings.WebfarmRoot) ? "ok" : "warn", Directory.Exists(settings.WebfarmRoot)
                ? $"Webfarm reachable: {settings.WebfarmRoot}" : $"Webfarm not reachable right now: {settings.WebfarmRoot}");
            Report(File.Exists(settings.MappingAssembly) ? "ok" : "warn", File.Exists(settings.MappingAssembly)
                ? $"Mapping assembly: {settings.MappingAssembly}"
                : $"Mapping assembly not found (frontend deployments need it): {settings.MappingAssembly}");
            Report("ok", $"Deployment journal: {settings.StateRoot}");
        }

        if (Process.GetProcessesByName("WebfarmFileSync").Length != 0)
            Report("warn", "The old WebfarmFileSync watcher is running. Close it before activating with Cultivate.");

        if (problems.Count == 0 && missing.Count == 0)
        {
            try
            {
                var state = await new Bridge(settings).Status();
                Report("ok", state?.Active == true ? $"Active deployment: {state.Branch}" : "No active deployment");
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Text.Json.JsonException)
            { Report("fail", "Could not read the deployment journal: " + exception.Message.Split('\n')[0]); }
        }

        AnsiConsole.MarkupLine(failures == 0
            ? $"\n[green]Ready.[/] [grey]{warnings} warning(s).[/]"
            : $"\n[red]{failures} problem(s)[/] [grey]and {warnings} warning(s). Fix the FAIL items above.[/]");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckHooks(string install)
    {
        foreach (var hook in Hooks.Paths(Hooks.FileName))
        {
            var imported = Hooks.ImportedTargets(hook);
            if (imported is null) Report("fail", $"MSBuild hook missing: {hook}. Run install.ps1.");
            else if (!File.Exists(imported)) Report("fail", $"MSBuild hook points to a missing file: {imported}. Run install.ps1.");
            else if (!Files.Comparer.Equals(Path.GetDirectoryName(imported)?.TrimEnd('\\'), install))
                Report("warn", $"MSBuild hook uses another Cultivate install: {imported}");
            else Report("ok", $"MSBuild hook installed ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(hook))))})");
        }
        foreach (var legacy in Hooks.Paths(Hooks.LegacyFileName).Where(File.Exists))
            Report("warn", $"The old WebfarmWorkspace hook is still installed and duplicates Cultivate's: {legacy}. Run install.ps1 to migrate it.");
    }
}
