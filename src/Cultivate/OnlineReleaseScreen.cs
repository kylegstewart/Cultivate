using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal enum OnlineChoice { Build, Back }

/// <summary>
/// The home screen's Build MeyerOnline release action: check what building would do to the MeyerOnline checkout, build the package with live
/// progress, and open its folder. It never acts on the dev webfarm, so it needs no deployment status and asks the bridge for nothing.
/// </summary>
internal sealed class OnlineReleaseScreen(Settings settings, GitRepository git, OnlineRelease.Runner? run = null, Func<string, string?>? find = null)
{
    internal const string Action = "Build MeyerOnline release";
    internal const string LocalChangesQuestion = "Build with your local changes anyway? The package will not match origin's master exactly.";

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
        var release = new OnlineRelease(settings, git, run, find);
        async Task<OnlineReleaseResult?> Preview(bool allow)
        {
            try
            {
                return await console.Status().StartAsync($"Fetching {OnlineRelease.Branch} from origin and checking the MeyerOnline checkout...",
                    _ => release.Preview(allow));
            }
            catch (Exception exception) when (Failed(exception))
            {
                // The engine's refusals already say what to do; anything else gets the usual diagnostics.
                console.Write(exception is InvalidOperationException
                    ? TerminalStyle.Card(new Text(exception.Message), "CANNOT BUILD MEYERONLINE RELEASE", TerminalStyle.Gold) : Failure(exception, "MeyerOnline preview"));
                pause();
                return null;
            }
        }

        if (await Preview(false) is not { } preview) return;
        while (true)
        {
            if (new OnlinePreview(preview).Show(console, readKey) == OnlineChoice.Back) return;
            if (!preview.CanBuild)
            {
                // Only modified tracked files leave Enter available; building with them needs an explicit yes.
                if (!confirm(LocalChangesQuestion, false)) continue;
                if (await Preview(true) is not { } allowed) return;
                preview = allowed;
                if (!preview.CanBuild) continue;
            }
            OnlineReleaseResult? built;
            bool escaped;
            try { (built, escaped) = Build(console, readKey, preview, release.Build); }
            catch (Exception exception) when (Failed(exception))
            {
                console.Write(Failure(exception, "MeyerOnline release"));
                pause();
                return;
            }
            if (built is null)
            {
                console.MarkupLine($"[{TerminalStyle.Gold}]Cancelled.[/] [{TerminalStyle.Muted}]{Terminal.Escape(preview.Zip)} was not changed.[/]");
                pause();
                return;
            }
            console.Clear();
            console.Write(ResultCard(built, escaped));
            while (select("What next?", [
                new(ReleaseScreen.OpenFolder, "Open the folder that holds MORelease.zip."),
                new(ReleaseScreen.Back, "Return home.")]) == ReleaseScreen.OpenFolder)
                explore(Path.GetDirectoryName(built.Zip)!);
            return;
        }
    }

    private static bool Failed(Exception exception) => TicketWorkspaces.Recoverable(exception) || exception is InvalidDataException;

    private static IRenderable Failure(Exception exception, string stage)
    {
        var report = Diagnostics.Record(exception, stage);
        return TerminalStyle.Card(new Text(report.Text), report.Label, ReleaseScreen.Danger);
    }

    /// <summary>
    /// Runs <paramref name="build"/> behind a screen of its steps and progress messages. Esc asks it to cancel, which kills the builds; the zip is
    /// replaced only at the very end. Returns the result, or null when it was cancelled, and whether Esc was pressed; a failure is thrown.
    /// </summary>
    internal static (OnlineReleaseResult? Result, bool Escaped) Build(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey, OnlineReleaseResult preview,
        Func<OnlineReleaseResult, Action<string>, CancellationToken, Action<string, bool>?, Task<OnlineReleaseResult>> build)
    {
        var view = new OnlineBuildView(preview);
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => build(preview, view.Report, cancel.Token, view.Step));
        var painted = (Changes: -1, Second: -1L);
        // A new message or step, a new second, or the end of the build repaints through a key the screen ignores. Real keys still arrive at once.
        ConsoleKeyInfo? Read(int wait)
        {
            var until = Environment.TickCount64 + wait;
            while (true)
            {
                if (task.IsCompleted) return Tick;
                if ((view.Changes, view.Second) is var now && now != painted)
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

    /// <summary>What was built: the folder as a link, the zip with its size, files and hash, the commit, what happened to the checkout, and each step's time.</summary>
    internal static IRenderable ResultCard(OnlineReleaseResult result, bool escaped = false)
    {
        static string Muted(string text) => $"[{TerminalStyle.Muted}]{Terminal.Escape(text)}[/]";
        var folder = Path.GetDirectoryName(result.Zip)!;
        var branch = result.Branch;
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {TerminalStyle.Accent}]Wrote {Terminal.Escape(Path.GetFileName(result.Zip))} from {branch} at {Terminal.Escape(Short(result.Commit))}[/]  " +
                Muted($"in {TimeSpan.FromSeconds(result.Seconds):m\\:ss}")),
            new Markup(Muted(result.Subject)),
            // Spectre drops the link where the terminal cannot show one, so the visible text is the whole path.
            new Paragraph(folder, new Style(Color.FromHex(TerminalStyle.Text), decoration: Decoration.Bold), new Link(new Uri(folder).AbsoluteUri)),
            new Markup($"{Terminal.Escape(Path.GetFileName(result.Zip))}  " +
                Muted($"{TidySizes.Format(result.Size)}, {TicketWorkspaces.Count(result.Entries, "file")}, SHA-256 {result.Sha256}")),
            new Markup(result.Switched ? $"[{TerminalStyle.Gold}]Switched {Terminal.Escape(result.Repository)} from {Terminal.Escape(result.SwitchedFrom ?? "")} to {branch}; it stays on {branch}.[/]"
                : Muted($"{result.Repository} was already on {branch}.")),
            new Markup(result.Pulled == 0 ? Muted($"{branch} was already up to date with origin.")
                : $"[{TerminalStyle.Accent}]Pulled {TicketWorkspaces.Count(result.Pulled, "new commit")} into {branch}.[/]"),
            new Markup(Muted(result.Installed ? $"Ran npm install first: {result.InstallReason}." : "npm install was not needed.")),
            new Markup(Muted($"version.json: {result.Version}"))
        };
        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn(Muted("STEP"))).AddColumn(new TableColumn(Muted("TIME")) { NoWrap = true });
        foreach (var step in result.Steps) table.AddRow(new Text(step.Step), new Text(Clock(step.Seconds), new Style(Color.FromHex(TerminalStyle.Muted))));
        rows.Add(table);
        rows.AddRange(result.Warnings.Select(warning => new Markup($"[{TerminalStyle.Gold}]![/] {Terminal.Escape(warning)}")));
        if (escaped) rows.Add(new Markup(Muted("Esc came after the zip was written, so the build finished.")));
        return TerminalStyle.Card(new Rows(rows), "MEYERONLINE RELEASE READY", result.Warnings.Count == 0 ? TerminalStyle.Accent : TerminalStyle.Gold);
    }

    internal static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
    internal static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Round(seconds)).ToString(@"m\:ss");
}

