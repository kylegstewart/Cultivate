using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>A pull request merged into the base branch, as the GitHub CLI lists it. Only <see cref="Number"/> is certain for one typed by number.
/// <see cref="Repository"/> is the "owner/name" of a pasted pull request link, which may be another repository's.</summary>
internal sealed record MergedPullRequest(int Number, string Title = "", string Author = "", string HeadBranch = "", DateTimeOffset? MergedAt = null,
    string Repository = "")
{
    public string Label => Title.Length == 0 ? $"PR #{Number}" : $"PR #{Number}: {Title}";
    /// <summary>The ticket the branch is named after, else the branch.</summary>
    public string Ticket => WorktreeCatalog.TicketOf(HeadBranch) ?? HeadBranch;
}

internal enum ReleaseTab { Mine, All }

/// <summary>
/// The searchable list of recently merged pull requests to build release files for: the developer's own on one tab, everyone's on the other,
/// newest merge first. A typed number can be chosen even when it is not listed, and so can a pasted pull request link, from any repository.
/// When other repositories are cloned, Ctrl+R asks to list another repository's instead.
/// </summary>
internal sealed class ReleasePicker
{
    private const string Fields = "number,title,author,headRefName,mergedAt";
    private readonly MergedPullRequest[] mine, all;
    private readonly string? problem;
    private readonly string baseBranch;
    private readonly string? repository;
    private readonly bool canChange;
    private readonly DateTimeOffset now;
    internal ReleaseTab Tab { get; private set; }
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<MergedPullRequest> Matches { get; private set; } = [];
    internal int Index { get; private set; }
    internal MergedPullRequest? Selected => Matches.Count == 0 ? null : Matches[Index];
    /// <summary>The pull request Enter chose in <see cref="Show"/>, with the repository of a pasted link.</summary>
    internal MergedPullRequest? Chosen { get; private set; }
    /// <summary>Whether <see cref="Show"/> closed because Ctrl+R asked for another repository.</summary>
    internal bool ChangeRepository { get; private set; }

    /// <param name="repository">The repository listed, named on the screen; null leaves it unnamed, as when only MyMeyer is cloned.</param>
    /// <param name="canChange">Whether Ctrl+R can choose another repository.</param>
    internal ReleasePicker(IEnumerable<MergedPullRequest> mine, IEnumerable<MergedPullRequest> all, string? problem = null, string baseBranch = "master",
        DateTimeOffset? now = null, string? repository = null, bool canChange = false)
    {
        static MergedPullRequest[] Newest(IEnumerable<MergedPullRequest> requests) =>
            requests.OrderByDescending(request => request.MergedAt ?? DateTimeOffset.MinValue).ThenByDescending(request => request.Number).ToArray();
        (this.mine, this.all) = (Newest(mine), Newest(all));
        this.problem = problem;
        this.baseBranch = baseBranch;
        this.repository = repository;
        this.canChange = canChange;
        this.now = now ?? DateTimeOffset.Now;
        Tab = this.mine.Length != 0 || this.all.Length == 0 ? ReleaseTab.Mine : ReleaseTab.All;
        Filter();
    }

