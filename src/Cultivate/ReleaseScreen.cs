using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum ReleaseChoice { Build, Clean, Back }

/// <summary>
/// The home screen's Build release files action: choose a merged pull request, check what its preview would write, build it, and open the
/// folder. It never acts on the dev webfarm, so it needs no deployment status and nothing in it asks the bridge for anything.
/// </summary>
internal sealed class ReleaseScreen(Settings settings, GitRepository git, Func<string[], Task<string>>? gh = null)
{
    internal const string Action = "Build release files", OpenFolder = "Open the folder in Explorer", Back = "Back";
    internal const string Danger = "#F0A3A3";
    internal const string ReuseQuestion = "Try again with your checkout's DLLs for the dependencies?";

    internal Task Run() => Show(AnsiConsole.Console, KeyScreen.ReadKey, Terminal.Select,
        (question, preset) => AnsiConsole.Confirm(Terminal.Escape(question), preset), Terminal.Pause, Explore);

    private static void Explore(string folder)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
    }

    /// <param name="confirm">Asks a yes or no question with the given default.</param>
    /// <param name="explore">Opens a folder in Explorer.</param>
    internal async Task Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, Func<string, IEnumerable<MenuAction>, string> select,
        Func<string, bool, bool> confirm, Action pause, Action<string> explore)
    {
        var github = gh ?? PullRequests.GitHub(settings);
        var basis = settings.BaseBranch;
        var (mine, all, problem) = await console.Status().StartAsync("Listing merged pull requests with the GitHub CLI...", _ => ReleasePicker.Load(github, basis));
        var picker = new ReleasePicker(mine, all, problem, basis);
        var release = new ReleaseFiles(settings, git, github);

        Task<ReleaseResult> Again(ReleaseResult shown, bool clean, bool reuse) => console.Status().StartAsync(
            $"Fetching PR #{shown.PullRequest.Number} and {basis} again and checking its release files...",
            _ => release.Preview(shown.PullRequest.Number, new(Clean: clean, ReuseDependencies: reuse)));

        async Task Release(int number)
        {
            var preview = await console.Status().StartAsync($"Fetching PR #{number} and {basis} from origin and working out its release files...",
                _ => release.Preview(number));
            ReleaseChoice? next = null;
            while (true)
            {
                var removable = preview.Clean ? preview.Removed : Removable(preview.Folder);
                var choice = next ?? new ReleasePreview(preview, removable).Show(console, readKey);
                next = null;
                if (choice == ReleaseChoice.Back) return;
                if (choice == ReleaseChoice.Clean)
                {
                    // What --clean removes was read from the folder; a fresh preview that differs is shown before anything is built.
                    var clean = await Again(preview, true, preview.ReuseDependencies);
                    var same = Same(clean, preview, removable);
                    preview = clean;
                    if (!same) continue;
                }
                ReleaseResult? built;
                bool escaped;
                try { (built, escaped) = Build(console, readKey, preview, release.Build); }
                catch (Exception exception) when (Failed(exception))
                {
                    console.Write(Failure(exception, "Release build"));
                    if (!preview.ReuseDependencies && FailedSupport(preview, exception) is { } support)
                    {
                        console.Write(ReuseCard(support, preview.BaseBranch));
                        if (confirm(ReuseQuestion, false))
                        {
                            var reuse = await Again(preview, preview.Clean, true);
                            if (Same(reuse, preview, removable)) next = ReleaseChoice.Build;
                            preview = reuse;
                            continue;
                        }
                    }
                    pause();
                    continue;
                }
                if (built is null)
                {
                    console.MarkupLine($"[{TerminalStyle.Gold}]Cancelled.[/] [{TerminalStyle.Muted}]Nothing was written to {Terminal.Escape(preview.Folder)}.[/]");
                    pause();
                    continue;
                }
                console.Clear();
                console.Write(ResultCard(built, escaped));
                while (select("What next?", [
                    new(OpenFolder, "Open the release folder, ready to upload its files to the Code Release Manager."),
                    new(Back, "Return to the merged pull requests.")]) == OpenFolder)
                    explore(built.Folder);
                return;
            }
        }

        while (picker.Show(console, readKey) is { } number)
        {
            try { await Release(number); }
            catch (Exception exception) when (Failed(exception))
            {
                // The engine's refusals already say what to do; anything else gets the usual diagnostics.
                console.Write(exception is InvalidOperationException
                    ? TerminalStyle.Card(new Text(exception.Message), "CANNOT BUILD RELEASE FILES", TerminalStyle.Gold) : Failure(exception, "Release preview"));
                pause();
            }
        }
    }

    private static bool Failed(Exception exception) => TicketWorkspaces.Recoverable(exception) || exception is InvalidDataException or BadImageFormatException;

    /// <summary>A fresh preview can be built without being shown again when it builds the same commits and removes the files already shown.</summary>
    private static bool Same(ReleaseResult fresh, ReleaseResult shown, IReadOnlyList<string> removable) => fresh.CanBuild &&
        fresh.BaseCommit == shown.BaseCommit && fresh.PullRequest.HeadCommit == shown.PullRequest.HeadCommit &&
        fresh.Removed.Order(Files.Comparer).SequenceEqual(fresh.Clean ? removable.Order(Files.Comparer) : [], Files.Comparer);

    /// <summary>The files in the folder that removing earlier files would remove: those an earlier release run wrote and nobody has changed since.</summary>
    internal static List<string> Removable(string folder) => ReleaseFolder.Removable(folder, ReleaseFolder.Load(folder)).Select(file => file.Name).ToList();

    /// <summary>The support project a build failed in, or null when it failed elsewhere.</summary>
    internal static SupportProject? FailedSupport(ReleaseResult preview, Exception exception) =>
        Diagnostics.Describe(exception).Context.Project is { } project
            ? preview.SupportProjects.FirstOrDefault(support => Files.Comparer.Equals(support.Project, project)) : null;

    private static IRenderable Failure(Exception exception, string stage)
    {
        var report = Diagnostics.Record(exception, stage);
        return TerminalStyle.Card(new Text(report.Text), report.Label, Danger);
    }

    internal static IRenderable ReuseCard(SupportProject support, string basis) => TerminalStyle.Card(new Rows(
        new Markup($"[bold {TerminalStyle.Gold}]{Terminal.Escape(support.Assembly)}.dll failed to build from {Terminal.Escape(basis)}.[/] " +
            $"[{TerminalStyle.Text}]It is a dependency, built only so the release DLLs compile.[/]"),
        new Markup($"[{TerminalStyle.Muted}]Cultivate can try again with the DLLs your checkout already has for the dependencies instead of building them. " +
            $"The release DLLs would then be compiled against DLLs from your checkout that Cultivate cannot verify match {Terminal.Escape(basis)}.[/]")),
        "DEPENDENCY FAILED", TerminalStyle.Gold);

    /// <summary>
    /// Runs <paramref name="build"/> behind a screen of its progress messages. Esc asks it to cancel, which it honours before anything is written
    /// to the folder. Returns the result, or null when it was cancelled, and whether Esc was pressed; a failure is thrown.
    /// </summary>
    internal static (ReleaseResult? Result, bool Escaped) Build(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, ReleaseResult preview,
        Func<ReleaseResult, Action<string>, CancellationToken, Task<ReleaseResult>> build)
    {
        var view = new ReleaseBuildView(preview);
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => build(preview, view.Report, cancel.Token));
        var painted = (Messages: -1, Second: -1L);
        // A new message, a new second, or the end of the build repaints through a key the screen ignores. Real keys still arrive at once.
        ConsoleKeyInfo? Read(int wait)
        {
            var until = Environment.TickCount64 + wait;
            while (true)
            {
                if (task.IsCompleted) return Tick;
                if ((view.Messages, view.Second) is var now && now != painted)
                {
                    painted = now;
                    return Tick;
                }
                var left = (int)Math.Max(0, until - Environment.TickCount64);
                if (readKey(Math.Min(left, 100)) is { } key) return key;
                if (left <= 100) return null;
            }
        }
        KeyScreen.Show(console, view.Render, (key, _) =>
        {
            if (key.Key == ConsoleKey.Escape && !cancel.IsCancellationRequested)
            {
                view.Cancelling = true;
                cancel.Cancel();
            }
            return task.IsCompleted;
        }, Read);
        try { return (task.GetAwaiter().GetResult(), cancel.IsCancellationRequested); }
        catch (OperationCanceledException) { return (null, true); }
    }

    private static readonly ConsoleKeyInfo Tick = new('\0', ConsoleKey.F24, false, false, false);

    /// <summary>What was written: the folder as a link, each file with its kind, size and version, and every warning again.</summary>
    internal static IRenderable ResultCard(ReleaseResult result, bool escaped = false)
    {
        static string Muted(string text) => $"[{TerminalStyle.Muted}]{Terminal.Escape(text)}[/]";
        var muted = new Style(Color.FromHex(TerminalStyle.Muted));
        var basis = result.BaseBranch.Length == 0 ? "the base branch" : result.BaseBranch;
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {TerminalStyle.Accent}]Wrote {TicketWorkspaces.Count(result.Files.Count, "file")} for {Terminal.Escape(result.PullRequest.Label)}[/]  " +
                Muted($"in {result.Seconds:0} s")),
            // Spectre drops the link where the terminal cannot show one, so the visible text is the whole path.
            new Paragraph(result.Folder, new Style(Color.FromHex(TerminalStyle.Text), decoration: Decoration.Bold), new Link(new Uri(result.Folder).AbsoluteUri)),
            new Markup(Muted(result.Files.Count == 0 ? "Nothing needed releasing." : "Upload every file in this folder to the Code Release Manager."))
        };
        if (result.Files.Count != 0)
        {
            var table = new Table().Border(TableBorder.None).Expand()
                .AddColumn(new TableColumn(Muted("FILE"))).AddColumn(new TableColumn(Muted("KIND")) { NoWrap = true })
                .AddColumn(new TableColumn(Muted("SIZE")) { NoWrap = true }).AddColumn(new TableColumn(Muted("VERSION")) { NoWrap = true })
                .AddColumn(new TableColumn("") { NoWrap = true });
            foreach (var file in result.Files)
                table.AddRow(new Text(file.Name), new Text(file.Kind, muted), new Text(TidySizes.Format(file.Size)), new Text(file.AssemblyVersion ?? "", muted),
                    file.Replaced ? new Text("replaced", new Style(Color.FromHex(TerminalStyle.Gold))) : new Text("new", muted));
            rows.Add(table);
        }
        if (result.Removed.Count != 0) rows.Add(new Markup($"[{TerminalStyle.Gold}]Removed first:[/] {Terminal.Escape(string.Join(", ", result.Removed))}"));
        if (result.Existing.Count != 0)
            rows.Add(new Markup(Muted("Also in the folder and kept: " + string.Join(", ", result.Existing.Select(file => $"{file.Name} ({file.Origin})")))));
        if (result.OutsideReferences.Count != 0)
        {
            rows.Add(new Markup($"[bold {TerminalStyle.Gold}]Compiled against files from outside {Terminal.Escape(basis)}:[/]"));
            rows.AddRange(result.OutsideReferences.Select(reference => new Markup($"  {Terminal.Escape(reference.Name)} {Terminal.Escape(reference.Version ?? "")}  " +
                Muted($"{reference.Path}, modified {reference.LastWriteTime:yyyy-MM-dd HH:mm}, for {reference.Project}"))));
        }
        rows.AddRange(result.Warnings.Select(warning => new Markup($"[{TerminalStyle.Gold}]![/] {Terminal.Escape(warning)}")));
        if (escaped) rows.Add(new Markup(Muted("Esc came after writing had started, so the build finished.")));
        return TerminalStyle.Card(new Rows(rows), "RELEASE FILES READY", result.Warnings.Count == 0 && result.OutsideReferences.Count == 0 ? TerminalStyle.Accent : TerminalStyle.Gold);
    }
}

