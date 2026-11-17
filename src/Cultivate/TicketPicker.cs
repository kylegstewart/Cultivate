using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum TicketTab { Tickets, Branches }

/// <summary>The searchable list of workspaces: tickets on one tab, other shared branch names on the other.</summary>
internal sealed class TicketPicker
{
    private readonly TicketWorkspace[] workspaces;
    private readonly DateTimeOffset now;
    internal DeploymentState? Active { get; set; }
    internal TicketTab Tab { get; private set; }
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<TicketWorkspace> Matches { get; private set; } = [];
    internal int Index { get; private set; }
    internal TicketWorkspace? Selected => Matches.Count == 0 ? null : Matches[Index];

    internal TicketPicker(IEnumerable<TicketWorkspace> workspaces, DeploymentState? active = null, DateTimeOffset? now = null)
    {
        this.workspaces = workspaces.OrderByDescending(workspace => workspace.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(workspace => workspace.Key, StringComparer.Ordinal).ToArray();
        this.now = now ?? DateTimeOffset.Now;
        Active = active;
        var live = this.workspaces.FirstOrDefault(IsActive);
        Tab = live is not null ? TabOf(live) : this.workspaces.Any(workspace => workspace.Ticket is not null) || this.workspaces.Length == 0
            ? TicketTab.Tickets : TicketTab.Branches;
        Filter();
        if (live is not null) Index = Math.Max(0, Matches.ToList().IndexOf(live));
    }

    internal static int PageSize(int height) => Math.Clamp(height - 18, 3, 18);

    private static TicketTab TabOf(TicketWorkspace workspace) => workspace.Ticket is not null ? TicketTab.Tickets : TicketTab.Branches;

    internal bool IsActive(TicketWorkspace workspace) => Active is { Active: true, SourceRoot.Length: > 0 } active &&
        workspace.Checkouts.Any(checkout => Files.Comparer.Equals(Files.Full(checkout.Path), Files.Full(active.SourceRoot)));

    internal void HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        var last = Math.Max(0, Matches.Count - 1);
        switch (key.Key)
        {
            case ConsoleKey.Tab:
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
                Tab = Tab == TicketTab.Tickets ? TicketTab.Branches : TicketTab.Tickets;
                Filter();
                break;
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
        Matches = workspaces.Where(workspace => TabOf(workspace) == Tab && terms.All(term =>
            workspace.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            workspace.Checkouts.Any(checkout => checkout.Repository.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                checkout.Branch.Contains(term, StringComparison.OrdinalIgnoreCase)))).ToArray();
        Index = 0;
    }

    /// <summary>"today", "yesterday", "3 days ago", "2 weeks ago", "5 months ago".</summary>
    internal static string Age(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is null) return "unknown";
        var days = (now.Date - date.Value.ToOffset(now.Offset).Date).Days;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : days < 14 ? $"{days} days ago" : days < 60 ? $"{days / 7} weeks ago" :
            days < 730 ? $"{days / 30} months ago" : $"{days / 365} years ago";
    }

    /// <summary>The repository names when they fit in <paramref name="width"/>, otherwise how many there are.</summary>
    internal static string RepositoryList(TicketWorkspace workspace, int width)
    {
        var names = string.Join(", ", workspace.Repositories);
        var count = workspace.Repositories.Count;
        return names.Length <= width ? names : $"{count} {(count == 1 ? "repository" : "repositories")}";
    }

