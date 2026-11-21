using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// The Settings screen: every setting with its value, the highlighted one explained in a card below. Enter edits a text setting in place
/// or turns a yes/no one the other way, and each accepted change is saved at once; Esc leaves.
/// </summary>
internal sealed class SettingsScreen
{
    // Lines around the list: the header, a blank line, the position line, the card's borders and lines, and the key line.
    private const int CardLines = 6;
    private const int Around = 4 + 1 + 1 + 2 + CardLines + 1;
    private readonly Settings settings;
    private readonly DeploymentState? active;
    private readonly string path;
    private readonly IReadOnlyList<SettingField> fields = SettingField.All;
    // Checking a share can take seconds, so the notes are first found in the background and filled in when they arrive.
    private readonly string?[] notes;
    private readonly bool[] rechecked;
    private bool merged;
    internal Task<string?[]> Checking { get; }
    internal int Index { get; private set; }
    internal SettingField Highlighted => fields[Index];
    /// <summary>The field the highlighted setting is being edited in, or null while moving through the list.</summary>
    internal TextField? Field { get; private set; }
    internal string? Error { get; private set; }
    internal string? Notice { get; private set; }

    /// <summary>Changes <paramref name="settings"/> in place and saves them to <paramref name="path"/>, normally the settings file.</summary>
    internal SettingsScreen(Settings settings, DeploymentState? active, string? path = null)
    {
        this.settings = settings;
        this.active = active;
        this.path = path ?? Settings.FilePath;
        notes = new string?[fields.Count];
        rechecked = new bool[fields.Count];
        Checking = Task.Run(() => fields.Select(field => field.Note?.Invoke(settings)).ToArray());
    }

    internal static int PageSize(int height) => Math.Clamp(height - Around, 3, SettingField.All.Count);

