using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>The opened workspace: its checkouts' state and pull requests, and how its SQL files compare with the SQL files folder.</summary>
internal static class TicketWorkspaceView
{
    /// <summary>Lines the action menu below the screen takes: its title, actions, an explanation that may wrap once, and the cursor.</summary>
    internal static int MenuLines(int actions) => actions + 4;

    internal static string StateText(CheckoutStatus? status, string? error)
    {
        if (error is not null) return error;
        if (status is null) return "unknown";
        if (status.Clean) return "clean";
        var parts = new List<string>();
        if (status.Uncommitted != 0) parts.Add($"{status.Uncommitted} uncommitted");
        if (status.Unpushed != 0) parts.Add($"{status.Unpushed} unpushed");
        return string.Join(", ", parts);
    }

    /// <summary>The SQL card's lines, cut to <paramref name="width"/>; none when no folder is configured, or neither the folder nor changed SQL files exist.</summary>
    internal static List<string> SqlLines(TicketSqlComparison? sql, string kind, int width)
    {
        if (sql is null || !sql.FolderExists && sql.Changed == 0) return [];
        static string Names(List<TicketSqlFile> files) => string.Join(", ", files.Select(file => file.Name));
        if (!sql.FolderExists)
            return [Segments(width, ($"No SQL files folder for this {kind}: ", TerminalStyle.Gold), (sql.Folder, TerminalStyle.Text)),
                Segments(width, ($"{TicketWorkspaces.Count(sql.Changed, "changed SQL file")} {(sql.Changed == 1 ? "has" : "have")} no copy: ", TerminalStyle.Muted),
                    (Names(sql.Missing), TerminalStyle.Text))];
        var lines = new List<string>
        {
            Segments(width, ($"{sql.Identical.Count} identical   ", TerminalStyle.Accent),
                ($"{sql.Different.Count} different   ", sql.Different.Count == 0 ? TerminalStyle.Muted : TerminalStyle.Gold),
                ($"{sql.Missing.Count} missing   ", sql.Missing.Count == 0 ? TerminalStyle.Muted : TerminalStyle.Gold),
                ($"{sql.OnlyInFolder.Count} only in the folder", TerminalStyle.Muted))
        };
        if (sql.Different.Count != 0) lines.Add(Segments(width, ("Different: ", TerminalStyle.Gold), (Names(sql.Different), TerminalStyle.Text)));
        if (sql.Missing.Count != 0) lines.Add(Segments(width, ("Missing from the folder: ", TerminalStyle.Gold), (Names(sql.Missing), TerminalStyle.Text)));
        if (sql.Changed == 0) lines.Add(Segments(width, ($"No SQL files changed in this {kind}'s checkouts.", TerminalStyle.Muted)));
        return lines;
    }

    /// <summary>Colored pieces of one line, cut with an ellipsis where the line would pass <paramref name="width"/>.</summary>
    private static string Segments(int width, params (string Text, string Color)[] parts)
    {
        var result = new System.Text.StringBuilder();
        var left = width;
        foreach (var (text, color) in parts)
        {
            if (left <= 0) break;
            var piece = Fit(text, left);
            result.Append($"[{color}]{Terminal.Escape(piece)}[/]");
            left -= piece.Length;
        }
        return result.ToString();
    }

