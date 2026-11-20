namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>The release folder default and the move of a saved Documents default into Documents\Releases, with fixture folders and a
    /// fixture settings file only: the real Documents folder is read, never written.</summary>
    private static async Task CheckReleaseRoot(string home)
    {
        var documents = Path.Combine(home, "documents fixture");
        var releases = Path.Combine(documents, "Releases");
        Check(Settings.ReleaseRootFor("", documents) == releases && Settings.ReleaseRootFor("  ", documents) == releases,
            "an unset release root defaults to the Releases folder in Documents");
        Check(Settings.ReleaseRootFor(documents, documents) == releases && Settings.ReleaseRootFor(documents.ToUpperInvariant() + "\\", documents) == releases,
            "a saved release root that is the Documents folder itself, the old default, moves to Documents\\Releases");
        var chosen = Path.Combine(documents, "My releases");
        Check(Settings.ReleaseRootFor(chosen, documents) == chosen && Settings.ReleaseRootFor(releases, documents) == releases &&
            Settings.ReleaseRootFor(@"D:\Releases", documents) == @"D:\Releases" && Settings.ReleaseRootFor("relative", documents) == "relative",
            "any other saved release root is kept as it is");
        Check(!Directory.Exists(documents), "working out the release root creates no folder");

        var real = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var existed = Directory.Exists(Path.Combine(real, "Releases"));
        var saved = Path.Combine(home, "settings fixture");
        Directory.CreateDirectory(saved);
        File.WriteAllText(Path.Combine(saved, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            RepositoryRoot = Path.Combine(home, "no repository"), BaseBranch = "master", WebfarmRoot = Path.Combine(home, "no farm"),
            MSBuild = Path.Combine(home, "no msbuild.exe"), StateRoot = Path.Combine(home, "no state"), LibraryRepositories = Array.Empty<string>(),
            ReleaseRoot = real
        }));
        var previous = Environment.GetEnvironmentVariable("CULTIVATE_HOME");
        Settings loaded;
        try
        {
            Environment.SetEnvironmentVariable("CULTIVATE_HOME", saved);
            loaded = await Settings.Read();
        }
        finally { Environment.SetEnvironmentVariable("CULTIVATE_HOME", previous); }
        Check(loaded.ReleaseRoot == Path.Combine(real, "Releases") && Directory.Exists(Path.Combine(real, "Releases")) == existed &&
            File.ReadAllText(Path.Combine(saved, "settings.json")).Contains(System.Text.Json.JsonSerializer.Serialize(real)),
            "settings saved with the Documents folder as the release root read as Documents\\Releases, without creating it or rewriting the file");
    }

    /// <summary>
    /// Open main worktree solution's pull, against a throwaway origin and clone: a clean fast-forward, an overlapping local change, a detached
    /// HEAD, a branch origin lacks, a failed fetch, and a checkout on another branch. Visual Studio is never started.
    /// </summary>
    private static async Task CheckMainWorktreePull(string home)
    {
        var root = Path.Combine(home, "main-pull");
        var (origin, seed, checkout) = (Path.Combine(root, "origin.git"), Path.Combine(root, "seed"), Path.Combine(root, "checkout"));
        Directory.CreateDirectory(seed);
        var git = new GitRepository(new Settings { RepositoryRoot = checkout });
        Task<string> Seed(params string[] args) => git.Git(seed, args);
        Task<string> Mine(params string[] args) => git.Git(checkout, args);
        await git.Git(root, "init", "-q", "--bare", "-b", "master", origin);
        await Seed("init", "-q", "-b", "master");
        await Seed("config", "user.email", "cultivate-test@localhost");
        await Seed("config", "user.name", "Cultivate isolated tests");
        await Seed("remote", "add", "origin", origin);
        async Task<string> Publish(string branch, string path, string content)
        {
            File.WriteAllText(Path.Combine(seed, path), content);
            await Seed("add", path);
            await Seed("commit", "-qm", $"{path} on {branch}");
            await Seed("push", "-q", "origin", "HEAD:refs/heads/" + branch);
            return await Seed("rev-parse", "HEAD");
        }
        await Publish("master", "App.sln", "");
        await Publish("master", "View.ascx", "original");
        await git.Git(root, "clone", "-q", origin, checkout);
        await Mine("config", "user.email", "cultivate-test@localhost");
        await Mine("config", "user.name", "Cultivate isolated tests");
        var pull = new BasePull(checkout, git, "cultivate open");
        var progress = new List<string>();
        Task<CheckoutPull> Run(string? baseBranch = null) => pull.CheckedOut(baseBranch, progress.Add);

        var start = await Mine("rev-parse", "HEAD");
        await Publish("master", "One.js", "one");
        var tip = await Publish("master", "Two.js", "two");
        var pulled = await Run();
        Check(pulled is { Warning: false, Pull: { Outcome: ReleasePullOutcome.Pulled, Commits: 2, Worktree: var at } } &&
            pulled.Detail == $"Pulled master: 2 new commit(s), {start[..7]} → {tip[..7]} ({at})" &&
            await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == tip + "\n" + tip && File.ReadAllText(Path.Combine(checkout, "Two.js")) == "two" &&
            await Mine("status", "--porcelain") == "" && progress.Contains("Fetching master from origin") && progress.Contains($"Pulling master in {at}"),
            "opening a main worktree's solution fetches its branch and fast-forwards a clean checkout, saying how many commits it pulled");
        var again = await Run();
        Check(again is { Warning: false, Pull.Outcome: ReleasePullOutcome.UpToDate } && again.Detail == $"master in {again.Pull.Worktree} is already up to date with origin ({tip[..7]}).",
            "a main worktree already at origin's tip says it is up to date");

        var view = Path.Combine(checkout, "View.ascx");
        File.WriteAllText(view, "my local edit");
        var theirs = await Publish("master", "View.ascx", "theirs");
        var refused = await Run();
        Check(refused is { Warning: true, Pull.Outcome: ReleasePullOutcome.NotUpdated } && refused.Detail.Contains("pulling would overwrite your local changes to View.ascx") &&
            refused.Detail.Contains("The solution opens on checkout as it is; commit, stash or undo those changes, then pull master by hand.") &&
            await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == tip + "\n" + theirs && File.ReadAllText(view) == "my local edit",
            "a local change the pull would overwrite is kept and the branch stays, with a warning that the solution still opens as it is");
        await Mine("checkout", "-q", "--", "View.ascx");
        await Mine("merge", "-q", "--ff-only", theirs);

        await Publish("feature", "Feature.js", "feature");
        await Mine("fetch", "-q", "origin");
        await Mine("switch", "-q", "feature");
        var featureTip = await Publish("feature", "Feature2.js", "more");
        var other = await Run();
        Check(other is { Warning: false, Pull.Outcome: ReleasePullOutcome.Pulled } && await Mine("rev-parse", "HEAD") == featureTip &&
            other.Detail.StartsWith("Pulled feature: 1 new commit(s)", StringComparison.Ordinal) && other.Detail.EndsWith(" checkout is on feature, not master.", StringComparison.Ordinal),
            "a main worktree on another branch pulls that branch and says it is not on the base branch");
        Check((await Run("feature")).Detail.EndsWith("up to date with origin (" + featureTip[..7] + ").", StringComparison.Ordinal) &&
            (await Run("develop")).Detail.EndsWith(" checkout is on feature, not develop.", StringComparison.Ordinal),
            "the base branch the caller names decides the note");

        await Mine("switch", "-q", "-c", "local-only");
        var missing = await Run();
        Check(missing is { Warning: false, Pull: null } && missing.Detail == "Origin has no local-only branch, so checkout was not pulled. checkout is on local-only, not master." &&
            await Mine("rev-parse", "HEAD") == featureTip, "a branch origin does not have is noted and left as it is");

        await Mine("switch", "-q", "--detach", "master");
        var detached = await Run();
        Check(detached is { Warning: false, Pull: null, Detail: "checkout is on a detached HEAD, so it was not pulled." } &&
            await Mine("rev-parse", "HEAD") == theirs, "a detached HEAD is noted and not pulled");

        await Mine("switch", "-q", "master");
        await Publish("master", "Three.js", "three");
        await Mine("remote", "set-url", "origin", Path.Combine(root, "gone.git"));
        var offline = await Run();
        Check(offline is { Warning: true, Pull: null } && offline.Detail.StartsWith("Could not fetch master from origin: ", StringComparison.Ordinal) &&
            offline.Detail.EndsWith(". checkout opens as it is.", StringComparison.Ordinal) && await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == theirs + "\n" + theirs,
            "a failed fetch is a warning, not a failure, and changes nothing");
    }
}
