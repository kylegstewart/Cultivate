using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Cultivate;

/// <summary>
/// Builds the Meyer Online web release package, MORelease.zip, the way the developer does by hand, in the MeyerOnline checkout itself: switch it to
/// master and pull master, publish Meyer.Online.Server with its FolderProfile publish profile, build the Ionic front end with
/// <c>npm run build-prod</c>, copy the front end's www folder into the publish folder as wwwroot, and zip the publish folder's contents into
/// <c>ReleaseRoot\MORelease\MORelease.zip</c>. Unlike a normal pull, anything Git cannot do exactly as asked stops the build before anything
/// further changes: a release is built from master exactly as origin has it. The checkout is left on master. It never touches a webfarm, the
/// deployment journal or a database.
/// </summary>
/// <remarks>Call <see cref="Preview"/>, show it, then pass it to <see cref="Build"/>. <paramref name="run"/> replaces the external builds and
/// <paramref name="find"/> the PATH lookup of dotnet and npm in tests.</remarks>
internal sealed class OnlineRelease(Settings settings, GitRepository git, OnlineRelease.Runner? run = null, Func<string, string?>? find = null)
{
    internal const string Branch = "master", Folder = "MeyerOnline", ServerProject = "Meyer.Online.Server", AppFolder = "Meyer.Online.App.Ionic",
        ProfileName = "FolderProfile", Script = "build-prod", Name = "MORelease";
    internal const string PullStep = "Pull master", InstallStep = "npm install", PublishStep = "Publish server", FrontEndStep = "Build front end",
        AssembleStep = "Copy wwwroot", ZipStep = "Zip";
    /// <summary>Files the publish must write; they are deleted from the publish folder first so their presence proves this run wrote them.</summary>
    internal static readonly string[] ServerFiles = [ServerProject + ".exe", ServerProject + ".dll", "web.config"];
    internal static readonly string[] AppFiles = ["index.html", "version.json", @".well-known\assetlinks.json", "apple-app-site-association"];

    /// <summary>Runs one external build step to completion, writing its whole output to its log; cancelling kills its process tree.</summary>
    internal delegate Task Runner(OnlineCommand command, CancellationToken cancel);

