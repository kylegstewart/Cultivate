using System.Reflection;
using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>
    /// Release files for merged pull requests, from a fixture origin with refs/pull/N/head and the GitHub CLI faked. The developer's checkout is a
    /// clone whose bin holds stale DLLs; every folder is written under the fixture, never the user's Documents.
    /// </summary>
    private static async Task CheckReleaseFiles(string home, string msbuild)
    {
        CheckReleaseNames();
        var root = Path.Combine(home, "rel");
        var origin = Path.Combine(root, "origin.git");
        var author = Path.Combine(root, "author");
        var checkout = Path.Combine(root, "checkout");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        var settings = new Settings
        {
            RepositoryRoot = checkout, WebfarmRoot = Path.Combine(root, "farm"), StateRoot = Path.Combine(root, "state"), MSBuild = msbuild,
            MappingAssembly = Path.Combine(root, "no-mapping-assembly.dll"), ReleaseRoot = output, LibraryRepositories = []
        };
        var git = new GitRepository(settings);
        Task<string> A(params string[] args) => git.Git(author, args);
        var safety = new Dictionary<string, string> { ["WebfarmPath"] = Path.Combine(root, "farm") + "\\" };

        const string package = "CultivateReleasePackage";
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var source = Path.Combine(root, "pkg " + version);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Library.csproj"), $$"""
                <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <OutputType>Library</OutputType><TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                    <OutputPath>out\</OutputPath><AssemblyName>{{package}}</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Value.cs" /><Reference Include="System" /></ItemGroup>
                  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
                </Project>
                """);
            File.WriteAllText(Path.Combine(source, "Value.cs"),
                $"[assembly: System.Reflection.AssemblyVersion(\"{version}.0\")] public class ReleasePackageValue {{ public static int Value => {version[0]}; }}");
            await Commands.Run(msbuild, [Path.Combine(source, "Library.csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], source, safety);
        }
        static string NoWebfarm(string project)
        {
            var start = project.IndexOf("<Target Name=\"CopyBuildOutputToWebfarm\"", StringComparison.Ordinal);
            var end = project.IndexOf("</Target>", start, StringComparison.Ordinal) + "</Target>".Length;
            return project[..start] + project[end..];
        }
        string Shop(string version) => Project("Shop", "Core").Replace("<Reference Include=\"System\" />", $"""
            <Reference Include="System" />
            <Reference Include="{package}, Version={version}.0, Culture=neutral, processorArchitecture=MSIL">
              <HintPath>..\packages\{package}.{version}\lib\net471\{package}.dll</HintPath>
            </Reference>
            """);

        await git.Git(root, "init", "-q", "--bare", "-b", "master", origin);
        await git.Git(root, "clone", "-q", origin, author);
        await A("config", "user.email", "cultivate-test@localhost");
        await A("config", "user.name", "Cultivate isolated tests");
        async Task<string> Commit(string? from, string message, params (string Path, string? Content)[] files)
        {
            if (from is not null) await A("checkout", "-q", "--detach", from);
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(author, path);
                if (content is null) File.Delete(full);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, content);
                }
            }
            await A("add", "-A");
            await A("commit", "-qm", message);
            return await A("rev-parse", "HEAD");
        }
        Task Publish(string commit, string reference) => A("push", "-q", "-f", "origin", commit + ":" + reference);
        async Task<string> Squash(string onto, string head, string message)
        {
            await A("checkout", "-q", "--detach", onto);
            await A("merge", "-q", "--squash", head);
            await A("commit", "-qm", message);
            return await A("rev-parse", "HEAD");
        }

        var c0 = await Commit(null, "base",
            (".gitignore", "bin/\nobj/\npackages/\n"), ("MyMeyer.sln", ""), (@"Core\App.config", "<configuration />"),
            (@"Helpers\App.config", "<configuration />"), (@"Page\App.config", "<configuration />"), (@"Shop\App.config", "<configuration />"),
            (@"Core\Core.csproj", Project("Core")), (@"Core\Value.cs", "public class CoreValue { public static int Value => 1; }"),
            (@"Core\Backup1\Core.csproj", NoWebfarm(Project("Core"))),
            (@"Helpers\Helpers.csproj", NoWebfarm(Project("Helpers"))), (@"Helpers\Value.cs", "public class HelperValue { public static int Value => 3; }"),
            (@"Page\Page.csproj", Project("Page", "Helpers")
                .Replace("<Reference Include=\"System\" />", "<Reference Include=\"System\" /><Reference Include=\"Core\"><HintPath>..\\bin\\Core.dll</HintPath></Reference>")
                .Replace("<Compile Include=\"Value.cs\" />", "<Compile Include=\"Value.cs\" /><Compile Include=\"Other.cs\" />")),
            (@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.Value + HelperValue.Value; }"),
            (@"Page\Other.cs", "public class PageOther { }"),
            (@"Shop\Shop.csproj", Shop("1.0.0")), (@"Shop\Value.cs", "public class ShopValue { public static int Value => CoreValue.Value + ReleasePackageValue.Value; }"),
            (@"Page\View.ascx", "view 0"), (@"Page\Again.css", "again 0"), (@"Page\Reverted.js", "reverted 0"), (@"Page\Old.js", "old 0"),
            (@"Page\Default.aspx", "page default 0"), (@"Admin\Default.aspx", "admin default 0"), (@"Db\Proc.sql", "select 0"),
            (@"Page\Web.config", "<configuration />"), (@"Docs\Notes.md", "notes 0"));
        await Publish(c0, "refs/heads/master");

        // The developer's checkout last built Core and Helpers at the first commit; master's Core has gained NewApi since.
        await git.Git(root, "clone", "-q", origin, checkout);
        foreach (var name in new[] { "Core", "Helpers" })
            await Commands.Run(msbuild, [Path.Combine(checkout, name, name + ".csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], checkout, safety);
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var destination = Path.Combine(checkout, $@"packages\{package}.{version}\lib\net471\{package}.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(root, "pkg " + version, "out", package + ".dll"), destination);
        }
        var staleCore = Path.Combine(checkout, @"bin\Core.dll");
        var staleHash = Files.Hash(staleCore);
        // Building pulls master, which moves the checkout's master, origin/master, HEAD's commit and the files that follow it; nothing else may change.
        static string Lines(string text, string skip) => string.Join("\n", text.Split('\n').Where(line => !line.StartsWith(skip, StringComparison.Ordinal)));
        async Task<string> Untouched() => string.Join("\n", await git.Git(checkout, "symbolic-ref", "HEAD"), await git.Git(checkout, "status", "--porcelain", "--ignored"),
            Lines(await git.Git(checkout, "worktree", "list", "--porcelain"), "HEAD "),
            // origin/HEAD is listed by the branch it names, not by that branch's commit, which a pull moves.
            string.Join("\n", (await git.Git(checkout, "for-each-ref", "--format=%(refname) %(if)%(symref)%(then)-> %(symref)%(else)%(objectname)%(end)")).Split('\n')
                .Where(line => !line.StartsWith("refs/heads/master ", StringComparison.Ordinal) && !line.StartsWith("refs/remotes/origin/master ", StringComparison.Ordinal))),
            Files.Hash(staleCore));
        async Task<string> Refs(string repository) => string.Join("\n", await git.Git(repository, "rev-parse", "HEAD"),
            await git.Git(repository, "for-each-ref", "--format=%(refname) %(objectname)"));
        var before = await Untouched();
        var refs = await Refs(checkout);
        var logs = Path.Combine(Files.Home, "Logs");
        int BridgeLogs() => Directory.Exists(logs) ? Directory.GetFiles(logs, "bridge-*").Length : 0;
        var bridgeLogs = BridgeLogs();
        int Runs() => Directory.Exists(Path.Combine(Files.Home, "Runs")) ? Directory.GetDirectories(Path.Combine(Files.Home, "Runs")).Length : 0;

        var pulls = new Dictionary<int, string>();
        void Pull(int number, string title, string branch, string merge, string head, string state = "MERGED", string into = "master") =>
            pulls[number] = JsonSerializer.Serialize(new
            {
                number, title, author = new { login = "alice" }, headRefName = branch, baseRefName = into, state,
                mergedAt = state == "MERGED" ? "2026-11-19T10:00:00Z" : null, mergeCommit = merge.Length == 0 ? null : new { oid = merge },
                headRefOid = head, url = $"https://github.com/fixture/MyMeyer/pull/{number}"
            });
        var asked = new List<string>();
        Task<string> Gh(string[] arguments)
        {
            asked.Add(string.Join(' ', arguments));
            return arguments is ["pr", "view", var number, ..] && pulls.TryGetValue(int.Parse(number), out var json) ? Task.FromResult(json)
                : Task.FromException<string>(new InvalidOperationException("gh.exe failed (1).\nGraphQL: Could not resolve to a PullRequest with the number of 404."));
        }
        var release = new ReleaseFiles(settings, git, Gh);
        List<string> Listing(string folder) => [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Order(Files.Comparer)];

        // PR 20: one project's code and one content file, squash-merged.
        var c1 = await Commit(c0, "Core gains NewApi", (@"Core\Value.cs", "public class CoreValue { public static int Value => 1; public static int NewApi => 20; }"));
        var h20 = await Commit(c1, "use the new core value",
            (@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.NewApi + HelperValue.Value; }"), (@"Page\View.ascx", "view 20"));
        await Publish(h20, "refs/pull/20/head");
        var m20 = await Squash(c1, h20, "Use the new core value (#20)");
        await Publish(m20, "refs/heads/master");
        Pull(20, "Use the new core value", "38636-2", m20, h20);
        var runs = Runs();
        var preview = await release.Preview(20);
        var folder20 = Path.Combine(output, "38636");
        Check(asked.Single() == "pr view 20 --json number,title,author,headRefName,baseRefName,state,mergedAt,mergeCommit,headRefOid,url",
            "a release asks the GitHub CLI for the pull request's merge commit, head, and base");
        Check(preview is { Written: false, CanBuild: true, Name: "38636", BaseCommit: var tip, ForkPoint: var fork } && tip == m20 && fork == c1 &&
            preview.Folder == folder20 && preview.Projects.Single().Assembly == "Page" && preview.Content.SequenceEqual(["Page/View.ascx"]) &&
            preview.Files.Count == 0 && preview.Changes.All(change => change.Status == ReleaseStatus.Same),
            "a squash-merged pull request previews its changed project and content file from master, in a folder named after the branch's ticket");
        Check(preview.SupportProjects.Select(project => project.Assembly).Order().SequenceEqual(["Core", "Helpers"]) &&
            preview.SupportProjects.Single(project => project.Assembly == "Core").Reason.Contains("build output Git does not track") &&
            preview.SupportProjects.Single(project => project.Assembly == "Helpers") is { HasWebfarmTarget: false, Reason: var reason } && reason.Contains("ProjectReference"),
            "the projects Page compiles against are support projects: a HintPath to an untracked build output, and a ProjectReference without the webfarm target");
        Check(!Directory.Exists(folder20) && !Directory.Exists(Path.Combine(Files.Home, "Releases")) && Runs() == runs,
            "the preview builds and writes nothing");
        Check(await Untouched() == before && await Refs(checkout) == refs && preview.Pull is { Outcome: ReleasePullOutcome.Behind, Commits: 2 },
            "the preview leaves the checkout, its refs and remote-tracking branches as they were, deletes its private refs, and says master is behind");

        var progress = new List<string>();
        var built = await release.Build(preview, progress.Add);
        Check(Listing(folder20).SequenceEqual(["Page.dll", "View.ascx"], Files.Comparer) && File.ReadAllText(Path.Combine(folder20, "View.ascx")) == "view 20" &&
            AssemblyName.GetAssemblyName(Path.Combine(folder20, "Page.dll")).Name == "Page",
            "the release folder holds exactly the changed project's DLL and the content file as master has it");
        Check(built is { Written: true } && built.Files.Select(file => (file.Name, file.Kind)).SequenceEqual([("Page.dll", ReleaseKind.ProjectDll), ("View.ascx", ReleaseKind.Content)]) &&
            built.Files.All(file => file.Sha256 == Files.Hash(Path.Combine(folder20, file.Name)) && file.Size > 0) && built.Files[0].AssemblyVersion is not null,
            "the result lists each written file with its kind, hash, size and assembly version");
        Check(progress.Where(line => line.StartsWith("Building ", StringComparison.Ordinal)).Order().SequenceEqual([
                "Building Core (2/3, dependency: needed to compile, not released)", "Building Helpers (1/3, dependency: needed to compile, not released)",
                "Building Page (3/3, release file)"]) &&
            progress.Contains("Preparing references for the release projects and the unchanged projects they compile against") &&
            progress.Contains("Verifying the release projects' DLLs and changed packages; dependency builds are not released"),
            "support projects are built from master's snapshot, each once, before the project that needs them, and the progress says which builds are released");
        Check(built.OutsideReferences.Count == 0 && File.Exists(staleCore) && Files.Hash(staleCore) == staleHash && !Listing(folder20).Contains("Core.dll"),
            "the release compiles against master's Core, not the checkout's stale DLL that lacks NewApi, and does not release it");
        Check(!Directory.Exists(Path.Combine(built.RunDirectory, "source")) && File.Exists(Path.Combine(built.RunDirectory, "release.json")) &&
            built.RunDirectory.StartsWith(Path.Combine(Files.Home, "Runs"), StringComparison.OrdinalIgnoreCase) &&
            Directory.GetFiles(Path.Combine(Files.Home, "Releases"), "*.result.json").Length == 1, "the snapshot is removed and the result is saved with the run and the folder's record");
        Check(built.Pull is { Outcome: ReleasePullOutcome.Pulled, Before: var pulledFrom, After: var pulledTo } && pulledFrom == c0 && pulledTo == m20 &&
            await git.Git(checkout, "rev-parse", "HEAD", "refs/remotes/origin/master") == m20 + "\n" + m20 &&
            File.ReadAllText(Path.Combine(checkout, @"Page\Value.cs")).Contains("CoreValue.NewApi"),
            "building fast-forwards the checked-out master and origin/master to the commit it built from, and the working files follow");
        Check(await Untouched() == before, "a full release run changes nothing in the checkout but pulling master: its branch, index, bin, other refs and worktrees stay");

        var reuse = await release.Preview(20, new(Folder: Path.Combine(output, "reuse"), ReuseDependencies: true));
        string? stale = null;
        try { await release.Build(reuse, _ => { }); }
        catch (InvalidOperationException exception) { stale = exception.Message; }
        Check(reuse.SupportProjects.All(project => !project.Built) && stale?.Contains("NewApi") == true && !Directory.Exists(Path.Combine(output, "reuse")),
            "with --reuse-dependencies the checkout's stale Core is used, so the build fails to compile and nothing is written");
        Check(await Untouched() == before, "a failed release removes its snapshot and private refs");

        // PR 21: merged three ways for the same change set, then master moves on.
        var p1 = await Commit(m20, "pr21 code and styles", (@"Page\Value.cs", "public class PageValue { public static int Value => CoreValue.NewApi + 21; }"),
            (@"Page\Again.css", "again 21"), (@"Page\Reverted.js", "reverted 21"), (@"Page\Old.js", null));
        var h21 = await Commit(p1, "pr21 markup and scripts", (@"Page\Handler.ashx", "handler 21"), (@"Page\Default.aspx", "page default 21"),
            (@"Admin\Default.aspx", "admin default 21"), (@"Db\Proc.sql", "select 21"), (@"Page\Web.config", "<configuration><appSettings /></configuration>"),
            (@"Docs\Notes.md", "notes 21"));
        await Publish(h21, "refs/pull/21/head");
        var m1 = await Commit(m20, "Other merged change", (@"Page\Other.cs", "public class PageOther { public static int Seen => 1; }"));
        var squashed = await Squash(m1, h21, "27220 Stop Pay Removed (#21)");
        await A("checkout", "-q", "--detach", m1);
        await A("merge", "-q", "--no-ff", "-m", "Merge pull request #21", h21);
        var mergeCommit = await A("rev-parse", "HEAD");
        await A("checkout", "-q", "--detach", h21);
        await A("rebase", "-q", "--onto", m1, m20);
        var rebased = await A("rev-parse", "HEAD");
        var shapes = new List<string>();
        foreach (var merge in new[] { squashed, mergeCommit, rebased })
        {
            await Publish(merge, "refs/heads/master");
            Pull(21, "27220 Stop Pay Removed", "feature/stop-pay", merge, h21);
            var shape = await release.Preview(21);
            shapes.Add(string.Join("|", shape.Changes.Select(change => $"{change.Path}:{change.Change}:{change.Status}:{change.Kind}")) + "#" +
                string.Join(",", shape.Projects.Single().OtherMergedChanges.Select(commit => commit.Subject)));
        }
        Check(shapes.Distinct().Count() == 1 && shapes[0].Contains("Page/Old.js:Deleted:Deleted") && shapes[0].Contains("Page/Value.cs:Modified:" + ReleaseStatus.Same) &&
            shapes[0].EndsWith("#Other merged change", StringComparison.Ordinal) && shapes[0].Split('|').Length == 10,
            "squash, merge-commit and rebase merges give the same change set, and a rebase's copies of the pull request's commits are not other changes");

        var a1 = await Commit(squashed, "a1 again", (@"Page\Again.css", "again later"));
        var a2 = await Commit(a1, "a2 revert", (@"Page\Reverted.js", "reverted 0"));
        var a3 = await Commit(a2, "a3 shop", (@"Shop\Value.cs", "public class ShopValue { public static int Value => CoreValue.Value + ReleasePackageValue.Value + 3; }"));
        var a4 = await Commit(a3, "a4 page", (@"Page\Other.cs", "public class PageOther { public static int Seen => 2; }"));
        await Publish(a4, "refs/heads/master");
        Pull(21, "27220 Stop Pay Removed", "feature/stop-pay", squashed, h21);
        var folder21 = Path.Combine(output, "27220");
        Directory.CreateDirectory(folder21);
        File.WriteAllText(Path.Combine(folder21, "notes.txt"), "mine");
        progress.Clear();
        var r21 = await release.Build(await release.Preview(21), progress.Add);
        ReleaseChange Change(string path) => r21.Changes.Single(change => change.Path == path);
        Check(r21.Name == "27220" && Change("Page/Again.css") is { Status: ReleaseStatus.ChangedAgain, Commits: [{ Subject: "a1 again" }] } &&
            File.ReadAllText(Path.Combine(folder21, "Again.css")) == "again later" && r21.Warnings.Contains(
                $"Page/Again.css is not exactly as it was on the pull request: other merged changes touched it too ({a1[..7]}); master's version is used."),
            "a file changed again on master after the merge is reported with the later commit, and master's content is copied");
        Check(r21.Projects.Single().OtherMergedChanges.Select(commit => commit.Subject).Order().SequenceEqual(["a4 page", "Other merged change"]) &&
            !progress.Any(line => line.Contains("Shop")) && !r21.SupportProjects.Any(project => project.Assembly == "Shop") && !File.Exists(Path.Combine(folder21, "Shop.dll")),
            "other merged changes to the release project are listed, and a later change in an unrelated project is neither built nor copied");
        Check(Change("Page/Reverted.js").Status == ReleaseStatus.NotOnBase && !File.Exists(Path.Combine(folder21, "Reverted.js")) &&
            r21.Warnings.Any(warning => warning.StartsWith("Page/Reverted.js", StringComparison.Ordinal) && warning.Contains("left out")),
            "a pull request change undone on master is left out with a warning");
        Check(Change("Page/Old.js").Status == ReleaseStatus.Deleted && r21.Deleted.Single() is { Path: "Page/Old.js", Reason: var deleted } &&
            deleted.Contains("by hand") && !File.Exists(Path.Combine(folder21, "Old.js")), "a file the pull request deleted is reported, not copied");
        Check(File.ReadAllText(Path.Combine(folder21, "Handler.ashx")) == "handler 21" &&
            r21.NotIncluded.Select(note => note.Path).SequenceEqual(["Db/Proc.sql", "Docs/Notes.md", "Page/Web.config"]) &&
            r21.NotIncluded.All(note => note.Reason.Length > 10) && r21.Warnings.Any(warning => warning.Contains("Page/Web.config changed")),
            "an .ashx handler is copied; SQL, config and Markdown changes are listed as not included with reasons, and web.config gets its own warning");
        Check(Listing(folder21).SequenceEqual(["Again.css", "Default.aspx", "Handler.ashx", "notes.txt", "Page.dll", @"Page\Default.aspx"], Files.Comparer) &&
            File.ReadAllText(Path.Combine(folder21, "Default.aspx")) == "admin default 21" && File.ReadAllText(Path.Combine(folder21, @"Page\Default.aspx")) == "page default 21" &&
            r21.Warnings.Any(warning => warning.Contains("More than one release file is named Default.aspx")),
            "two files with the same name keep the first at the top level and the other in a folder named after its source folder, with a warning");
        Check(r21.Existing.Single() is { Name: "notes.txt", Origin: "Not written by Cultivate" } && File.ReadAllText(Path.Combine(folder21, "notes.txt")) == "mine",
            "a file already in the folder is kept and reported as not written by Cultivate");

        // PR 22 writes into the same folder: overwrites, then --clean.
        var h22 = await Commit(a4, "pr22", (@"Page\Again.css", "again 22"), (@"Page\New.js", "new 22"));
        await Publish(h22, "refs/pull/22/head");
        var m22 = await Squash(a4, h22, "39050 2 HD - Styles (#22)");
        await Publish(m22, "refs/heads/master");
        Pull(22, "39050 2 HD - Styles", "styles", m22, h22);
        Check((await release.Preview(22)).Name == "39050", "the ticket comes from the title when the branch has none");
        File.WriteAllText(Path.Combine(folder21, "Handler.ashx"), "edited by hand");
        var r22 = await release.Build(await release.Preview(22, new(Name: "27220")), _ => { });
        string Origin(ReleaseResult result, string name) => result.Existing.Single(file => Files.Comparer.Equals(file.Name, name)).Origin;
        Check(r22.Folder == folder21 && File.ReadAllText(Path.Combine(folder21, "Again.css")) == "again 22" &&
            r22.Files.Single(file => file.Name == "Again.css").Replaced && !r22.Files.Single(file => file.Name == "New.js").Replaced &&
            Origin(r22, "Page.dll").StartsWith("Written by Cultivate for PR #21", StringComparison.Ordinal) && Origin(r22, "Handler.ashx").EndsWith("changed since", StringComparison.Ordinal) &&
            Origin(r22, "notes.txt") == "Not written by Cultivate", "a second run overwrites same-named files and reports what earlier runs wrote");
        var cleanPreview = await release.Preview(22, new(Name: "27220", Clean: true));
        Check(cleanPreview.Removed.Order(Files.Comparer).SequenceEqual(["Again.css", "Default.aspx", "New.js", "Page.dll", @"Page\Default.aspx"], Files.Comparer) &&
            File.Exists(Path.Combine(folder21, "Page.dll")), "a --clean preview lists what it would remove and removes nothing");
        var cleaned = await release.Build(cleanPreview, _ => { });
        Check(cleaned.Removed.Count == 5 && Listing(folder21).SequenceEqual(["Again.css", "Handler.ashx", "New.js", "notes.txt"], Files.Comparer) &&
            File.ReadAllText(Path.Combine(folder21, "Handler.ashx")) == "edited by hand" && Origin(cleaned, "Handler.ashx").EndsWith("changed since", StringComparison.Ordinal),
            "--clean removes only the unchanged files Cultivate wrote and keeps files edited since or never written by Cultivate");

        // PR 23 bumps a package in Shop, which has a ProjectReference to Core.
        var h23 = await Commit(m22, "pr23", (@"Shop\Shop.csproj", Shop("2.0.0")));
        await Publish(h23, "refs/pull/23/head");
        var m23 = await Squash(m22, h23, "Bump the package (#23)");
        await Publish(m23, "refs/heads/master");
        Pull(23, "Bump the package", "bump", m23, h23);
        var r23 = await release.Build(await release.Preview(23, new(Folder: Path.Combine(output, "package"))), _ => { });
        var released = Path.Combine(output, "package", package + ".dll");
        Check(Listing(Path.Combine(output, "package")).SequenceEqual([package + ".dll", "Shop.dll"], Files.Comparer) &&
            AssemblyName.GetAssemblyName(released).Version?.ToString() == "2.0.0.0" && r23.Packages.Single() is { Version: "2.0.0.0", Project: @"Shop\Shop.csproj" } &&
            r23.Files.Single(file => file.Name == package + ".dll") is { Kind: ReleaseKind.Package, AssemblyVersion: "2.0.0.0" } &&
            r23.SupportProjects.Single().Assembly == "Core", "a package bump in the pull request's project file releases the bumped package's DLL with the project");

        // Refusals leave nothing behind.
        await Publish(h21, "refs/pull/30/head");
        await Publish(h21, "refs/pull/31/head");
        await Publish(h21, "refs/pull/32/head");
        Pull(30, "30001 Still open", "30001", "", h21, state: "OPEN");
        Pull(31, "31001 Other base", "31001", h21, h21, into: "release");
        Pull(32, "32001 Lost merge", "32001", h21, h21);
        async Task<string> Refused(ReleaseFiles files, int number)
        {
            try { await files.Preview(number); }
            catch (InvalidOperationException exception) { return exception.Message; }
            return "";
        }
        Task<string> Missing(string[] _) => throw new System.ComponentModel.Win32Exception("gh.exe was not found");
        var refusals = new[]
        {
            await Refused(release, 30), await Refused(release, 31), await Refused(new ReleaseFiles(settings, git, Missing), 20), await Refused(release, 404),
            await Refused(release, 32)
        };
        Check(refusals[0].Contains("not merged yet") && refusals[0].Contains("activate --pr 30") && refusals[1].Contains("merged into release, not master") &&
            refusals[2].Contains("GitHub CLI (gh) is not installed") && refusals[3].Contains("#404 was not found") && refusals[4].Contains("not part of origin's master"),
            "an unmerged pull request, one merged elsewhere, a missing GitHub CLI, an unknown number, and a merge commit not on master are refused with what to do");
        Check(await Untouched() == before && !new[] { "30001", "31001", "32001", "PR-404" }.Any(name => Directory.Exists(Path.Combine(output, name))),
            "refusals leave no refs, worktrees or folders behind");
        Check(BridgeLogs() == bridgeLogs && !File.Exists(settings.MappingAssembly), "release files never look up Code Release Manager mappings");

        // The screen run comes first: it checks the checkout is untouched, and the fixtures below add DLLs to the checkout's bin.
        await CheckReleaseScreenRun(new(settings, git, output, m23, Gh, (number, title, branch, merge, head) => Pull(number, title, branch, merge, head),
            (from, message, files) => Commit(from, message, files), Publish, Squash, Untouched, before));

        // PR 24: Tools and Kit reference Third and Fourth by HintPaths that resolve nowhere. Master tracks bin\Third.dll 2.0.0, and the
        // checkout's bin holds Third.dll 1.0.0 and the only Fourth.dll.
        async Task<string> ThirdParty(string name, string version)
        {
            var source = Path.Combine(root, name + " " + version);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, name + ".csproj"), $$"""
                <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <OutputType>Library</OutputType><TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                    <OutputPath>out\</OutputPath><AssemblyName>{{name}}</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Value.cs" /><Reference Include="System" /></ItemGroup>
                  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
                </Project>
                """);
            File.WriteAllText(Path.Combine(source, "Value.cs"),
                $"[assembly: System.Reflection.AssemblyVersion(\"{version}.0\")] public class {name}Value {{ public static int Value => {version[0]}; }}");
            await Commands.Run(msbuild, [Path.Combine(source, name + ".csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], source, safety);
            return Path.Combine(source, "out", name + ".dll");
        }
        var (third1, third2, fourth) = (await ThirdParty("Third", "1.0.0"), await ThirdParty("Third", "2.0.0"), await ThirdParty("Fourth", "1.0.0"));
        string Tool(string name) => Project(name).Replace("<Reference Include=\"System\" />", """
            <Reference Include="System" />
            <Reference Include="Third"><HintPath>..\lib\Third.dll</HintPath></Reference>
            <Reference Include="Fourth"><HintPath>..\lib\Fourth.dll</HintPath></Reference>
            """);
        await A("checkout", "-q", "--detach", m23);
        Directory.CreateDirectory(Path.Combine(author, "bin"));
        File.Copy(third2, Path.Combine(author, @"bin\Third.dll"), true);
        await A("add", "-f", "bin/Third.dll");
        var t0 = await Commit(null, "tools",
            (@"Tools\Tools.csproj", Tool("Tools")), (@"Tools\App.config", "<configuration />"), (@"Tools\Value.cs", "public class ToolsValue { public static int Value => 1; }"),
            (@"Kit\Kit.csproj", Tool("Kit")), (@"Kit\App.config", "<configuration />"), (@"Kit\Value.cs", "public class KitValue { public static int Value => 1; }"),
            (@"Page\Lines.js", "one\ntwo\nthree\nfour\nfive\nsix\nseven\n"));
        var h24 = await Commit(t0, "pr24",
            (@"Tools\Value.cs", "public class ToolsValue { public static int Value => ThirdValue.Value + FourthValue.Value; }"),
            (@"Kit\Value.cs", "public class KitValue { public static int Value => ThirdValue.Value + FourthValue.Value; }"));
        await Publish(h24, "refs/pull/24/head");
        var m24 = await Squash(t0, h24, "Use the third-party values (#24)");
        await Publish(m24, "refs/heads/master");
        Pull(24, "Use the third-party values", "tools", m24, h24);
        File.Copy(third1, Path.Combine(checkout, @"bin\Third.dll"));
        File.Copy(fourth, Path.Combine(checkout, @"bin\Fourth.dll"));
        progress.Clear();
        // Pulling master would replace the checkout's untracked bin\Third.dll with the one master now tracks; this run needs the checkout's own.
        var r24 = await release.Build(await release.Preview(24, new(Folder: Path.Combine(output, "tools"), NoPull: true)), progress.Add);
        var releaseLog = File.ReadAllText(Path.Combine(r24.RunDirectory, "build.log"));
        Check(Listing(Path.Combine(output, "tools")).SequenceEqual(["Kit.dll", "Tools.dll"], Files.Comparer) &&
            References(Path.Combine(output, "tools", "Tools.dll"))["Third"] == "2.0.0.0" && References(Path.Combine(output, "tools", "Kit.dll"))["Third"] == "2.0.0.0" &&
            new[] { "Kit", "Tools" }.All(name => Files.Hash(Path.Combine(r24.RunDirectory, "references", "projects", name, "Third.dll")) == Files.Hash(third2)) &&
            releaseLog.Contains($"Reference Third.dll: {Path.Combine(r24.RunDirectory, "source", "bin", "Third.dll")}\n") &&
            !r24.OutsideReferences.Any(reference => reference.Name == "Third.dll"),
            "a release compiles against the snapshot's tracked bin copy of a reference before the checkout's, and does not report it as an outside reference");
        Check(r24.OutsideReferences.Single() is { Name: "Fourth.dll", Path: var fourthPath, Project: var users } &&
            Files.Comparer.Equals(fourthPath, Path.Combine(checkout, @"bin\Fourth.dll")) &&
            users.Split(", ").Order(Files.Comparer).SequenceEqual([@"Kit\Kit.csproj", @"Tools\Tools.csproj"], Files.Comparer),
            "a reference only the checkout has is still used, and reported once with every project that used it");
        Check(progress.Contains("Building Kit (1/2, release file)") && progress.Contains("Building Tools (2/2, release file)"),
            "release projects are reported as release files");

        // The same project built for the dev farm keeps its lookups: the checkout's bin copy, even though the snapshot tracks another.
        var devSnapshot = Path.Combine(root, "dev", "source");
        var devRun = Path.Combine(root, "dev", "run");
        await git.Git(root, "clone", "-q", origin, devSnapshot);
        Directory.CreateDirectory(devRun);
        var devProgress = new List<string>();
        var devPlan = new Plan
        {
            Target = new("dev farm fixture", checkout, "refs/heads/master"), Commit = "", Fingerprint = "", Projects = [ProjectInfo.Read(devSnapshot, @"Tools\Tools.csproj")]
        };
        await new Pipeline(settings, git, new Bridge(settings)).Build(devPlan, devSnapshot, devRun, devProgress.Add);
        Check(Files.Hash(Path.Combine(devRun, @"references\projects\Tools\Third.dll")) == Files.Hash(third1) &&
            File.ReadAllText(Path.Combine(devRun, "build.log")).Contains($"Reference Third.dll: {Path.Combine(checkout, "bin", "Third.dll")}\n") &&
            devProgress.SequenceEqual(["Preparing references for changed projects only", "Building Tools (1/1)",
                "Verifying only the changed projects' own DLLs and changed packages"]),
            "a dev farm build of the same project still takes the checkout's bin copy, with the dev farm progress wording");

        // PR 25 changes Lines.js; another pull request changed another line of it and merged first, cleanly.
        var h25 = await Commit(m24, "pr25 lines", (@"Page\Lines.js", "ONE\ntwo\nthree\nfour\nfive\nsix\nseven\n"));
        await Publish(h25, "refs/pull/25/head");
        var o25 = await Commit(m24, "other lines", (@"Page\Lines.js", "one\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n"));
        var m25 = await Squash(o25, h25, "Lines (#25)");
        await Publish(m25, "refs/heads/master");
        Pull(25, "Lines", "lines", m25, h25);
        var p25 = await release.Preview(25, new(Folder: Path.Combine(output, "lines")));
        Check(p25.Changes.Single() is { Path: "Page/Lines.js", Status: ReleaseStatus.ChangedAgain, Commits: [{ Subject: "other lines", Commit: var other }] } && other == o25 &&
            p25.Content.SequenceEqual(["Page/Lines.js"]) && p25.Warnings.Contains(
                $"Page/Lines.js is not exactly as it was on the pull request: other merged changes touched it too ({o25[..7]}); master's version is used."),
            "a file another pull request also changed before the merge is reported with that commit");

        // PR 26's merge commit itself holds content the pull request never had.
        var h26 = await Commit(m25, "pr26 lines", (@"Page\Lines.js", "pr26\n"));
        await Publish(h26, "refs/pull/26/head");
        var m26 = await Commit(m25, "Lines resolved (#26)", (@"Page\Lines.js", "resolved\n"));
        await Publish(m26, "refs/heads/master");
        Pull(26, "Lines resolved", "resolved", m26, h26);
        var p26 = await release.Preview(26, new(Folder: Path.Combine(output, "resolved")));
        Check(p26.Changes.Single() is { Status: ReleaseStatus.ChangedAgain, Commits: [] } && p26.Warnings.Contains(
                "Page/Lines.js is not exactly as it was on the pull request: the merge itself changed it (for example, resolving a conflict); master's version is used."),
            "a file only the merge changed says so instead of naming no commits");

        // PR 27, the release that stopped with "Incompatible input DLLs": Alpha references Lib 2.0.0 by a package HintPath, and Beta by
        // ..\bin\Lib.dll, the copy Alpha's build leaves there. Gamma's HintPath names a sibling repository's build outside the snapshot, where the
        // developer has a stale Lib 1.0.0.
        var (lib1, lib2) = (await ThirdParty("Lib", "1.0.0"), await ThirdParty("Lib", "2.0.0"));
        var libPackage = Path.Combine(checkout, @"packages\Lib.2.0.0\lib\net471\Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(libPackage)!);
        File.Copy(lib2, libPackage);
        Directory.CreateDirectory(Path.Combine(root, "LibSibling"));
        File.Copy(lib1, Path.Combine(root, "LibSibling", "Lib.dll"));
        string Uses(string name, string references) => Project(name).Replace("<Reference Include=\"System\" />", "<Reference Include=\"System\" />" + references);
        const string loose = "<SpecificVersion>False</SpecificVersion>";
        const string libName = "Lib, Version={0}, Culture=neutral, processorArchitecture=MSIL";
        var l0 = await Commit(m26, "lib projects",
            (@"Alpha\Alpha.csproj", Uses("Alpha", $"""<Reference Include="{string.Format(libName, "2.0.0.0")}"><HintPath>..\packages\Lib.2.0.0\lib\net471\Lib.dll</HintPath></Reference>""")),
            (@"Alpha\App.config", "<configuration />"), (@"Alpha\Value.cs", "public class AlphaValue { public static int Value => LibValue.Value; }"),
            (@"Beta\Beta.csproj", Uses("Beta", $"""
                <Reference Include="Alpha"><HintPath>..\bin\Alpha.dll</HintPath></Reference>
                <Reference Include="{string.Format(libName, "1.5.0.0")}">{loose}<HintPath>..\bin\Lib.dll</HintPath></Reference>
                """)),
            (@"Beta\App.config", "<configuration />"), (@"Beta\Value.cs", "public class BetaValue { public static int Value => AlphaValue.Value + LibValue.Value; }"),
            (@"Gamma\Gamma.csproj", Uses("Gamma", $"""
                <Reference Include="Alpha"><HintPath>..\bin\Alpha.dll</HintPath></Reference>
                <Reference Include="Beta"><HintPath>..\bin\Beta.dll</HintPath></Reference>
                <Reference Include="{string.Format(libName, "1.0.0.0")}">{loose}<HintPath>..\..\LibSibling\Lib.dll</HintPath></Reference>
                """)),
            (@"Gamma\App.config", "<configuration />"), (@"Gamma\Value.cs", "public class GammaValue { public static int Value => 0; }"));
        var h27 = await Commit(l0, "pr27", (@"Gamma\Value.cs", "public class GammaValue { public static int Value => AlphaValue.Value + BetaValue.Value + LibValue.Value; }"));
        await Publish(h27, "refs/pull/27/head");
        var m27 = await Squash(l0, h27, "Gamma uses Lib (#27)");
        await Publish(m27, "refs/heads/master");
        Pull(27, "Gamma uses Lib", "gamma", m27, h27);
        var r27 = await release.Build(await release.Preview(27, new(Folder: Path.Combine(output, "lib"))), _ => { });
        var libLog = File.ReadAllText(Path.Combine(r27.RunDirectory, "build.log"));
        Check(r27.SupportProjects.Select(project => project.Assembly).Order().SequenceEqual(["Alpha", "Beta"]) &&
            References(Path.Combine(output, "lib", "Gamma.dll"))["Lib"] == "2.0.0.0" && !r27.OutsideReferences.Any(reference => reference.Name == "Lib.dll") &&
            libLog.Contains($"Reference Lib.dll: {Path.Combine(r27.RunDirectory, "source", "bin", "Lib.dll")}\n") &&
            libLog.Contains($"Reference Lib.dll: {Path.Combine(r27.RunDirectory, "source", "packages", "Lib.2.0.0", "lib", "net471", "Lib.dll")} (Alpha's copy: " +
                @"Gamma's HintPath ..\..\LibSibling\Lib.dll is not in the snapshot, and Gamma depends on Alpha, which resolved this copy from the snapshot"),
            "a release project whose HintPath is outside the snapshot compiles against the copy its dependency resolved from the snapshot, " +
            "not the developer's stale sibling build, does not report it as an outside reference, and build.log says whose copy it is");

        // PR 28: DupA and DupB reference different tracked copies of Dup, DupC a package of Dup by a Reference that is not version-specific,
        // and DupD a path that resolves nowhere. One shared reference folder refused this build, or let DupC compile against DupB's copy.
        var (dup1, dup2) = (await ThirdParty("Dup", "1.0.0"), await ThirdParty("Dup", "2.0.0"));
        var dupPackage = Path.Combine(checkout, @"packages\Dup.1.0.0\lib\net471\Dup.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dupPackage)!);
        File.Copy(dup1, dupPackage);
        await A("checkout", "-q", "--detach", m27);
        foreach (var (folder, dll) in new[] { ("one", dup1), ("two", dup2) })
        {
            Directory.CreateDirectory(Path.Combine(author, "lib", folder));
            File.Copy(dll, Path.Combine(author, "lib", folder, "Dup.dll"), true);
        }
        var dups = new[] { ("DupA", @"..\lib\one\Dup.dll"), ("DupB", @"..\lib\two\Dup.dll"), ("DupC", @"..\packages\Dup.1.0.0\lib\net471\Dup.dll"), ("DupD", @"..\nowhere\Dup.dll") };
        var d0 = await Commit(null, "dup projects", dups.SelectMany(dup => new (string, string?)[]
        {
            ($@"{dup.Item1}\{dup.Item1}.csproj", Uses(dup.Item1, $"""<Reference Include="Dup">{loose}<HintPath>{dup.Item2}</HintPath></Reference>""")),
            ($@"{dup.Item1}\App.config", "<configuration />"), ($@"{dup.Item1}\Value.cs", $"public class {dup.Item1}Value {{ public static int Value => 0; }}")
        }).ToArray());
        var h28 = await Commit(d0, "pr28", dups.Select(dup => ($@"{dup.Item1}\Value.cs", (string?)$"public class {dup.Item1}Value {{ public static int Value => DupValue.Value; }}")).ToArray());
        await Publish(h28, "refs/pull/28/head");
        var m28 = await Squash(d0, h28, "Dup values (#28)");
        await Publish(m28, "refs/heads/master");
        Pull(28, "Dup values", "dup", m28, h28);
        string[] dupVersions = ["DupA 1.0.0.0", "DupB 2.0.0.0", "DupC 1.0.0.0", "DupD 1.0.0.0"];
        var r28 = await release.Build(await release.Preview(28, new(Folder: Path.Combine(output, "dup"))), _ => { });
        var dupLog = File.ReadAllText(Path.Combine(r28.RunDirectory, "build.log"));
        Check(dups.Select(dup => $"{dup.Item1} {References(Path.Combine(output, "dup", dup.Item1 + ".dll"))["Dup"]}").SequenceEqual(dupVersions) &&
            !r28.OutsideReferences.Any(reference => reference.Name == "Dup.dll"),
            "in a release, projects referencing different copies of a same-named DLL each compile against their own, a later project's HintPath " +
            "in the snapshot wins over an earlier project's copy, and a project that resolves nothing uses another project's copy from the snapshot");
        Check(dupLog.Contains(@"(DupA's copy: DupD's HintPath ..\nowhere\Dup.dll is not in the snapshot, and DupA, built earlier in this release, resolved this copy"),
            "build.log says when a release project uses a copy another project in the release resolved from the snapshot");

        // The same projects built for the dev farm: each its own copy, and DupD only through the last resort.
        var dupSnapshot = Path.Combine(root, "dup", "source");
        var dupRun = Path.Combine(root, "dup", "run");
        await git.Git(root, "clone", "-q", origin, dupSnapshot);
        Directory.CreateDirectory(dupRun);
        var dupPlan = new Plan
        {
            Target = new("dup fixture", checkout, "refs/heads/master"), Commit = "", Fingerprint = "",
            Projects = [.. dups.Select(dup => ProjectInfo.Read(dupSnapshot, $@"{dup.Item1}\{dup.Item1}.csproj"))]
        };
        var dupOutputs = await new Pipeline(settings, git, new Bridge(settings)).Build(dupPlan, dupSnapshot, dupRun, _ => { });
        string DupOutput(string name) => dupOutputs.Single(file => Files.Comparer.Equals(file.RelativePath, $@"bin\{name}.dll")).Source;
        Check(dups.Select(dup => $"{dup.Item1} {References(DupOutput(dup.Item1))["Dup"]}").SequenceEqual(dupVersions) &&
            File.ReadAllText(Path.Combine(dupRun, "build.log")).Contains($"Reference Dup.dll: {Path.Combine(dupSnapshot, "lib", "one", "Dup.dll")} " +
                @"(last resort: DupD cannot resolve its HintPath ..\nowhere\Dup.dll, so it uses the copy DupA resolved earlier in this build)"),
            "a dev farm build compiles each project against its own copy of a same-named DLL, and a reference only an earlier project resolved " +
            "is used as a last resort, which build.log says");

        await CheckReleasePull(new(settings, git, output, m28, Gh, (number, title, branch, merge, head) => Pull(number, title, branch, merge, head),
            (from, message, files) => Commit(from, message, files), Publish, Squash, Untouched, before), root, Refs);
    }

    /// <summary>
    /// Building pulls master, from a second checkout of the release fixture's origin with master checked out. The pull requests change content
    /// alone, so nothing is compiled.
    /// </summary>
    private static async Task CheckReleasePull(ReleaseFixture fixture, string root, Func<string, Task<string>> refs)
    {
        var (git, output) = (fixture.Git, fixture.Output);
        var mine = Path.Combine(root, "mine");
        await git.Git(root, "clone", "-q", Path.Combine(root, "origin.git"), mine);
        Task<string> Mine(params string[] args) => git.Git(mine, args);
        await Mine("config", "user.email", "cultivate-test@localhost");
        await Mine("config", "user.name", "Cultivate isolated tests");
        var settings = new Settings
        {
            RepositoryRoot = mine, WebfarmRoot = fixture.Settings.WebfarmRoot, StateRoot = fixture.Settings.StateRoot, MSBuild = fixture.Settings.MSBuild,
            MappingAssembly = fixture.Settings.MappingAssembly, ReleaseRoot = output, LibraryRepositories = []
        };
        var release = new ReleaseFiles(settings, new GitRepository(settings), fixture.Gh);
        var tip = fixture.Master;
        async Task<string> Land(int number, string path)
        {
            var head = await fixture.Commit(tip, $"pr{number}", [(path, $"pull {number}")]);
            await fixture.Publish(head, $"refs/pull/{number}/head");
            tip = await fixture.Squash(tip, head, $"Pull fixture {number} (#{number})");
            await fixture.Publish(tip, "refs/heads/master");
            fixture.Pull(number, $"Pull fixture {number}", $"pull-{number}", tip, head);
            return tip;
        }
        ReleaseOptions Options(int number, bool noPull = false) => new(Folder: Path.Combine(output, "pull" + number), NoPull: noPull);
        string Released(int number, string name) => File.ReadAllText(Path.Combine(output, "pull" + number, name));
        static bool SamePath(string? path, string expected) => path is not null && Files.Comparer.Equals(Files.Full(path), Files.Full(expected));
        var view = Path.Combine(mine, @"Page\View.ascx");

        // Master gains two commits on origin.
        var start = await Mine("rev-parse", "HEAD");
        tip = await fixture.Commit(tip, "other merged work", [(@"Docs\Other.md", "other")]);
        var t40 = await Land(40, @"Page\Pull40.js");
        var before40 = await refs(mine);
        var p40 = await release.Preview(40, Options(40));
        Check(await refs(mine) == before40 && await Mine("status", "--porcelain") == "" &&
            p40.Pull is { Outcome: ReleasePullOutcome.Behind, Commits: 2, Before: var behindAt, Worktree: var behindIn, Detail: var behind } &&
            behindAt == start && SamePath(behindIn, mine) && behind == $"master in {behindIn} is 2 commit(s) behind origin; building pulls it." &&
            p40.Note?.EndsWith(behind, StringComparison.Ordinal) == true,
            "a preview says how far the checked-out master is behind origin, and changes no ref, origin/master included");
        var progress = new List<string>();
        var r40 = await release.Build(p40, progress.Add);
        var pulled = $"Pulled master: 2 new commit(s), {start[..7]} → {t40[..7]} ({r40.Pull?.Worktree})";
        Check(r40.Pull is { Outcome: ReleasePullOutcome.Pulled, Before: var from, After: var to, Commits: 2, Detail: var detail } && from == start && to == t40 &&
            detail == pulled && await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == t40 + "\n" + t40 && await Mine("status", "--porcelain") == "" &&
            File.ReadAllText(Path.Combine(mine, @"Page\Pull40.js")) == "pull 40" && progress.Contains($"Pulling master in {r40.Pull.Worktree}") && progress.Contains(pulled),
            "building fast-forwards a clean checked-out master and origin/master to the tip it fetched, the working files follow, and the progress says so");
        Check(Released(40, "Pull40.js") == "pull 40" && await Mine("for-each-ref", "refs/cultivate") == "" &&
            JsonSerializer.Deserialize<ReleaseResult>(File.ReadAllText(Path.Combine(r40.RunDirectory, "release.json")), Files.Json)?.Pull == r40.Pull,
            "the release folder is written, the pull is recorded with the result, and no private ref is left");

        File.WriteAllText(view, "my local edit");
        var t41 = await Land(41, @"Page\Pull41.js");
        var r41 = await release.Build(await release.Preview(41, Options(41)), _ => { });
        Check(r41.Pull?.Outcome == ReleasePullOutcome.Pulled && await Mine("rev-parse", "HEAD") == t41 && File.ReadAllText(view) == "my local edit" &&
            await Mine("status", "--porcelain") == " M Page/View.ascx" && File.ReadAllText(Path.Combine(mine, @"Page\Pull41.js")) == "pull 41",
            "a local change to a file the pulled commits do not touch survives the fast-forward");

        var t42 = await Land(42, @"Page\View.ascx");
        var r42 = await release.Build(await release.Preview(42, Options(42)), _ => { });
        Check(r42.Pull is { Outcome: ReleasePullOutcome.NotUpdated, Before: var kept, After: var still } && kept == t41 && still == t41 &&
            await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == t41 + "\n" + t42 && File.ReadAllText(view) == "my local edit" &&
            Released(42, "View.ascx") == "pull 42" && r42.Written && r42.Warnings.SingleOrDefault(warning => warning.StartsWith("master in ", StringComparison.Ordinal)) is { } refused &&
            refused.Contains("Page/View.ascx") && refused.Contains($"still built from origin's {t42[..7]}") && refused.Contains("pull master by hand"),
            "a local change the pulled commits would overwrite stops the pull, not the build: master and the change stay, the folder is written, and the warning names the file");
        await Mine("checkout", "-q", "--", "Page/View.ascx");

        await Mine("switch", "-q", "-c", "work");
        var second = Path.Combine(root, "mine-master");
        await Mine("worktree", "add", "-q", second, "master");
        var t43 = await Land(43, @"Page\Pull43.js");
        var r43 = await release.Build(await release.Preview(43, Options(43)), _ => { });
        Check(r43.Pull is { Outcome: ReleasePullOutcome.Pulled, Worktree: var there } && SamePath(there, second) && await git.Git(second, "rev-parse", "HEAD") == t43 &&
            File.Exists(Path.Combine(second, @"Page\Pull43.js")) && await Mine("symbolic-ref", "HEAD") == "refs/heads/work" && await Mine("rev-parse", "HEAD") == t41 &&
            !File.Exists(Path.Combine(mine, @"Page\Pull43.js")),
            "master checked out in another worktree is fast-forwarded there, and the main checkout on another branch stays as it was");

        await Mine("worktree", "remove", second);
        var t44 = await Land(44, @"Page\Pull44.js");
        var r44 = await release.Build(await release.Preview(44, Options(44)), _ => { });
        Check(r44.Pull is { Outcome: ReleasePullOutcome.Pulled, Worktree: null, Detail: var moved } &&
            moved == $"Pulled master: 1 new commit(s), {t43[..7]} → {t44[..7]} (not checked out)" && await Mine("rev-parse", "master") == t44 &&
            await Mine("reflog", "-1", "--format=%gs", "master") == "cultivate release: fast-forward to origin/master" && await Mine("rev-parse", "HEAD") == t41 &&
            !File.Exists(Path.Combine(mine, @"Page\Pull44.js")), "master checked out nowhere is moved to origin's tip with a reflog entry, and the checkout's own branch stays");

        await Mine("switch", "-q", "master");
        File.WriteAllText(Path.Combine(mine, @"Page\Local.js"), "mine");
        await Mine("add", "Page/Local.js");
        await Mine("commit", "-qm", "local work");
        var ahead = await Mine("rev-parse", "HEAD");
        var t45 = await Land(45, @"Page\Pull45.js");
        var p45 = await release.Preview(45, Options(45));
        var r45 = await release.Build(p45, _ => { });
        Check(p45.Pull?.Outcome == ReleasePullOutcome.LocalCommits && r45.Pull is { Outcome: ReleasePullOutcome.LocalCommits, Commits: 1, Worktree: var at } &&
            await Mine("rev-parse", "HEAD", "refs/remotes/origin/master") == ahead + "\n" + t45 && Released(45, "Pull45.js") == "pull 45" && r45.Warnings.Contains(
                $"Your local master in {at} has 1 commit(s) that are not on origin, so it was not updated to origin's {t45[..7]}. The release files were still " +
                $"built from origin's {t45[..7]}; pull master by hand."),
            "master with commits origin does not have is left where it is with a warning, and the release files are still built");
        await Mine("reset", "-q", "--hard", t45);

        var t46 = await Land(46, @"Page\Pull46.js");
        var before46 = await refs(mine);
        var p46 = await release.Preview(46, Options(46, noPull: true));
        var r46 = await release.Build(p46, _ => { });
        Check(p46.Pull?.Outcome == ReleasePullOutcome.Off && r46 is { NoPull: true, Pull.Detail: "Left master and origin/master where they were (--no-pull)." } &&
            await refs(mine) == before46 && !File.Exists(Path.Combine(mine, @"Page\Pull46.js")) && Released(46, "Pull46.js") == "pull 46",
            "--no-pull builds the release files and leaves master and origin/master where they were");

        var t47 = await Land(47, @"Page\Pull47.js");
        var p47 = await release.Preview(47, Options(47));
        tip = await fixture.Commit(tip, "after the preview", [(@"Docs\After.md", "after")]);
        await fixture.Publish(tip, "refs/heads/master");
        var r47 = await release.Build(p47, _ => { });
        Check(r47.BaseCommit == t47 && r47.Pull is { Outcome: ReleasePullOutcome.Pulled, After: var newest, Commits: 3 } && newest == tip &&
            await Mine("rev-parse", "HEAD") == tip && r47.Warnings.Any(warning => warning.Contains("moved on origin since the preview")),
            "when master moves on origin after the preview, the files are built from the previewed commit and master is pulled to the newest tip");
        Check(await fixture.Untouched() == fixture.Before, "pulling another checkout's master changes nothing in the first checkout");
    }

    private static void CheckReleaseNames()
    {
        static ReleasePullRequest Request(string branch, string title) => new(7, title, "", "", branch, "master", "MERGED", "m", "", "h");
        Check(ReleaseFiles.FolderName(Request("38636-2", "Anything"), null) == "38636" && ReleaseFiles.FolderName(Request("feature", "27220 Stop Pay Removed"), null) == "27220" &&
            ReleaseFiles.FolderName(Request("fix", "39050 2 HD - Totals"), null) == "39050" && ReleaseFiles.FolderName(Request("fix", "Fix 12345"), null) == "PR-7" &&
            ReleaseFiles.FolderName(Request("fix", "12 Short"), null) == "PR-7" && ReleaseFiles.FolderName(Request("38636", "1"), " Hot fix ") == "Hot fix",
            "a release folder is named by --name, else the branch's ticket, else a ticket starting the title, else PR-N");
        static bool Refused(string name)
        {
            try { ReleaseFiles.FolderName(Request("x", ""), name); }
            catch (InvalidOperationException) { return true; }
            return false;
        }
        Check(new[] { "..", @"a\b", "a/b", "CON", "nul.txt", "x:y", "trailing.", new string('a', 101) }.All(Refused), "unsafe release folder names are refused");
        var parsed = ReleaseFiles.Parse("""
            {"number":5,"title":"T","author":{"login":"al"},"headRefName":"5555","baseRefName":"master","state":"merged","mergedAt":"2026-01-02T03:04:05Z",
             "mergeCommit":{"oid":"abc"},"headRefOid":"def","url":"https://x/pull/5"}
            """);
        Check(parsed is { Number: 5, Author: "al", State: "MERGED", MergeCommit: "abc", HeadCommit: "def", MergedAt: "2026-01-02T03:04:05Z", Url: "https://x/pull/5" } &&
            ReleaseFiles.Parse("""{"number":6,"state":"OPEN","mergeCommit":null}""") is { MergeCommit: "" }, "gh's merged pull request JSON parses");
    }
}
