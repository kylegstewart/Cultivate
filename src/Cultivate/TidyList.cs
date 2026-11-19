using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum TidySort { Largest, Smallest, Oldest, Newest, Name }

internal enum TidyCommand { None, Review, Rescan, Back }

/// <summary>What must never be deleted: the active deployment's worktree, the one this terminal or Cultivate runs in, and locked ones.</summary>
internal sealed record TidyGuard(DeploymentState? State, string CurrentFolder, string Program)
{
    public static TidyGuard Current(DeploymentState? state) => new(state, Environment.CurrentDirectory, AppContext.BaseDirectory);

    /// <summary>Why <paramref name="checkout"/> cannot be deleted and the short label its row shows instead of a checkbox, or null when it can be.</summary>
    public (string Reason, string Tag)? Check(Checkout checkout)
    {
        if (checkout.Primary) return ("This is the repository's main checkout, which is never deleted.", "main");
        if (State?.Active == true && Inside(checkout.Path, State.SourceRoot)) return ("The active webfarm deployment runs from this worktree.", "live");
        if (State?.Active == true && State.Libraries.Any(library => Inside(checkout.Path, library.Worktree)))
            return ("The active webfarm deployment builds a library from this worktree.", "live");
        if (Inside(checkout.Path, Program)) return ("Cultivate is running from this worktree.", "in use");
        if (Inside(checkout.Path, CurrentFolder)) return ("This terminal's current folder is inside this worktree.", "in use");
        if (checkout.Locked) return ("Git has this worktree locked. Unlock it with git worktree unlock to delete it here.", "locked");
        return null;
    }

    internal static bool Inside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        var full = Files.Full(path);
        root = Files.Full(root);
        return Files.Comparer.Equals(full, root) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TidyRow(Checkout checkout, (string Reason, string Tag)? protection)
{
    public Checkout Checkout { get; } = checkout;
    public string Path => Checkout.Path;
    /// <summary>Why this worktree cannot be selected for deletion, or null when it can be.</summary>
    public string? Protection { get; } = protection?.Reason;
    public string? Tag { get; } = protection?.Tag;
    public long? Bytes { get; internal set; }
    /// <summary>When the size was measured, which may be an earlier visit's scan.</summary>
    public DateTimeOffset? Scanned { get; internal set; }
    public string? SizeError { get; internal set; }
    /// <summary>The newest write among the worktree's own files, known once its size walk finishes.</summary>
    public DateTimeOffset? LastWrite { get; internal set; }
    public CheckoutStatus? Status { get; internal set; }
    public string? StatusError { get; internal set; }

    public string Branch => BranchOf(Checkout);
    public string Name => NameOf(Checkout);

    internal static string BranchOf(Checkout checkout) =>
        checkout.Detached ? $"(detached {checkout.Head[..Math.Min(7, checkout.Head.Length)]})" : checkout.Branch;

    /// <summary>"Repository / branch", as every tidy screen names a worktree.</summary>
    internal static string NameOf(Checkout checkout) => $"{checkout.Repository} / {BranchOf(checkout)}";

    /// <summary>What deleting this worktree would lose, once its state is known.</summary>
    public TidyLoss? Loss => Status is { } status ? TidyLoss.Of(Checkout, status) : null;
    public bool Safe => Protection is null && Loss is { None: true };
    public DateTimeOffset? LastActivity => Status?.LastCommit is { } commit
        ? LastWrite is { } write && write > commit ? write : commit : LastWrite;

    public string StateText => Status is { } status ? State(status) : StatusError is null ? "…" : "unknown";

    internal static string State(CheckoutStatus status) => status.Clean ? "clean" : string.Join(", ",
        new[] { status.Uncommitted == 0 ? "" : $"{status.Uncommitted} uncommitted", status.Unpushed == 0 ? "" : $"{status.Unpushed} unpushed" }
            .Where(part => part.Length != 0));
}

/// <summary>
/// The Tidy worktrees screen: every linked worktree with its size, age, and state, sorted, filtered, and selected by size and date.
/// Sizes and states come from the saved scans (see <see cref="TidyScan"/>), so the list appears at once; F5 scans every worktree again.
/// </summary>
internal sealed class TidyList
{
    internal static readonly int[] AgeDays = [0, 7, 14, 30, 90];
    internal static readonly long[] SizeLimits = [0, 100L << 20, 500L << 20, 1L << 30, 5L << 30];
    private readonly List<TidyRow> rows;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<string> selected = new(Files.Comparer);
    internal IReadOnlyList<TidyRow> Rows => rows;
    internal IReadOnlyList<TidyRow> Matches { get; private set; } = [];
    internal int Index { get; private set; }
    internal TidyRow? Highlighted => Matches.Count == 0 ? null : Matches[Index];
    internal string Query { get; private set; } = "";
    internal TidySort Sort { get; private set; }
    internal int Age { get; private set; }
    internal int Size { get; private set; }
    internal bool SafeOnly { get; private set; }
    internal string Notice { get; private set; } = "";
    /// <summary>Selected rows in the current order, including any a filter hides.</summary>
    internal IReadOnlyList<TidyRow> Selected => Matches.Concat(rows.Except(Matches))
        .Where(row => row.Protection is null && selected.Contains(row.Path)).ToList();