    /// <param name="height">Lines the screen may use above the action menu.</param>
    internal static IRenderable Render(TicketWorkspaceDetails details, int width, int height, DeploymentState? state = null)
    {
        var workspace = details.Workspace;
        var kind = workspace.Ticket is null ? "branch" : "ticket";
        var sql = SqlLines(details.Sql, kind, width - 4);
        var sqlHeight = sql.Count == 0 ? 0 : sql.Count + 2;
        var count = details.Checkouts.Count;
        // The full header, title, table heading, every row, and the SQL card; the compact header and fewer rows when that does not fit.
        var full = height >= 4 + 2 + count + sqlHeight;
        var rows = Math.Min(count, Math.Max(1, height - (full ? 4 : 1) - 2 - sqlHeight));
        if (rows < count) rows = Math.Max(1, rows - 1);
        var content = new List<IRenderable>
        {
            full ? TerminalStyle.Header("TICKET WORKSPACE", "Every repository's checkout of one ticket, opened together.")
                : new Markup($"[bold {TerminalStyle.Accent}]CULTIVATE[/]  [{TerminalStyle.Gold}]TICKET WORKSPACE[/]"),
            new Markup($"[bold {TerminalStyle.Accent}]{Terminal.Escape(Fit(workspace.Key, width - 1))}[/]" +
                $"[{TerminalStyle.Text}]{Terminal.Escape(Fit("  " + (details.Title ?? (workspace.Ticket is null ? "Branch" : "Ticket") + " workspace, " +
                    TicketWorkspaces.Count(count, "checkout")), Math.Max(0, width - 1 - Math.Min(workspace.Key.Length, width - 1))))}[/]")
        };
        var wide = width >= 100;
        var gaps = 2 * (wide ? 4 : 3);
        var available = Math.Max(20, width - 1 - gaps);
        var repositoryWidth = available * (wide ? 20 : 24) / 100;
        var branchWidth = available * (wide ? 11 : 14) / 100;
        var folderWidth = wide ? available * 17 / 100 : 0;
        var stateWidth = available * (wide ? 25 : 30) / 100;
        var pullRequestWidth = available - repositoryWidth - branchWidth - folderWidth - stateWidth;
        TableColumn Column(string title, int columnWidth, bool last = false) =>
            new($"[{TerminalStyle.Muted}]{title}[/]") { Width = columnWidth, NoWrap = true, Padding = new(0, 0, last ? 0 : 2, 0) };
        var table = new Table().Border(TableBorder.None).AddColumn(Column("REPOSITORY", repositoryWidth)).AddColumn(Column("BRANCH", branchWidth));
        if (wide) table.AddColumn(Column("WORKTREE FOLDER", folderWidth));
        table.AddColumn(Column("STATE", stateWidth)).AddColumn(Column("PULL REQUEST", pullRequestWidth, last: true));
        var text = new Style(Color.FromHex(TerminalStyle.Text));
        foreach (var checkout in details.Checkouts.Take(rows))
        {
            var live = state is { Active: true, SourceRoot.Length: > 0 } && Files.Comparer.Equals(Files.Full(checkout.Checkout.Path), Files.Full(state.SourceRoot));
            var stateStyle = new Style(Color.FromHex(checkout.Error is not null ? "#F0A3A3" :
                checkout.Status?.Clean == true ? TerminalStyle.Muted : TerminalStyle.Gold));
            // Cells are cut to their column, since a cell with spaces would otherwise wrap onto a second line.
            var cells = new List<IRenderable>
            {
                TerminalStyle.Line(Fit((live ? "LIVE  " : "") + checkout.Checkout.Repository, repositoryWidth), live ? new Style(Color.FromHex(TerminalStyle.Accent)) : text),
                TerminalStyle.Line(Fit(checkout.Checkout.Branch, branchWidth), text)
            };
            if (wide) cells.Add(TerminalStyle.Line(Fit(Path.GetFileName(checkout.Checkout.Path) + (checkout.Checkout.Primary ? " (primary)" : ""), folderWidth), text));
            cells.Add(TerminalStyle.Line(Fit(StateText(checkout.Status, checkout.Error), stateWidth), stateStyle));
            cells.Add(TerminalStyle.Line(Fit(checkout.PullRequest?.Summary ?? "-", pullRequestWidth),
                checkout.PullRequest is null ? new Style(Color.FromHex(TerminalStyle.Muted)) : text));
            table.AddRow(cells);
        }
        content.Add(table);
        if (rows < count)
            content.Add(TerminalStyle.Line(Fit($"+{count - rows} more; make the terminal taller to see every checkout.", width - 1),
                new Style(Color.FromHex(TerminalStyle.Muted))));
        if (sql.Count != 0)
        {
            var problem = details.Sql is { } comparison && (!comparison.FolderExists || comparison.Different.Count + comparison.Missing.Count != 0);
            content.Add(TerminalStyle.Card(new Rows(sql.Select(line => (IRenderable)new Markup(line))), "SQL FILES",
                problem ? TerminalStyle.Gold : null));
        }
        return new Rows(content);
    }

    internal static string Fit(string value, int width) =>
        width <= 0 ? "" : value.Length <= width ? value : width == 1 ? "…" : value[..(width - 1)] + "…";
}
