using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Cultivate;

internal sealed record FailureContext(string Stage)
{
    public string? Project { get; init; }
    public string? Branch { get; init; }
    public string? Worktree { get; init; }
    public string? Snapshot { get; init; }
    public string? RunDirectory { get; init; }
    public string? Tool { get; init; }
    public int? ExitCode { get; init; }
    public string? Log { get; init; }
    public string? BinaryLog { get; init; }
    public string? LoggingError { get; init; }
}

internal sealed record FailureReport(string Label, string Summary, string Diagnosis, FailureContext Context,
    List<string> Errors, List<string> Hints, List<string> Logs)
{
    public string? Incident { get; init; }
    public string? LoggingError { get; init; }
    public string Brief => $"{Label}: {Context.Project ?? Context.Stage}: {Errors.FirstOrDefault() ?? Summary}" +
        (Logs.LastOrDefault() is { } log ? $"\nDetails: {log}" : "");

    public string Text
    {
        get
        {
            var text = new StringBuilder(Summary).AppendLine().AppendLine($"Stage: {Context.Stage}");
            if (Context.Project is { } project) text.AppendLine($"Project: {project}");
            if (Context.Branch is { } branch) text.AppendLine($"Branch: {branch}");
            if (Context.Worktree is { } worktree) text.AppendLine($"Worktree: {worktree}");
            if (Context.ExitCode is { } exit) text.AppendLine($"Exit code: {exit}");
            text.AppendLine($"Diagnosis: {Diagnosis}");
            if (Errors.Count != 0) text.AppendLine("Errors (first distinct messages):").AppendLine(string.Join("\n", Errors.Select(x => "  " + x)));
            text.AppendLine("Next checks:").AppendLine(string.Join("\n", Hints.Select(x => "  " + x)));
            if (Logs.Count != 0) text.AppendLine("Logs:").AppendLine(string.Join("\n", Logs.Select(x => "  " + x)));
            if (Incident is not null) text.AppendLine($"Incident: {Incident}");
            if (LoggingError is not null) text.AppendLine(LoggingError);
            return text.ToString().TrimEnd();
        }
    }
}

