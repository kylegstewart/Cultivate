using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal sealed class Terminal(Settings settings, GitRepository git, Bridge bridge)
{
    private static string Escape(string value) => Markup.Escape(value);
    private const string UpdateAction = "Update Cultivate";
    internal static IReadOnlyList<MenuAction> HomeActions { get; } =
    [
        new("Choose a worktree or branch", "Browse versions and preview their changed files before deploying anything."),
        new("Refresh deployment status", "Reload the active version and its automatic-update status."),
        new("Restore original webfarm files", "Stop automatic updates and restore the originals backed up before this test."),
        new("Settings", "Change the MyMeyer checkout, dev webfarm destination, MSBuild, and automatic-update default."),
        new("Open logs", "Open Cultivate's diagnostic logs in File Explorer."),
        new("How it works", "Read the guide to version selection, deployment, and recovery."),
        new("Exit", "Close this menu without stopping active automatic updates.")
    ];

    internal static List<MenuAction> PreviewActions(bool canDeploy)
    {
        var actions = new List<MenuAction>();
        if (canDeploy)
        {
            actions.Add(new("Activate and automatically update saved changes",
                "Deploy the planned files and enable automatic updates after successful builds."));
            actions.Add(new("Activate once (no automatic updates)",
                "Deploy this preview once; later saves will not update the webfarm."));
        }
        actions.Add(new("Show every changed file", "Show all Git changes, including files with no deployment action."));
        actions.Add(new("Back", "Return home without deploying this version."));
        return actions;
    }

    private static string Select(string title, IEnumerable<MenuAction> choices) => new ActionPicker(choices).Show(title).Label;

    internal static List<(Change Change, string Action)> ActionableChanges(Plan plan)
    {
        var actions = new List<(Change, string)>();
        foreach (var change in plan.Changes)
        {
            var frontend = plan.Frontend.FirstOrDefault(file => Files.Comparer.Equals(file.Source, change.Path));
            var owners = plan.Projects.Where(project => Planner.IsCompiledInput(change)
                ? project.Owns(change, plan.Changes) : project.Owns(change.Path)).ToList();
            if (frontend is not null)
                actions.Add((change, (frontend.Delete ? "Delete " : "Sync ") + frontend.RelativePath));
            else if (owners.Count != 0)
                actions.Add((change, "Build " + string.Join(", ", owners.Select(project => project.Assembly + ".dll"))));
        }
        return actions;
    }

    public async Task Run()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("Open Cultivate in an interactive terminal, or use list / status / plan for JSON output.");
        // Runs in the background; the notice appears on the first home screen drawn after it finishes.
        var updateCheck = settings.CheckForUpdates && Updater.IsInstalled ? Updater.CheckQuietly() : Task.FromResult<UpdateInfo?>(null);
        const string section = "DEV LAUNCHPAD", tagline = "Your code. Your workspace. Ready for the farm.";
        while (true)
        {
            var workspace = TerminalStyle.Card(new Rows(
                new Markup($"[{TerminalStyle.Muted}]REPOSITORY[/]   {Escape(Path.GetFileName(Files.Full(settings.RepositoryRoot)))}"),
                new Markup($"[{TerminalStyle.Muted}]DESTINATION[/]  {Escape(settings.WebfarmRoot)}")), "CONNECTED WORKSPACE");
            // The banner is drawn while the status loads, so it plans room for a typical screen: an active deployment and the menu.
            var banner = new Banner(section, tagline, TerminalStyle.Lines(workspace) + 5 + MenuLines(HomeActions.Count));
            AnsiConsole.Clear();
            AnsiConsole.Write(banner);
            AnsiConsole.Write(workspace);
            var state = await bridge.Status();
            var cards = new List<IRenderable>();
            if (state?.Active == true)
            {
                var health = !state.WatchEnabled && state.Mode == "Cultivate" ? "[grey]one-time deployment[/]" :
                    state.Mode == "Cultivate" && DateTime.TryParse(state.Heartbeat, out var heartbeat) &&
                    DateTime.UtcNow - heartbeat.ToUniversalTime() < TimeSpan.FromSeconds(20) ? "[green]watching saved files[/]" : "[yellow]no healthy auto-update worker[/]";
                cards.Add(TerminalStyle.Card(new Markup($"[bold {TerminalStyle.Accent}]LIVE[/]  {Escape(state.Branch)}\n{Escape(state.SourceRoot.Length == 0 ? state.SourceRef : state.SourceRoot)}\n{health}" +
                    (string.IsNullOrEmpty(state.LastError) ? "" : $"\n[red]Update blocked:[/] {Escape(state.LastError)}")), "ACTIVE DEPLOYMENT", TerminalStyle.Accent));
            }
            else cards.Add(TerminalStyle.Card(new Markup(
                $"[{TerminalStyle.Gold}]READY WHEN YOU ARE[/]\n[{TerminalStyle.Muted}]Choose a version to inspect its files and builds. Nothing deploys until you confirm.[/]"),
                "NO ACTIVE TEST VERSION"));
            var update = updateCheck.IsCompletedSuccessfully ? updateCheck.Result : null;
            var actions = HomeActions.ToList();
            if (update is not null)
            {
                cards.Add(TerminalStyle.Card(new Markup(
                    $"[bold {TerminalStyle.Gold}]Cultivate {Escape(update.Version)} is available[/]  [{TerminalStyle.Muted}]you have {Escape(Files.Version)}[/]\n" +
                    $"[{TerminalStyle.Muted}]Choose Update Cultivate below, or run `cultivate update`.[/]"), "UPDATE AVAILABLE", TerminalStyle.Gold));
                actions.Insert(actions.Count - 1, new(UpdateAction,
                    $"Download and install {update.Version}, then restart. Active deployments and automatic updates keep running."));
            }
            // An update notice or a long error can outgrow that plan; switch to the compact header instead of scrolling the banner away.
            var below = TerminalStyle.Lines(workspace) + cards.Sum(TerminalStyle.Lines) + MenuLines(actions.Count);
            if (banner.FitsOn(AnsiConsole.Console) && !new Banner(section, tagline, below).FitsOn(AnsiConsole.Console))
            {
                AnsiConsole.Clear();
                AnsiConsole.Write(TerminalStyle.Header(section, tagline));
                AnsiConsole.Write(workspace);
            }
            foreach (var card in cards) AnsiConsole.Write(card);
            AnsiConsole.WriteLine();
            var action = Select("What would you like to do?", actions);
            try
            {
                switch (action)
                {
                    case UpdateAction when update is not null:
                        if (!AnsiConsole.Confirm($"Update Cultivate {Escape(Files.Version)} to {Escape(update.Version)}?", true)) break;
                        var release = await AnsiConsole.Status().StartAsync("Checking the release...", _ => Updater.Fetch(update.Version));
                        await AnsiConsole.Status().StartAsync("Updating...", context => Updater.Install(release, message => context.Status(Escape(message))));
                        AnsiConsole.MarkupLine($"[{TerminalStyle.Accent}]Updated to Cultivate {Escape(release.Version)}.[/] Restarting...");
                        Updater.Restart();
                        break;
                    case "Choose a worktree or branch": await Choose(state); break;
                    case "Restore original webfarm files":
                        if (AnsiConsole.Confirm("Stop automatic updates and restore files from before this test?", false))
                        {
                            await AnsiConsole.Status().StartAsync("Restoring journaled originals...", _ => bridge.Call("Restore"));
                            AnsiConsole.MarkupLine("[green]Original webfarm files restored.[/]");
                            Pause();
                        }
                        break;
                    case "Settings":
                        await Setup.Interactive(settings, state);
                        Pause();
                        break;
                    case "Open logs":
                        Directory.CreateDirectory(Path.Combine(Files.Home, "Logs"));
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Path.Combine(Files.Home, "Logs")) { UseShellExecute = true });
                        break;
                    case "How it works":
                        var guide = TerminalStyle.Card(new Text(
                            "1. Choose a worktree for its saved disk contents, or a branch for committed contents.\n" +
                            "2. Review frontend destinations and affected projects. Nothing is deployed yet.\n" +
                            "3. Confirm activation. Cultivate snapshots and builds only projects owning changed compiled files.\n" +
                            "4. Verified frontend files and DLLs replace the previous test version together.\n" +
                            "5. Built-in file sync copies later frontend saves; code saves rebuild only the changed projects.\n" +
                            "   Unchanged dependencies and consumers are never automatically rebuilt or copied.\n" +
                            "   Failed builds leave the last successful version in place.\n\n" +
                            "Your Git branch is never switched, committed, or pushed. Unsaved editor buffers are not accessible.\n" +
                            "Close the old WebfarmFileSync watcher first. Multiple files cannot be atomically swapped on a live IIS share;\n" +
                            "an interrupted deployment can be recovered with Restore. External webfarm edits block restoration.\n" +
                            "SQL and site configuration are not automatically published. Static assets need Code Release Manager mappings."),
                            "HOW CULTIVATE WORKS");
                        AnsiConsole.Clear();
                        AnsiConsole.Write(new Banner("FIELD GUIDE", "A safe route from local code to your development webfarm.",
                            TerminalStyle.Lines(guide) + PauseLines));
                        AnsiConsole.Write(guide);
                        Pause();
                        break;
                    case "Exit": return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Xml.XmlException or System.Text.Json.JsonException)
            {
                ShowError(exception);
                Pause();
            }
        }
    }

    private async Task Choose(DeploymentState? state)
    {
        var targets = await AnsiConsole.Status().StartAsync("Finding worktrees and cached branches...", _ => git.Targets());
        var target = new VersionPicker(targets, state).Show();
        if (target is null) return;
        var planner = new Planner(settings, git, bridge);
        var plan = await AnsiConsole.Status().StartAsync("Inspecting changed files, frontend mappings, and owning projects...", _ => planner.Create(target));
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(TerminalStyle.Header("DEPLOYMENT PREVIEW", "Inspect the plan. You are still in preview mode."));
            AnsiConsole.Write(TerminalStyle.Card(new Markup(
                $"[bold {TerminalStyle.Accent}]{Escape(target.Branch)}[/]\n" +
                $"[{TerminalStyle.Muted}]SOURCE[/]       {Escape(target.Worktree ?? target.Ref)}\n" +
                $"[{TerminalStyle.Muted}]DESTINATION[/]  {Escape(settings.WebfarmRoot)}"), "SELECTED VERSION"));
            var actionable = ActionableChanges(plan);
            AnsiConsole.MarkupLine($"[bold {(plan.CanDeploy ? TerminalStyle.Accent : TerminalStyle.Gold)}]{(plan.CanDeploy ? "READY TO ACTIVATE" : "ACTIVATION BLOCKED")}[/]   " +
                $"[{TerminalStyle.Muted}]{actionable.Count} changed files   /   {plan.Frontend.Count} frontend   /   {plan.Projects.Count} DLL builds[/]");
            var changes = new Table().Border(TableBorder.Rounded).BorderColor(Color.FromHex(TerminalStyle.Border)).Expand()
                .AddColumn($"[{TerminalStyle.Accent}]CHANGED FILES[/]").AddColumn($"[{TerminalStyle.Accent}]ACTION[/]");
            // Each project builds once however many of its files changed; later rows say so instead of repeating the action.
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (change, description) in actionable)
                changes.AddRow(Escape((change.Deleted ? "DELETE " : "") + change.Path),
                    description.StartsWith("Build ", StringComparison.Ordinal) && !shown.Add(description)
                        ? $"[{TerminalStyle.Muted}]{Escape(description[6..])} (same build)[/]" : Escape(description));
            if (actionable.Count == 0) changes.AddRow("[grey]No files have a deployment action[/]", "");
            AnsiConsole.Write(changes);
            AnsiConsole.Write(TerminalStyle.Card(new Markup(plan.Projects.Count == 0 ? $"[{TerminalStyle.Muted}]Frontend only. No DLL builds needed.[/]" :
                string.Join("\n", plan.Projects.Select((p, index) => $"[{TerminalStyle.Gold}]{index + 1:00}[/]  {Escape(p.Assembly)}  [{TerminalStyle.Muted}]{Escape(p.RelativePath)}[/]"))),
                "CHANGED PROJECTS ONLY"));
            foreach (var note in plan.Notes) AnsiConsole.MarkupLine($"[grey]{Escape(note)}[/]");
            foreach (var error in plan.Errors) AnsiConsole.MarkupLine($"[red]{Escape(error)}[/]");
            var action = Select("[bold]Preview only[/] - choose an action", PreviewActions(plan.CanDeploy));
            if (action == "Back") return;
            if (action == "Show every changed file")
            {
                foreach (var change in plan.Changes) AnsiConsole.MarkupLine(Escape((change.Deleted ? "DELETE " : "") + change.Path));
                Pause();
                continue;
            }
            if (!AnsiConsole.Confirm($"Deploy [{TerminalStyle.Accent}]{Escape(target.Branch)}[/] to [{TerminalStyle.Gold}]{Escape(settings.WebfarmRoot)}[/]?", false)) continue;
            var watch = action == "Activate and automatically update saved changes";
            var activated = await AnsiConsole.Status().StartAsync("Preparing activation...", context =>
                new Pipeline(settings, git, bridge).Activate(plan, message => context.Status(Escape(message)), watch));
            AnsiConsole.Write(TerminalStyle.Card(new Markup($"[bold {TerminalStyle.Accent}]VERSION ACTIVATED[/]\nRefresh your development site to use the selected version."),
                "DEPLOYMENT COMPLETE", TerminalStyle.Accent));
            AnsiConsole.Write(UpdatedFiles(activated, settings.WebfarmRoot));
            AnsiConsole.MarkupLine(watch ? "[grey]Saved changes update automatically after successful builds; closing this menu keeps updates running.[/]" :
                "[grey]This is a one-time deployment. Later saves will not be published.[/]");
            Pause();
            return;
        }
    }

    /// <summary>Every webfarm file the activation wrote, deleted, or restored, with what happened to it.</summary>
    internal static IRenderable UpdatedFiles(DeploymentState state, string webfarmRoot)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.FromHex(TerminalStyle.Border)).Expand()
            .AddColumn($"[{TerminalStyle.Accent}]UPDATED ON {Escape(webfarmRoot.ToUpperInvariant())}[/]")
            .AddColumn($"[{TerminalStyle.Accent}]CHANGE[/]");
        foreach (var file in state.Updated)
            table.AddRow(Escape(file.RelativePath), $"[{(file.Change == UpdatedFile.Restored ? TerminalStyle.Gold : TerminalStyle.Muted)}]{Escape(file.Change)}[/]");
        if (state.Updated.Count == 0) table.AddRow("[grey]No webfarm files changed[/]", "");
        return table;
    }

    // Lines below the cards on the home screen: a blank line, the menu title, its actions, the highlighted help, the cursor.
    private static int MenuLines(int actions) => actions + 4;
    // Pause's blank line and prompt, plus the cursor.
    private const int PauseLines = 3;

    private static void Pause()
    {
        AnsiConsole.MarkupLine($"\n[{TerminalStyle.Gold}]Press any key[/] [{TerminalStyle.Muted}]to continue[/]");
        Console.ReadKey(true);
    }
    internal static void ShowError(Exception exception)
    {
        var log = Path.Combine(Files.Home, "Logs", "last-error.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, exception.ToString());
        AnsiConsole.Write(TerminalStyle.Card(new Text(exception.Message), "OPERATION BLOCKED", "#F0A3A3"));
        AnsiConsole.MarkupLine($"[grey]Details: {Escape(log)}[/]");
    }

}

