using System.Diagnostics;
using System.Text.Json;

namespace Cultivate;

/// <summary>
/// Builds release files for a merged pull request and for the pull requests the Associated PRs section of its description links, followed
/// through their own descriptions without revisiting any. Each one gets its own <see cref="ReleaseFiles"/> release in its repository, written to its
/// own subfolder of the requested pull request's ticket folder. A link that cannot be built (not merged, not cloned, unreadable, merged elsewhere)
/// is skipped with the reason, and one repository failing to build never stops the others or undoes what they wrote.
/// </summary>
/// <remarks><paramref name="gh"/> replaces the GitHub CLI in tests.</remarks>
internal sealed class ReleaseRunner(Settings settings, GitRepository git, Func<string[], Task<string>>? gh = null)
{
    /// <summary>
    /// The repository and number <c>--pr</c> and <c>--repo</c> name: a pull request URL names both, a number is the MyMeyer checkout's unless
    /// <c>--repo</c> names another clone.
    /// </summary>
    internal static (ReleaseRepository Repository, int Number) Target(Settings settings, string value, string? repo)
    {
        var named = repo is null ? null : ReleaseRepositories.Named(settings, repo);
        if (ReleaseRepositories.Link(value) is { } link)
        {
            var clone = ReleaseRepositories.Find(settings, link.GitHub) ?? throw ReleaseRepositories.NotCloned(settings, link.GitHub);
            if (named is not null && !Files.Comparer.Equals(named.Root, clone.Root))
                throw new InvalidOperationException($"--repo {repo} names {named.Label}, but the pull request is in {link.GitHub}. Leave out --repo or the URL.");
            return (clone, link.Number);
        }
        return (named ?? ReleaseRepositories.MainCheckout(settings), PullRequests.Number(value));
    }

    /// <summary>The repository a release was made in, as its result records it.</summary>
    private ReleaseRepository Of(ReleaseResult release) => new(release.Repository, release.GitHub, release.Checkout, release.BaseBranch,
        Files.Comparer.Equals(Files.Full(release.Checkout), Files.Full(settings.RepositoryRoot)));

    private ReleaseFiles Engine(ReleaseRepository repository) => new(settings, git, gh, repository);

    /// <summary>
    /// Previews the requested pull request's release and, unless <see cref="ReleaseOptions.NoLinked"/>, every linked pull request's. Builds,
    /// writes and pulls nothing. A refusal of the requested pull request is thrown, as <see cref="ReleaseFiles.Preview"/> throws it.
    /// </summary>
    public async Task<ReleaseRun> Preview(ReleaseRepository repository, int number, ReleaseOptions? options = null)
    {
        var watch = Stopwatch.StartNew();
        options ??= new();
        repository = await ReleaseRepositories.WithBranch(git, repository);
        var taken = new Dictionary<string, ReleaseResult>(Files.Comparer);
        // The first release to want a subfolder name has it; a later one from another repository adds its repository's name, and a second pull
        // request of the same repository its number.
        Func<ReleasePullRequest, string, (string, string?)> Place(ReleaseRepository clone, ReleaseFiles files) => (request, kind) =>
        {
            var usual = files.Subfolder(kind);
            if (!taken.TryGetValue(usual, out var holder)) return (usual, null);
            var same = Files.Comparer.Equals(holder.Checkout, clone.Root);
            var chosen = new[] { same ? $"{usual} (PR {request.Number})" : $"{usual} ({clone.Name})", $"{usual} ({clone.Name} PR {request.Number})" }
                .Concat(Enumerable.Range(2, 98).Select(index => $"{usual} ({clone.Name} PR {request.Number}) {index}")).First(name => !taken.ContainsKey(name));
            return (chosen, $"{usual} already holds {holder.Repository} PR #{holder.PullRequest.Number}'s files, so {clone.Name} PR #{request.Number}'s are " +
                $"written to {chosen} instead.");
        };
        var requested = Engine(repository);
        var first = await requested.Preview(number, options, Place(repository, requested));
        taken[first.Subfolder] = first;
        var run = new ReleaseRun
        {
            Name = first.Name, Folder = Path.GetDirectoryName(first.Folder) ?? first.Folder, NoLinked = options.NoLinked, Releases = [first],
            Requested = $"{repository.GitHub}#{number}"
        };
        var visited = new HashSet<string>(StringComparer.Ordinal) { new PullRequestLink(repository.GitHub ?? repository.Root, number).Key };
        var queue = new Queue<string>(options.NoLinked ? [] : first.Linked);
        while (queue.TryDequeue(out var url))
        {
            if (ReleaseRepositories.Link(url) is not { } link || !visited.Add(link.Key)) continue;
            if (ReleaseRepositories.Find(settings, link.GitHub) is not { } clone)
            {
                run.Skipped.Add(new(link.Url, link.GitHub, link.Number, $"{link.GitHub} is not cloned in {ReleaseRepositories.Where(settings)}, so its files are not " +
                    "built. Clone it there and build again, or release its files by hand."));
                continue;
            }
            try
            {
                clone = await ReleaseRepositories.WithBranch(git, clone);
                var files = Engine(clone);
                var linked = await files.Preview(link.Number, options with { Name = run.Name }, Place(clone, files));
                taken[linked.Subfolder] = linked;
                run.Releases.Add(linked);
                foreach (var next in linked.Linked) queue.Enqueue(next);
            }
            catch (Exception exception) when (Failed(exception))
            {
                run.Skipped.Add(new(link.Url, clone.Name, link.Number, exception.Message));
            }
        }
        var buildable = run.Releases.Count(release => release.CanBuild);
        run.Note = run.Releases.Count == 1 ? first.Note : !run.CanBuild ? "These release files cannot be built: " + string.Join(" ", run.Releases.SelectMany(release => release.Errors))
            : $"Preview only: nothing was built or written. Building writes {buildable} of {run.Releases.Count} repositories' files to {run.Folder}" +
              (buildable == run.Releases.Count ? "." : $"; {string.Join(", ", run.Releases.Where(release => !release.CanBuild).Select(release => release.Subfolder))} " +
                  "cannot be built.");
        if (run.Skipped.Count != 0) run.Note += $" {run.Skipped.Count} linked pull request(s) are skipped.";
        run.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        return run;
    }

