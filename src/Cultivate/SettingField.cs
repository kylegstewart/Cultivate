using System.Reflection;
using System.Text.Json;
using Spectre.Console;

namespace Cultivate;

/// <summary>
/// One setting as the Settings screen lists it and the first run asks for it. A text setting is read and written as text and checked by
/// <see cref="Problem"/> once trimmed and unquoted; a yes/no setting has <see cref="Get"/> and <see cref="Set"/> instead.
/// </summary>
internal sealed class SettingField
{
    internal required string Name { get; init; }
    internal required string Description { get; init; }
    /// <summary>Whether the setting may be blank; a blank optional setting shows as none.</summary>
    internal bool Optional { get; init; }
    internal Func<Settings, string> Read { get; init; } = _ => "";
    /// <summary>Writes a checked value, and any setting that follows from it.</summary>
    internal Action<Settings, string> Write { get; init; } = (_, _) => { };
    internal Func<string, string?> Problem { get; init; } = _ => null;
    internal Func<Settings, bool>? Get { get; init; }
    internal Action<Settings, bool>? Set { get; init; }
    /// <summary>Whether the setting does anything with the others as they are; one that does not is skipped and shows <see cref="Unused"/>.</summary>
    internal Func<Settings, bool> Applies { get; init; } = _ => true;
    internal string Unused { get; init; } = "";
    /// <summary>A warning about the saved value, such as a folder that cannot be reached; it never stops the value being saved.</summary>
    internal Func<Settings, string?>? Note { get; init; }
    internal bool YesNo => Get is not null;

    internal const string NotReachable = "Not reachable right now. Check your VPN/share access before deploying.";
    internal const string NotBuilt = "Not built yet. Frontend deployments need it; build MeyerObjects in your normal checkout.";

