using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>One menu choice. <paramref name="Back"/> marks the choice Esc gives: the menu's Back, Cancel, or No.</summary>
internal sealed record MenuAction(string Label, string Description, bool Back = false);

/// <summary>
/// A menu of actions. Esc leaves it at once with its back action, or with none when it has no back action; a <c>top</c> menu, home, has
/// nothing to go back to, so Esc only highlights its back action. Only a <c>search</c> menu, whose list is as long as the machine's
/// data, filters as you type.
/// </summary>
internal sealed class ActionPicker
{
    private readonly MenuAction[] actions;
    private readonly bool search;
    private readonly bool top;
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<MenuAction> Matches { get; private set; }
    internal int Index { get; private set; }
    internal MenuAction? Selected => Matches.Count == 0 ? null : Matches[Index];
    private MenuAction? BackAction => actions.FirstOrDefault(action => action.Back);

    internal ActionPicker(IEnumerable<MenuAction> actions, bool search = false, bool top = false, string? highlight = null)
    {
        this.actions = actions.ToArray();
        if (this.actions.Length == 0) throw new ArgumentException("An action menu needs at least one choice.", nameof(actions));
        this.search = search;
        this.top = top;
        Matches = this.actions;
        Index = Math.Max(0, Array.FindIndex(this.actions, action => action.Label == highlight));
    }

    private static int PageSize(int height) => Math.Clamp(height - 13, 3, 14);

    internal void HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        var last = Math.Max(0, Matches.Count - 1);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: Index = Index == 0 ? last : Index - 1; break;
            case ConsoleKey.DownArrow: Index = Index == last ? 0 : Index + 1; break;
            case ConsoleKey.PageUp: Index = Math.Max(0, Index - pageSize); break;
            case ConsoleKey.PageDown: Index = Math.Min(last, Index + pageSize); break;
            case ConsoleKey.Home: Index = 0; break;
            case ConsoleKey.End: Index = last; break;
            case ConsoleKey.Escape:
                if (top && BackAction is { } back)
                {
                    if (!Matches.Contains(back)) (Query, Matches) = ("", actions);
                    Index = Matches.ToList().IndexOf(back);
                }
                break;
            case ConsoleKey.Backspace when search:
                if (Query.Length != 0) { Query = Query[..^1]; Filter(); }
                break;
            case ConsoleKey.U when search && key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (Query.Length != 0) { Query = ""; Filter(); }
                break;
            default:
                if (search && !char.IsControl(key.KeyChar) && !key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                    !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    Query += key.KeyChar;
                    Filter();
                }
                break;
        }
    }

    private void Filter()
    {
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Matches = actions.Where(action => terms.All(term =>
            action.Label.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        Index = 0;
    }

    internal IRenderable Render(string title, int height)
    {
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var table = new Table().Border(TableBorder.None).HideHeaders().Expand()
            .AddColumn(new TableColumn("") { Width = 2, NoWrap = true, Padding = new(0, 0, 1, 0) })
            .AddColumn(new TableColumn("") { NoWrap = true });
        for (var row = start; row < end; row++)
        {
            var style = row == Index ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            table.AddRow(TerminalStyle.Line(row == Index ? ">" : "", style),
                TerminalStyle.Line(Matches[row].Label, style));
        }
        var content = new List<IRenderable> { new Markup(title) };
        if (search)
            content.Add(TerminalStyle.Line(Query.Length == 0 ? "/  Type to search..." : "/  " + Query,
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))));
        if (Matches.Count != 0) content.Add(table);
        if (Matches.Count > pageSize)
            content.Add(new Text($"{start + 1}-{end} of {Matches.Count} actions", new Style(Color.FromHex(TerminalStyle.Muted))));
        content.Add(new Text(Selected?.Description ?? "No matching actions. Backspace or Ctrl+U clears the filter.",
            new Style(Color.FromHex(TerminalStyle.Hint))));
        content.Add(new Markup(Keys()));
        return new Rows(content);
    }

    private string Keys()
    {
        static string Key(string key, string action) => $"[{TerminalStyle.Gold}]{key}[/] {action}";
        var keys = new List<string> { Key("arrows", "move"), Key("Enter", "choose") };
        if (search) keys.Add(Key("Ctrl+U", "clear"));
        keys.Add(top && BackAction is { } back ? Key("Esc", "highlights " + Terminal.Escape(back.Label)) : Key("Esc", "back"));
        return string.Join("   ", keys);
    }

    internal MenuAction? Show(string title) => Show(title, AnsiConsole.Console, KeyScreen.ReadKey);

    /// <summary>Shows the menu below what the terminal already shows, and erases it once an action is chosen or Esc leaves it.</summary>
    internal MenuAction? Show(string title, IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey) => Show(title, console, readKey, null, null);

    /// <summary>
    /// Shows the menu under <paramref name="above"/> on a cleared terminal, painting both again when <paramref name="update"/> completes.
    /// Returns the chosen action, the back action when Esc leaves, or null for Esc in a menu without one, or when the update fails.
    /// </summary>
    internal MenuAction? Show(string title, IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, Func<int, int, IRenderable>? above, Task? update)
    {
        MenuAction? chosen = null;
        KeyScreen.Show(console, (_, height) => Render(title, height), (key, height) =>
        {
            if (key.Key == ConsoleKey.Escape && !top)
            {
                chosen = BackAction;
                return true;
            }
            if (key.Key == ConsoleKey.Enter && Selected is not null) chosen = Selected;
            else HandleKey(key, PageSize(height));
            return chosen is not null;
        }, readKey, inline: above is null, above, update);
        return chosen;
    }
}
