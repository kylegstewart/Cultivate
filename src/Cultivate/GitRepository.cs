using System.Security.Cryptography;
using System.Text;

namespace Cultivate;

internal sealed class GitRepository(Settings settings)
{
    public Task<string> Git(string root, params string[] arguments) =>
        Commands.Run("git.exe", new[] { "-c", "core.quotepath=false", "-C", root }.Concat(arguments));

    public async Task<List<Target>> Targets()
    {
        var result = new List<Target>();
        var listing = await Git(settings.RepositoryRoot, "worktree", "list", "--porcelain", "-z");
        foreach (var block in listing.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\0');
            var path = fields.FirstOrDefault(x => x.StartsWith("worktree "))?[9..].Replace('/', '\\');
            var reference = fields.FirstOrDefault(x => x.StartsWith("branch "))?[7..];
            var head = fields.FirstOrDefault(x => x.StartsWith("HEAD "))?[5..];
            if (path is not null && Directory.Exists(path) && !path.StartsWith(Path.Combine(Files.Home, "Runs") + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var name = reference is null ? $"(detached {head?[..7]})" : reference["refs/heads/".Length..];
                result.Add(new(name, path, reference ?? head ?? throw new InvalidDataException("A worktree has no HEAD.")));
            }
        }
        var refs = await Git(settings.RepositoryRoot, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");
        foreach (var reference in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            if (reference.EndsWith("/HEAD")) continue;
            var remote = reference.StartsWith("refs/remotes/");
            var name = reference[(remote ? "refs/remotes/".Length : "refs/heads/".Length)..];
            result.Add(new(name, null, reference, remote));
        }
        return result.OrderBy(x => x.Worktree is null).ThenBy(x => x.Branch, Files.Comparer).ToList();
    }

    public async Task<(string Commit, List<Change> Changes, string Fingerprint)> Inspect(Target target)
    {
        var root = target.Worktree ?? settings.RepositoryRoot;
        if (target.Worktree is not null)
        {
            var conflicts = await Git(root, "diff", "--name-only", "--diff-filter=U", "-z");
            if (conflicts.Length != 0)
                throw new InvalidOperationException("Resolve Git merge conflicts before deploying: " + conflicts.Replace('\0', ' '));
        }
        if (target.Worktree is not null && target.Ref.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var current = await Git(root, "symbolic-ref", "--quiet", "HEAD");
            if (current != target.Ref) throw new InvalidOperationException("This worktree changed branches. Refresh the selection.");
        }
        else if (target.Worktree is not null && await Git(root, "rev-parse", "HEAD") != target.Ref)
            throw new InvalidOperationException("This detached worktree moved to another commit. Refresh the selection.");
        var commit = await Git(root, "rev-parse", "--verify", target.Worktree is null ? target.Ref + "^{commit}" : "HEAD");
        var baseline = await Git(root, "merge-base", commit, settings.BaseBranch);
        var names = target.Worktree is null
            ? await Git(root, "diff", "--name-only", "--no-renames", "-z", baseline, commit, "--")
            : await Git(root, "diff", "--name-only", "--no-renames", "-z", baseline, "--");
        var untracked = target.Worktree is null ? "" : await Git(root, "ls-files", "--others", "--exclude-standard", "-z");
        var paths = (names + "\0" + untracked).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(Files.Comparer).Order(Files.Comparer).ToList();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(commit + "\0"));
        var changes = new List<Change>();
        foreach (var path in paths)
        {
            var deleted = false;
            string contentHash;
            if (target.Worktree is not null)
            {
                var full = Files.Child(root, path);
                deleted = !File.Exists(full);
                contentHash = deleted ? "(deleted)" : Files.Hash(full);
            }
            else
            {
                contentHash = await Git(root, "ls-tree", commit, "--", path);
                deleted = contentHash.Length == 0;
            }
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0" + contentHash + "\0"));
            changes.Add(new(path, deleted));
        }
        return (commit, changes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public async Task<string> Snapshot(Plan plan, string run)
    {
        var root = Path.Combine(run, "source");
        await Git(settings.RepositoryRoot, "worktree", "add", "--detach", root, plan.Commit);
        try
        {
            if (plan.Target.Worktree is not null)
            {
                var names = await Git(plan.Target.Worktree, "diff", "--name-only", "--no-renames", "-z", plan.Commit, "--");
                var untracked = await Git(plan.Target.Worktree, "ls-files", "--others", "--exclude-standard", "-z");
                foreach (var path in (names + "\0" + untracked).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(Files.Comparer))
                {
                    var source = Files.Child(plan.Target.Worktree, path);
                    var destination = Files.Child(root, path);
                    if (!File.Exists(source)) File.Delete(destination);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, true);
                    }
                }
            }
            if ((await Inspect(plan.Target)).Fingerprint != plan.Fingerprint)
                throw new InvalidOperationException("Files changed while taking the snapshot. Preview again; nothing has been deployed.");
            return root;
        }
        catch
        {
            await Git(settings.RepositoryRoot, "worktree", "remove", "--force", root);
            throw;
        }
    }
}
