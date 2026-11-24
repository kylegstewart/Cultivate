using System.Text.Json;

namespace Cultivate;

/// <summary>An editor that opens solutions: one installed Visual Studio's devenv.exe, or VS Code's code.cmd.</summary>
internal sealed record Editor(string Name, string Path, bool VSCode = false);

/// <summary>The installed editors: every Visual Studio, the automatic choice first, and VS Code when it is found.</summary>
internal sealed record InstalledEditors(IReadOnlyList<Editor> VisualStudios, Editor? VSCode)
{
    /// <summary>The Visual Studio that opens unless another is chosen: the newest release, or a preview when no release is installed.</summary>
    public Editor? Automatic => VisualStudios.FirstOrDefault();
    public List<Editor> All => VisualStudios.Append(VSCode).OfType<Editor>().ToList();
}

/// <summary>
/// Finding the editors and reading the Open solutions with setting (<see cref="Settings.OpenWith"/>): blank for the automatic Visual Studio,
/// a devenv.exe path for one in particular, <see cref="VSCodeSetting"/>, or <see cref="AskSetting"/>.
/// </summary>
internal static class Editors
{
    internal const string VSCodeSetting = "VSCode", AskSetting = "Ask";
    internal const string AutomaticLabel = "Visual Studio (newest installed)";

    // Found once per run, since vswhere takes a moment and the Settings screen asks often; the self-test sets its own.
    private static Lazy<InstalledEditors> installed = new(Find);
    internal static InstalledEditors Installed => installed.Value;
    internal static void Use(InstalledEditors editors) => installed = new(editors);

