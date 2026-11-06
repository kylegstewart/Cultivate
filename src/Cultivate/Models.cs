using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cultivate;

internal static class Files
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Per-user data folder: settings, logs, runs and worker state. Override with CULTIVATE_HOME.</summary>
    public static string Home => Environment.GetEnvironmentVariable("CULTIVATE_HOME") is { Length: > 0 } custom
        ? Full(custom) : Path.Combine(LocalAppData, "Meyer", "Cultivate");

    /// <summary>The original WebfarmWorkspace utility. Its journal folder is reused so earlier deployments stay restorable.</summary>
    public static string LegacyWorkspace => Path.Combine(LocalAppData, "Meyer", "WebfarmWorkspace");

    /// <summary>Package version plus the short commit it was built from, e.g. 1.1.0+a2bb547.</summary>
    public static string Version
    {
        get
        {
            var version = typeof(Files).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
            var plus = version.IndexOf('+');
            return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
        }
    }
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
    /// <summary>Assembly providing the Code Release Manager connection string (normally MyMeyer's bin\MeyerObjects.dll).</summary>
    public string MappingAssembly { get; set; } = "";
    public bool WatchSavedFiles { get; set; } = true;
    /// <summary>Show a notice in the terminal UI when a newer release is published.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Deployment journal, backups and per-destination locks.</summary>
    public string StateRoot { get; set; } = "";
    /// <summary>Folder whose worktrees, from any repository, can be opened with Open solution.</summary>
    public string WorktreesRoot { get; set; } = "";

    public static string FilePath => Path.Combine(Files.Home, "settings.json");

    /// <summary>Reads saved settings (or suggestions from the legacy utility) and fills discoverable defaults. Never writes.</summary>
    public static async Task<Settings> Read()
    {
        var settings = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(FilePath), Files.Json) ?? throw new InvalidDataException($"Invalid settings: {FilePath}")
            : FromLegacy() ?? new Settings();
        await settings.FillDefaults();
        return settings;
    }

    /// <summary>Loads settings for commands that need a configured workspace and saves any filled defaults.</summary>
    public static async Task<Settings> Load()
    {
        var settings = await Read();
        var problems = settings.Problems();
        if (problems.Count != 0)
            throw new InvalidOperationException("Cultivate is not set up yet:\n  " + string.Join("\n  ", problems) +
                "\nRun `cultivate setup`, then `cultivate doctor` to check the installation.");
        settings.Save();
        return settings;
    }

    public void Save() => Files.WriteJson(FilePath, this);

    public async Task FillDefaults()
    {
        if (string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "GitHub", "MyMeyer");
            if (File.Exists(Path.Combine(guess, "MyMeyer.sln"))) RepositoryRoot = guess;
        }
        if (string.IsNullOrWhiteSpace(BaseBranch)) BaseBranch = "master";
        if (string.IsNullOrWhiteSpace(WebfarmRoot))
            WebfarmRoot = Environment.GetEnvironmentVariable("WebfarmPath", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("WebfarmPath") ?? "";
        if (string.IsNullOrWhiteSpace(MSBuild)) MSBuild = await FindMSBuild() ?? "";
        if (string.IsNullOrWhiteSpace(MappingAssembly) && !string.IsNullOrWhiteSpace(RepositoryRoot))
            MappingAssembly = Path.Combine(RepositoryRoot, "bin", "MeyerObjects.dll");
        if (string.IsNullOrWhiteSpace(StateRoot))
            StateRoot = Directory.Exists(Files.LegacyWorkspace) ? Files.LegacyWorkspace : Path.Combine(Files.Home, "State");
        if (string.IsNullOrWhiteSpace(WorktreesRoot))
        {
            var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "GitHub", "t3-worktrees");
            if (Directory.Exists(guess)) WorktreesRoot = guess;
        }
    }

    public List<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(RepositoryRoot))
            problems.Add("RepositoryRoot is not set. Point it at your normal MyMeyer checkout.");
        else if (!File.Exists(Path.Combine(RepositoryRoot, "MyMeyer.sln")))
            problems.Add($"RepositoryRoot does not contain MyMeyer.sln: {RepositoryRoot}");
        if (string.IsNullOrWhiteSpace(BaseBranch)) problems.Add("BaseBranch is not set (normally master).");
        if (string.IsNullOrWhiteSpace(WebfarmRoot) || !Path.IsPathFullyQualified(WebfarmRoot))
            problems.Add(@"WebfarmRoot must be the absolute root of your dev site, for example \\webfarm-dev02\C$\inetpub\MyMeyer.");
        if (string.IsNullOrWhiteSpace(MSBuild))
            problems.Add("Visual Studio MSBuild was not found. Install Visual Studio or Build Tools with MSBuild and the .NET Framework targeting packs.");
        else if (!File.Exists(MSBuild)) problems.Add($"Configured MSBuild was not found: {MSBuild}");
        if (string.IsNullOrWhiteSpace(StateRoot) || !Path.IsPathFullyQualified(StateRoot))
            problems.Add("StateRoot must be an absolute folder for the deployment journal and backups.");
        return problems;
    }

    public static Task<string?> FindMSBuild() =>
        FindVisualStudioFile(@"MSBuild\**\Bin\MSBuild.exe", "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild");

    // Search IDE products so a newer SSMS or Build Tools installation cannot hide Visual Studio.
    public static Task<string?> FindVisualStudio() =>
        FindVisualStudioFile(@"Common7\IDE\devenv.exe", "-sort", "-prerelease");

    private static async Task<string?> FindVisualStudioFile(string pattern, params string[] arguments)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var result = await Commands.Run(vswhere, arguments.Concat(["-utf8", "-find", pattern]));
        return result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(File.Exists);
    }

    private static Settings? FromLegacy()
    {
        var path = Path.Combine(Files.LegacyWorkspace, "App", "settings.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var legacy = JsonDocument.Parse(File.ReadAllText(path));
            string? Value(string name) => legacy.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
            return new Settings { RepositoryRoot = Value("RepositoryRoot") ?? "", MappingAssembly = Value("DependencyAssembly") ?? "" };
        }
        catch (JsonException) { return null; }
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
    /// <summary>The journaled webfarm files this version deployed.</summary>
    public List<JournalEntry> Files { get; set; } = [];
    /// <summary>Previously deployed files left as is because they changed outside Cultivate. Not stored by the bridge.</summary>
    public List<string> Kept { get; set; } = [];
    /// <summary>What the activation that returned this state changed on the webfarm. Not stored by the bridge.</summary>
    public List<UpdatedFile> Updated { get; set; } = [];
}
internal sealed record JournalEntry(string RelativePath);
internal sealed record UpdatedFile(string RelativePath, string Change)
{
    internal const string Built = "Built and copied", Reused = "Copied (unchanged build)", Synced = "Synced",
        Deleted = "Deleted", Restored = "Restored original", Kept = "Kept (changed outside Cultivate)";
}
