using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum VersionKind { Worktrees, Local, Remote }

internal sealed class VersionPicker
{
    private readonly Target[] targets;
    private readonly DeploymentState? active;
    private readonly bool openSolution;
    private readonly VersionKind[] kinds;
    internal VersionKind Kind { get; private set; }
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<Target> Matches { get; private set; } = [];
    // In the solution picker, -1 selects the pinned main-worktree action above the paged targets.
    internal int Index { get; private set; }
    internal Target? Selected => Index < 0 || Matches.Count == 0 ? null : Matches[Index];
    internal bool OpenMainWorktree { get; private set; }

    internal VersionPicker(IEnumerable<Target> targets, DeploymentState? active = null, bool openSolution = false)
    {
        this.targets = targets.Where(x => !openSolution || !x.Remote)
            .OrderBy(x => x.Branch, Files.Comparer).ThenBy(x => x.Worktree, Files.Comparer).ToArray();
        this.active = active;
        this.openSolution = openSolution;
        kinds = openSolution ? [VersionKind.Worktrees, VersionKind.Local] : Enum.GetValues<VersionKind>();
        Kind = this.targets.Any(x => x.Worktree is not null) ? VersionKind.Worktrees :
            this.targets.Any(x => !x.Remote) || openSolution ? VersionKind.Local : VersionKind.Remote;
        Filter();
        var live = Matches.ToList().FindIndex(IsActive);
        if (!openSolution && live >= 0) Index = live;
    }

    internal static int PageSize(int height) => Math.Clamp(height - 18, 3, 18);
    private int VisiblePageSize(int height) => PageSize(height - (openSolution ? 1 : 0));

