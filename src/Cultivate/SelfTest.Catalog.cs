namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>A committed fixture repository on <paramref name="branch"/>, for checks that need real Git worktrees.</summary>
    private static async Task<string> FixtureRepository(GitRepository git, string root, string branch = "main")
    {
        Directory.CreateDirectory(root);
        await git.Git(root, "init", "-b", branch);
        await git.Git(root, "config", "user.email", "cultivate-test@localhost");
        await git.Git(root, "config", "user.name", "Cultivate isolated tests");
        File.WriteAllText(Path.Combine(root, "readme.txt"), Path.GetFileName(root));
        await git.Git(root, "add", "-A");
        await git.Git(root, "commit", "-m", "Initial");
        return root;
    }

    private static async Task CheckWorktreeCatalog(string home)
    {
        Check(WorktreeCatalog.TicketOf("12345") == "12345" && WorktreeCatalog.TicketOf("12345-2") == "12345" &&
            WorktreeCatalog.TicketOf("7253-10") == "7253", "ticket branches and their numbered follow-ups share one ticket");
        Check(WorktreeCatalog.TicketOf("automation-manager") is null && WorktreeCatalog.TicketOf("t3/e38a55c3") is null &&
            WorktreeCatalog.TicketOf("12") is null && WorktreeCatalog.TicketOf("12345-fix") is null && WorktreeCatalog.TicketOf("") is null,
            "branches that are not a ticket number have no ticket");

        var root = Path.Combine(home, "catalog");
        var settings = new Settings { RepositoryRoot = Path.Combine(root, "clones", "Alpha"), WorktreesRoot = Path.Combine(root, "worktrees") };
        var git = new GitRepository(settings);
        var alpha = await FixtureRepository(git, settings.RepositoryRoot, "master");
        var beta = await FixtureRepository(git, Path.Combine(root, "clones", "Beta"));
        var gamma = await FixtureRepository(git, Path.Combine(root, "elsewhere", "Gamma"));
        Directory.CreateDirectory(Path.Combine(root, "clones", "not a repository"));
        var ticket = Path.Combine(settings.WorktreesRoot, "Alpha", "12345-worktree");
        var followUp = Path.Combine(settings.WorktreesRoot, "Beta", "12345-2-worktree");
        var outside = Path.Combine(root, "other folder", "feature");
        var detached = Path.Combine(settings.WorktreesRoot, "Alpha", "detached");
        var locked = Path.Combine(settings.WorktreesRoot, "Gamma", "locked");
        await git.Git(alpha, "worktree", "add", "-b", "12345", ticket);
        await git.Git(beta, "worktree", "add", "-b", "12345-2", followUp);
        await git.Git(alpha, "worktree", "add", "-b", "feature/outside", outside);
        await git.Git(alpha, "worktree", "add", "--detach", detached);
        await git.Git(gamma, "worktree", "add", "-b", "locked-work", locked);
        await git.Git(gamma, "worktree", "lock", locked);

        var checkouts = await WorktreeCatalog.Discover(settings, git);
        Checkout Find(string path) => checkouts.Single(checkout => Files.Comparer.Equals(checkout.Path, Files.Full(path)));
        Check(checkouts.Count == 8 && checkouts.Select(checkout => checkout.Repository).Distinct().SequenceEqual(["Alpha", "Beta", "Gamma"]),
            "the catalog finds the configured repository, the clones beside it, and repositories known only by a worktree");
        Check(Find(alpha).Primary && Find(alpha).Branch == "master" && Find(beta).Primary && Find(gamma).Primary &&
            checkouts.Count(checkout => checkout.Primary) == 3, "each repository's primary checkout is marked");
        Check(Find(ticket) is { Primary: false, Branch: "12345", Ticket: "12345", Repository: "Alpha" } &&
            Files.Comparer.Equals(Find(ticket).RepositoryRoot, Files.Full(alpha)) && Find(followUp) is { Ticket: "12345", Repository: "Beta" },
            "worktrees name their repository, branch, and ticket");
        Check(Find(outside) is { Branch: "feature/outside", Ticket: null, Repository: "Alpha" },
            "a worktree outside the worktrees folder is found through its repository");
        Check(Find(detached) is { Detached: true, Branch: "" } && Find(detached).ToTarget().Ref == Find(detached).Head &&
            Find(ticket).ToTarget() is { Branch: "12345", Ref: "refs/heads/12345" } && Find(ticket).ToTarget().Worktree == Find(ticket).Path,
            "checkouts convert to the targets the rest of Cultivate opens and inspects");
        Check(Find(locked).Locked && !Find(ticket).Locked, "locked worktrees are marked");

        var clone = Path.Combine(root, "status clone");
        await git.Git(root, "clone", "--quiet", alpha, clone);
        await git.Git(clone, "config", "user.email", "cultivate-test@localhost");
        await git.Git(clone, "config", "user.name", "Cultivate isolated tests");
        var cloned = new Checkout("Alpha", clone, clone, "master", "", true, false, false);
        var status = await WorktreeCatalog.Status(git, cloned);
        Check(status is { Clean: true, Uncommitted: 0, Unpushed: 0, Subject: "Initial" } && status.LastCommit is not null,
            "a checkout whose commits are all on a remote and has no saved changes is clean");
        File.WriteAllText(Path.Combine(clone, "new file.txt"), "untracked");
        File.WriteAllText(Path.Combine(clone, "readme.txt"), "edited");
        status = await WorktreeCatalog.Status(git, cloned);
        Check(status is { Clean: false, Uncommitted: 2, Unpushed: 0 }, "untracked and modified files count as uncommitted changes");
        await git.Git(clone, "add", "-A");
        await git.Git(clone, "commit", "-m", "Local only");
        status = await WorktreeCatalog.Status(git, cloned);
        Check(status is { Clean: false, Uncommitted: 0, Unpushed: 1, Subject: "Local only" }, "commits that are on no remote count as unpushed");
        Check((await WorktreeCatalog.Status(git, Find(ticket))).Unpushed == 1,
            "every commit of a repository without a remote counts as unpushed");

        Directory.Delete(outside, recursive: true);
        Check((await WorktreeCatalog.Discover(settings, git)).All(checkout => !Files.Comparer.Equals(checkout.Path, Files.Full(outside))),
            "a worktree whose folder is gone is left out");
        Check((await WorktreeCatalog.Discover(new Settings { RepositoryRoot = Path.Combine(root, "missing") }, git)).Count == 0,
            "discovery without a repository or worktrees folder finds nothing");
    }
}
