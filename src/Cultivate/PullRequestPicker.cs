using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>The searchable list of open pull requests. A typed number can be reviewed even when it is not listed.</summary>
internal sealed class PullRequestPicker
{
    private readonly PullRequest[] open;
    private readonly string? problem;
    private readonly DeploymentState? active;
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<PullRequest> Matches { get; private set; } = [];
    internal int Index { get; private set; }
    internal PullRequest? Selected => Matches.Count == 0 ? null : Matches[Index];

    internal PullRequestPicker(IEnumerable<PullRequest> open, string? problem = null, DeploymentState? active = null)
    {
        this.open = open.OrderByDescending(request => request.Number).ToArray();
        this.problem = problem;
        this.active = active;
        Filter();
        Index = Math.Max(0, Matches.ToList().FindIndex(IsActive));
    }

    internal static int PageSize(int height) => Math.Clamp(height - 17, 3, 18);

    private bool IsActive(PullRequest request) => active?.Active == true && active.SourceRef == PullRequests.HeadRef(request.Number);

    private bool Listed(PullRequest request) => open.Contains(request);

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
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Query = "";
                Filter();
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
        var matches = open.Where(request => terms.All(term => ("#" + request.Number).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            request.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || request.Author.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            request.HeadBranch.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        // A typed pull request number comes first. One that is not listed (closed, merged, or a ticket branch's number) comes last.
        if (int.TryParse(Query.Trim().TrimStart('#'), out var number) && number > 0)
        {
            if (matches.FirstOrDefault(request => request.Number == number) is { } exact)
            {
                matches.Remove(exact);
                matches.Insert(0, exact);
            }
            else matches.Add(new PullRequest(number));
        }
        Matches = matches;
        Index = 0;
    }

    internal IRenderable Render(int width, int height)
    {
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var available = Math.Max(24, width - 12);
        var numberWidth = 7;
        var authorWidth = Math.Clamp(available / 6, 6, 16);
        var branchWidth = Math.Clamp((available - numberWidth - authorWidth) / 3, 6, 30);
        var titleWidth = Math.Max(6, available - numberWidth - authorWidth - branchWidth);
        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("") { Width = 2, NoWrap = true, Padding = new(0, 0, 1, 0) })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]PR[/]") { Width = numberWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]TITLE[/]") { Width = titleWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]AUTHOR[/]") { Width = authorWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]BRANCH[/]") { Width = branchWidth, NoWrap = true });
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end)
            {
                table.AddRow("", "", "", "", "");
                continue;
            }
            var item = Matches[row];
            var style = row == Index ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            var title = (IsActive(item) ? "LIVE  " : "") + (item.IsDraft ? "DRAFT  " : "") + (Listed(item) ? item.Title : "Review by number");
            table.AddRow(TerminalStyle.Line(row == Index ? ">" : "", style), TerminalStyle.Line("#" + item.Number, style),
                TerminalStyle.Line(TicketWorkspaceView.Fit(title, titleWidth), style), TerminalStyle.Line(TicketWorkspaceView.Fit(item.Author, authorWidth), style),
                TerminalStyle.Line(TicketWorkspaceView.Fit(item.HeadBranch, branchWidth), style));
        }
        var description = problem is not null ? $"{problem}. Type a pull request number to review it anyway." :
            "Open pull requests on GitHub. Type a number for a closed or unlisted one.";
        IRenderable details = Selected is not { } selected
            ? new Markup($"[{TerminalStyle.Muted}]{(open.Length == 0 && Query.Length == 0 ? "No open pull requests are listed. Type a number to review one." :
                "No matches. Type a number, or press Ctrl+U to clear the search.")}[/]")
            : new Rows(
                TerminalStyle.Line(TicketWorkspaceView.Fit(selected.Label + (selected.Status.Length == 0 ? "" : "   " + selected.Status) +
                    (IsActive(selected) ? "   LIVE" : ""), width - 4), new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
                TerminalStyle.Line(TicketWorkspaceView.Fit(Listed(selected) ? selected.Source :
                    "Not in the open list. Enter fetches it from origin, even if it is closed or merged.", width - 4)));
        return new Rows(
            TerminalStyle.Header("PULL REQUESTS", "Test a pull request. Preview before you deploy."),
            new Text(""),
            TerminalStyle.Line(TicketWorkspaceView.Fit(description, width - 1),
                new Style(Color.FromHex(problem is null ? TerminalStyle.Muted : TerminalStyle.Gold))),
            TerminalStyle.Line(TicketWorkspaceView.Fit(Query.Length == 0 ? "/  Type a number, title, author, or branch..." : "/  " + Query, width - 1),
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))),
            new Text(""),
            table,
            new Markup($"[{TerminalStyle.Muted}]{(Matches.Count == 0 ? "0 matches" : $"{start + 1}-{end} of {Matches.Count}")}   |   Enter fetches it and opens a preview.[/]"),
            TerminalStyle.Card(details, "SELECTED PULL REQUEST"),
            new Markup($"[{TerminalStyle.Gold}]arrows[/] move   [{TerminalStyle.Gold}]Enter[/] preview   [{TerminalStyle.Gold}]Esc[/] back"),
            new Markup($"[{TerminalStyle.Muted}]{(width < 80 ? "Type to search  PgUp/PgDn: page  Ctrl+U: clear" : "Type to search   PgUp/PgDn: page   Home/End: jump   Ctrl+U: clear")}[/]"));
    }

    internal int? Show() => Show(AnsiConsole.Console, () => Console.ReadKey(true));

    internal int? Show(IAnsiConsole console, Func<ConsoleKeyInfo> readKey)
    {
        console.Clear();
        return console.Live(Render(console.Profile.Width, console.Profile.Height))
            .AutoClear(true).Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                context.Refresh();
                while (true)
                {
                    var key = readKey();
                    if (key.Key == ConsoleKey.Escape) return (int?)null;
                    if (key.Key == ConsoleKey.Enter && Selected is not null) return Selected.Number;
                    HandleKey(key, PageSize(console.Profile.Height));
                    context.UpdateTarget(Render(console.Profile.Width, console.Profile.Height));
                    context.Refresh();
                }
            });
    }
}
