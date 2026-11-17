using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json;

namespace Cultivate;

/// <summary>A worktree's measured size and the newest write among its own files, or null when it holds none.</summary>
internal sealed record TidyMeasurement(long Bytes, DateTimeOffset? LastWrite);

/// <summary>
/// A worktree's size and state as last scanned, saved so later visits show it without scanning again.
/// <see cref="Head"/> is the commit it was scanned at; a worktree whose HEAD has moved since is scanned again.
/// </summary>
internal sealed record TidyScan(string Path, string Head, long Bytes, DateTimeOffset? LastWrite, CheckoutStatus Status, DateTimeOffset Scanned);

internal static class TidySizes
{
    /// <summary>Build output, restored packages, and Git's own files change without anyone working in the worktree.</summary>
    internal static readonly HashSet<string> InactiveFolders = new([".git", "bin", "obj", "packages", "node_modules", ".vs"], Files.Comparer);

    internal static string CachePath => Path.Combine(Files.Home, "worktree-scans.json");

    // Hidden and system files take space too; only links are skipped, so a junction's target is never counted.
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false
    };

    private readonly record struct Entry(bool Directory, string? Path, bool Inactive, long Length, DateTimeOffset LastWrite);

    /// <summary>Totals every file under <paramref name="root"/> without following junctions or symbolic links.</summary>
    internal static TidyMeasurement Measure(string root)
    {
        long bytes = 0;
        DateTimeOffset? newest = null;
        var folders = new Stack<(string Path, bool Inactive)>();
        folders.Push((root, false));
        while (folders.Count != 0)
        {
            var (folder, inactive) = folders.Pop();
            try
            {
                var entries = new FileSystemEnumerable<Entry>(folder, (ref FileSystemEntry entry) => new(entry.IsDirectory,
                    entry.IsDirectory ? entry.ToFullPath() : null, InactiveFolders.Contains(entry.FileName.ToString()),
                    entry.Length, entry.LastWriteTimeUtc), Options);
                foreach (var entry in entries)
                {
                    if (entry.Directory)
                    {
                        folders.Push((entry.Path!, inactive || entry.Inactive));
                        continue;
                    }
                    bytes += entry.Length;
                    if (!inactive && !entry.Inactive && (newest is null || entry.LastWrite > newest)) newest = entry.LastWrite;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } // Removed or unreadable while measuring.
        }
        return new(bytes, newest);
    }

    internal static Dictionary<string, TidyScan> Load(string path)
    {
        try
        {
            var scans = JsonSerializer.Deserialize<List<TidyScan>>(File.ReadAllText(path), Files.Json) ?? [];
            return scans.Where(scan => scan.Path is { Length: > 0 } && scan.Head is not null && scan.Status is not null)
                .DistinctBy(scan => scan.Path, Files.Comparer).ToDictionary(scan => scan.Path, Files.Comparer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(Files.Comparer); // A missing or damaged cache only means worktrees are scanned again.
        }
    }

    /// <summary>Saves the scans of worktrees that still exist; a failed save only means the next visit scans them again.</summary>
    internal static void Save(string path, IEnumerable<TidyScan> scans)
    {
        try
        {
            Files.WriteJson(path, scans.Where(scan => Directory.Exists(scan.Path)).OrderBy(scan => scan.Path, Files.Comparer).ToList());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0} KB"),
        _ => $"{bytes} B"
    };
}
