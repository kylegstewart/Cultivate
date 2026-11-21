using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

internal sealed class Terminal(Settings settings, GitRepository git, Bridge bridge)
{
    internal static string Escape(string value) => Markup.Escape(value);
    private const string UpdateAction = "Update Cultivate";
    internal static IReadOnlyList<MenuAction> HomeActions { get; } =
    [
        new("Update my webfarm", "Browse versions and preview their changed files before deploying anything."),
        new("Open solution", "Open a repository's main solution, or a worktree or local branch with its changed source files."),
        new("Review a pull request", "Pick a GitHub pull request and preview its changes for your dev webfarm, without a branch or worktree."),
        new(ReleaseScreen.Action, "Build a merged pull request's DLLs and changed files from master for the Code Release Manager."),
        new(OnlineReleaseScreen.Action, "Pull MeyerOnline's master, publish the server, build the app, and zip both as MORelease.zip."),
        new("Ticket workspaces", "See one ticket's worktrees, pull requests, and SQL files across every repository, and open them together."),
        new("Tidy worktrees", "Sort and filter every repository's worktrees by size and age, then delete the ones you choose."),
        new("Clean up", "Remove Cultivate's old build runs and logs, and review worktrees unused for weeks that have nothing to lose."),
        new("Restore original webfarm files", "Stop automatic updates and restore the originals backed up before this test."),
        new("Settings", "Change the MyMeyer checkout, dev webfarm destination, MSBuild, and automatic-update default."),
        new("Exit", "Close this menu without stopping active automatic updates.", Back: true)
    ];