    /// <summary>Every setting, in the order the first run asks for them.</summary>
    internal static IReadOnlyList<SettingField> All { get; } =
    [
        new()
        {
            Name = "MyMeyer checkout", Description = "Your normal MyMeyer checkout: the folder containing MyMeyer.sln.",
            Read = settings => settings.RepositoryRoot,
            Problem = value => !Path.IsPathFullyQualified(value) ? "Enter an absolute path." :
                File.Exists(Path.Combine(value, "MyMeyer.sln")) ? null : "MyMeyer.sln was not found in that folder.",
            Write = (settings, value) =>
            {
                var repository = Files.Full(value);
                if (!Files.Comparer.Equals(settings.RepositoryRoot, repository) || settings.MappingAssembly.Length == 0)
                    settings.MappingAssembly = Setup.DefaultMapping(repository);
                settings.RepositoryRoot = repository;
            }
        },
        new()
        {
            Name = "Base branch", Description = "The baseline branch that changes are compared against.",
            Read = settings => settings.BaseBranch, Write = (settings, value) => settings.BaseBranch = value
        },
        new()
        {
            Name = "Dev webfarm root",
            Description = @"The dev webfarm folder deployments write to, for example \\webfarm-dev02\C$\inetpub\MyMeyer. " +
                "Changing it suggests the site address again.",
            Read = settings => settings.WebfarmRoot,
            Problem = value => Path.IsPathFullyQualified(value) ? null : "Enter an absolute path or UNC share.",
            Write = (settings, value) =>
            {
                var farm = Files.Full(value);
                if (!Files.Comparer.Equals(settings.WebfarmRoot, farm)) settings.SiteUrl = Warmup.SuggestSiteUrl(farm);
                settings.WebfarmRoot = farm;
            },
            Note = settings => settings.WebfarmRoot.Length == 0 || Directory.Exists(settings.WebfarmRoot) ? null : NotReachable
        },
        new()
        {
            Name = "Mapping assembly",
            Description = "The Code Release Manager mapping assembly (MeyerObjects.dll). Changing the MyMeyer checkout sets it to that checkout's.",
            Read = settings => settings.MappingAssembly, Problem = Absolute, Write = (settings, value) => settings.MappingAssembly = Files.Full(value),
            Note = settings => settings.MappingAssembly.Length == 0 || File.Exists(settings.MappingAssembly) ? null : NotBuilt
        },
        new()
        {
            Name = "MSBuild", Description = "Visual Studio's MSBuild.exe, which builds the changed projects.",
            Read = settings => settings.MSBuild,
            Problem = value => Path.IsPathFullyQualified(value) && File.Exists(value) ? null : "MSBuild.exe was not found there.",
            Write = (settings, value) => settings.MSBuild = Files.Full(value)
        },
        new()
        {
            Name = "Automatic updates", Description = "Whether deploying a version also deploys your later saved changes automatically, by default.",
            Get = settings => settings.WatchSavedFiles, Set = (settings, value) => settings.WatchSavedFiles = value
        },
        new()
        {
            Name = "Worktrees folder", Optional = true,
            Description = "The default worktrees folder. Open solution lists its worktrees from any repository.",
            Read = settings => settings.WorktreesRoot, Problem = Absolute, Write = (settings, value) => settings.WorktreesRoot = OptionalPath(value)
        },
        new()
        {
            Name = "Dev site address", Optional = true,
            Description = "The dev site's address, requested after a deployment so the first page load is not the one that waits. Blank skips the warm-up.",
            Read = settings => settings.SiteUrl,
            Problem = value => Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Scheme is "http" or "https"
                ? null : "Enter an http or https address.",
            Write = (settings, value) => settings.SiteUrl = value
        },
        new()
        {
            Name = "Warm up after deploying", Description = "Whether the dev site is warmed up after each deployment.",
            Get = settings => settings.WarmUpAfterDeploy, Set = (settings, value) => settings.WarmUpAfterDeploy = value,
            Applies = settings => settings.SiteUrl.Length != 0, Unused = "Not used without a dev site address."
        },
        new()
        {
            Name = "SQL files folder", Optional = true,
            Description = "The folder with a subfolder of SQL files per ticket, which Ticket workspaces compares.",
            Read = settings => settings.SqlMirrorRoot, Problem = Absolute, Write = (settings, value) => settings.SqlMirrorRoot = OptionalPath(value)
        },
        new()
        {
            Name = "MeyerOnline checkout", Optional = true,
            Description = "The MeyerOnline checkout that Build MeyerOnline release switches to master and builds in.",
            Read = settings => settings.MeyerOnlineRoot,
            Problem = value => !Path.IsPathFullyQualified(value) ? "Enter an absolute path." :
                Path.Exists(Path.Combine(value, ".git")) ? null : "That folder is not a Git checkout.",
            Write = (settings, value) => settings.MeyerOnlineRoot = OptionalPath(value)
        },
        new()
        {
            Name = "Stop old watcher",
            Description = "Whether the old WebfarmFileSync watcher is stopped without asking when it could overwrite a deployment.",
            Get = settings => settings.StopLegacyWatcher, Set = (settings, value) => settings.StopLegacyWatcher = value
        },
        new()
        {
            Name = "Library repositories", Optional = true,
            Description = "Library repositories whose matching worktrees can deploy with MyMeyer. Separate them with ;.",
            Read = settings => string.Join(";", settings.LibraryRepositories ?? []), Problem = LibraryLoop.RepositoriesProblem,
            Write = (settings, value) => settings.LibraryRepositories = LibraryLoop.ParseRepositories(value)
        },
        new()
        {
            Name = "Unused worktree days", Description = "Days a worktree must go unused before Clean up offers to delete it.",
            Read = settings => settings.CleanupDays.ToString(),
            Problem = value => int.TryParse(value, out var days) && days >= 1 ? null : "Enter a whole number of days, 1 or more.",
            Write = (settings, value) => settings.CleanupDays = int.Parse(value)
        },
        new()
        {
            Name = "Release files folder", Description = "The folder Build release files makes a folder per ticket in.",
            Read = settings => settings.ReleaseRoot, Problem = Absolute, Write = (settings, value) => settings.ReleaseRoot = Files.Full(value)
        },
    ];

    private static string? Absolute(string value) => Path.IsPathFullyQualified(value) ? null : "Enter an absolute path.";
    private static string OptionalPath(string value) => value.Length == 0 ? "" : Files.Full(value);

    /// <summary>The value as the list shows it; blank for a text setting with none.</summary>
    internal string Shown(Settings settings) => YesNo ? Get!(settings) ? "Yes" : "No" : Read(settings);

    /// <summary>
    /// A copy of <paramref name="settings"/> with <paramref name="input"/> written, or why it is refused: the value's problem, or a new
    /// destination for the active deployment.
    /// </summary>
    internal (Settings? Changed, string? Error) Change(Settings settings, string input, DeploymentState? active)
    {
        var value = input.Trim().Trim('"');
        var error = value.Length == 0 ? Optional ? null : "A value is required." : Problem(value);
        if (error is not null) return (null, error);
        var changed = Copy(settings);
        Write(changed, value);
        return Setup.MovesActive(active, settings.WebfarmRoot, changed.WebfarmRoot) ? (null, Setup.ActiveMoved) : (changed, null);
    }

