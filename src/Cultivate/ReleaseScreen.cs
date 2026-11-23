using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum ReleaseChoice { Build, Clean, Back }

/// <summary>
/// The home screen's Build release files action: choose a merged pull request in MyMeyer or, with Ctrl+R, another cloned repository, check what
/// its preview and its linked pull requests' would write, build them, and open the folder. It never acts on the dev webfarm, so it needs no
/// deployment status and nothing in it asks the bridge for anything.
/// </summary>
internal sealed class ReleaseScreen(Settings settings, GitRepository git, Func<string[], Task<string>>? gh = null)
{
    internal const string Action = "Build release files", OpenFolder = "Open the folder in Explorer", Back = "Back";
    internal const string Danger = "#F0A3A3";
    internal const string ReuseQuestion = "Try again with your checkout's DLLs for the dependencies?";
    internal const string RepositoryQuestion = "Which repository's merged pull requests?";

    // Only the repository question is as long as the machine's clones, so only it filters as you type.
    internal Task Run() => Show(AnsiConsole.Console, KeyScreen.ReadKey, (title, actions) => Terminal.Select(title, actions, search: title == RepositoryQuestion),
        (question, preset) => Terminal.Confirm(Terminal.Escape(question), preset), Terminal.Pause, Explore);

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
        var clones = ReleaseRepositories.Clones(settings);
        var runner = new ReleaseRunner(settings, git, github);
        var repository = ReleaseRepositories.MainCheckout(settings);

        // Listing another repository's pull requests first asks its origin for its release branch.
        async Task<(ReleaseRepository, ReleasePicker)> List(ReleaseRepository chosen) => await console.Status().StartAsync(chosen.Main
            ? "Listing merged pull requests with the GitHub CLI..." : $"Listing {chosen.Name}'s merged pull requests with the GitHub CLI...", async _ =>
        {
            chosen = await ReleaseRepositories.WithBranch(git, chosen);
            var (mine, all, problem) = await ReleasePicker.Load(github, chosen.Branch, chosen.Main ? null : chosen.GitHub);
            return (chosen, new ReleasePicker(mine, all, problem, chosen.Branch, repository: clones.Count > 1 ? chosen.Name : null, canChange: clones.Count > 1));
        });

        Task<ReleaseRun> Again(ReleaseRun shown, ReleaseRepository requested, bool clean, bool reuse) => console.Status().StartAsync(
            $"Fetching PR #{shown.Releases[0].PullRequest.Number} and {shown.Releases[0].BaseBranch} again and checking its release files...",
            _ => runner.Preview(requested, shown.Releases[0].PullRequest.Number,
                new(Clean: clean, ReuseDependencies: reuse, NoPull: shown.Releases[0].NoPull, NoLinked: shown.NoLinked)));

        async Task Release(ReleaseRepository requested, int number)
        {
            var preview = await console.Status().StartAsync(requested.Main
                ? $"Fetching PR #{number} and {requested.Branch} from origin and working out its release files..."
                : $"Fetching {requested.Name} PR #{number} from origin and working out its release files...", _ => runner.Preview(requested, number));
            ReleaseChoice? next = null;
            while (true)
            {
                var removable = Removables(preview);
                var choice = next ?? new ReleasePreview(preview, removable).Show(console, readKey);
                next = null;
                if (choice == ReleaseChoice.Back) return;
                var first = preview.Releases[0];
                if (choice == ReleaseChoice.Clean)
                {
                    // What --clean removes was read from the folder; a fresh preview that differs is shown before anything is built.
                    var clean = await Again(preview, requested, true, first.ReuseDependencies);
                    var same = Same(clean, preview, removable);
                    preview = clean;
                    if (!same) continue;
                }
                ReleaseRun? built;
                bool escaped;
                // With one repository a failure is thrown; with several, each one's failure is in the result.
                try { (built, escaped) = Build(console, readKey, preview, runner.Build); }
                catch (Exception exception) when (Failed(exception))
                {
                    console.Write(Failure(exception, "Release build"));
                    if (!first.ReuseDependencies && FailedSupport(first, exception) is { } support)
                    {
                        console.Write(ReuseCard(support, first.BaseBranch));
                        if (confirm(ReuseQuestion, false))
                        {
                            var reuse = await Again(preview, requested, first.Clean, true);
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
                    new(Back, "Return to the merged pull requests.", Back: true)]) == OpenFolder)
                    explore(built.Releases.Count == 1 ? built.Releases[0].Folder : built.Folder);
                return;
            }
        }

