using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace Cultivate;

/// <summary>One ticket's or one branch's checkouts across every repository.</summary>
internal sealed record TicketWorkspace(string Key, string? Ticket, IReadOnlyList<Checkout> Checkouts, DateTimeOffset? LastActivity)
{
    public IReadOnlyList<string> Repositories => Checkouts.Select(checkout => checkout.Repository).Distinct(Files.Comparer).ToList();
}

/// <summary>A workspace checkout's full state, read when the workspace is opened. <see cref="Error"/> is set when it could not be read.</summary>
internal sealed record TicketCheckout(Checkout Checkout, CheckoutStatus? Status, string? Error, bool MyMeyer, IReadOnlyList<string> Solutions,
    IReadOnlyList<TicketPullRequest> PullRequests)
{
    public TicketPullRequest? PullRequest => TicketPullRequests.Best(PullRequests);
}

internal sealed record TicketWorkspaceDetails(TicketWorkspace Workspace, IReadOnlyList<TicketCheckout> Checkouts, TicketSqlComparison? Sql)
{
    /// <summary>The title of the workspace's pull request, preferring an open one; ticket titles are not available locally.</summary>
    public string? Title => TicketPullRequests.Best(Checkouts.SelectMany(checkout => checkout.PullRequests))?.Title;

    /// <summary>Each checkout's shown pull request, once each.</summary>
    public IReadOnlyList<string> PullRequestUrls =>
        Checkouts.Select(checkout => checkout.PullRequest?.Url).OfType<string>().Where(url => url.Length != 0).Distinct().ToList();
}

/// <summary>Groups every repository's checkouts by ticket, so one ticket's work is seen and opened together.</summary>
internal sealed class TicketWorkspaces(Settings settings, GitRepository git, Bridge bridge, Terminal terminal)
{
    private const int Parallelism = 8;
    private static readonly HashSet<string> Mainlines = new(["main", "master", "development", "develop"], StringComparer.Ordinal);
    internal const string OpenAll = "Open all solutions", OpenOne = "Open one solution", PreviewDeployment = "Preview webfarm deployment",
        OpenTicket = "Open ticket page", OpenPullRequests = "Open pull requests", OpenSqlFolder = "Open SQL files folder", Refresh = "Refresh", Back = "Back";

    /// <summary>The home screen's Ticket workspaces action.</summary>
    internal async Task Run(DeploymentState? state)
    {
        var workspaces = await AnsiConsole.Status().StartAsync("Finding every repository's worktrees...", _ => Load(settings, git));
        var picker = new TicketPicker(workspaces, state);
        while (picker.Show() is { } workspace)
        {
            try { state = await Open(workspace, state); }
            catch (Exception exception) when (Recoverable(exception))
            {
                Terminal.ShowError(exception, "Ticket workspace");
                Terminal.Pause();
            }
            picker.Active = state;
        }
    }

    private async Task<DeploymentState?> Open(TicketWorkspace workspace, DeploymentState? state)
    {
        var myMeyer = await MyMeyerRoot(settings, git);
        while (true)
        {
            var details = await AnsiConsole.Status().StartAsync("Reading each checkout's changes, pull requests, and SQL files...", _ =>
                Inspect(settings, git, workspace, myMeyer, compareSql: true, TicketPullRequests.GitHub));
            while (true)
            {
                var actions = Actions(details, settings);
                var height = AnsiConsole.Console.Profile.Height;
                AnsiConsole.Clear();
                AnsiConsole.Write(TicketWorkspaceView.Render(details, AnsiConsole.Console.Profile.Width, height - TicketWorkspaceView.MenuLines(actions.Count), state));
                var action = Terminal.Select("Choose an action", actions);
                if (action == Back) return state;
                if (action == Refresh) break;
                try
                {
                    switch (action)
                    {
                        case OpenAll: await OpenSolutions(details.Checkouts); break;
                        case OpenOne:
                            var withSolutions = details.Checkouts.Where(checkout => checkout.Solutions.Count != 0).ToList();
                            if (Choose("Which checkout's solution?", withSolutions, "Return without opening a solution.") is { } one)
                                await OpenSolutions([one]);
                            break;
                        case PreviewDeployment:
                            var candidates = details.Checkouts.Where(checkout => checkout.MyMeyer && checkout.Error is null).ToList();
                            if (Choose("Which MyMeyer checkout?", candidates, "Return without a preview.") is { } source)
                            {
                                await terminal.Preview(source.Checkout.ToTarget(), state);
                                try { state = await bridge.Status(); }
                                catch (Exception exception) when (Recoverable(exception)) { }
                            }
                            break;
                        case OpenTicket: Launch(string.Format(settings.TicketUrl, workspace.Ticket)); break;
                        case OpenPullRequests:
                            foreach (var url in details.PullRequestUrls)
                                Launch(url);
                            break;
                        case OpenSqlFolder: Launch(details.Sql!.Folder, explorer: true); break;
                    }
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    Terminal.ShowError(exception, action);
                    Terminal.Pause();
                }
            }
        }
    }