    internal TidyList(IEnumerable<Checkout> checkouts, TidyGuard guard, Func<DateTimeOffset> clock)
    {
        rows = checkouts.Where(checkout => !checkout.Primary).Select(checkout => new TidyRow(checkout, guard.Check(checkout))).ToList();
        this.clock = clock;
        Refresh();
    }

    internal static int PageSize(int height) => Math.Clamp(height - 18, 3, 40);

    private TidyRow? Row(string path) => rows.FirstOrDefault(row => Files.Comparer.Equals(row.Path, path));

    internal void SetScan(TidyScan scan)
    {
        SetSize(scan.Path, new(scan.Bytes, scan.LastWrite), scan.Scanned);
        SetStatus(scan.Path, scan.Status);
    }

    internal void SetSize(string path, TidyMeasurement measurement, DateTimeOffset measured)
    {
        if (Row(path) is not { } row) return;
        row.Bytes = measurement.Bytes;
        row.LastWrite = measurement.LastWrite;
        row.Scanned = measured;
        row.SizeError = null;
        Refresh();
    }

    internal void SetSizeError(string path, string error)
    {
        if (Row(path) is not { } row) return;
        row.Bytes = null;
        row.LastWrite = null;
        row.SizeError = error;
        Refresh();
    }

    internal void SetStatus(string path, CheckoutStatus status)
    {
        if (Row(path) is not { } row) return;
        row.Status = status;
        row.StatusError = null;
        Refresh();
    }

    internal void SetStatusError(string path, string error)
    {
        if (Row(path) is not { } row) return;
        row.Status = null;
        row.StatusError = error;
        Refresh();
    }

    internal void Remove(IEnumerable<string> paths)
    {
        var removed = paths.ToHashSet(Files.Comparer);
        rows.RemoveAll(row => removed.Contains(row.Path));
        selected.Clear();
        Refresh();
    }

