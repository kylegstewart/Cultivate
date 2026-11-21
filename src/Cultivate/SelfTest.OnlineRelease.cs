using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>
    /// Build MeyerOnline release on a fixture origin and checkout. The publish, npm install and npm builds are faked through the runner seam and dotnet
    /// and npm are "found" by a fake PATH lookup, so nothing needs the real MeyerOnline repository, npm, or a net5.0 publish.
    /// </summary>
    private static async Task CheckOnlineRelease(string home)
    {
        await CheckOnlineDiscovery(home);
        await CheckOnlineRunner(home);
        var root = Path.Combine(home, "mo");
        var origin = Path.Combine(root, "origin.git");
        var author = Path.Combine(root, "author");
        var checkout = Path.Combine(root, OnlineRelease.Folder);
        var output = Path.Combine(root, "Releases");
        var project = Path.Combine(checkout, OnlineRelease.ServerProject);
        var app = Path.Combine(checkout, OnlineRelease.AppFolder);
        var zip = Path.Combine(output, OnlineRelease.Name, OnlineRelease.Name + ".zip");
        Directory.CreateDirectory(root);
        var settings = new Settings { RepositoryRoot = Path.Combine(root, "MyMeyer"), MeyerOnlineRoot = checkout, ReleaseRoot = output };
        var git = new GitRepository(settings);
        Task<string> A(params string[] args) => git.Git(author, args);
        Task<string> C(params string[] args) => git.Git(checkout, args);
        static string Profile(string configuration, string url) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project>
              <PropertyGroup>
                <DeleteExistingFiles>true</DeleteExistingFiles>
                <LastUsedBuildConfiguration>{configuration}</LastUsedBuildConfiguration>
                <PublishUrl>{url}</PublishUrl>
                <WebPublishMethod>FileSystem</WebPublishMethod>
                <TargetFramework>net5.0</TargetFramework>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <SelfContained>true</SelfContained>
              </PropertyGroup>
            </Project>
            """;
        async Task<string> Commit(string branch, string message, params (string Path, string Content)[] files)
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(author, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            await A("add", "-A");
            await A("commit", "-qm", message);
            await A("push", "-q", "origin", "HEAD:refs/heads/" + branch);
            return await A("rev-parse", "HEAD");
        }
        await git.Git(root, "init", "-q", "--bare", "-b", "master", origin);
        await git.Git(root, "clone", "-q", origin, author);
        await A("config", "user.email", "cultivate-test@localhost");
        await A("config", "user.name", "Cultivate isolated tests");
        var c0 = await Commit("master", "base",
            (".gitignore", "Meyer.Online.Server/bin/\nMeyer.Online.Server/obj/\nMeyer.Online.App.Ionic/www/\nMeyer.Online.App.Ionic/node_modules/\n"),
            (@"Meyer.Online.Server\Meyer.Online.Server.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"),
            (@"Meyer.Online.Server\Properties\PublishProfiles\FolderProfile.pubxml", Profile("Release", @"bin\Release\net5.0")),
            (@"Meyer.Online.App.Ionic\package.json", "{ \"scripts\": { \"build-prod\": \"fixture\" } }"), ("README.md", "readme 0"));
        // As on the real origin, development is the default branch, so a fresh clone starts there and only master is ever built.
        await A("switch", "-q", "-c", "development");
        await Commit("development", "development work", ("Development.txt", "not on master"));
        await git.Git(origin, "symbolic-ref", "HEAD", "refs/heads/development");
        await git.Git(root, "clone", "-q", origin, checkout);
        await C("config", "user.email", "cultivate-test@localhost");
        await C("config", "user.name", "Cultivate isolated tests");
        await C("branch", "-q", "master", "origin/master");
        Directory.CreateDirectory(Path.Combine(app, "node_modules"));
        await A("switch", "-q", "master");
        await Commit("master", "Update the readme", ("README.md", "readme 1"));
        var c2 = await Commit("master", "Server change for 41001", (@"Meyer.Online.Server\Startup.cs", "class Startup { }"));

        var calls = new List<OnlineCommand>();
        var publishUrl = @"bin\Release\net5.0";
        string? fail = null;
        var (frontEndWrites, holdPublish) = (true, false);
        var publishStarted = false;
        static void Write(string folder, string relative, string content)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        async Task Fake(OnlineCommand command, CancellationToken cancel)
        {
            lock (calls) calls.Add(command);
            if (command.Step == fail)
            {
                await OnlineRelease.Start(command with { Tool = "cmd.exe", Arguments = ["/c", $"echo {command.Step} broke here& exit 3"] }, cancel);
                return;
            }
            if (command.Step == OnlineRelease.PublishStep) publishStarted = true;
            if (command.Step == OnlineRelease.PublishStep && holdPublish) await Task.Delay(Timeout.Infinite, cancel);
            await Task.Delay(50, cancel);
            switch (command.Step)
            {
                case OnlineRelease.PublishStep:
                    // The profile's DeleteExistingFiles empties the folder first, a previous wwwroot included.
                    var publish = Path.Combine(command.Directory, publishUrl);
                    if (Directory.Exists(publish)) Directory.Delete(publish, true);
                    foreach (var name in new[] { "Meyer.Online.Server.exe", "Meyer.Online.Server.dll", "web.config", "appsettings.json", @"runtimes\win-x64\native\e_sqlite3.dll" })
                        Write(publish, name, "published " + name);
                    break;
                case OnlineRelease.FrontEndStep when frontEndWrites:
                    var www = Path.Combine(command.Directory, "www");
                    if (Directory.Exists(www)) Directory.Delete(www, true);
                    foreach (var name in new[] { "index.html", @".well-known\assetlinks.json", "apple-app-site-association", @"assets\app.js" }) Write(www, name, "built " + name);
                    Write(www, "version.json", "{\"lastupdated\": \"11/20/2026, 9:00:00 AM\"}");
                    break;
                case OnlineRelease.InstallStep:
                    Directory.CreateDirectory(Path.Combine(command.Directory, "node_modules"));
                    break;
            }
            File.AppendAllText(command.Log, command.Step + " done\n");
        }
        static string? Find(string name) => @"C:\fixture\tools\" + name;
        OnlineRelease Release() => new(settings, git, Fake, Find);
        async Task<string> State() => string.Join("\n", await C("symbolic-ref", "HEAD"), await C("rev-parse", "HEAD"), await C("status", "--porcelain", "--ignored"),
            string.Join("\n", (await C("for-each-ref", "--format=%(refname) %(objectname)")).Split('\n')
                .Where(line => !line.StartsWith("refs/remotes/origin/master ", StringComparison.Ordinal))));
        string? ZipHash() => File.Exists(zip) ? Files.Hash(zip) : null;
        bool Leftovers() => Directory.Exists(Path.GetDirectoryName(zip)) && Directory.GetFiles(Path.GetDirectoryName(zip)!, "*.tmp").Length != 0;

        // An earlier package is there, to be replaced.
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using (var old = ZipFile.Open(zip, ZipArchiveMode.Create)) old.CreateEntry("old.txt");
        var oldZip = ZipHash();
        var before = await State();
        var preview = await Release().Preview();
        Check(await State() == before && await C("rev-parse", "refs/remotes/origin/master") == c2 && calls.Count == 0 && ZipHash() == oldZip,
            "a MeyerOnline preview fetches master, moving origin/master, and changes nothing else: no branch, file, build or zip");
        Check(preview.CanBuild && preview.Repository == checkout && preview.CurrentBranch == "development" && preview.Branch == "master" &&
            preview.Master is { Local: var local, Origin: var tip, Behind: 2, Ahead: 0 } && local == c0 && tip == c2 &&
            preview.Master.Detail == "master is 2 commit(s) behind origin; building pulls it." && preview.ModifiedFiles.Count == 0 && preview.NodeModules &&
            preview.Dotnet == @"C:\fixture\tools\dotnet.exe" && preview.Npm == @"C:\fixture\tools\npm.cmd" && preview.Zip == zip && preview.ZipExists,
            "the preview reports the checkout's branch, how far master is behind origin, the tools found and the zip it replaces");
        Check(preview.Profile is { Configuration: "Release", PublishUrl: @"bin\Release\net5.0" } profile && profile.PublishFolder == Path.Combine(project, @"bin\Release\net5.0") &&
            profile.Properties["DeleteExistingFiles"] == "true" && profile.Properties["RuntimeIdentifier"] == "win-x64" && profile.Properties.Count == 7,
            "the preview reads the publish profile's configuration, folder and every property from FolderProfile.pubxml");
        Check(preview.Note == $"Preview only: nothing was built. Building switches {checkout} from development to master, pulls 2 new commit(s) into master, " +
            $"publishes Meyer.Online.Server with FolderProfile, runs npm run build-prod, and replaces {zip}.",
            "the preview's note says it built nothing and everything building would do");
        var json = JsonSerializer.Serialize(preview, Files.Json);
        Check(new[] { "\"CurrentBranch\": \"development\"", "\"Behind\": 2", "\"ModifiedFiles\": []", "\"PublishFolder\":", "\"NodeModules\": true", "\"ZipExists\": true",
            "\"Errors\": []", "\"CanBuild\": true" }.All(json.Contains), "the preview's JSON names each field the command prints");

        // master checked out in another worktree: stopped and named.
        var other = Path.Combine(root, "master-wt");
        await C("worktree", "add", "-q", other, "master");
        var blocked = await Release().Preview();
        Check(!blocked.CanBuild && blocked.Errors.Single() == $"master is checked out in another worktree, {other}, so {checkout} cannot switch to it. " +
            "Switch that worktree to another branch, then build again; nothing was changed.", "master checked out in another worktree stops the build and names that worktree");
        before = await State();
        await Reject(() => Release().Build(preview, _ => { }), "building after master moved into another worktree is refused, even from a buildable preview");
        Check(await State() == before && calls.Count == 0 && ZipHash() == oldZip, "a refused build changes no branch, starts no build and leaves the zip");
        await C("worktree", "remove", other);

        // Modified tracked files stop the build unless allowed.
        File.WriteAllText(Path.Combine(project, "Meyer.Online.Server.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><!-- local --></Project>");
        File.WriteAllText(Path.Combine(checkout, "scratch.txt"), "untracked");
        var dirty = await Release().Preview();
        Check(!dirty.CanBuild && dirty.ModifiedFiles.SequenceEqual(["Meyer.Online.Server/Meyer.Online.Server.csproj"]) && dirty.UntrackedFiles.SequenceEqual(["scratch.txt"]) &&
            dirty.Errors.Single().StartsWith($"{checkout} has 1 modified tracked file(s): Meyer.Online.Server/Meyer.Online.Server.csproj. A release is built from master exactly as origin has it", StringComparison.Ordinal) &&
            dirty.Errors.Single().Contains("--allow-local-changes") && dirty.Warnings.Any(warning => warning.StartsWith("1 untracked file(s) that Git does not ignore", StringComparison.Ordinal)),
            "a modified tracked file stops the build and is listed; an untracked file only warns");
        before = await State();
        await Reject(() => Release().Build(preview, _ => { }), "building with a modified tracked file is refused");
        Check(await State() == before && calls.Count == 0, "a refused build leaves the branch and files as they were");
        var allowed = await Release().Preview(allowLocalChanges: true);
        Check(allowed.CanBuild && allowed.AllowLocalChanges && allowed.Warnings.Any(warning => warning.StartsWith("Building with local changes to 1 tracked file(s)", StringComparison.Ordinal)),
            "--allow-local-changes lets the build go ahead, with a warning that the package will not match origin's master");
        await C("checkout", "--", "Meyer.Online.Server/Meyer.Online.Server.csproj");
        // build-prod rewrites capacitor.config.json with LF endings after a CRLF checkout: status lists it from its size alone, with nothing changed.
        var ignore = Path.Combine(checkout, ".gitignore");
        await C("config", "core.autocrlf", "true");
        File.Delete(ignore);
        await C("checkout", "--", ".gitignore");
        File.WriteAllText(ignore, File.ReadAllText(ignore).Replace("\r\n", "\n"));
        var listed = (await C("status", "--porcelain")).Contains(".gitignore", StringComparison.Ordinal) && (await C("diff", "--name-only", "HEAD")).Length == 0;
        var rewritten = await Release().Preview();
        Check(listed && rewritten.CanBuild && rewritten.ModifiedFiles.Count == 0,
            "a tracked file rewritten with other line endings, which status lists although its content is the commit's, does not count as modified");
        await C("config", "--unset", "core.autocrlf");
        File.Delete(ignore);
        await C("checkout", "--", ".gitignore");
        File.Delete(Path.Combine(checkout, "scratch.txt"));

        // A clean run from development, with master behind origin.
        var messages = new List<string>();
        var events = new List<string>();
        var result = await Release().Build(preview, message => { lock (messages) messages.Add(message); }, default, (name, started) => { lock (events) events.Add((started ? "+" : "-") + name); });
        Check(result.Built && result.Switched && result.SwitchedFrom == "development" && result.Pulled == 2 && result.Commit == c2 && result.Subject == "Server change for 41001" &&
            await C("symbolic-ref", "--short", "HEAD") == "master" && await C("rev-parse", "HEAD") == c2,
            "building switches the checkout from development to master, pulls master to origin's tip, says so, and leaves it on master");
        Check(messages.Contains($"Switching {checkout} from development to master") && messages.Contains($"Pulled master: 2 new commit(s), {c0[..7]} → {c2[..7]}") &&
            !result.Installed && result.InstallReason is null && calls.All(call => call.Step != OnlineRelease.InstallStep),
            "progress says the branch was switched and what was pulled, and npm install is skipped when node_modules is there and package.json did not change");
        var publishCall = calls.Single(call => call.Step == OnlineRelease.PublishStep);
        var buildCall = calls.Single(call => call.Step == OnlineRelease.FrontEndStep);
        Check(publishCall.Tool == @"C:\fixture\tools\dotnet.exe" && publishCall.Directory == project &&
            publishCall.Arguments.SequenceEqual(["build", Path.Combine(project, "Meyer.Online.Server.csproj"), "-c", "Release", "-p:DeployOnBuild=true", "-p:PublishProfile=FolderProfile"]) &&
            buildCall.Tool == "cmd.exe" && buildCall.Directory == app && buildCall.Arguments.SequenceEqual(["/c", "npm", "run", "build-prod"]),
            "the server is published through FolderProfile with its configuration and nothing overridden, and the front end is built with npm run build-prod");
        List<string> Entries()
        {
            using var archive = ZipFile.OpenRead(zip);
            return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToList();
        }
        var entries = Entries();
        Check(entries.SequenceEqual(new[] { "Meyer.Online.Server.exe", "Meyer.Online.Server.dll", "web.config", "appsettings.json", "runtimes/win-x64/native/e_sqlite3.dll",
                "wwwroot/index.html", "wwwroot/version.json", "wwwroot/.well-known/assetlinks.json", "wwwroot/apple-app-site-association", "wwwroot/assets/app.js" }
                .Order(StringComparer.Ordinal)) && result.Entries == 10,
            "the zip's root is the publish folder's contents, with the front end's www as wwwroot");
        Check(ZipHash() != oldZip && result.Sha256 == ZipHash() && result.Size == new FileInfo(zip).Length && result.Zip == zip && !Leftovers(),
            "the existing MORelease.zip is replaced in one step, and the result's size and SHA-256 are the new zip's");
        Check(result.Version == "11/20/2026, 9:00:00 AM" && result.Steps.Select(step => step.Step).SequenceEqual(
                [OnlineRelease.PullStep, OnlineRelease.PublishStep, OnlineRelease.FrontEndStep, OnlineRelease.AssembleStep, OnlineRelease.ZipStep]) &&
            events.Count(item => item.StartsWith('+')) == 5 && events.Count(item => item.StartsWith('-')) == 5,
            "the result has version.json's value and each step's time, and every step is reported starting and ending");
        Check(File.Exists(Path.Combine(result.RunDirectory, "online-release.json")) && result.Logs.All(File.Exists) &&
            Directory.GetFiles(Path.Combine(Files.Home, "Releases"), "MORelease.*.result.json").Length == 1 &&
            result.RunDirectory.StartsWith(Path.Combine(Files.Home, "Runs") + "\\", StringComparison.OrdinalIgnoreCase),
            "the result is recorded in the run folder and under Releases, with each build's log kept in the run folder");

        // Local changes allowed: the build goes ahead and says so.
        File.WriteAllText(Path.Combine(project, "Meyer.Online.Server.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><!-- local --></Project>");
        var withChanges = await Release().Build(await Release().Preview(allowLocalChanges: true), _ => { });
        Check(withChanges.Built && !withChanges.Switched && withChanges.Pulled == 0 && withChanges.Warnings.Any(warning => warning.StartsWith("Building with local changes", StringComparison.Ordinal)),
            "with local changes allowed, a build on master that is up to date builds and warns");
        await C("checkout", "--", "Meyer.Online.Server/Meyer.Online.Server.csproj");

        // Local commits on master: stopped.
        File.WriteAllText(Path.Combine(checkout, "Local.txt"), "local");
        await C("add", "Local.txt");
        await C("commit", "-qm", "local only");
        var ahead = await Release().Preview();
        Check(!ahead.CanBuild && ahead.Master?.Ahead == 1 && ahead.Errors.Single().StartsWith("Your local master has 1 commit(s) that origin does not.", StringComparison.Ordinal),
            "local commits on master that origin lacks stop the build");
        var head = await C("rev-parse", "HEAD");
        await Reject(() => Release().Build(preview, _ => { }), "building with local commits on master is refused");
        Check(await C("rev-parse", "HEAD") == head, "a refused build leaves the local commits where they are");
        await C("reset", "-q", "--hard", "origin/master");

        // Either build failing stops everything and leaves the zip.
        foreach (var step in new[] { OnlineRelease.PublishStep, OnlineRelease.FrontEndStep })
        {
            fail = step;
            var kept = ZipHash();
            Exception? failure = null;
            try { await Release().Build(await Release().Preview(), _ => { }); }
            catch (InvalidOperationException exception) { failure = exception; }
            var report = failure is null ? null : Diagnostics.Describe(failure);
            var log = report?.Context.Log;
            Check(failure is not null && log is not null && failure.Message.Contains($"{step} broke here") && failure.Message.Contains("Full log: " + log) &&
                File.ReadAllText(log).Contains($"{step} broke here") && report!.Context.Stage == step && report.Context.ExitCode == 3 && ZipHash() == kept && !Leftovers(),
                $"{step} failing stops the build with its log path and last output, and the old zip is untouched");
        }
        fail = null;

        // A www the build did not write is rejected.
        frontEndWrites = false;
        File.SetLastWriteTimeUtc(Path.Combine(app, "www", "index.html"), DateTime.UtcNow.AddHours(-1));
        var stale = ZipHash();
        Exception? rejected = null;
        try { await Release().Build(await Release().Preview(), _ => { }); }
        catch (InvalidOperationException exception) { rejected = exception; }
        Check(rejected?.Message.StartsWith(Path.Combine(app, "www", "index.html") + " was last written", StringComparison.Ordinal) == true &&
            rejected.Message.Contains("before this build started") && ZipHash() == stale, "a www whose index.html is older than the run is rejected, naming the file");
        frontEndWrites = true;

        // A pull that changes package.json, or a missing node_modules, runs npm install first.
        await Commit("master", "Bump a package", (@"Meyer.Online.App.Ionic\package.json", "{ \"scripts\": { \"build-prod\": \"fixture 2\" } }"));
        calls.Clear();
        var bumped = await Release().Build(await Release().Preview(), _ => { });
        var install = calls.SingleOrDefault(call => call.Step == OnlineRelease.InstallStep);
        Check(bumped.Installed && bumped.InstallReason?.StartsWith("package.json changed since ", StringComparison.Ordinal) == true && install is not null &&
            install.Tool == "cmd.exe" && install.Directory == app && install.Arguments.SequenceEqual(["/c", "npm", "install"]) &&
            bumped.Steps.Select(item => item.Step).Contains(OnlineRelease.InstallStep), "a pull that changes package.json runs npm install in the app folder first");
        Directory.Delete(Path.Combine(app, "node_modules"), true);
        var bare = await Release().Preview();
        calls.Clear();
        var installed = await Release().Build(bare, _ => { });
        Check(!bare.NodeModules && bare.Warnings.Contains($"{OnlineRelease.AppFolder} has no node_modules; building runs npm install there first.") &&
            installed.Installed && installed.InstallReason == $"{OnlineRelease.AppFolder} had no node_modules" && calls.Count(call => call.Step == OnlineRelease.InstallStep) == 1,
            "a missing node_modules is reported in the preview and installed before the front-end build");

        // The publish profile is read, not assumed: a new folder and configuration are followed.
        await Commit("master", "Publish elsewhere", (@"Meyer.Online.Server\Properties\PublishProfiles\FolderProfile.pubxml", Profile("Staging", @"bin\Publish\custom")));
        publishUrl = @"bin\Publish\custom";
        calls.Clear();
        var moved = await Release().Build(await Release().Preview(), _ => { });
        Check(moved.Profile?.PublishFolder == Path.Combine(project, @"bin\Publish\custom") &&
            calls.Single(call => call.Step == OnlineRelease.PublishStep).Arguments.SequenceEqual(["build", Path.Combine(project, "Meyer.Online.Server.csproj"), "-c", "Staging", "-p:DeployOnBuild=true", "-p:PublishProfile=FolderProfile"]) &&
            File.Exists(Path.Combine(project, @"bin\Publish\custom\wwwroot\index.html")) && Entries().Contains("wwwroot/index.html") && Entries().Contains("Meyer.Online.Server.exe"),
            "a changed PublishUrl and configuration in FolderProfile.pubxml are followed: the publish uses them and the zip comes from the new folder");

        await CheckOnlineScreenRun(settings, git, Fake, Find, zip, () => publishStarted, value => holdPublish = value, value => publishStarted = value, project);
        // Later fixtures expect no release records yet.
        var records = Path.Combine(Files.Home, "Releases");
        foreach (var record in Directory.GetFiles(records, OnlineRelease.Name + ".*.result.json")) File.Delete(record);
        if (!Directory.EnumerateFileSystemEntries(records).Any()) Directory.Delete(records);
    }

    /// <summary>The screen end to end on the fixture: a full build, the local changes question, and Esc during a build.</summary>
    private static async Task CheckOnlineScreenRun(Settings settings, GitRepository git, OnlineRelease.Runner fake, Func<string, string?> find, string zip,
        Func<bool> publishStarted, Action<bool> hold, Action<bool> started, string project)
    {
        var offered = new List<List<string>>();
        var explored = new List<string>();
        var questions = new List<(string Question, bool Default)>();
        var pauses = 0;
        var screen = new OnlineReleaseScreen(settings, git, fake, find);
        async Task<string> Drive(Func<int, ConsoleKeyInfo?> keys)
        {
            using var console = new StringWriter();
            await screen.Show(LiveConsole(console), keys, (_, actions) =>
            {
                lock (offered) offered.Add(actions.Select(action => action.Label).ToList());
                return explored.Count == 0 ? ReleaseScreen.OpenFolder : ReleaseScreen.Back;
            }, (question, preset) =>
            {
                lock (questions) questions.Add((question, preset));
                return false;
            }, () => Interlocked.Increment(ref pauses), explored.Add);
            return console.ToString();
        }
        var before = Files.Hash(zip);
        var shown = Shown(await Drive(Script(Press(ConsoleKey.Enter, '\r'), () => offered.Count != 0)));
        Check(shown.Contains("MEYERONLINE RELEASE Check the checkout and the package before you build.") && shown.Contains("Enter build Esc back") &&
            shown.Contains("BUILDING MEYERONLINE RELEASE") && shown.Contains("Zip " + zip) && shown.Contains("Publish server running") &&
            shown.Contains("MEYERONLINE RELEASE READY") && Files.Hash(zip) != before && explored.SequenceEqual([Path.GetDirectoryName(zip)!]) &&
            offered.All(actions => actions.SequenceEqual([ReleaseScreen.OpenFolder, ReleaseScreen.Back])),
            "the screen previews, builds with live progress, shows the result, and opens the zip's folder through the injected launcher");

        File.WriteAllText(Path.Combine(project, "Meyer.Online.Server.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><!-- screen --></Project>");
        before = Files.Hash(zip);
        shown = Shown(await Drive(Script(Press(ConsoleKey.Enter, '\r'), () => questions.Count == 1, Press(ConsoleKey.Escape))));
        Check(questions.SequenceEqual([(OnlineReleaseScreen.LocalChangesQuestion, false)]) && shown.Contains("Enter build with your local changes (asks first)") &&
            Files.Hash(zip) == before, "with modified tracked files, Enter asks, defaulting to No, before building with them, and No builds nothing");
        await git.Git(settings.MeyerOnlineRoot, "checkout", "--", "Meyer.Online.Server/Meyer.Online.Server.csproj");

        hold(true);
        started(false);
        var paused = pauses;
        shown = Shown(await Drive(Script(Press(ConsoleKey.Enter, '\r'), publishStarted, Press(ConsoleKey.Escape), () => pauses > paused)));
        hold(false);
        Check(shown.Contains("Cancelled.") && shown.Contains(zip + " was not changed.") && Files.Hash(zip) == before && offered.Count == 2,
            "Esc during the build cancels it, kills the builds and leaves the zip as it was");
    }

    /// <summary>The settings default: the MeyerOnline clone beside the MyMeyer checkout, when it is a Git checkout.</summary>
    private static async Task CheckOnlineDiscovery(string home)
    {
        var clones = Path.Combine(home, "clones");
        var myMeyer = Path.Combine(clones, "MyMeyer");
        var online = Path.Combine(clones, OnlineRelease.Folder);
        Directory.CreateDirectory(myMeyer);
        Directory.CreateDirectory(online);
        Check(OnlineRelease.Discover(myMeyer) is null, "a MeyerOnline folder that is not a Git checkout is not taken as the MeyerOnline checkout");
        Directory.CreateDirectory(Path.Combine(online, ".git"));
        var settings = new Settings { RepositoryRoot = myMeyer, LibraryRepositories = [] };
        await settings.FillDefaults();
        var kept = new Settings { RepositoryRoot = myMeyer, MeyerOnlineRoot = @"D:\Elsewhere\MeyerOnline", LibraryRepositories = [] };
        await kept.FillDefaults();
        Check(OnlineRelease.Discover(myMeyer) == online && settings.MeyerOnlineRoot == online && kept.MeyerOnlineRoot == @"D:\Elsewhere\MeyerOnline",
            "settings find the MeyerOnline clone beside the MyMeyer checkout when none is set, and keep one that is");
        Check(new Settings { MeyerOnlineRoot = "MeyerOnline" }.Problems().Contains("MeyerOnlineRoot must be the absolute folder of your MeyerOnline checkout."),
            "a relative MeyerOnlineRoot is a settings problem");
        Check(Program.Help.Contains("online-release [--yes] [--allow-local-changes]") && Program.Help.Contains("[--meyeronline PATH]"),
            "help lists the online-release command and the setup option for its checkout");
    }

    /// <summary>The real runner on cmd.exe: output goes to the log, a failure carries the last lines and the log path, and cancelling kills the tree.</summary>
    private static async Task CheckOnlineRunner(string home)
    {
        var folder = Path.Combine(home, "runner");
        Directory.CreateDirectory(folder);
        var ok = new OnlineCommand("Fixture", "cmd.exe", ["/c", "echo first line& echo second line"], folder, Path.Combine(folder, "ok.log"));
        await OnlineRelease.Start(ok, default);
        var text = File.ReadAllText(ok.Log);
        Check(text.Contains("first line") && text.Contains("second line") && text.Contains("Exit code: 0"), "the build runner writes a step's whole output and exit code to its log");
        var bad = ok with { Arguments = ["/c", "echo early& echo the error line& exit 7"], Log = Path.Combine(folder, "bad.log") };
        Exception? failure = null;
        try { await OnlineRelease.Start(bad, default); }
        catch (InvalidOperationException exception) { failure = exception; }
        Check(failure is not null && failure.Message.StartsWith("Fixture failed (7). Last lines of its output:", StringComparison.Ordinal) &&
            failure.Message.Contains("the error line") && failure.Message.EndsWith("Full log: " + bad.Log, StringComparison.Ordinal) &&
            Diagnostics.Describe(failure).Context is { Log: var log, ExitCode: 7, Stage: "Fixture" } && log == bad.Log,
            "a failing step reports its exit code, its last output lines and its log");
        var slow = ok with { Arguments = ["/c", "ping -n 4 127.0.0.1 >nul & echo late> marker.txt"], Log = Path.Combine(folder, "slow.log") };
        using var cancel = new CancellationTokenSource(500);
        var clock = Stopwatch.StartNew();
        var cancelled = false;
        try { await OnlineRelease.Start(slow, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        var stopped = clock.Elapsed;
        await Task.Delay(4500);
        Check(cancelled && stopped < TimeSpan.FromSeconds(3) && !File.Exists(Path.Combine(folder, "marker.txt")) && File.ReadAllText(slow.Log).Contains("Cancelled after"),
            "cancelling a step kills its process tree at once, so nothing it started carries on");
    }

    /// <summary>The Build MeyerOnline release screen's place on the home screen, preview, progress and result, from fixture data alone.</summary>
    private static void CheckOnlineScreens()
    {
        var labels = Terminal.HomeActions.Select(action => action.Label).ToList();
        Check(labels.IndexOf(OnlineReleaseScreen.Action) == labels.IndexOf(ReleaseScreen.Action) + 1 &&
            Terminal.HomeActions.Single(action => action.Label == OnlineReleaseScreen.Action).Description.Contains("MORelease.zip"),
            "Build MeyerOnline release comes directly after Build release files on the home screen, with its own help");
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
        OnlineReleaseResult Fixture(bool modified = false, bool other = false) => new()
        {
            Repository = @"C:\fixture\MeyerOnline", CurrentBranch = "development",
            Master = new("abcdef1" + new string('1', 33), "2bc2fa7" + new string('2', 33), 2, 0, "master is 2 commit(s) behind origin; building pulls it."),
            ModifiedFiles = modified ? ["Meyer.Online.Server/Startup.cs"] : [], UntrackedFiles = ["notes.txt"],
            Profile = new(@"C:\fixture\MeyerOnline\Meyer.Online.Server\Properties\PublishProfiles\FolderProfile.pubxml", "Release", @"bin\Release\net5.0",
                @"C:\fixture\MeyerOnline\Meyer.Online.Server\bin\Release\net5.0", new() { ["LastUsedBuildConfiguration"] = "Release", ["SelfContained"] = "true" }),
            NodeModules = true, Dotnet = @"C:\Program Files\dotnet\dotnet.exe", Npm = null, Zip = @"C:\fixture\Releases\MORelease\MORelease.zip", ZipExists = true,
            Warnings = ["1 untracked file(s) that Git does not ignore are in C:\\fixture\\MeyerOnline: notes.txt."],
            Errors = [.. modified ? new[] { "C:\\fixture\\MeyerOnline has 1 modified tracked file(s): Meyer.Online.Server/Startup.cs." } : [], .. other ? new[] { "npm was not found on PATH." } : []]
        };
        var full = Flat(Capture(new OnlinePreview(Fixture()).Render(120, 200), 120, 200));
        Check(full.Contains("MEYERONLINE RELEASE") && full.Contains(@"C:\fixture\MeyerOnline on development | master at abcdef1 | origin/master at 2bc2fa7 master is 2 commit(s) behind origin; building pulls it.") &&
            full.Contains("Building switches this checkout from development to master and leaves it there.") &&
            full.Contains("dotnet build Meyer.Online.Server.csproj -c Release -p:DeployOnBuild=true -p:PublishProfile=FolderProfile") &&
            full.Contains(@"Publish folder C:\fixture\MeyerOnline\Meyer.Online.Server\bin\Release\net5.0") && full.Contains("LastUsedBuildConfiguration Release, SelfContained true") &&
            full.Contains("npm run build-prod in Meyer.Online.App.Ionic") && full.Contains("npm not found on PATH") &&
            full.Contains(@"PACKAGE C:\fixture\Releases\MORelease\MORelease.zip Replaces the MORelease.zip there once the new one is complete and checked.") &&
            full.Contains("Untracked files, kept as they are (1): notes.txt") && full.Contains("Enter build Esc back"),
            "the MeyerOnline preview shows the branch switch and pull, both builds with the profile's settings, the tools, and the zip it replaces");
        var local = new OnlinePreview(Fixture(modified: true));
        var localScreen = Flat(Capture(local.Render(120, 200), 120, 200));
        Check(local.HandleKey(Press(ConsoleKey.Enter, '\r'), 24) == OnlineChoice.Build && localScreen.Contains("Modified tracked files (1): Meyer.Online.Server/Startup.cs") &&
            localScreen.Contains("Modified tracked files stop the build. Enter asks whether to build with them anyway.") &&
            localScreen.Contains("Enter build with your local changes (asks first)"), "modified tracked files are listed, and Enter offers to build with them after asking");
        var stuck = new OnlinePreview(Fixture(modified: true, other: true));
        Check(stuck.HandleKey(Press(ConsoleKey.Enter, '\r'), 24) is null && stuck.HandleKey(Press(ConsoleKey.Escape), 24) == OnlineChoice.Back &&
            Flat(Capture(stuck.Render(120, 200), 120, 200)).Contains("CANNOT BUILD (2)") && Capture(stuck.Render(120, 200), 120, 200).Contains("This release cannot be built."),
            "a preview with any other error offers no build");

        var view = new OnlineBuildView(Fixture());
        view.Step(OnlineRelease.PullStep, true);
        view.Step(OnlineRelease.PullStep, false);
        view.Step(OnlineRelease.PublishStep, true);
        view.Step(OnlineRelease.FrontEndStep, true);
        view.Report("Running npm run build-prod in Meyer.Online.App.Ionic");
        var progress = Flat(Capture(view.Render(100, 30), 100, 30));
        Check(progress.Contains("BUILDING MEYERONLINE RELEASE") && progress.Contains("Pull master done in 0:00") && progress.Contains("Publish server running 0:00") &&
            progress.Contains("Build front end running 0:00") && progress.Contains("Copy wwwroot waiting") && !progress.Contains("npm install") &&
            progress.Contains("Running npm run build-prod") && progress.Contains("Esc cancel"),
            "the build screen shows each step with its state and time, both builds running at once, and the latest progress");

        var built = Fixture();
        (built.Built, built.Switched, built.SwitchedFrom, built.Pulled, built.Commit, built.Subject) = (true, true, "development", 2, "2bc2fa7" + new string('2', 33), "Server change");
        (built.Installed, built.InstallReason, built.Version, built.Size, built.Entries, built.Sha256, built.Seconds) =
            (true, "package.json changed since abcdef1", "11/20/2026, 9:00:00 AM", 5120, 10, "ABC123", 252);
        built.Steps.AddRange([new(OnlineRelease.PullStep, 3), new(OnlineRelease.PublishStep, 95), new(OnlineRelease.FrontEndStep, 240)]);
        var card = Flat(Capture(OnlineReleaseScreen.ResultCard(built), 120, 40));
        Check(card.Contains("MEYERONLINE RELEASE READY") && card.Contains("Wrote MORelease.zip from master at 2bc2fa7 in 4:12") && card.Contains("Server change") &&
            card.Contains($"MORelease.zip {TidySizes.Format(5120)}, 10 files, SHA-256 ABC123") &&
            card.Contains(@"Switched C:\fixture\MeyerOnline from development to master; it stays on master.") && card.Contains("Pulled 2 new commits into master.") &&
            card.Contains("Ran npm install first: package.json changed since abcdef1.") && card.Contains("version.json: 11/20/2026, 9:00:00 AM") &&
            card.Contains("Publish server 1:35") && card.Contains("Build front end 4:00"),
            "the result gives the zip's size, files and hash, the commit, the branch switch, the pull, npm install, version.json and each step's time");
        using var ansi = new StringWriter();
        LiveConsole(ansi).Write(OnlineReleaseScreen.ResultCard(built));
        Check(ansi.ToString().Contains("\u001b]8;") && ansi.ToString().Contains("file:///C:/fixture/Releases/MORelease"), "the result links the zip's folder as a file: address");
    }
}
