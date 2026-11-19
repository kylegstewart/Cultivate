using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    private const string PullRequestJson = """
        [
          {"number":2763,"title":"12345 - Restrict [Will Call] items","state":"OPEN","isDraft":true,"url":"https://example.invalid/pull/2763",
           "statusCheckRollup":[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"},{"__typename":"StatusContext","state":"SUCCESS"}]},
          {"number":101,"title":"Earlier attempt","state":"MERGED","isDraft":false,"url":"https://example.invalid/pull/101",
           "statusCheckRollup":[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE"}]}
        ]
        """;

    private static async Task CheckTicketWorkspaces(string home)
    {
        CheckTicketPullRequests();
        CheckTicketSql(home);
        CheckTicketGrouping();
        CheckTicketPicker();
        CheckTicketWorkspaceView();
        await CheckTicketFixtures(home);
    }

    private static void CheckTicketPullRequests()
    {
        var parsed = TicketPullRequests.Parse(PullRequestJson);
        Check(parsed.Count == 2 && parsed[0] is { Number: 2763, State: "OPEN", IsDraft: true, Checks: TicketChecks.Passing } &&
            parsed[0].Title == "12345 - Restrict [Will Call] items" && parsed[1] is { Number: 101, State: "MERGED", Checks: TicketChecks.Failing },
            "gh pull request JSON parses into numbers, titles, states, drafts, and check results");
        Check(parsed[0].Summary == "#2763 draft, checks passing" && parsed[1].Summary == "#101 merged",
            "pull request summaries name draft and merged pull requests, and checks only while open");
        var more = TicketPullRequests.Parse("""
            [{"number":5,"title":"a","state":"OPEN","isDraft":false,"url":"u5","statusCheckRollup":[{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"COMPLETED","conclusion":"TIMED_OUT"}]},
             {"number":6,"title":"b","state":"OPEN","isDraft":false,"url":"u6","statusCheckRollup":[{"status":"IN_PROGRESS","conclusion":""},{"status":"COMPLETED","conclusion":"SUCCESS"}]},
             {"number":7,"title":"c","state":"OPEN","isDraft":false,"url":"u7","statusCheckRollup":[{"__typename":"StatusContext","state":"PENDING"}]},
             {"number":8,"title":"d","state":"CLOSED","isDraft":false,"url":"u8","statusCheckRollup":[]},
             {"number":9,"title":"e","state":"OPEN","isDraft":false,"url":"u9","statusCheckRollup":[]},
             {"number":10,"title":"f","state":"OPEN","isDraft":false,"url":"u10","statusCheckRollup":[{"__typename":"StatusContext","state":"ERROR"}]}]
            """);
        Check(more.Select(pullRequest => pullRequest.Summary).SequenceEqual(["#5 open, checks failing", "#6 open, checks pending",
            "#7 open, checks pending", "#8 closed", "#9 open", "#10 open, checks failing"]),
            "check runs and commit statuses summarise as passing, failing, or pending, and no checks says nothing");
        Check(TicketPullRequests.Best(parsed)?.Number == 2763 && TicketPullRequests.Best([parsed[1], more[3]])?.Number == 101 &&
            TicketPullRequests.Best([]) is null, "an open pull request is shown before merged and closed ones");
        Check(TicketPullRequests.Parse("[]").Count == 0 && TicketPullRequests.Parse("{}").Count == 0, "no pull requests parse to an empty list");
        var checkout = new Checkout("Alpha", @"C:\fixture\Alpha", @"C:\fixture\Alpha", "12345", "abc", true, false, false);
        Check(TicketPullRequests.Find(_ => throw new System.ComponentModel.Win32Exception("gh.exe was not found"), checkout).Result.Count == 0 &&
            TicketPullRequests.Find(_ => Task.FromException<string>(new InvalidOperationException("gh: not logged in")), checkout).Result.Count == 0 &&
            TicketPullRequests.Find(_ => Task.FromResult("not json"), checkout).Result.Count == 0,
            "a missing GitHub CLI, a failed sign-in, or unreadable output means no pull request data instead of an error");
    }

    private static void CheckTicketSql(string home)
    {
        var root = Path.Combine(home, "ticket sql");
        var source = Path.Combine(root, "source", "Sql", "StoredProcedures");
        var folder = Path.Combine(root, "SPs", "12345");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(source, "Same.sql"), "SELECT 1\r\nFROM Example\r\n");
        File.WriteAllText(Path.Combine(folder, "same.SQL"), "SELECT 1\nFROM Example\n");
        File.WriteAllText(Path.Combine(source, "Changed.sql"), "SELECT 2\r\n");
        File.WriteAllText(Path.Combine(folder, "nested", "Changed.sql"), "SELECT 3\r\n");
        File.WriteAllText(Path.Combine(source, "New.sql"), "SELECT 4\r\n");
        File.WriteAllText(Path.Combine(folder, "AlterTableExample.sql"), "ALTER TABLE Example ADD Flag BIT\r\n");
        var changed = new[] { "Same.sql", "Changed.sql", "New.sql" }
            .Select(name => ("Database--SQL", "Sql/StoredProcedures/" + name, Path.Combine(source, name))).ToList();
        var comparison = TicketSql.Compare(folder, changed);
        Check(comparison.FolderExists && comparison.Identical.Single() is { Name: "Same.sql", Repository: "Database--SQL", CopyPath: "same.SQL" },
            "SQL files whose copy differs only in line endings are identical, matched by name in any case");
        Check(comparison.Different.Single() is { Name: "Changed.sql" } && comparison.Different[0].CopyPath == Path.Combine("nested", "Changed.sql"),
            "a SQL file whose copy anywhere under the folder has other content is different");
        Check(comparison.Missing.Single() is { Name: "New.sql", CopyPath: null }, "a changed SQL file with no copy is missing from the folder");
        Check(comparison.OnlyInFolder.SequenceEqual(["AlterTableExample.sql"]) && comparison.Changed == 3,
            "files only in the SQL files folder are listed without counting as changed files");
        var absent = TicketSql.Compare(Path.Combine(root, "SPs", "99999"), changed);
        Check(!absent.FolderExists && absent.Missing.Count == 3 && absent.OnlyInFolder.Count == 0,
            "without a SQL files folder every changed SQL file is missing");
        Check(TicketWorkspaceView.SqlLines(absent, "ticket", 200)[0].Contains("No SQL files folder for this ticket") &&
            TicketWorkspaceView.SqlLines(TicketSql.Compare(Path.Combine(root, "SPs", "99999"), []), "ticket", 200).Count == 0 &&
            TicketWorkspaceView.SqlLines(null, "ticket", 200).Count == 0,
            "a missing folder is mentioned only when the workspace changed SQL files");
    }

    private static Checkout FakeCheckout(string repository, string branch, bool primary = false, bool detached = false) =>
        new(repository, $@"C:\fixture\{repository}", primary ? $@"C:\fixture\{repository}" : $@"C:\fixture\worktrees\{repository}\{branch}-worktree",
            detached ? "" : branch, "0123456789abcdef", primary, detached, false);

    private static void CheckTicketGrouping()
    {
        var checkouts = new List<Checkout>
        {
            FakeCheckout("MyMeyer", "master", primary: true), FakeCheckout("MyMeyer", "38636"), FakeCheckout("MyMeyer", "38636-2"),
            FakeCheckout("Database--SQL", "38636"), FakeCheckout("Database--SQL", "automation-manager"),
            FakeCheckout("AutomatedTasks", "automation-manager", primary: true), FakeCheckout("MyMeyer", "automation-manager"),
            FakeCheckout("MyMeyer", "", detached: true), FakeCheckout("Warehouse--WillCall", "trunk", primary: true),
            FakeCheckout("MeyerOnline", "master", primary: true), FakeCheckout("Other", "t3/e38a55c3")
        };
        var defaults = new Dictionary<string, string?>(Files.Comparer)
        {
            [@"C:\fixture\MyMeyer"] = "origin/master", [@"C:\fixture\Database--SQL"] = "origin/main", [@"C:\fixture\AutomatedTasks"] = "main",
            [@"C:\fixture\Warehouse--WillCall"] = "origin/trunk", [@"C:\fixture\MeyerOnline"] = "origin/development", [@"C:\fixture\Other"] = null
        };
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var dates = new Dictionary<string, DateTimeOffset>
        {
            ["38636"] = now.AddDays(-9), ["38636-2"] = now.AddDays(-2), ["automation-manager"] = now.AddDays(-5), ["t3/e38a55c3"] = now.AddDays(-1)
        };
        var workspaces = TicketWorkspaces.Group(checkouts, root => defaults[root], checkout => dates.TryGetValue(checkout.Branch, out var date) ? date : null);
        var ticket = workspaces.Single(workspace => workspace.Key == "38636");
        Check(ticket.Ticket == "38636" && ticket.Checkouts.Count == 3 && ticket.Repositories.SequenceEqual(["Database--SQL", "MyMeyer"]) &&
            ticket.Checkouts.Any(checkout => checkout.Branch == "38636-2"), "a ticket's follow-up branches join its workspace across repositories");
        var shared = workspaces.Single(workspace => workspace.Key == "automation-manager");
        Check(shared.Ticket is null && shared.Repositories.SequenceEqual(["AutomatedTasks", "Database--SQL", "MyMeyer"]) && shared.Checkouts.Any(checkout => checkout.Primary),
            "same-named branches in different repositories form one workspace, including a primary checkout on that branch");
        Check(workspaces.Count == 3 && workspaces.All(workspace => workspace.Checkouts.All(checkout => !checkout.Detached && checkout.Branch is not ("master" or "trunk"))),
            "detached checkouts, checkouts on their repository's default branch, and mainline branches beside another default are left out");
        Check(workspaces.Select(workspace => workspace.Key).SequenceEqual(["t3/e38a55c3", "38636", "automation-manager"]) &&
            ticket.LastActivity == now.AddDays(-2), "workspaces are ordered by their most recent commit in any checkout");
    }

    private static List<TicketWorkspace> PickerWorkspaces(DateTimeOffset now) =>
        Enumerable.Range(0, 30).Select(index => new TicketWorkspace((39000 + index).ToString(), (39000 + index).ToString(),
                [FakeCheckout("MyMeyer", (39000 + index).ToString()), FakeCheckout(index == 4 ? "Database--SQL" : "API--Public", (39000 + index).ToString())],
                now.AddDays(-index)))
            .Concat([
                new TicketWorkspace("automation-manager", null, [FakeCheckout("AutomatedTasks", "automation-manager"), FakeCheckout("MyMeyer", "automation-manager")], now.AddDays(-3)),
                new TicketWorkspace("t3/e38a55c3", null, [FakeCheckout("Cultivate", "t3/e38a55c3")], now.AddHours(-1)),
                new TicketWorkspace("fix[red]" + new string('x', 120), null,
                    [FakeCheckout("Repo[bold]", "fix[red]"), .. Enumerable.Range(0, 12).Select(index => FakeCheckout("Repository-" + index, "fix[red]"))], now.AddDays(-400))])
            .ToList();

    private static void CheckTicketPicker()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var workspaces = PickerWorkspaces(now);
        var picker = new TicketPicker(workspaces, now: now);
        void Key(ConsoleKey key, ConsoleModifiers modifiers = 0) =>
            picker.HandleKey(new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)), 7);
        void Type(string text)
        {
            foreach (var character in text) picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        }
        Check(picker.Tab == TicketTab.Tickets && picker.Matches.Count == 30 && picker.Selected?.Key == "39000",
            "the ticket list starts on tickets with the most recent first");
        Key(ConsoleKey.End);
        Check(picker.Index == 29, "End reaches the oldest ticket");
        Key(ConsoleKey.PageUp);
        Check(picker.Index == 22, "PgUp moves back one page");
        Key(ConsoleKey.PageDown);
        Key(ConsoleKey.DownArrow);
        Check(picker.Index == 0, "the ticket list wraps from the last row to the first");
        Key(ConsoleKey.UpArrow);
        Key(ConsoleKey.Home);
        Check(picker.Index == 0, "Home returns to the newest ticket");
        Type("database");
        Check(picker.Matches.Count == 1 && picker.Selected?.Key == "39004", "typing searches the repositories of each ticket");
        Key(ConsoleKey.Backspace);
        Check(picker.Query == "databas", "Backspace edits the search");
        Key(ConsoleKey.U, ConsoleModifiers.Control);
        Type("39012");
        Check(picker.Matches.Count == 1 && picker.Selected?.Key == "39012", "typing searches ticket numbers");
        Key(ConsoleKey.Tab);
        Check(picker.Tab == TicketTab.Branches && picker.Matches.Count == 0 && picker.Selected is null, "Tab switches to branches and keeps the search");
        Key(ConsoleKey.U, ConsoleModifiers.Control);
        Check(picker.Matches.Select(workspace => workspace.Key).Take(2).SequenceEqual(["t3/e38a55c3", "automation-manager"]),
            "other branches are listed on their own tab, most recent first");
        Type("automatedtasks");
        Check(picker.Selected?.Key == "automation-manager", "branch workspaces are searchable by repository");
        Key(ConsoleKey.LeftArrow);
        Check(picker.Tab == TicketTab.Tickets, "Left switches back to tickets");
        Key(ConsoleKey.RightArrow);
        Check(picker.Tab == TicketTab.Branches, "Right switches to branches");
        Key(ConsoleKey.U, ConsoleModifiers.Control);
        Type("no-such-workspace");
        Key(ConsoleKey.DownArrow);
        Key(ConsoleKey.PageDown);
        Check(picker.Selected is null && picker.Index == 0, "an empty search cannot select a workspace");

        Check(TicketPicker.Age(now, now) == "today" && TicketPicker.Age(now.AddDays(-1), now) == "yesterday" &&
            TicketPicker.Age(now.AddDays(-3), now) == "3 days ago" && TicketPicker.Age(now.AddDays(-20), now) == "2 weeks ago" &&
            TicketPicker.Age(now.AddDays(-90), now) == "3 months ago" && TicketPicker.Age(now.AddDays(-800), now) == "2 years ago" &&
            TicketPicker.Age(null, now) == "unknown", "last activity is shown as an age");
        Check(TicketPicker.RepositoryList(workspaces[0], 40) == "MyMeyer, API--Public" && TicketPicker.RepositoryList(workspaces[0], 10) == "2 repositories",
            "repository names are listed when they fit, otherwise counted");

        var live = new DeploymentState { Active = true, SourceRoot = workspaces[17].Checkouts[0].Path + "\\" };
        picker = new TicketPicker(workspaces, live, now);
        static string Render(TicketPicker model, int width, int height) => Capture(model.Render(width, height), width, height);
        Check(picker.Selected?.Key == "39017" && Render(picker, 100, 30).Contains("LIVE  39017"),
            "the workspace holding the active deployment's source is selected and marked LIVE");
        picker = new TicketPicker(workspaces, new DeploymentState { Active = false, SourceRoot = workspaces[17].Checkouts[0].Path }, now);
        Check(!Render(picker, 100, 30).Contains("LIVE"), "an inactive deployment marks nothing LIVE");
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            foreach (var tab in new[] { TicketTab.Tickets, TicketTab.Branches })
            {
                picker = new TicketPicker(workspaces, now: now);
                if (tab == TicketTab.Branches) picker.HandleKey(new('\t', ConsoleKey.Tab, false, false, false), 7);
                var lines = Render(picker, width, height).Split('\n');
                Check(lines.Length <= height && lines.All(line => line.Length <= width) && lines.Any(line => line.Contains("Esc")) &&
                    lines.Any(line => line.Contains("TICKETS")), $"the {tab.ToString().ToLowerInvariant()} list fits {width}x{height} with its keys visible");
                var first = picker.Matches[0].Key;
                Check(lines.Count(line => line.Contains(first[..Math.Min(5, first.Length)])) >= 1 && lines.Count(line => line.Contains("> ")) <= 1,
                    $"the {tab.ToString().ToLowerInvariant()} list rows do not wrap at {width}x{height}");
            }
        }
        picker = new TicketPicker(workspaces, now: now);
        picker.HandleKey(new('\t', ConsoleKey.Tab, false, false, false), 7);
        picker.HandleKey(new('\0', ConsoleKey.End, false, false, false), 7);
        var screen = Render(picker, 200, 35);
        Check(screen.Contains("fix[red]") && screen.Contains("Repo[bold]") && screen.Contains("13 repositories"),
            "markup characters in branch and repository names are shown literally");
        Check(Render(new TicketPicker([], now: now), 80, 24).Contains("No ticket branches were found"), "an empty list renders safely");

        using (var output = new StringWriter())
        {
            picker = new TicketPicker(workspaces, now: now);
            var keys = new Queue<ConsoleKeyInfo>([new('\0', ConsoleKey.DownArrow, false, false, false), new('\r', ConsoleKey.Enter, false, false, false)]);
            var chosen = picker.Show(LiveConsole(output), _ =>
            {
                Check(output.ToString().Contains("TICKET WORKSPACES"), "the ticket list renders before waiting for a key");
                return keys.Dequeue();
            });
            Check(chosen?.Key == "39001", "Enter opens the highlighted workspace");
        }
        using (var output = new StringWriter())
        {
            var chosen = new TicketPicker(workspaces, now: now).Show(LiveConsole(output), _ => new('\u001b', ConsoleKey.Escape, false, false, false));
            Check(chosen is null, "Esc leaves the ticket list without opening a workspace");
        }
    }

    private static TicketWorkspaceDetails ViewDetails(string folder)
    {
        var repositories = new[] { "API--Public", "Database--SQL", "DriverPortal--API", "Libraries--BusinessLogic", "MeyerOnline", "MyMeyer", "Warehouse--WillCall[x]" };
        var checkouts = repositories.Select((repository, index) => FakeCheckout(repository, index == 2 ? "38636-2" : "38636")).ToList();
        var workspace = new TicketWorkspace("38636", "38636", checkouts, DateTimeOffset.Now);
        var pullRequests = TicketPullRequests.Parse(PullRequestJson);
        var details = checkouts.Select((checkout, index) => new TicketCheckout(checkout,
            index == 3 ? null : new CheckoutStatus(index == 0 ? 3 : 0, index == 0 ? 2 : index == 4 ? 1 : 0, DateTimeOffset.Now, "Subject"),
            index == 3 ? "fatal: not a git repository (or any of the parent directories): .git" : null,
            checkout.Repository == "MyMeyer", index is 1 or 6 ? [] : [checkout.Path + @"\Example.sln"],
            index is 0 or 5 ? pullRequests : [])).ToList();
        var sql = new TicketSqlComparison(folder, true,
            [new("Same.sql", "Database--SQL", "Sql/Same.sql", "Same.sql")],
            [new("Changed[1].sql", "Database--SQL", "Sql/Changed[1].sql", "Changed[1].sql"), .. Enumerable.Range(0, 20).Select(index => new TicketSqlFile($"Procedure{index}.sql", "Database--SQL", "x", "x"))],
            [new("New.sql", "Database--SQL", "Sql/New.sql", null)], ["AlterTable.sql"]);
        return new(workspace, details, sql);
    }

    private static void CheckTicketWorkspaceView()
    {
        Check(TicketWorkspaceView.StateText(new(0, 0, null, ""), null) == "clean" && TicketWorkspaceView.StateText(new(3, 0, null, ""), null) == "3 uncommitted" &&
            TicketWorkspaceView.StateText(new(0, 2, null, ""), null) == "2 unpushed" && TicketWorkspaceView.StateText(new(3, 2, null, ""), null) == "3 uncommitted, 2 unpushed" &&
            TicketWorkspaceView.StateText(null, "The worktree folder is missing.") == "The worktree folder is missing.",
            "checkout state reads clean, uncommitted, unpushed, both, or the checkout's error");
        var settings = new Settings();
        var details = ViewDetails(@"C:\fixture\SPs\38636");
        var actions = TicketWorkspaces.Actions(details, settings);
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var view = Capture(TicketWorkspaceView.Render(details, width, height - TicketWorkspaceView.MenuLines(actions.Count)), width, height).Split('\n');
            var menu = Capture(new ActionPicker(actions).Render("Choose an action", height), width, height).Split('\n');
            Check(view.Length + menu.Length + 1 <= height && view.Concat(menu).All(line => line.Length <= width),
                $"the workspace screen and its actions fit {width}x{height} without wrapping");
            Check(view.Any(line => line.Contains("#2763 draft")) && view.Any(line => line.Contains("SQL FILES")) &&
                view.Any(line => line.Contains("1 identical")) && view.Any(line => line.Contains("Different: Changed[1].sql")),
                $"the workspace screen shows pull requests and the SQL card at {width}x{height}");
            var shown = details.Checkouts.Count(checkout => view.Any(line => line.Contains(checkout.Checkout.Repository[..Math.Min(8, checkout.Checkout.Repository.Length)])));
            Check(shown == details.Checkouts.Count || view.Any(line => line.Contains($"+{details.Checkouts.Count - shown} more")),
                $"checkouts that do not fit {width}x{height} are counted instead of hidden");
        }
        var wide = Capture(TicketWorkspaceView.Render(details, 120, 35 - TicketWorkspaceView.MenuLines(actions.Count)), 120, 35);
        Check(wide.Contains("TICKET WORKSPACE") && wide.Contains("12345 - Restrict [Will Call] items") && wide.Contains("Warehouse--WillCall[x]"),
            "the header names the key and the pull request title, with markup characters shown literally");
        Check(wide.Contains("3 uncommitted, 2 unpushed") && wide.Contains("fatal: not a git") && wide.Contains("38636-worktree") &&
            wide.Contains("Missing from the folder: New.sql") && wide.Contains("only in the folder"),
            "each row shows its state or its own error, the worktree folder, and the SQL differences");
        var live = new DeploymentState { Active = true, SourceRoot = details.Checkouts[5].Checkout.Path };
        Check(Capture(TicketWorkspaceView.Render(details, 120, 30, live), 120, 30).Contains("LIVE  MyMeyer"), "the deployed checkout is marked LIVE");
        Check(actions.Select(action => action.Label).SequenceEqual([TicketWorkspaces.OpenAll, TicketWorkspaces.OpenOne, TicketWorkspaces.PreviewDeployment,
            TicketWorkspaces.OpenTicket, TicketWorkspaces.OpenPullRequests, TicketWorkspaces.OpenSqlFolder, TicketWorkspaces.Refresh, TicketWorkspaces.Back]) &&
            actions[0].Description.Contains("5 solutions") && actions[0].Description.Contains("2 checkouts without one are skipped") &&
            actions.Single(action => action.Label == TicketWorkspaces.OpenPullRequests).Description.Contains("1 pull request "),
            "a ticket workspace offers its solutions, a MyMeyer preview, the ticket page, and its pull requests");
        var branch = new TicketWorkspaceDetails(new TicketWorkspace("automation-manager", null, [details.Workspace.Checkouts[1]], null),
            [details.Checkouts[1] with { PullRequests = [] }], null);
        Check(TicketWorkspaces.Actions(branch, settings).Select(action => action.Label).SequenceEqual([TicketWorkspaces.Refresh, TicketWorkspaces.Back]) &&
            !Capture(TicketWorkspaceView.Render(branch, 80, 20), 80, 20).Contains("SQL FILES"),
            "a branch workspace without solutions, MyMeyer, pull requests, or SQL files offers only refresh and back");
    }

    private static async Task CheckTicketFixtures(string home)
    {
        var root = Path.Combine(home, "tickets");
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(root, "clones", "MyMeyer"), WorktreesRoot = Path.Combine(root, "worktrees"), SqlMirrorRoot = Path.Combine(root, "SPs")
        };
        var git = new GitRepository(settings);
        async Task Commit(string repository, string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repository, file))!);
            File.WriteAllText(Path.Combine(repository, file), text);
            await git.Git(repository, "add", "-A");
            await git.Git(repository, "commit", "-m", "Add " + file);
        }
        var myMeyer = await FixtureRepository(git, settings.RepositoryRoot, "master");
        await Commit(myMeyer, "MyMeyer.sln", "");
        var database = await FixtureRepository(git, Path.Combine(root, "clones", "Database--SQL"));
        await Commit(database, "Sql/StoredProcedures/Old.sql", "SELECT 0\r\n");
        var other = await FixtureRepository(git, Path.Combine(root, "clones", "Other"));
        await Commit(other, "Other.sln", "");
        string Worktree(string repository, string branch) => Path.Combine(settings.WorktreesRoot, repository, branch.Replace('/', '-') + "-worktree");
        await git.Git(myMeyer, "worktree", "add", "-b", "12345", Worktree("MyMeyer", "12345"));
        await git.Git(myMeyer, "worktree", "add", "-b", "automation-manager", Worktree("MyMeyer", "automation-manager"));
        await git.Git(myMeyer, "worktree", "add", "--detach", Worktree("MyMeyer", "detached"));
        var sqlWorktree = Worktree("Database--SQL", "12345-2");
        await git.Git(database, "worktree", "add", "-b", "12345-2", sqlWorktree);
        await git.Git(database, "worktree", "add", "-b", "automation-manager", Worktree("Database--SQL", "automation-manager"));
        await git.Git(other, "worktree", "add", "-b", "t3/abc12345", Worktree("Other", "t3/abc12345"));
        await git.Git(other, "checkout", "--quiet", "-b", "12345");
        File.Delete(Path.Combine(sqlWorktree, "Sql", "StoredProcedures", "Old.sql"));
        await Commit(sqlWorktree, "Sql/StoredProcedures/Same.sql", "SELECT 1\r\nFROM Example\r\n");
        await Commit(sqlWorktree, "Sql/StoredProcedures/Changed.sql", "SELECT 2\r\n");
        File.WriteAllText(Path.Combine(sqlWorktree, "Sql", "StoredProcedures", "New.sql"), "SELECT 3\r\n");
        await Commit(Worktree("Database--SQL", "automation-manager"), "Sql/Only.sql", "SELECT 4\r\n");
        var mirror = Path.Combine(settings.SqlMirrorRoot, "12345");
        Directory.CreateDirectory(mirror);
        File.WriteAllText(Path.Combine(mirror, "Same.sql"), "SELECT 1\nFROM Example\n");
        File.WriteAllText(Path.Combine(mirror, "Changed.sql"), "SELECT 2 -- edited\r\n");
        File.WriteAllText(Path.Combine(mirror, "AlterTableExample.sql"), "ALTER TABLE Example ADD Flag BIT\r\n");

        var calls = git.Calls;
        var workspaces = await TicketWorkspaces.Load(settings, git);
        Check(workspaces.Select(workspace => workspace.Key).Order(StringComparer.Ordinal).SequenceEqual(["12345", "automation-manager", "t3/abc12345"]),
            "real worktrees group into a ticket and shared branches, leaving out default-branch and detached checkouts");
        calls = git.Calls - calls;
        await FixtureRepository(git, Path.Combine(root, "clones", "Quiet"));
        var withQuiet = git.Calls;
        Check((await TicketWorkspaces.Load(settings, git)).Select(workspace => workspace.Key).SequenceEqual(workspaces.Select(workspace => workspace.Key)) &&
            git.Calls - withQuiet == calls + 1, "a repository whose only checkout is on its default branch is listed but its branches are not read");
        var ticket = workspaces.Single(workspace => workspace.Key == "12345");
        Check(ticket.Ticket == "12345" && ticket.Repositories.SequenceEqual(["Database--SQL", "MyMeyer", "Other"]) &&
            ticket.Checkouts.Single(checkout => checkout.Repository == "Other").Primary && ticket.LastActivity is not null,
            "a ticket workspace holds every repository's checkout of the ticket, including a primary checkout on its branch");
        Check(workspaces.Single(workspace => workspace.Key == "automation-manager").Repositories.SequenceEqual(["Database--SQL", "MyMeyer"]),
            "real worktrees on the same branch name in two repositories form one workspace");

        var myMeyerRoot = await git.MainWorktree(settings.RepositoryRoot);
        var lookups = new List<string>();
        Task<string> Lookup(Checkout checkout)
        {
            lock (lookups) lookups.Add(checkout.Repository);
            return checkout.Repository switch
            {
                "MyMeyer" => Task.FromResult(PullRequestJson),
                "Database--SQL" => throw new System.ComponentModel.Win32Exception("gh.exe was not found"),
                _ => Task.FromResult("gh: To get started with GitHub CLI, please run: gh auth login")
            };
        }
        var ghost = new Checkout("Ghost", Path.Combine(root, "clones", "Ghost"), Path.Combine(root, "gone"), "12345", "abc", false, false, false);
        var notRepository = Path.Combine(root, "not a repository");
        Directory.CreateDirectory(notRepository);
        var broken = new Checkout("Broken", notRepository, notRepository, "12345", "abc", true, false, false);
        var details = await TicketWorkspaces.Inspect(settings, git, ticket with { Checkouts = [.. ticket.Checkouts, ghost, broken] }, myMeyerRoot, true, Lookup);
        TicketCheckout Row(string repository) => details.Checkouts.Single(checkout => checkout.Checkout.Repository == repository);
        Check(Row("MyMeyer") is { MyMeyer: true, Error: null, Solutions.Count: 1 } && Row("MyMeyer").PullRequest?.Summary == "#2763 draft, checks passing" &&
            details.Title == "12345 - Restrict [Will Call] items", "opening a workspace reads pull requests and recognises the MyMeyer checkout");
        Check(Row("Database--SQL") is { MyMeyer: false, Error: null, Solutions.Count: 0, PullRequests.Count: 0 } &&
            Row("Database--SQL").Status is { Uncommitted: 1, Unpushed: > 0 } && Row("Other").PullRequests.Count == 0 && lookups.Count == 5,
            "each checkout's status is read, and a failed pull request lookup leaves that checkout without pull requests");
        Check(Row("Ghost").Error == "The worktree folder is missing." && Row("Broken").Error is { Length: > 0 } && Row("Broken").Status is null &&
            Row("Other").Error is null, "a missing folder or a Git error shows on its own row without stopping the others");
        Check(details.Sql is { FolderExists: true } sql && sql.Identical.Single().Name == "Same.sql" && sql.Different.Single().Name == "Changed.sql" &&
            sql.Missing.Single().Name == "New.sql" && sql.OnlyInFolder.SequenceEqual(["AlterTableExample.sql"]) &&
            sql.Identical[0].Path == "Sql/StoredProcedures/Same.sql" && sql.Changed == 3,
            "changed SQL files are compared with the SQL files folder, and deleted ones are skipped");
        var actions = TicketWorkspaces.Actions(details, settings).Select(action => action.Label).ToList();
        Check(actions.SequenceEqual([TicketWorkspaces.OpenAll, TicketWorkspaces.OpenOne, TicketWorkspaces.PreviewDeployment, TicketWorkspaces.OpenTicket,
            TicketWorkspaces.OpenPullRequests, TicketWorkspaces.OpenSqlFolder, TicketWorkspaces.Refresh, TicketWorkspaces.Back]),
            "a ticket workspace with a SQL files folder offers to open it");
        var shared = await TicketWorkspaces.Inspect(settings, git, workspaces.Single(workspace => workspace.Key == "automation-manager"), myMeyerRoot, true, null);
        Check(shared.Sql is { FolderExists: false, Missing.Count: 1 } &&
            TicketWorkspaces.Actions(shared, settings).Select(action => action.Label).SequenceEqual([TicketWorkspaces.OpenAll, TicketWorkspaces.PreviewDeployment,
                TicketWorkspaces.Refresh, TicketWorkspaces.Back]) &&
            Capture(TicketWorkspaceView.Render(shared, 120, 30), 120, 30).Contains("No SQL files folder for this branch"),
            "a branch workspace has no ticket page, and says when its changed SQL files have no folder");
        var oneOff = await TicketWorkspaces.Inspect(settings, git, workspaces.Single(workspace => workspace.Key == "t3/abc12345"), myMeyerRoot, true, null);
        Check(!TicketWorkspaces.Actions(oneOff, settings).Any(action => action.Label == TicketWorkspaces.PreviewDeployment) &&
            !Capture(TicketWorkspaceView.Render(oneOff, 120, 30), 120, 30).Contains("SQL FILES"),
            "a workspace without MyMeyer offers no deployment preview, and one without SQL files shows no SQL card");

        using (var output = new StringWriter())
        {
            Check(await TicketWorkspaces.Command(settings, git, ["tickets"], Lookup, output) == 0, "the tickets command succeeds");
            using var json = JsonDocument.Parse(output.ToString());
            var all = json.RootElement;
            var first = all.EnumerateArray().Single(item => item.GetProperty("Key").GetString() == "12345");
            var checkout = first.GetProperty("Checkouts")[0];
            Check(all.GetArrayLength() == 3 && first.GetProperty("Ticket").GetString() == "12345" &&
                all.EnumerateArray().Single(item => item.GetProperty("Key").GetString() == "automation-manager").GetProperty("Ticket").ValueKind == JsonValueKind.Null &&
                checkout.GetProperty("Repository").GetString() == "Database--SQL" && checkout.GetProperty("Branch").GetString() == "12345-2" &&
                checkout.GetProperty("Path").GetString() == Files.Full(sqlWorktree) && !checkout.GetProperty("Primary").GetBoolean() &&
                checkout.GetProperty("Status").GetProperty("Uncommitted").GetInt32() == 1 &&
                checkout.GetProperty("PullRequests").ValueKind == JsonValueKind.Null && first.GetProperty("Sql").ValueKind == JsonValueKind.Null,
                "tickets lists every workspace with its ticket and its checkouts' repository, branch, path, primary flag, and status");
        }
        using (var output = new StringWriter())
        {
            lookups.Clear();
            await TicketWorkspaces.Command(settings, git, ["tickets", "--ticket", "12345", "--prs"], Lookup, output);
            using var json = JsonDocument.Parse(output.ToString());
            var one = json.RootElement;
            Check(one.ValueKind == JsonValueKind.Object && one.GetProperty("Key").GetString() == "12345" &&
                one.GetProperty("Sql").GetProperty("Different")[0].GetProperty("Name").GetString() == "Changed.sql" &&
                one.GetProperty("Checkouts").EnumerateArray().Single(item => item.GetProperty("Repository").GetString() == "MyMeyer")
                    .GetProperty("PullRequests")[0].GetProperty("Summary").GetString() == "#2763 draft, checks passing" && lookups.Count == 3,
                "tickets --ticket shows one workspace with its SQL comparison, and --prs adds pull requests");
        }
        lookups.Clear();
        using (var output = new StringWriter()) await TicketWorkspaces.Command(settings, git, ["tickets", "--ticket", "12345"], Lookup, output);
        Check(lookups.Count == 0, "pull requests are looked up only with --prs");
        await Reject(() => TicketWorkspaces.Command(settings, git, ["tickets", "--ticket", "99999"], Lookup, TextWriter.Null), "an unknown --ticket is an error");
    }
}