    /// <summary>Handles a key; true when Esc leaves the screen.</summary>
    internal bool HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        if (Field is not null)
        {
            Edit(key);
            return false;
        }
        var last = fields.Count - 1;
        var field = Highlighted;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return true;
            case ConsoleKey.UpArrow:
                Move(Index == 0 ? last : Index - 1);
                break;
            case ConsoleKey.DownArrow:
                Move(Index == last ? 0 : Index + 1);
                break;
            case ConsoleKey.PageUp:
                Move(Math.Max(0, Index - pageSize));
                break;
            case ConsoleKey.PageDown:
                Move(Math.Min(last, Index + pageSize));
                break;
            case ConsoleKey.Home:
                Move(0);
                break;
            case ConsoleKey.End:
                Move(last);
                break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar when field.YesNo && field.Applies(settings):
                Error = Save(field.Toggled(settings));
                break;
            case ConsoleKey.Enter when !field.YesNo:
                (Field, Error, Notice) = (new TextField(field.Read(settings)), null, null);
                break;
        }
        return false;
    }

    private void Move(int index) => (Index, Error, Notice) = (index, null, null);

    private void Edit(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                (Field, Error) = (null, null);
                break;
            case ConsoleKey.Enter:
                var (changed, error) = Highlighted.Change(settings, Field!.Text, active);
                Error = changed is null ? error : Save(changed);
                if (Error is null) Field = null;
                break;
            default:
                if (Field!.Handle(key)) Error = null;
                break;
        }
    }

    /// <summary>Saves <paramref name="changed"/> and gives its values to the settings, or says why not: a change may not add a problem.</summary>
    private string? Save(Settings changed)
    {
        var problems = changed.Problems().Except(settings.Problems()).ToList();
        if (problems.Count != 0) return "Not saved: " + string.Join(" ", problems);
        try { Files.WriteJson(path, changed); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Not saved: " + exception.Message;
        }
        var before = fields.Select(field => field.Note is null ? "" : field.Read(settings)).ToList();
        SettingField.Apply(changed, settings);
        for (var index = 0; index < fields.Count; index++)
            if (fields[index].Note is { } note && before[index] != fields[index].Read(settings))
                (notes[index], rechecked[index]) = (note(settings), true);
        Notice = $"Saved {Highlighted.Name}.";
        return null;
    }

    private void Merge()
    {
        if (merged || !Checking.IsCompletedSuccessfully) return;
        for (var index = 0; index < fields.Count; index++)
            if (!rechecked[index]) notes[index] = Checking.Result[index];
        merged = true;
    }

    internal IRenderable Render(int width, int height)
    {
        Merge();
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var nameWidth = fields.Max(field => field.Name.Length);
        // One column is left free so a full row never wraps on terminals that move to the next line after the last column.
        var valueWidth = Math.Max(4, width - nameWidth - 5);
        var rows = new List<IRenderable> { TerminalStyle.Header("SETTINGS", "Enter changes a setting. Each change is saved at once."), new Text("") };
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= fields.Count)
            {
                rows.Add(new Text(""));
                continue;
            }
            var field = fields[row];
            var (value, color) = Value(field, notes[row]);
            var line = (row == Index ? "> " : "  ") + field.Name.PadRight(nameWidth) + "  ";
            value = SettingField.Fit(value, valueWidth);
            rows.Add(row == Index
                ? new Markup($"[bold {TerminalStyle.Accent} on {TerminalStyle.Surface}]{Markup.Escape((line + value).PadRight(width - 1))}[/]")
                : new Markup($"[{TerminalStyle.Text}]{Markup.Escape(line)}[/][{color}]{Markup.Escape(value)}[/]"));
        }
        var position = $"{Index + 1} of {fields.Count}   |   ";
        rows.Add(Notice is null
            ? TerminalStyle.Line(position + SettingField.Fit(path, Math.Max(4, width - 1 - position.Length)), new Style(Color.FromHex(TerminalStyle.Muted)))
            : new Markup($"[{TerminalStyle.Muted}]{Markup.Escape(position)}[/][{TerminalStyle.Accent}]{Markup.Escape(Notice)}[/]"));
        rows.Add(TerminalStyle.Card(new Rows(Card(width - 4)), Highlighted.Name.ToUpperInvariant(), Field is null ? null : TerminalStyle.Accent));
        rows.Add(new Markup(Keys()));
        return new Rows(rows);
    }

    private (string Value, string Color) Value(SettingField field, string? note) =>
        !field.Applies(settings) ? ("not used", TerminalStyle.Muted) :
        field.Shown(settings) is { Length: 0 } ? (field.Optional ? "none" : "not set", field.Optional ? TerminalStyle.Muted : TerminalStyle.Gold) :
        (field.Shown(settings), note is null ? TerminalStyle.Text : TerminalStyle.Gold);

    /// <summary>The card's lines, always <see cref="CardLines"/> of them so the list keeps its place: the explanation, the value or the field, and any error or note.</summary>
    private List<IRenderable> Card(int width)
    {
        var field = Highlighted;
        IRenderable Line(string text, string color) => new Markup($"[{color}]{Markup.Escape(text)}[/]");
        var lines = SettingField.Wrap(field.Description, width).Take(3).Select(line => Line(line, TerminalStyle.Text)).ToList();
        if (Field is not null) lines.Add(new Markup(Field.Render(width)));
        else
        {
            var (value, color) = Value(field, null);
            lines.AddRange(SettingField.Wrap(field.Applies(settings) ? value : field.Unused, width).Take(2).Select(line => Line(line, color)));
        }
        var message = Error ?? notes[Index];
        if (message is not null)
            lines.AddRange(SettingField.Wrap(message, width).Select(line => Line(line, Error is null ? TerminalStyle.Gold : "red")));
        lines = lines.Take(CardLines).ToList();
        while (lines.Count < CardLines) lines.Add(new Text(""));
        return lines;
    }

    private string Keys()
    {
        string Key(string key, string action) => $"[{TerminalStyle.Gold}]{key}[/] {action}   ";
        if (Field is not null) return Key("Enter", "save") + Key("Ctrl+U", "clear") + Key("Esc", "cancel").TrimEnd();
        var field = Highlighted;
        var change = field.YesNo ? field.Applies(settings) ? Key("Enter/Space", "toggle") : "" : Key("Enter", "edit");
        return Key("arrows", "move") + Key("PgUp/PgDn", "page") + change + Key("Esc", "back").TrimEnd();
    }

    internal void Show() => Show(AnsiConsole.Console, KeyScreen.ReadKey);

    internal void Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey) =>
        KeyScreen.Show(console, Render, (key, height) => HandleKey(key, PageSize(height)), readKey, update: Checking);
}