    /// <summary><c>cultivate online-release [--yes] [--allow-local-changes]</c></summary>
    internal static async Task<int> Command(Settings settings, GitRepository git, string[] args)
    {
        var release = new OnlineRelease(settings, git);
        var preview = await release.Preview(args.Contains("--allow-local-changes"));
        if (!args.Contains("--yes"))
        {
            Console.WriteLine(JsonSerializer.Serialize(preview, Files.Json));
            return preview.CanBuild ? 0 : 2;
        }
        using var cancel = new CancellationTokenSource();
        void Stop(object? sender, ConsoleCancelEventArgs pressed)
        {
            pressed.Cancel = true;
            Console.Error.WriteLine("Cancelling: stopping the builds.");
            cancel.Cancel();
        }
        Console.CancelKeyPress += Stop;
        try
        {
            var result = await release.Build(preview, message => Console.Error.WriteLine(message), cancel.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, Files.Json));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Cancelled. {preview.Zip} was not changed.");
            return 1;
        }
        finally { Console.CancelKeyPress -= Stop; }
    }

    /// <summary>The MeyerOnline clone beside <paramref name="repositoryRoot"/>, when there is one.</summary>
    internal static string? Discover(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathFullyQualified(repositoryRoot) || Path.GetDirectoryName(Files.Full(repositoryRoot)) is not { } clones)
            return null;
        var guess = Path.Combine(clones, Folder);
        return Path.Exists(Path.Combine(guess, ".git")) ? guess : null;
    }

    /// <summary>The configured MeyerOnline checkout, or why there is none to build in.</summary>
    internal string Root()
    {
        if (string.IsNullOrWhiteSpace(settings.MeyerOnlineRoot))
            throw new InvalidOperationException($"No MeyerOnline checkout is set, and none was found beside {settings.RepositoryRoot}. " +
                "Run `cultivate setup --meyeronline PATH`.");
        if (!Path.IsPathFullyQualified(settings.MeyerOnlineRoot))
            throw new InvalidOperationException($"MeyerOnlineRoot must be an absolute folder, not '{settings.MeyerOnlineRoot}'. Run `cultivate setup --meyeronline PATH`.");
        var root = Files.Full(settings.MeyerOnlineRoot);
        if (!Path.Exists(Path.Combine(root, ".git")))
            throw new InvalidOperationException($"{root} is not a Git checkout. Point Cultivate at your MeyerOnline clone with `cultivate setup --meyeronline PATH`.");
        return root;
    }

    /// <summary>Where the package goes: <c>ReleaseRoot\MORelease\MORelease.zip</c>.</summary>
    private string? Zip(string root, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.ReleaseRoot) || !Path.IsPathFullyQualified(settings.ReleaseRoot))
        {
            errors.Add("ReleaseRoot is not set. Run `cultivate setup --release-root PATH`.");
            return null;
        }
        var zip = Path.Combine(Files.Full(settings.ReleaseRoot), Name, Name + ".zip");
        if (zip.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{zip} is inside the MeyerOnline checkout. Choose a ReleaseRoot outside it.");
        return zip;
    }

    /// <summary>
    /// Fetches master from origin as a normal <c>git fetch origin master</c> does, so origin/master moves, and works out everything building
    /// would do and every reason it cannot. Changes nothing else.
    /// </summary>
    public async Task<OnlineReleaseResult> Preview(bool allowLocalChanges = false)
    {
        var watch = Stopwatch.StartNew();
        var result = await Read(allowLocalChanges);
        var steps = new List<string>();
        if (result.CurrentBranch != Branch) steps.Add($"switches {result.Repository} from {result.CurrentBranch} to {Branch}");
        if (result.Master is { Behind: > 0 } master) steps.Add($"pulls {master.Behind} new commit(s) into {Branch}");
        if (!result.NodeModules) steps.Add("runs npm install");
        steps.Add($"publishes {ServerProject} with {ProfileName}, runs npm run {Script}, and {(result.ZipExists ? "replaces" : "writes")} {result.Zip}");
        result.Note = result.CanBuild ? $"Preview only: nothing was built. Building {string.Join(", ", steps)}."
            : "The MeyerOnline release cannot be built: " + string.Join(" ", result.Errors);
        result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        return result;
    }

    private async Task<OnlineReleaseResult> Read(bool allowLocalChanges)
    {
        var root = Root();
        Task<string> G(params string[] arguments) => git.Git(root, arguments);
        var (errors, warnings) = (new List<string>(), new List<string>());

        // Starting Git costs more than most of what it is asked here, so what does not depend on the fetch runs alongside it.
        var markersRead = G("rev-parse", "--path-format=absolute", "--git-path", "MERGE_HEAD", "--git-path", "rebase-merge", "--git-path", "rebase-apply",
            "--git-path", "CHERRY_PICK_HEAD", "--git-path", "REVERT_HEAD", "--git-path", "BISECT_LOG");
        async Task<string> Current()
        {
            try { return await G("symbolic-ref", "--quiet", "--short", "HEAD"); }
            catch (InvalidOperationException) { return $"(detached {await G("rev-parse", "--short=7", "HEAD")})"; }
        }
        var currentRead = Current();
        var worktreesRead = G("worktree", "list", "--porcelain", "-z");
        var statusRead = G("status", "--porcelain=v1", "-z", "--untracked-files=all");

        string[] operations = ["merge", "rebase", "rebase", "cherry-pick", "revert", "bisect"];
        var markers = (await markersRead).Split('\n', StringSplitOptions.TrimEntries);
        var operation = operations.Where((_, index) => index < markers.Length && Path.Exists(markers[index])).FirstOrDefault();
        if (operation is not null)
            errors.Add($"A {operation} is in progress in {root}. Finish or abort it, then build again; nothing was changed.");
        var current = await currentRead;

        try { await G("fetch", "--quiet", "origin", Branch); }
        catch (InvalidOperationException exception) { errors.Add($"Could not fetch {Branch} from origin: {BasePull.GitProblem(exception)}."); }
        var origin = await G("for-each-ref", "--format=%(objectname)", "refs/remotes/origin/" + Branch);
        var local = await G("for-each-ref", "--format=%(objectname)", "refs/heads/" + Branch);
        OnlineMaster? master = null;
        if (origin.Length == 0) errors.Add($"Origin has no {Branch} branch to build the release from.");
        else
        {
            var (behind, ahead) = (0, 0);
            if (local.Length != 0)
            {
                var counts = (await G("rev-list", "--left-right", "--count", $"{local}...{origin}")).Split('\t');
                (ahead, behind) = (int.Parse(counts[0]), int.Parse(counts[1]));
            }
            var detail = local.Length == 0 ? $"You have no local {Branch}; building creates it from origin's {origin[..7]}." :
                ahead != 0 ? $"{Branch} has {ahead} local commit(s) that origin does not." :
                behind == 0 ? $"{Branch} is up to date with origin ({origin[..7]})." : $"{Branch} is {behind} commit(s) behind origin; building pulls it.";
            master = new(local, origin, behind, ahead, detail);
            if (ahead != 0)
                errors.Add($"Your local {Branch} has {ahead} commit(s) that origin does not. A release is built from origin's {Branch}: push them or move " +
                    $"them to another branch, then build again; nothing was changed.");
        }

        var elsewhere = (await worktreesRead).Split("\0\0", StringSplitOptions.RemoveEmptyEntries).Select(block => block.Split('\0'))
            .Where(fields => fields.Contains("branch refs/heads/" + Branch))
            .Select(fields => fields.FirstOrDefault(field => field.StartsWith("worktree ", StringComparison.Ordinal))?[9..])
            .OfType<string>().Select(Files.Full).FirstOrDefault(path => !Files.Comparer.Equals(path, root));
        if (elsewhere is not null)
            errors.Add($"{Branch} is checked out in another worktree, {elsewhere}, so {root} cannot switch to it. Switch that worktree to another branch, " +
                "then build again; nothing was changed.");

        var (modified, untracked) = (new List<string>(), new List<string>());
        var entries = (await statusRead).Split('\0');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4) continue;
            if (entry.StartsWith("??", StringComparison.Ordinal)) untracked.Add(entry[3..]);
            else
            {
                modified.Add(entry[3..]);
                // A rename or copy is followed by the path it came from.
                if (entry[..2].IndexOfAny(['R', 'C']) >= 0) index++;
            }
        }
        if (modified.Count != 0 && !allowLocalChanges)
            errors.Add($"{root} has {modified.Count} modified tracked file(s): {Names(modified)}. A release is built from {Branch} exactly as origin has it: " +
                "commit, stash or undo them, or build with them anyway (--allow-local-changes); nothing was changed.");
        else if (modified.Count != 0)
            warnings.Add($"Building with local changes to {modified.Count} tracked file(s): {Names(modified)}. The package will not match origin's {Branch} exactly.");
        if (untracked.Count != 0)
            warnings.Add($"{untracked.Count} untracked file(s) that Git does not ignore are in {root}: {Names(untracked)}. They stay where they are, and the builds can see them.");

        // The profile master will have, which is the one the build reads after the pull.
        string? text = null;
        if (origin.Length != 0)
            try { text = (await G("show", $"{origin}:{ServerProject}/Properties/PublishProfiles/{ProfileName}.pubxml")).TrimStart('﻿'); }
            catch (InvalidOperationException) { }
        var profile = origin.Length == 0 ? null : Profile(root, text, errors);
        var csproj = Path.Combine(root, ServerProject, ServerProject + ".csproj");
        if (!File.Exists(csproj)) errors.Add($"{csproj} was not found.");
        var app = Path.Combine(root, AppFolder);
        if (!File.Exists(Path.Combine(app, "package.json"))) errors.Add($"{Path.Combine(app, "package.json")} was not found.");
        var nodeModules = Directory.Exists(Path.Combine(app, "node_modules"));
        if (!nodeModules) warnings.Add($"{AppFolder} has no node_modules; building runs npm install there first.");
        var lookup = find ?? OnPath;
        var dotnet = lookup("dotnet.exe");
        var npm = lookup("npm.cmd");
        if (dotnet is null) errors.Add("dotnet was not found on PATH. Install the .NET SDK, then open a new terminal.");
        if (npm is null) errors.Add("npm was not found on PATH. Install Node.js, then open a new terminal.");
        var zip = Zip(root, errors);
        return new()
        {
            Repository = root, CurrentBranch = current, Master = master, Operation = operation, ModifiedFiles = modified, UntrackedFiles = untracked,
            AllowLocalChanges = allowLocalChanges, Profile = profile, NodeModules = nodeModules, Dotnet = dotnet, Npm = npm, Zip = zip ?? "",
            ZipExists = zip is not null && File.Exists(zip), Warnings = warnings, Errors = errors
        };
    }

    private static string Names(List<string> paths) => string.Join(", ", paths.Take(8)) + (paths.Count > 8 ? $" and {paths.Count - 8} more" : "");

    private static string ProfilePath(string root) => Path.Combine(root, ServerProject, "Properties", "PublishProfiles", ProfileName + ".pubxml");

    /// <summary>
    /// The publish profile's settings, read from its text (<paramref name="text"/>, null when there is no such file) so a change to the profile is
    /// followed; null, with the reason, when it cannot be used.
    /// </summary>
    internal static OnlineProfile? Profile(string root, string? text, List<string> errors)
    {
        var project = Path.Combine(root, ServerProject);
        var path = ProfilePath(root);
        if (text is null)
        {
            errors.Add($"The publish profile {path} was not found.");
            return null;
        }
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var property in XDocument.Parse(text).Root?.Elements().Where(element => element.Name.LocalName == "PropertyGroup").SelectMany(group => group.Elements()) ?? [])
                properties[property.Name.LocalName] = property.Value.Trim();
        }
        catch (XmlException exception)
        {
            errors.Add($"The publish profile {path} could not be read: {exception.Message}");
            return null;
        }
        var configuration = properties.GetValueOrDefault("LastUsedBuildConfiguration", "");
        var url = properties.GetValueOrDefault("PublishUrl", "");
        if (configuration.Length == 0) errors.Add($"The publish profile {path} sets no LastUsedBuildConfiguration.");
        if (url.Length == 0)
        {
            errors.Add($"The publish profile {path} sets no PublishUrl.");
            return new(path, configuration, url, "", properties);
        }
        var folder = Files.Full(Path.IsPathFullyQualified(url) ? url : Path.Combine(project, url));
        if (Files.Comparer.Equals(folder, project) || project.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            errors.Add($"The publish profile's PublishUrl {url} is {folder}, which holds the project itself. Cultivate zips the whole publish folder; publish to a folder of its own.");
        return new(path, configuration, url, folder, properties);
    }

    internal static string? OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder =>
        {
            try { return Path.Combine(folder.Trim('"'), name); }
            catch (ArgumentException) { return null; }
        }).FirstOrDefault(File.Exists);

    /// <summary>
    /// Builds what <paramref name="preview"/> showed, checking everything again first: switches to master, pulls it fast-forward only, runs
    /// npm install when needed, publishes the server and builds the front end at the same time, copies www into the publish folder as wwwroot, and
    /// replaces the zip in one step. Anything that fails stops the rest; the zip is only ever replaced by a complete, verified one. Cancelling
    /// kills the builds. <paramref name="step"/> is told when each step starts (true) and ends (false).
    /// </summary>
    public async Task<OnlineReleaseResult> Build(OnlineReleaseResult preview, Action<string> progress, CancellationToken cancel = default,
        Action<string, bool>? step = null)
    {
        var watch = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        var runs = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-online-" + Guid.NewGuid().ToString("N")[..8]);
        var stage = "MeyerOnline pull";
        var times = new List<OnlineStepTime>();
        async Task Timed(string name, Func<Task> work)
        {
            step?.Invoke(name, true);
            var clock = Stopwatch.StartNew();
            await work();
            lock (times) times.Add(new(name, Math.Round(clock.Elapsed.TotalSeconds, 1)));
            step?.Invoke(name, false);
        }
        try
        {
            if (!preview.CanBuild) throw new InvalidOperationException(string.Join("\n", preview.Errors));
            OnlineReleaseResult result = null!;
            var root = "";
            await Timed(PullStep, async () =>
            {
                progress($"Fetching {Branch} from origin and checking {preview.Repository}");
                result = await Read(preview.AllowLocalChanges);
                if (!result.CanBuild) throw new InvalidOperationException("The MeyerOnline release cannot be built: " + string.Join(" ", result.Errors));
                root = result.Repository;
                Task<string> G(params string[] arguments) => git.Git(root, arguments);
                cancel.ThrowIfCancellationRequested();
                var before = await G("rev-parse", "HEAD");
                if (result.CurrentBranch != Branch)
                {
                    progress($"Switching {root} from {result.CurrentBranch} to {Branch}");
                    await G("switch", "--quiet", Branch);
                    (result.Switched, result.SwitchedFrom) = (true, result.CurrentBranch);
                }
                var head = await G("rev-parse", "HEAD");
                var origin = result.Master!.Origin;
                if (head != origin) await G("merge", "--ff-only", "--no-autostash", "--quiet", "refs/remotes/origin/" + Branch);
                var built = await G("rev-parse", "HEAD");
                if (built != origin)
                    throw new InvalidOperationException($"{Branch} in {root} is at {built[..7]}, not origin's {origin[..7]}, after the pull. Nothing was built.");
                result.Pulled = int.Parse(await G("rev-list", "--count", $"{head}..{built}"));
                (result.Commit, result.Subject) = (built, (await G("log", "-1", "--format=%s")).Trim());
                progress(result.Pulled == 0 ? $"{Branch} is already up to date with origin ({built[..7]})"
                    : $"Pulled {Branch}: {result.Pulled} new commit(s), {head[..7]} → {built[..7]}");
                if (preview.Master is { } shown && shown.Origin != origin)
                    result.Warnings.Add($"Origin's {Branch} moved since the preview (was {shown.Origin[..7]}); this package is built from {origin[..7]}.");
                // The publish reads the profile file the pull left, so that is the one used.
                var problems = new List<string>();
                var file = ProfilePath(root);
                result.Profile = Profile(root, File.Exists(file) ? File.ReadAllText(file) : null, problems);
                if (problems.Count != 0) throw new InvalidOperationException(string.Join(" ", problems) + " Nothing was built.");
                if (!result.NodeModules) result.InstallReason = $"{AppFolder} had no node_modules";
                else if ((await G("diff", "--name-only", before, built, "--", AppFolder + "/package.json")).Length != 0)
                    result.InstallReason = $"package.json changed since {before[..7]}";
            });
            var profile = result.Profile!;
            var project = Path.Combine(root, ServerProject);
            var app = Path.Combine(root, AppFolder);
            var publish = profile.PublishFolder;
            Directory.CreateDirectory(runs);
            result.RunDirectory = runs;
            cancel.ThrowIfCancellationRequested();

            if (Directory.Exists(publish))
                foreach (var name in ServerFiles) File.Delete(Path.Combine(publish, name));
            var runner = run ?? Start;
            OnlineCommand Command(string name, string tool, string directory, string file, params string[] arguments)
            {
                var command = new OnlineCommand(name, tool, arguments, directory, Path.Combine(runs, file));
                result.Logs.Add(command.Log);
                return command;
            }
            var publishing = Command(PublishStep, result.Dotnet!, project, "publish.log",
                "publish", Path.Combine(project, ServerProject + ".csproj"), "-c", profile.Configuration, "-p:PublishProfile=" + ProfileName);
            var installing = result.InstallReason is null ? null : Command(InstallStep, "cmd.exe", app, "npm-install.log", "/c", "npm", "install");
            var building = Command(FrontEndStep, "cmd.exe", app, "front-end.log", "/c", "npm", "run", Script);
            stage = "MeyerOnline build";
            using var builds = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var server = Task.Run(() => Timed(PublishStep, async () =>
            {
                progress($"Publishing {ServerProject} with {ProfileName} ({profile.Configuration}) to {publish}");
                await runner(publishing, builds.Token);
                progress($"Published {ServerProject}");
            }));
            var front = Task.Run(async () =>
            {
                if (installing is not null)
                    await Timed(InstallStep, async () =>
                    {
                        progress($"Running npm install in {AppFolder}: {result.InstallReason}");
                        await runner(installing, builds.Token);
                        result.Installed = true;
                    });
                await Timed(FrontEndStep, async () =>
                {
                    progress($"Running npm run {Script} in {AppFolder}");
                    await runner(building, builds.Token);
                    progress("Built the front end");
                });
            });
            var first = await Task.WhenAny(server, front);
            if (!first.IsCompletedSuccessfully)
            {
                // Either build failing stops the other; the first failure is the one reported.
                builds.Cancel();
                try { await Task.WhenAll(server, front); }
                catch (Exception) { } // The first failure is the one thrown below.
                await first;
            }
            await Task.WhenAll(server, front);
            cancel.ThrowIfCancellationRequested();

            stage = "MeyerOnline verification";
            progress("Checking the published files and www");
            var www = Path.Combine(app, "www");
            foreach (var name in ServerFiles.Where(name => !File.Exists(Path.Combine(publish, name))))
                throw new InvalidOperationException($"The publish did not write {Path.Combine(publish, name)}. Check {publishing.Log}; nothing was zipped.");
            foreach (var name in AppFiles.Where(name => !File.Exists(Path.Combine(www, name))))
                throw new InvalidOperationException($"npm run {Script} did not write {Path.Combine(www, name)}. Check {building.Log}; nothing was zipped.");
            var index = Path.Combine(www, "index.html");
            if (File.GetLastWriteTimeUtc(index) < started)
                throw new InvalidOperationException($"{index} was last written {File.GetLastWriteTime(index):yyyy-MM-dd HH:mm:ss}, before this build started, so www is " +
                    $"not from this build. Check {building.Log}; nothing was zipped.");
            result.Version = Version(Path.Combine(www, "version.json"));

            await Timed(AssembleStep, () =>
            {
                stage = "MeyerOnline wwwroot";
                var wwwroot = Path.Combine(publish, "wwwroot");
                Remove(wwwroot);
                var (count, bytes) = Measure(www);
                progress($"Copying www to {wwwroot} ({TicketWorkspaces.Count(count, "file")}, {TidySizes.Format(bytes)})");
                foreach (var file in Directory.EnumerateFiles(www, "*", SearchOption.AllDirectories))
                {
                    cancel.ThrowIfCancellationRequested();
                    var target = Path.Combine(wwwroot, Path.GetRelativePath(www, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                if (Measure(wwwroot) != (count, bytes))
                    throw new IOException($"{wwwroot} does not hold the {count} file(s) and {bytes} bytes of {www} after copying. Build again; nothing was zipped.");
                return Task.CompletedTask;
            });

            await Timed(ZipStep, () =>
            {
                stage = "MeyerOnline package";
                Package(publish, result, progress, cancel);
                return Task.CompletedTask;
            });
            result.Built = true;
            result.Steps.AddRange(new[] { PullStep, InstallStep, PublishStep, FrontEndStep, AssembleStep, ZipStep }
                .SelectMany(name => times.Where(time => time.Step == name)));
            result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            result.Note = $"Wrote {result.Zip}: {TicketWorkspaces.Count(result.Entries, "file")}, {TidySizes.Format(result.Size)}, built from {Branch} at {result.Commit[..7]}.";
            Files.WriteJson(Path.Combine(runs, "online-release.json"), result);
            Files.WriteJson(Path.Combine(Files.Home, "Releases", $"{Name}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.result.json"), result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Attach(exception, new(stage) { Branch = Branch, Worktree = preview.Repository, RunDirectory = Directory.Exists(runs) ? runs : null });
            throw;
        }
    }

    /// <summary>Zips every file in the publish folder, paths relative to it, beside the destination, checks it, and only then replaces the zip.</summary>
    private static void Package(string publish, OnlineReleaseResult result, Action<string> progress, CancellationToken cancel)
    {
        var zip = result.Zip;
        var folder = Path.GetDirectoryName(zip)!;
        Directory.CreateDirectory(folder);
        var files = Directory.GetFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var temporary = Path.Combine(folder, $".{Name}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
        progress($"Zipping {TicketWorkspaces.Count(files.Count, "file")} to {zip}");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(publish, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            using (var check = ZipFile.OpenRead(temporary))
            {
                var names = check.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (check.Entries.Count != files.Count || !names.Contains("wwwroot/index.html") || !names.Contains(ServerProject + ".exe"))
                    throw new IOException($"The new zip holds {check.Entries.Count} entries, not the {files.Count} files of {publish}. {zip} was not changed.");
                result.Entries = check.Entries.Count;
            }
            cancel.ThrowIfCancellationRequested();
            File.Move(temporary, zip, true);
        }
        finally { File.Delete(temporary); }
        result.Size = new FileInfo(zip).Length;
        result.Sha256 = Files.Hash(zip);
        progress($"Wrote {zip} ({TidySizes.Format(result.Size)})");
    }

    private static (int Files, long Bytes) Measure(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Aggregate((Files: 0, Bytes: 0L), (total, file) => (total.Files + 1, total.Bytes + new FileInfo(file).Length));

    private static void Remove(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, true);
    }

    /// <summary>version.json's <c>lastupdated</c>, or its text when it has none.</summary>
    private static string Version(string path)
    {
        var text = File.ReadAllText(path).Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("lastupdated", out var value) ? value.ToString() : text;
        }
        catch (JsonException) { return text; }
    }

    /// <summary>
    /// Runs <paramref name="command"/> without a window or input, writing every line of its output to its log as it arrives. A failure names the log
    /// and carries its last lines; cancelling kills the whole process tree, so npm's node children stop too.
    /// </summary>
    internal static async Task Start(OnlineCommand command, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(command.Tool)
        {
            WorkingDirectory = command.Directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        var context = new FailureContext(command.Step) { Tool = command.Tool, Log = command.Log, RunDirectory = Path.GetDirectoryName(command.Log) };
        Directory.CreateDirectory(Path.GetDirectoryName(command.Log)!);
        var tail = new Queue<string>();
        var closed = false;
        using var log = new StreamWriter(command.Log, false, new UTF8Encoding(false)) { AutoFlush = true };
        void Write(string line)
        {
            lock (tail)
            {
                if (closed) return;
                log.WriteLine(line);
                tail.Enqueue(line);
                if (tail.Count > 15) tail.Dequeue();
            }
        }
        log.WriteLine($"[{DateTime.UtcNow:O}] {command.Step}\nDirectory: {command.Directory}\nCommand: {command.Tool}\n" +
            $"Arguments (JSON): {JsonSerializer.Serialize(command.Arguments)}\n");
        var clock = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {command.Tool}."); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw Diagnostics.Attach(exception, context);
        }
        using (process)
        {
            process.StandardInput.Close();
            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line) Write(line);
            }
            var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
            try { await process.WaitForExitAsync(cancel); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // It had already exited.
                await process.WaitForExitAsync();
                await Task.WhenAny(pumps, Task.Delay(2000));
                lock (tail)
                {
                    log.WriteLine($"\nCancelled after {clock.Elapsed}; the process tree was stopped.");
                    closed = true;
                }
                throw;
            }
            // A child that outlives the tool can hold its output open; the log keeps what arrived.
            await Task.WhenAny(pumps, Task.Delay(5000));
            string[] last;
            lock (tail)
            {
                log.WriteLine($"\nExit code: {process.ExitCode}; elapsed: {clock.Elapsed}");
                closed = true;
                last = [.. tail];
            }
            if (process.ExitCode != 0)
                throw Diagnostics.Attach(new InvalidOperationException($"{command.Step} failed ({process.ExitCode}). Last lines of its output:\n" +
                    string.Join("\n", last) + $"\nFull log: {command.Log}"), context with { ExitCode = process.ExitCode });
        }
    }
}