    internal static List<MenuAction> PreviewActions(bool canDeploy, bool hasSelectableChanges = false, bool hasLibraries = false)
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
        if (hasLibraries)
            actions.Add(new(LibraryLoop.ChooseAction, "Build a matching library worktree's changed projects and deploy their DLLs with this version, or leave them out."));
        actions.Add(new("Show every changed file", "Show all Git changes, including files with no deployment action."));
        actions.Add(new("Back", "Return to where you chose this version, without deploying it.", Back: true));
        return actions;
    }

    internal static string Select(string title, IEnumerable<MenuAction> choices) => Select(title, choices, search: false);

    /// <summary>The chosen action's label; Esc gives the menu's back action, or an empty label when it has none.</summary>
    internal static string Select(string title, IEnumerable<MenuAction> choices, bool search) => new ActionPicker(choices, search).Show(title)?.Label ?? "";

    /// <summary>The home menu, with the update action before Exit when one is available, and <paramref name="highlighted"/> highlighted.</summary>
    internal static ActionPicker HomeMenu(UpdateInfo? update, string? highlighted)
    {
        var actions = HomeActions.ToList();
        if (update is not null)
            actions.Insert(actions.Count - 1, new(UpdateAction,
                $"Download and install {update.Version}, then restart. Active deployments and automatic updates keep running."));
        return new ActionPicker(actions, top: true, highlight: highlighted);
    }

    internal static bool Confirm(string question, bool preset) => Confirm(question, preset, AnsiConsole.Console, KeyScreen.ReadKey);

    /// <summary>
    /// Asks the yes or no <paramref name="question"/>, written in markup. One key answers: Y is yes, N is no, Enter takes
    /// <paramref name="preset"/>, and Esc is always no; other keys are ignored. The keys line is replaced by the answer.
    /// </summary>
    internal static bool Confirm(string question, bool preset, IAnsiConsole console, Func<int, ConsoleKeyInfo?> readKey)
    {
        console.MarkupLine(question);
        console.Markup($"[{TerminalStyle.Gold}]Y[/] yes   [{TerminalStyle.Gold}]N[/] no   [{TerminalStyle.Gold}]Enter[/] {(preset ? "yes" : "no")}   " +
            $"[{TerminalStyle.Gold}]Esc[/] no");
        bool? answer = null;
        while (answer is null)
        {
            if (readKey(200) is not { } key || key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) continue;
            answer = key.Key switch
            {
                ConsoleKey.Y => true,
                ConsoleKey.N or ConsoleKey.Escape => false,
                ConsoleKey.Enter => preset,
                _ => null
            };
        }
        if (console.Profile.Capabilities.Ansi) console.Write(new ControlCode("\r\u001b[2K"));
        else console.WriteLine();
        console.MarkupLine(answer.Value ? $"[{TerminalStyle.Accent}]Yes[/]" : $"[{TerminalStyle.Muted}]No[/]");
        return answer.Value;
    }

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
        _ = Task.Run(() => CultivateFiles.Automatic(settings));
        KeyScreen.Buffer(AnsiConsole.Console);
        string? highlighted = null;
        while (true)
        {
            // The status read takes most of a second, so the screen and menu are drawn first and its card is filled in when it arrives.
            var status = bridge.Status();
            var update = updateCheck.IsCompletedSuccessfully ? updateCheck.Result : null;
            var menu = HomeMenu(update, highlighted);
            var chosen = menu.Show("What would you like to do?", AnsiConsole.Console, KeyScreen.ReadKey,
                (_, _) => Home(status.IsCompletedSuccessfully ? status.Result : null, !status.IsCompletedSuccessfully, update, menu.Matches.Count), status);
            if (chosen?.Label == "Exit") return;
            var action = chosen!.Label;
            highlighted = action;
            // Nothing acts on the deployment until its status is read; a failed read stops here, as it always has. Release files never
            // touch the dev webfarm, so they neither wait for the status nor need it.
            var state = action is ReleaseScreen.Action or OnlineReleaseScreen.Action ? null :
                status.IsCompleted ? await status : await AnsiConsole.Status().StartAsync("Reading the active deployment...", _ => status);
            try
            {
                switch (action)
                {
                    case UpdateAction when update is not null:
                        if (!Confirm($"Update Cultivate {Escape(Files.Version)} to {Escape(update.Version)}?", true)) break;
                        var release = await AnsiConsole.Status().StartAsync("Checking the release...", _ => Updater.Fetch(update.Version));
                        await AnsiConsole.Status().StartAsync("Updating...", context => Updater.Install(release, message => context.Status(Escape(message))));
                        AnsiConsole.MarkupLine($"[{TerminalStyle.Accent}]Updated to Cultivate {Escape(release.Version)}.[/] Restarting...");
                        Updater.Restart();
                        break;
                    case "Update my webfarm": await Choose(state); break;
                    case "Open solution": await OpenSolution(state); break;
                    case "Review a pull request": await ReviewPullRequest(state); break;
                    case ReleaseScreen.Action: await new ReleaseScreen(settings, git).Run(); break;
                    case OnlineReleaseScreen.Action: await new OnlineReleaseScreen(settings, git).Run(); break;
                    case "Ticket workspaces": await new TicketWorkspaces(settings, git, bridge, this).Run(state); break;
                    case "Tidy worktrees": await new WorktreeTidy(settings, git).Run(state); break;
                    case "Clean up": await new CleanupScreen(settings, git, bridge).Run(); break;
                    case "Restore original webfarm files":
                        if (Confirm("Stop automatic updates and restore files from before this test?", false))
                        {
                            await AnsiConsole.Status().StartAsync("Restoring journaled originals...", _ => bridge.Call("Restore"));
                            await PullRequests.Prune(git, settings, null);
                            AnsiConsole.MarkupLine("[green]Original webfarm files restored.[/]");
                            Pause();
                        }
                        break;
                    case "Settings": new SettingsScreen(settings, state).Show(); break;
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

    /// <summary>
    /// The home screen above its menu of <paramref name="actions"/>: the banner, the workspace, the active deployment, or a placeholder
    /// while its status is <paramref name="reading"/>, and the update notice.
    /// </summary>
    internal IRenderable Home(DeploymentState? state, bool reading, UpdateInfo? update, int actions)
    {
        const string section = "DEV LAUNCHPAD", tagline = "Your code. Your workspace. Ready for the farm.";
        var workspace = TerminalStyle.Card(new Rows(
            new Markup($"[{TerminalStyle.Muted}]REPOSITORY[/]   {Escape(Path.GetFileName(Files.Full(settings.RepositoryRoot)))}"),
            new Markup($"[{TerminalStyle.Muted}]DESTINATION[/]  {Escape(settings.WebfarmRoot)}")), "CONNECTED WORKSPACE");
        var cards = new List<IRenderable>();
        if (reading)
            cards.Add(TerminalStyle.Card(new Markup($"[{TerminalStyle.Muted}]Reading the active deployment...[/]"), "ACTIVE DEPLOYMENT"));
        else if (state?.Active == true)
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
        if (update is not null)
            cards.Add(TerminalStyle.Card(new Markup(
                $"[bold {TerminalStyle.Gold}]Cultivate {Escape(update.Version)} is available[/]  [{TerminalStyle.Muted}]you have {Escape(Files.Version)}[/]\n" +
                $"[{TerminalStyle.Muted}]Choose Update Cultivate below, or run `cultivate update`.[/]"), "UPDATE AVAILABLE", TerminalStyle.Gold));
        // The banner needs room for a typical screen, an active deployment and the menu, and for this one; otherwise the compact
        // header is drawn instead of letting an update notice or a long error scroll the banner away.
        var below = TerminalStyle.Lines(workspace) + Math.Max(5 + MenuLines(HomeActions.Count), cards.Sum(TerminalStyle.Lines) + MenuLines(actions));
        var rows = new List<IRenderable> { new Banner(section, tagline, below), workspace };
        rows.AddRange(cards);
        rows.Add(new Text(""));
        return new Rows(rows);
    }

    private async Task OpenSolution(DeploymentState? state)
    {
        var (targets, others) = await AnsiConsole.Status().StartAsync("Finding worktrees and local branches...", async _ =>
        {
            var myMeyer = await git.Targets();
            return (myMeyer, await git.OtherWorktrees(myMeyer));
        });
        var picker = new VersionPicker(targets.Concat(others), state, openSolution: true);
        List<string>? roots = null;
        while (true)
        {
            var target = picker.Show();
            if (picker.OpenMainWorktree)
            {
                roots ??= await AnsiConsole.Status().StartAsync("Finding repositories' main worktrees...", _ =>
                    git.MainWorktrees(targets.Concat(others)));
                if (roots.Count == 0) throw new InvalidOperationException("No main worktrees containing a solution were found.");
                if (await OpenMainWorktree(roots)) return;
                continue;
            }
            if (target is null) return;
            if (await OpenTarget(target, others.Contains(target))) return;
        }
    }

    /// <summary>
    /// Asks which repository's main worktree to open, pulls it, and opens its solution. Returns false, having opened nothing, when Esc or
    /// Back leaves the question, so the picker shows again.
    /// </summary>
    private async Task<bool> OpenMainWorktree(List<string> roots)
    {
        var choices = roots.Select(root => new MenuAction(Path.GetFileName(root) + "  |  " + root,
            "Open this repository's main worktree solution without opening specific files.")).ToList();
        choices.Add(new("Back", "Return to the worktrees and branches without opening a solution.", Back: true));
        var menu = new ActionPicker(choices, search: true);
        while (true)
        {
            AnsiConsole.Clear();
            var choice = menu.Show("Which repository's main worktree?");
            if (choice is null || choice.Back) return false;
            var root = roots[choices.IndexOf(choice)];
            // Pulling never stops the solution from opening; a warning stays on screen until a key is pressed.
            var pull = await AnsiConsole.Status().StartAsync("Pulling the main worktree...", async context =>
            {
                var myMeyer = Files.Comparer.Equals(await git.MainWorktree(settings.RepositoryRoot), root);
                return await new BasePull(root, git, "cultivate open").CheckedOut(myMeyer ? settings.BaseBranch : null,
                    message => context.Status(Escape(message) + "..."));
            });
            AnsiConsole.MarkupLine($"[{(pull.Warning ? TerminalStyle.Gold : TerminalStyle.Muted)}]{Escape(pull.Detail)}[/]");
            if (SelectSolution(root) is not { } mainSolution) continue;
            var ide = await Settings.FindVisualStudio()
                ?? throw new InvalidOperationException("Visual Studio IDE was not found. Install Visual Studio to open the solution.");
            await Open(ide, mainSolution, [], "Opening the main worktree solution...", pull.Warning);
            return true;
        }
    }

    /// <summary>
    /// Opens a checkout's solution with its changed source files. A MyMeyer checkout opens the files of its deployment plan;
    /// one from another repository has no plan, so <paramref name="otherRepository"/> opens every changed source file under its solution.
    /// Returns false, having opened nothing, when Esc or Back leaves the choice of solution.
    /// </summary>
    internal async Task<bool> OpenTarget(Target target, bool otherRepository)
    {
        var visualStudio = await Settings.FindVisualStudio()
            ?? throw new InvalidOperationException("Visual Studio IDE was not found. Install Visual Studio to open the solution and its changed files.");
        string solution;
        List<string> files;
        if (otherRepository)
        {
            // Another repository has no deployment plan, so every changed source file under its solution opens.
            var root = target.Worktree!;
            if (SelectSolution(root) is not { } chosen) return false;
            solution = chosen;
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
        await Open(visualStudio, solution, files, "Opening the solution and changed source files...");
        return true;
    }

    /// <summary>Fills in the solution's missing ignored references first, so Visual Studio loads its projects with them resolved.
    /// <paramref name="pause"/> waits for a key afterwards even without warnings, so one shown before stays readable.</summary>
    private async Task Open(string visualStudio, string solution, List<string> files, string status, bool pause = false)
    {
        SolutionDependencies.Result prepared;
        try
        {
            prepared = await AnsiConsole.Status().StartAsync("Preparing the solution's dependencies...", context =>
                SolutionDependencies.Prepare(git, settings.MSBuild, solution, message => context.Status(Escape(message) + "...")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Missing references only cost IntelliSense, so they never stop the solution from opening.
            prepared = new(0, 0, 0, ["Could not prepare the solution's dependencies: " + exception.Message]);
        }
        await AnsiConsole.Status().StartAsync(status, _ => SolutionOpener.Open(visualStudio, solution, files));
        if (prepared.Warnings.Count == 0)
        {
            if (pause) Pause();
            return;
        }
        if (prepared.Summary is { } summary) AnsiConsole.MarkupLine($"[{TerminalStyle.Muted}]{Escape(summary)}[/]");
        foreach (var warning in prepared.Warnings) AnsiConsole.MarkupLine($"[{TerminalStyle.Gold}]{Escape(warning)}[/]");
        Pause();
    }

    /// <summary>The solution to open in <paramref name="root"/>, asking when it has several; null when Esc or Back leaves the question.</summary>
    private static string? SelectSolution(string root)
    {
        var solutions = SolutionOpener.Solutions(root);
        if (solutions.Count == 0) throw new InvalidOperationException($"No solution was found in {root}.");
        if (solutions.Count == 1) return solutions[0];
        var choices = solutions.Select(path => new MenuAction(Path.GetRelativePath(root, path), path)).ToList();
        choices.Add(new("Back", "Return without opening a solution.", Back: true));
        var choice = new ActionPicker(choices).Show($"[{TerminalStyle.Accent}]Which solution in {Escape(Path.GetFileName(root))}?[/]");
        return choice is null || choice.Back ? null : solutions[choices.IndexOf(choice)];
    }

    private async Task Choose(DeploymentState? state)
    {
        var targets = await AnsiConsole.Status().StartAsync("Finding worktrees and cached branches...", _ => git.Targets());
        var picker = new VersionPicker(targets, state);
        await Previews(() => Task.FromResult(picker.Show()), target => Preview(target, state));
    }

    private async Task ReviewPullRequest(DeploymentState? state)
    {
        var (open, problem) = await AnsiConsole.Status().StartAsync("Listing open pull requests with the GitHub CLI...", _ =>
            PullRequests.Open(PullRequests.GitHub(settings)));
        var picker = new PullRequestPicker(open, problem, state);
        await Previews(async () => picker.Show() is { } number
            ? await AnsiConsole.Status().StartAsync($"Fetching pull request #{number} from origin...", _ => PullRequests.Fetch(settings, git, number, state))
            : null, target => Preview(target, state));
    }

    /// <summary>
    /// A list and the previews it opens: going back from a preview shows the same list again, as it was, and a preview that has finished,
    /// by deploying or after an error, ends the loop. The list gives null when Esc leaves it.
    /// </summary>
    internal static async Task Previews(Func<Task<Target?>> list, Func<Target, Task<bool>> preview)
    {
        while (await list() is { } target)
            if (await preview(target)) return;
    }

    /// <summary>
    /// The deployment preview of one MyMeyer version, through to its activation. Returns false when Back or Esc leaves it without deploying,
    /// and true once it has finished: after a deployment, or after an activation error.
    /// </summary>
    internal async Task<bool> Preview(Target target, DeploymentState? state)
    {
        var planner = new Planner(settings, git, bridge);
        var excluded = state?.Active == true && state.SourceRef == target.Ref &&
            Files.Comparer.Equals(state.SourceRoot, target.Worktree ?? "") ? state.ExcludedPaths : [];
        var libraries = LibraryLoop.Kept(state, target);
        var plan = await AnsiConsole.Status().StartAsync("Inspecting changed files, frontend mappings, and owning projects...", _ =>
            planner.Create(target, excludedPaths: excluded, libraries: libraries));
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(TerminalStyle.Header("DEPLOYMENT PREVIEW", "Inspect the plan. You are still in preview mode."));
            AnsiConsole.Write(TerminalStyle.Card(new Markup(
                $"[bold {TerminalStyle.Accent}]{Escape(target.Branch)}[/]\n" +
                $"[{TerminalStyle.Muted}]SOURCE[/]       {Escape(target.Worktree ?? target.PullRequest?.Source ?? target.Ref)}\n" +
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
            if (plan.Libraries.Count != 0) AnsiConsole.Write(LibraryLoop.Card(plan));
            foreach (var note in plan.Notes) AnsiConsole.MarkupLine($"[grey]{Escape(note)}[/]");
            foreach (var error in plan.Errors) AnsiConsole.MarkupLine($"[red]{Escape(error)}[/]");
            var action = Select("[bold]Preview only[/] - choose an action", PreviewActions(plan.CanDeploy, selectable.Count != 0, plan.Libraries.Count != 0));
            if (action == "Back") return false;
            if (action == LibraryLoop.ChooseAction)
            {
                if (LibraryLoop.Choose(plan) is not { } chosen) continue;
                libraries = chosen;
                plan = await AnsiConsole.Status().StartAsync("Updating library changes and owning projects...", _ =>
                    planner.Create(target, excludedPaths: excluded, libraries: libraries));
                continue;
            }
            if (action == "Choose files to deploy")
            {
                var checklist = new Checklist<string>(TerminalStyle.Header("SELECT DEPLOYMENT FILES",
                    "Checked files deploy. Unchecked files stay excluded from automatic updates."), "Choose files to deploy", selectable, path => path,
                    selectable.Where(path => !IsExcluded(plan, path)));
                if (checklist.Show() is not { } checkedFiles) continue;
                var included = checkedFiles.ToHashSet(Files.Comparer);
                excluded = plan.ExcludedPaths.Where(path => !selectable.Contains(path, Files.Comparer))
                    .Concat(selectable.Where(path => !included.Contains(path))).ToList();
                plan = await AnsiConsole.Status().StartAsync("Updating selected files and owning projects...", _ =>
                    planner.Create(target, excludedPaths: excluded, libraries: libraries));
                continue;
            }
            if (action == "Show every changed file")
            {
                foreach (var change in plan.Changes) AnsiConsole.MarkupLine(Escape((IsExcluded(plan, change.Path) ? "EXCLUDED " : "") +
                    (change.Deleted ? "DELETE " : "") + change.Path));
                Pause();
                continue;
            }
            if (!Confirm($"Deploy[{TerminalStyle.Accent}]{Escape(target.Branch)}[/] to [{TerminalStyle.Gold}]{Escape(settings.WebfarmRoot)}[/]?", true)) continue;
            if (!StopLegacyWatcher()) continue;
            var watch = action == "Activate and automatically update saved changes";
            var screen = new WarmupScreen(Warmup.SnakePath);
            DeploymentState activated;
            try { activated = Warmup.Activate(settings, screen, report => new Pipeline(settings, git, bridge).Activate(plan, report, watch)); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
                System.Xml.XmlException or System.Text.Json.JsonException)
            {
                var report = ShowError(exception, "Activation");
                if (SkipOffer.Of(plan, report) is not { } skip || Select(skip.Title, SkipOffer.Actions) != SkipOffer.Accept)
                {
                    Pause();
                    return true;
                }
                excluded = skip.Exclusions;
                var without = await AnsiConsole.Status().StartAsync($"Leaving {Escape(skip.Project.Assembly)} out and updating the plan...", _ =>
                    skip.Plan(planner, target, libraries));
                if (without is null)
                {
                    AnsiConsole.MarkupLine($"[red]Leaving out {Escape(skip.Project.RelativePath)}'s changed code did not remove it from the plan, so nothing was changed.[/]");
                    Pause();
                    return true;
                }
                plan = without;
                continue;
            }
            AnsiConsole.Write(TerminalStyle.Card(new Markup($"[bold {TerminalStyle.Accent}]VERSION ACTIVATED[/]\nRefresh your development site to use the selected version."),
                "DEPLOYMENT COMPLETE", TerminalStyle.Accent));
            AnsiConsole.Write(UpdatedFiles(activated, settings.WebfarmRoot));
            AnsiConsole.MarkupLine(watch ? "[grey]Saved changes update automatically after successful builds; closing this menu keeps updates running.[/]" :
                "[grey]This is a one-time deployment. Later saves will not be published.[/]");
            await Warmup.AfterActivation(settings, activated, screen);
            Pause();
            return true;
        }
    }

    /// <summary>Stops the old watcher that would block this activation, asking first unless the setting says not to; false to cancel.</summary>
    private bool StopLegacyWatcher()
    {
        var running = LegacyWatcher.Running(settings.WebfarmRoot);
        var action = LegacyWatcher.Decide(running.Count, settings.StopLegacyWatcher, canAsk: true);
        if (action == LegacyWatcherAction.None) return true;
        if (action == LegacyWatcherAction.Ask)
        {
            var ids = string.Join(", ", running.Select(process => process.Id));
            var choice = Select($"The old [{TerminalStyle.Gold}]WebfarmFileSync[/] watcher is running and could overwrite this deployment", [
                new("Stop it and continue", $"End WebfarmFileSync (process {ids}) and deploy. Cultivate asks again next time."),
                new("Always stop it without asking", "Save this choice, then stop it now, at later activations, and before automatic updates."),
                new("Cancel", "Return to the preview without deploying or stopping anything.", Back: true)]);
            if (choice == "Cancel") return false;
            if (choice == "Always stop it without asking")
            {
                settings.StopLegacyWatcher = true;
                settings.Save();
            }
        }
        AnsiConsole.MarkupLine($"[{TerminalStyle.Muted}]{Escape(LegacyWatcher.Stop(running))}[/]");
        return true;
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

    // Lines below the cards on the home screen: a blank line, the menu title, its actions, the highlighted help, the keys, the cursor.
    private static int MenuLines(int actions) => actions + 5;
    // Pause's blank line and prompt, plus the cursor.
    private const int PauseLines = 3;

    internal static void Pause()
    {
        AnsiConsole.MarkupLine($"\n[{TerminalStyle.Gold}]Press any key[/] [{TerminalStyle.Muted}]to continue[/]");
        Console.ReadKey(true);
    }
    internal static FailureReport ShowError(Exception exception, string stage = "Operation")
    {
        var report = Diagnostics.Record(exception, stage);
        AnsiConsole.Write(TerminalStyle.Card(new Text(report.Text), report.Label, "#F0A3A3"));
        return report;
    }

}

/// <summary>
/// After one changed project fails to compile, the offer to deploy the rest of the plan without it: the changed compiled files it owns
/// are excluded the way Choose files to deploy excludes them, and the developer confirms the new plan in the preview.
/// </summary>
internal sealed record SkipOffer(ProjectInfo Project, List<string> Exclusions, int CodeFiles, bool Unchanged)
{
    internal const string Accept = "Leave it out and preview again";
    internal static IReadOnlyList<MenuAction> Actions { get; } =
    [
        new("No", "Leave the preview without deploying anything.", Back: true),
        new(Accept, "Exclude its changed code files and show the new plan; nothing deploys until you confirm there.")
    ];

    /// <summary>The offer for <paramref name="report"/>, or null unless one project of <paramref name="plan"/> failed to compile and
    /// leaving it out still deploys something.</summary>
    internal static SkipOffer? Of(Plan plan, FailureReport report) =>
        report.Context.SkipExclusions is { Count: > 0 } exclusions &&
        plan.Projects.FirstOrDefault(project => Files.Comparer.Equals(project.RelativePath, report.Context.Project)) is { } failed
            ? new(failed, exclusions.ToList(), exclusions.Count(path => !plan.ExcludedPaths.Contains(path, Files.Comparer)), report.Unchanged)
            : null;

    internal string Title
    {
        get
        {
            var name = Terminal.Escape(Project.Assembly);
            return $"[bold]Deploy the rest without {name}?[/]{(Unchanged ? $" [{TerminalStyle.Gold}]Its errors are not from your changes.[/]" : "")}\n" +
                $"[{TerminalStyle.Muted}]Its {CodeFiles} changed code file(s) are left out of this deployment and automatic updates, and {name}.dll " +
                "on the webfarm stays as it is. Its changed frontend files still deploy, so their markup could run against that old DLL.[/]";
        }
    }

    /// <summary>The plan without the project, or null when excluding its files did not take it out of the plan.</summary>
    internal async Task<Plan?> Plan(Planner planner, Target target, IEnumerable<string> libraries,
        Func<string, Task<Dictionary<string, string>>>? mapping = null)
    {
        var plan = await planner.Create(target, mapping, Exclusions, libraries);
        return plan.Projects.Any(project => Files.Comparer.Equals(project.RelativePath, Project.RelativePath)) ? null : plan;
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
