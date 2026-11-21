namespace Cultivate;

internal static partial class SelfTest
{
    private const string SevenJson = """
        {"number":7,"title":"Raise the [page] value","author":{"login":"alice"},"headRefName":"alice/page-value","baseRefName":"master","isDraft":true,"state":"OPEN"}
        """;
    private const string EightJson = """
        {"number":8,"title":"Release markup","author":{"login":"bob"},"headRefName":"bob/release-markup","baseRefName":"release","isDraft":false,"state":"MERGED"}
        """;

    /// <summary>Pull requests fetched from a local bare repository standing in for origin, with the GitHub CLI faked.</summary>
    private static async Task CheckPullRequests(string home, Settings settings, GitRepository git, Bridge bridge, Planner planner, Pipeline pipeline,
        Func<string, Task<Dictionary<string, string>>> maps)
    {
        CheckPullRequestParsing();
        CheckPullRequestPicker();
        var repository = settings.RepositoryRoot;
        var origin = Path.Combine(home, "pr-origin.git");
        var author = Path.Combine(home, "pr-author");
        Task<string> G(string root, params string[] args) => git.Git(root, args);
        await G(home, "init", "-q", "--bare", "-b", "master", origin);
        await G(repository, "remote", "add", "origin", origin);
        await G(repository, "push", "-q", "origin", "master:refs/heads/master");
        await G(home, "clone", "-q", origin, author);
        await G(author, "config", "user.email", "cultivate-test@localhost");
        await G(author, "config", "user.name", "Cultivate isolated tests");
        async Task<string> Push(string branch, string from, string reference, params (string Path, string Content)[] files)
        {
            await G(author, "checkout", "-q", "-B", branch, from);
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(author, path))!);
                File.WriteAllText(Path.Combine(author, path), content);
            }
            await G(author, "add", ".");
            await G(author, "commit", "-qm", branch);
            await G(author, "push", "-q", "-f", "origin", "HEAD:" + reference);
            return await G(author, "rev-parse", "HEAD");
        }
        var first = await Push("pr7", "origin/master", "refs/pull/7/head",
            (@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value + 7; }"), (@"Page\View.ascx", "pull request markup"));
        var release = await Push("release", "origin/master", "refs/heads/release", (@"Docs\release.txt", "release notes"));
        var eight = await Push("pr8", "release", "refs/pull/8/head", (@"Page\View.ascx", "release markup"));
        var calls = new List<string>();
        Task<string> Gh(string[] arguments)
        {
            calls.Add(string.Join(' ', arguments));
            return arguments switch
            {
                ["pr", "list", ..] => Task.FromResult($"[{SevenJson}]"),
                ["pr", "view", "7", ..] => Task.FromResult(SevenJson),
                ["pr", "view", "8", ..] => Task.FromResult(EightJson),
                _ => Task.FromException<string>(new InvalidOperationException("gh.exe failed (1).\nGraphQL: Could not resolve to a PullRequest with the number of 404."))
            };
        }
        Task<string> Missing(string[] _) => throw new System.ComponentModel.Win32Exception("gh.exe was not found");
        async Task<List<string>> PrivateRefs() =>
            [.. (await G(repository, "for-each-ref", "--format=%(refname)", PullRequests.RefPrefix)).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        async Task<string> Untouched() => string.Join("\n", await G(repository, "symbolic-ref", "HEAD"), await G(repository, "status", "--porcelain"),
            await G(repository, "worktree", "list", "--porcelain"), await G(repository, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/remotes"));
        var before = await Untouched();

        var (listed, problem) = await PullRequests.Open(Gh);
        Check(problem is null && listed.Single() is { Number: 7, Author: "alice", HeadBranch: "alice/page-value", IsDraft: true } &&
            calls.Single().StartsWith("pr list --state open", StringComparison.Ordinal), "open pull requests are listed through the GitHub CLI");
        var target = await PullRequests.Fetch(settings, git, 7, null, Gh);
        Check(target is { Worktree: null, Ref: "refs/cultivate/pr/7/head", Branch: "PR #7: Raise the [page] value" } && target.PullRequest?.Commit == first,
            "a pull request is fetched by number into a private ref at its current head");
        Check(await Untouched() == before, "fetching a pull request changes no branch, remote branch, worktree, HEAD, or saved file");
        Check(!(await git.Targets()).Any(x => x.Ref.StartsWith(PullRequests.RefPrefix, StringComparison.Ordinal)),
            "fetched pull requests are not listed as branches or worktrees");
        var plan = await planner.Create(target, maps);
        Check(plan.CanDeploy && plan.Commit == first && plan.Projects.Select(x => x.Assembly).SequenceEqual(["Page"]) &&
            plan.Frontend.Single().RelativePath == @"modules\View.ascx", "a pull request plans like a committed branch: its changed project and markup");
        Check(plan.Notes.Any(note => note.Contains("#7 by alice") && note.Contains("alice/page-value into master") && note.Contains(first[..7])),
            "the preview names the pull request's author, branches, and head commit");

        var farmMarkup = Files.Child(settings.WebfarmRoot, @"modules\View.ascx");
        var farmPage = Files.Child(settings.WebfarmRoot, @"bin\Page.dll");
        var markupBefore = File.ReadAllText(farmMarkup);
        var pageBefore = Files.Hash(farmPage);
        await pipeline.Activate(plan, _ => { }, watch: false);
        var status = await bridge.Status() ?? throw new InvalidDataException("The pull request deployment has no journal.");
        Check(status is { Active: true, Branch: "PR #7: Raise the [page] value", SourceRef: "refs/cultivate/pr/7/head" } &&
            string.IsNullOrEmpty(status.SourceRoot) && File.ReadAllText(farmMarkup) == "pull request markup" && Files.Hash(farmPage) != pageBefore,
            "activating a pull request deploys its head, and status names it");
        Check(await Untouched() == before, "activating a pull request leaves the checkout and its branches alone");

        var merged = await PullRequests.Fetch(settings, git, 8, status, Gh);
        var mergedPlan = await planner.Create(merged, maps);
        Check(mergedPlan.CanDeploy && mergedPlan.Commit == eight && mergedPlan.Baseline == release &&
            mergedPlan.Changes.Select(change => change.Path).SequenceEqual(["Page/View.ascx"]),
            "a pull request into another branch is compared with that branch, not the configured base");
        Check(mergedPlan.Notes.Any(note => note.Contains("is merged")) && mergedPlan.Notes.Any(note => note.Contains("Compared with release")),
            "a merged pull request can still be reviewed, and the preview says so and names its base");
        Check((await PrivateRefs()).SequenceEqual(["refs/cultivate/pr/7/base", "refs/cultivate/pr/7/head", "refs/cultivate/pr/8/base", "refs/cultivate/pr/8/head"]),
            "the active pull request's refs are kept while another is previewed");
        await pipeline.Activate(mergedPlan, _ => { }, watch: false);
        status = await bridge.Status() ?? throw new InvalidDataException("The pull request deployment has no journal.");
        var rebuilt = new Target(status.Branch, null, status.SourceRef);
        Check(status.Branch == "PR #8: Release markup" && File.ReadAllText(farmMarkup) == "release markup" && Files.Hash(farmPage) == pageBefore,
            "another pull request replaces the active one and restores the files it no longer deploys");
        Check((await git.Inspect(rebuilt)).Fingerprint == status.Fingerprint &&
            (await git.Inspect(rebuilt, settings.BaseBranch)).Fingerprint != status.Fingerprint,
            "automatic updates rebuild a pull request's version from the journal with the base it was compared with");

        var second = await Push("pr7", "pr7", "refs/pull/7/head", (@"Page\View.ascx", "second push markup"));
        var moved = await planner.Create(await PullRequests.Fetch(settings, git, 7, status, Gh), maps);
        Check(moved.Commit == second && moved.Target.PullRequest?.Commit == second && moved.Fingerprint != plan.Fingerprint,
            "planning a pull request again fetches the head it has moved to");

        string? notFound = null;
        try { await PullRequests.Fetch(settings, git, 404, status, Gh); }
        catch (InvalidOperationException exception) { notFound = exception.Message; }
        Check(notFound?.Contains("Pull request #404 was not found on origin") == true, "a pull request number that does not exist is reported clearly");
        Check((await PrivateRefs()).SequenceEqual(["refs/cultivate/pr/8/base", "refs/cultivate/pr/8/head"]),
            "fetching another pull request removes refs that are neither active nor being previewed");

        await bridge.Call("Restore");
        await PullRequests.Prune(git, settings, null);
        Check(File.ReadAllText(farmMarkup) == markupBefore && Files.Hash(farmPage) == pageBefore && (await PrivateRefs()).Count == 0,
            "restore returns the original files and removes every fetched pull request ref");

        var offline = await PullRequests.Fetch(settings, git, 8, null, Missing);
        var offlinePlan = await planner.Create(offline, maps);
        Check(offline is { Branch: "PR #8", PullRequest.Problem: "The GitHub CLI (gh) is not installed" } && offlinePlan.CanDeploy &&
            offlinePlan.Changes.Select(change => change.Path).SequenceEqual(["Docs/release.txt", "Page/View.ascx"]) &&
            offlinePlan.Notes.Any(note => note.Contains("is not installed") && note.Contains("compared with master")),
            "without the GitHub CLI a pull request is still fetched by number, compared with the configured base, and the preview says what is missing");
        await PullRequests.Prune(git, settings, null);
        Check(await Untouched() == before && (await PrivateRefs()).Count == 0, "reviewing pull requests leaves the repository as it was");
        await G(repository, "remote", "remove", "origin");
    }

    private static void CheckPullRequestParsing()
    {
        Check(PullRequests.Number("7") == 7 && PullRequests.Number(" #42 ") == 42 && PullRequests.Number("https://github.com/o/r/pull/2763") == 2763,
            "a pull request is named by its number, #number, or URL");
        static bool Rejected(string value)
        {
            try { PullRequests.Number(value); }
            catch (InvalidOperationException) { return true; }
            return false;
        }
        Check(new[] { "abc", "0", "-3", "" }.All(Rejected), "anything but a positive number is rejected as a pull request number");
        Check(PullRequests.Base("refs/cultivate/pr/7/head") == "refs/cultivate/pr/7/base" && PullRequests.Base("refs/heads/feature") is null &&
            PullRequests.Base("refs/cultivate/pr/7/base") is null, "only a fetched pull request head is compared with its own fetched base");
        var parsed = PullRequests.Parse($"[{SevenJson},{EightJson},{{\"title\":\"no number\"}}]");
        Check(parsed.Select(request => request.Number).SequenceEqual([8, 7]) && parsed[0] is { Author: "bob", BaseBranch: "release", State: "MERGED", Status: "merged" } &&
            parsed[1] is { Title: "Raise the [page] value", Status: "draft" } && PullRequests.Parse(SevenJson).Single().Number == 7,
            "gh pull request JSON parses into numbers, titles, authors, branches, and states");
        Check(PullRequests.Problem(new System.ComponentModel.Win32Exception("missing")) == "The GitHub CLI (gh) is not installed" &&
            PullRequests.Problem(new InvalidOperationException("gh.exe failed (4).\nTo get started with GitHub CLI, please run:  gh auth login")).Contains("not signed in") &&
            PullRequests.Problem(new InvalidOperationException("gh.exe failed (1).\nHTTP 502\n")) == "The GitHub CLI failed: HTTP 502",
            "a GitHub CLI failure says what is missing");
        Check(new PullRequest(7, Commit: "0123456789").Source == "#7  |  head 0123456" && new PullRequest(7).Label == "PR #7",
            "a pull request without GitHub details is still named by its number");
    }

    private static void CheckPullRequestPicker()
    {
        var requests = PullRequests.Parse("""
            [{"number":12,"title":"Fix [rounding]","author":{"login":"carol"},"headRefName":"carol/rounding","baseRefName":"master","isDraft":true,"state":"OPEN"},
             {"number":120,"title":"Ship it","author":{"login":"dave"},"headRefName":"38636","baseRefName":"master","isDraft":false,"state":"OPEN"},
             {"number":7,"title":"Older","author":{"login":"erin"},"headRefName":"older","baseRefName":"master","isDraft":false,"state":"OPEN"}]
            """);
        var picker = new PullRequestPicker(requests);
        void Type(string text)
        {
            foreach (var character in text) picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        }
        void Clear() => picker.HandleKey(new('\0', ConsoleKey.U, false, false, true), 7);
        static string Render(PullRequestPicker model, int width, int height) => Capture(model.Render(width, height), width, height);
        Check(picker.Matches.Select(request => request.Number).SequenceEqual([120, 12, 7]) && picker.Selected?.Number == 120,
            "open pull requests are listed newest first");
        Type("carol");
        Check(picker.Matches.Single().Number == 12, "pull requests are searchable by author");
        Clear();
        Type("38636");
        Check(picker.Selected?.Number == 120 && picker.Matches[^1] is { Number: 38636, Title: "" },
            "a ticket branch's number finds its pull request first, and that number can still be fetched last");
        Clear();
        Type("12");
        Check(picker.Matches.Select(request => request.Number).SequenceEqual([12, 120]), "a typed number puts that pull request first");
        Clear();
        Type("#99");
        Check(picker.Selected is { Number: 99, Title: "" } && Render(picker, 100, 30).Contains("Review by number"),
            "a number that is not listed can still be chosen, to review a closed or other pull request");
        Clear();
        Type("no-such-pull-request");
        picker.HandleKey(new('\0', ConsoleKey.DownArrow, false, false, false), 7);
        Check(picker.Selected is null && Render(picker, 80, 24).Contains("No matches"), "an empty search cannot select a pull request");

        picker = new PullRequestPicker(requests, active: new DeploymentState { Active = true, SourceRef = PullRequests.HeadRef(7) });
        Check(picker.Selected?.Number == 7 && Render(picker, 100, 30).Contains("LIVE  Older"), "the active pull request is selected and marked LIVE");
        var offline = Render(new PullRequestPicker([], "The GitHub CLI (gh) is not installed"), 100, 30);
        Check(offline.Contains("not installed. Type a pull request number") && offline.Contains("No open pull requests are listed"),
            "without the GitHub CLI the list says what is missing and still takes a number");
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var lines = Render(new PullRequestPicker(requests), width, height).Split('\n');
            Check(lines.Length <= height && lines.All(line => line.Length <= width) && lines.Any(line => line.Contains("Esc")) &&
                lines.Count(line => line.Contains("> ")) == 1, $"the pull request list fits {width}x{height} with its keys visible");
        }
        Check(Render(new PullRequestPicker(requests), 120, 35).Contains("DRAFT  Fix [rounding]"), "drafts are marked and markup characters shown literally");

        using (var output = new StringWriter())
        {
            var keys = new Queue<ConsoleKeyInfo?>([null, new('\0', ConsoleKey.DownArrow, false, false, false), null, new('\r', ConsoleKey.Enter, false, false, false)]);
            var chosen = new PullRequestPicker(requests).Show(LiveConsole(output), _ =>
            {
                Check(output.ToString().Contains("PULL REQUESTS"), "the pull request list renders before waiting for a key");
                return keys.Dequeue();
            });
            var paints = ReleasePaints(output.ToString());
            Check(chosen == 12 && paints.Length == 2 && PaintedRows(paints[1]).Length is > 0 and <= 6,
                "Enter chooses the highlighted pull request, and the list repaints only the lines an arrow changes, as the other pickers do");
        }
        using (var output = new StringWriter())
        {
            picker = new PullRequestPicker(requests);
            foreach (var character in "carol") picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
            Check(picker.Show(LiveConsole(output), Script(Press(ConsoleKey.Escape))) is null && picker.Query == "carol",
                "Esc leaves the pull request list at once, even with a search typed, without fetching anything, and keeps the search for coming back");
        }
    }
}
