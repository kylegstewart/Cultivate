using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cultivate;

/// <summary>
/// Writes release files to their folder. Files are staged and verified in the run folder first, then copied under a temporary name and renamed,
/// and each copy's hash is checked. Nothing already in the folder is deleted unless <c>--clean</c> asks, and then only files an earlier release
/// run wrote that still hold what it wrote; Cultivate's record of those lives under <c>Files.Home\Releases</c>, never in the folder itself.
/// </summary>
internal static class ReleaseFolder
{
    /// <summary>A file to release: its name in the folder, kind, repository source, the file on disk, and the assembly it must be.</summary>
    internal sealed record Source(string Name, string Kind, string Origin, string Path, string? Assembly, string? Version);
    /// <summary>A verified file in the run folder, at <see cref="Name"/> relative to the release folder.</summary>
    internal sealed record Staged(string Name, string Kind, string Origin, string Path, long Size, string Sha256, string? Version);

    private static string Root => Path.Combine(Files.Home, "Releases");

    private static string Key(string folder)
    {
        var name = new string(Path.GetFileName(folder).Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        return $"{name}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..12]}";
    }

    internal static ReleaseRecord Load(string folder)
    {
        var path = Path.Combine(Root, Key(folder) + ".json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ReleaseRecord>(File.ReadAllText(path), Files.Json) ?? new() { Folder = folder } : new() { Folder = folder }; }
        catch (JsonException) { return new() { Folder = folder }; }
    }

    /// <summary>Saves the full result beside the folder's record and in the run folder.</summary>
    internal static void Save(ReleaseResult result, string run)
    {
        Files.WriteJson(Path.Combine(run, "release.json"), result);
        Files.WriteJson(Path.Combine(Root, $"{Key(result.Folder)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.PR{result.PullRequest.Number}.result.json"), result);
    }

    /// <summary>
    /// Gives each file a name in the flat folder, copies it to <paramref name="stage"/> and verifies it there. Files sharing a name keep the first
    /// at the top level; each other one goes in a subfolder named after its source folder, with a warning. Names that differ only by case share
    /// a name too, since a Windows folder cannot hold both.
    /// </summary>
    internal static List<Staged> Stage(List<Source> sources, string stage, List<string> warnings)
    {
        var used = new Dictionary<string, string>(Files.Comparer);
        var staged = new List<Staged>();
        foreach (var source in sources)
        {
            var name = source.Name;
            if (!used.TryAdd(name, name))
            {
                var first = used[name];
                var directory = Path.GetDirectoryName(source.Origin) ?? "";
                name = Path.Combine(Path.GetFileName(directory) is { Length: > 0 } parent ? parent : "root", source.Name);
                if (!used.TryAdd(name, name) && !used.TryAdd(name = Path.Combine(directory.Replace('\\', '_'), source.Name), name))
                    throw new InvalidDataException($"More than one release file would be written as {name}.");
                warnings.Add(first == source.Name
                    ? $"More than one release file is named {source.Name}: {source.Origin} is written as {name}. Upload it to its own destination."
                    : $"{first} and {source.Name} differ only by case, so one folder cannot hold both: {source.Origin} is written as {name}. " +
                      "Check which one production needs; Git tracks both.");
            }
            var file = Files.Child(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(source.Path, file, true);
            var size = new FileInfo(file).Length;
            var hash = Files.Hash(file);
            string? version = null;
            if (source.Assembly is not null)
            {
                var identity = size == 0 ? null : AssemblyName.GetAssemblyName(file);
                if (identity is null || !Files.Comparer.Equals(identity.Name, source.Assembly) || source.Version is not null && identity.Version?.ToString() != source.Version)
                    throw new InvalidDataException($"{source.Origin} did not produce {source.Assembly}.dll {source.Version}; nothing was written to the release folder.");
                version = identity.Version?.ToString();
            }
            else if (hash != Files.Hash(source.Path))
                throw new InvalidDataException($"{name} could not be copied intact from the snapshot; nothing was written to the release folder.");
            else if (size == 0) warnings.Add($"{source.Origin} is empty on the base branch.");
            staged.Add(new(name, source.Kind, source.Origin, file, size, hash, version));
        }
        return staged;
    }

    /// <summary>Files in the folder other than <paramref name="written"/>, and whether an earlier release run wrote them.</summary>
    internal static List<ExistingFile> Existing(string folder, ReleaseRecord record, IEnumerable<string> written)
    {
        if (!Directory.Exists(folder)) return [];
        var skip = written.ToHashSet(Files.Comparer);
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path))
            .Where(name => !skip.Contains(name)).Order(Files.Comparer).Select(name =>
            {
                var path = Path.Combine(folder, name);
                var entry = record.Files.FirstOrDefault(file => Files.Comparer.Equals(file.Name, name));
                var origin = entry is null ? "Not written by Cultivate" :
                    $"Written by Cultivate for PR #{entry.PullRequest} on {entry.WrittenAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                    (Files.Hash(path) == entry.Sha256 ? "" : ", changed since");
                return new ExistingFile(name, new FileInfo(path).Length, origin);
            }).ToList();
    }

    /// <summary>The files an earlier release run wrote to the folder that still hold exactly what it wrote: all <c>--clean</c> removes.</summary>
    internal static List<RecordedFile> Removable(string folder, ReleaseRecord record) => record.Files.Where(entry =>
    {
        var path = Files.Child(folder, entry.Name);
        return File.Exists(path) && Files.Hash(path) == entry.Sha256;
    }).ToList();

    /// <summary>Copies the verified files into the release folder and records them; with <c>--clean</c> first removes Cultivate's unchanged files.</summary>
    internal static void Write(List<Staged> staged, ReleaseResult result, int number)
    {
        var folder = result.Folder;
        Directory.CreateDirectory(folder);
        var record = Load(folder);
        record.Folder = folder;
        try
        {
            if (result.Clean)
                foreach (var entry in Removable(folder, record))
                {
                    File.Delete(Files.Child(folder, entry.Name));
                    record.Files.Remove(entry);
                    result.Removed.Add(entry.Name);
                }
            record.Files.RemoveAll(entry => !File.Exists(Path.Combine(folder, entry.Name)));
            result.Existing.AddRange(Existing(folder, record, staged.Select(file => file.Name)));
            foreach (var file in staged)
            {
                var destination = Files.Child(folder, file.Name);
                var replaced = File.Exists(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
                try
                {
                    File.Copy(file.Path, temporary);
                    File.Move(temporary, destination, true);
                }
                finally { File.Delete(temporary); }
                if (Files.Hash(destination) != file.Sha256) throw new IOException($"{destination} does not match the verified file after copying. Build again.");
                record.Files.RemoveAll(entry => Files.Comparer.Equals(entry.Name, file.Name));
                record.Files.Add(new(file.Name, file.Sha256, number, DateTimeOffset.Now));
                result.Files.Add(new(file.Name, file.Kind, file.Origin, file.Size, file.Sha256, file.Version, replaced));
            }
        }
        finally
        {
            record.Files.Sort((left, right) => Files.Comparer.Compare(left.Name, right.Name));
            Files.WriteJson(Path.Combine(Root, Key(folder) + ".json"), record);
        }
    }
}