/// <summary>A release preview to check before building, in the order that matters for production, scrolled when it is taller than the terminal.</summary>
internal sealed class ReleasePreview(ReleaseResult preview, IReadOnlyList<string> removable)
{
    private (int Width, List<IRenderable> Lines)? body;
    private int last;
    internal int Top { get; private set; }
    internal bool CanClean => preview.CanBuild && !preview.Clean && removable.Count != 0;

    // The header's four lines, the position and keys lines, and the cursor's.
    internal static int BodyHeight(int height) => Math.Max(3, height - 7);

    internal ReleaseChoice? HandleKey(ConsoleKeyInfo key, int height)
    {
        var page = BodyHeight(height);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: Scroll(-1); break;
            case ConsoleKey.DownArrow: Scroll(1); break;
            case ConsoleKey.PageUp: Scroll(-page); break;
            case ConsoleKey.PageDown: Scroll(page); break;
            case ConsoleKey.Home: Top = 0; break;
            case ConsoleKey.End: Top = last; break;
            case ConsoleKey.Enter when preview.CanBuild: return ReleaseChoice.Build;
            case ConsoleKey.C when CanClean && !key.Modifiers.HasFlag(ConsoleModifiers.Control): return ReleaseChoice.Clean;
            case ConsoleKey.Escape: return ReleaseChoice.Back;
        }
        return null;
    }

    private void Scroll(int lines) => Top = Math.Clamp(Top + lines, 0, last);

    internal IRenderable Render(int width, int height)
    {
        if (body?.Width != width) body = (width, Body(width - 1));
        var lines = body.Value.Lines;
        var visible = BodyHeight(height);
        last = Math.Max(0, lines.Count - visible);
        Top = Math.Min(Top, last);
        var rows = new List<IRenderable> { TerminalStyle.Header("RELEASE FILES", "Check what lands in the folder before you build.") };
        rows.AddRange(lines.Skip(Top).Take(visible));
        rows.AddRange(Enumerable.Repeat<IRenderable>(new Text(""), visible - Math.Min(visible, lines.Count - Top)));
        rows.Add(TerminalStyle.Line(TicketWorkspaceView.Fit(lines.Count <= visible ? "" : $"Lines {Top + 1}-{Math.Min(Top + visible, lines.Count)} of {lines.Count}" +
            (Top < last ? "   more below: arrows or PgDn scroll" : "   end: arrows or PgUp scroll"), width - 1), new Style(Color.FromHex(TerminalStyle.Muted))));
        rows.Add(new Markup(preview.CanBuild
            ? $"[{TerminalStyle.Gold}]Enter[/] build   " + (CanClean ? $"[{TerminalStyle.Gold}]C[/] remove earlier files, then build   " : "") + $"[{TerminalStyle.Gold}]Esc[/] back"
            : $"[{TerminalStyle.Gold}]Esc[/] back   [{ReleaseScreen.Danger}]These release files cannot be built.[/]"));
        return new Rows(rows);
    }

    internal ReleaseChoice Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey)
    {
        var choice = ReleaseChoice.Back;
        KeyScreen.Show(console, Render, (key, height) =>
        {
            if (HandleKey(key, height) is not { } chosen) return false;
            choice = chosen;
            return true;
        }, readKey);
        return choice;
    }

    private List<IRenderable> Body(int width)
    {
        const string heading = "bold " + TerminalStyle.Gold, text = TerminalStyle.Text, muted = TerminalStyle.Muted, gold = TerminalStyle.Gold;
        static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
        var page = new WrappedLines(width);
        var request = preview.PullRequest;
        var basis = preview.BaseBranch.Length == 0 ? "the base branch" : preview.BaseBranch;
        page.Add(0, (request.Label, "bold " + TerminalStyle.Accent));
        page.Add(0, (string.Join("  |  ", new[]
        {
            $"#{request.Number}" + (request.Author.Length == 0 ? "" : " by " + request.Author), request.HeadBranch,
            DateTimeOffset.TryParse(request.MergedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var merged)
                ? $"merged {merged.ToLocalTime():yyyy-MM-dd HH:mm}" : "",
            preview.BaseCommit.Length == 0 ? "" : $"built from {basis} at {Short(preview.BaseCommit)}"
        }.Where(part => part.Length != 0)), muted));
        if (!preview.CanBuild) page.Add(0, ("These release files cannot be built. CANNOT BUILD below says why.", "bold " + ReleaseScreen.Danger));

        var planned = preview.Projects.Select(project => project.Assembly + ".dll").Concat(preview.Packages.Select(package => package.Name))
            .Concat(preview.Content.Select(path => Path.GetFileName(path))).ToHashSet(Files.Comparer);
        var packages = preview.Packages.Select(package => package.Name).Distinct(Files.Comparer).Count();
        page.Blank();
        page.Add(0, ("FOLDER  ", heading), (preview.Folder, "bold " + text));
        var count = preview.Projects.Count + packages + preview.Content.Count;
        page.Add(2, (count == 0 ? "Building writes nothing: there is no DLL, package or content file to release." :
            $"Building writes {TicketWorkspaces.Count(count, "file")}: {TicketWorkspaces.Count(preview.Projects.Count, "DLL")}, " +
            $"{TicketWorkspaces.Count(packages, "changed package")} and {TicketWorkspaces.Count(preview.Content.Count, "content file")}.", text));
        if (preview.Existing.Count == 0) page.Add(2, ("Nothing is in the folder yet.", muted));
        else
        {
            page.Add(2, ($"Already in the folder ({preview.Existing.Count}):", text));
            foreach (var file in preview.Existing)
            {
                var notes = new List<string> { TidySizes.Format(file.Size), file.Origin };
                if (planned.Contains(file.Name)) notes.Add("replaced by this build");
                if (removable.Contains(file.Name, Files.Comparer) && (preview.Clean || CanClean)) notes.Add(preview.Clean ? "removed first" : "C removes it first");
                page.Add(4, (file.Name, text), ("  " + string.Join(", ", notes), muted));
            }
            if (preview.Clean)
                page.Add(2, (removable.Count == 0 ? "Nothing is removed first: no file here is one Cultivate wrote that is unchanged." :
                    $"Removed first: {string.Join(", ", removable)}. Every other file is kept.", gold));
            else if (CanClean)
                page.Add(2, ($"C removes the {TicketWorkspaces.Count(removable.Count, "file")} Cultivate wrote here that nobody has changed since, then builds. " +
                    "Every other file is kept.", muted));
            else if (removable.Count == 0)
                page.Add(2, ("Every file here is kept. Cultivate removes only files it wrote that nobody has changed since, and none are here.", muted));
        }

        page.Blank();
        page.Add(0, ($"DLLS TO BUILD ({preview.Projects.Count})", heading));
        if (preview.Projects.Count == 0) page.Add(2, ("None: the pull request changed no compiled code.", muted));
        foreach (var project in preview.Projects)
        {
            page.Add(2, (project.Assembly + ".dll", "bold " + text), ("  " + project.Project, muted));
            var others = project.OtherMergedChanges;
            if (others.Count == 0) page.Add(4, ("Carries no other merged changes.", muted));
            else
            {
                page.Add(4, ($"Also carries {TicketWorkspaces.Count(others.Count, "other merged change")}, which {(others.Count == 1 ? "goes" : "go")} to production with it:", gold));
                foreach (var commit in others) page.Add(6, (Short(commit.Commit), muted), ("  " + commit.Subject, text));
            }
        }

        if (preview.Packages.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"CHANGED PACKAGES ({preview.Packages.Count})", heading));
            foreach (var package in preview.Packages)
                page.Add(2, (package.Name, text), ($"  {(package.Version.Length == 0 ? "version not recorded" : package.Version)}  for {package.Project}", muted));
        }

        page.Blank();
        page.Add(0, ($"CONTENT FILES ({preview.Content.Count})", heading));
        if (preview.Content.Count == 0) page.Add(2, ("None: no markup, script, style or other content file to upload.", muted));
        foreach (var path in preview.Content)
        {
            var change = preview.Changes.FirstOrDefault(item => item.Path == path);
            if (change?.Status != ReleaseStatus.ChangedAgain)
            {
                page.Add(2, (path, text));
                continue;
            }
            page.Add(2, (path, text), ($"  not exactly as on the pull request; {basis}'s version is copied:", gold));
            if (change.Commits.Count == 0) page.Add(6, ("the merge itself changed it (for example, resolving a conflict)", muted));
            foreach (var commit in change.Commits) page.Add(6, (Short(commit.Commit), muted), ("  " + commit.Subject, text));
        }

        var left = preview.NotIncluded.Concat(preview.Changes.Where(change => change.Status == ReleaseStatus.NotOnBase)
            .Select(change => new ReleaseNote(change.Path, $"Undone on {basis} since the merge, so there is nothing to release."))).ToList();
        if (left.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"NOT INCLUDED ({left.Count})", heading), ("  release these by hand if production needs them", muted));
            foreach (var note in left) page.Add(2, (note.Path, text), ("  " + note.Reason, muted));
        }
        if (preview.Deleted.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"DELETED BY THE PULL REQUEST ({preview.Deleted.Count})", heading));
            foreach (var note in preview.Deleted) page.Add(2, (note.Path, text), ("  " + note.Reason, muted));
        }
        if (preview.SupportProjects.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"BUILT FIRST, NOT RELEASED ({preview.SupportProjects.Count})", heading));
            page.Add(2, preview.ReuseDependencies
                ? ($"Taken from your checkout instead of built from {basis}; Cultivate cannot verify they match {basis}. They are not copied to the folder.", gold)
                : ($"Built from {basis} so the DLLs above compile. They are not copied to the folder.", muted));
            foreach (var support in preview.SupportProjects) page.Add(4, (support.Assembly + ".dll", text), ("  " + support.Reason, muted));
        }
        if (preview.Warnings.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"WARNINGS ({preview.Warnings.Count})", heading));
            foreach (var warning in preview.Warnings) page.Add(2, ("! ", "bold " + gold), (warning, text));
        }
        if (preview.Errors.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"CANNOT BUILD ({preview.Errors.Count})", "bold " + ReleaseScreen.Danger));
            foreach (var error in preview.Errors) page.Add(2, (error, ReleaseScreen.Danger));
        }
        return page.Lines;
    }
}

