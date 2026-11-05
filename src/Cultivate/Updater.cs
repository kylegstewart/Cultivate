using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace Cultivate;

internal sealed record ReleaseAsset(string Name, string Url, long Size);
internal sealed record Release(string Version, string Tag, string Page, string Notes, ReleaseAsset Package, ReleaseAsset? Checksum);
internal sealed record UpdateInfo(string Version, string Tag, string Page);

/// <summary>
/// In-place updates from GitHub Releases. The release workflow publishes Cultivate-&lt;version&gt;-win-x64.zip and a
/// .sha256 file; <c>cultivate update</c> downloads, verifies, and swaps the installed files without a reinstall.
/// </summary>
internal static class Updater
{
    private static readonly string[] RequiredFiles =
        ["Cultivate.exe", "Bridge.ps1", "Cultivate.Journal.psm1", "Capture.targets", Hooks.TargetsFileName, "Cultivate.ico"];
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    public static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>True for copies installed by install.ps1 (which writes installation.json); false for build output.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "installation.json"));

    /// <summary>GitHub "owner/name" that publishes releases. Baked in at build time; CULTIVATE_UPDATE_REPOSITORY overrides.</summary>
    public static string Repository =>
        Environment.GetEnvironmentVariable("CULTIVATE_UPDATE_REPOSITORY") is { Length: > 0 } custom ? custom :
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateRepository")?.Value ?? "";

    public static Version Current => Parse(Files.Version) ?? new Version(0, 0, 0);

    /// <summary>Parses "v1.2.3", "1.2.3+sha" or "1.2" as a three-part version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['+', '-', ' ']);
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out var version) && version.Revision <= 0
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    /// <summary><c>cultivate update [--check] [--version X.Y.Z] [--yes]</c></summary>
    public static async Task<int> Run(string[] args)
    {
        var index = Array.IndexOf(args, "--version");
        var requested = index >= 0 ? (index + 1 < args.Length ? args[index + 1] : throw new InvalidOperationException("--version needs a value, e.g. 1.2.0.")) : null;
        if (requested is not null && Parse(requested) is null) throw new InvalidOperationException($"'{requested}' is not a version like 1.2.0.");

        var release = await AnsiConsole.Status().StartAsync($"Checking {Repository} for releases...", _ => Fetch(requested));
        var newer = Parse(release.Version) > Current;
        if (args.Contains("--check"))
        {
            AnsiConsole.MarkupLine(newer
                ? $"[{TerminalStyle.Gold}]Cultivate {release.Version} is available[/] (you have {Markup.Escape(Files.Version)}). Run [bold]cultivate update[/]."
                : $"[{TerminalStyle.Accent}]Cultivate {Markup.Escape(Files.Version)} is up to date[/] (latest release: {release.Version}).");
            return 0;
        }
        if (!newer && requested is null)
        {
            AnsiConsole.MarkupLine($"[{TerminalStyle.Accent}]Cultivate {Markup.Escape(Files.Version)} is up to date.[/]");
            return 0;
        }
        RequireInstalled();
        AnsiConsole.MarkupLine($"[bold]Cultivate {Markup.Escape(Files.Version)} -> {release.Version}[/]  [{TerminalStyle.Muted}]{Markup.Escape(release.Page)}[/]");
        foreach (var line in release.Notes.Replace("\r", "").Split('\n').Where(line => line.Trim().Length != 0).Take(15))
            AnsiConsole.MarkupLine($"[{TerminalStyle.Muted}]  {Markup.Escape(line)}[/]");
        if (!args.Contains("--yes"))
        {
            if (Console.IsInputRedirected) throw new InvalidOperationException("Updating requires --yes when not run interactively.");
            if (!AnsiConsole.Confirm($"Install Cultivate {release.Version}?", true)) return 0;
        }
        await AnsiConsole.Status().StartAsync("Updating...", context => Install(release, message => context.Status(Markup.Escape(message))));
        AnsiConsole.MarkupLine($"[{TerminalStyle.Accent}]Updated to Cultivate {release.Version}.[/] [{TerminalStyle.Muted}]A running saved-file worker switches to it automatically.[/]");
        return 0;
    }

    /// <summary>Latest release (or a specific version), with its package and checksum assets.</summary>
    public static async Task<Release> Fetch(string? version = null, CancellationToken cancel = default)
    {
        var repository = Repository;
        if (!repository.Contains('/')) throw new InvalidOperationException("This build of Cultivate has no update repository configured.");
        var token = await Token();
        using var client = Client(token);
        var path = version is null ? "releases/latest" : $"releases/tags/v{Parse(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/{path}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await client.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException((version is null ? $"No published release was found in {repository}." : $"Release v{Parse(version)} was not found in {repository}.") +
                    (token is null ? " If the repository is private, sign in with `gh auth login` (or Git Credential Manager), or set GH_TOKEN." : ""));
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"GitHub refused the release lookup ({(int)response.StatusCode}). Check `gh auth status` or GH_TOKEN.");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("The release has no tag.");
            var assets = root.GetProperty("assets").EnumerateArray().Select(asset => new ReleaseAsset(
                asset.GetProperty("name").GetString() ?? "", asset.GetProperty("url").GetString() ?? "", asset.GetProperty("size").GetInt64())).ToList();
            var package = assets.FirstOrDefault(asset => asset.Name.StartsWith("Cultivate-", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Release {tag} has no Cultivate-<version>-win-x64.zip package.");
            var checksum = assets.FirstOrDefault(asset => asset.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            return new(Parse(tag)?.ToString() ?? tag, tag, root.GetProperty("html_url").GetString() ?? "",
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", package, checksum);
        }
        catch (HttpRequestException exception) { throw new InvalidOperationException("Couldn't reach GitHub: " + exception.Message, exception); }
    }

    /// <summary>A newer release than this copy, using a cached answer for a few hours. Never throws.</summary>
    public static async Task<UpdateInfo?> CheckQuietly()
    {
        var cache = Path.Combine(Files.Home, "update-check.json");
        try
        {
            UpdateInfo? latest = null;
            if (File.Exists(cache))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(cache));
                var root = saved.RootElement;
                if (root.GetProperty("Repository").GetString() == Repository &&
                    DateTime.UtcNow - root.GetProperty("CheckedAt").GetDateTime().ToUniversalTime() < CheckInterval)
                    latest = new(root.GetProperty("Version").GetString() ?? "", root.GetProperty("Tag").GetString() ?? "", root.GetProperty("Page").GetString() ?? "");
            }
            if (latest is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var release = await Fetch(cancel: timeout.Token);
                latest = new(release.Version, release.Tag, release.Page);
                Files.WriteJson(cache, new { CheckedAt = DateTime.UtcNow, Repository, latest.Version, latest.Tag, latest.Page });
            }
            return Parse(latest.Version) > Current ? latest : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await File.AppendAllTextAsync(Path.Combine(Files.Home, "Logs", "update.log"), $"{DateTime.Now:O} update check failed: {exception.Message}\n"); }
            catch (IOException) { }
            return null;
        }
    }

    /// <summary>Downloads, verifies, and installs a release over this installation.</summary>
    public static async Task Install(Release release, Action<string> progress)
    {
        RequireInstalled();
        var updates = Path.Combine(Files.Home, "Updates");
        Directory.CreateDirectory(updates);
        FileStream guard;
        try { guard = new FileStream(Path.Combine(updates, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another Cultivate update is already running."); }
        using var _ = guard;
        var work = Path.Combine(updates, release.Version);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            using var client = Client(await Token());
            var zip = Path.Combine(work, release.Package.Name);
            progress($"Downloading {release.Package.Name} ({release.Package.Size / 1048576.0:0.0} MB)");
            await Download(client, release.Package, zip);
            if (release.Checksum is null) throw new InvalidOperationException($"Release {release.Tag} has no checksum file, so it was not installed.");
            await Download(client, release.Checksum, zip + ".sha256");
            progress("Verifying the download");
            var expected = File.ReadAllText(zip + ".sha256").Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!Files.Comparer.Equals(expected, Files.Hash(zip)))
                throw new InvalidOperationException("The download does not match its published SHA-256 checksum. Nothing was changed.");
            var files = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, files);
            foreach (var name in RequiredFiles)
                if (!File.Exists(Path.Combine(files, name))) throw new InvalidOperationException($"The release package is missing {name}. Nothing was changed.");
            var packaged = FileVersionInfo.GetVersionInfo(Path.Combine(files, "Cultivate.exe")).ProductVersion;
            if (Parse(packaged)?.ToString() != release.Version)
                throw new InvalidOperationException($"The package contains Cultivate {packaged}, not {release.Version}. Nothing was changed.");
            progress("Replacing program files");
            Apply(files, InstallDir);
            RefreshHooks();
            var manifest = Path.Combine(InstallDir, "installation.json");
            var installation = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ?? [];
            installation["Version"] = packaged;
            installation["UpdatedAt"] = DateTime.Now.ToString("o");
            Files.WriteJson(manifest, installation);
            File.Delete(Path.Combine(Files.Home, "update-check.json"));
        }
        catch (HttpRequestException exception) { throw new InvalidOperationException("The download failed: " + exception.Message, exception); }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Replaces the files in <paramref name="install"/> with those in <paramref name="source"/>, rolling back on failure.
    /// Every file is swapped by renames, so the installed name always refers to a complete file; a running executable can
    /// be renamed but not overwritten. Cultivate.exe goes last: a running worker restarts when it changes, by which time
    /// every file it loads is already new.
    /// </summary>
    internal static void Apply(string source, string install)
    {
        var names = Directory.GetFiles(source).Select(Path.GetFileName).OfType<string>()
            .Where(name => !name.Equals("install.ps1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name.Equals("Cultivate.exe", StringComparison.OrdinalIgnoreCase)).ThenBy(name => name, Files.Comparer).ToList();
        var stamp = "." + Guid.NewGuid().ToString("N")[..8];
        var swapped = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var name in names)
            {
                var target = Path.Combine(install, name);
                File.Copy(Path.Combine(source, name), target + stamp + ".new");
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + stamp + ".old";
                    File.Move(target, backup);
                }
                swapped.Add((target, backup));
                File.Move(target + stamp + ".new", target);
                // Extracted files keep the build's timestamp; mark the swap so running copies can notice it.
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            }
        }
        catch
        {
            foreach (var (target, backup) in Enumerable.Reverse(swapped))
            {
                try
                {
                    if (backup is not null) File.Move(backup, target, overwrite: true);
                    else File.Delete(target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            CleanupStaleFiles(install, incoming: true);
            throw;
        }
        CleanupStaleFiles(install, incoming: true);
    }

    /// <summary>
    /// Deletes replaced files from earlier updates (a replaced executable that is still running stays until next time).
    /// Incoming (.new) files are only removed by the update itself, which holds the update lock.
    /// </summary>
    public static void CleanupStaleFiles(string? install = null, bool incoming = false)
    {
        install ??= InstallDir;
        if (!Directory.Exists(install)) return;
        foreach (var file in Directory.EnumerateFiles(install).Where(path => path.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || incoming &&
                     path.EndsWith(".new", StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Starts the updated Cultivate in this console and exits with its exit code.</summary>
    public static void Restart()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "Cultivate.exe")) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Cannot restart Cultivate.");
        process.WaitForExit();
        Environment.Exit(process.ExitCode);
    }

    private static void RequireInstalled()
    {
        if (!IsInstalled)
            throw new InvalidOperationException($"This copy of Cultivate ({InstallDir}) was not installed with install.ps1, so it can't update itself. Install it first.");
    }

    /// <summary>Keeps this installation's MSBuild hook current; hooks owned by another installation are left alone.</summary>
    private static void RefreshHooks()
    {
        var targets = Path.Combine(InstallDir, Hooks.TargetsFileName);
        var content = "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            $"  <Import Project=\"{System.Security.SecurityElement.Escape(targets)}\" />\r\n</Project>\r\n";
        foreach (var hook in Hooks.Paths(Hooks.FileName))
        {
            var imported = Hooks.ImportedTargets(hook);
            if (imported is not null && !Files.Comparer.Equals(imported, targets)) continue;
            if (File.Exists(hook) && File.ReadAllText(hook) == content) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook, content);
        }
    }

    private static async Task Download(HttpClient client, ReleaseAsset asset, string destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        // GitHub redirects to signed storage; HttpClient drops the Authorization header when following it.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(destination);
        await response.Content.CopyToAsync(file);
    }

    private static HttpClient Client(string? token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Cultivate", Current.ToString()));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A GitHub token for private repositories: environment, then the gh CLI, then Git Credential Manager.</summary>
    private static async Task<string?> Token()
    {
        foreach (var name in new[] { "CULTIVATE_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;
        try
        {
            var token = (await Commands.Run("gh.exe", ["auth", "token", "--hostname", "github.com"])).Trim();
            if (token.Length != 0) return token;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return await GitCredential();
    }

    private static async Task<string?> GitCredential()
    {
        try
        {
            var start = new ProcessStartInfo("git.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("credential");
            start.ArgumentList.Add("fill");
            // Never prompt: a background check must not open a sign-in window.
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "never";
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("protocol=https\nhost=github.com\n\n");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); return null; }
            if (process.ExitCode != 0) return null;
            return (await output).Split('\n').Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..];
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) { return null; }
    }
}