internal static class Diagnostics
{
    private static readonly object ContextKey = new();
    private static readonly Regex ErrorLine = new(@"\b(?:error(?:\s+[A-Z]+[0-9]+)?\s*:|fatal:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Code = new(@"\b(?:error|warning)\s+(?<code>[A-Z]+[0-9]+)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Preserve exception types and stack traces so existing retry and recovery behavior stays intact.
    internal static T Attach<T>(T exception, FailureContext context) where T : Exception
    {
        if (exception.Data[ContextKey] is FailureContext detail)
            context = detail with
            {
                Stage = detail.Stage is "Git" or "Command" ? context.Stage : detail.Stage,
                LoggingError = detail.LoggingError ?? context.LoggingError,
                Project = detail.Project ?? context.Project, Branch = detail.Branch ?? context.Branch,
                Worktree = detail.Worktree ?? context.Worktree, Snapshot = detail.Snapshot ?? context.Snapshot,
                RunDirectory = detail.RunDirectory ?? context.RunDirectory, Tool = detail.Tool ?? context.Tool,
                ExitCode = detail.ExitCode ?? context.ExitCode, Log = detail.Log ?? context.Log,
                BinaryLog = detail.BinaryLog ?? context.BinaryLog
            };
        exception.Data[ContextKey] = context;
        return exception;
    }

    internal static FailureReport Describe(Exception exception, string stage = "Operation")
    {
        var chain = new List<Exception>();
        for (var current = exception; current is not null; current = current.InnerException) chain.Add(current);
        var context = chain.Select(x => x.Data[ContextKey]).OfType<FailureContext>().FirstOrDefault() ?? new(stage);
        var lines = string.Join("\n", chain.Select(x => x.Message)).Split('\n').Select(x => x.Trim()).Where(x => x.Length != 0).ToList();
        var errors = lines.Where(line => ErrorLine.IsMatch(line)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6)
            .Select(line => Shorten(context.Snapshot is null ? line :
                line.Replace(Files.Full(context.Snapshot) + "\\", "", StringComparison.OrdinalIgnoreCase), 400)).ToList();
        var codes = Code.Matches(string.Join("\n", errors)).Select(x => x.Groups["code"].Value.ToUpperInvariant()).ToHashSet();
        var root = chain[^1];
        var diagnosis = "Operation failure";
        var hints = new List<string>();
        if (codes.Contains("MSB3644"))
        {
            diagnosis = "Missing .NET Framework targeting pack";
            hints.Add("Install the developer/targeting pack for the framework named in the error, then run cultivate doctor.");
        }
        else if (codes.Contains("MSB4019"))
        {
            diagnosis = "Missing imported build targets";
            hints.Add("Check the exact imported path. Restore its package or install the required build tools; run cultivate doctor to verify MSBuild.");
        }
        else if (codes.Any(x => x.StartsWith("NU", StringComparison.Ordinal)))
        {
            diagnosis = "Package restore error";
            hints.Add("Check the package ID/version and configured NuGet sources. For feed access failures, check connectivity and credentials.");
        }
        else if (codes.Overlaps(["CS1705", "CS0433"]))
        {
            diagnosis = "Assembly version conflict";
            hints.Add("Compare the assemblies and versions named in the error with the project's Reference/HintPath entries and the selected DLL paths in build.log.");
        }
        else if (codes.Overlaps(["CS0006", "CS0012", "CS0234", "CS0246", "CS1069"]))
        {
            diagnosis = "Type or assembly reference error";
            hints.Add("Check the reported type/namespace, using directives, and the project's declared DLL or package version.");
            hints.Add("If an unchanged project DLL is missing, build only that dependency separately. Cultivate does not rebuild unchanged projects.");
        }
        else if (codes.Any(x => x.StartsWith("CS", StringComparison.Ordinal)))
        {
            diagnosis = "C# compiler error";
            hints.Add("Fix the compiler errors at the listed source locations, save the files, and retry the affected project.");
        }
        else if (root is UnauthorizedAccessException || root is Win32Exception { NativeErrorCode: 5 } || codes.Overlaps(["MSB3021", "MSB3027"]))
        {
            diagnosis = "File access or copy failure";
            hints.Add("Check the source/destination paths and your access. If a file is locked, close only the process holding that file before retrying.");
        }
        else if (root is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            diagnosis = "Tool or working directory not found";
            hints.Add("Check the configured tool and working directory paths; run cultivate doctor to verify local prerequisites.");
        }
        else if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            diagnosis = "Missing file or directory";
            hints.Add("Check the missing path in the full report and the selected worktree. Build only a missing unchanged dependency if required.");
        }
        else if (context.Stage == "Open solution" && lines.Any(x =>
            x.Contains("0x80010001", StringComparison.OrdinalIgnoreCase) || x.Contains("0x8001010A", StringComparison.OrdinalIgnoreCase)))
        {
            diagnosis = "Visual Studio is busy";
            hints.Add("Let Visual Studio finish loading and close any modal dialogs, then retry Open solution.");
        }
        else
            hints.Add("Check the first error and the full report below. Run cultivate doctor if the failure concerns local configuration or build hooks.");
        if (errors.Count == 0 && context.ExitCode is not null and not 0)
            errors = lines.Skip(1).Where(x => !x.StartsWith("Full log:", StringComparison.Ordinal)).Take(5).Select(x => Shorten(x, 400)).ToList();
        var logs = new[] { context.Log, context.BinaryLog, context.RunDirectory is null ? null : Path.Combine(context.RunDirectory, "plan.json") }
            .OfType<string>().Where(File.Exists).Distinct(Files.Comparer).ToList();
        var summary = context.ExitCode is { } exit && exit != 0 ? $"{Path.GetFileName(context.Tool ?? "Command")} exited with code {exit}." :
            Shorten(lines.FirstOrDefault() ?? exception.GetType().Name, 400);
        return new($"{context.Stage.ToUpperInvariant()} FAILED", summary, diagnosis, context, errors, hints, logs)
        {
            LoggingError = context.LoggingError
        };
    }

    internal static FailureReport Record(Exception exception, string stage = "Operation", string? directory = null)
    {
        var report = Describe(exception, stage) with { Incident = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] };
        try
        {
            directory ??= Path.Combine(Files.Home, "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "failure-" + report.Incident + ".log");
            report.Logs.Add(path);
            var text = $"Cultivate {Files.Version}\nUTC: {DateTime.UtcNow:O}\n{report.Label}\n{report.Text}\n\nFull exception:\n{exception}\n";
            File.WriteAllText(path, text);
            File.WriteAllText(Path.Combine(directory, "last-error.log"), text);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            report.Logs.RemoveAll(path => !File.Exists(path));
            report = report with
            {
                LoggingError = string.Join("\n", new[]
                {
                    report.LoggingError, "Could not save all diagnostic logs: " + Shorten(failure.Message, 240)
                }.OfType<string>())
            };
        }
        return report;
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
