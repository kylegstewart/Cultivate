using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    private static async Task CheckCleanup(string home)
    {
        var root = Path.Combine(home, "cleanup");
        var git = new GitRepository(new Settings());
        var repository = await FixtureRepository(git, Path.Combine(root, "MyMeyer"), "master");
        var farm = Path.Combine(root, "farm");
        Directory.CreateDirectory(farm);
        Settings Fixture(string data) => new()
        {
            RepositoryRoot = repository, WebfarmRoot = farm, StateRoot = Path.Combine(data, "State"), LibraryRepositories = [], CleanupDays = 14
        };
        await CheckCleanupRetention(root, Fixture);
        await CheckCleanupDeployment(root, Fixture, farm);
        await CheckCleanupSafety(root, Fixture);
        await CheckCleanupWorktrees(root, Fixture);
    }

    /// <summary>Sets the write time of a file, or of a folder and everything in it, to <paramref name="age"/> before <paramref name="now"/>.</summary>
    private static void Age(string path, DateTimeOffset now, TimeSpan age)
    {
        var time = (now - age).UtcDateTime;
        if (Directory.Exists(path))
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                entry.LastWriteTimeUtc = time;
            Directory.SetLastWriteTimeUtc(path, time);
        }
        else File.SetLastWriteTimeUtc(path, time);
    }

    private static string Plant(string data, string relative, DateTimeOffset now, TimeSpan age, string content = "fixture")
    {
        var path = Path.Combine(data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Age(path, now, age);
        return path;
    }

    private static string PlantFolder(string data, string relative, DateTimeOffset now, TimeSpan age)
    {
        var path = Path.Combine(data, relative);
        Plant(path, @"nested\build.log", now, age);
        Age(path, now, age);
        return path;
    }

    private static async Task CheckCleanupRetention(string root, Func<string, Settings> fixture)
    {
        var data = Path.Combine(root, "retention");
        var settings = fixture(data);
        var git = new GitRepository(settings);
        var now = DateTimeOffset.UtcNow;
        var hour = TimeSpan.FromHours(1);
        TimeSpan Days(double days) => TimeSpan.FromDays(days);
        var kept = new List<string>
        {
            PlantFolder(data, @"Runs\young-run", now, Days(7) - hour), Plant(data, @"Logs\bridge-young.log", now, Days(14) - hour),
            Plant(data, @"Workers\young.json", now, Days(1) - hour), Plant(data, @"Requests\young.json", now, Days(1) - hour),
            PlantFolder(data, @"Tests\young", now, Days(1) - hour), PlantFolder(data, @"Updates\1.1.30", now, Days(1) - hour),
            Plant(data, @"Staging\young.txt", now, Days(1) - hour), Plant(data, "settings.json.young.tmp", now, Days(1) - hour),
            Plant(data, @"Logs\watch.log", now, Days(300)), Plant(data, @"Logs\update.log", now, Days(300)), Plant(data, @"Logs\cleanup.log", now, Days(300)),
            Plant(data, @"Updates\update.lock", now, Days(300)), Plant(data, "settings.json", now, Days(300)), Plant(data, "worktree-scans.json", now, Days(300)),
            Plant(data, "snake.json", now, Days(300)), Plant(data, @"Migrated\Current-Meyer.WebfarmWorkspace.targets", now, Days(300)),
            Plant(data, @"State\0123\workspace.json", now, Days(300)), PlantFolder(data, @"Worktrees\0123456789abcdef", now, Days(300))
        };
        var reported = PlantFolder(data, @"Runs\reported-run", now, Days(10));
        var named = Plant(data, @"Logs\bridge-named.log", now, Days(20));
        Plant(data, @"Logs\last-error.log", now, Days(30), $"BUILD FAILED\nLogs:\n  {reported}\\build.log");
        var recentReport = Plant(data, @"Logs\failure-recent.log", now, Days(2), $"DEPLOY FAILED\nLogs:\n  {named}");
        kept.AddRange([reported, named, recentReport]);
        var removed = new List<string>
        {
            PlantFolder(data, @"Runs\old-run", now, Days(7) + hour), Plant(data, @"Logs\bridge-old.log", now, Days(14) + hour),
            Plant(data, @"Logs\failure-old.log", now, Days(14) + hour, $"Logs:\n  {Path.Combine(data, @"Runs\forgotten-run")}"),
            PlantFolder(data, @"Runs\forgotten-run", now, Days(7) + hour), Plant(data, @"Logs\bridge.log", now, Days(14) + hour),
            Plant(data, @"Workers\old.json", now, Days(1) + hour), Plant(data, @"Requests\old.json", now, Days(1) + hour),
            Plant(data, @"Requests\old.json.result", now, Days(1) + hour), PlantFolder(data, @"Tests\old", now, Days(1) + hour),
            PlantFolder(data, @"Updates\1.1.20", now, Days(1) + hour), Plant(data, @"Staging\old.txt", now, Days(1) + hour),
            Plant(data, "worktree-sizes.json", now, Days(1) + hour), Plant(data, "settings.json.old.tmp", now, Days(1) + hour),
            PlantFolder(data, @"Runs\.deleting-interrupted-1a2b3c4d", now, TimeSpan.Zero)
        };
        var emptyOld = Path.Combine(data, @"Worktrees\empty-old");
        var emptyYoung = Path.Combine(data, @"Worktrees\empty-young");
        Directory.CreateDirectory(emptyOld);
        Directory.CreateDirectory(emptyYoung);
        Age(emptyOld, now, Days(1) + hour);
        Age(emptyYoung, now, Days(1) - hour);
        removed.Add(emptyOld);
        kept.Add(emptyYoung);
        var crashed = Path.Combine(data, @"Runs\crashed-run\source");
        var running = Path.Combine(data, @"Runs\running-run\source");
        await git.Git(settings.RepositoryRoot, "worktree", "add", "--detach", crashed);
        await git.Git(settings.RepositoryRoot, "worktree", "add", "--detach", running);
        Age(Path.GetDirectoryName(crashed)!, now, Days(8));
        removed.Add(Path.GetDirectoryName(crashed)!);
        kept.Add(running);

        var report = await CultivateFiles.Clean(data, settings, git, new(null, true), now, dryRun: false, TimeSpan.Zero);
        var listing = await git.Git(settings.RepositoryRoot, "worktree", "list", "--porcelain");
        bool Listed(string path) => listing.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Check(removed.All(path => !Path.Exists(path)) && report.Failed.Count == 0,
            "every category's entries just past its retention period are removed: runs after 7 days, logs after 14, leftovers after 1" +
            (removed.FirstOrDefault(Path.Exists) is { } left ? ": " + left : ""));
        Check(kept.All(Path.Exists), "entries just inside their retention period, the kept logs, settings, caches, the journal, " +
            "a checkout under Worktrees, and runs and logs a recent error report names all survive" + (kept.FirstOrDefault(path => !Path.Exists(path)) is { } gone ? ": " + gone : ""));
        Check(report.Kept.Select(note => note.Path).ToHashSet(Files.Comparer).SetEquals([reported, named]) &&
            report.Kept.All(note => note.Reason.Contains("error report")), "runs and logs old enough to go but named by the latest error are kept with the reason");
        Check(!Listed(crashed) && Listed(running) && report.SnapshotWorktrees == 1,
            "a crashed run's snapshot worktree goes with its run and Git forgets it, while a young run's snapshot stays registered");
        Check(report.Categories.Single(category => category.Name == "Runs").Count == 4 && report.Categories.Single(category => category.Name == "Logs").Count == 3 &&
            report.Categories.Single(category => category.Name == "Requests").Count == 2 && report.Count == removed.Count &&
            report.Categories.Single(category => category.Name == "Runs").Bytes > 0,
            "the report counts what each category removed and its size");
        await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", running);
    }

    private static async Task CheckCleanupDeployment(string root, Func<string, Settings> fixture, string farm)
    {
        var data = Path.Combine(root, "deployment");
        var settings = fixture(data);
        // The journal may live anywhere, even inside Runs; it is never removed.
        settings.StateRoot = Path.Combine(data, "Runs", "journal");
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var now = DateTimeOffset.UtcNow;
        Plant(farm, @"bin\Live.dll", now, TimeSpan.Zero, "original");
        var run = Path.Combine(data, "Runs", "20261001-090000-1a2b3c4d");
        var built = Plant(run, @"source\bin\Live.dll", now, TimeSpan.Zero, "built");
        var verified = Plant(run, @"verified\bin\Live.dll", now, TimeSpan.Zero, "built");
        var state = (await bridge.Call("Deploy", new
        {
            Snapshot = Path.Combine(run, "source"), SourceRoot = "", SourceRef = "refs/heads/live", Branch = "live", Fingerprint = "f", ExpectedToken = "",
            WatchEnabled = true, BuildFingerprint = "b", ExcludedPaths = Array.Empty<string>(), WorkerUpdate = false, Libraries = Array.Empty<object>(),
            RuntimeFiles = new[] { new RuntimeFile(verified, @"bin\Live.dll", Files.Hash(verified)) },
            Files = new[] { new { Source = built, RelativePath = @"bin\Live.dll", Delete = false, Hash = Files.Hash(built) } }
        })).Deserialize<DeploymentState>(Files.Json)!;
        Directory.Delete(Path.Combine(run, "source"), true);
        var worker = Plant(data, $@"Workers\{state.Token}.json", now, TimeSpan.FromDays(30));
        var started = Plant(data, @"Workers\started-by-activation.json", now, TimeSpan.FromDays(3));
        var stale = Plant(data, @"Workers\stale.json", now, TimeSpan.FromDays(10));
        var other = PlantFolder(data, @"Runs\20260901-090000-5e6f7a8b", now, TimeSpan.FromDays(30));
        var journal = Directory.GetFiles(settings.StateRoot, "*", SearchOption.AllDirectories);
        Age(run, now, TimeSpan.FromDays(30));
        Age(settings.StateRoot, now, TimeSpan.FromDays(30));
        Check(state.Active && journal.Length >= 2 && File.ReadAllText(Path.Combine(farm, @"bin\Live.dll")) == "built",
            "the fixture deployment is active, journaled, and replaced the webfarm file");

        var report = await CultivateFiles.Clean(data, settings, git, await CleanupDeployment.Read(bridge), now, dryRun: false, TimeSpan.Zero);
        Check(File.Exists(verified) && File.Exists(worker) && File.Exists(started) && journal.All(File.Exists) && !Path.Exists(other) && !File.Exists(stale) &&
            report.Failed.Count == 0, "a cleanup keeps the active deployment's run, its worker token and settings, and the journal with its backups, " +
            "and removes an older run and a stale worker file");
        Check(report.Kept.Any(note => note.Path == run && note.Reason.Contains("active deployment")) &&
            report.Kept.Any(note => note.Path == worker) && report.Kept.Any(note => note.Path == started) &&
            report.Kept.Any(note => note.Path == settings.StateRoot && note.Reason.Contains("journal")),
            "what is kept for the active deployment and the journal is reported with the reason");
        await bridge.Call("Restore");
        Check(File.ReadAllText(Path.Combine(farm, @"bin\Live.dll")) == "original" && (await bridge.Status())?.Active == false,
            "restore after a cleanup puts the original webfarm file back from the journal's backup");

        var unknown = await CultivateFiles.Clean(data, settings, git, new(null, false), now.AddDays(60), dryRun: false, TimeSpan.Zero);
        Check(File.Exists(verified) && File.Exists(worker) && unknown.Categories.Single(category => category.Name == "Runs").Count == 0 &&
            unknown.Kept.Any(note => note.Reason.Contains("could not be read")),
            "when the deployment state cannot be read, no run or worker file is removed");
    }

    private static async Task CheckCleanupSafety(string root, Func<string, Settings> fixture)
    {
        var data = Path.Combine(root, "safety");
        var settings = fixture(data);
        var git = new GitRepository(settings);
        var now = DateTimeOffset.UtcNow;
        var old = TimeSpan.FromDays(30);

        var held = PlantFolder(data, @"Runs\held-run", now, old);
        using (new FileStream(Path.Combine(held, @"nested\build.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var report = await CultivateFiles.Clean(data, settings, git, new(null, true), now, dryRun: false, TimeSpan.Zero);
            Check(File.Exists(Path.Combine(held, @"nested\build.log")) && report.Failed.Count == 0 &&
                report.Kept.Any(note => note.Path == held && note.Reason.Contains("In use")),
                "a run with a file open in another program is left whole and reported as in use, without failing the cleanup");
        }
        Check((await CultivateFiles.Clean(data, settings, git, new(null, true), now, dryRun: false, TimeSpan.Zero)).Count == 1 && !Path.Exists(held),
            "the run is removed by the next cleanup once it is no longer in use");

        var many = Enumerable.Range(0, 12).Select(index => PlantFolder(data, $@"Runs\run-{index:00}", now, old))
            .Concat(Enumerable.Range(0, 12).Select(index => Plant(data, $@"Logs\bridge-{index:00}.log", now, old))).ToList();
        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => CultivateFiles.Clean(data, settings, git, new(null, true), now, dryRun: false, TimeSpan.FromSeconds(30)))));
        Check(both.All(report => report.Failed.Count == 0) && both.Sum(report => report.Count) == many.Count && many.All(path => !Path.Exists(path)),
            "two cleanups at once neither fail nor count anything twice, and together remove everything once");

        var dry = Enumerable.Range(0, 3).Select(index => PlantFolder(data, $@"Runs\dry-{index}", now, old)).Append(Plant(data, @"Logs\bridge-dry.log", now, old)).ToList();
        string Snapshot() => string.Join("\n", Directory.EnumerateFileSystemEntries(data, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => $"{path}|{(File.Exists(path) ? new FileInfo(path).Length : 0)}|{File.GetLastWriteTimeUtc(path).Ticks}"));
        var before = Snapshot();
        var preview = await CultivateFiles.Clean(data, settings, git, new(null, true), now, dryRun: true, TimeSpan.Zero);
        Check(preview.DryRun && preview.Count == dry.Count && Snapshot() == before, "a dry run lists what would go and changes nothing");

        var stamp = CultivateFiles.StampPath(data);
        var first = await CultivateFiles.Automatic(settings, data, () => now);
        var again = await CultivateFiles.Automatic(settings, data, () => now.AddHours(1));
        Check(first is { DryRun: false } && first.Count == dry.Count && CultivateFiles.LastRun(stamp) == now && again is null,
            "the automatic pass removes what retention lets go, then waits about a day before running again");
        var olderRun = PlantFolder(data, @"Runs\next-day", now, old);
        var next = await CultivateFiles.Automatic(settings, data, () => now.AddHours(23).AddMinutes(1));
        Check(next is { Count: 1 } && !Path.Exists(olderRun) && CultivateFiles.LastRun(stamp) == now.AddHours(23).AddMinutes(1),
            "the automatic pass runs again a day later");
        using (new FileStream(Path.Combine(data, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Check(await CultivateFiles.Automatic(settings, data, () => now.AddDays(5)) is null && CultivateFiles.LastRun(stamp) == now.AddHours(23).AddMinutes(1),
                "the automatic pass skips at once, without failing, while another process is cleaning");
        Check(await CultivateFiles.Automatic(new Settings { StateRoot = "relative" }, Path.Combine(root, "unconfigured"), () => now) is { } quiet &&
            quiet.Failed.Count == 0, "the automatic pass never fails, even before Cultivate is set up");
    }

    private static async Task CheckCleanupWorktrees(string root, Func<string, Settings> fixture)
    {
        var data = Path.Combine(root, "worktrees");
        var settings = fixture(data);
        var git = new GitRepository(settings);
        var alpha = settings.RepositoryRoot;
        // Commits made now are a month old to a clock a month ahead; recent activity is placed just inside 14 days of that clock.
        var now = DateTimeOffset.Now.AddDays(30);
        string Folder(string name) => Path.Combine(root, "trees", name);
        foreach (var name in new[] { "old-clean", "old-modified", "old-untracked", "recent-write", "recent-commit", "locked", "live", "library" })
            await git.Git(alpha, "worktree", "add", "-q", "-b", name, Folder(name));
        await git.Git(alpha, "worktree", "add", "-q", "--detach", Folder("detached"));
        await git.Git(alpha, "worktree", "lock", Folder("locked"));
        File.WriteAllText(Path.Combine(Folder("old-modified"), "readme.txt"), "changed");
        File.WriteAllText(Path.Combine(Folder("old-untracked"), "notes.txt"), "new");
        Age(Path.Combine(Folder("recent-write"), "readme.txt"), now, TimeSpan.FromDays(13));
        File.WriteAllText(Path.Combine(Folder("recent-commit"), "next.txt"), "next");
        await git.Git(Folder("recent-commit"), "add", "-A");
        var date = now.AddDays(-13).ToString("yyyy-MM-ddTHH:mm:sszzz");
        await Commands.Run("git.exe", ["-C", Folder("recent-commit"), "commit", "-qm", "Recent"], null,
            new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = date, ["GIT_AUTHOR_DATE"] = date });
        Age(Path.Combine(Folder("recent-commit"), "next.txt"), now, TimeSpan.FromDays(30));
        File.WriteAllText(Path.Combine(Folder("detached"), "loose.txt"), "loose");
        await git.Git(Folder("detached"), "add", "-A");
        await git.Git(Folder("detached"), "commit", "-qm", "Only here");
        var state = new DeploymentState { Active = true, SourceRoot = Folder("live"), Libraries = [new(Folder("library"), "refs/heads/library", "master")] };
        var guard = new TidyGuard(state, root, Path.Combine(root, "program"));

        var scans = new Dictionary<string, TidyScan>(Files.Comparer);
        var checkouts = await WorktreeCatalog.Discover(settings, git);
        var scan = await CleanupWorktrees.Find(git, checkouts, guard, 14, now, scans);
        Check(scan.Qualifying.Select(worktree => worktree.Checkout.Branch).SequenceEqual(["old-clean"]) && scan.Qualifying[0].Bytes > 0 &&
            scan.Qualifying[0].LastActivity <= now.AddDays(-14),
            "only an old worktree with nothing to lose qualifies, with its size and last activity");
        Check(scan is { Protected: 3, HoldingWork: 3, Recent: 2, Unreadable: 0 },
            "a modified or untracked file and a detached worktree's own commit hold work; a recent write or commit is recent; a locked worktree and " +
            "the active deployment's worktree and library worktree are protected");
        Check(scans.ContainsKey(Folder("old-clean")) && !scans.ContainsKey(Folder("old-modified")), "qualifying worktrees' measurements are kept for the Tidy screen");
        var later = await CleanupWorktrees.Find(git, checkouts, guard, 31, now, scans);
        Check(later.Qualifying.Count == 0 && later.Recent == 3, "the age is adjustable: at 31 days the same worktrees are all recent");
        // The command reads the fixture's real deployment state, in which nothing is active, so these would no longer be protected.
        foreach (var name in new[] { "live", "library" })
            await git.Git(alpha, "worktree", "remove", "--force", Folder(name));

        var bridge = new Bridge(settings);
        async Task<JsonDocument> Command(params string[] args)
        {
            var output = new StringWriter();
            var console = Console.Out;
            Console.SetOut(output);
            try { await CleanupCommand.Run(settings, git, bridge, args, data, now); }
            finally { Console.SetOut(console); }
            return JsonDocument.Parse(output.ToString());
        }
        Plant(data, @"Logs\bridge-old.log", now, TimeSpan.FromDays(40));
        using (var json = await Command())
        {
            var files = json.RootElement.GetProperty("Files");
            var worktrees = json.RootElement.GetProperty("Worktrees");
            var category = files.GetProperty("Categories").EnumerateArray().First();
            var entry = worktrees.GetProperty("Worktrees").EnumerateArray().FirstOrDefault(item => item.GetProperty("Branch").GetString() == "old-clean");
            Check(files.GetProperty("DryRun").GetBoolean() && files.GetProperty("Count").GetInt32() == 1 && files.GetProperty("Bytes").GetInt64() > 0 &&
                new[] { "Name", "Rule", "Count", "Bytes" }.All(name => category.TryGetProperty(name, out _)) &&
                new[] { "Kept", "Failed", "SnapshotWorktrees" }.All(name => files.TryGetProperty(name, out _)) &&
                worktrees.GetProperty("DryRun").GetBoolean() && worktrees.GetProperty("Days").GetInt32() == 14 &&
                new[] { "Count", "Bytes", "Protected", "HoldingWork", "Recent", "Unreadable", "Removed", "Freed" }.All(name => worktrees.TryGetProperty(name, out _)) &&
                entry.ValueKind == JsonValueKind.Object && entry.GetProperty("Bytes").GetInt64() > 0 && entry.GetProperty("DaysUnused").GetInt32() >= 14 &&
                new[] { "Repository", "Path", "LastActivity" }.All(name => entry.TryGetProperty(name, out _)) && !entry.TryGetProperty("Result", out _) &&
                json.RootElement.GetProperty("Note").GetString()!.Contains("Nothing was changed"),
                "cleanup prints Cultivate's files by category and the qualifying worktrees with size, age and branch as JSON, and changes nothing");
        }
        Check(File.Exists(Path.Combine(data, @"Logs\bridge-old.log")) && Directory.Exists(Folder("old-clean")), "the cleanup dry run removed nothing");
        using (var json = await Command("--yes"))
            Check(!File.Exists(Path.Combine(data, @"Logs\bridge-old.log")) && Directory.Exists(Folder("old-clean")) &&
                json.RootElement.GetProperty("Worktrees").GetProperty("Removed").GetInt32() == 0 &&
                json.RootElement.GetProperty("Note").GetString()!.Contains("--worktrees"),
                "--yes removes Cultivate's files but only lists worktrees");
        using (var json = await Command("--yes", "--worktrees", "--days", "20"))
        {
            var removed = json.RootElement.GetProperty("Worktrees");
            Check(!Directory.Exists(Folder("old-clean")) && (await git.Git(alpha, "rev-parse", "--verify", "refs/heads/old-clean")).Length != 0 &&
                removed.GetProperty("Removed").GetInt32() >= 1 && removed.GetProperty("Days").GetInt32() == 20 &&
                new[] { "old-modified", "old-untracked", "recent-write", "recent-commit", "locked", "detached" }.All(name => Directory.Exists(Folder(name))),
                "--worktrees with --yes deletes only the qualifying worktrees, through Tidy's guarded removal, and keeps their branches");
        }

        await git.Git(alpha, "worktree", "add", "-q", "-b", "ui-clean", Folder("ui-clean"));
        Plant(data, @"Runs\old-ui-run\build.log", now, TimeSpan.FromDays(40));
        Age(Path.Combine(data, @"Runs\old-ui-run"), now, TimeSpan.FromDays(40));
        var picks = new Queue<string>([CleanupScreen.RemoveFiles, CleanupScreen.ReviewWorktrees, CleanupScreen.ReviewWorktrees, CleanupScreen.Back]);
        var offered = new List<List<string>>();
        var answers = new Queue<bool>([false, true]);
        var questions = new List<string>();
        var screenOutput = new StringWriter();
        await new CleanupScreen(settings, git, bridge).Show(LiveConsole(screenOutput), (_, actions) =>
        {
            offered.Add(actions.Select(action => action.Label).ToList());
            return picks.Dequeue();
        }, question => { questions.Add(question); return answers.Dequeue(); }, () => { }, data, () => now);
        var screen = screenOutput.ToString();
        Check(picks.Count == 0 && offered[0].SequenceEqual([CleanupScreen.RemoveFiles, CleanupScreen.ReviewWorktrees, CleanupScreen.Back]) &&
            offered[1].SequenceEqual([CleanupScreen.ReviewWorktrees, CleanupScreen.Back]) && !Path.Exists(Path.Combine(data, @"Runs\old-ui-run")),
            "Clean up offers removing Cultivate's files and reviewing worktrees, and removing the files leaves only the worktree review");
        Check(questions.Count == 2 && questions[0].StartsWith("Delete 1 worktree and free", StringComparison.Ordinal) &&
            screen.Contains("Nothing was deleted") && !Directory.Exists(Folder("ui-clean")) &&
            (await git.Git(alpha, "rev-parse", "--verify", "refs/heads/ui-clean")).Length != 0,
            "the worktree review lists them and asks first; declining deletes nothing, and confirming deletes them and keeps the branch");
        Check(screen.Contains("CLEAN UP") && screen.Contains("CULTIVATE FILES") && screen.Contains("WORKTREES UNUSED FOR 14 DAYS") &&
            screen.Contains("CLEANED UP") && screen.Contains("REVIEW DELETION") && screen.Contains("TIDY COMPLETE"),
            "the Clean up screens show the files, the worktrees, the review and both results in the existing cards");
        await git.Git(alpha, "worktree", "unlock", Folder("locked"));
    }
}