    internal static bool Is(string openWith, string keyword) => string.Equals(openWith.Trim(), keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the setting names one Visual Studio, installed or not.</summary>
    internal static bool IsPath(string openWith) =>
        Path.IsPathFullyQualified(openWith) && Files.Comparer.Equals(Path.GetFileName(openWith), "devenv.exe");

    /// <summary>Looks for the installed editors; nothing found, or vswhere failing, gives none rather than an error.</summary>
    internal static InstalledEditors Find()
    {
        List<Editor> studios = [];
        if (File.Exists(Settings.VsWhere))
        {
            // Default products only, so a newer SSMS or Build Tools installation cannot hide Visual Studio.
            try
            {
                var json = Commands.Run(Settings.VsWhere, ["-prerelease", "-utf8", "-format", "json"], Path.GetDirectoryName(Settings.VsWhere)).GetAwaiter().GetResult();
                studios = VisualStudios(json, File.Exists);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { }
        }
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var code = FindVSCode(path, [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin")]);
        return new(studios, code is null ? null : new Editor("VS Code", code, VSCode: true));
    }

    /// <summary>
    /// The Visual Studios in vswhere's JSON whose devenv.exe <paramref name="exists"/>: releases before previews, then newest first. A preview
    /// is named so, and installs that would share a name are told apart by their version.
    /// </summary>
    internal static List<Editor> VisualStudios(string json, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        static string Text(JsonElement install, string name) =>
            install.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var installs = document.RootElement.EnumerateArray().Select(install =>
            {
                var version = Text(install, "installationVersion");
                var preview = install.TryGetProperty("isPrerelease", out var flag) && flag.ValueKind == JsonValueKind.True;
                var name = Text(install, "displayName") is { Length: > 0 } display ? display : "Visual Studio";
                if (preview && !name.Contains("Preview", StringComparison.OrdinalIgnoreCase) && !name.Contains("Insiders", StringComparison.OrdinalIgnoreCase))
                    name += " Preview";
                return (Name: name, Version: version, Preview: preview, Path: Text(install, "productPath"));
            })
            .Where(install => Files.Comparer.Equals(Path.GetFileName(install.Path), "devenv.exe") && exists(install.Path))
            .OrderBy(install => install.Preview)
            .ThenByDescending(install => System.Version.TryParse(install.Version, out var parsed) ? parsed : new System.Version())
            .ToList();
        return installs.Select(install => new Editor(installs.Count(other => other.Name == install.Name) > 1
            ? $"{install.Name} ({install.Version})" : install.Name, install.Path)).ToList();
    }

    /// <summary>VS Code's code.cmd in the first of the <paramref name="path"/> folders, then the <paramref name="defaults"/>, that has it.</summary>
    internal static string? FindVSCode(IEnumerable<string> path, IEnumerable<string> defaults) => path.Concat(defaults)
        .Select(folder => folder.Trim('"'))
        .Where(Path.IsPathFullyQualified)
        .Select(folder =>
        {
            try { return Path.Combine(folder, "code.cmd"); }
            catch (ArgumentException) { return null; }
        }).FirstOrDefault(File.Exists);

    /// <summary>The Settings screen's choices, in the order Enter steps through them; a particular Visual Studio only when there is more than one.</summary>
    internal static List<(string Value, string Label)> Choices(InstalledEditors installed)
    {
        var choices = new List<(string Value, string Label)> { ("", AutomaticLabel) };
        if (installed.VisualStudios.Count > 1) choices.AddRange(installed.VisualStudios.Select(studio => (studio.Path, studio.Name)));
        choices.Add((VSCodeSetting, "VS Code"));
        choices.Add((AskSetting, "Ask each time"));
        return choices;
    }

    /// <summary>Why the setting cannot open what it says, or null; it never stops the setting being saved.</summary>
    internal static string? Note(string openWith, InstalledEditors installed) =>
        Is(openWith, VSCodeSetting) ? installed.VSCode is null ? "VS Code was not found. Install it before opening a solution." : null :
        Is(openWith, AskSetting) ? null :
        IsPath(openWith) && !File.Exists(openWith) ? "That Visual Studio is no longer installed, so the newest installed one opens instead." :
        IsPath(openWith) || installed.Automatic is not null ? null : "Visual Studio was not found.";

    /// <summary>
    /// The editor the setting opens with, without asking: null for Ask. Throws when the editor it names is not installed; a particular
    /// Visual Studio that is gone, or text Cultivate does not know, opens the automatic one.
    /// </summary>
    internal static Editor? Configured(string openWith, InstalledEditors installed)
    {
        if (Is(openWith, AskSetting)) return null;
        if (Is(openWith, VSCodeSetting))
            return installed.VSCode ?? throw new InvalidOperationException(
                "VS Code was not found. Install VS Code, or choose another editor under Settings → Open solutions with.");
        if (IsPath(openWith) && File.Exists(openWith))
            return installed.VisualStudios.FirstOrDefault(studio => Files.Comparer.Equals(studio.Path, openWith)) ?? new("Visual Studio", openWith);
        return installed.Automatic ?? throw new InvalidOperationException(
            "Visual Studio was not found. Install Visual Studio, or choose VS Code under Settings → Open solutions with.");
    }

    /// <summary>How Ticket workspaces says where its solutions open.</summary>
    internal static string Where(string openWith) =>
        Is(openWith, VSCodeSetting) ? "in VS Code" : Is(openWith, AskSetting) ? "in the editor you choose" : "in Visual Studio";

    /// <summary><c>cultivate doctor</c>'s line for the setting: whether it can open solutions, and with what.</summary>
    internal static (bool Ok, string Message) Doctor(string openWith, InstalledEditors installed)
    {
        if (Is(openWith, AskSetting))
            return installed.All.Count == 0 ? (false, "Opens solutions with the editor chosen each time, but neither Visual Studio nor VS Code was found.")
                : (true, "Opens solutions with the editor chosen each time: " + string.Join(", ", installed.All.Select(editor => editor.Name)));
        try
        {
            var editor = Configured(openWith, installed)!;
            var stale = IsPath(openWith) && !Files.Comparer.Equals(editor.Path, openWith);
            return (!stale, stale ? $"Opens solutions with {openWith}, which is no longer installed, so {editor.Name} opens instead."
                : "Opens solutions with: " + (editor.VSCode || IsPath(openWith) ? editor.Name : $"{AutomaticLabel}, {editor.Name}"));
        }
        catch (InvalidOperationException exception) { return (false, exception.Message); }
    }

    /// <summary>The setting for <c>cultivate setup --open-with</c>'s <paramref name="value"/>.</summary>
    internal static string FromOption(string value) => value.ToLowerInvariant() switch
    {
        "visual-studio" => "",
        "vscode" => VSCodeSetting,
        "ask" => AskSetting,
        _ when IsPath(value) && File.Exists(value) => Files.Full(value),
        _ => throw new InvalidOperationException("--open-with expects visual-studio, vscode, ask, or the full path of an installed devenv.exe.")
    };
}
