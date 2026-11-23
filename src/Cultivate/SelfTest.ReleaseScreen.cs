using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>What the Build release files screen's run needs from the release files fixture: its settings, origin, faked GitHub CLI, and checkout check.</summary>
    private sealed record ReleaseFixture(Settings Settings, GitRepository Git, string Output, string Master, Func<string[], Task<string>> Gh,
        Action<int, string, string, string, string> Pull, Func<string, string, (string Path, string? Content)[], Task<string>> Commit,
        Func<string, string, Task> Publish, Func<string, string, string, Task<string>> Squash, Func<Task<string>> Untouched, string Before);

    private static ConsoleKeyInfo Press(ConsoleKey key, char character = '\0') => new(character, key, false, false, false);
    private static IEnumerable<object?> Typed(string text) => text.Select(character => (object?)new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false));

    /// <summary>
    /// Keys for screens in turn: each step is a key, null for no key, or a condition that holds the next key back until it is true, so a key meant
    /// for a later screen is not read while a build runs. With no steps left every screen gets Esc.
    /// </summary>
    private static Func<int, ConsoleKeyInfo?> Script(params object?[] steps)
    {
        var queue = new Queue<object?>(steps);
        return wait =>
        {
            while (queue.Count != 0 && queue.Peek() is Func<bool> until && until()) queue.Dequeue();
            if (queue.Count == 0) return Press(ConsoleKey.Escape);
            if (queue.Peek() is Func<bool>)
            {
                Thread.Sleep(Math.Min(wait, 20));
                return null;
            }
            return (ConsoleKeyInfo?)queue.Dequeue();
        };
    }

    /// <summary>The text terminal output shows, without escape codes, card borders, or line breaks, so wrapped sentences read whole.</summary>
    private static string Shown(string output) =>
        Regex.Replace(Regex.Replace(output, @"\u001b\][^\u001b]*\u001b\\|\u001b\[[0-9;?]*[A-Za-z]", ""), @"[\s│╭╮╰╯─]+", " ");
    private static string[] ReleasePaints(string output) => output.Split("\u001b[?2026h").Skip(1).Select(paint => paint.Split("\u001b[?2026l")[0]).ToArray();
    private static int[] PaintedRows(string paint) =>
        Regex.Matches(paint, @"\u001b\[(\d+);1H").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The Build release files screen's picker, preview, progress and result, from fixture data alone.</summary>
    private static void CheckReleaseScreens()
    {
        var labels = Terminal.HomeActions.Select(action => action.Label).ToList();
        Check(labels.IndexOf(ReleaseScreen.Action) == labels.IndexOf("Review a pull request") + 1 &&
            Terminal.HomeActions.Single(action => action.Label == ReleaseScreen.Action).Description.Contains("merged pull request"),
            "Build release files comes directly after Review a pull request on the home screen, with its own help");
        CheckReleasePicker();
        CheckReleasePreview();
        CheckReleaseProgress();
        CheckReleaseResult();
    }

    private static void CheckReleasePicker()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        static string Item(int number, string title, string author, string branch, string merged) =>
            JsonSerializer.Serialize(new { number, title, author = new { login = author }, headRefName = branch, mergedAt = merged });
        string[] mineItems = [Item(31, "Older mine", "me", "40001", "2026-11-10T09:00:00Z"), Item(12, "Fix [totals]", "me", "40002-2", "2026-11-19T08:00:00Z")];
        var mineJson = $"[{string.Join(",", mineItems)}]";
        var allJson = $"[{string.Join(",", mineItems.Concat([Item(50, "Someone else's", "bob", "feature/bob", "2026-11-18T10:00:00Z"),
            Item(44, "Hotfix", "carol", "40005", "2026-11-19T09:30:00Z")]))}]";
        var calls = new List<string>();
        Task<string> Gh(string[] arguments)
        {
            lock (calls) calls.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments.Contains("@me") ? mineJson : allJson);
        }
        var (mine, all, problem) = ReleasePicker.Load(Gh, "master").GetAwaiter().GetResult();
        Check(problem is null && calls.Order(StringComparer.Ordinal).SequenceEqual([
                "pr list --state merged --base master --author @me --limit 50 --json number,title,author,headRefName,mergedAt",
                "pr list --state merged --base master --limit 100 --json number,title,author,headRefName,mergedAt"]),
            "merged pull requests are listed through the GitHub CLI: yours, and everyone's, merged into master");

        var picker = new ReleasePicker(mine, all, null, "master", now);
        void Type(string text)
        {
            foreach (var character in text) picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        }
        void Clear() => picker.HandleKey(new('\0', ConsoleKey.U, false, false, true), 7);
        static string Render(ReleasePicker model, int width, int height) => Capture(model.Render(width, height), width, height);
        Check(picker.Tab == ReleaseTab.Mine && picker.Matches.Select(request => request.Number).SequenceEqual([12, 31]) &&
            picker.Selected is { Number: 12, Ticket: "40002" } && Render(picker, 100, 30).Contains("today"),
            "the release picker opens on your own merged pull requests, newest merge first, with the ticket from the branch and when it merged");
        picker.HandleKey(Press(ConsoleKey.Tab), 7);
        Check(picker.Tab == ReleaseTab.All && picker.Matches.Select(request => request.Number).SequenceEqual([44, 12, 50, 31]),
            "Tab shows everyone's merged pull requests, ordered by when they merged rather than by number");
        Type("bob");
        Check(picker.Matches.Single().Number == 50, "merged pull requests are searchable by author");
        Clear();
        Type("totals");
        Check(picker.Matches.Single().Number == 12, "merged pull requests are searchable by title");
        Clear();
        Type("40005");
        Check(picker.Selected?.Number == 44 && picker.Matches[^1] is { Number: 40005, Title: "" },
            "a ticket finds its merged pull request by branch, and the number can still be built by number");
        Clear();
        Type("#99");
        Check(picker.Selected is { Number: 99, Title: "" } && Render(picker, 100, 30).Contains("Build by number") && Render(picker, 100, 30).Contains("Not listed"),
            "a number that is not listed can still be chosen; the engine then says whether it can be built");
        picker.HandleKey(Press(ConsoleKey.Tab), 7);
        Clear();
        Type("50");
        Check(picker.Tab == ReleaseTab.Mine && picker.Selected is { Number: 50, Title: "Someone else's" },
            "a number typed on your tab that only everyone's lists is offered with its details");

        Task<string> Missing(string[] _) => throw new System.ComponentModel.Win32Exception("gh.exe was not found");
        var (noMine, noAll, missing) = ReleasePicker.Load(Missing, "master").GetAwaiter().GetResult();
        var offline = new ReleasePicker(noMine, noAll, missing, "master", now);
        var offlineScreen = Render(offline, 100, 30);
        foreach (var character in "123") offline.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        Check(missing == "The GitHub CLI (gh) is not installed" && offlineScreen.Contains("not installed. Type a pull request number to build it anyway") &&
            offlineScreen.Contains("No merged pull requests are listed") && offline.Selected?.Number == 123,
            "without the GitHub CLI the release picker says why and still takes a number");
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var lines = Render(new ReleasePicker(mine, all, null, "master", now), width, height).Split('\n');
            Check(lines.Length <= height && lines.All(line => line.Length <= width) && lines.Any(line => line.Contains("Esc")) &&
                lines.Count(line => line.Contains("> ")) == 1 && lines.Any(line => line.Contains("MINE 2") && line.Contains("ALL 4")),
                $"the release picker fits {width}x{height} with its tabs and keys visible");
        }

        using (var output = new StringWriter())
        {
            var chosen = new ReleasePicker(mine, all, null, "master", now).Show(LiveConsole(output), Script(null, Press(ConsoleKey.DownArrow), null, Press(ConsoleKey.Enter, '\r')));
            var paints = ReleasePaints(output.ToString());
            Check(chosen == 31 && paints.Length == 2 && PaintedRows(paints[1]).Length is > 0 and <= 6,
                "Enter chooses the highlighted merged pull request, and moving the highlight repaints only the rows and details it changes");
        }
        using (var output = new StringWriter())
            Check(new ReleasePicker(mine, all).Show(LiveConsole(output), Script(Press(ConsoleKey.Escape))) is null, "Esc leaves the release picker without fetching anything");
    }

    private static ReleaseResult ReleasePreviewFixture(bool errors = false, bool reuse = false, string subfolder = "MyMeyer") => new()
    {
        Subfolder = subfolder, ReuseDependencies = reuse,
        PullRequest = new(21, "27220 Stop Pay Removed", "alice", "https://github.com/fixture/MyMeyer/pull/21", "feature/stop-pay", "master", "MERGED",
            new string('1', 40), "2026-11-19T10:00:00Z", new string('2', 40)),
        BaseBranch = "master", BaseCommit = "abcdef1234567890abcdef1234567890abcdef12", Name = "27220", Folder = @"C:\fixture\Documents\27220",
        Changes =
        [
            new("Page/Value.cs", "Modified", ReleaseStatus.Same, ReleaseKind.Compiled, []),
            new("Page/Again.css", "Modified", ReleaseStatus.ChangedAgain, ReleaseKind.Content, [new("a1a1a1a1a1a1", "a1 again")]),
            new("Page/View.ascx", "Modified", ReleaseStatus.Same, ReleaseKind.Content, []),
            new("Page/Reverted.js", "Modified", ReleaseStatus.NotOnBase, ReleaseKind.Content, []),
            new("Page/Old.js", "Deleted", ReleaseStatus.Deleted, ReleaseKind.Content, []),
            new("Db/Proc.sql", "Modified", ReleaseStatus.Same, ReleaseKind.NotIncluded, [])
        ],
        Projects = [new(@"Page\Page.csproj", "Page", [new("c0ffee1234567", "Other merged change"), new("beef123456789", "a4 page")])],
        Packages = [new("CultivateReleasePackage.dll", "2.0.0.0", @"..\packages\CultivateReleasePackage.2.0.0\lib\net471\CultivateReleasePackage.dll", @"Shop\Shop.csproj")],
        Content = ["Page/Again.css", "Page/View.ascx"],
        NotIncluded = [new("Db/Proc.sql", "SQL script: release it through the database process; Cultivate never runs SQL.")],
        Deleted = [new("Page/Old.js", "Not on master: nothing to upload. Remove it from production by hand if it was released.")],
        SupportProjects = [new(@"Core\Core.csproj", "Core", @"Page references ..\bin\Core.dll, a build output Git does not track", true, true)],
        Existing = [new("notes.txt", 4, "Not written by Cultivate"), new("Page.dll", 5120, "Written by Cultivate for PR #20 on 2026-11-18 10:00")],
        Warnings = ["Page/Again.css changed again on master after the merge (a1a1a1a); master's version is used."],
        Errors = errors ? [@"Page\Page.csproj has no webfarm copy target."] : [],
        Pull = new("master", @"C:\fixture\MyMeyer", "4044096" + new string('4', 33), "4044096" + new string('4', 33), 7, ReleasePullOutcome.Behind,
            @"master in C:\fixture\MyMeyer is 7 commit(s) behind origin; building pulls it.")
    };

    private static void CheckReleasePreview()
    {
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
        static string Shot(ReleasePreview preview) => Flat(Capture(preview.Render(120, 200), 120, 200));
        // The body's lines: below the header's four, above the position and keys lines, without the padding under a short body.
        static List<string> Body(ReleasePreview preview)
        {
            var body = Capture(preview.Render(120, 200), 120, 200).Split('\n').Select(line => line.TrimEnd()).Skip(4).SkipLast(2).ToList();
            while (body.Count != 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            return body;
        }
        static bool Spaced(List<string> body) => body.Count != 0 && body[0].Length != 0 && !body.Zip(body.Skip(1)).Any(pair => pair.First.Length == 0 && pair.Second.Length == 0);
        var preview = new ReleasePreview(ReleasePreviewFixture(), ["Page.dll"]);
        var full = Shot(preview);
        string[] sections = ["DLLS TO BUILD (1)", "CHANGED PACKAGES (1)", "FRONTEND FILES (2)", "NOT INCLUDED (2)", "DELETED BY THE PULL REQUEST (1)",
            @"ALREADY IN THE FOLDER (2) C:\fixture\Documents\27220", "WARNINGS (1)"];
        Check(sections.All(full.Contains) && sections.Select(section => full.IndexOf(section, StringComparison.Ordinal)).Order().SequenceEqual(sections.Select(section => full.IndexOf(section, StringComparison.Ordinal))),
            "the release preview lists the DLLs, packages and frontend files, then what is left out, the files already in the folder and warnings, in that order");
        Check(full.Contains("PR #21: 27220 Stop Pay Removed DLLS TO BUILD (1) Page.dll Also carries 2 other merged changes, which go to production with it: " +
                "c0ffee1 Other merged change beef123 a4 page CHANGED PACKAGES (1)") && Body(preview)[0] == "PR #21: 27220 Stop Pay Removed" && Spaced(Body(preview)),
            "the preview starts with the pull request and goes straight to its DLLs, each named alone with the other merged changes it takes to production");
        string[] gone = ["TICKET FOLDER", "One subfolder per repository", "Building writes", "Nothing is in the folder yet", "BUILT FIRST", "built from master at",
            "merged 2026-", "by alice", @"Page.dll Page\Page.csproj", "CONTENT FILES", @"FOLDER C:\fixture", "Carries no other merged changes", "None:", "Core.dll",
            "behind origin", "Dependencies are taken"];
        Check(!gone.Any(full.Contains), "the preview leaves out the ticket folder, the folder summary, the pull request's details, the routine pull, the dependencies and empty sections");
        Check(full.Contains("CultivateReleasePackage.dll 2.0.0.0 for Shop\\Shop.csproj"), "changed packages show their versions");
        Check(full.Contains("FRONTEND FILES (2) Page/Again.css not exactly as on the pull request; master's version is copied: a1a1a1a a1 again Page/View.ascx"),
            "frontend files are listed, and one other merged work also changed is flagged with the commits that changed it");
        Check(full.Contains("Db/Proc.sql SQL script: release it through the database process") && full.Contains("Page/Reverted.js Undone on master since the merge") &&
            full.Contains("Page/Old.js Not on master: nothing to upload. Remove it from production by hand"),
            "files left out and files deleted are each listed with their reason, never only counted");
        Check(full.Contains(@"ALREADY IN THE FOLDER (2) C:\fixture\Documents\27220 notes.txt 4 B, Not written by Cultivate " +
                "Page.dll 5 KB, Written by Cultivate for PR #20 on 2026-11-18 10:00, replaced by this build, C removes it first " +
                "C removes the 1 file Cultivate wrote here that nobody has changed since, then builds. Every other file is kept. WARNINGS (1)"),
            "a folder that already holds files is listed after what is left out, with what this build replaces and what removing earlier files would remove");
        var empty = ReleasePreviewFixture();
        empty.Existing.Clear();
        var emptyScreen = Shot(new ReleasePreview(empty, []));
        Check(!emptyScreen.Contains("ALREADY IN THE FOLDER") && !emptyScreen.Contains(empty.Folder) && emptyScreen.Contains("DELETED BY THE PULL REQUEST (1) Page/Old.js") &&
            emptyScreen.Contains("WARNINGS (1)"), "an empty folder is not mentioned at all");
        Check(full.Contains("! Page/Again.css changed again on master") && full.Contains("Enter build") && full.Contains("C remove earlier files, then build") &&
            !full.Contains("CANNOT BUILD"), "warnings are listed, and a buildable preview offers building and removing earlier files first");

        foreach (var (outcome, detail) in new[] { (ReleasePullOutcome.LocalCommits, @"master in C:\fixture\MyMeyer has 2 local commit(s) origin lacks; building does not pull it."),
            (ReleasePullOutcome.NotUpdated, @"master in C:\fixture\MyMeyer is checked out with changes; building does not pull it.") })
        {
            var held = ReleasePreviewFixture();
            held.Pull = held.Pull! with { Outcome = outcome, Detail = detail };
            Check(Shot(new ReleasePreview(held, ["Page.dll"])).Contains($"PR #21: 27220 Stop Pay Removed {detail} DLLS TO BUILD (1)"),
                $"a pull that {(outcome == ReleasePullOutcome.LocalCommits ? "finds local commits" : "cannot update master")} is shown under the pull request, unlike a routine one");
        }
        var reused = Body(new ReleasePreview(ReleasePreviewFixture(reuse: true), ["Page.dll"]));
        Check(Flat(string.Join(" ", reused)).Contains("PR #21: 27220 Stop Pay Removed Dependencies are taken from your checkout instead of built from master; " +
                "Cultivate cannot verify they match master. DLLS TO BUILD (1)") && !reused.Any(line => line.Contains("Core.dll")) && Spaced(reused),
            "dependencies taken from the checkout are flagged in one line under the pull request, without listing them");

        var nothing = ReleasePreviewFixture();
        nothing.Projects.Clear();
        nothing.Packages.Clear();
        nothing.Content.Clear();
        var nothingScreen = Shot(new ReleasePreview(nothing, []));
        var sql = new ReleaseResult { PullRequest = nothing.PullRequest, Kind = ReleaseRepositoryKind.Sql, BaseBranch = "main", Folder = @"C:\fixture\Documents\27220\SQL" };
        var sqlBody = Body(new ReleasePreview(sql, []));
        Check(nothingScreen.Contains("PR #21: 27220 Stop Pay Removed Nothing to release: the pull request changed no DLL, package or frontend file. NOT INCLUDED (2)") &&
            !nothingScreen.Contains("DLLS TO BUILD") && !nothingScreen.Contains("CHANGED PACKAGES") && !nothingScreen.Contains("FRONTEND FILES") &&
            sqlBody.SequenceEqual(["PR #21: 27220 Stop Pay Removed", "", "Nothing to release: the pull request changed no SQL script."]),
            "a pull request with nothing to release says so in one line instead of empty sections, for code and for SQL");

        var several = new ReleasePreview(new ReleaseRun
        {
            Folder = @"C:\fixture\Documents\27220", Releases = [ReleasePreviewFixture(), ReleasePreviewFixture(errors: true, subfolder: "Shop")],
            Skipped = [new("https://github.com/fixture/Shop/pull/30", "Shop", 30, "Shop pull request #30 is not merged yet.")]
        }, [["Page.dll"], []]);
        var severalBody = Body(several);
        var severalScreen = Flat(string.Join(" ", severalBody));
        Check(severalBody[0] == "LINKED PULL REQUESTS NOT BUILT (1)" && severalBody.Contains("━━ MYMEYER") && severalBody.Contains("━━ SHOP") && Spaced(severalBody) &&
            severalScreen.StartsWith("LINKED PULL REQUESTS NOT BUILT (1) Shop #30 Shop pull request #30 is not merged yet. ━━ MYMEYER PR #21: 27220 Stop Pay Removed DLLS TO BUILD (1)",
                StringComparison.Ordinal) &&
            severalScreen.Contains("━━ SHOP PR #21: 27220 Stop Pay Removed Cannot be built, so building skips it. CANNOT BUILD below says why. DLLS TO BUILD (1)") &&
            !severalScreen.Contains("These release files cannot be built") && !severalScreen.Contains("TICKET FOLDER") && !severalScreen.Contains("in MyMeyer, and"),
            "several repositories start with the linked pull requests not built, then each repository under its name alone, one that cannot build saying building skips it");

        var screen = new ReleasePreview(ReleasePreviewFixture(), ["Page.dll"]);
        var seen = new StringBuilder();
        var pages = 0;
        var fits = true;
        while (true)
        {
            var shot = Capture(screen.Render(80, 24), 80, 24);
            var lines = shot.Split('\n');
            fits &= lines.Length <= 24 && lines.All(line => line.Length <= 80) && lines[^1].Contains("Esc back");
            seen.AppendLine(shot);
            pages++;
            var top = screen.Top;
            screen.HandleKey(Press(ConsoleKey.PageDown), 24);
            if (screen.Top == top) break;
        }
        var scrolled = seen.ToString();
        Check(fits && pages > 1 && scrolled.Contains("Lines 1-17 of") && scrolled.Contains("more below") && scrolled.Contains("end: arrows or PgUp scroll") &&
            sections.Select(section => section.Split(' ')[0]).All(scrolled.Contains), "a long preview pages through every section in an 80x24 terminal with its keys always visible");
        Check(screen.HandleKey(Press(ConsoleKey.Enter, '\r'), 24) == ReleaseChoice.Build && screen.HandleKey(Press(ConsoleKey.C, 'c'), 24) == ReleaseChoice.Clean &&
            screen.HandleKey(Press(ConsoleKey.Escape), 24) == ReleaseChoice.Back && screen.HandleKey(Press(ConsoleKey.X, 'x'), 24) is null,
            "Enter builds, C removes earlier files and builds, Esc goes back, and other keys only scroll");
        var nothingToRemove = new ReleasePreview(ReleasePreviewFixture(), []);
        Check(nothingToRemove.HandleKey(Press(ConsoleKey.C, 'c'), 24) is null && !Capture(nothingToRemove.Render(120, 200), 120, 200).Contains("C remove"),
            "removing earlier files is offered only when the folder holds unchanged files Cultivate wrote");
        var blocked = new ReleasePreview(ReleasePreviewFixture(errors: true), ["Page.dll"]);
        var blockedScreen = Flat(Capture(blocked.Render(120, 200), 120, 200));
        Check(blockedScreen.Contains("These release files cannot be built. CANNOT BUILD below says why.") && blockedScreen.Contains("CANNOT BUILD (1) Page\\Page.csproj has no webfarm copy target.") &&
            !blockedScreen.Contains("Enter build") && !blockedScreen.Contains("C remove") && blocked.HandleKey(Press(ConsoleKey.Enter, '\r'), 24) is null &&
            blocked.HandleKey(Press(ConsoleKey.C, 'c'), 24) is null, "a preview with errors shows them and offers no build");

        using (var output = new StringWriter())
        {
            var choice = new ReleasePreview(ReleasePreviewFixture(), ["Page.dll"]).Show(LiveConsole(output),
                Script(null, Press(ConsoleKey.X, 'x'), null, Press(ConsoleKey.DownArrow), null, Press(ConsoleKey.Escape)));
            var paints = ReleasePaints(output.ToString());
            Check(choice == ReleaseChoice.Back && paints.Length == 2 && PaintedRows(paints[1]).Length > 0 && PaintedRows(paints[1]).All(row => row is > 4 and < 29),
                "a key that does nothing repaints nothing, and scrolling the preview rewrites only its body and position lines, not the header or keys");
        }
    }

    private static void CheckReleaseProgress()
    {
        var preview = ReleasePreviewFixture();
        var view = new ReleaseBuildView(preview);
        view.Report("Fetching PR #21 and master from origin");
        view.Report("Building Page (1/1)");
        var screen = Regex.Replace(Capture(view.Render(80, 24), 80, 24), @"\s+", " ");
        Check(screen.Contains("Building Page (1/1)") && screen.Contains("Esc cancel") &&
            screen.Contains("Cancelling stops before anything is written to the folder. Once writing starts, it finishes.") && screen.Contains(preview.Folder),
            "the build screen shows its progress, the folder, and what Esc does");
        view.Cancelling = true;
        Check(Capture(view.Render(80, 24), 80, 24).Contains("Cancelling. The build stops at its next step"), "after Esc the build screen says it is cancelling");

        using (var output = new StringWriter())
        {
            var started = false;
            var (result, escaped) = ReleaseScreen.Build(LiveConsole(output), Script(() => started, null, Press(ConsoleKey.Escape)), preview, async (_, progress, cancel) =>
            {
                progress("Fetching PR #21");
                started = true;
                await Task.Delay(Timeout.Infinite, cancel);
                throw new InvalidOperationException("unreachable");
            });
            Check(result is null && escaped && Shown(output.ToString()).Contains("Fetching PR #21"), "Esc on the build screen cancels the build through its token");
        }
        using (var output = new StringWriter())
        {
            var written = ReleasePreviewFixture();
            var (result, escaped) = ReleaseScreen.Build(LiveConsole(output), Script(() => false), preview, async (_, progress, _) =>
            {
                progress("Building Page (1/1)");
                await Task.Delay(400);
                progress("Writing 2 file(s)");
                await Task.Delay(400);
                return written;
            });
            var shown = Shown(output.ToString());
            Check(result == written && !escaped && shown.Contains("Building Page (1/1)") && shown.Contains("Writing 2 file(s)"),
                "the build screen shows each progress message as it arrives and closes when the build ends");
        }

        var support = Diagnostics.Attach(new InvalidOperationException("MSBuild failed."), new FailureContext("Build") { Project = @"core\core.csproj" });
        var release = Diagnostics.Attach(new InvalidOperationException("MSBuild failed."), new FailureContext("Build") { Project = @"Page\Page.csproj" });
        Check(ReleaseScreen.FailedSupport(preview, support)?.Assembly == "Core" && ReleaseScreen.FailedSupport(preview, release) is null &&
            ReleaseScreen.FailedSupport(preview, new IOException("disk full")) is null, "only a failure in a support project counts as a dependency failure");
    }

    /// <summary>The card after building: under each pull request its files grouped by kind with their sizes in one column, for one repository and several.</summary>
    private static void CheckReleaseResult()
    {
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
        // The card's lines inside its border, without the padding or trailing spaces.
        static List<string> Lines(string captured) => [.. captured.Split('\n')[1..^1].Select(line => line[2..^1].TrimEnd())];
        static bool Spaced(List<string> lines) => lines.Count != 0 && lines[^1].Length != 0 && !lines.Zip(lines.Skip(1)).Any(pair => pair.First.Length == 0 && pair.Second.Length == 0);
        // The column each file row's size ends at.
        static List<int> SizeEnds(List<string> lines) =>
            [.. lines.Select(line => Regex.Match(line, @"^  \S+ +\d+ [KMG]?B(?=  |$)")).Where(match => match.Success).Select(match => match.Length)];
        string[] gone = ["Wrote ", "One subfolder per repository", "Upload every file", "Release these scripts through the database process", "Pulled master", "KIND",
            "VERSION", " new", "Project DLL", "Frontend file", "Also in the folder and kept"];

        var built = ReleasePreviewFixture();
        built.Written = true;
        built.Seconds = 42;
        built.Files.AddRange([new("Page.dll", ReleaseKind.ProjectDll, @"Page\Page.csproj", 5120, "hash", "1.0.0.0", true),
            new("View.ascx", ReleaseKind.Content, @"Page\View.ascx", 7, "hash", null, false)]);
        built.Existing.RemoveAll(file => file.Name == "Page.dll");
        built.OutsideReferences.Add(new("Helpers.dll", @"C:\fixture\checkout\bin\Helpers.dll", "1.0.0.0", new DateTime(2026, 11, 1, 9, 0, 0), @"Page\Page.csproj"));
        built.Pull = built.Pull! with { After = "2bc2fa7" + new string('2', 33), Outcome = ReleasePullOutcome.Pulled,
            Detail = @"Pulled master: 7 new commit(s), 4044096 → 2bc2fa7 (C:\fixture\MyMeyer)" };
        var captured = Capture(ReleaseScreen.ResultCard(built, true), 120, 40);
        var lines = Lines(captured);
        Check(captured.Contains("RELEASE FILES READY") && lines.SequenceEqual([
                "2 files written  in 42 s", @"C:\fixture\Documents\27220", "", "PR #21: 27220 Stop Pay Removed",
                "DLLS (1)", "  Page.dll   5 KB  replaced", "FRONTEND FILES (1)", "  View.ascx   7 B",
                "ALSO IN THE FOLDER (1)  kept, not written by this build", "  notes.txt  Not written by Cultivate",
                "COMPILED AGAINST FILES FROM OUTSIDE MASTER (1)", @"  Helpers.dll 1.0.0.0  C:\fixture\checkout\bin\Helpers.dll, modified 2026-11-01 09:00, for Page\Page.csproj",
                "WARNINGS (1)", "  ! Page/Again.css changed again on master after the merge (a1a1a1a); master's version is used.",
                "Esc came after writing had started, so the build finished."]),
            "one repository's result gives the files written and the time, its folder and pull request, its DLLs and frontend files with their sizes and " +
            "which replaced a file, then the files kept, the outside references and the warnings");
        Check(!gone.Any(captured.Contains) && !captured.Contains("REMOVED FIRST") && !captured.Contains("CHANGED PACKAGES") && !captured.Contains("OTHER FILES") && Spaced(lines),
            "the result has no table, file kinds, 'new', upload note, routine pull or empty group, and no doubled or trailing blank line");
        using (var ansi = new StringWriter())
        {
            LiveConsole(ansi).Write(ReleaseScreen.ResultCard(built));
            Check(ansi.ToString().Contains("\u001b]8;") && ansi.ToString().Contains("file:///C:/fixture/Documents/27220"), "the result links the release folder as a file: address");
        }

        var more = ReleasePreviewFixture();
        more.Written = true;
        more.Warnings.Clear();
        more.Existing.Clear();
        more.Removed.Add("Old.dll");
        more.Files.AddRange([new("Page.dll", ReleaseKind.ProjectDll, @"Page\Page.csproj", 5120, "hash", "1.0.0.0", false),
            new("CultivateReleasePackage.dll", ReleaseKind.Package, @"..\packages\CultivateReleasePackage.dll", 2048, "hash", "2.0.0.0", true),
            new("Layout.rdl", "Report", @"Reports\Layout.rdl", 3072, "hash", null, false)]);
        var moreLines = Lines(Capture(ReleaseScreen.ResultCard(more), 120, 40));
        var moreCard = Flat(string.Join(" ", moreLines));
        Check(moreCard.Contains("PR #21: 27220 Stop Pay Removed DLLS (1) Page.dll 5 KB CHANGED PACKAGES (1) CultivateReleasePackage.dll 2 KB replaced 2.0.0.0 " +
                "OTHER FILES (1) Layout.rdl 3 KB REMOVED FIRST (1) Old.dll") && moreLines[^1] == "  Old.dll" && !moreCard.Contains("1.0.0.0"),
            "a changed package shows its version and a project DLL does not, a file of any other kind is still listed, and the files removed first follow");
        Check(!new[] { "ALSO IN THE FOLDER", "COMPILED AGAINST", "WARNINGS", "NOT BUILT", "Esc came" }.Any(moreCard.Contains) && Spaced(moreLines),
            "the files kept, outside references, warnings and links not built appear only when there are some");

        foreach (var (outcome, detail) in new[] { (ReleasePullOutcome.LocalCommits, "Did not pull master: it has 2 local commit(s) not on origin."),
            (ReleasePullOutcome.NotUpdated, @"Did not pull master: it would overwrite local files in C:\fixture\MyMeyer.") })
        {
            var held = ReleasePreviewFixture();
            held.Written = true;
            held.Pull = held.Pull! with { Outcome = outcome, Detail = detail };
            held.Warnings.Add(@"Your local master in C:\fixture\MyMeyer was not updated to origin's 2bc2fa7. The release files were still built from origin's 2bc2fa7; pull master by hand.");
            var heldCard = Flat(string.Join(" ", Lines(Capture(ReleaseScreen.ResultCard(held), 120, 40))));
            Check(heldCard.Contains("WARNINGS (2) ! Page/Again.css changed again on master after the merge (a1a1a1a); master's version is used. ! " + held.Warnings[1]) &&
                !heldCard.Contains("Did not pull"), $"a pull that {(outcome == ReleasePullOutcome.LocalCommits ? "finds local commits" : "cannot update master")} " +
                "is told by the build's warning, not by a line of its own");
        }

        var nothing = ReleasePreviewFixture();
        nothing.Written = true;
        nothing.Warnings.Clear();
        nothing.Existing.Clear();
        Check(Lines(Capture(ReleaseScreen.ResultCard(nothing), 120, 40)).SequenceEqual(
                ["0 files written  in 0 s", @"C:\fixture\Documents\27220", "", "PR #21: 27220 Stop Pay Removed", "Nothing needed releasing."]),
            "a release that wrote nothing says nothing needed releasing under its pull request");

        static ReleaseResult Release(string subfolder, int number, string title, bool written, params ReleaseFile[] files)
        {
            var release = new ReleaseResult
            {
                PullRequest = ReleasePreviewFixture().PullRequest with { Number = number, Title = title }, Subfolder = subfolder, BaseBranch = "master",
                Folder = @"C:\fixture\Documents\27220\" + subfolder, Written = written
            };
            release.Files.AddRange(files);
            return release;
        }
        const string longest = "SupersedeClassIDMatrixPriceLevelAdjustmentQueueItems.sql";
        var run = new ReleaseRun
        {
            Folder = @"C:\fixture\Documents\27220", Seconds = 54,
            Releases =
            [
                Release("MyMeyer", 21, "27220 Stop Pay Removed", true, new("CustomerMatrix.dll", ReleaseKind.ProjectDll, @"CustomerMatrix\CustomerMatrix.csproj", 58 * 1024, "hash",
                    "1.0.0.0", false), new("ClassIDMatrix.ascx", ReleaseKind.Content, @"CustomerMatrix\ClassIDMatrix.ascx", 15 * 1024, "hash", null, false)),
                Release("SQL", 5323, "27220 Extend the matrix queue", true, new(longest, ReleaseKind.Sql, @"MeyerLogic\" + longest, 2048, "hash", null, false),
                    new("Queue.sql", ReleaseKind.Sql, @"MeyerLogic\Queue.sql", 1024, "hash", null, true)),
                Release("Shop", 30, "27220 Shop totals", false)
            ],
            Skipped = [new("https://github.com/fixture/Reports/pull/40", "Reports", 40, "Reports pull request #40 is closed without being merged.")]
        };
        run.Releases[2].Failure = "Build: error CS0029: Cannot implicitly convert type 'int' to 'string'.";
        run.Releases[2].Pull = new("master", null, "", "", 0, ReleasePullOutcome.NotUpdated, "Did not pull master: its files were not built.");
        var severalCaptured = Capture(ReleaseScreen.ResultCard(run), 120, 60);
        var several = Lines(severalCaptured);
        static string Row(string name, string rest) => $"  {name.PadRight(longest.Length)}  {rest}";
        Check(severalCaptured.Contains("RELEASE FILES PARTLY WRITTEN") && several.SequenceEqual([
                "4 files written  in 54 s", @"C:\fixture\Documents\27220", "",
                "━━ MYMEYER  2 files", "PR #21: 27220 Stop Pay Removed", "DLLS (1)", Row("CustomerMatrix.dll", "58 KB"), "FRONTEND FILES (1)", Row("ClassIDMatrix.ascx", "15 KB"), "",
                "━━ SQL  2 files", "PR #5323: 27220 Extend the matrix queue", "SQL SCRIPTS (2)", Row(longest, " 2 KB"), Row("Queue.sql", " 1 KB  replaced"), "",
                "━━ SHOP  not written", "PR #30: 27220 Shop totals", "Build: error CS0029: Cannot implicitly convert type 'int' to 'string'.",
                "Did not pull master: its files were not built.", "",
                "LINKED PULL REQUESTS NOT BUILT (1)", "  Reports #40  Reports pull request #40 is closed without being merged."]),
            "several repositories' result gives the files written, the ticket folder, then each repository under its name with its file count or 'not written' " +
            "and its failure, with a master that could not be pulled, then the linked pull requests not built");
        Check(several.Count(line => line.Contains(@"C:\fixture\Documents\27220")) == 1 && !gone.Any(severalCaptured.Contains) &&
            Spaced(several), "several repositories' result names the ticket folder once, with no folder or note per repository and no doubled or trailing blank line");
        run.Releases[2].Pull = run.Releases[2].Pull! with { Outcome = ReleasePullOutcome.Pulled, Detail = "Pulled master: 2 new commit(s)." };
        Check(!Capture(ReleaseScreen.ResultCard(run), 120, 60).Contains("Pulled master"), "a repository that was not written says nothing of a routine pull");
        run.Releases[2].Pull = run.Releases[2].Pull! with { Outcome = ReleasePullOutcome.NotUpdated, Detail = "Did not pull master: its files were not built." };
        var ends = SizeEnds(several);
        var wide = SizeEnds(Lines(Capture(ReleaseScreen.ResultCard(run), 200, 60)));
        Check(ends.Count == 4 && ends.All(end => end == 2 + longest.Length + 2 + "58 KB".Length) && wide.SequenceEqual(ends),
            "file sizes end in one column across every repository, two spaces after the card's longest file name, and a 200-column console does not stretch it");
        var narrow = Capture(ReleaseScreen.ResultCard(run), 50, 80);
        Check(narrow.Split('\n').All(line => line.Length <= 50) && Regex.Replace(narrow, @"[\s│╭╮╰╯─]+", "").Contains(longest) &&
            !narrow.Split('\n').Any(line => line.Contains(longest)), "on a console narrower than a row, the row wraps and keeps the whole file name");
    }

    /// <summary>
    /// The screen end to end on the release files fixture: a full build, Esc during a build, and a dependency that fails to build, with the GitHub
    /// CLI faked and a bridge request treated as a failure.
    /// </summary>
    private static async Task CheckReleaseScreenRun(ReleaseFixture fixture)
    {
        var (settings, git, output) = (fixture.Settings, fixture.Git, fixture.Output);
        static string Item(int number, string title, string branch, string merged) =>
            JsonSerializer.Serialize(new { number, title, author = new { login = "alice" }, headRefName = branch, mergedAt = merged });
        var mineJson = $"[{Item(20, "Use the new core value", "38636-2", "2026-11-19T10:00:00Z")}]";
        var allJson = $"[{Item(20, "Use the new core value", "38636-2", "2026-11-19T10:00:00Z")},{Item(22, "39050 2 HD - Styles", "styles", "2026-11-19T11:00:00Z")}]";
        var listed = 0;
        Task<string> Gh(string[] arguments)
        {
            if (arguments is not ["pr", "list", ..]) return fixture.Gh(arguments);
            Interlocked.Increment(ref listed);
            return Task.FromResult(arguments.Contains("@me") ? mineJson : allJson);
        }

        // Any bridge request names the webfarm; this one is only for this run, and the screen must never send it.
        var webfarm = settings.WebfarmRoot;
        settings.WebfarmRoot = Path.Combine(Path.GetDirectoryName(webfarm)!, "no-bridge-farm");
        var requests = Path.Combine(Files.Home, "Requests");
        Directory.CreateDirectory(requests);
        var bridged = new List<string>();
        using var watcher = new FileSystemWatcher(requests) { EnableRaisingEvents = true };
        watcher.Created += (_, created) =>
        {
            for (var attempt = 0; attempt < 50; attempt++)
                try
                {
                    if (File.ReadAllText(created.FullPath).Contains("no-bridge-farm")) lock (bridged) bridged.Add(created.FullPath);
                    return;
                }
                catch (IOException) { Thread.Sleep(5); }
        };
        var logs = Path.Combine(Files.Home, "Logs");
        int BridgeLogs() => Directory.Exists(logs) ? Directory.GetFiles(logs, "bridge-*").Length : 0;
        var bridgeLogs = BridgeLogs();

        var offered = new List<List<string>>();
        var picks = new Queue<string>();
        var explored = new List<string>();
        var questions = new List<(string Question, bool Default)>();
        var answers = new Queue<bool>();
        var pauses = 0;
        var screen = new ReleaseScreen(settings, git, Gh);
        async Task<string> Drive(Func<int, ConsoleKeyInfo?> keys)
        {
            using var console = new StringWriter();
            await screen.Show(LiveConsole(console), keys, (_, actions) =>
            {
                lock (offered) offered.Add(actions.Select(action => action.Label).ToList());
                return picks.Dequeue();
            }, (question, preset) =>
            {
                lock (questions) questions.Add((question, preset));
                return answers.Dequeue();
            }, () => Interlocked.Increment(ref pauses), explored.Add);
            return console.ToString();
        }
        List<string> Listing(string folder) => [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Order(Files.Comparer)];

        // PR 20 again: it is first on your tab, and its folder holds the files the engine's run wrote.
        var folder20 = Path.Combine(output, "38636");
        var files20 = Path.Combine(folder20, "MyMeyer");
        var writtenBefore = File.GetLastWriteTimeUtc(Path.Combine(files20, "Page.dll"));
        picks.Enqueue(ReleaseScreen.OpenFolder);
        picks.Enqueue(ReleaseScreen.Back);
        var run = await Drive(Script(Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Enter, '\r'), () => offered.Count == 2, Press(ConsoleKey.Escape)));
        var shown = Shown(run);
        Check(listed == 2 && shown.Contains("MINE 1") && shown.Contains("DLLS TO BUILD (1)") && shown.Contains("other merged changes, which go to production with it") &&
            shown.Contains("ALREADY IN THE FOLDER (") && shown.Contains("C removes it first"),
            "the screen lists merged pull requests, then previews the chosen one's DLL with the other merged changes it carries, and the files already in its folder");
        Check(Listing(files20).SequenceEqual(["Page.dll", "View.ascx"], Files.Comparer) && File.GetLastWriteTimeUtc(Path.Combine(files20, "Page.dll")) > writtenBefore &&
            shown.Contains("Building Page (3/3, release file)") && shown.Contains("RELEASE FILES READY") && shown.Contains("DLLS (1) Page.dll") && shown.Contains("replaced"),
            "building from the screen shows live progress and writes the release folder, and the result lists the files it replaced");
        Check(run.Contains("\u001b]8;") && run.Contains(new Uri(files20).AbsoluteUri) && offered.All(actions => actions.SequenceEqual([ReleaseScreen.OpenFolder, ReleaseScreen.Back])) &&
            explored.SequenceEqual([files20]), "the result links the folder and opens it in Explorer through the injected launcher, then goes back");

        // PR 22, typed: Esc during the build cancels it before anything is written.
        var folder22 = Path.Combine(output, "39050");
        var paused = pauses;
        run = Shown(await Drive(Script([.. Typed("22"), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Escape), () => pauses > paused,
            Press(ConsoleKey.Escape), Press(ConsoleKey.Escape)])));
        Check(!Directory.Exists(folder22) && run.Contains("Cancelled.") && run.Contains("Nothing was written to " + folder22) && offered.Count == 2,
            "Esc during the build cancels it, writes nothing, and returns to the preview");

        // PR 24 needs Helpers, which no longer compiles on master; PR 24 is not listed, so its number is typed.
        var broken = await fixture.Commit(fixture.Master, "break helpers", [(@"Helpers\Value.cs", "public class HelperValue { broken }")]);
        var h24 = await fixture.Commit(broken, "pr24", [(@"Page\View.ascx", "view 24"), (@"Page\Other.cs", "public class PageOther { public static int Seen => 24; }")]);
        await fixture.Publish(h24, "refs/pull/24/head");
        var m24 = await fixture.Squash(broken, h24, "24001 Page for 24 (#24)");
        await fixture.Publish(m24, "refs/heads/master");
        fixture.Pull(24, "24001 Page for 24", "24001", m24, h24);
        var folder24 = Path.Combine(output, "24001");
        answers.Enqueue(false);
        run = Shown(await Drive(Script([.. Typed("24"), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Enter, '\r'), () => questions.Count == 1,
            Press(ConsoleKey.Escape), Press(ConsoleKey.Escape)])));
        Check(questions.SequenceEqual([(ReleaseScreen.ReuseQuestion, false)]) && run.Contains("DEPENDENCY FAILED") && run.Contains("Helpers.dll failed to build from master") &&
            run.Contains("cannot verify") && !Directory.Exists(folder24),
            "a dependency that fails to build offers, defaulting to No, to retry with the checkout's DLLs, and says they cannot be verified");

        answers.Enqueue(true);
        paused = pauses;
        run = Shown(await Drive(Script([.. Typed("24"), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Enter, '\r'), () => questions.Count == 2, () => pauses > paused,
            Press(ConsoleKey.Escape), Press(ConsoleKey.Escape)])));
        Check(questions.Count == 2 && run.Contains("Dependencies are taken from your checkout instead of built from master") && run.Contains("NewApi") && !Directory.Exists(folder24),
            "retrying with the checkout's DLLs builds at once, and a failure in a released DLL shows its diagnostics without offering again");

        await Task.Delay(200);
        settings.WebfarmRoot = webfarm;
        Check(bridged.Count == 0 && BridgeLogs() == bridgeLogs, "nothing in the Build release files screen asks the bridge for anything");
        Check(await fixture.Untouched() == fixture.Before, "the Build release files screen leaves the checkout, its refs and worktrees as they were");
    }
}