    /// <summary>The developer's merged pull requests and everyone's, from two GitHub CLI calls, or none and why.</summary>
    /// <param name="github">"owner/name" of a repository other than the one the GitHub CLI runs in; null for MyMeyer.</param>
    internal static async Task<(List<MergedPullRequest> Mine, List<MergedPullRequest> All, string? Problem)> Load(Func<string[], Task<string>> gh, string baseBranch,
        string? github = null)
    {
        string[] Arguments(params string[] more) => ["pr", "list", .. github is null ? Array.Empty<string>() : ["--repo", github], "--state", "merged",
            "--base", baseBranch, .. more, "--json", Fields];
        try
        {
            var (own, everyone) = (gh(Arguments("--author", "@me", "--limit", "50")), gh(Arguments("--limit", "100")));
            await Task.WhenAll(own, everyone);
            return (Parse(own.Result), Parse(everyone.Result), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return ([], [], PullRequests.Problem(exception)); }
    }

    internal static List<MergedPullRequest> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        return document.RootElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("number", out var number) && number.TryGetInt32(out _))
            .Select(item => new MergedPullRequest(item.GetProperty("number").GetInt32(), Text(item, "title"),
                item.TryGetProperty("author", out var author) ? Text(author, "login") : "", Text(item, "headRefName"),
                DateTimeOffset.TryParse(Text(item, "mergedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var merged) ? merged : null))
            .ToList();
    }

    internal static int PageSize(int height) => Math.Clamp(height - 19, 3, 18);

    private MergedPullRequest[] Listed(ReleaseTab tab) => tab == ReleaseTab.Mine ? mine : all;

    private bool IsListed(MergedPullRequest request) => mine.Contains(request) || all.Contains(request);

    internal void HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        var last = Math.Max(0, Matches.Count - 1);
        switch (key.Key)
        {
            case ConsoleKey.Tab:
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
                Tab = Tab == ReleaseTab.Mine ? ReleaseTab.All : ReleaseTab.Mine;
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
        // A pasted pull request link is built as it is, in whichever repository it names.
        if (ReleaseRepositories.Link(Query) is { } link)
        {
            Matches = [new MergedPullRequest(link.Number, Repository: link.GitHub)];
            Index = 0;
            return;
        }
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Listed(Tab).Where(request => terms.All(term => ("#" + request.Number).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            request.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || request.Author.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            request.HeadBranch.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        // A typed number comes first. One not on this tab comes last, with its details when the other tab lists it.
        if (int.TryParse(Query.Trim().TrimStart('#'), out var number) && number > 0)
        {
            if (matches.FirstOrDefault(request => request.Number == number) is { } exact)
            {
                matches.Remove(exact);
                matches.Insert(0, exact);
            }
            else matches.Add(mine.Concat(all).FirstOrDefault(request => request.Number == number) ?? new MergedPullRequest(number));
        }
        Matches = matches;
        Index = 0;
    }

    internal IRenderable Render(int width, int height)
    {
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var tabs = string.Join("   ", Enum.GetValues<ReleaseTab>().Select(tab =>
        {
            var label = (tab == ReleaseTab.Mine ? "MINE " : "ALL ") + Listed(tab).Length;
            return tab == Tab ? $"[bold {TerminalStyle.Accent} on {TerminalStyle.Surface}] {label} [/]" : $"[{TerminalStyle.Muted}]{label}[/]";
        }));
        var description = (repository is null ? "" : repository + ": ") + (problem is not null ? $"{problem}. Type a pull request number to build it anyway." :
            Tab == ReleaseTab.Mine ? $"Your pull requests merged into {baseBranch}, newest first. Type a number or paste a link for any other." :
            $"Pull requests merged into {baseBranch} by anyone, newest first. Type a number or paste a link for any other.");
        const int numberWidth = 7, mergedWidth = 13;
        var available = Math.Max(30, width - 14);
        var authorWidth = Math.Clamp(available / 7, 6, 14);
        var ticketWidth = Math.Clamp(available / 6, 6, 16);
        var titleWidth = Math.Max(6, available - numberWidth - mergedWidth - authorWidth - ticketWidth);
        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("") { Width = 2, NoWrap = true, Padding = new(0, 0, 1, 0) })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]PR[/]") { Width = numberWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]TICKET[/]") { Width = ticketWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]TITLE[/]") { Width = titleWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]AUTHOR[/]") { Width = authorWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]MERGED[/]") { Width = mergedWidth, NoWrap = true });
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end)
            {
                table.AddRow("", "", "", "", "", "");
                continue;
            }
            var item = Matches[row];
            var style = row == Index ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            var listed = IsListed(item);
            table.AddRow(TerminalStyle.Line(row == Index ? ">" : "", style), TerminalStyle.Line("#" + item.Number, style),
                TerminalStyle.Line(TicketWorkspaceView.Fit(item.Ticket, ticketWidth), style),
                TerminalStyle.Line(TicketWorkspaceView.Fit(listed ? item.Title : item.Repository.Length != 0 ? "Build " + item.Repository + " by link" : "Build by number",
                    titleWidth), style),
                TerminalStyle.Line(TicketWorkspaceView.Fit(item.Author, authorWidth), style),
                TerminalStyle.Line(item.MergedAt is null ? "" : TicketPicker.Age(item.MergedAt, now), style));
        }
        IRenderable details = Selected is not { } selected
            ? new Markup($"[{TerminalStyle.Muted}]{(Listed(Tab).Length == 0 && Query.Length == 0 ? "No merged pull requests are listed here. Change tabs, or type a number." :
                "No matches. Change tabs, type a number, or press Ctrl+U to clear the search.")}[/]")
            : new Rows(
                TerminalStyle.Line(TicketWorkspaceView.Fit(selected.Label, width - 4), new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
                TerminalStyle.Line(TicketWorkspaceView.Fit(IsListed(selected)
                    ? string.Join("  |  ", new[]
                    {
                        $"#{selected.Number}" + (selected.Author.Length == 0 ? "" : " by " + selected.Author), selected.HeadBranch,
                        selected.MergedAt is { } merged ? $"merged {merged.ToLocalTime():yyyy-MM-dd HH:mm}" : ""
                    }.Where(part => part.Length != 0))
                    : selected.Repository.Length != 0
                        ? $"From a link. Enter asks GitHub about {selected.Repository} #{selected.Number}; it is built in that repository's clone once merged into its release branch."
                        : $"Not listed. Enter asks GitHub about it; only a pull request merged into {baseBranch} can be built.", width - 4)));
        return new Rows(
            TerminalStyle.Header("RELEASE FILES", "Build a merged pull request's release files."),
            new Text(""),
            new Markup(tabs),
            TerminalStyle.Line(TicketWorkspaceView.Fit(description, width - 1),
                new Style(Color.FromHex(problem is null ? TerminalStyle.Muted : TerminalStyle.Gold))),
            TerminalStyle.Line(TicketWorkspaceView.Fit(Query.Length == 0 ? "/  Type a number, ticket, title, or author..." : "/  " + Query, width - 1),
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))),
            new Text(""),
            table,
            new Markup($"[{TerminalStyle.Muted}]{(Matches.Count == 0 ? "0 matches" : $"{start + 1}-{end} of {Matches.Count}")}   |   Enter previews it; nothing is built yet.[/]"),
            TerminalStyle.Card(details, "SELECTED PULL REQUEST"),
            new Markup($"[{TerminalStyle.Gold}]Tab[/] mine/all   [{TerminalStyle.Gold}]arrows[/] move   [{TerminalStyle.Gold}]Enter[/] preview   [{TerminalStyle.Gold}]Esc[/] back"),
            new Markup($"[{TerminalStyle.Muted}]{(canChange
                ? width < 80 ? "Type to search  Ctrl+R: repository  Ctrl+U: clear" : "Type to search   Ctrl+R: repository   PgUp/PgDn: page   Ctrl+U: clear"
                : width < 80 ? "Type to search  PgUp/PgDn: page  Ctrl+U: clear" : "Type to search   PgUp/PgDn: page   Home/End: jump   Ctrl+U: clear")}[/]"));
    }

    /// <summary>The number Enter chose, or null for Esc, or for Ctrl+R with <see cref="ChangeRepository"/> set.</summary>
    internal int? Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey)
    {
        int? chosen = null;
        (Chosen, ChangeRepository) = (null, false);
        KeyScreen.Show(console, Render, (key, height) =>
        {
            if (key.Key == ConsoleKey.Escape) return true;
            if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control) && canChange) return ChangeRepository = true;
            if (key.Key == ConsoleKey.Enter && Selected is not null) return (chosen = (Chosen = Selected).Number) is not null;
            HandleKey(key, PageSize(height));
            return false;
        }, readKey);
        return chosen;
    }
}
