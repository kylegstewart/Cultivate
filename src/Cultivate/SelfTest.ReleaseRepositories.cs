using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>Points a fixture clone's origin at a GitHub-style URL that Git rewrites to the fixture's own origin, so it reads like a real clone's.</summary>
    private static async Task GitHubOrigin(GitRepository git, string clone, string origin, string url)
    {
        await git.Git(clone, "config", $"url.{origin.Replace('\\', '/')}.insteadOf", url);
        await git.Git(clone, "remote", "set-url", "origin", url);
    }

    /// <summary>Pull request URLs, Associated PRs sections and origin URLs, parsed without any repository.</summary>
    private static void CheckReleaseLinks(string home)
    {
        Check(ReleaseRepositories.GitHub("https://github.com/Meyer-Distributing/Public--Applications.git") == "Meyer-Distributing/Public--Applications" &&
            ReleaseRepositories.GitHub("https://github.com/Meyer-Distributing/MyMeyer") == "Meyer-Distributing/MyMeyer" &&
            ReleaseRepositories.GitHub("git@github.com:Meyer-Distributing/Database--SQL.git") == "Meyer-Distributing/Database--SQL" &&
            ReleaseRepositories.GitHub("ssh://git@github.com/Meyer-Distributing/MyMeyer--PMA-and-CategoryHierarchy.git") == "Meyer-Distributing/MyMeyer--PMA-and-CategoryHierarchy" &&
            ReleaseRepositories.GitHub("HTTPS://user@GitHub.com/a/b.git/") == "a/b" && ReleaseRepositories.GitHub(@"C:\repos\origin.git") is null &&
            ReleaseRepositories.GitHub("https://gitlab.com/a/b") is null,
            "a GitHub repository is read from an https, ssh or scp-style origin, with or without .git, in any case; other origins are not GitHub's");
        Check(ReleaseRepositories.Link("https://github.com/Meyer-Distributing/MyMeyer/pull/2823") is { GitHub: "Meyer-Distributing/MyMeyer", Number: 2823 } &&
            ReleaseRepositories.Link(" https://github.com/Meyer-Distributing/Public--Applications/pull/190/files#diff ") is { Number: 190 } &&
            ReleaseRepositories.Link("2823") is null && ReleaseRepositories.Link("https://github.com/a/b/issues/3") is null &&
            new PullRequestLink("Meyer-Distributing/MyMeyer", 7).Key == new PullRequestLink("meyer-distributing/mymeyer", 7).Key,
            "a pull request URL gives its repository and number, also with a trailing path; issues and bare numbers are not links");
        const string body = """
            ### Ticket:
            https://mymeyer.meyerdistributing.com/it/ticket-list?ticketNum=39126

            ---

            ### Associated PRs:
            - Public--Applications: https://github.com/Meyer-Distributing/Public--Applications/pull/190
            - SQL: https://github.com/Meyer-Distributing/Database--SQL/pull/5415
            - Again: https://github.com/meyer-distributing/public--applications/pull/190

            ---

            ### Description:
            Follows https://github.com/Meyer-Distributing/MyMeyer/pull/2800.
            """;
        Check(ReleaseRepositories.Associated(body).Select(link => link.Url).SequenceEqual([
                "https://github.com/Meyer-Distributing/Public--Applications/pull/190", "https://github.com/Meyer-Distributing/Database--SQL/pull/5415"]) &&
            ReleaseRepositories.Associated(body.Replace("- Public--Applications", "x").Split("### Associated")[0]).Count == 0 &&
            ReleaseRepositories.Associated("### Associated PRs:\r\n- None\r\n\r\n---\r\n### Description:\r\nhttps://github.com/a/b/pull/1").Count == 0 &&
            ReleaseRepositories.Associated(null).Count == 0,
            "only the Associated PRs section's links are followed, each once; - None and a body without the section link nothing");
        var config = Path.Combine(home, "origin-config");
        Directory.CreateDirectory(Path.Combine(config, "main", ".git"));
        File.WriteAllText(Path.Combine(config, "main", ".git", "config"),
            "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/other/fork.git\n[remote \"origin\"]\n\turl = git@github.com:Meyer-Distributing/MyMeyer.git\n");
        Directory.CreateDirectory(Path.Combine(config, "linked"));
        File.WriteAllText(Path.Combine(config, "linked", ".git"), "gitdir: C:/elsewhere/.git/worktrees/linked");
        Check(ReleaseRepositories.Origin(Path.Combine(config, "main")) == "git@github.com:Meyer-Distributing/MyMeyer.git" &&
            ReleaseRepositories.Origin(Path.Combine(config, "linked")) is null, "a main checkout's origin is read from its .git\\config; a linked worktree has none");
    }

    /// <summary>
    /// Release files across repositories: GitHub-style clones beside the MyMeyer checkout, built from bare fixture origins with Git plumbing (so a
    /// SQL repository can track two paths that differ only by case, as Database--SQL does), and the GitHub CLI faked with Meyer's PR template.
    /// </summary>
    private static async Task CheckReleaseRepositories(string home, string msbuild)
    {
        CheckReleaseLinks(home);
        var root = Path.Combine(home, "multi");
        var origins = Path.Combine(root, "origins");
        var github = Path.Combine(root, "GitHub");
        var output = Path.Combine(root, "Releases");
        var scratch = Path.Combine(root, "scratch");
        var empty = Path.Combine(root, "empty");
        foreach (var folder in new[] { origins, github, scratch, empty }) Directory.CreateDirectory(folder);
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(github, "MyMeyer"), WebfarmRoot = Path.Combine(root, "farm"), StateRoot = Path.Combine(root, "state"), MSBuild = msbuild,
            MappingAssembly = Path.Combine(root, "no-mapping-assembly.dll"), ReleaseRoot = output, LibraryRepositories = []
        };
        var git = new GitRepository(settings);
        const string owner = "Meyer-Fixture";
        var identity = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Cultivate isolated tests", ["GIT_AUTHOR_EMAIL"] = "cultivate-test@localhost",
            ["GIT_COMMITTER_NAME"] = "Cultivate isolated tests", ["GIT_COMMITTER_EMAIL"] = "cultivate-test@localhost"
        };
        var safety = new Dictionary<string, string> { ["WebfarmPath"] = Path.Combine(root, "farm") + "\\" };
        static string Url(string repository, int number) => $"https://github.com/{owner}/{repository}/pull/{number}";
        List<string> Listing(string folder) => [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Order(Files.Comparer)];

        async Task<string> Bare(string name, string branch = "master")
        {
            var path = Path.Combine(origins, name + ".git");
            await git.Git(root, "init", "-q", "--bare", "-b", branch, path);
            return path;
        }
        // Commits straight into a bare origin through a private index: no checkout, so paths that differ only by case each keep their content.
        async Task<string> Commit(string bare, string? parent, string message, params (string Path, object? Content)[] files)
        {
            var index = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".index");
            var environment = new Dictionary<string, string>(identity) { ["GIT_INDEX_FILE"] = index };
            Task<string> G(params string[] arguments) => Commands.Run("git.exe", ["-c", "core.ignorecase=false", "-C", bare, "--work-tree=" + empty, .. arguments], bare, environment);
            try
            {
                await (parent is null ? G("read-tree", "--empty") : G("read-tree", parent));
                foreach (var (path, content) in files)
                {
                    var name = path.Replace('\\', '/');
                    if (content is null)
                    {
                        await G("update-index", "--force-remove", "--", name);
                        continue;
                    }
                    var blob = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                    if (content is byte[] bytes) File.WriteAllBytes(blob, bytes);
                    else File.WriteAllText(blob, (string)content);
                    var id = await G("hash-object", "-w", "--no-filters", blob);
                    File.Delete(blob);
                    await G("update-index", "--add", "--cacheinfo", $"100644,{id},{name}");
                }
                var tree = await G("write-tree");
                return await G(["commit-tree", tree, .. parent is null ? Array.Empty<string>() : ["-p", parent], "-m", message]);
            }
            finally { File.Delete(index); }
        }
        async Task<string> Squash(string bare, string onto, string head, string message)
        {
            var tree = (await Commands.Run("git.exe", ["-C", bare, "merge-tree", "--write-tree", onto, head], bare, identity)).Split('\n')[0].Trim();
            return await Commands.Run("git.exe", ["-C", bare, "commit-tree", tree, "-p", onto, "-m", message], bare, identity);
        }
        Task Publish(string bare, string commit, string reference) => git.Git(bare, "update-ref", reference, commit);
        async Task<string> Clone(string name, string bare, string url)
        {
            var path = Path.Combine(github, name);
            await git.Git(root, "clone", "-q", bare, path);
            await GitHubOrigin(git, path, bare, url);
            return path;
        }
        async Task<byte[]> Library(string name, string version, string code)
        {
            var source = Path.Combine(scratch, $"{name} {version}");
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
            File.WriteAllText(Path.Combine(source, "Value.cs"), $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] {code}");
            await Commands.Run(msbuild, [Path.Combine(source, name + ".csproj"), "-nologo", "-t:Build", "-p:Configuration=Release", "-v:minimal"], source, safety);
            return File.ReadAllBytes(Path.Combine(source, "out", name + ".dll"));
        }
        // A classic project with no webfarm copy target, its output in its own bin, as Public--Applications' are.
        static string Classic(string name, string references = "", string compile = "", string properties = "") => $$"""
            <Project ToolsVersion="15.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
                <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
                <OutputType>Library</OutputType>
                <AssemblyName>{{name}}</AssemblyName>
                <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>{{properties}}
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "><OutputPath>bin\</OutputPath></PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "><OutputPath>bin\</OutputPath></PropertyGroup>
              <ItemGroup><Reference Include="System" />{{references}}</ItemGroup>
              <ItemGroup><Compile Include="Value.cs" />{{compile}}</ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <Target Name="BeforeBuild"></Target>
              <Target Name="AfterBuild"></Target>
            </Project>
            """;

        var pulls = new Dictionary<string, string>();
        var lists = new Dictionary<string, string>();
        void Pull(string repository, int number, string title, string branch, string merge, string head, IEnumerable<string> links, string state = "MERGED",
            string into = "master")
        {
            var linked = links.ToList();
            var body = "### Ticket:\nhttps://mymeyer.meyerdistributing.com/it/ticket-list?ticketNum=39126\n\n---\n\n### Associated PRs:\n" +
                (linked.Count == 0 ? "- None" : string.Join("\n", linked.Select(link => $"- {link.Split('/')[4]}: {link}"))) +
                $"\n\n---\n\n### Description:\nSee {Url("Elsewhere", 1)} for background.\n";
            pulls[$"{owner}/{repository}#{number}".ToLowerInvariant()] = JsonSerializer.Serialize(new
            {
                number, title, author = new { login = "alice" }, headRefName = branch, baseRefName = into, state,
                mergedAt = state == "MERGED" ? "2026-11-19T10:00:00Z" : null, mergeCommit = merge.Length == 0 ? null : new { oid = merge },
                headRefOid = head, url = Url(repository, number), body
            });
        }
        var asked = new List<string>();
        Task<string> Gh(string[] arguments)
        {
            lock (asked) asked.Add(string.Join(' ', arguments));
            var at = Array.IndexOf(arguments, "--repo");
            var repository = (at < 0 ? $"{owner}/MyMeyer" : arguments[at + 1]).ToLowerInvariant();
            if (arguments is ["pr", "list", ..]) return Task.FromResult(lists.GetValueOrDefault(repository) ?? "[]");
            if (arguments is ["pr", "view", "999", ..])
                return Task.FromException<string>(new InvalidOperationException("gh.exe failed (1).\nHTTP 403: Resource not accessible by integration"));
            return arguments is ["pr", "view", var number, ..] && pulls.TryGetValue($"{repository}#{number}", out var json) ? Task.FromResult(json)
                : Task.FromException<string>(new InvalidOperationException("gh.exe failed (1).\nGraphQL: Could not resolve to a PullRequest with the number of 404."));
        }

        // The DLLs the Public--Applications project compiles against, and the stale build of its own output that Git tracks.
        var sibling = await Library("Sibling", "1.0.0.0", "public class SiblingValue { public static int Value => 1; }");
        var tracked = await Library("Tracked", "2.0.0.0", "public class TrackedValue { public static int Value => 2; }");
        var fallback = await Library("Fallback", "3.0.0.0", "public class FallbackValue { public static int Value => 3; }");
        var stale = await Library("SalesPortal", "9.9.9.9", "public class Stale { }");
        // The PMA project's Objects: the MyMeyer checkout's current build, and the stale copy the PMA repository tracks.
        var objects = await Library("Objects", "2.0.0.0", "public class ObjectsValue { public static int Value => 1; public static int NewApi => 2; }");
        var staleObjects = await Library("Objects", "1.0.0.0", "public class ObjectsValue { public static int Value => 1; }");

        // MyMeyer: content-only pull requests #10 and #11 for ticket 39126.
        var myMeyer = await Bare("MyMeyer");
        var m0 = await Commit(myMeyer, null, "base", ("Page/View.ascx", "view 0"), ("Page/Other.ascx", "other 0"));
        await Publish(myMeyer, m0, "refs/heads/master");
        var mainClone = await Clone("MyMeyer", myMeyer, $"https://github.com/{owner}/MyMeyer.git");
        Directory.CreateDirectory(Path.Combine(mainClone, "bin"));
        File.WriteAllBytes(Path.Combine(mainClone, "bin", "Sibling.dll"), sibling);
        File.WriteAllBytes(Path.Combine(mainClone, "bin", "Objects.dll"), objects);

        // Public--Applications: SalesPortal has no webfarm target, tracks its bin (its own stale output included), references a sibling
        // repository's build, a DLL only the checkout's bin has, and SalesCore by ProjectReference.
        var applications = await Bare("Public--Applications");
        string Portal(string properties = "") => Classic("SalesPortal", """
            <Reference Include="Tracked"><HintPath>bin\Tracked.dll</HintPath></Reference>
            <Reference Include="Sibling"><HintPath>..\..\MyMeyer\bin\Sibling.dll</HintPath></Reference>
            <Reference Include="Fallback"><HintPath>..\..\DistForm\bin\Fallback.dll</HintPath></Reference>
            <ProjectReference Include="..\SalesCore\SalesCore.csproj"><Project>{6E0C8E2A-1A3B-4C5D-8E9F-0A1B2C3D4E5F}</Project><Name>SalesCore</Name></ProjectReference>
            """, properties: properties);
        var a0 = await Commit(applications, null, "base",
            ("SalesPortal/SalesPortal.csproj", Portal()), ("SalesPortal/SalesPortal.aspx", "portal 0"),
            ("SalesPortal/Value.cs", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class SalesPortalValue { public static int Value => 0; }"),
            ("SalesPortal/bin/Tracked.dll", tracked), ("SalesPortal/bin/SalesPortal.dll", stale),
            ("SalesCore/SalesCore.csproj", Classic("SalesCore")), ("SalesCore/Value.cs", "public class SalesCoreValue { public static int Value => 5; }"));
        await Publish(applications, a0, "refs/heads/master");
        var applicationsClone = await Clone("Public--Applications", applications, $"https://github.com/{owner}/Public--Applications.git");
        File.WriteAllBytes(Path.Combine(applicationsClone, "SalesPortal", "bin", "Fallback.dll"), fallback);

        // MyMeyer--PMA-and-CategoryHierarchy: #238 uses Objects through a HintPath into the MyMeyer checkout's bin, beside stale copies the
        // repository tracks in Libraries and in the project's bin, and has a reference and a package that exist nowhere but that no code uses.
        // #239 changes an SDK-style project.
        var pma = await Bare("MyMeyer--PMA-and-CategoryHierarchy");
        var p0 = await Commit(pma, null, "base",
            ("PartsMatrixAdminV2/PartsMatrixAdminV2.csproj", Classic("PartsMatrixAdminV2", """
                <Reference Include="Objects"><HintPath>..\..\MyMeyer\bin\Objects.dll</HintPath></Reference>
                <Reference Include="Nowhere"><HintPath>..\..\DesktopModules\POPv3\bin\Nowhere.dll</HintPath></Reference>
                <Reference Include="Pkg, Version=1.0.0.0, Culture=neutral, processorArchitecture=MSIL"><HintPath>packages\Missing.Package.1.0.0\lib\net471\Pkg.dll</HintPath></Reference>
                """, """<Compile Include="PMAObjects.cs" />""")),
            ("PartsMatrixAdminV2/Value.cs", "public class PmaValue { }"), ("PartsMatrixAdminV2/PMAObjects.cs", "public class PmaObjects { }"),
            ("PartsMatrixAdminV2/Libraries/Objects.dll", staleObjects), ("PartsMatrixAdminV2/bin/Objects.dll", staleObjects),
            ("PartsMatrixAdminV2/Controls/ucMainAddOrEditPart.ascx", "part 0"),
            ("ModernApi/ModernApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
            ("ModernApi/Program.cs", "class Program { static void Main() { } }"));
        await Publish(pma, p0, "refs/heads/master");
        var pmaClone = await Clone("MyMeyer--PMA-and-CategoryHierarchy", pma, $"https://github.com/{owner}/MyMeyer--PMA-and-CategoryHierarchy.git");

        // Database--SQL: no project files, origin's only branch is main, and two tracked paths differ only by case.
        var sql = await Bare("Database--SQL", "main");
        const string procedures = "SqlSource/SqlSource/CompatMatrix/StoredProcedures/", logic = "SqlSource/SqlSource/MeyerLogic/StoredProcedures/";
        var s0 = await Commit(sql, null, "base", (".gitattributes", "*.sql text eol=crlf\n"), (procedures + "PMA_ACES_GetPartDataV9.sql", "select 9\n"),
            (procedures + "PMA_ACES_UpdateExistingPartV28.sql", "update 28\n"), (procedures + "Old.sql", "old\n"),
            (logic + "UpdateBOAFByMFG.sql", "-- ByMFG 0\n"), (logic + "UpdateBOAFbyMFG.sql", "-- byMFG 0\n"));
        await Publish(sql, s0, "refs/heads/main");
        var sqlClone = await Clone("Database--SQL", sql, $"https://github.com/{owner}/Database--SQL.git");

        // Other--SQL, whose origin is in scp-style ssh form.
        var otherSql = await Bare("Other--SQL");
        var o0 = await Commit(otherSql, null, "base", ("Procs/Other.sql", "other 0\n"));
        await Publish(otherSql, o0, "refs/heads/master");
        await Clone("Other--SQL", otherSql, $"git@github.com:{owner}/Other--SQL.git");

        // Broken--App: #5 does not compile.
        var broken = await Bare("Broken--App");
        var b0 = await Commit(broken, null, "base", ("Broken/Broken.csproj", Classic("Broken")), ("Broken/Value.cs", "public class BrokenValue { }"));
        await Publish(broken, b0, "refs/heads/master");
        var brokenClone = await Clone("Broken--App", broken, $"https://github.com/{owner}/Broken--App.git");

        // Not clones: a plain folder, and a linked worktree of a clone.
        Directory.CreateDirectory(Path.Combine(github, "NotARepo"));
        await git.Git(applicationsClone, "worktree", "add", "-q", "--detach", Path.Combine(github, "Applications-worktree"));

        // Ticket 39126 across the repositories.
        var h10 = await Commit(myMeyer, m0, "pr10", ("Page/View.ascx", "view 10"));
        var h11 = await Commit(myMeyer, m0, "pr11", ("Page/Other.ascx", "other 11"));
        var m10 = await Squash(myMeyer, m0, h10, "39126 - Remove price levels (#10)");
        var m11 = await Squash(myMeyer, m10, h11, "39126 - Second part (#11)");
        await Publish(myMeyer, h10, "refs/pull/10/head");
        await Publish(myMeyer, h11, "refs/pull/11/head");
        await Publish(myMeyer, m11, "refs/heads/master");
        Pull("MyMeyer", 10, "39126 - Remove price levels", "39126", m10, h10, [Url("Public--Applications", 190), Url("MyMeyer", 11),
            Url("Public--Applications", 191), Url("Public--Applications", 192), Url("Not--Cloned", 7), Url("Database--SQL", 999)]);
        Pull("MyMeyer", 11, "39126 - Second part", "39126-2", m11, h11, []);

        var h190 = await Commit(applications, a0, "pr190", ("SalesPortal/SalesPortal.aspx", "portal 190"),
            ("SalesPortal/Value.cs", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class SalesPortalValue { public static int Value => " +
                "TrackedValue.Value + SiblingValue.Value + FallbackValue.Value + SalesCoreValue.Value; }"));
        var m190 = await Squash(applications, a0, h190, "39126 - Sales portal (#190)");
        await Publish(applications, h190, "refs/pull/190/head");
        await Publish(applications, m190, "refs/heads/master");
        Pull("Public--Applications", 190, "39126 - Sales portal", "39126", m190, h190, [Url("MyMeyer", 10), Url("MyMeyer--PMA-and-CategoryHierarchy", 238)]);
        Pull("Public--Applications", 191, "39126 - Still open", "39126-3", "", h190, [], state: "OPEN");
        Pull("Public--Applications", 192, "39126 - Into development", "39126-4", m190, h190, [], into: "development");
        // #193 stops the build copying the project's output, so the snapshot's tracked stale SalesPortal.dll is all its bin holds.
        var h193 = await Commit(applications, m190, "pr193", ("SalesPortal/SalesPortal.csproj", Portal("<SkipCopyBuildProduct>true</SkipCopyBuildProduct>")));
        var m193 = await Squash(applications, m190, h193, "39127 - Skip the copy (#193)");
        await Publish(applications, h193, "refs/pull/193/head");
        Pull("Public--Applications", 193, "39127 - Skip the copy", "39127", m193, h193, []);

        var h238 = await Commit(pma, p0, "pr238", ("PartsMatrixAdminV2/PMAObjects.cs", "public class PmaObjects { public static int Value => ObjectsValue.NewApi; }"),
            ("PartsMatrixAdminV2/Controls/ucMainAddOrEditPart.ascx", "part 238"));
        var m238 = await Squash(pma, p0, h238, "39187 - Parts (#238)");
        var h239 = await Commit(pma, m238, "pr239", ("ModernApi/Program.cs", "class Program { static void Main() { System.Console.WriteLine(); } }"),
            ("PartsMatrixAdminV2/Controls/Other.ascx", "other 239"));
        var m239 = await Squash(pma, m238, h239, "39188 - Modern (#239)");
        await Publish(pma, h238, "refs/pull/238/head");
        await Publish(pma, h239, "refs/pull/239/head");
        await Publish(pma, m239, "refs/heads/master");
        Pull("MyMeyer--PMA-and-CategoryHierarchy", 238, "39187 - Parts", "39187", m238, h238, [Url("Database--SQL", 5415), Url("Public--Applications", 190)]);
        Pull("MyMeyer--PMA-and-CategoryHierarchy", 239, "39188 - Modern", "39188", m239, h239, []);

        var h5415 = await Commit(sql, s0, "pr5415", (procedures + "PMA_ACES_GetPartDataV9.sql", "select 9 new\nline 2\n"),
            (procedures + "PMA_ACES_UpdateExistingPartV28.sql", "update 28 new\n"), (procedures + "Old.sql", null),
            (logic + "UpdateBOAFByMFG.sql", "-- ByMFG 5415\n"), (logic + "UpdateBOAFbyMFG.sql", "-- byMFG 5415\n"), ("docs/notes.md", "notes\n"), ("deploy.ps1", "deploy\n"));
        var m5415 = await Squash(sql, s0, h5415, "39187 - Procedures (#5415)");
        var later = await Commit(sql, m5415, "later V28", (procedures + "PMA_ACES_UpdateExistingPartV28.sql", "update 28 later\n"));
        await Publish(sql, h5415, "refs/pull/5415/head");
        await Publish(sql, later, "refs/heads/main");
        Pull("Database--SQL", 5415, "39187 - Procedures", "39187", m5415, h5415, [Url("Other--SQL", 3)], into: "main");

        var h3 = await Commit(otherSql, o0, "pr3", ("Procs/Other.sql", "other 3\n"));
        var m3 = await Squash(otherSql, o0, h3, "39187 - Other (#3)");
        await Publish(otherSql, h3, "refs/pull/3/head");
        await Publish(otherSql, m3, "refs/heads/master");
        Pull("Other--SQL", 3, "39187 - Other", "39187", m3, h3, [Url("Broken--App", 5)]);

        var h5 = await Commit(broken, b0, "pr5", ("Broken/Value.cs", "public class BrokenValue { public static int Value => \"text\"; }"));
        var m5 = await Squash(broken, b0, h5, "39187 - Broken (#5)");
        await Publish(broken, h5, "refs/pull/5/head");
        await Publish(broken, m5, "refs/heads/master");
        Pull("Broken--App", 5, "39187 - Broken", "39187", m5, h5, []);

        // Discovery reads each clone's config: no Git process, main checkouts only.
        var calls = git.Calls;
        var clones = ReleaseRepositories.Clones(settings);
        Check(git.Calls == calls && clones[0] is { Main: true, Name: "MyMeyer", GitHub: "Meyer-Fixture/MyMeyer", Branch: "master" } &&
            clones.Select(clone => clone.Name).Order(StringComparer.Ordinal).SequenceEqual(
                ["Broken--App", "Database--SQL", "MyMeyer", "MyMeyer--PMA-and-CategoryHierarchy", "Other--SQL", "Public--Applications"]),
            "the clones beside MyMeyer are found by their origin without starting Git, MyMeyer first; a plain folder and a linked worktree are not clones");
        Check(ReleaseRepositories.Find(settings, "meyer-fixture/PUBLIC--applications")?.Root == applicationsClone &&
            ReleaseRepositories.Find(settings, $"{owner}/Other--SQL")?.GitHub == $"{owner}/Other--SQL" && ReleaseRepositories.Find(settings, $"{owner}/Not--Cloned") is null &&
            ReleaseRepositories.Named(settings, "public--applications").Root == applicationsClone &&
            ReleaseRepositories.Named(settings, $"{owner}/Database--SQL").Root == sqlClone && ReleaseRepositories.Named(settings, pmaClone + "\\").Root == pmaClone,
            "a repository is found by owner/name in any case, also from an ssh origin, and --repo takes a name, owner/name or a checkout path");
        static string Refused(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) { return exception.Message; }
            return "";
        }
        Check(ReleaseRunner.Target(settings, Url("Public--Applications", 190), null) is var (byUrl, n190) && byUrl.Root == applicationsClone && n190 == 190 &&
            ReleaseRunner.Target(settings, "190", "Public--Applications") is var (byRepo, _) && byRepo.Root == applicationsClone &&
            ReleaseRunner.Target(settings, "#12", null) is var (byNumber, n12) && byNumber.Main && n12 == 12 &&
            Refused(() => ReleaseRunner.Target(settings, Url("Public--Applications", 190), "MyMeyer")).Contains("Leave out --repo or the URL") &&
            Refused(() => ReleaseRepositories.Named(settings, "Nope")).Contains($"Nope is not cloned in {github}") &&
            Refused(() => ReleaseRunner.Target(settings, Url("Not--Cloned", 7), null)).Contains($"{owner}/Not--Cloned is not cloned in {github}"),
            "--pr takes a number for MyMeyer or --repo's clone, or a pull request URL; a URL --repo contradicts, or a repository not cloned, is refused");
        var sqlRepository = await ReleaseRepositories.WithBranch(git, ReleaseRepositories.Named(settings, "Database--SQL"));
        var applicationsRepository = await ReleaseRepositories.WithBranch(git, ReleaseRepositories.Named(settings, "Public--Applications"));
        var mainRepository = ReleaseRepositories.MainCheckout(settings);
        Check(sqlRepository.Branch == "main" && applicationsRepository.Branch == "master" && (await ReleaseRepositories.WithBranch(git, mainRepository)).Branch == "master",
            "another repository releases from master when origin has one, else origin's default branch; MyMeyer from the configured base branch");

        // The whole ticket: MyMeyer #10 and everything it links, directly or through the linked pull requests' own descriptions.
        var runner = new ReleaseRunner(settings, git, Gh);
        var ticket = Path.Combine(output, "39126");
        var preview = await runner.Preview(mainRepository, 10);
        ReleaseResult In(ReleaseRun run, string subfolder) => run.Releases.Single(release => release.Subfolder == subfolder);
        string[] subfolders = ["MyMeyer", "Public--Applications", "MyMeyer (PR 11)", "MyMeyer--PMA-and-CategoryHierarchy", "SQL", "SQL (Other--SQL)", "Broken--App"];
        Check(preview is { Name: "39126", Written: false, CanBuild: true, Complete: true } && preview.Folder == ticket &&
            preview.Releases.Select(release => release.Subfolder).SequenceEqual(subfolders) &&
            preview.Releases.All(release => release.Name == "39126" && release.Folder == Path.Combine(ticket, release.Subfolder)) && !Directory.Exists(ticket),
            "a preview follows the Associated PRs links transitively, each repository in its own subfolder of the requested pull request's ticket folder, and writes nothing");
        Check(new[] { "pr view 10 ", "pr view 190 ", "pr view 238 ", "pr view 5415 " }.All(call => asked.Count(line => line.StartsWith(call, StringComparison.Ordinal)) == 1) &&
            !asked.Any(line => line.StartsWith("pr view 1 ", StringComparison.Ordinal)) &&
            asked.Contains("pr view 190 --repo Meyer-Fixture/Public--Applications --json number,title,author,headRefName,baseRefName,state,mergedAt,mergeCommit,headRefOid,url,body"),
            "links back to a pull request already seen are not followed again, a link outside the Associated PRs section is not followed, and gh is asked for the other repository");
        Check(preview.Skipped.Select(skipped => $"{skipped.Repository} #{skipped.Number}").SequenceEqual(["Public--Applications #191", "Public--Applications #192",
                $"{owner}/Not--Cloned #7", "Database--SQL #999"]) &&
            preview.Skipped[0].Reason.StartsWith("Public--Applications pull request #191 is not merged yet. Release files are built from master after the merge.", StringComparison.Ordinal) &&
            !preview.Skipped[0].Reason.Contains("activate") &&
            preview.Skipped[1].Reason.StartsWith("Public--Applications pull request #192 was merged into development, not master.", StringComparison.Ordinal) &&
            preview.Skipped[2].Reason == $"{owner}/Not--Cloned is not cloned in {github}, so its files are not built. Clone it there and build again, or release its files by hand." &&
            preview.Skipped[3].Reason.StartsWith("The GitHub CLI failed: HTTP 403", StringComparison.Ordinal) && preview.Skipped[2].Url == Url("Not--Cloned", 7),
            "a linked pull request that is not merged, merged into another branch, not cloned, or unreadable is skipped with its reason; the others are still previewed");
        var sqlPreview = In(preview, "SQL");
        Check(sqlPreview is { Kind: ReleaseRepositoryKind.Sql, Repository: "Database--SQL", BaseBranch: "main", Projects.Count: 0, SupportProjects.Count: 0 } &&
            sqlPreview.Content.SequenceEqual([procedures + "PMA_ACES_GetPartDataV9.sql", procedures + "PMA_ACES_UpdateExistingPartV28.sql", logic + "UpdateBOAFByMFG.sql",
                logic + "UpdateBOAFbyMFG.sql"]) &&
            sqlPreview.Changes.Single(change => change.Path.EndsWith("V28.sql", StringComparison.Ordinal)) is { Status: ReleaseStatus.ChangedAgain, Kind: ReleaseKind.Sql, Commits: [{ Subject: "later V28" }] } &&
            sqlPreview.Changes.Single(change => change.Path.EndsWith("V9.sql", StringComparison.Ordinal)).Status == ReleaseStatus.Same &&
            sqlPreview.Deleted.Single() is { Path: var old, Reason: var dropped } && old.EndsWith("Old.sql", StringComparison.Ordinal) && dropped.Contains("Drop it from the database") &&
            sqlPreview.NotIncluded.Select(note => note.Path).SequenceEqual(["deploy.ps1", "docs/notes.md"]) &&
            sqlPreview.NotIncluded[0].Reason.StartsWith("Not a SQL script", StringComparison.Ordinal),
            "a repository without project files is a SQL repository: its changed .sql files are released with their status, a deletion is reported, other files are listed as not included");
        var applicationsPreview = In(preview, "Public--Applications");
        Check(applicationsPreview is { Kind: ReleaseRepositoryKind.Code, Projects: [{ Assembly: "SalesPortal" }], Content: ["SalesPortal/SalesPortal.aspx"] } &&
            applicationsPreview.SupportProjects.Single() is { Assembly: "SalesCore", HasWebfarmTarget: false, Reason: var because } && because.Contains("ProjectReference") &&
            applicationsPreview.Pull?.Outcome == ReleasePullOutcome.Behind && applicationsPreview.Checkout == applicationsClone,
            "a classic project without the webfarm target is released, with the project it has a ProjectReference to as a support project");
        Check(In(preview, "MyMeyer (PR 11)").Warnings.Contains("MyMeyer already holds MyMeyer PR #10's files, so MyMeyer PR #11's are written to MyMeyer (PR 11) instead.") &&
            In(preview, "SQL (Other--SQL)").Warnings.Contains("SQL already holds Database--SQL PR #5415's files, so Other--SQL PR #3's are written to SQL (Other--SQL) instead.") &&
            In(preview, "SQL (Other--SQL)").Kind == ReleaseRepositoryKind.Sql,
            "a second pull request of a repository, and a second SQL repository, get their own subfolders, with a warning saying why");

        // The screens for several repositories.
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
        var screen = Flat(Capture(new ReleasePreview(preview, ReleaseScreen.Removables(preview)).Render(400, 400), 400, 400));
        Check(screen.Contains($"PR #10: 39126 - Remove price levels in MyMeyer, and 6 linked pull requests it lists TICKET FOLDER {ticket} One subfolder per repository: MyMeyer MyMeyer PR #10, 1 file") &&
            screen.Contains("SQL Database--SQL PR #5415, 4 files") && screen.Contains("LINKED PULL REQUESTS NOT BUILT (4) Public--Applications #191 Public--Applications pull request #191 is not merged yet") &&
            screen.Contains("━━ SQL Meyer-Fixture/Database--SQL, SQL scripts") && screen.Contains("SQL SCRIPTS (4)") &&
            screen.Contains("Building writes 4 SQL scripts, as main has them. Nothing is built or run.") && screen.Contains("━━ PUBLIC--APPLICATIONS Meyer-Fixture/Public--Applications, built from master") &&
            !screen[screen.IndexOf("━━ SQL Meyer", StringComparison.Ordinal)..screen.IndexOf("SQL SCRIPTS (4)", StringComparison.Ordinal)].Contains("DLLS TO BUILD") &&
            screen.Contains("Enter build"),
            "the preview of several repositories starts with the ticket folder, each subfolder and the skipped links, then each repository's own preview");
        var paging = new ReleasePreview(preview, ReleaseScreen.Removables(preview));
        var fits = true;
        for (var page = 0; page < 40; page++)
        {
            var lines = Capture(paging.Render(80, 24), 80, 24).Split('\n');
            fits &= lines.Length <= 24 && lines.All(line => line.Length <= 80) && lines[^1].Contains("Esc back");
            paging.HandleKey(Press(ConsoleKey.PageDown), 24);
        }
        var building = Flat(Capture(new ReleaseBuildView(preview).Render(400, 30), 400, 30));
        Check(fits && building.Contains("7 repositories, each built from its release branch") && building.Contains("PR #10: 39126 - Remove price levels and 6 linked pull requests") &&
            building.Contains("Folder " + ticket), "several repositories' preview pages in an 80x24 terminal, and the build screen names the ticket folder");

        // Building: PMA's missing reference fails only its own release.
        var progress = new List<string>();
        var built = await runner.Build(preview, progress.Add);
        string[] expected = [@"MyMeyer\View.ascx", @"MyMeyer (PR 11)\Other.ascx", @"Public--Applications\SalesPortal.aspx", @"Public--Applications\SalesPortal.dll",
            @"SQL\PMA_ACES_GetPartDataV9.sql", @"SQL\PMA_ACES_UpdateExistingPartV28.sql", @"SQL\UpdateBOAFByMFG.sql", @"SQL\StoredProcedures\UpdateBOAFbyMFG.sql",
            @"SQL (Other--SQL)\Other.sql", @"MyMeyer--PMA-and-CategoryHierarchy\PartsMatrixAdminV2.dll", @"MyMeyer--PMA-and-CategoryHierarchy\ucMainAddOrEditPart.ascx"];
        Check(Listing(ticket).SequenceEqual(expected.Order(Files.Comparer), StringComparer.Ordinal) && built.Written && !built.Complete,
            "building writes each repository's files to its subfolder: both MyMeyer pull requests', Public--Applications' and PMA's DLL and page, and both SQL repositories' scripts");
        var brokenBuilt = In(built, "Broken--App");
        Check(!brokenBuilt.Written && brokenBuilt.Failure?.Contains("error CS0029") == true && !Directory.Exists(Path.Combine(ticket, "Broken--App")) &&
            built.Note?.Contains("Not written: Broken--App; see each one's failure.") == true &&
            brokenBuilt.Pull is { Outcome: ReleasePullOutcome.Pulled, After: var brokenTip } && brokenTip == m5 && await git.Git(brokenClone, "rev-parse", "master") == m5,
            "a repository that does not compile fails only its own release, reports what its pull really did, and the others are still written; the run says it is incomplete");
        var pmaBuilt = In(built, "MyMeyer--PMA-and-CategoryHierarchy");
        var pmaDll = Path.Combine(ticket, "MyMeyer--PMA-and-CategoryHierarchy", "PartsMatrixAdminV2.dll");
        Check(pmaBuilt.Written && References(pmaDll)["Objects"] == "2.0.0.0" &&
            pmaBuilt.OutsideReferences.Single() is { Name: "Objects.dll", Version: "2.0.0.0", Path: var objectsPath } && objectsPath == Path.Combine(mainClone, @"bin\Objects.dll") &&
            File.ReadAllText(Path.Combine(pmaBuilt.RunDirectory, "build.log")).Contains($"Reference Objects.dll: {Path.Combine(mainClone, @"bin\Objects.dll")}\n"),
            "a HintPath into a sibling repository wins over the stale copies the repository tracks in a Libraries folder and in the project's bin, as in MSBuild");
        var unresolved = $@"Cultivate found 2 references nowhere and left them to MSBuild, as Visual Studio does: Nowhere.dll (HintPath ..\..\DesktopModules\POPv3\bin\Nowhere.dll) " +
            @"for PartsMatrixAdminV2\PartsMatrixAdminV2.csproj; Pkg.dll (HintPath packages\Missing.Package.1.0.0\lib\net471\Pkg.dll) for PartsMatrixAdminV2\PartsMatrixAdminV2.csproj. " +
            "The build fails with a compiler error if the code needs one; this one compiled, so it did not. build.log says where Cultivate looked.";
        var pmaLog = File.ReadAllText(Path.Combine(pmaBuilt.RunDirectory, "build.log"));
        Check(pmaBuilt.Warnings.Contains(unresolved) &&
            pmaLog.Contains($@"Unresolved reference Nowhere.dll for PartsMatrixAdminV2: ..\..\DesktopModules\POPv3\bin\Nowhere.dll was not found. Looked in: " +
                $@"{Path.Combine(github, @"DesktopModules\POPv3\bin\Nowhere.dll")}; the snapshot's PartsMatrixAdminV2\bin\Nowhere.dll; the snapshot copies of the projects it depends on; " +
                $@"{Path.Combine(pmaClone, @"PartsMatrixAdminV2\bin\Nowhere.dll")}. Left to MSBuild.") &&
            pmaLog.Contains($"Package Missing.Package.1.0.0 for PartsMatrixAdminV2 is in neither the snapshot nor your checkout ({Path.Combine(pmaClone, @"PartsMatrixAdminV2\packages\Missing.Package.1.0.0")})") &&
            pmaLog.Contains(@"Unresolved reference Pkg.dll for PartsMatrixAdminV2: packages\Missing.Package.1.0.0\lib\net471\Pkg.dll was not found in version 1.0.0.0."),
            "a reference and a package found nowhere that the code does not use are left to MSBuild: the build succeeds with one warning, and build.log says where Cultivate looked");
        var portal = Path.Combine(ticket, "Public--Applications", "SalesPortal.dll");
        var applicationsBuilt = In(built, "Public--Applications");
        Check(AssemblyName.GetAssemblyName(portal).Version?.ToString() == "1.0.0.0" && !File.ReadAllBytes(portal).SequenceEqual(stale) &&
            References(portal) is var used && new[] { "SalesCore", "Tracked", "Sibling", "Fallback" }.All(used.ContainsKey) &&
            applicationsBuilt.Files.Single(file => file.Name == "SalesPortal.dll").Kind == ReleaseKind.ProjectDll,
            "the released project DLL is this build's, compiled against the support project, never the stale copy of its own output that Git tracks");
        Check(applicationsBuilt.OutsideReferences.Select(reference => (reference.Name, reference.Path, reference.Version)).Order().SequenceEqual([
                ("Fallback.dll", Path.Combine(applicationsClone, @"SalesPortal\bin\Fallback.dll"), (string?)"3.0.0.0"),
                ("Sibling.dll", Path.Combine(mainClone, @"bin\Sibling.dll"), "1.0.0.0")]) &&
            applicationsBuilt.OutsideReferences.All(reference => reference.Project == @"SalesPortal\SalesPortal.csproj"),
            "a sibling repository's DLL reached by HintPath, and one only the checkout's output folder has, are reported as outside references; the tracked bin DLL is not");
        string Text(string name) => File.ReadAllText(Path.Combine(ticket, "SQL", name));
        Check(Text("PMA_ACES_GetPartDataV9.sql") == "select 9 new\r\nline 2\r\n" && Text("PMA_ACES_UpdateExistingPartV28.sql") == "update 28 later\r\n" &&
            Text("UpdateBOAFByMFG.sql") == "-- ByMFG 5415\r\n" && Text(@"StoredProcedures\UpdateBOAFbyMFG.sql") == "-- byMFG 5415\r\n" &&
            In(built, "SQL").Warnings.Contains($"UpdateBOAFByMFG.sql and UpdateBOAFbyMFG.sql differ only by case, so one folder cannot hold both: " +
                @"SqlSource\SqlSource\MeyerLogic\StoredProcedures\UpdateBOAFbyMFG.sql is written as StoredProcedures\UpdateBOAFbyMFG.sql. Check which one production needs; Git tracks both.") &&
            In(built, "SQL").Files.All(file => file.Kind == ReleaseKind.Sql),
            "SQL scripts are copied as the base branch has them with checkout line endings, each case twin with its own content, the second in a subfolder with a warning");
        Check(progress.Contains("Public--Applications: Building SalesCore (1/2, dependency: needed to compile, not released)") &&
            progress.Contains("Public--Applications: Building SalesPortal (2/2, release file)") &&
            progress.Any(line => line.StartsWith($"SQL: Reading 4 SQL scripts from main at {later[..7]}", StringComparison.Ordinal)) &&
            progress.Any(line => line.StartsWith("Broken--App: failed.", StringComparison.Ordinal)),
            "each progress line names the repository subfolder its step belongs to");
        Check(In(built, "MyMeyer").Pull?.Outcome == ReleasePullOutcome.Pulled && In(built, "MyMeyer (PR 11)").Pull?.Outcome == ReleasePullOutcome.UpToDate &&
            applicationsBuilt.Pull?.Outcome == ReleasePullOutcome.Pulled && await git.Git(applicationsClone, "rev-parse", "master") == m190 &&
            In(built, "SQL (Other--SQL)").Pull?.Outcome == ReleasePullOutcome.Pulled &&
            // On Windows a checkout holds one file for two case twins, so the twin looks changed locally and the pull is refused, as Database--SQL's is.
            In(built, "SQL").Pull?.Outcome == ReleasePullOutcome.NotUpdated && In(built, "SQL").Warnings.Any(warning => warning.Contains("UpdateBOAF") && warning.Contains("pull main by hand")),
            "each repository's release branch is pulled by its own release, and one that cannot be pulled only warns");
        Check(ReleaseFolder.Load(Path.Combine(ticket, "SQL")).Files.Count == 4 && ReleaseFolder.Load(Path.Combine(ticket, "MyMeyer")).Files.Single().Name == "View.ascx",
            "each subfolder has its own record of the files Cultivate wrote");
        var card = Shown(Capture(ReleaseScreen.ResultCard(built), 400, 200));
        Check(card.Contains("RELEASE FILES PARTLY WRITTEN") && card.Contains("Wrote 11 files for PR #10: 39126 - Remove price levels and 6 linked pull requests") &&
            card.Contains("One subfolder per repository: MyMeyer (1 file), Public--Applications (2 files), MyMeyer (PR 11) (1 file), MyMeyer--PMA-and-CategoryHierarchy (2 files), " +
                "SQL (4 files), SQL (Other--SQL) (1 file), Broken--App (not written).") && card.Contains("Release these scripts through the database process") &&
            card.Contains("Linked pull requests not built (4):") && card.Contains("error CS0029") && card.Contains("Pulled master: 1 new commit(s)") &&
            card.Contains("Cultivate found 2 references nowhere"),
            "the result of several repositories lists each subfolder with its files or its failure, and the links not built");

        // --clean and the files already there, per subfolder.
        File.WriteAllText(Path.Combine(ticket, "SQL", "hand.sql"), "mine");
        var again = await runner.Preview(mainRepository, 10, new(Clean: true));
        Check(In(again, "SQL").Removed.Order(Files.Comparer).SequenceEqual(["PMA_ACES_GetPartDataV9.sql", "PMA_ACES_UpdateExistingPartV28.sql", @"StoredProcedures\UpdateBOAFbyMFG.sql",
                "UpdateBOAFByMFG.sql"], Files.Comparer) && In(again, "MyMeyer").Removed.SequenceEqual(["View.ascx"]) &&
            In(again, "MyMeyer").Existing.Select(file => file.Name).SequenceEqual(["View.ascx"]) &&
            In(again, "SQL").Existing.Single(file => file.Name == "hand.sql").Origin == "Not written by Cultivate" &&
            In(again, "SQL").Existing.Single(file => file.Name == "UpdateBOAFByMFG.sql").Origin.StartsWith("Written by Cultivate for PR #5415", StringComparison.Ordinal) &&
            In(again, "Broken--App").Removed.Count == 0 && File.Exists(Path.Combine(ticket, "SQL", "UpdateBOAFByMFG.sql")),
            "--clean and the files already in the folder are worked out per subfolder, so one repository's files are never another's");

        // --no-linked, --name, --out and --no-pull.
        var before = asked.Count;
        var alone = await runner.Preview(mainRepository, 10, new(NoLinked: true, NoPull: true));
        Check(alone is { NoLinked: true, Releases.Count: 1, Skipped.Count: 0 } && alone.Folder == ticket && alone.Releases[0].Folder == Path.Combine(ticket, "MyMeyer") &&
            asked.Skip(before).All(line => line.StartsWith("pr view 10 ", StringComparison.Ordinal)) && alone.Releases[0].Pull?.Outcome == ReleasePullOutcome.Off,
            "--no-linked builds only the requested pull request, still in its repository's subfolder");
        var named = await runner.Preview(mainRepository, 10, new(Name: "Hotfix", NoPull: true));
        Check(named.Folder == Path.Combine(output, "Hotfix") && named.Releases.Count == 7 &&
            named.Releases.All(release => release.Name == "Hotfix" && release.Folder == Path.Combine(output, "Hotfix", release.Subfolder) && release.Pull?.Outcome == ReleasePullOutcome.Off),
            "--name names the ticket folder for every repository, and --no-pull applies to every repository");
        var elsewhere = Path.Combine(root, "elsewhere");
        var moved = await runner.Preview(applicationsRepository, 190, new(Folder: elsewhere, NoLinked: true));
        Check(moved.Folder == elsewhere && moved.Releases.Single().Folder == Path.Combine(elsewhere, "Public--Applications") && moved.Requested == $"{owner}/Public--Applications#190",
            "--out replaces the ticket folder, and a pull request of another repository can be the requested one");

        // The command: an SDK-style project is refused without hiding the repository's other files, and a failure sets the exit code.
        var original = Console.Out;
        async Task<(int Code, JsonDocument Json)> Command(params string[] arguments)
        {
            using var captured = new StringWriter();
            Console.SetOut(captured);
            try
            {
                var code = await ReleaseFiles.Command(settings, git, ["release", .. arguments], Gh);
                return (code, JsonDocument.Parse(captured.ToString()));
            }
            finally { Console.SetOut(original); }
        }
        var (sdkCode, sdkJson) = await Command("--pr", Url("MyMeyer--PMA-and-CategoryHierarchy", 239), "--no-pull");
        var sdk = sdkJson.RootElement.GetProperty("Releases")[0];
        Check(sdkCode == 2 && sdkJson.RootElement.GetProperty("Complete").GetBoolean() == false &&
            sdk.GetProperty("Errors").EnumerateArray().Single().GetString() == @"ModernApi\ModernApi.csproj is an SDK-style project, which Build release files does not support. " +
                "Build ModernApi in Visual Studio and release its files by hand; if it is deployed by publishing, publish it instead." &&
            sdk.GetProperty("Content").EnumerateArray().Single().GetString() == "PartsMatrixAdminV2/Controls/Other.ascx" && sdk.GetProperty("Subfolder").GetString() == "MyMeyer--PMA-and-CategoryHierarchy",
            "an SDK-style project gets a clear error, the repository's other files are still listed, and the preview exits 2");
        var commandOut = Path.Combine(root, "command");
        var (failedCode, failedJson) = await Command("--pr", "190", "--repo", "Public--Applications", "--out", commandOut, "--no-pull", "--yes");
        Check(failedCode == 1 && failedJson.RootElement.GetProperty("Written").GetBoolean() &&
            failedJson.RootElement.GetProperty("Releases").EnumerateArray().Select(release => release.GetProperty("Subfolder").GetString()).SequenceEqual(
                ["Public--Applications", "MyMeyer", "MyMeyer--PMA-and-CategoryHierarchy", "MyMeyer (PR 11)", "SQL", "SQL (Other--SQL)", "Broken--App"]) &&
            File.Exists(Path.Combine(commandOut, "Public--Applications", "SalesPortal.dll")) && failedJson.RootElement.GetProperty("Skipped").GetArrayLength() == 4,
            "building from another repository's pull request writes every repository it links, and a repository that failed makes the command exit 1");

        // A tracked copy of the project's own output never passes for the build's. #193 is merged only now: the builds above use master before it.
        await Publish(applications, m193, "refs/heads/master");
        string? untouched = null;
        try { await runner.Build(await runner.Preview(applicationsRepository, 193, new(Folder: Path.Combine(root, "skip"), NoLinked: true, NoPull: true)), _ => { }); }
        catch (InvalidDataException exception) { untouched = exception.Message; }
        Check(untouched?.Contains("was not written by this build of SalesPortal\\SalesPortal.csproj") == true &&
            !Directory.Exists(Path.Combine(root, "skip")), "when the build leaves the stale tracked copy of the project's DLL in its output, nothing is written");

        // The terminal UI: Ctrl+R lists another clone's merged pull requests, and a pasted link names its repository.
        lists[$"{owner}/database--sql".ToLowerInvariant()] = JsonSerializer.Serialize(new[]
        {
            new { number = 5415, title = "39187 - Procedures", author = new { login = "alice" }, headRefName = "39187", mergedAt = "2026-11-19T10:00:00Z" }
        });
        var picker = new ReleasePicker([], [], null, "master", new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), "Public--Applications", canChange: true);
        foreach (var character in Url("MyMeyer--PMA-and-CategoryHierarchy", 238)) picker.HandleKey(new(character, ConsoleKey.A, false, false, false), 7);
        var pasted = Capture(picker.Render(100, 30), 100, 30);
        Check(picker.Selected is { Number: 238, Repository: $"{owner}/MyMeyer--PMA-and-CategoryHierarchy" } && Flat(pasted).Contains("From a link. Enter asks GitHub about") &&
            pasted.Contains("Public--Applications: ") && pasted.Contains("Ctrl+R: repository"),
            "a pasted pull request link is offered with its repository, and the picker names the repository it lists and how to change it");
        foreach (var (width, height) in new[] { (60, 24), (80, 24) })
        {
            var lines = Capture(new ReleasePicker([], [], null, "main", null, "Database--SQL", true).Render(width, height), width, height).Split('\n');
            Check(lines.Length <= height && lines.All(line => line.Length <= width) && lines.Any(line => line.Contains("Ctrl+R")),
                $"the release picker with a repository fits {width}x{height}");
        }
        var titles = new List<string>();
        var offered = new List<string>();
        using (var console = new StringWriter())
        {
            await new ReleaseScreen(settings, git, Gh).Show(LiveConsole(console), Script(new ConsoleKeyInfo('\u0012', ConsoleKey.R, false, false, true), null, Press(ConsoleKey.Escape)),
                (title, actions) =>
                {
                    titles.Add(title);
                    offered.AddRange(actions.Select(action => action.Label));
                    return title == ReleaseScreen.RepositoryQuestion ? "Database--SQL" : ReleaseScreen.Back;
                }, (_, _) => false, () => { }, _ => { });
            var shown = Shown(console.ToString());
            Check(titles.SequenceEqual([ReleaseScreen.RepositoryQuestion]) && offered.Order(StringComparer.Ordinal).SequenceEqual(clones.Select(clone => clone.Name).Order(StringComparer.Ordinal)) &&
                asked.Contains("pr list --repo Meyer-Fixture/Database--SQL --state merged --base main --author @me --limit 50 --json number,title,author,headRefName,mergedAt") &&
                shown.Contains("Database--SQL: Your pull requests merged into main") && shown.Contains("MINE 1"),
                "Ctrl+R offers every clone, and the chosen repository's merged pull requests are listed from its release branch");
        }
    }
}