    /// <summary>A copy of <paramref name="settings"/> with this yes/no setting turned the other way.</summary>
    internal Settings Toggled(Settings settings)
    {
        var changed = Copy(settings);
        Set!(changed, !Get!(settings));
        return changed;
    }

    internal static Settings Copy(Settings settings) =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Files.Json), Files.Json)!;

    /// <summary>Gives <paramref name="to"/> every value of <paramref name="from"/>, so whoever holds <paramref name="to"/> sees them.</summary>
    internal static void Apply(Settings from, Settings to)
    {
        foreach (var property in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite))
            property.SetValue(to, property.GetValue(from));
    }

    /// <summary><paramref name="text"/> in lines of at most <paramref name="width"/> characters, broken between words, or inside a word too long for one.</summary>
    internal static List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            if (line.Length != 0 && line.Length + 1 + rest.Length <= width)
            {
                line += " " + rest;
                continue;
            }
            if (line.Length != 0) lines.Add(line);
            for (; rest.Length > width; rest = rest[width..]) lines.Add(rest[..width]);
            line = rest;
        }
        if (line.Length != 0 || lines.Count == 0) lines.Add(line);
        return lines;
    }

    /// <summary><paramref name="value"/> in at most <paramref name="width"/> characters, keeping its start and end, as a path is best read.</summary>
    internal static string Fit(string value, int width)
    {
        if (value.Length <= width) return value;
        if (width < 2) return value[..Math.Max(0, width)];
        var left = (width - 1) / 2;
        return value[..left] + "…" + value[^(width - 1 - left)..];
    }
}

/// <summary>
/// A one-line text field: typing (or pasting, which arrives as keys) inserts at the caret, Backspace and Delete remove, the arrows, Home and End
/// move the caret, and Ctrl+U clears. Text longer than the field scrolls sideways to keep the caret in view.
/// </summary>
internal sealed class TextField(string text)
{
    internal string Text { get; private set; } = text;
    internal int Caret { get; private set; } = text.Length;
    private int scroll;

    /// <summary>Handles an editing key; true when the text changed.</summary>
    internal bool Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                Caret = Math.Max(0, Caret - 1);
                return false;
            case ConsoleKey.RightArrow:
                Caret = Math.Min(Text.Length, Caret + 1);
                return false;
            case ConsoleKey.Home:
                Caret = 0;
                return false;
            case ConsoleKey.End:
                Caret = Text.Length;
                return false;
            case ConsoleKey.Backspace:
                if (Caret == 0) return false;
                Text = Text.Remove(--Caret, 1);
                return true;
            case ConsoleKey.Delete:
                if (Caret == Text.Length) return false;
                Text = Text.Remove(Caret, 1);
                return true;
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                (Text, Caret) = ("", 0);
                return true;
        }
        if (char.IsControl(key.KeyChar) || key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return false;
        Text = Text.Insert(Caret++, key.KeyChar.ToString());
        return true;
    }

    /// <summary>The field's markup in <paramref name="width"/> cells, the caret as a highlighted cell and … where the text runs past either edge.</summary>
    internal string Render(int width)
    {
        width = Math.Max(4, width);
        if (Text.Length < width) scroll = 0;
        else
        {
            // The caret stays off the edge cells while text runs past them, since those show the … instead.
            scroll = Math.Min(scroll, Text.Length + 2 - width);
            if (Caret - scroll < (scroll > 0 ? 1 : 0)) scroll = Math.Max(0, Caret - 1);
            if (Caret - scroll > width - 2) scroll = Caret - width + 2;
        }
        var cells = (Text + new string(' ', width)).Substring(scroll, width).ToCharArray();
        if (scroll > 0) cells[0] = '…';
        if (Text.Length > scroll + width) cells[^1] = '…';
        var at = Caret - scroll;
        string Part(int from, int to, string style) => from == to ? "" : $"[{style}]{Markup.Escape(new string(cells[from..to]))}[/]";
        return Part(0, at, TerminalStyle.Accent) + Part(at, at + 1, $"{TerminalStyle.Surface} on {TerminalStyle.Accent}") +
            Part(at + 1, width, TerminalStyle.Accent);
    }
}
