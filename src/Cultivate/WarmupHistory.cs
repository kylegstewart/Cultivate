using System.Text.Json;

namespace Cultivate;

/// <summary>
/// How long recent successful warm-ups took, kept apart for deployments that changed <c>bin\</c> (which recompile every control)
/// and frontend-only ones, so the progress bar has an estimate to fill toward.
/// </summary>
internal sealed class WarmupHistory
{
    private const int Kept = 10, Considered = 5;
    public List<double> Compiled { get; set; } = [];
    public List<double> Frontend { get; set; } = [];

    /// <summary>The saved history, or an empty one when the file is missing or unreadable.</summary>
    internal static WarmupHistory Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WarmupHistory>(File.ReadAllText(path), Files.Json) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>The median of the recent warm-ups of this kind, or null before the first one.</summary>
    internal TimeSpan? Estimate(bool compiled)
    {
        var recent = (compiled ? Compiled : Frontend).TakeLast(Considered).Order().ToList();
        if (recent.Count == 0) return null;
        var middle = recent.Count / 2;
        var median = recent.Count % 2 == 1 ? recent[middle] : (recent[middle - 1] + recent[middle]) / 2;
        return TimeSpan.FromSeconds(Math.Max(1, median));
    }

    /// <summary>Adds a successful warm-up and saves; a history that cannot be saved is not worth interrupting anyone over.</summary>
    internal void Record(bool compiled, double seconds, string path)
    {
        var list = compiled ? Compiled : Frontend;
        list.Add(Math.Round(seconds, 1));
        if (list.Count > Kept) list.RemoveRange(0, list.Count - Kept);
        try { Files.WriteJson(path, this); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// How full the progress bar is: up to 90% at the estimate, then creeping toward but never reaching 100% until the site answers.
    /// Null without an estimate, which draws a moving bar instead.
    /// </summary>
    internal static double? Fraction(TimeSpan elapsed, TimeSpan? estimate)
    {
        if (estimate is not { } expected) return null;
        var ratio = Math.Max(0, elapsed.TotalSeconds) / expected.TotalSeconds;
        return ratio <= 1 ? 0.9 * ratio : Math.Min(0.99, 0.9 + 0.09 * (1 - Math.Exp(1 - ratio)));
    }
}