    internal IRenderable Render(int width, int height)
    {
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var tabs = string.Join("   ", Enum.GetValues<TicketTab>().Select(tab =>
        {
            var label = (tab == TicketTab.Tickets ? "TICKETS " : "BRANCHES ") + workspaces.Count(workspace => TabOf(workspace) == tab);
            return tab == Tab ? $"[bold {TerminalStyle.Accent} on {TerminalStyle.Surface}] {label} [/]" : $"[{TerminalStyle.Muted}]{label}[/]";
        }));
        var description = Tab == TicketTab.Tickets ? "Ticket branches, such as 38636 and 38636-2." :
            "Other branches, grouped by name across repositories.";
        var available = Math.Max(20, width - 11);
        var ageWidth = 13;
        var keyWidth = Math.Clamp(available / 3, 8, 30);
        var repositoryWidth = Math.Max(4, available - keyWidth - ageWidth);
        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("") { Width = 2, NoWrap = true, Padding = new(0, 0, 1, 0) })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]{(Tab == TicketTab.Tickets ? "TICKET" : "BRANCH")}[/]") { Width = keyWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]REPOSITORIES[/]") { Width = repositoryWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]LAST COMMIT[/]") { Width = ageWidth, NoWrap = true });
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end)
            {
                table.AddRow("", "", "", "");
                continue;
            }
            var item = Matches[row];
            var style = row == Index ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            table.AddRow(TerminalStyle.Line(row == Index ? ">" : "", style), TerminalStyle.Line((IsActive(item) ? "LIVE  " : "") + item.Key, style),
                TerminalStyle.Line(RepositoryList(item, repositoryWidth), style), TerminalStyle.Line(Age(item.LastActivity, now), style));
        }
        IRenderable details = Selected is not { } selected
            ? new Markup($"[{TerminalStyle.Muted}]{(workspaces.Any(workspace => TabOf(workspace) == Tab) ? "No matches. Change tabs or press Ctrl+U to clear the search." :
                Tab == TicketTab.Tickets ? "No ticket branches were found in any repository." : "No other branches with work were found.")}[/]")
            : new Rows(
                TerminalStyle.Line(TicketWorkspaceView.Fit($"{selected.Key}   {TicketWorkspaces.Count(selected.Checkouts.Count, "checkout")}" + (IsActive(selected) ? "   LIVE" : ""), width - 4),
                    new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
                TerminalStyle.Line(TicketWorkspaceView.Fit(string.Join(", ", selected.Checkouts.Select(checkout =>
                    checkout.Branch == selected.Key ? checkout.Repository : $"{checkout.Repository} {checkout.Branch}")), width - 4)));
        return new Rows(
            TerminalStyle.Header("TICKET WORKSPACES", "One ticket's work in every repository, together."),
            new Text(""),
            new Markup(tabs),
            new Markup($"[{TerminalStyle.Muted}]{description}[/]"),
            TerminalStyle.Line(TicketWorkspaceView.Fit(Query.Length == 0 ? "/  Type a ticket, branch, or repository..." : "/  " + Query, width - 1),
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))),
            new Text(""),
            table,
            new Markup($"[{TerminalStyle.Muted}]{(Matches.Count == 0 ? "0 matches" : $"{start + 1}-{end} of {Matches.Count}")}   |   Enter shows the workspace.[/]"),
            TerminalStyle.Card(details, "SELECTED WORKSPACE"),
            new Markup($"[{TerminalStyle.Gold}]Tab[/] tickets/branches   [{TerminalStyle.Gold}]arrows[/] move   [{TerminalStyle.Gold}]Enter[/] open   [{TerminalStyle.Gold}]Esc[/] back"),
            new Markup($"[{TerminalStyle.Muted}]{(width < 80 ? "Type to search  PgUp/PgDn: page  Ctrl+U: clear" : "Type to search   PgUp/PgDn: page   Home/End: jump   Ctrl+U: clear")}[/]"));
    }

    internal TicketWorkspace? Show() => Show(AnsiConsole.Console, () => Console.ReadKey(true));

    internal TicketWorkspace? Show(IAnsiConsole console, Func<ConsoleKeyInfo> readKey)
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
                    if (key.Key == ConsoleKey.Escape) return null;
                    if (key.Key == ConsoleKey.Enter && Selected is not null) return Selected;
                    HandleKey(key, PageSize(console.Profile.Height));
                    context.UpdateTarget(Render(console.Profile.Width, console.Profile.Height));
                    context.Refresh();
                }
            });
    }
}