    /// <summary>The MyMeyer repository's primary checkout; its checkouts open with their deployment plan's files.</summary>
    private static async Task<string?> MyMeyerRoot(Settings settings, GitRepository git)
    {
        try { return await git.MainWorktree(settings.RepositoryRoot); }
        catch (InvalidOperationException) { return null; }
    }

    private static TicketCheckout? Choose(string title, List<TicketCheckout> checkouts, string back)
    {
        if (checkouts.Count <= 1) return checkouts.FirstOrDefault();
        var choices = checkouts.Select(checkout => new MenuAction(checkout.Checkout.Repository + "  |  " + checkout.Checkout.Branch,
            checkout.Checkout.Path)).Append(new(Back, back)).ToList();
        var label = Terminal.Select(title, choices);
        return label == Back ? null : checkouts[choices.FindIndex(choice => choice.Label == label)];
    }

    /// <summary>Opens each checkout's solution in turn; one that fails is reported and the rest still open.</summary>
    private async Task OpenSolutions(IReadOnlyList<TicketCheckout> checkouts)
    {
        var notes = new List<string>();
        var opened = 0;
        foreach (var checkout in checkouts)
        {
            var name = $"{checkout.Checkout.Repository} ({checkout.Checkout.Branch})";
            if (checkout.Solutions.Count == 0)
            {
                notes.Add($"[{TerminalStyle.Muted}]Skipped {Terminal.Escape(name)}: it has no solution.[/]");
                continue;
            }
            try
            {
                await terminal.OpenTarget(checkout.Checkout.ToTarget(), !checkout.MyMeyer);
                notes.Add($"[{TerminalStyle.Accent}]Opened {Terminal.Escape(name)}.[/]");
                opened++;
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                notes.Add($"[red]Could not open {Terminal.Escape(name)}:[/] {Terminal.Escape(FirstLine(exception.Message))}");
            }
        }
        if (checkouts.Count == 1 && opened == 1)
            return;

        foreach (var note in notes) AnsiConsole.MarkupLine(note);
        Terminal.Pause();
    }

    private static void Launch(string target, bool explorer = false)
    {
        var start = explorer ? new ProcessStartInfo("explorer.exe") { ArgumentList = { target } } : new ProcessStartInfo(target) { UseShellExecute = true };
        using var _ = Process.Start(start);
    }

    internal static bool Recoverable(Exception exception) => exception is IOException or InvalidOperationException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or JsonException or System.Xml.XmlException or TimeoutException or FormatException;

    internal static string FirstLine(string message) =>
        message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>The actions a workspace offers; each is shown only when it can do something.</summary>
    internal static List<MenuAction> Actions(TicketWorkspaceDetails details, Settings settings)
    {
        var actions = new List<MenuAction>();
        var solutions = details.Checkouts.Count(checkout => checkout.Solutions.Count != 0);
        if (solutions != 0)
        {
            var skipped = details.Checkouts.Count - solutions;
            actions.Add(new(OpenAll, $"Open {Count(solutions, "solution")} in Visual Studio with changed files" +
                (skipped == 0 ? "." : $"; {Count(skipped, "checkout")} without one {(skipped == 1 ? "is" : "are")} skipped.")));
        }
        if (solutions > 1) actions.Add(new(OpenOne, "Choose one checkout and open its solution with its changed files."));
        if (details.Checkouts.Any(checkout => checkout.MyMeyer && checkout.Error is null))
            actions.Add(new(PreviewDeployment, "Preview deploying the MyMeyer checkout. Nothing deploys until you confirm."));
        if (details.Workspace.Ticket is not null && settings.TicketUrl.Contains("{0}", StringComparison.Ordinal))
            actions.Add(new(OpenTicket, $"Open ticket {details.Workspace.Ticket} in your browser."));
        var pullRequests = details.PullRequestUrls.Count;
        if (pullRequests != 0) actions.Add(new(OpenPullRequests, $"Open {Count(pullRequests, "pull request")} in your browser."));
        if (details.Sql is { FolderExists: true }) actions.Add(new(OpenSqlFolder, $"Open this {(details.Workspace.Ticket is null ? "branch" : "ticket")}'s SQL files folder in Explorer."));
        actions.Add(new(Refresh, "Read the checkouts, pull requests, and SQL files again."));
        actions.Add(new(Back, "Return to the list of workspaces."));
        return actions;
    }