    internal TidyCommand HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        Notice = "";
        var last = Math.Max(0, Matches.Count - 1);
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.Escape: return TidyCommand.Back;
            case ConsoleKey.Delete:
            case ConsoleKey.Enter:
                if (Selected.Count != 0) return TidyCommand.Review;
                Notice = "Select worktrees with Space first; nothing is deleted until you review and confirm.";
                break;
            case ConsoleKey.UpArrow: Index = Index == 0 ? last : Index - 1; break;
            case ConsoleKey.DownArrow: Index = Index == last ? 0 : Index + 1; break;
            case ConsoleKey.PageUp: Index = Math.Max(0, Index - pageSize); break;
            case ConsoleKey.PageDown: Index = Math.Min(last, Index + pageSize); break;
            case ConsoleKey.Home: Index = 0; break;
            case ConsoleKey.End: Index = last; break;
            case ConsoleKey.Tab:
                var sorts = Enum.GetValues<TidySort>();
                Sort = sorts[((int)Sort + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? sorts.Length - 1 : 1)) % sorts.Length];
                Refresh();
                break;
            case ConsoleKey.F2:
                Age = (Age + 1) % AgeDays.Length;
                Refresh();
                break;
            case ConsoleKey.F3:
                Size = (Size + 1) % SizeLimits.Length;
                Refresh();
                break;
            case ConsoleKey.F4:
                SafeOnly = !SafeOnly;
                Refresh();
                break;
            case ConsoleKey.F5: return TidyCommand.Rescan;
            case ConsoleKey.Spacebar:
                if (Highlighted is not { } row) break;
                if (row.Protection is not null) Notice = row.Protection;
                else if (!selected.Remove(row.Path)) selected.Add(row.Path);
                break;
            case ConsoleKey.A when control:
                foreach (var listed in Matches.Where(listed => listed.Protection is null)) selected.Add(listed.Path);
                break;
            case ConsoleKey.D when control:
                selected.Clear();
                break;
            case ConsoleKey.U when control:
                Query = "";
                Refresh();
                break;
            case ConsoleKey.Backspace:
                if (Query.Length == 0) break;
                Query = Query[..^1];
                Refresh();
                break;
            default:
                if (char.IsControl(key.KeyChar) || char.IsWhiteSpace(key.KeyChar) || control || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) break;
                Query += key.KeyChar;
                Refresh();
                break;
        }
        return TidyCommand.None;
    }

    /// <summary>Filters and sorts again, keeping the highlight on the same worktree while it is still listed.</summary>
    private void Refresh()
    {
        var highlighted = Highlighted?.Path;
        var now = clock();
        var listed = rows.Where(row => (Query.Length == 0 || new[] { row.Checkout.Repository, row.Branch, row.Path, row.Checkout.Ticket ?? "" }
                .Any(field => field.Contains(Query, StringComparison.OrdinalIgnoreCase))) &&
            (Age == 0 || row.LastActivity is { } activity && now - activity > TimeSpan.FromDays(AgeDays[Age])) &&
            (Size == 0 || row.Bytes >= SizeLimits[Size]) && (!SafeOnly || row.Safe));
        var ordered = Sort switch
        {
            TidySort.Largest => listed.OrderBy(row => row.Bytes is null).ThenByDescending(row => row.Bytes),
            TidySort.Smallest => listed.OrderBy(row => row.Bytes is null).ThenBy(row => row.Bytes),
            TidySort.Oldest => listed.OrderBy(row => row.LastActivity is null).ThenBy(row => row.LastActivity),
            TidySort.Newest => listed.OrderBy(row => row.LastActivity is null).ThenByDescending(row => row.LastActivity),
            _ => listed.OrderBy(row => 0)
        };
        Matches = ordered.ThenBy(row => row.Checkout.Repository, Files.Comparer).ThenBy(row => row.Branch, Files.Comparer)
            .ThenBy(row => row.Path, Files.Comparer).ToList();
        var index = highlighted is null ? -1 : Matches.ToList().FindIndex(row => Files.Comparer.Equals(row.Path, highlighted));
        Index = index >= 0 ? index : Math.Clamp(Index, 0, Math.Max(0, Matches.Count - 1));
    }

    internal static string SortLabel(TidySort sort) => sort switch
    {
        TidySort.Largest => "largest first", TidySort.Smallest => "smallest first", TidySort.Oldest => "oldest first",
        TidySort.Newest => "newest first", _ => "by name"
    };

    internal static string AgeText(TimeSpan age) => age.TotalDays switch
    {
        < 1 => "today",
        < 2 => "1 day",
        < 14 => $"{(int)age.TotalDays} days",
        < 60 => $"{(int)(age.TotalDays / 7)} weeks",
        < 365 => $"{(int)(age.TotalDays / 30)} months",
        < 730 => "1 year",
        _ => $"{(int)(age.TotalDays / 365)} years"
    };

    private static string Clip(string text, int width) =>
        width <= 0 ? "" : text.Length <= width ? text : width == 1 ? "…" : text[..(width - 1)] + "…";

    /// <summary>One line of markup that is cut off at <paramref name="width"/> instead of wrapping.</summary>
    private static IRenderable OneLine(string markup, int width) =>
        new Table().Border(TableBorder.None).HideHeaders()
            .AddColumn(new TableColumn("") { Width = Math.Max(1, width), NoWrap = true, Padding = new(0, 0, 0, 0) })
            .AddRow(new Markup(markup) { Overflow = Overflow.Ellipsis });

    private static string Muted(string text) => $"[{TerminalStyle.Muted}]{Terminal.Escape(text)}[/]";
    private static string Gold(string text) => $"[{TerminalStyle.Gold}]{Terminal.Escape(text)}[/]";

    internal IRenderable Render(int width, int height)
    {
        var now = clock();
        var pageSize = PageSize(height);
        var start = Index / pageSize * pageSize;
        var end = Math.Min(start + pageSize, Matches.Count);
        var narrow = width < 80;
        var line = width - 1;

        var ageLabel = Age == 0 ? "any" : narrow ? $">{AgeDays[Age]}d" : $"older than {AgeDays[Age]} days";
        var sizeLabel = Size == 0 ? "any" : narrow ? $"≥{TidySizes.Format(SizeLimits[Size])}" : $"{TidySizes.Format(SizeLimits[Size])} or more";
        string Filter(string name, string value, bool active) => Muted(name) + " " + (active ? Gold(value) : Terminal.Escape(value));
        var filters = string.Join(narrow ? "  " : "   ", Filter(narrow ? "Sort" : "Sort:", narrow ? SortLabel(Sort).Replace(" first", "") : SortLabel(Sort), false),
            Filter(narrow ? "Age" : "Age:", ageLabel, Age != 0), Filter(narrow ? "Size" : "Size:", sizeLabel, Size != 0),
            SafeOnly ? Gold("Safe only") : Muted(narrow ? "Safe off" : "Safe only: off"));

        var stateWidth = width >= 100 ? 26 : width >= 80 ? 18 : 13;
        var nameWidth = Math.Max(8, line - 29 - stateWidth);
        TableColumn Column(string header, int columnWidth, int right = 1) =>
            new($"[{TerminalStyle.Muted}]{header}[/]") { Width = columnWidth, NoWrap = true, Padding = new(0, 0, right, 0) };
        var table = new Table().Border(TableBorder.None)
            .AddColumn(Column("", 1)).AddColumn(Column("", 6)).AddColumn(Column("WORKTREE", nameWidth))
            .AddColumn(Column("SIZE", 8)).AddColumn(Column("AGE", 9)).AddColumn(Column("STATE", stateWidth, 0));
        for (var row = start; row < start + pageSize; row++)
        {
            if (row >= end)
            {
                table.AddRow("", "", "", "", "", "");
                continue;
            }
            var item = Matches[row];
            var highlighted = row == Index;
            Style Cell(string color) => highlighted ? TerminalStyle.Selection : new Style(Color.FromHex(color));
            var size = item.Bytes is { } bytes ? TidySizes.Format(bytes) : item.SizeError is null ? "…" : "?";
            var age = item.LastActivity is { } activity ? AgeText(now - activity) : "…";
            table.AddRow(TerminalStyle.Line(highlighted ? ">" : "", Cell(TerminalStyle.Text)),
                TerminalStyle.Line(item.Tag ?? (selected.Contains(item.Path) ? "[x]" : "[ ]"),
                    Cell(item.Tag is not null ? TerminalStyle.Gold : selected.Contains(item.Path) ? TerminalStyle.Accent : TerminalStyle.Muted)),
                TerminalStyle.Line(item.Name, Cell(TerminalStyle.Text)),
                TerminalStyle.Line(size, Cell(TerminalStyle.Text)),
                TerminalStyle.Line(age, Cell(TerminalStyle.Text)),
                TerminalStyle.Line(item.StateText, Cell(item.Loss is { None: false } || item.StatusError is not null ? TerminalStyle.Gold : TerminalStyle.Muted)));
        }

        var chosen = Selected;
        var summary = (Matches.Count == 0 ? "0 listed" : $"{start + 1}-{end} of {Matches.Count}") +
            $"  ·  listed {TidySizes.Format(Matches.Sum(row => row.Bytes ?? 0))}  ·  {chosen.Count} selected ({TidySizes.Format(chosen.Sum(row => row.Bytes ?? 0))})";
        var notice = Notice.Length != 0 ? Gold(Clip(Notice, line)) :
            Muted(Clip(rows.Count == 0 ? "No linked worktrees were found. Main checkouts are never listed." :
                "Sizes and states are saved from each worktree's last scan; F5 scans them all again.", line));

        var content = new List<IRenderable>
        {
            TerminalStyle.Header("TIDY WORKTREES", "Nothing is deleted until you confirm."),
            TerminalStyle.Line(Clip(Query.Length == 0 ? "/  Type to search repository, branch, folder or ticket..." : "/  " + Query, line),
                new Style(Color.FromHex(Query.Length == 0 ? TerminalStyle.Muted : TerminalStyle.Accent))),
            OneLine(filters, line),
            table,
            OneLine(Terminal.Escape(Clip(summary, line)), line),
            OneLine(notice, line),
            TerminalStyle.Card(Details(Highlighted, now, width - 4), "HIGHLIGHTED WORKTREE"),
            OneLine(Keys(width, first: true), line),
            OneLine(Keys(width, first: false), line)
        };
        return new Rows(content);
    }

    private static string Keys(int width, bool first)
    {
        string Key(string key, string action) => $"[{TerminalStyle.Gold}]{key}[/] {action}";
        if (width >= 100)
            return first
                ? string.Join("   ", Key("Space", "select"), Key("Ctrl+A", "select listed"), Key("Ctrl+D", "clear selection"), Key("Del", "review and delete"), Key("Esc", "back"))
                : string.Join("   ", Key("Tab", "sort"), Key("F2", "age"), Key("F3", "size"), Key("F4", "safe only"), Key("F5", "rescan"), Muted("arrows/PgUp/PgDn"), Key("Ctrl+U", "clear search"));
        if (width >= 80)
            return first
                ? string.Join("  ", Key("Space", "select"), Key("Ctrl+A", "all listed"), Key("Ctrl+D", "clear"), Key("Del", "review & delete"), Key("Esc", "back"))
                : string.Join("  ", Key("Tab", "sort"), Key("F2", "age"), Key("F3", "size"), Key("F4", "safe only"), Key("F5", "rescan"), Key("Ctrl+U", "clear search"));
        return first
            ? string.Join("  ", Key("Space", "select"), Key("Ctrl+A", "all"), Key("Ctrl+D", "none"), Key("Del", "delete"), Key("Esc", "back"))
            : string.Join("  ", Key("Tab", "sort"), Key("F2", "age"), Key("F3", "size"), Key("F4", "safe"), Key("F5", "scan"), Key("Ctrl+U", "clear"));
    }

    private static IRenderable Details(TidyRow? row, DateTimeOffset now, int width)
    {
        if (row is null)
            return new Rows(TerminalStyle.Line("No worktrees match."),
                TerminalStyle.Line(Clip("Ctrl+U clears the search; F2, F3 and F4 change the filters.", width)), new Text(""), new Text(""));
        string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var size = row.Bytes is not { } bytes ? row.SizeError is null ? "Size not measured" : "Size unknown: " + row.SizeError :
            TidySizes.Format(bytes) + (row.Scanned is { } scanned ? $" (scanned {Date(scanned)})" : "");
        var activity = row.LastActivity is not { } last ? "last activity: checking…" :
            $"last activity {Date(last)} ({AgeText(now - last)} ago, {(row.LastWrite is { } write && write == last ? "file saved" : "commit")})";
        var state = row.Protection ?? (row.Status is not { } status
            ? row.StatusError is null ? "Checking for uncommitted changes and unpushed commits…" : "State could not be read: " + row.StatusError
            : (row.Loss!.None ? "Safe to delete: nothing would be lost." : $"Deleting it would lose {row.Loss}.") +
                (TidyLoss.KeptOnBranch(row.Checkout, status) is { } kept ? "  " + kept :
                    row.Loss.None && status.Subject.Length != 0 ? "  Last commit: " + status.Subject : ""));
        return new Rows(
            TerminalStyle.Line(Clip(row.Name + (row.Checkout.Ticket is { } ticket ? $"  ·  ticket {ticket}" : ""), width),
                new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
            TerminalStyle.Line(Clip(row.Path, width)),
            TerminalStyle.Line(Clip(size + "  ·  " + activity, width)),
            TerminalStyle.Line(Clip(state, width), new Style(Color.FromHex(row.Protection is not null || row.Loss is { None: false } ||
                row.StatusError is not null ? TerminalStyle.Gold : TerminalStyle.Muted))));
    }

    /// <summary>Shows the list until a key asks to review the selection, scan again, or go back, and returns that command.</summary>
    internal TidyCommand Show(IAnsiConsole console, Func<ConsoleKeyInfo?> readKey)
    {
        console.Clear();
        return console.Live(Render(console.Profile.Width, console.Profile.Height))
            .AutoClear(true).Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                context.Refresh();
                var size = (console.Profile.Width, console.Profile.Height);
                while (true)
                {
                    var changed = false;
                    if (readKey() is { } pressed)
                    {
                        var command = HandleKey(pressed, PageSize(console.Profile.Height));
                        if (command != TidyCommand.None) return command;
                        changed = true;
                    }
                    if (!changed && size == (console.Profile.Width, console.Profile.Height)) continue;
                    size = (console.Profile.Width, console.Profile.Height);
                    context.UpdateTarget(Render(size.Width, size.Height));
                    context.Refresh();
                }
            });
    }
}