        ReleasePicker? picker = null;
        while (true)
        {
            if (picker is null)
            {
                try { (repository, picker) = await List(repository); }
                catch (Exception exception) when (Failed(exception) && !repository.Main)
                {
                    console.Write(TerminalStyle.Card(new Text(exception.Message), "CANNOT LIST PULL REQUESTS", TerminalStyle.Gold));
                    pause();
                    repository = ReleaseRepositories.MainCheckout(settings);
                    continue;
                }
            }
            if (picker.Show(console, readKey) is not { } number)
            {
                if (!picker.ChangeRepository) return;
                var labels = clones.ToDictionary(clone => clones.Count(other => other.Name.Equals(clone.Name, StringComparison.OrdinalIgnoreCase)) == 1 ? clone.Name : clone.Root,
                    Files.Comparer);
                var label = select(RepositoryQuestion, labels.Select(pair => new MenuAction(pair.Key,
                    (pair.Value.Main ? "Your MyMeyer checkout. " : "") + (pair.Value.GitHub ?? "Origin is not on GitHub") + "  |  " + pair.Value.Root)));
                if (labels.TryGetValue(label, out var chosen) && !Files.Comparer.Equals(chosen.Root, repository.Root)) (repository, picker) = (chosen, null);
                continue;
            }
            try
            {
                var requested = repository;
                if (picker.Chosen?.Repository is { Length: > 0 } linked)
                    requested = ReleaseRepositories.Find(settings, linked) ?? throw ReleaseRepositories.NotCloned(settings, linked);
                await Release(await ReleaseRepositories.WithBranch(git, requested), number);
            }
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

    /// <summary>
    /// A fresh preview can be built without being shown again when it has the same repositories building the same commits and removes the files
    /// already shown.
    /// </summary>
    private static bool Same(ReleaseRun fresh, ReleaseRun shown, IReadOnlyList<IReadOnlyList<string>> removable) => fresh.CanBuild &&
        fresh.Releases.Count == shown.Releases.Count && fresh.Skipped.Count == shown.Skipped.Count &&
        fresh.Releases.Zip(shown.Releases, removable).All(release => Same(release.First, release.Second, release.Third));

    private static bool Same(ReleaseResult fresh, ReleaseResult shown, IReadOnlyList<string> removable) => fresh.CanBuild == shown.CanBuild &&
        Files.Comparer.Equals(fresh.Folder, shown.Folder) && fresh.BaseCommit == shown.BaseCommit && fresh.PullRequest.HeadCommit == shown.PullRequest.HeadCommit &&
        fresh.Removed.Order(Files.Comparer).SequenceEqual(fresh.Clean ? removable.Order(Files.Comparer) : [], Files.Comparer);

    /// <summary>The files in the folder that removing earlier files would remove: those an earlier release run wrote and nobody has changed since.</summary>
    internal static List<string> Removable(string folder) => ReleaseFolder.Removable(folder, ReleaseFolder.Load(folder)).Select(file => file.Name).ToList();

    /// <summary>Each release's files that removing earlier files removes, or a --clean preview says it removes.</summary>
    internal static List<IReadOnlyList<string>> Removables(ReleaseRun run) =>
        run.Releases.Select(release => (IReadOnlyList<string>)(release.Clean ? release.Removed : Removable(release.Folder))).ToList();

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
        Func<ReleaseResult, Action<string>, CancellationToken, Task<ReleaseResult>> build) =>
        Build(console, readKey, new ReleaseBuildView(preview), preview, build);

    /// <inheritdoc cref="Build(IAnsiConsole, Func{int, ConsoleKeyInfo?}, ReleaseResult, Func{ReleaseResult, Action{string}, CancellationToken, Task{ReleaseResult}})"/>
    internal static (ReleaseRun? Result, bool Escaped) Build(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, ReleaseRun preview,
        Func<ReleaseRun, Action<string>, CancellationToken, Task<ReleaseRun>> build) => Build(console, readKey, new ReleaseBuildView(preview), preview, build);