/// <summary>A MeyerOnline release preview to check before building, scrolled when it is taller than the terminal.</summary>
internal sealed class OnlinePreview(OnlineReleaseResult preview)
{
    private (int Width, List<IRenderable> Lines)? body;
    private int last;
    internal int Top { get; private set; }
    /// <summary>Modified tracked files are the only reason it cannot build, so Enter offers to build with them after asking.</summary>
    internal bool OnlyLocalChanges => !preview.CanBuild && !preview.AllowLocalChanges && preview.ModifiedFiles.Count != 0 && preview.Errors.Count == 1;

    internal OnlineChoice? HandleKey(ConsoleKeyInfo key, int height)
    {
        var page = ReleasePreview.BodyHeight(height);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: Scroll(-1); break;
            case ConsoleKey.DownArrow: Scroll(1); break;
            case ConsoleKey.PageUp: Scroll(-page); break;
            case ConsoleKey.PageDown: Scroll(page); break;
            case ConsoleKey.Home: Top = 0; break;
            case ConsoleKey.End: Top = last; break;
            case ConsoleKey.Enter when preview.CanBuild || OnlyLocalChanges: return OnlineChoice.Build;
            case ConsoleKey.Escape: return OnlineChoice.Back;
        }
        return null;
    }

    private void Scroll(int lines) => Top = Math.Clamp(Top + lines, 0, last);

    internal IRenderable Render(int width, int height)
    {
        if (body?.Width != width) body = (width, Body(width - 1));
        var lines = body.Value.Lines;
        var visible = ReleasePreview.BodyHeight(height);
        last = Math.Max(0, lines.Count - visible);
        Top = Math.Min(Top, last);
        var rows = new List<IRenderable> { TerminalStyle.Header("MEYERONLINE RELEASE", "Check the checkout and the package before you build.") };
        rows.AddRange(lines.Skip(Top).Take(visible));
        rows.AddRange(Enumerable.Repeat<IRenderable>(new Text(""), visible - Math.Min(visible, lines.Count - Top)));
        rows.Add(TerminalStyle.Line(TicketWorkspaceView.Fit(lines.Count <= visible ? "" : $"Lines {Top + 1}-{Math.Min(Top + visible, lines.Count)} of {lines.Count}" +
            (Top < last ? "   more below: arrows or PgDn scroll" : "   end: arrows or PgUp scroll"), width - 1), new Style(Color.FromHex(TerminalStyle.Muted))));
        rows.Add(new Markup(preview.CanBuild ? $"[{TerminalStyle.Gold}]Enter[/] build   [{TerminalStyle.Gold}]Esc[/] back"
            : OnlyLocalChanges ? $"[{TerminalStyle.Gold}]Enter[/] build with your local changes (asks first)   [{TerminalStyle.Gold}]Esc[/] back"
            : $"[{TerminalStyle.Gold}]Esc[/] back   [{ReleaseScreen.Danger}]This release cannot be built.[/]"));
        return new Rows(rows);
    }

    internal OnlineChoice Show(IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey)
    {
        var choice = OnlineChoice.Back;
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
        var page = new WrappedLines(width);
        var branch = preview.Branch;
        var master = preview.Master;
        page.Add(0, (preview.Repository, "bold " + TerminalStyle.Accent));
        page.Add(0, (string.Join("  |  ", new[]
        {
            "on " + preview.CurrentBranch, master is null ? "" : master.Local.Length == 0 ? $"no local {branch}" : $"{branch} at {OnlineReleaseScreen.Short(master.Local)}",
            master is null ? "" : $"origin/{branch} at {OnlineReleaseScreen.Short(master.Origin)}"
        }.Where(part => part.Length != 0)), muted));
        if (master is not null) page.Add(0, (master.Detail, master.Ahead != 0 ? gold : muted));
        if (preview.CurrentBranch != branch) page.Add(0, ($"Building switches this checkout from {preview.CurrentBranch} to {branch} and leaves it there.", gold));
        if (OnlyLocalChanges) page.Add(0, ("Modified tracked files stop the build. Enter asks whether to build with them anyway.", "bold " + gold));
        else if (!preview.CanBuild) page.Add(0, ("This release cannot be built. CANNOT BUILD below says why.", "bold " + ReleaseScreen.Danger));

        page.Blank();
        page.Add(0, ("CHECKOUT", heading));
        if (preview.ModifiedFiles.Count == 0) page.Add(2, ("No modified tracked files.", muted));
        else
        {
            page.Add(2, ($"Modified tracked files ({preview.ModifiedFiles.Count}):", gold));
            foreach (var path in preview.ModifiedFiles) page.Add(4, (path, text));
        }
        if (preview.UntrackedFiles.Count != 0)
        {
            page.Add(2, ($"Untracked files, kept as they are ({preview.UntrackedFiles.Count}):", muted));
            foreach (var path in preview.UntrackedFiles) page.Add(4, (path, muted));
        }

        page.Blank();
        page.Add(0, ("BUILDS", heading), ("  at the same time", muted));
        var profile = preview.Profile;
        page.Add(2, ("Server  ", "bold " + text), ($"dotnet publish {OnlineRelease.ServerProject}.csproj -c {profile?.Configuration} -p:PublishProfile={OnlineRelease.ProfileName}", text));
        if (profile is not null)
        {
            page.Add(4, ("Publish folder  ", muted), (profile.PublishFolder, text));
            page.Add(4, ("Profile  ", muted), (profile.Path, muted));
            page.Add(4, (string.Join(", ", profile.Properties.Select(property => $"{property.Key} {property.Value}")), muted));
        }
        page.Add(4, ("dotnet  ", muted), preview.Dotnet is null ? ("not found on PATH", ReleaseScreen.Danger) : (preview.Dotnet, muted));
        page.Add(2, ("App     ", "bold " + text), ($"npm run {OnlineRelease.Script} in {OnlineRelease.AppFolder}", text));
        page.Add(4, preview.NodeModules ? ("node_modules is there; npm install runs only if the pull changes package.json.", muted)
            : ("node_modules is missing, so npm install runs first.", gold));
        page.Add(4, ("npm  ", muted), preview.Npm is null ? ("not found on PATH", ReleaseScreen.Danger) : (preview.Npm, muted));
        page.Add(2, ("Then www is copied into the publish folder as wwwroot, and the publish folder is zipped.", muted));

        page.Blank();
        page.Add(0, ("PACKAGE  ", heading), (preview.Zip, "bold " + text));
        page.Add(2, preview.ZipExists ? ("Replaces the MORelease.zip there once the new one is complete and checked.", gold) : ("Nothing is there yet.", muted));

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

/// <summary>The build's steps with their times, its progress messages as they arrive, and what Esc does.</summary>
internal sealed class OnlineBuildView(OnlineReleaseResult preview)
{
    private readonly List<string> messages = [];
    private readonly Dictionary<string, (long Start, long? End)> steps = [];
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private int changes;
    internal bool Cancelling { get; set; }
    internal long Second => (long)watch.Elapsed.TotalSeconds;
    internal int Changes => Volatile.Read(ref changes);

    internal void Report(string message)
    {
        lock (messages) messages.Add(message);
        Interlocked.Increment(ref changes);
    }

    internal void Step(string name, bool started)
    {
        lock (messages) steps[name] = started ? (watch.ElapsedMilliseconds, null) : (steps.GetValueOrDefault(name).Start, watch.ElapsedMilliseconds);
        Interlocked.Increment(ref changes);
    }

    internal IRenderable Render(int width, int height)
    {
        List<string> shown;
        List<(string Name, string State, bool Running)> lines;
        lock (messages)
        {
            shown = [.. messages];
            var now = watch.ElapsedMilliseconds;
            lines = new[] { OnlineRelease.PullStep, OnlineRelease.InstallStep, OnlineRelease.PublishStep, OnlineRelease.FrontEndStep, OnlineRelease.AssembleStep, OnlineRelease.ZipStep }
                .Where(name => name != OnlineRelease.InstallStep || steps.ContainsKey(name))
                .Select(name => steps.TryGetValue(name, out var time)
                    ? (name, time.End is { } end ? "done in " + OnlineReleaseScreen.Clock((end - time.Start) / 1000.0) : "running " + OnlineReleaseScreen.Clock((now - time.Start) / 1000.0), time.End is null)
                    : (name, "waiting", false)).ToList();
        }
        var footer = new WrappedLines(width - 1);
        if (Cancelling) footer.Add(0, ("Cancelling. The builds are being stopped; MORelease.zip stays as it was.", TerminalStyle.Gold));
        else
        {
            footer.Add(0, ("Esc", TerminalStyle.Gold), (" cancel", TerminalStyle.Text));
            footer.Add(0, ("Cancelling stops the builds. MORelease.zip is replaced only once a complete new one is ready.", TerminalStyle.Muted));
        }
        // The header, a blank line, the checkout, zip and clock, the steps, a blank line, and the footer with the cursor's line.
        var room = Math.Max(1, height - 4 - 1 - 3 - lines.Count - 1 - 1 - footer.Lines.Count - 1);
        var text = new Style(Color.FromHex(TerminalStyle.Text));
        var muted = new Style(Color.FromHex(TerminalStyle.Muted));
        var rows = new List<IRenderable>
        {
            TerminalStyle.Header("BUILDING MEYERONLINE RELEASE", $"In your MeyerOnline checkout, which stays on {preview.Branch}."),
            new Text(""),
            TerminalStyle.Line(TicketWorkspaceView.Fit(preview.Repository, width - 1), new Style(Color.FromHex(TerminalStyle.Accent), decoration: Decoration.Bold)),
            TerminalStyle.Line(TicketWorkspaceView.Fit("Zip  " + preview.Zip, width - 1), text),
            TerminalStyle.Line($"Elapsed {watch.Elapsed:m\\:ss}", muted)
        };
        rows.AddRange(lines.Select(line => TerminalStyle.Line(TicketWorkspaceView.Fit($"  {line.Name,-16}{line.State}", width - 1),
            line.Running ? new Style(Color.FromHex(TerminalStyle.Accent)) : muted)));
        rows.Add(new Text(""));
        var recent = shown.Skip(Math.Max(0, shown.Count - room)).ToList();
        for (var index = 0; index < room; index++)
            rows.Add(index >= recent.Count ? new Text("") : TerminalStyle.Line(TicketWorkspaceView.Fit("  " + recent[index], width - 1), index == recent.Count - 1 ? text : muted));
        rows.Add(new Text(""));
        rows.AddRange(footer.Lines);
        return new Rows(rows);
    }
}
