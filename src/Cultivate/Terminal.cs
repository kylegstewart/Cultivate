using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal sealed class Terminal(Settings settings, GitRepository git, Bridge bridge)
{
    private static string Escape(string value) => Markup.Escape(value);
    private const string UpdateAction = "Update Cultivate";
    internal static IReadOnlyList<MenuAction> HomeActions { get; } =
    [
        new("Update my webfarm", "Browse versions and preview their changed files before deploying anything."),
        new("Open solution", "Open a repository's main solution, or a worktree or local branch with its changed source files."),
        new("Restore original webfarm files", "Stop automatic updates and restore the originals backed up before this test."),
        new("Settings", "Change the MyMeyer checkout, dev webfarm destination, MSBuild, and automatic-update default."),
        new("Exit", "Close this menu without stopping active automatic updates.")
    ];

    internal static List<MenuAction> PreviewActions(bool canDeploy, bool hasSelectableChanges = false)
    {
        var actions = new List<MenuAction>();
        if (canDeploy)
        {
            actions.Add(new("Activate once (no automatic updates)",
                "Deploy this preview once; later saves will not update the webfarm."));
            actions.Add(new("Activate and automatically update saved changes",
                "Deploy the planned files and enable automatic updates after successful builds."));
        }
        if (hasSelectableChanges)
            actions.Add(new("Choose files to deploy", "Uncheck files to exclude them from activation and automatic updates; check them again to include them."));
        actions.Add(new("Show every changed file", "Show all Git changes, including files with no deployment action."));
        actions.Add(new("Back", "Return home without deploying this version."));
        return actions;
    }

    private static string Select(string title, IEnumerable<MenuAction> choices) => new ActionPicker(choices).Show(title).Label;

    internal static List<(Change Change, string Action)> ActionableChanges(Plan plan)
    {
        var actions = new List<(Change, string)>();
        var selected = plan.Changes.Where(change => !IsExcluded(plan, change.Path)).ToList();
        foreach (var change in selected)
        {
            var frontend = plan.Frontend.FirstOrDefault(file => Files.Comparer.Equals(file.Source, change.Path));
            var owners = plan.Projects.Where(project => Planner.IsCompiledInput(change)
                ? project.Owns(change, selected) : project.Owns(change.Path)).ToList();
            if (frontend is not null)
                actions.Add((change, (frontend.Delete ? "Delete " : "Sync ") + frontend.RelativePath));
            else if (owners.Count != 0)
                actions.Add((change, "Build " + string.Join(", ", owners.Select(project => project.Assembly + ".dll"))));
        }
        return actions;
    }

    /// <summary>Changed files Open solution shows: actionable code plus every changed frontend file, whether or not it is mapped for deployment.</summary>
    internal static IEnumerable<Change> ChangesToOpen(Plan plan) => ActionableChanges(plan).Select(item => item.Change)
        .Concat(plan.Changes.Where(change => Planner.FrontendExtensions.Contains(Path.GetExtension(change.Path))));

    private static bool IsExcluded(Plan plan, string path) =>
        plan.ExcludedPaths.Contains(path.Replace('\\', '/'), Files.Comparer);

    internal static List<string> SelectableChanges(Plan plan) => plan.Changes
        .Where(change => Planner.IsCompiledInput(change) ||
            Planner.FrontendExtensions.Contains(Path.GetExtension(change.Path)) && !Planner.IsExcluded(change.Path))
        .Select(change => change.Path).ToList();

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
                    (string.IsNullOrEmpty(state.LastError) ? "" : $"\n[red]Automatic update failed:[/] {Escape(state.LastError)}")), "ACTIVE DEPLOYMENT", TerminalStyle.Accent));
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
                    case "Update my webfarm": await Choose(state); break;
                    case "Open solution": await OpenSolution(state); break;
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
                    case "Exit": return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
                System.Xml.XmlException or System.Text.Json.JsonException)
            {
                ShowError(exception, action);
                Pause();
            }
        }
    }

    private async Task OpenSolution(DeploymentState? state)
    {
        var (targets, others) = await AnsiConsole.Status().StartAsync("Finding worktrees and local branches...", async _ =>
        {
            var myMeyer = await git.Targets();
            return (myMeyer, await git.OtherWorktrees(myMeyer));
        });
        var picker = new VersionPicker(targets.Concat(others), state, openSolution: true);
        var target = picker.Show();
        if (picker.OpenMainWorktree)
        {
            var roots = await AnsiConsole.Status().StartAsync("Finding repositories' main worktrees...", _ =>
                git.MainWorktrees(targets.Concat(others)));
            if (roots.Count == 0) throw new InvalidOperationException("No main worktrees containing a solution were found.");
            var choices = roots.Select(root => new MenuAction(Path.GetFileName(root) + "  |  " + root,
                "Open this repository's main worktree solution without opening specific files.")).ToList();
            choices.Add(new("Back", "Return home without opening a solution."));
            var choice = Select("Which repository's main worktree?", choices);
            if (choice == "Back") return;
            var root = roots[choices.FindIndex(item => item.Label == choice)];
            var mainSolution = SelectSolution(root);
            var ide = await Settings.FindVisualStudio()
                ?? throw new InvalidOperationException("Visual Studio IDE was not found. Install Visual Studio to open the solution.");
            await AnsiConsole.Status().StartAsync("Opening the main worktree solution...", _ =>
                SolutionOpener.Open(ide, mainSolution, []));
            return;
        }
        if (target is null) return;
        var visualStudio = await Settings.FindVisualStudio()
            ?? throw new InvalidOperationException("Visual Studio IDE was not found. Install Visual Studio to open the solution and its changed files.");
        string solution;
        List<string> files;
        if (others.Contains(target))
        {
            // Another repository has no deployment plan, so every changed source file under its solution opens.
            var root = target.Worktree!;
            solution = SelectSolution(root);
            var changes = await AnsiConsole.Status().StartAsync("Finding changed source files to open...", async _ =>
                (await git.Inspect(target, await git.DefaultBranch(root))).Changes);
            files = SolutionOpener.FilesToOpen(root, solution, changes);
        }
        else
        {
            solution = await AnsiConsole.Status().StartAsync("Locating the solution...", _ => git.Solution(target));
            var root = Path.GetDirectoryName(solution)!;
            // Opening needs no webfarm destinations, so skip the Code Release Manager lookup; unmapped markup still opens.
            var plan = await AnsiConsole.Status().StartAsync("Finding actionable changes to open...", _ =>
                new Planner(settings, git, bridge).Create(target with { Worktree = root }, _ => Task.FromResult(new Dictionary<string, string>())));
            files = SolutionOpener.FilesToOpen(root, solution, ChangesToOpen(plan));
        }
        await AnsiConsole.Status().StartAsync("Opening the solution and changed source files...", _ =>
            SolutionOpener.Open(visualStudio, solution, files));
    }

    private static string SelectSolution(string root)
    {
        var solutions = SolutionOpener.Solutions(root);
        if (solutions.Count == 0) throw new InvalidOperationException($"No solution was found in {root}.");
        return solutions.Count == 1 ? solutions[0] : AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"[{TerminalStyle.Accent}]Which solution in {Escape(Path.GetFileName(root))}?[/]")
            .HighlightStyle(TerminalStyle.Selection).UseConverter(path => Escape(Path.GetRelativePath(root, path)))
            .AddChoices(solutions));
    }

    private async Task Choose(DeploymentState? state)
    {
        var targets = await AnsiConsole.Status().StartAsync("Finding worktrees and cached branches...", _ => git.Targets());
        var target = new VersionPicker(targets, state).Show();
        if (target is null) return;
        var planner = new Planner(settings, git, bridge);
        var excluded = state?.Active == true && state.SourceRef == target.Ref &&
            Files.Comparer.Equals(state.SourceRoot, target.Worktree ?? "") ? state.ExcludedPaths : [];
        var plan = await AnsiConsole.Status().StartAsync("Inspecting changed files, frontend mappings, and owning projects...", _ =>
            planner.Create(target, excludedPaths: excluded));
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(TerminalStyle.Header("DEPLOYMENT PREVIEW", "Inspect the plan. You are still in preview mode."));
            AnsiConsole.Write(TerminalStyle.Card(new Markup(
                $"[bold {TerminalStyle.Accent}]{Escape(target.Branch)}[/]\n" +
                $"[{TerminalStyle.Muted}]SOURCE[/]       {Escape(target.Worktree ?? target.Ref)}\n" +
                $"[{TerminalStyle.Muted}]DESTINATION[/]  {Escape(settings.WebfarmRoot)}"), "SELECTED VERSION"));
            var actionable = ActionableChanges(plan);
            var selectable = SelectableChanges(plan);
            AnsiConsole.MarkupLine($"[bold {(plan.CanDeploy ? TerminalStyle.Accent : TerminalStyle.Gold)}]{(plan.CanDeploy ? "READY TO ACTIVATE" : "ACTIVATION BLOCKED")}[/]   " +
                $"[{TerminalStyle.Muted}]{actionable.Count} selected files   /   {plan.ExcludedPaths.Count} excluded   /   {plan.Frontend.Count} frontend   /   {plan.Projects.Count} DLL builds[/]");
            var changes = new Table().Border(TableBorder.Rounded).BorderColor(Color.FromHex(TerminalStyle.Border)).Expand()
                .AddColumn($"[{TerminalStyle.Accent}]CHANGED FILES[/]").AddColumn($"[{TerminalStyle.Accent}]ACTION[/]");
            // Each project builds once however many of its files changed; later rows say so instead of repeating the action.
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (change, description) in actionable)
                changes.AddRow(Escape((change.Deleted ? "DELETE " : "") + change.Path),
                    description.StartsWith("Build ", StringComparison.Ordinal) && !shown.Add(description)
                        ? $"[{TerminalStyle.Muted}]{Escape(description[6..])} (same build)[/]" : Escape(description));
            foreach (var change in plan.Changes.Where(change => IsExcluded(plan, change.Path)))
                changes.AddRow($"[{TerminalStyle.Muted}]{Escape(change.Path)}[/]", $"[{TerminalStyle.Gold}]Excluded[/]");
            if (actionable.Count == 0) changes.AddRow("[grey]No files have a deployment action[/]", "");
            AnsiConsole.Write(changes);
            AnsiConsole.Write(TerminalStyle.Card(new Markup(plan.Projects.Count == 0 ? $"[{TerminalStyle.Muted}]Frontend only. No DLL builds needed.[/]" :
                string.Join("\n", plan.Projects.Select((p, index) => $"[{TerminalStyle.Gold}]{index + 1:00}[/]  {Escape(p.Assembly)}  [{TerminalStyle.Muted}]{Escape(p.RelativePath)}[/]" +
                    string.Concat(p.PackageDependencies.Select(hint => $"\n    + {Escape(Path.GetFileName(hint))}  " +
                        $"[{TerminalStyle.Muted}]changed package {Escape(p.HintVersions.GetValueOrDefault(hint) ?? hint)}[/]"))))),
                "CHANGED PROJECTS ONLY"));
            foreach (var note in plan.Notes) AnsiConsole.MarkupLine($"[grey]{Escape(note)}[/]");
            foreach (var error in plan.Errors) AnsiConsole.MarkupLine($"[red]{Escape(error)}[/]");
            var action = Select("[bold]Preview only[/] - choose an action", PreviewActions(plan.CanDeploy, selectable.Count != 0));
            if (action == "Back") return;
            if (action == "Choose files to deploy")
            {
                AnsiConsole.Clear();
                AnsiConsole.Write(TerminalStyle.Header("SELECT DEPLOYMENT FILES", "Checked files deploy. Unchecked files stay excluded from automatic updates."));
                var prompt = new MultiSelectionPrompt<string>().Title($"[{TerminalStyle.Accent}]Choose files to deploy[/]")
                    .NotRequired().PageSize(12).HighlightStyle(TerminalStyle.Selection)
                    .InstructionsText("[grey](Space toggles a file; Enter applies the selection)[/]")
                    .UseConverter(Escape).AddChoices(selectable);
                foreach (var path in selectable.Where(path => !IsExcluded(plan, path))) prompt.Select(path);
                var included = AnsiConsole.Prompt(prompt).ToHashSet(Files.Comparer);
                excluded = plan.ExcludedPaths.Where(path => !selectable.Contains(path, Files.Comparer))
                    .Concat(selectable.Where(path => !included.Contains(path))).ToList();
                plan = await AnsiConsole.Status().StartAsync("Updating selected files and owning projects...", _ =>
                    planner.Create(target, excludedPaths: excluded));
                continue;
            }
            if (action == "Show every changed file")
            {
                foreach (var change in plan.Changes) AnsiConsole.MarkupLine(Escape((IsExcluded(plan, change.Path) ? "EXCLUDED " : "") +
                    (change.Deleted ? "DELETE " : "") + change.Path));
                Pause();
                continue;
            }
            if (!AnsiConsole.Confirm($"Deploy [{TerminalStyle.Accent}]{Escape(target.Branch)}[/] to [{TerminalStyle.Gold}]{Escape(settings.WebfarmRoot)}[/]?", true)) continue;
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
            table.AddRow(Escape(file.RelativePath), $"[{(file.Change is UpdatedFile.Restored or UpdatedFile.Kept ? TerminalStyle.Gold : TerminalStyle.Muted)}]{Escape(file.Change)}[/]");
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
    internal static void ShowError(Exception exception, string stage = "Operation")
    {
        var report = Diagnostics.Record(exception, stage);
        AnsiConsole.Write(TerminalStyle.Card(new Text(report.Text), report.Label, "#F0A3A3"));
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
