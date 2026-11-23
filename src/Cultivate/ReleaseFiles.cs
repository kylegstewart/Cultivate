using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cultivate;

/// <summary>
/// Builds a merged pull request's release files: every file to upload to the Code Release Manager, built from the base branch as origin has it
/// now, in one flat folder named after the ticket. The pull request and the base branch are fetched into private refs that are deleted when the
/// run ends, and the build happens in a throwaway snapshot of the fetched base commit, so nothing in the developer's checkout changes what is
/// built. The unchanged projects a released DLL compiles against are built from that snapshot too, never taken from the checkout.
/// Building, never previewing, also pulls the base branch: origin's remote-tracking branch moves to the fetched tip, and the local base branch
/// follows when Git can fast-forward it without touching local changes; otherwise it stays where it is, with a warning. Nothing else in the
/// checkout changes. No Code Release Manager mapping is looked up: it matches each uploaded file by name itself.
/// One instance builds one repository's release: the MyMeyer checkout unless <paramref name="repository"/> names another clone, whose checkout
/// and release branch then stand in for MyMeyer's everywhere. A repository whose base commit tracks no project file is a SQL repository: its
/// changed SQL scripts are copied as the base branch has them, read straight from the commit, and nothing is built.
/// </summary>
/// <remarks>Call <see cref="Preview"/>, show it, then pass it to <see cref="Build"/>. <paramref name="gh"/> replaces the GitHub CLI in tests.
/// <see cref="ReleaseRunner"/> runs it for a pull request and the pull requests it links.</remarks>
internal sealed partial class ReleaseFiles(Settings settings, GitRepository git, Func<string[], Task<string>>? gh = null, ReleaseRepository? repository = null)
{
    internal const string RefPrefix = "refs/cultivate/release/";
    /// <summary>The key of a failed build's exception data that holds the <see cref="ReleasePull"/> its pull step made before the failure.</summary>
    internal const string PulledKey = "Cultivate.ReleasePull";
    private const string Fields = "number,title,author,headRefName,baseRefName,state,mergedAt,mergeCommit,headRefOid,url,body";
    /// <summary>Files uploaded as they are. The dev farm planner leaves handlers and the like out; a release must not.</summary>
    internal static readonly HashSet<string> ContentExtensions = new(Planner.FrontendExtensions.Concat([".ashx", ".asmx", ".master", ".asax"]), Files.Comparer);

    private readonly ReleaseRepository repository = repository is null ? ReleaseRepositories.MainCheckout(settings) : repository;
    /// <summary>For another repository: its checkout and release branch, so Git, the planner and the build all run there.</summary>
    private readonly Settings settings = repository is null || repository.Main ? settings : ReleaseRepositories.For(settings, repository);
    private readonly string mainCheckout = settings.RepositoryRoot;

    /// <summary>Every project file in the base commit by path, with its text, and the projects a changed compiled file can belong to.</summary>
    private sealed record Catalog(HashSet<string> Tracked, Dictionary<string, string> Projects, List<ProjectInfo> Candidates);
    /// <summary>What the build needs beyond the result: the plan of release projects and every project to build, dependencies first.</summary>
    private sealed record Work(Plan Plan, List<ProjectInfo> Order);
    /// <summary>Where a release is written and what kind it is, decided before it is analysed.</summary>
    private sealed record Placement(string Name, string Folder, string Subfolder, string? Reason, string Kind, List<string> Linked);

    private static string Ref(int number, string name) => $"{RefPrefix}{number}/{name}";
    private Task<string> G(params string[] arguments) => git.Git(settings.RepositoryRoot, arguments);
    private BasePull Base => new(settings.RepositoryRoot, git, "cultivate release");
    /// <summary>"Pull request #N", naming the repository when it is not MyMeyer.</summary>
    private string Named(int number) => repository.Main ? $"Pull request #{number}" : $"{repository.Name} pull request #{number}";

