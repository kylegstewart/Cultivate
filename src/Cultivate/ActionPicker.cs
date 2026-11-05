using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal sealed record MenuAction(string Label, string Description);

internal sealed class ActionPicker
{
    private readonly MenuAction[] actions;
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<MenuAction> Matches { get; private set; }
    internal int Index { get; private set; }
    internal MenuAction? Selected => Matches.Count == 0 ? null : Matches[Index];

    internal ActionPicker(IEnumerable<MenuAction> actions)
    {
        this.actions = actions.ToArray();
        if (this.actions.Length == 0) throw new ArgumentException("An action menu needs at least one choice.", nameof(actions));
        Matches = this.actions;
    }

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
            case ConsoleKey.Backspace:
                if (Query.Length != 0) { Query = Query[..^1]; Filter(); }
                break;
            case ConsoleKey.Escape:
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (Query.Length != 0) { Query = ""; Filter(); }
                break;
            default:
                if (!char.IsControl(key.KeyChar) && !key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
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
        var pageSize = Math.Clamp(height - 13, 3, 14);
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
        if (Query.Length != 0) content.Add(new Text("/  " + Query, new Style(Color.FromHex(TerminalStyle.Muted))));
        if (Matches.Count != 0) content.Add(table);
        if (Matches.Count > pageSize)
            content.Add(new Text($"{start + 1}-{end} of {Matches.Count} actions", new Style(Color.FromHex(TerminalStyle.Muted))));
        content.Add(new Text(Selected?.Description ?? "No matching actions. Backspace or Ctrl+U clears the filter.",
            new Style(Color.FromHex(TerminalStyle.Hint))));
        return new Rows(content);
    }

    internal MenuAction Show(string title) => Show(title, AnsiConsole.Console, () => Console.ReadKey(true));

    internal MenuAction Show(string title, IAnsiConsole console, Func<ConsoleKeyInfo> readKey) =>
        console.Live(Render(title, console.Profile.Height))
            .AutoClear(true).Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                context.Refresh();
                while (true)
                {
                    var key = readKey();
                    if (key.Key == ConsoleKey.Enter && Selected is not null) return Selected;
                    HandleKey(key, Math.Clamp(console.Profile.Height - 13, 3, 14));
                    context.UpdateTarget(Render(title, console.Profile.Height));
                    context.Refresh();
                }
            });
}