    internal void HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        var last = Math.Max(0, Matches.Count - 1);
        var first = openSolution ? -1 : 0;
        if (openSolution && Matches.Count == 0) last = -1;
        switch (key.Key)
        {
            case ConsoleKey.Tab:
                ChangeKind(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                break;
            case ConsoleKey.LeftArrow: ChangeKind(-1); break;
            case ConsoleKey.RightArrow: ChangeKind(1); break;
            case ConsoleKey.UpArrow: Index = Index == first ? last : Index - 1; break;
            case ConsoleKey.DownArrow: Index = Index == last ? first : Index + 1; break;
            case ConsoleKey.PageUp: Index = Math.Max(first, Index - pageSize); break;
            case ConsoleKey.PageDown: Index = Math.Min(last, Index + pageSize); break;
            case ConsoleKey.Home: Index = first; break;
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

    private void ChangeKind(int direction)
    {
        Kind = kinds[(Array.IndexOf(kinds, Kind) + direction + kinds.Length) % kinds.Length];
        Filter();
    }

    private static VersionKind KindOf(Target target) => target.Worktree is not null ? VersionKind.Worktrees :
        target.Remote ? VersionKind.Remote : VersionKind.Local;

    private void Filter()
    {
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Matches = targets.Where(x => KindOf(x) == Kind && terms.All(term =>
            x.Branch.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (x.Worktree?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
            x.Ref.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        Index = openSolution && (Query.Length == 0 || Matches.Count == 0) ? -1 : 0;
    }

    private bool IsActive(Target target) => active?.Active == true &&
        (target.Worktree is not null
            ? active.SourceRoot.Length != 0 && Files.Comparer.Equals(Files.Full(target.Worktree), Files.Full(active.SourceRoot))
            : active.SourceRoot.Length == 0 && target.Ref == active.SourceRef);

    internal IRenderable Render(int width, int height)
    {
        var pageSize = VisiblePageSize(height);
        var start = Math.Max(0, Index) / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var tabs = string.Join("   ", kinds.Select(kind =>
        {
            var label = kind switch { VersionKind.Worktrees => "WORKTREES", VersionKind.Local => "LOCAL", _ => "REMOTE" };
            var count = targets.Count(x => KindOf(x) == kind);
            return kind == Kind ? $"[bold {TerminalStyle.Accent} on {TerminalStyle.Surface}] {label} {count} [/]" :
                $"[{TerminalStyle.Muted}]{label} {count}[/]";
        }));
        var description = openSolution ? Kind == VersionKind.Worktrees ? "Open the solution in this checkout." :
            "Reuse a checkout, or create a worktree for this branch." :
            Kind == VersionKind.Worktrees ? "Saved files, including uncommitted changes." :
            Kind == VersionKind.Local ? "Committed local branches. Your checkout stays untouched." :
            "Cached remote branches. No fetch or checkout switch.";
        var available = Math.Max(16, width - 10);
        var branchWidth = Math.Max(8, available * 3 / 5);
        var sourceWidth = Math.Max(8, available - branchWidth);
        var table = new Table().Border(TableBorder.None).Expand()
            .AddColumn(new TableColumn("") { Width = 2, NoWrap = true, Padding = new(0, 0, 1, 0) })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]BRANCH[/]") { Width = branchWidth, NoWrap = true })
            .AddColumn(new TableColumn($"[{TerminalStyle.Muted}]{(Kind == VersionKind.Worktrees ? "WORKTREE FOLDER" : "SOURCE")}[/]")
                { Width = sourceWidth, NoWrap = true });
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end) { table.AddRow("", "", ""); continue; }
            var item = Matches[row];
            var selected = row == Index;
            var style = selected ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text));
            var folder = item.Worktree is not null ? WorktreeFolder(item.Worktree) :
                item.Remote ? item.Branch.Split('/')[0] + " / committed" : "Local / committed";
            if (IsActive(item)) folder = "LIVE  " + folder;
            table.AddRow(TerminalStyle.Line(selected ? ">" : "", style),
                TerminalStyle.Line(item.Branch, style), TerminalStyle.Line(folder, style));
        }
        var selectedTarget = Selected;
        IRenderable details = openSolution && Index < 0
            ? new Text("Choose a repository and open its main worktree solution without specific files.")
            : selectedTarget is null
            ? new Markup($"[{TerminalStyle.Muted}]No matches in this source. Change tabs or press Ctrl+U to clear the search.[/]")
            : new Rows(
                TerminalStyle.Line(selectedTarget.Branch, new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
                TerminalStyle.Line(selectedTarget.Worktree is not null ? DisplayPath(selectedTarget.Worktree) : selectedTarget.Ref));
        var content = new List<IRenderable>
        {
            TerminalStyle.Header(openSolution ? "OPEN SOLUTION" : "VERSION LIBRARY",
                openSolution ? "Open a main solution, worktree, or local branch." : "Find your version. Preview before you deploy."),
            new Text(""),
        };
        if (openSolution)
            content.Add(TerminalStyle.Line((Index < 0 ? "> " : "  ") + "Open main worktree solution",
                Index < 0 ? TerminalStyle.Selection : new Style(Color.FromHex(TerminalStyle.Text))));
        content.AddRange([
            new Markup(tabs),
            new Markup($"[{TerminalStyle.Muted}]{description}[/]"),
            TerminalStyle.Line(Query.Length == 0 ? "/  Type a branch name or any part of a path..." : "/  " + Query,
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))),
            new Text(""),
            table,
            new Markup($"[{TerminalStyle.Muted}]{(Matches.Count == 0 ? "0 matches" : $"{start + 1}-{end} of {Matches.Count}")}   |   " +
                (openSolution ? "Enter opens the solution.[/]" : "Enter opens a preview, not a deployment.[/]")),
            TerminalStyle.Card(details, "SELECTED SOURCE"),
            new Markup($"[{TerminalStyle.Gold}]Tab[/] source   [{TerminalStyle.Gold}]arrows[/] move   [{TerminalStyle.Gold}]Enter[/] " +
                $"{(openSolution ? "open" : "preview")}   [{TerminalStyle.Gold}]Esc[/] back"),
            new Markup($"[{TerminalStyle.Muted}]{(width < 80 ? "Type to search  PgUp/PgDn: page  Ctrl+U: clear" : "Type to search   PgUp/PgDn: page   Home/End: jump   Ctrl+U: clear")}[/]")]);
        return new Rows(content);
    }

    // Solutions can come from several repositories, so the solution picker also names the repository folder.
    private string WorktreeFolder(string worktree)
    {
        var full = Files.Full(worktree);
        return openSolution ? Path.Combine(Path.GetFileName(Path.GetDirectoryName(full)) ?? "", Path.GetFileName(full)) : Path.GetFileName(full);
    }

    private static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~\\" + path[home.Length..] : path;
    }

    internal Target? Show() => Show(AnsiConsole.Console, () => Console.ReadKey(true));

    internal Target? Show(IAnsiConsole console, Func<ConsoleKeyInfo> readKey)
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
                    if (key.Key == ConsoleKey.Enter && openSolution && Index < 0)
                    {
                        OpenMainWorktree = true;
                        return null;
                    }
                    if (key.Key == ConsoleKey.Enter && Selected is not null) return Selected;
                    HandleKey(key, VisiblePageSize(console.Profile.Height));
                    context.UpdateTarget(Render(console.Profile.Width, console.Profile.Height));
                    context.Refresh();
                }
            });
    }
}
