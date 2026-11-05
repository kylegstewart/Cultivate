using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cultivate;

internal static class Files
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meyer", "Cultivate");
    public static string OldApp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meyer", "WebfarmWorkspace", "App");
    public static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException($"Expected a relative path: {relative}");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
        if (!full.StartsWith(Full(root) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes its root: {relative}");
        for (var current = full; current is not null && current.Length >= Full(root).Length; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Linked files/directories are not supported: {current}");
        return full;
    }

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
}

internal sealed class Settings
{
    public string RepositoryRoot { get; set; } = "";
    public string BaseBranch { get; set; } = "master";
    public string WebfarmRoot { get; set; } = "";
    public string MSBuild { get; set; } = "";
    public bool WatchSavedFiles { get; set; } = true;
    public string StateRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meyer", "WebfarmWorkspace");

    public static async Task<Settings> Load()
    {
        var path = Path.Combine(Files.Home, "settings.json");
        Settings settings;
        if (File.Exists(path))
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Files.Json) ?? throw new InvalidDataException("Invalid settings.");
        else
        {
            using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(Files.OldApp, "settings.json")));
            settings = new() { RepositoryRoot = old.RootElement.GetProperty("RepositoryRoot").GetString()! };
        }
        if (string.IsNullOrWhiteSpace(settings.WebfarmRoot))
            settings.WebfarmRoot = Environment.GetEnvironmentVariable("WebfarmPath", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("WebfarmPath") ?? "";
        if (string.IsNullOrWhiteSpace(settings.MSBuild))
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            var result = await Commands.Run(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"]);
            settings.MSBuild = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Install Visual Studio MSBuild and the .NET Framework targeting packs.");
        }
        if (!File.Exists(settings.MSBuild)) throw new FileNotFoundException("Configured MSBuild was not found.", settings.MSBuild);
        Files.WriteJson(path, settings);
        return settings;
    }
}

internal sealed record Target(string Branch, string? Worktree, string Ref, bool Remote = false)
{
    public override string ToString() => Worktree is null ? $"{Branch}  |  committed branch" : $"{Branch}  |  {Worktree}";
}
internal sealed record Change(string Path, bool Deleted);
internal sealed record DeployFile(string Source, string RelativePath, bool Delete = false);
internal sealed record RuntimeFile(string Source, string RelativePath, string Hash);
internal sealed class Plan
{
    public required Target Target { get; init; }
    public required string Commit { get; init; }
    public required string Fingerprint { get; init; }
    public string BuildFingerprint { get; set; } = "";
    public List<Change> Changes { get; init; } = [];
    public List<DeployFile> Frontend { get; init; } = [];
    public List<ProjectInfo> Projects { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool CanDeploy => Errors.Count == 0;
}
internal sealed class DeploymentState
{
    public bool Active { get; set; }
    public string Branch { get; set; } = "";
    public string Token { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string LastError { get; set; } = "";
    public string Heartbeat { get; set; } = "";
    public string Mode { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public int WatcherPid { get; set; }
    public bool WatchEnabled { get; set; }
    public string BuildFingerprint { get; set; } = "";
    public List<RuntimeFile> RuntimeFiles { get; set; } = [];
}