    /// <summary>
    /// <c>cultivate release --pr N|URL [--repo NAME|OWNER/NAME|PATH] [--yes] [--name NAME] [--out FOLDER] [--clean] [--reuse-dependencies]
    /// [--no-pull] [--no-linked]</c>. Exits 2 when a preview has a repository that cannot be built, and 1 when building one failed.
    /// </summary>
    internal static async Task<int> Command(Settings settings, GitRepository git, string[] args, Func<string[], Task<string>>? gh = null)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1]
                : throw new InvalidOperationException($"{name} needs a value.");
        }
        var value = Option("--pr") ?? throw new InvalidOperationException("Name the merged pull request: cultivate release --pr 1234.");
        var (target, number) = ReleaseRunner.Target(settings, value, Option("--repo"));
        var runner = new ReleaseRunner(settings, git, gh);
        var preview = await runner.Preview(target, number, new(Option("--name"), Option("--out"), args.Contains("--clean"), args.Contains("--reuse-dependencies"),
            args.Contains("--no-pull"), args.Contains("--no-linked")));
        if (!args.Contains("--yes"))
        {
            Console.WriteLine(JsonSerializer.Serialize(preview, Files.Json));
            return preview.Complete ? 0 : 2;
        }
        var result = await runner.Build(preview, message => Console.Error.WriteLine(message));
        Console.WriteLine(JsonSerializer.Serialize(result, Files.Json));
        return result.Complete ? 0 : 1;
    }

    /// <summary>
    /// Asks the GitHub CLI about the merged pull request, fetches it and the base branch, and works out what would be built and written, and
    /// what building would do to the local base branch. Builds, writes and pulls nothing; the private refs are deleted before it returns, whatever
    /// happens. The files go in this repository's subfolder of the ticket folder: <paramref name="place"/>, given the pull request and the
    /// repository's kind, can choose another subfolder name and say why; otherwise it is the repository's name, or SQL.
    /// </summary>
    public async Task<ReleaseResult> Preview(int number, ReleaseOptions? options = null,
        Func<ReleasePullRequest, string, (string Subfolder, string? Reason)>? place = null)
    {
        var watch = Stopwatch.StartNew();
        options ??= new();
        if (options.Name?.Trim() is { Length: > 0 } given) SafeName(given);
        var (request, linked) = await Describe(number);
        var name = FolderName(request, options.Name);
        var ticket = Folder(name, options.Folder);
        try
        {
            var (head, tip) = await Fetch(request);
            var kind = await KindOf(tip);
            var (subfolder, reason) = place?.Invoke(request, kind) ?? (Subfolder(kind), null);
            var folder = Path.Combine(ticket, subfolder);
            var (result, _) = await Analyse(request, head, tip, options, new(name, folder, subfolder, reason, kind, linked));
            var record = ReleaseFolder.Load(folder);
            result.Existing.AddRange(ReleaseFolder.Existing(folder, record, []));
            if (options.Clean) result.Removed.AddRange(ReleaseFolder.Removable(folder, record).Select(file => file.Name));
            result.Pull = options.NoPull ? BasePull.Off(request.BaseBranch, false) : await Base.Pending(request.BaseBranch, tip);
            result.Note = result.CanBuild ? $"Preview only: nothing was built or written. Building writes {Planned(result)} file(s) to {folder}. {result.Pull.Detail}"
                : "These release files cannot be built: " + string.Join(" ", result.Errors);
            result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            return result;
        }
        finally { await DeleteRefs(number); }
    }

    private static int Planned(ReleaseResult result) => result.Projects.Count + result.Packages.Select(package => package.Name).Distinct(Files.Comparer).Count() +
        result.Content.Count;

    /// <summary>The usual subfolder: SQL for a SQL repository, otherwise the repository's name.</summary>
    internal string Subfolder(string kind) => kind == ReleaseRepositoryKind.Sql ? "SQL" : repository.Name;

    /// <summary>Whether the base commit tracks any project file; the MyMeyer checkout is always a code repository.</summary>
    private async Task<string> KindOf(string tip) => repository.Main || (await Tracked(tip)).Any(path =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
        ? ReleaseRepositoryKind.Code : ReleaseRepositoryKind.Sql;

    private readonly Dictionary<string, Task<HashSet<string>>> trees = [];

    /// <summary>Every file the commit tracks, with backslashes; read once per commit.</summary>
    private Task<HashSet<string>> Tracked(string tip)
    {
        lock (trees)
            return trees.TryGetValue(tip, out var known) ? known : trees[tip] = Read();
        async Task<HashSet<string>> Read() => (await G("ls-tree", "-r", "--name-only", "-z", tip)).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('/', '\\')).ToHashSet(Files.Comparer);
    }

    /// <summary>
    /// Writes <paramref name="path"/> as <paramref name="commit"/> holds it to <paramref name="destination"/>, with the bytes a checkout of that commit
    /// would write: its .gitattributes and the line-ending settings apply. Needs no worktree, so paths that differ only by case each keep their own content.
    /// </summary>
    private async Task Extract(string commit, string path, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var start = new ProcessStartInfo("git.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-c", "core.quotepath=false", "-C", settings.RepositoryRoot, "--attr-source=" + commit, "cat-file", "--filters", $"{commit}:{path}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start git.exe.");
        var error = process.StandardError.ReadToEndAsync();
        await using (var file = File.Create(destination)) await process.StandardOutput.BaseStream.CopyToAsync(file);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git.exe failed ({process.ExitCode}).\nCould not read {path} from {commit[..7]}: {(await error).Trim()}");
    }

    /// <summary>
    /// Builds and writes what <paramref name="preview"/> showed: the same pull request head and base commit, fetched again so nothing depends on
    /// the developer's checkout. Before the snapshot it pulls the base branch to the newly fetched tip, unless the preview was made with
    /// <see cref="ReleaseOptions.NoPull"/>; that step only ever adds warnings, never stops the build. Cancelling stops before anything is written
    /// to the release folder. The snapshot and the private refs are removed when it ends, whatever happens.
    /// </summary>
    public async Task<ReleaseResult> Build(ReleaseResult preview, Action<string> progress, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var request = preview.PullRequest;
        var run = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-release-" + Guid.NewGuid().ToString("N")[..8]);
        string? snapshot = null;
        ReleasePull? pulled = null;
        var stage = "Release fetch";
        try
        {
            if (!preview.CanBuild) throw new InvalidOperationException(string.Join("\n", preview.Errors));
            progress($"Fetching PR #{request.Number} and {request.BaseBranch} from origin");
            var (head, tip) = await Fetch(request);
            if (head != request.HeadCommit)
                throw new InvalidOperationException($"Pull request #{request.Number}'s head moved since the preview. Preview again; nothing was built.");
            try { await G("cat-file", "-e", preview.BaseCommit + "^{commit}"); }
            catch (InvalidOperationException) { throw new InvalidOperationException($"{preview.BaseCommit} is no longer available. Preview again; nothing was built."); }
            var options = new ReleaseOptions(preview.Name, preview.Folder, preview.Clean, preview.ReuseDependencies, preview.NoPull);
            var (result, work) = await Analyse(request, head, preview.BaseCommit, options,
                new(preview.Name, preview.Folder, preview.Subfolder, preview.Placement, preview.Kind, preview.Linked));
            if (tip != preview.BaseCommit)
                result.Warnings.Add($"{request.BaseBranch} moved on origin since the preview (now {tip[..7]}). These files were built from " +
                    $"{preview.BaseCommit[..7]}, the commit the preview showed; preview again to include the newer changes.");
            if (!result.CanBuild) throw new InvalidOperationException(string.Join("\n", result.Errors));
            cancel.ThrowIfCancellationRequested();
            result.Pull = pulled = result.NoPull ? BasePull.Off(request.BaseBranch, true) : await Base.PullBase(request.BaseBranch, tip, result.Warnings, progress,
                $"The release files were still built from origin's {tip[..7]}");
            progress(result.Pull.Detail);
            Directory.CreateDirectory(run);
            result.RunDirectory = run;
            var files = new List<ReleaseFolder.Source>();
            if (result.Kind == ReleaseRepositoryKind.Sql)
            {
                // Only the changed scripts are needed; a checkout of a large SQL repository is slow, and on Windows it cannot hold two paths
                // that differ only by case.
                stage = "SQL scripts";
                progress($"Reading {TicketWorkspaces.Count(result.Content.Count, "SQL script")} from {request.BaseBranch} at {preview.BaseCommit[..7]}");
                foreach (var (path, index) in result.Content.Select((path, index) => (path, index)))
                {
                    var extracted = Path.Combine(run, "sql", index.ToString(), Path.GetFileName(path));
                    await Extract(preview.BaseCommit, path, extracted);
                    files.Add(new(Path.GetFileName(path), ReleaseKind.Sql, path.Replace('/', '\\'), extracted, null, null));
                }
            }
            else
            {
                stage = "Snapshot";
                progress($"Snapshotting {request.BaseBranch} at {preview.BaseCommit[..7]}");
                snapshot = Path.Combine(run, "source");
                await G("worktree", "add", "--detach", snapshot, preview.BaseCommit);
            }
            if (work.Plan.Projects.Count != 0 && snapshot is not null)
            {
                cancel.ThrowIfCancellationRequested();
                stage = "Build";
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Capture.targets")))
                    throw new FileNotFoundException("Cultivate's build capture target is missing. Reinstall Cultivate.");
                var support = new SupportBuild(result.ReuseDependencies ? work.Plan.Projects : work.Order) { Main = repository.Main };
                var outputs = await new Pipeline(settings, git, new Bridge(settings)).Build(work.Plan, snapshot, run, progress, support);
                result.OutsideReferences.AddRange(support.Outside.GroupBy(reference => reference.Path, Files.Comparer).Select(group =>
                    group.First() with { Project = string.Join(", ", group.Select(reference => reference.Project).Distinct(Files.Comparer)) }));
                result.Warnings.AddRange(support.Substituted.Select(reference => $"{reference.Project} did not compile against " +
                    (reference.Borrowed is null ? $@"{request.BaseBranch}'s tracked bin\{reference.Name}" : $"{reference.Borrowed}'s copy of {reference.Name}") +
                    $" ({reference.Replaced}), so it was compiled against {reference.Path} ({reference.Version}, modified {reference.LastWriteTime:yyyy-MM-dd HH:mm}), " +
                    $"the file its project names. Cultivate cannot verify that file; make sure production has a {reference.Name} at least that new."));
                if (support.Unresolved.Count != 0)
                    result.Warnings.Add($"Cultivate found {TicketWorkspaces.Count(support.Unresolved.Count, "reference")} nowhere and left " +
                        $"{(support.Unresolved.Count == 1 ? "it" : "them")} to MSBuild, as Visual Studio does: {string.Join("; ", support.Unresolved)}. The build fails " +
                        "with a compiler error if the code needs one; this one compiled, so it did not. build.log says where Cultivate looked.");
                string Output(string name) => outputs.Single(file => Files.Comparer.Equals(file.RelativePath, @"bin\" + name)).Source;
                files.AddRange(work.Plan.Projects.Select(project =>
                    new ReleaseFolder.Source(project.Assembly + ".dll", ReleaseKind.ProjectDll, project.RelativePath, Output(project.Assembly + ".dll"), project.Assembly, null)));
                files.AddRange(result.Packages.DistinctBy(package => package.Name, Files.Comparer).Select(package =>
                    new ReleaseFolder.Source(package.Name, ReleaseKind.Package, Package(package), Output(package.Name), Path.GetFileNameWithoutExtension(package.Name),
                        package.Version.Length == 0 ? null : package.Version)));
                if (result.ReuseDependencies && result.OutsideReferences.Count != 0)
                    result.Warnings.Add("Built with --reuse-dependencies: the release DLLs were compiled against unverified DLLs from outside master's snapshot: " +
                        string.Join(", ", result.OutsideReferences.Select(reference => $"{reference.Name} ({reference.Path}, {reference.LastWriteTime:yyyy-MM-dd HH:mm})")
                            .Distinct()) + ". Check they match master before releasing.");
            }
            if (snapshot is not null)
                files.AddRange(result.Content.Select(path =>
                    new ReleaseFolder.Source(Path.GetFileName(path), ReleaseKind.Content, path.Replace('/', '\\'), Files.Child(snapshot, path), null, null)));
            stage = "Release verification";
            progress("Staging and verifying the release files");
            var staged = ReleaseFolder.Stage(files, Path.Combine(run, "release"), result.Warnings);
            cancel.ThrowIfCancellationRequested();
            stage = "Release folder";
            progress($"Writing {staged.Count} file(s) to {result.Folder}");
            ReleaseFolder.Write(staged, result, request.Number);
            result.Written = true;
            result.Note = $"Wrote {result.Files.Count} file(s) to {result.Folder}. " + (result.Kind == ReleaseRepositoryKind.Sql
                ? "Release them through the database process; Cultivate never runs SQL." : "Upload them to the Code Release Manager.");
            result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            ReleaseFolder.Save(result, run);
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or System.Xml.XmlException or JsonException or BadImageFormatException)
        {
            Diagnostics.Attach(exception, new(stage)
            {
                Branch = request.Label, Snapshot = snapshot, RunDirectory = Directory.Exists(run) ? run : null, Log = Path.Combine(run, "build.log")
            });
            // The pull already happened: what it did is reported with the failure, not the preview's plan.
            if (pulled is not null) exception.Data[PulledKey] = pulled;
            throw;
        }
        catch (OperationCanceledException exception) when (pulled is not null)
        {
            exception.Data[PulledKey] = pulled;
            throw;
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot)) await G("worktree", "remove", "--force", snapshot);
            await DeleteRefs(request.Number);
        }
    }

    private string Package(ReleasePackage package) => Path.GetRelativePath(settings.RepositoryRoot, Path.GetFullPath(
        Path.Combine(settings.RepositoryRoot, Path.GetDirectoryName(package.Project)!, package.HintPath)));

    /// <summary>The pull request as the GitHub CLI describes it and the pull requests its description links, or why it cannot be released.</summary>
    private async Task<(ReleasePullRequest Request, List<string> Linked)> Describe(int number)
    {
        string json;
        // The GitHub CLI finds MyMeyer from the checkout it runs in; any other repository is named.
        string[] arguments = repository.Main || repository.GitHub is null ? ["pr", "view", number.ToString(), "--json", Fields]
            : ["pr", "view", number.ToString(), "--repo", repository.GitHub, "--json", Fields];
        try { json = await (gh ?? PullRequests.GitHub(settings))(arguments); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception.Message.Contains("Could not resolve to a PullRequest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{Named(number)} was not found on GitHub. Check the number; nothing was fetched or built.");
            throw new InvalidOperationException($"{PullRequests.Problem(exception)}. Building release files needs the GitHub CLI to find the commit " +
                "the pull request was merged as; nothing was fetched or built.");
        }
        var request = Parse(json);
        if (request is null || request.Number != number)
            throw new InvalidOperationException($"The GitHub CLI returned no details for {(repository.Main ? "" : repository.Name + " ")}pull request #{number}; " +
                "nothing was fetched or built.");
        if (request.State != "MERGED" || request.MergeCommit.Length == 0)
            throw new InvalidOperationException($"{Named(number)} is {(request.State == "CLOSED" ? "closed without being merged" : "not merged yet")}. " +
                $"Release files are built from {settings.BaseBranch} after the merge." + (repository.Main
                    ? $" To try it on your dev webfarm instead, use Review a pull request or `cultivate activate --pr {number} --yes`." : ""));
        if (request.BaseBranch != settings.BaseBranch)
            throw new InvalidOperationException($"{Named(number)} was merged into {request.BaseBranch}, not {settings.BaseBranch}. Release files are " +
                $"built only from {settings.BaseBranch}; release it once its changes reach {settings.BaseBranch}.");
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement.TryGetProperty("body", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return (request, ReleaseRepositories.Associated(body).Select(link => link.Url).ToList());
    }

    internal static ReleasePullRequest? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("number", out var value) || !value.TryGetInt32(out var number)) return null;
        static string Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var field) &&
            field.ValueKind == JsonValueKind.String ? field.GetString()?.Trim() ?? "" : "";
        JsonElement Child(string name) => item.TryGetProperty(name, out var child) ? child : default;
        return new(number, Text(item, "title"), Text(Child("author"), "login"), Text(item, "url"), Text(item, "headRefName"), Text(item, "baseRefName"),
            Text(item, "state").ToUpperInvariant(), Text(Child("mergeCommit"), "oid"), Text(item, "mergedAt"), Text(item, "headRefOid"));
    }

    /// <summary>
    /// Fetches the pull request's head and the base branch into this run's private refs. Origin's remote-tracking branches stay as they are here;
    /// only <see cref="BasePull.PullBase"/>, when building, moves the base branch's.
    /// </summary>
    private async Task<(string Head, string Tip)> Fetch(ReleasePullRequest request)
    {
        var (number, branch) = (request.Number, request.BaseBranch);
        await DeleteRefs(number);
        try
        {
            // An empty --refmap stops Git from also moving origin's remote-tracking branches.
            await G("fetch", "--quiet", "--no-tags", "--refmap=", "origin", $"+refs/pull/{number}/head:{Ref(number, "head")}", $"+refs/heads/{branch}:{Ref(number, "base")}");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains($"refs/pull/{number}/head", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{Named(number)} was not found on origin. Check the number; nothing was built.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains($"refs/heads/{branch}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Origin{(repository.Main ? "" : " of " + repository.Name)} has no {branch} branch to build release files from. Nothing was built.");
        }
        var head = await G("rev-parse", "--verify", Ref(number, "head") + "^{commit}");
        var tip = await G("rev-parse", "--verify", Ref(number, "base") + "^{commit}");
        try { await G("merge-base", "--is-ancestor", request.MergeCommit, tip); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException($"{Named(number)} was merged as {request.MergeCommit[..Math.Min(7, request.MergeCommit.Length)]}, which is not " +
                $"part of origin's {branch} ({tip[..7]}); it may have been removed by a force push. Nothing was built.");
        }
        return (head, tip);
    }

    private async Task DeleteRefs(int number)
    {
        try
        {
            foreach (var reference in (await G("for-each-ref", "--format=%(refname)", $"{RefPrefix}{number}/"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await G("update-ref", "-d", reference);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Works out the release from commits alone: the pull request's files from its fork point to its head, each compared with the base commit
    /// <paramref name="tip"/>, the projects that own its compiled files, their changed packages, and the unchanged projects they compile against.
    /// </summary>
    private async Task<(ReleaseResult Result, Work Work)> Analyse(ReleasePullRequest request, string head, string tip, ReleaseOptions options, Placement place)
    {
        var (number, branch, merge) = (request.Number, request.BaseBranch, request.MergeCommit);
        var sql = place.Kind == ReleaseRepositoryKind.Sql;
        // M^1 is the base before the merge for squash, merge-commit and rebase merges alike. merge-base(H, B) would be H itself after a
        // merge-commit merge, and the pull request would seem to change nothing.
        var fork = await G("merge-base", head, merge + "^1");
        var result = new ReleaseResult
        {
            PullRequest = request with { HeadCommit = head }, Repository = repository.Name, GitHub = repository.GitHub, Checkout = settings.RepositoryRoot,
            Kind = place.Kind, BaseBranch = branch, BaseCommit = tip, ForkPoint = fork, Name = place.Name, Folder = place.Folder, Subfolder = place.Subfolder,
            Placement = place.Reason, Linked = place.Linked, Clean = options.Clean, ReuseDependencies = options.ReuseDependencies, NoPull = options.NoPull
        };
        if (place.Reason is not null) result.Warnings.Add(place.Reason);
        if (request.HeadCommit.Length != 0 && request.HeadCommit != head)
            result.Warnings.Add($"Origin's refs/pull/{number}/head is {head[..7]}, but GitHub reports the head as {request.HeadCommit[..Math.Min(7, request.HeadCommit.Length)]}. " +
                "The fetched head was used.");
        var since = Raw(await G("diff", "--raw", "-z", "--no-renames", "--no-abbrev", fork, tip)).ToDictionary(entry => entry.Path, entry => entry.After, StringComparer.Ordinal);
        var compiled = new List<Change>();
        Task<List<MergedCommit>>? merged = null;
        Task<List<MergedCommit>> Merged() => merged ??= OtherMerged(head, fork, merge, tip);
        foreach (var (path, before, after, letter) in Raw(await G("diff", "--raw", "-z", "--no-renames", "--no-abbrev", fork, head)))
        {
            var current = since.GetValueOrDefault(path, before);
            var status = current == before ? ReleaseStatus.NotOnBase : Absent(current) ? ReleaseStatus.Deleted :
                current == after ? ReleaseStatus.Same : ReleaseStatus.ChangedAgain;
            var change = new Change(path, Absent(current));
            var kind = sql ? Path.GetExtension(path).Equals(".sql", StringComparison.OrdinalIgnoreCase) ? ReleaseKind.Sql : ReleaseKind.NotIncluded :
                Planner.IsCompiledInput(change) ? ReleaseKind.Compiled :
                ContentExtensions.Contains(Path.GetExtension(path)) && !Planner.IsExcluded(path) ? ReleaseKind.Content : ReleaseKind.NotIncluded;
            var commits = status == ReleaseStatus.ChangedAgain
                ? (await Merged()).Where(other => other.Paths.Contains(path, StringComparer.Ordinal)).Select(other => other.Commit).ToList() : [];
            result.Changes.Add(new(path, letter switch { "A" => "Added", "D" => "Deleted", "M" => "Modified", _ => "Changed" }, status, kind, commits));
            if (status == ReleaseStatus.NotOnBase)
            {
                result.Warnings.Add($"{path}: the pull request's change is no longer on {branch} (it was undone since the merge), so it is left out.");
                continue;
            }
            if (status == ReleaseStatus.ChangedAgain)
                result.Warnings.Add($"{path} is not exactly as it was on the pull request: " + (commits.Count == 0
                    ? "the merge itself changed it (for example, resolving a conflict)"
                    : $"other merged changes touched it too ({string.Join(", ", commits.Select(commit => commit.Commit[..7]))})") + $"; {branch}'s version is used.");
            if (kind == ReleaseKind.Compiled) compiled.Add(change);
            else if (change.Deleted)
                result.Deleted.Add(new(path, kind == ReleaseKind.Content ? "Not on " + branch + ": nothing to upload. Remove it from production by hand if it was released."
                    : kind == ReleaseKind.Sql ? "Not on " + branch + ": nothing to release. Drop it from the database by hand if it was released."
                    : "Not on " + branch + ": nothing to upload."));
            else if (kind is ReleaseKind.Content or ReleaseKind.Sql) result.Content.Add(path);
            else result.NotIncluded.Add(new(path, sql ? SqlReason(path) : Reason(path)));
        }
        foreach (var config in result.NotIncluded.Where(note => !sql && Path.GetFileName(note.Path).Equals("web.config", StringComparison.OrdinalIgnoreCase)))
            result.Warnings.Add($"{config.Path} changed. Cultivate never releases configuration: compare it with production and release it by hand if needed.");
        var plan = new Plan
        {
            Target = new(request.Label, null, Ref(number, "base"), PullRequest: new(number, request.Title, request.Author, request.HeadBranch, branch,
                State: request.State, Commit: head)),
            Commit = tip, Baseline = fork, Fingerprint = "", Changes = compiled.Concat(result.Content.Select(path => new Change(path, false))).ToList()
        };
        var order = new List<ProjectInfo>();
        if (compiled.Count != 0)
        {
            var catalog = await ReadCatalog(tip);
            // An SDK-style project compiles every file in its folder without listing it, so its changed files are matched by folder.
            var sdk = repository.Main ? [] : catalog.Candidates.Where(project => SdkStyle().IsMatch(catalog.Projects[project.RelativePath])).ToList();
            var unsupported = new HashSet<string>(Files.Comparer);
            void Unsupported(ProjectInfo project)
            {
                if (unsupported.Add(project.RelativePath))
                    plan.Errors.Add($"{project.RelativePath} is an SDK-style project, which Build release files does not support. Build {project.Assembly} in " +
                        "Visual Studio and release its files by hand; if it is deployed by publishing, publish it instead.");
            }
            var supported = compiled.Where(change =>
            {
                var path = change.Path.Replace('/', '\\');
                var owner = sdk.FirstOrDefault(project => project.Owns(path) || (Path.GetDirectoryName(project.RelativePath) is { Length: > 0 } directory
                    ? path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase) : !path.Contains('\\')));
                if (owner is not null) Unsupported(owner);
                return owner is null;
            }).ToList();
            try { plan.Projects.AddRange(Planner.BuildOrder(catalog.Candidates.Except(sdk).ToList(), supported, plan.Errors)); }
            catch (InvalidOperationException exception) when (!repository.Main) { plan.Errors.Add(exception.Message); }
            await new Planner(settings, git, new Bridge(settings)).AddPackageDependencies(plan, supported, anyPackagesFolder: !repository.Main);
            result.Packages.AddRange(plan.Projects.SelectMany(project => project.PackageDependencies.Select(hint =>
                new ReleasePackage(Path.GetFileName(hint), project.HintVersions.GetValueOrDefault(hint) ?? "", hint, project.RelativePath))));
            order = Support(catalog, plan.Projects, result);
            foreach (var project in order.Where(project => !repository.Main && SdkStyle().IsMatch(catalog.Projects[project.RelativePath]))) Unsupported(project);
            var others = await Merged();
            result.Projects.AddRange(plan.Projects.Select(project => new ReleaseProject(project.RelativePath, project.Assembly,
                others.Where(other => other.Paths.Any(project.Owns)).Select(other => other.Commit).ToList())));
        }
        result.Errors.AddRange(plan.Errors);
        return (result, new(plan, order));
    }

    private static bool Absent(string blob) => blob.All(digit => digit == '0');

    /// <summary>Parses <c>git diff --raw -z</c>: each file's blob before and after, and its change letter.</summary>
    private static List<(string Path, string Before, string After, string Letter)> Raw(string output)
    {
        var fields = output.Split('\0');
        var entries = new List<(string, string, string, string)>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            var meta = fields[index].TrimStart('\n', ':').Split(' ');
            if (meta.Length < 5) continue;
            entries.Add((fields[index + 1], meta[2], meta[3], meta[4][..1]));
        }
        return entries;
    }

    private static string Reason(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        _ when Planner.IsExcluded(path) => "In a bin, obj, packages or Backup folder, which is never released.",
        ".sql" => "SQL script: release it through the database process; Cultivate never runs SQL.",
        ".config" => "Configuration file: compare it with production and release it by hand if needed.",
        ".md" or ".txt" => "Documentation: not released.",
        ".sln" => "Solution file: not released.",
        "" => "A file without an extension is not released by Cultivate; release it by hand if production needs it.",
        var extension => $"{extension} files are not released by Cultivate; release it by hand if production needs it."
    };

    private static string SqlReason(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".txt" => "Documentation: not released.",
        _ => "Not a SQL script: only the .sql files of a SQL repository are released. Release it by hand if needed."
    };

    private static bool HasWebfarmTarget(string text) => text.Contains("CopyBuildOutputToWebfarm", StringComparison.Ordinal);

    /// <summary>
    /// Every project file in the base commit, and the projects a changed compiled file can belong to: in MyMeyer those with the webfarm copy
    /// target that the dev farm planner also considers; in another repository every project outside bin, obj, packages and Backup folders.
    /// </summary>
    private async Task<Catalog> ReadCatalog(string tip)
    {
        var tracked = await Tracked(tip);
        var paths = tracked.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Order(Files.Comparer).ToList();
        var texts = await Task.WhenAll(paths.Select(async path => (await G("show", $"{tip}:{path.Replace('\\', '/')}")).TrimStart('﻿')));
        var projects = paths.Zip(texts).ToDictionary(pair => pair.First, pair => pair.Second, Files.Comparer);
        var candidates = projects.Where(pair => repository.Main ? HasWebfarmTarget(pair.Value) : !Planner.IsExcluded(pair.Key))
            .Select(pair => ProjectInfo.Read(settings.RepositoryRoot, pair.Key, pair.Value)).ToList();
        return new(tracked, projects, candidates);
    }

    /// <summary>
    /// The unchanged projects the release projects need to compile, transitively: a ProjectReference to a project file in the snapshot, or a
    /// reference to the assembly of a project with the webfarm copy target whose HintPath file Git does not track (a build output). Returns every
    /// project to build, dependencies first, and lists the support projects in <paramref name="result"/>.
    /// </summary>
    private List<ProjectInfo> Support(Catalog catalog, List<ProjectInfo> release, ReleaseResult result)
    {
        var webfarm = catalog.Candidates.ToDictionary(project => project.RelativePath, Files.Comparer);
        // In MyMeyer only projects with the webfarm target are matched by assembly name: Backup copies share their names. Elsewhere an assembly
        // more than one project builds is matched by none.
        var byAssembly = catalog.Candidates.GroupBy(project => project.Assembly, Files.Comparer).Where(group => repository.Main || group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), Files.Comparer);
        var chosen = release.ToDictionary(project => project.RelativePath, Files.Comparer);
        var queue = new Queue<ProjectInfo>(release);
        void Add(ProjectInfo project, string reason)
        {
            chosen[project.RelativePath] = project;
            queue.Enqueue(project);
            result.SupportProjects.Add(new(project.RelativePath, project.Assembly, reason, HasWebfarmTarget(catalog.Projects[project.RelativePath]), !result.ReuseDependencies));
        }
        while (queue.TryDequeue(out var project))
        {
            var directory = Path.GetDirectoryName(project.RelativePath) ?? "";
            foreach (var reference in project.ProjectReferences.Distinct(Files.Comparer))
                if (!chosen.ContainsKey(reference) && catalog.Projects.TryGetValue(reference, out var text))
                    Add(webfarm.GetValueOrDefault(reference) ?? ProjectInfo.Read(settings.RepositoryRoot, reference, text), $"{project.Assembly} has a ProjectReference to it");
            foreach (var assembly in project.AssemblyReferences.Distinct(Files.Comparer))
            {
                if (!byAssembly.TryGetValue(assembly, out var owner) || chosen.ContainsKey(owner.RelativePath)) continue;
                var hints = project.Hints.Where(hint => Path.GetFileNameWithoutExtension(hint).Equals(assembly, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hints.Any(hint => !hint.Contains('$') && catalog.Tracked.Contains(
                    Path.GetRelativePath(settings.RepositoryRoot, Path.GetFullPath(Path.Combine(settings.RepositoryRoot, directory, hint)))))) continue;
                Add(owner, hints.Count == 0 ? $"{project.Assembly} references {assembly}, which only a build produces"
                    : $"{project.Assembly} references {hints[0]}, a build output Git does not track");
            }
        }
        var names = chosen.Values.Where(project => byAssembly.GetValueOrDefault(project.Assembly) == project)
            .ToDictionary(project => project.Assembly, project => project.RelativePath, Files.Comparer);
        var ordered = new List<ProjectInfo>();
        var visiting = new HashSet<string>(Files.Comparer);
        void Visit(ProjectInfo project)
        {
            if (ordered.Contains(project)) return;
            if (!visiting.Add(project.RelativePath)) throw new InvalidOperationException($"Project dependency cycle at {project.RelativePath}.");
            foreach (var dependency in project.ProjectReferences.Where(chosen.ContainsKey)
                .Concat(project.AssemblyReferences.Where(names.ContainsKey).Select(assembly => names[assembly]))
                .Where(path => !Files.Comparer.Equals(path, project.RelativePath)).Distinct(Files.Comparer))
                Visit(chosen[dependency]);
            visiting.Remove(project.RelativePath);
            ordered.Add(project);
        }
        foreach (var project in release.Concat(chosen.Values.Except(release).OrderBy(project => project.RelativePath, Files.Comparer))) Visit(project);
        return ordered;
    }

    private sealed record MergedCommit(ReleaseCommit Commit, string[] Paths);

    /// <summary>
    /// The other merged work on the base branch since the fork point: each first-parent commit up to <paramref name="tip"/> with the files it changed,
    /// other than the merge itself and a rebase merge's copies of the pull request's own commits. A release project's DLL carries these changes to
    /// its files though the pull request branch's own build did not, and they are why a changed file can differ from the pull request's version.
    /// </summary>
    private async Task<List<MergedCommit>> OtherMerged(string head, string fork, string merge, string tip)
    {
        var own = (await G("log", "--format=%ae%x1f%at%x1f%s", fork + ".." + head)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var log = await G("log", "--first-parent", "--diff-merges=first-parent", "--name-only", "--no-renames", "--format=%x1e%H%x1f%ae%x1f%at%x1f%s", fork + ".." + tip);
        var merged = new List<MergedCommit>();
        foreach (var entry in log.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = entry.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            var fields = lines[0].Split('\u001f', 2);
            if (fields[0] == merge || fields.Length < 2 || own.Contains(fields[1])) continue;
            merged.Add(new(new ReleaseCommit(fields[0], fields[1].Split('\u001f').Last()), lines[1..]));
        }
        return merged;
    }

    /// <summary>The release folder's name: <paramref name="name"/>, else the ticket from the branch, else a ticket number starting the title, else PR-N.</summary>
    internal static string FolderName(ReleasePullRequest request, string? name)
    {
        var chosen = name?.Trim() is { Length: > 0 } given ? given : WorktreeCatalog.TicketOf(request.HeadBranch) ??
            (TitleTicket().Match(request.Title) is { Success: true } match ? match.Groups[1].Value : $"PR-{request.Number}");
        return SafeName(chosen);
    }

    private static string SafeName(string name) => IsSafeName(name) ? name
        : throw new InvalidOperationException($"'{name}' cannot be a folder name. Use --name with a single folder name of letters, digits, spaces or dashes.");

    internal static bool IsSafeName(string name) => name.Length is > 0 and <= 100 && name == name.Trim() && !name.EndsWith('.') &&
        name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !ReservedName().IsMatch(name);

    /// <summary>The folder to write: <paramref name="custom"/> when given, else the release root's folder for <paramref name="name"/>.</summary>
    private string Folder(string name, string? custom)
    {
        string folder;
        if (custom is { Length: > 0 })
        {
            if (!Path.IsPathFullyQualified(custom)) throw new InvalidOperationException($"--out needs an absolute folder, not '{custom}'.");
            folder = Files.Full(custom);
        }
        else if (string.IsNullOrWhiteSpace(settings.ReleaseRoot) || !Path.IsPathFullyQualified(settings.ReleaseRoot))
            throw new InvalidOperationException("ReleaseRoot is not set. Run `cultivate setup --release-root PATH`, or pass --out FOLDER.");
        else folder = Path.Combine(Files.Full(settings.ReleaseRoot), name);
        if (Path.GetPathRoot(folder) is { } root && Files.Comparer.Equals(Files.Full(root), folder))
            throw new InvalidOperationException($"Release files need their own folder, not the drive root {folder}.");
        foreach (var (checkout, label) in new[] { (mainCheckout, "MyMeyer"), (settings.RepositoryRoot, repository.Name) })
        {
            var inside = Files.Full(checkout);
            if (Files.Comparer.Equals(folder, inside) || folder.StartsWith(inside + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{folder} is inside your {label} checkout. Choose a release folder outside it.");
        }
        return folder;
    }

    [GeneratedRegex(@"^\s*(\d{3,})\b")]
    private static partial Regex TitleTicket();

    [GeneratedRegex(@"<Project\b[^>]*\bSdk\s*=|<Sdk\b|<Import\b[^>]*\bSdk\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SdkStyle();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}
