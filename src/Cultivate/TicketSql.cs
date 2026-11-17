namespace Cultivate;

/// <summary>A changed SQL file of a checkout, and where its copy in the SQL files folder is when there is one.</summary>
internal sealed record TicketSqlFile(string Name, string Repository, string Path, string? CopyPath);

internal sealed record TicketSqlComparison(string Folder, bool FolderExists, List<TicketSqlFile> Identical, List<TicketSqlFile> Different,
    List<TicketSqlFile> Missing, List<string> OnlyInFolder)
{
    public int Changed => Identical.Count + Different.Count + Missing.Count;
}

/// <summary>
/// Developers keep a second copy of every SQL file a ticket changes in &lt;SqlMirrorRoot&gt;\&lt;key&gt;, usually flat while the
/// SQL repository nests them, so copies are matched by file name. Table scripts are never committed, so files only in the folder are normal.
/// </summary>
internal static class TicketSql
{
    /// <param name="changed">Each changed file's repository, path relative to its checkout, and full path.</param>
    internal static TicketSqlComparison Compare(string folder, IEnumerable<(string Repository, string Path, string FullPath)> changed)
    {
        var exists = Directory.Exists(folder);
        var copies = exists ? Directory.EnumerateFiles(folder, "*.sql", SearchOption.AllDirectories).ToList() : [];
        var byName = copies.ToLookup(Path.GetFileName, Files.Comparer);
        var result = new TicketSqlComparison(folder, exists, [], [], [], []);
        var names = new HashSet<string>(Files.Comparer);
        foreach (var (repository, path, full) in changed.OrderBy(file => Path.GetFileName(file.Path), Files.Comparer))
        {
            var name = Path.GetFileName(full);
            names.Add(name);
            var matches = byName[name].ToList();
            var identical = matches.FirstOrDefault(copy => Same(full, copy));
            var copy = identical ?? matches.FirstOrDefault();
            var file = new TicketSqlFile(name, repository, path.Replace('\\', '/'), copy is null ? null : Path.GetRelativePath(folder, copy));
            (identical is not null ? result.Identical : copy is not null ? result.Different : result.Missing).Add(file);
        }
        result.OnlyInFolder.AddRange(copies.Where(copy => !names.Contains(Path.GetFileName(copy)))
            .Select(copy => Path.GetRelativePath(folder, copy)).Order(Files.Comparer));
        return result;
    }

    private static bool Same(string first, string second)
    {
        try { return Normalise(File.ReadAllText(first)) == Normalise(File.ReadAllText(second)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
