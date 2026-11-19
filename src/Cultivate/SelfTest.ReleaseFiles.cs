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
        async Task<string> Untouched() => string.Join("\n", await git.Git(checkout, "symbolic-ref", "HEAD"), await git.Git(checkout, "rev-parse", "HEAD"),
            await git.Git(checkout, "ls-files", "--stage"), await git.Git(checkout, "status", "--porcelain", "--ignored"),
            await git.Git(checkout, "worktree", "list", "--porcelain"), await git.Git(checkout, "for-each-ref", "--format=%(refname) %(objectname)"),
            Files.Hash(staleCore), Files.Hash(Path.Combine(checkout, @"Page\Value.cs")));
        var before = await Untouched();
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
        Check(await Untouched() == before, "the preview leaves the checkout, its refs and remote-tracking branches as they were, and deletes its private refs");

        var progress = new List<string>();
        var built = await release.Build(preview, progress.Add);
        Check(Listing(folder20).SequenceEqual(["Page.dll", "View.ascx"], Files.Comparer) && File.ReadAllText(Path.Combine(folder20, "View.ascx")) == "view 20" &&
            AssemblyName.GetAssemblyName(Path.Combine(folder20, "Page.dll")).Name == "Page",
            "the release folder holds exactly the changed project's DLL and the content file as master has it");
        Check(built is { Written: true } && built.Files.Select(file => (file.Name, file.Kind)).SequenceEqual([("Page.dll", ReleaseKind.ProjectDll), ("View.ascx", ReleaseKind.Content)]) &&
            built.Files.All(file => file.Sha256 == Files.Hash(Path.Combine(folder20, file.Name)) && file.Size > 0) && built.Files[0].AssemblyVersion is not null,
            "the result lists each written file with its kind, hash, size and assembly version");
        Check(progress.Where(line => line.StartsWith("Building ", StringComparison.Ordinal)).Order().SequenceEqual(["Building Core (2/3)", "Building Helpers (1/3)", "Building Page (3/3)"]),
            "support projects are built from master's snapshot, each once, before the project that needs them");
        Check(built.OutsideReferences.Count == 0 && File.Exists(staleCore) && Files.Hash(staleCore) == staleHash && !Listing(folder20).Contains("Core.dll"),
            "the release compiles against master's Core, not the checkout's stale DLL that lacks NewApi, and does not release it");
        Check(!Directory.Exists(Path.Combine(built.RunDirectory, "source")) && File.Exists(Path.Combine(built.RunDirectory, "release.json")) &&
            built.RunDirectory.StartsWith(Path.Combine(Files.Home, "Runs"), StringComparison.OrdinalIgnoreCase) &&
            Directory.GetFiles(Path.Combine(Files.Home, "Releases"), "*.result.json").Length == 1, "the snapshot is removed and the result is saved with the run and the folder's record");
        Check(await Untouched() == before, "a full release run leaves the checkout's branch, HEAD, index, files, bin, local and origin branches, and worktrees as they were");

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
            File.ReadAllText(Path.Combine(folder21, "Again.css")) == "again later" && r21.Warnings.Any(warning => warning.Contains("Page/Again.css changed again")),
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