    internal static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// Discovers every checkout and groups them, reading only what is cheap: one Git call per repository for its branches' last
    /// commit dates and origin's default branch, a few repositories at a time. Full status waits until a workspace is opened.
    /// </summary>
    internal static async Task<List<TicketWorkspace>> Load(Settings settings, GitRepository git)
    {
        var checkouts = await WorktreeCatalog.Discover(settings, git);
        using var gate = new SemaphoreSlim(Parallelism);
        var repositories = await Task.WhenAll(checkouts.GroupBy(checkout => checkout.RepositoryRoot, Files.Comparer).Select(async repository =>
        {
            // A repository whose checkouts are all detached or on a mainline holds no workspace whatever its default branch: no Git start.
            if (!repository.Any(MayHoldWork)) return (Root: repository.Key, DefaultBranch: (string?)null, Dates: new Dictionary<string, DateTimeOffset>());
            await gate.WaitAsync();
            try
            {
                var root = repository.First().Path;
                string? defaultBranch = null;
                var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                try
                {
                    var refs = await git.Git(root, "for-each-ref", "--format=%(refname)%00%(committerdate:iso-strict)%00%(symref)",
                        "refs/heads", "refs/remotes/origin/HEAD");
                    foreach (var fields in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\0')))
                    {
                        if (fields.Length != 3)
                            continue;

                        if (fields[0] == "refs/remotes/origin/HEAD" && fields[2].StartsWith("refs/remotes/", StringComparison.Ordinal))
                            defaultBranch = fields[2]["refs/remotes/".Length..];
                        else if (fields[0].StartsWith("refs/heads/", StringComparison.Ordinal) && DateTimeOffset.TryParse(fields[1], out var date))
                            dates[fields[0]["refs/heads/".Length..]] = date;
                    }
                }
                catch (InvalidOperationException) { }
                if (defaultBranch is null)
                {
                    try { defaultBranch = await git.DefaultBranch(root); }
                    catch (InvalidOperationException) { }
                }
                return (Root: repository.Key, DefaultBranch: defaultBranch, Dates: dates);
            }
            finally { gate.Release(); }
        }));
        var facts = repositories.ToDictionary(repository => repository.Root, Files.Comparer);
        return Group(checkouts, root => facts[root].DefaultBranch, checkout => facts[checkout.RepositoryRoot].Dates.TryGetValue(checkout.Branch, out var date) ? date : null);
    }

    private static bool MayHoldWork(Checkout checkout) => !checkout.Detached && checkout.Branch.Length != 0 && !Mainlines.Contains(checkout.Branch);

    /// <summary>
    /// A ticket's follow-up branches (12345-2) join its workspace, and other branches join the same-named branch of every repository.
    /// Detached checkouts and checkouts on their repository's default branch hold no ticket's work, and neither do the mainline
    /// branches some repositories keep beside a different default (MeyerOnline's primary checkout sits on master while origin's default is development).
    /// </summary>
    internal static List<TicketWorkspace> Group(IEnumerable<Checkout> checkouts, Func<string, string?> defaultBranch, Func<Checkout, DateTimeOffset?> lastCommit)
    {
        static string Local(string? branch) => branch is null ? "" : branch.StartsWith("origin/", StringComparison.Ordinal) ? branch["origin/".Length..] : branch;
        return checkouts.Where(checkout => MayHoldWork(checkout) && checkout.Branch != Local(defaultBranch(checkout.RepositoryRoot)))
            .GroupBy(checkout => checkout.Ticket ?? checkout.Branch, StringComparer.Ordinal)
            .Select(group => new TicketWorkspace(group.Key, group.First().Ticket,
                group.OrderBy(checkout => checkout.Repository, Files.Comparer).ThenBy(checkout => checkout.Branch, StringComparer.Ordinal).ToList(),
                group.Select(lastCommit).Max()))
            .OrderByDescending(workspace => workspace.LastActivity ?? DateTimeOffset.MinValue).ThenBy(workspace => workspace.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Reads each checkout's status, solutions, and pull requests, and compares its changed SQL files with the SQL files folder.
    /// A checkout that cannot be read gets an error of its own; the others are still read.
    /// </summary>
    internal static async Task<TicketWorkspaceDetails> Inspect(Settings settings, GitRepository git, TicketWorkspace workspace, string? myMeyerRoot,
        bool compareSql, Func<Checkout, Task<string>>? pullRequests, SemaphoreSlim? gate = null)
    {
        using var own = gate is null ? new SemaphoreSlim(Parallelism) : null;
        gate ??= own!;
        var compare = compareSql && settings.SqlMirrorRoot.Length != 0;
        var results = await Task.WhenAll(workspace.Checkouts.Select(async checkout =>
        {
            await gate.WaitAsync();
            try { return await Read(checkout); }
            finally { gate.Release(); }
        }));

        async Task<(TicketCheckout Checkout, List<(string, string, string)> Sql)> Read(Checkout checkout)
        {
            var myMeyer = myMeyerRoot is not null && Files.Comparer.Equals(checkout.RepositoryRoot, myMeyerRoot);
            var prs = pullRequests is null ? Task.FromResult(new List<TicketPullRequest>()) : TicketPullRequests.Find(pullRequests, checkout);
            if (!Directory.Exists(checkout.Path))
                return (new(checkout, null, "The worktree folder is missing.", myMeyer, [], await prs), []);
            var status = Attempt(() => WorktreeCatalog.Status(git, checkout));
            var sql = compare ? Attempt(async () => (await git.Inspect(checkout.ToTarget(), await git.DefaultBranch(checkout.Path))).Changes
                .Where(change => !change.Deleted && change.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .Select(change => (checkout.Repository, change.Path, Path.Combine(checkout.Path, change.Path.Replace('/', '\\')))).ToList())
                : Task.FromResult<(List<(string, string, string)>?, string?)>(([], null));
            List<string> solutions;
            try { solutions = SolutionOpener.Solutions(checkout.Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { solutions = []; }
            var (state, statusError) = await status;
            var (changed, sqlError) = await sql;
            return (new(checkout, state, statusError ?? sqlError, myMeyer, solutions, await prs), changed ?? []);
        }

        TicketSqlComparison? comparison = null;
        if (compare)
        {
            try { comparison = TicketSql.Compare(Path.Combine(settings.SqlMirrorRoot, workspace.Key), results.SelectMany(result => result.Sql)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new(workspace, results.Select(result => result.Checkout).ToList(), comparison);
    }

    private static async Task<(T? Value, string? Error)> Attempt<T>(Func<Task<T>> read) where T : class
    {
        try { return (await read(), null); }
        catch (Exception exception) when (Recoverable(exception)) { return (null, FirstLine(exception.Message)); }
    }

    /// <summary><c>cultivate tickets [--ticket KEY] [--prs]</c></summary>
    internal static async Task<int> Command(Settings settings, GitRepository git, string[] args) =>
        await Command(settings, git, args, TicketPullRequests.GitHub, Console.Out);

    internal static async Task<int> Command(Settings settings, GitRepository git, string[] args, Func<Checkout, Task<string>> pullRequestLookup, TextWriter output)
    {
        var index = Array.IndexOf(args, "--ticket");
        var key = index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new InvalidOperationException("--ticket needs a ticket number or branch name.");
        var lookup = args.Contains("--prs") ? pullRequestLookup : null;
        var workspaces = await Load(settings, git);
        var myMeyer = await MyMeyerRoot(settings, git);
        if (key is not null)
        {
            var workspace = workspaces.FirstOrDefault(item => item.Key == key) ?? workspaces.FirstOrDefault(item => Files.Comparer.Equals(item.Key, key))
                ?? throw new InvalidOperationException($"No ticket or branch workspace named '{key}' was found. Run `cultivate tickets` to list them.");
            await output.WriteLineAsync(JsonSerializer.Serialize(Json(await Inspect(settings, git, workspace, myMeyer, compareSql: true, lookup), lookup is not null, sql: true), Files.Json));
            return 0;
        }
        using var gate = new SemaphoreSlim(Parallelism);
        var details = await Task.WhenAll(workspaces.Select(workspace => Inspect(settings, git, workspace, myMeyer, compareSql: false, lookup, gate)));
        await output.WriteLineAsync(JsonSerializer.Serialize(details.Select(item => Json(item, lookup is not null, sql: false)), Files.Json));
        return 0;
    }

    private static object Json(TicketWorkspaceDetails details, bool pullRequests, bool sql) => new
    {
        details.Workspace.Key,
        details.Workspace.Ticket,
        details.Workspace.LastActivity,
        details.Workspace.Repositories,
        Checkouts = details.Checkouts.Select(checkout => new
        {
            checkout.Checkout.Repository,
            checkout.Checkout.Branch,
            checkout.Checkout.Path,
            checkout.Checkout.Primary,
            checkout.Status,
            checkout.Error,
            checkout.Solutions,
            PullRequests = pullRequests ? checkout.PullRequests.Select(pullRequest => new
            {
                pullRequest.Number, pullRequest.Title, pullRequest.State, pullRequest.IsDraft, pullRequest.Url,
                Checks = pullRequest.Checks.ToString(), pullRequest.Summary
            }) : null
        }),
        Sql = sql ? details.Sql : null
    };
}