/// <summary>The build's progress messages as they arrive, how long it has taken, and what Esc does.</summary>
internal sealed class ReleaseBuildView(ReleaseResult preview)
{
    private readonly List<string> messages = [];
    private readonly Stopwatch watch = Stopwatch.StartNew();
    internal bool Cancelling { get; set; }
    internal long Second => (long)watch.Elapsed.TotalSeconds;

    internal int Messages
    {
        get { lock (messages) return messages.Count; }
    }

    internal void Report(string message)
    {
        lock (messages) messages.Add(message);
    }

    internal IRenderable Render(int width, int height)
    {
        List<string> shown;
        lock (messages) shown = [.. messages];
        var footer = new WrappedLines(width - 1);
        if (Cancelling) footer.Add(0, ("Cancelling. The build stops at its next step, before anything is written to the folder.", TerminalStyle.Gold));
        else
        {
            footer.Add(0, ("Esc", TerminalStyle.Gold), (" cancel", TerminalStyle.Text));
            footer.Add(0, ("Cancelling stops before anything is written to the folder. Once writing starts, it finishes.", TerminalStyle.Muted));
        }
        // The header, a blank line, the pull request, folder and clock, a blank line before the footer, and the cursor's line.
        var room = Math.Max(1, height - 4 - 1 - 3 - 1 - footer.Lines.Count - 1);
        var text = new Style(Color.FromHex(TerminalStyle.Text));
        var rows = new List<IRenderable>
        {
            TerminalStyle.Header("BUILDING RELEASE FILES", $"Built from {preview.BaseBranch} in a throwaway snapshot."),
            new Text(""),
            TerminalStyle.Line(TicketWorkspaceView.Fit(preview.PullRequest.Label, width - 1), new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
            TerminalStyle.Line(TicketWorkspaceView.Fit("Folder  " + preview.Folder, width - 1), text),
            TerminalStyle.Line($"Elapsed {watch.Elapsed:m\\:ss}", new Style(Color.FromHex(TerminalStyle.Muted)))
        };
        var recent = shown.Skip(Math.Max(0, shown.Count - room)).ToList();
        for (var index = 0; index < room; index++)
            rows.Add(index >= recent.Count ? new Text("") : TerminalStyle.Line(TicketWorkspaceView.Fit("  " + recent[index], width - 1), index == recent.Count - 1
                ? new Style(Color.FromHex(TerminalStyle.Accent)) : new Style(Color.FromHex(TerminalStyle.Muted))));
        rows.Add(new Text(""));
        rows.AddRange(footer.Lines);
        return new Rows(rows);
    }
}

/// <summary>Coloured text wrapped at word boundaries to a width, with continuation lines indented, so nothing is cut off on a narrow terminal.</summary>
internal sealed class WrappedLines(int width)
{
    internal List<IRenderable> Lines { get; } = [];

