using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// A screen of items to check or uncheck under a header. Enter applies the checked items; Esc goes back with nothing changed. Space toggles
/// the highlighted item, and Ctrl+A and Ctrl+D check and clear every item, the keys Tidy worktrees uses.
/// </summary>
internal sealed class Checklist<T>(IRenderable header, string title, IReadOnlyList<T> items, Func<T, string> label, IEnumerable<T> checkedItems)
{
    private readonly HashSet<int> marked = Enumerable.Range(0, items.Count).Where(index => checkedItems.Contains(items[index])).ToHashSet();
    internal int Index { get; private set; }
    internal IReadOnlyList<T> Checked => items.Where((_, index) => marked.Contains(index)).ToList();

    // The header, a blank line, the title, the position line, the keys line and the cursor's.
    private int PageSize(int height) => Math.Max(3, height - TerminalStyle.Lines(header) - 5);

    /// <summary>Handles one key; returns true for Enter, false for Esc, and null while the list stays open.</summary>
    internal bool? HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        var last = Math.Max(0, items.Count - 1);
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.Enter: return true;
            case ConsoleKey.Escape: return false;
            case ConsoleKey.UpArrow: Index = Index == 0 ? last : Index - 1; break;
            case ConsoleKey.DownArrow: Index = Index == last ? 0 : Index + 1; break;
            case ConsoleKey.PageUp: Index = Math.Max(0, Index - pageSize); break;
            case ConsoleKey.PageDown: Index = Math.Min(last, Index + pageSize); break;
            case ConsoleKey.Home: Index = 0; break;
            case ConsoleKey.End: Index = last; break;
            case ConsoleKey.Spacebar:
                if (items.Count != 0 && !marked.Remove(Index)) marked.Add(Index);
                break;
            case ConsoleKey.A when control:
                marked.UnionWith(Enumerable.Range(0, items.Count));
                break;
            case ConsoleKey.D when control:
                marked.Clear();
                break;
        }
        return null;
    }

    internal IRenderable Render(int width, int height)
    {
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, items.Count);
        var rows = new List<IRenderable> { header, new Text(""), new Markup($"[{TerminalStyle.Accent}]{Terminal.Escape(title)}[/]") };
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end)
            {
                rows.Add(new Text(""));
                continue;
            }
            var style = row == Index ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            rows.Add(TerminalStyle.Line(TicketWorkspaceView.Fit($"{(row == Index ? ">" : " ")} [{(marked.Contains(row) ? "x" : " ")}] {label(items[row])}", width - 1), style));
        }
        rows.Add(TerminalStyle.Line(items.Count == 0 ? "Nothing to choose." : $"{start + 1}-{end} of {items.Count}   |   {marked.Count} checked",
            new Style(Color.FromHex(TerminalStyle.Muted))));
        rows.Add(new Markup(Keys(width)));
        return new Rows(rows);
    }

    private static string Keys(int width)
    {
        static string Key(string key, string action) => $"[{TerminalStyle.Gold}]{key}[/] {action}";
        if (width >= 100)
            return string.Join("   ", Key("arrows", "move"), Key("Space", "check"), Key("Ctrl+A", "check all"), Key("Ctrl+D", "clear all"),
                Key("Enter", "apply"), Key("Esc", "back"));
        if (width >= 80)
            return string.Join("   ", Key("Space", "check"), Key("Ctrl+A", "all"), Key("Ctrl+D", "none"), Key("Enter", "apply"), Key("Esc", "back"));
        return string.Join("  ", Key("Space", "check"), Key("Ctrl+A", "all"), Key("Ctrl+D", "none"), Key("Enter", "apply"), Key("Esc", "back"));
    }

    internal IReadOnlyList<T>? Show() => Show(AnsiConsole.Console, KeyScreen.ReadKey);

    /// <summary>Returns the checked items when Enter applies them, or null when Esc goes back.</summary>
    internal IReadOnlyList<T>? Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey)
    {
        bool? applied = null;
        KeyScreen.Show(console, Render, (key, height) => (applied = HandleKey(key, PageSize(height))) is not null, readKey);
        return applied == true ? Checked : null;
    }
}