    private static (T? Result, bool Escaped) Build<T>(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, ReleaseBuildView view, T preview,
        Func<T, Action<string>, CancellationToken, Task<T>> build) where T : class
    {
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

    private static string Muted(string text) => $"[{TerminalStyle.Muted}]{Terminal.Escape(text)}[/]";

    // Spectre drops the link where the terminal cannot show one, so the visible text is the whole path.
    private static Paragraph FolderLink(string folder) =>
        new(folder, new Style(Color.FromHex(TerminalStyle.Text), decoration: Decoration.Bold), new Link(new Uri(folder).AbsoluteUri));

    /// <summary>What was written: the folder as a link, each file with its kind, size and version, and every warning again.</summary>
    internal static IRenderable ResultCard(ReleaseResult result, bool escaped = false) => ResultCard(ReleaseRun.Single(result), escaped);

    /// <summary>
    /// What a run wrote. One repository's is shown as it always was; with several, the ticket folder comes first, then each repository's
    /// subfolder with its files or why it was not written, then the linked pull requests that were skipped.
    /// </summary>
    internal static IRenderable ResultCard(ReleaseRun run, bool escaped = false)
    {
        var rows = new List<IRenderable>();
        var failed = run.Releases.Any(release => !release.Written);
        if (run.Releases.Count == 1)
        {
            var result = run.Releases[0];
            rows.Add(new Markup($"[bold {TerminalStyle.Accent}]Wrote {TicketWorkspaces.Count(result.Files.Count, "file")} for {Terminal.Escape(result.PullRequest.Label)}[/]  " +
                Muted($"in {result.Seconds:0} s")));
            rows.AddRange(Details(result));
        }
        else
        {
            var first = run.Releases[0];
            rows.Add(new Markup($"[bold {TerminalStyle.Accent}]Wrote {TicketWorkspaces.Count(run.Releases.Sum(release => release.Files.Count), "file")} for " +
                $"{Terminal.Escape(first.PullRequest.Label)}[/]  " + Muted($"and {TicketWorkspaces.Count(run.Releases.Count - 1, "linked pull request")}, in {run.Seconds:0} s")));
            rows.Add(FolderLink(run.Folder));
            rows.Add(new Markup(Muted("One subfolder per repository: " + string.Join(", ", run.Releases.Select(release =>
                $"{release.Subfolder} ({(release.Written ? TicketWorkspaces.Count(release.Files.Count, "file") : "not written")})")) + ".")));
            foreach (var release in run.Releases)
            {
                rows.Add(new Text(""));
                rows.Add(new Markup($"[bold {(release.Written ? TerminalStyle.Accent : Danger)}]{Terminal.Escape(release.Subfolder)}[/]  " +
                    Muted($"{release.Repository} {release.PullRequest.Label}")));
                if (release.Written) rows.AddRange(Details(release));
                else
                {
                    rows.Add(new Text(release.Failure ?? "Not written.", new Style(Color.FromHex(Danger))));
                    if (release.Pull is { } pull) rows.Add(new Markup(Muted(pull.Detail)));
                }
            }
        }
        if (run.Skipped.Count != 0)
        {
            rows.Add(new Markup($"[bold {TerminalStyle.Gold}]Linked pull requests not built ({run.Skipped.Count}):[/]"));
            rows.AddRange(run.Skipped.Select(skipped => new Markup($"  {Terminal.Escape($"{skipped.Repository} #{skipped.Number}")}  {Muted(skipped.Reason)}")));
        }
        if (escaped) rows.Add(new Markup(Muted("Esc came after writing had started, so the build finished.")));
        var clean = run.Releases.All(release => release.Warnings.Count == 0 && release.OutsideReferences.Count == 0) && run.Skipped.Count == 0;
        return TerminalStyle.Card(new Rows(rows), failed ? "RELEASE FILES PARTLY WRITTEN" : "RELEASE FILES READY",
            failed ? Danger : clean ? TerminalStyle.Accent : TerminalStyle.Gold);
    }

    /// <summary>One written release: its folder as a link, what to do with it, the pull, its files, and every warning again.</summary>
    private static List<IRenderable> Details(ReleaseResult result)
    {
        var muted = new Style(Color.FromHex(TerminalStyle.Muted));
        var basis = result.BaseBranch.Length == 0 ? "the base branch" : result.BaseBranch;
        var rows = new List<IRenderable>
        {
            FolderLink(result.Folder),
            new Markup(Muted(result.Files.Count == 0 ? "Nothing needed releasing." : result.Kind == ReleaseRepositoryKind.Sql
                ? "Release these scripts through the database process; Cultivate never runs SQL." : "Upload every file in this folder to the Code Release Manager."))
        };
        if (result.Pull is { } pull)
            rows.Add(new Markup(pull.Outcome == ReleasePullOutcome.Pulled ? $"[{TerminalStyle.Accent}]{Terminal.Escape(pull.Detail)}[/]" : Muted(pull.Detail)));
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
        return rows;
    }
}

/// <summary>
/// A release preview to check before building: under each pull request, the DLLs, changed packages and frontend files it releases, then what
/// is left out, any files already in the folder, and warnings and errors, scrolled when it is taller than the terminal. With several
/// repositories it starts with the linked pull requests skipped, then each repository's preview under its subfolder's name.
/// </summary>
internal sealed class ReleasePreview(ReleaseRun run, IReadOnlyList<IReadOnlyList<string>> removables)
{
    private (int Width, List<IRenderable> Lines)? body;
    private int last;
    internal int Top { get; private set; }
    internal bool CanClean => run.CanBuild && !run.Releases[0].Clean && removables.Any(removable => removable.Count != 0);

    internal ReleasePreview(ReleaseResult preview, IReadOnlyList<string> removable) : this(ReleaseRun.Single(preview), [removable]) { }

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
            case ConsoleKey.Enter when run.CanBuild: return ReleaseChoice.Build;
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
        rows.Add(new Markup(run.CanBuild
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

    private const string Heading = "bold " + TerminalStyle.Gold;

    private List<IRenderable> Body(int width)
    {
        var page = new WrappedLines(width);
        if (run.Releases.Count == 1 && run.Skipped.Count == 0)
        {
            Release(page, run.Releases[0], removables[0], false);
            return page.Lines;
        }
        if (run.Skipped.Count != 0)
        {
            page.Add(0, ($"LINKED PULL REQUESTS NOT BUILT ({run.Skipped.Count})", Heading));
            foreach (var skipped in run.Skipped) page.Add(2, ($"{skipped.Repository} #{skipped.Number}", TerminalStyle.Text), ("  " + skipped.Reason, TerminalStyle.Muted));
        }
        foreach (var (release, removable) in run.Releases.Zip(removables))
        {
            if (page.Lines.Count != 0) page.Blank();
            page.Add(0, ("━━ " + release.Subfolder.ToUpperInvariant(), "bold " + TerminalStyle.Accent));
            Release(page, release, removable, true);
        }
        return page.Lines;
    }

    /// <summary>
    /// One repository's preview: its pull request, the files it releases, what is left out, the files already in its folder, and its warnings
    /// and errors.
    /// </summary>
    private void Release(WrappedLines page, ReleaseResult preview, IReadOnlyList<string> removable, bool several)
    {
        const string heading = Heading, text = TerminalStyle.Text, muted = TerminalStyle.Muted, gold = TerminalStyle.Gold;
        static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
        var canClean = run.CanBuild && !preview.Clean && removable.Count != 0;
        var sql = preview.Kind == ReleaseRepositoryKind.Sql;
        var basis = preview.BaseBranch.Length == 0 ? "the base branch" : preview.BaseBranch;
        page.Add(0, (preview.PullRequest.Label, "bold " + TerminalStyle.Accent));
        if (preview.Pull is { Outcome: ReleasePullOutcome.LocalCommits or ReleasePullOutcome.NotUpdated } pull) page.Add(0, (pull.Detail, gold));
        if (!preview.CanBuild)
            page.Add(0, (several ? "Cannot be built, so building skips it. CANNOT BUILD below says why." : "These release files cannot be built. CANNOT BUILD below says why.",
                "bold " + ReleaseScreen.Danger));
        if (preview.ReuseDependencies && preview.SupportProjects.Count != 0)
            page.Add(0, ($"Dependencies are taken from your checkout instead of built from {basis}; Cultivate cannot verify they match {basis}.", gold));

        if (preview.Projects.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"DLLS TO BUILD ({preview.Projects.Count})", heading));
        }
        foreach (var project in preview.Projects)
        {
            page.Add(2, (project.Assembly + ".dll", "bold " + text));
            var others = project.OtherMergedChanges;
            if (others.Count == 0) continue;
            page.Add(4, ($"Also carries {TicketWorkspaces.Count(others.Count, "other merged change")}, which {(others.Count == 1 ? "goes" : "go")} to production with it:", gold));
            foreach (var commit in others) page.Add(6, (Short(commit.Commit), muted), ("  " + commit.Subject, text));
        }

        if (preview.Packages.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"CHANGED PACKAGES ({preview.Packages.Count})", heading));
            foreach (var package in preview.Packages)
                page.Add(2, (package.Name, text), ($"  {(package.Version.Length == 0 ? "version not recorded" : package.Version)}  for {package.Project}", muted));
        }

        if (preview.Content.Count != 0)
        {
            page.Blank();
            page.Add(0, ($"{(sql ? "SQL SCRIPTS" : "FRONTEND FILES")} ({preview.Content.Count})", heading));
        }
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
        if (preview.Projects.Count + preview.Packages.Count + preview.Content.Count == 0)
        {
            page.Blank();
            page.Add(0, (sql ? "Nothing to release: the pull request changed no SQL script." : "Nothing to release: the pull request changed no DLL, package or frontend file.",
                muted));
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
        if (preview.Existing.Count != 0)
        {
            var planned = preview.Projects.Select(project => project.Assembly + ".dll").Concat(preview.Packages.Select(package => package.Name))
                .Concat(preview.Content.Select(path => Path.GetFileName(path))).ToHashSet(Files.Comparer);
            page.Blank();
            page.Add(0, ($"ALREADY IN THE FOLDER ({preview.Existing.Count})", heading), ("  " + preview.Folder, muted));
            foreach (var file in preview.Existing)
            {
                var notes = new List<string> { TidySizes.Format(file.Size), file.Origin };
                if (planned.Contains(file.Name)) notes.Add("replaced by this build");
                if (removable.Contains(file.Name, Files.Comparer) && (preview.Clean || canClean)) notes.Add(preview.Clean ? "removed first" : "C removes it first");
                page.Add(2, (file.Name, text), ("  " + string.Join(", ", notes), muted));
            }
            if (preview.Clean)
                page.Add(2, (removable.Count == 0 ? "Nothing is removed first: no file here is one Cultivate wrote that is unchanged." :
                    $"Removed first: {string.Join(", ", removable)}. Every other file is kept.", gold));
            else if (canClean)
                page.Add(2, ($"C removes the {TicketWorkspaces.Count(removable.Count, "file")} Cultivate wrote here that nobody has changed since, then builds. " +
                    "Every other file is kept.", muted));
            else if (removable.Count == 0)
                page.Add(2, ("Every file here is kept. Cultivate removes only files it wrote that nobody has changed since, and none are here.", muted));
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
    }
}

/// <summary>The build's progress messages as they arrive, how long it has taken, and what Esc does.</summary>
internal sealed class ReleaseBuildView(ReleaseRun run)
{
    internal ReleaseBuildView(ReleaseResult preview) : this(ReleaseRun.Single(preview)) { }

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
        var preview = run.Releases[0];
        var single = run.Releases.Count == 1;
        var rows = new List<IRenderable>
        {
            TerminalStyle.Header("BUILDING RELEASE FILES", single ? $"Built from {preview.BaseBranch} in a throwaway snapshot."
                : $"{run.Releases.Count} repositories, each built from its release branch in a throwaway snapshot, one after another."),
            new Text(""),
            TerminalStyle.Line(TicketWorkspaceView.Fit(preview.PullRequest.Label + (single ? "" : $" and {TicketWorkspaces.Count(run.Releases.Count - 1, "linked pull request")}"),
                width - 1), new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
            TerminalStyle.Line(TicketWorkspaceView.Fit("Folder  " + (single ? preview.Folder : run.Folder), width - 1), text),
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