    internal void Blank() => Lines.Add(new Text(""));

    /// <summary>Adds the pieces as one paragraph starting at <paramref name="indent"/>; its later lines start two columns further in.</summary>
    internal void Add(int indent, params (string Text, string Style)[] parts)
    {
        var line = new StringBuilder().Append(' ', indent);
        var (used, start, gap) = (indent, indent, "");
        string? open = null;
        // Words of one colour share a single markup run.
        void Put(string piece, string style)
        {
            if (open != style) line.Append(open is null ? "" : "[/]").Append('[').Append(style).Append(']');
            open = style;
            line.Append(Markup.Escape(piece));
        }
        void Flush()
        {
            Lines.Add(new Markup(line.Append(open is null ? "" : "[/]").ToString()));
            line.Clear().Append(' ', indent + 2);
            (used, start, gap, open) = (indent + 2, indent + 2, "", null);
        }
        foreach (var (text, style) in parts)
            foreach (var token in Regex.Split(text.ReplaceLineEndings(" "), "( +)"))
            {
                if (token.Length == 0) continue;
                if (token[0] == ' ')
                {
                    if (used > start) gap = token;
                    continue;
                }
                var word = token;
                while (word.Length != 0)
                {
                    if (word.Length <= width - used - gap.Length)
                    {
                        Put(gap + word, style);
                        (used, gap, word) = (used + gap.Length + word.Length, "", "");
                    }
                    else if (used > start) Flush();
                    else
                    {
                        var piece = word[..Math.Clamp(width - used, 1, word.Length)];
                        Put(piece, style);
                        word = word[piece.Length..];
                        Flush();
                    }
                }
            }
        if (used > start) Flush();
    }
}
