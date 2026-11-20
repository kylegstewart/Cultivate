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

    private static ReleaseResult ReleasePreviewFixture(bool errors = false) => new()
    {
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
        var full = Flat(Capture(new ReleasePreview(ReleasePreviewFixture(), ["Page.dll"]).Render(120, 200), 120, 200));
        string[] sections = ["FOLDER C:\\fixture\\Documents\\27220", "DLLS TO BUILD (1)", "CHANGED PACKAGES (1)", "CONTENT FILES (2)", "NOT INCLUDED (2)",
            "DELETED BY THE PULL REQUEST (1)", "BUILT FIRST, NOT RELEASED (1)", "WARNINGS (1)"];
        Check(sections.All(full.Contains) && sections.Select(section => full.IndexOf(section, StringComparison.Ordinal)).Order().SequenceEqual(sections.Select(section => full.IndexOf(section, StringComparison.Ordinal))),
            "the release preview leads with the folder, then the DLLs, packages, content, what is left out, support projects and warnings, in that order");
        Check(full.Contains("Building writes 4 files: 1 DLL, 1 changed package and 2 content files.") && full.Contains("Already in the folder (2):") &&
            full.Contains("notes.txt 4 B, Not written by Cultivate") && full.Contains("Page.dll 5 KB, Written by Cultivate for PR #20 on 2026-11-18 10:00, replaced by this build, C removes it first") &&
            full.Contains("C removes the 1 file Cultivate wrote here that nobody has changed since, then builds. Every other file is kept."),
            "the preview says what lands in the folder, what is already there, what this build replaces, and what removing earlier files would remove");
        Check(full.Contains("Page.dll Page\\Page.csproj Also carries 2 other merged changes, which go to production with it: c0ffee1 Other merged change beef123 a4 page"),
            "each DLL lists the other merged changes it takes to production");
        Check(full.Contains("CultivateReleasePackage.dll 2.0.0.0 for Shop\\Shop.csproj"), "changed packages show their versions");
        Check(full.Contains("Page/Again.css not exactly as on the pull request; master's version is copied: a1a1a1a a1 again") && full.Contains("Page/View.ascx"),
            "a content file other merged work also changed is flagged with the commits that changed it");
        Check(full.Contains("Db/Proc.sql SQL script: release it through the database process") && full.Contains("Page/Reverted.js Undone on master since the merge") &&
            full.Contains("Page/Old.js Not on master: nothing to upload. Remove it from production by hand"),
            "files left out and files deleted are each listed with their reason, never only counted");
        Check(full.Contains(@"built from master at abcdef1 master in C:\fixture\MyMeyer is 7 commit(s) behind origin; building pulls it. FOLDER"),
            "the preview says, under the pull request, how far the local master is behind origin and that building pulls it");
        Check(full.Contains("Built from master so the DLLs above compile. They are not copied to the folder. Core.dll Page references ..\\bin\\Core.dll"),
            "support projects are described as built from master so the DLLs compile, and not released");
        Check(full.Contains("! Page/Again.css changed again on master") && full.Contains("Enter build") && full.Contains("C remove earlier files, then build") &&
            !full.Contains("CANNOT BUILD"), "warnings are listed, and a buildable preview offers building and removing earlier files first");

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

        var built = ReleasePreviewFixture();
        built.Written = true;
        built.Seconds = 42;
        built.Files.AddRange([new("Page.dll", ReleaseKind.ProjectDll, @"Page\Page.csproj", 5120, "hash", "1.0.0.0", true),
            new("View.ascx", ReleaseKind.Content, @"Page\View.ascx", 7, "hash", null, false)]);
        built.OutsideReferences.Add(new("Helpers.dll", @"C:\fixture\checkout\bin\Helpers.dll", "1.0.0.0", new DateTime(2026, 11, 1, 9, 0, 0), @"Page\Page.csproj"));
        built.Pull = built.Pull! with { After = "2bc2fa7" + new string('2', 33), Outcome = ReleasePullOutcome.Pulled,
            Detail = @"Pulled master: 7 new commit(s), 4044096 → 2bc2fa7 (C:\fixture\MyMeyer)" };
        var card = Regex.Replace(Capture(ReleaseScreen.ResultCard(built, true), 120, 40), @"\s+", " ");
        Check(card.Contains("RELEASE FILES READY") && card.Contains("Wrote 2 files for PR #21: 27220 Stop Pay Removed") && card.Contains(@"C:\fixture\Documents\27220") &&
            card.Contains($"Page.dll Project DLL {TidySizes.Format(5120)} 1.0.0.0 replaced") && card.Contains($"View.ascx Content file {TidySizes.Format(7)} new") &&
            card.Contains(@"Helpers.dll 1.0.0.0 C:\fixture\checkout\bin\Helpers.dll, modified 2026-11-01 09:00") && card.Contains("! Page/Again.css changed again on master") &&
            card.Contains("Also in the folder and kept: notes.txt (Not written by Cultivate)") && card.Contains("Esc came after writing had started"),
            "the result lists each file with its kind, size, version and whether it replaced one, outside references, and every warning again");
        using var ansi = new StringWriter();
        LiveConsole(ansi).Write(ReleaseScreen.ResultCard(built));
        Check(ansi.ToString().Contains("\u001b]8;") && ansi.ToString().Contains("file:///C:/fixture/Documents/27220"), "the result links the release folder as a file: address");
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
        var writtenBefore = File.GetLastWriteTimeUtc(Path.Combine(folder20, "Page.dll"));
        picks.Enqueue(ReleaseScreen.OpenFolder);
        picks.Enqueue(ReleaseScreen.Back);
        var run = await Drive(Script(Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Enter, '\r'), () => offered.Count == 2, Press(ConsoleKey.Escape)));
        var shown = Shown(run);
        Check(listed == 2 && shown.Contains("MINE 1") && shown.Contains("FOLDER " + folder20) && shown.Contains("DLLS TO BUILD (1)") &&
            shown.Contains("other merged changes, which go to production with it") && shown.Contains("C removes it first"),
            "the screen lists merged pull requests, then previews the chosen one's folder, its existing files, and the other merged changes its DLL carries");
        Check(Listing(folder20).SequenceEqual(["Page.dll", "View.ascx"], Files.Comparer) && File.GetLastWriteTimeUtc(Path.Combine(folder20, "Page.dll")) > writtenBefore &&
            shown.Contains("Building Page (3/3, release file)") && shown.Contains("RELEASE FILES READY") && shown.Contains("Project DLL") && shown.Contains("replaced"),
            "building from the screen shows live progress and writes the release folder, and the result lists the files it replaced");
        Check(run.Contains("\u001b]8;") && run.Contains(new Uri(folder20).AbsoluteUri) && offered.All(actions => actions.SequenceEqual([ReleaseScreen.OpenFolder, ReleaseScreen.Back])) &&
            explored.SequenceEqual([folder20]), "the result links the folder and opens it in Explorer through the injected launcher, then goes back");

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
        Check(questions.Count == 2 && run.Contains("Taken from your checkout instead of built from master") && run.Contains("NewApi") && !Directory.Exists(folder24),
            "retrying with the checkout's DLLs builds at once, and a failure in a released DLL shows its diagnostics without offering again");

        await Task.Delay(200);
        settings.WebfarmRoot = webfarm;
        Check(bridged.Count == 0 && BridgeLogs() == bridgeLogs, "nothing in the Build release files screen asks the bridge for anything");
        Check(await fixture.Untouched() == fixture.Before, "the Build release files screen leaves the checkout, its refs and worktrees as they were");
    }
}