internal static class TerminalStyle
{
    internal const string Accent = "#C9E9AC";
    internal const string Gold = "#EBC477";
    internal const string Muted = "#93A69D";
    internal const string Hint = "#B3B3B3";
    internal const string Text = "#E4EDE6";
    internal const string Surface = "#224B3A";
    internal const string Border = "#35584A";
    internal static Style Selection { get; } = new(Color.FromHex(Accent), Color.FromHex(Surface), Decoration.Bold);

    internal static Text Line(string value, Style? style = null) =>
        new(value, style ?? new Style(Color.FromHex(Text))) { Overflow = Overflow.Ellipsis };

    internal static Panel Card(IRenderable content, string title, string? color = null) =>
        new Panel(content).Header($"[{color ?? Muted}]{title}[/]")
            .Border(BoxBorder.Rounded).BorderColor(Color.FromHex(color ?? Border)).Expand();

    /// <summary>Lines <paramref name="renderable"/> takes on the terminal at its current width.</summary>
    internal static int Lines(IRenderable renderable) =>
        renderable.GetSegments(AnsiConsole.Console).Count(segment => segment.IsLineBreak);

    /// <summary>The compact screen header, for screens that need their rows and as the <see cref="Banner"/>'s fallback.</summary>
    internal static IRenderable Header(string section, string subtitle) => Card(new Rows(
        new Markup($"[bold {Accent}]C U L T I V A T E[/]    [{Gold}]{section}[/]"),
        new Markup($"[{Muted}]{Markup.Escape(subtitle)}[/]")), "LOCAL CODE / DEV WEBFARM", Accent);
}