    /// <summary>
    /// Builds and writes each release <paramref name="preview"/> showed, in order, each progress message naming its subfolder. With one release a
    /// failure is thrown as <see cref="ReleaseFiles.Build"/> throws it. With several, a release that cannot be built or fails records why in
    /// <see cref="ReleaseResult.Failure"/> and the others still build. Cancelling before any files are written cancels the run; once one
    /// repository's files are written, the rest are recorded as cancelled.
    /// </summary>
    public async Task<ReleaseRun> Build(ReleaseRun preview, Action<string> progress, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var single = preview.Releases.Count == 1;
        var run = new ReleaseRun
        {
            Name = preview.Name, Folder = preview.Folder, Requested = preview.Requested, NoLinked = preview.NoLinked, Skipped = preview.Skipped
        };
        foreach (var release in preview.Releases)
        {
            if (!single && !release.CanBuild)
            {
                release.Failure = "Not built: " + string.Join(" ", release.Errors);
                release.Pull = new(release.BaseBranch, null, "", "", 0, ReleasePullOutcome.NotUpdated, $"Did not pull {release.BaseBranch}: its files were not built.");
                run.Releases.Add(release);
                continue;
            }
            try { run.Releases.Add(await Engine(Of(release)).Build(release, message => progress($"{release.Subfolder}: {message}"), cancel)); }
            catch (OperationCanceledException) when (!run.Releases.Any(built => built.Written)) { throw; }
            catch (OperationCanceledException exception)
            {
                release.Failure = "Cancelled before its files were written.";
                if (exception.Data[ReleaseFiles.PulledKey] is ReleasePull pulled) release.Pull = pulled;
                run.Releases.Add(release);
            }
            catch (Exception exception) when (!single && Failed(exception))
            {
                var report = Diagnostics.Record(exception, "Release build");
                release.Failure = $"{report.Label}: {exception.Message.Trim()}" + (report.Logs.FirstOrDefault() is { } log ? $" Full report: {log}" : "");
                release.Exception = exception;
                if (exception.Data[ReleaseFiles.PulledKey] is ReleasePull pulled) release.Pull = pulled;
                run.Releases.Add(release);
                progress($"{release.Subfolder}: failed. {exception.Message.Split('\n')[0]}");
            }
        }
        run.Written = run.Releases.Any(release => release.Written);
        var written = run.Releases.Where(release => release.Written).ToList();
        run.Note = single ? run.Releases[0].Note : $"Wrote {written.Sum(release => release.Files.Count)} file(s) to {run.Folder}: " +
            string.Join(", ", written.Select(release => $"{release.Subfolder} ({release.Files.Count})")) +
            (run.Complete ? "." : $". Not written: {string.Join(", ", run.Releases.Where(release => !release.Written).Select(release => release.Subfolder))}; see each one's failure.");
        run.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        return run;
    }

    internal static bool Failed(Exception exception) => exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
        UnauthorizedAccessException or System.Xml.XmlException or JsonException or BadImageFormatException;
}
