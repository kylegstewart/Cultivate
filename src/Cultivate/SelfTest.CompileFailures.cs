using System.Text.Json.Nodes;

namespace Cultivate;

internal static partial class SelfTest
{
    private const string FixHint = "Fix the compiler errors at the listed source locations";

    // Mirrors a project already broken on master: a partial designer class added beside a code-behind class that is not partial.
    private static async Task CheckCompileFailures(string home, string msbuild)
    {
        var root = Path.Combine(home, "inherited");
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(root, "repository"), WebfarmRoot = Path.Combine(root, "farm"),
            StateRoot = Path.Combine(root, "state"), MSBuild = msbuild
        };
        Directory.CreateDirectory(settings.RepositoryRoot);
        Directory.CreateDirectory(settings.WebfarmRoot);
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var planner = new Planner(settings, git, bridge);
        var pipeline = new Pipeline(settings, git, bridge);
        async Task G(params string[] args) => _ = await git.Git(settings.RepositoryRoot, args);
        void Save(string path, string content)
        {
            var full = Files.Child(settings.RepositoryRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Task<Dictionary<string, string>> Maps(string _) => Task.FromResult(new Dictionary<string, string>(Files.Comparer) { ["View.ascx"] = "modules" });
        const string link = "public class LegacyLink { public static string Url() { return \"http://example.invalid\"; } }";
        const string secure = "public class LegacyLink { public static string Url() { return \"https://example.invalid\"; } }";
        const string unchangedNote = @"Not from your changes: Legacy\Value.cs line 3 is unchanged from master.";
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        Save(".gitignore", "bin/\nobj/\n");
        Save("MyMeyer.sln", "");
        Save(@"Legacy\Legacy.csproj", Project("Legacy").Replace("<Compile Include=\"Value.cs\" />", "<Compile Include=\"Value.cs\" /><Compile Include=\"Value.designer.cs\" />"));
        Save(@"Legacy\App.config", "<configuration />");
        Save(@"Legacy\Value.cs", link + "\n\npublic class LegacyView { }\n");
        Save(@"Legacy\Value.designer.cs", "public partial class LegacyView { }\n");
        Save(@"Legacy\View.ascx", "http markup");
        Save(@"Healthy\Healthy.csproj", Project("Healthy"));
        Save(@"Healthy\App.config", "<configuration />");
        Save(@"Healthy\Value.cs", "public class HealthyValue { public static int Value => 1; }\n");
        await G("add", ".");
        await G("commit", "-qm", "a designer file made Legacy's class partial in one file only");
        async Task Branch(string name, params (string Path, string Content)[] files)
        {
            await G("checkout", "-qb", name, "master");
            foreach (var (path, content) in files) Save(path, content);
            await G("add", ".");
            await G("commit", "-qm", name);
        }
        await Branch("inherited", (@"Legacy\Value.cs", secure + "\n\npublic class LegacyView { }\n"),
            (@"Healthy\Value.cs", "public class HealthyValue { public static int Value => 2; }\n"), (@"Legacy\View.ascx", "https markup"));
        await Branch("alone", (@"Legacy\Value.cs", secure + "\n\npublic class LegacyView { }\n"));
        await Branch("introduced", (@"Healthy\Value.cs", "public class HealthyValue { public static int Value => missingHealthy; }\n"),
            (@"Legacy\View.ascx", "https markup"));
        await Branch("cli", (@"Legacy\Value.cs", secure + "\n\npublic class LegacyView { }\n"),
            (@"Healthy\Value.cs", "public class HealthyValue { public static int Value => 2; }\n"));
        await G("checkout", "-qb", "worktree", "master");

        async Task<(Plan Plan, Exception Failure, FailureReport Report)> Fail(Target target, string message)
        {
            var plan = await planner.Create(target, Maps);
            Exception? failure = null;
            await Reject(async () =>
            {
                try { await pipeline.Activate(plan, _ => { }, watch: false); }
                catch (InvalidOperationException exception) { failure = exception; throw; }
            }, message);
            return (plan, failure!, Diagnostics.Record(failure!, "Activation", Path.Combine(root, "logs")));
        }

        var committed = new Target("inherited", null, "refs/heads/inherited");
        var (plan, failure, report) = await Fail(committed, "a project that already fails on master stops the committed branch's activation");
        var skip = SkipOffer.Of(plan, report);
        Check(report.Label == "BUILD FAILED" && report.Context.Project == @"Legacy\Legacy.csproj" && report.Errors.Any(error => error.Contains("CS0260")) &&
            report.Origins.SequenceEqual([unchangedNote]) && report.Unchanged,
            "an error on a line the committed branch did not change is reported as not from its changes");
        Check(!report.Hints.Any(hint => hint.StartsWith(FixHint)) &&
            report.Hints.Any(hint => hint.Contains(@"Legacy\Legacy.csproj probably does not compile on master either") && hint.Contains("did not build master")) &&
            report.Hints.Any(hint => hint.Contains("--exclude Legacy/Value.cs to cultivate plan/activate")),
            "the report drops the fix-it-here hint, says the project probably fails on master, and names the exact exclusion");
        var logged = File.ReadAllText(Path.Combine(root, "logs", "last-error.log"));
        Check(logged.Contains("  " + unchangedNote) && logged.Contains("--exclude Legacy/Value.cs"), "last-error.log carries the same explanation");
        Console.WriteLine("Fixture failure report:\n" + report.Text);
        Check(skip is { CodeFiles: 1, Unchanged: true } && skip.Exclusions.SequenceEqual(["Legacy/Value.cs"]) && skip.Project.Assembly == "Legacy",
            "a single project's compile failure with other work in the plan is offered to deploy without that project");
        var offer = skip ?? throw new InvalidOperationException("TEST FAILED: no offer to deploy without Legacy");

        using (var output = new StringWriter())
        {
            var chosen = new ActionPicker(SkipOffer.Actions).Show(offer.Title, LiveConsole(output), Script(Press(ConsoleKey.Enter, '\r')));
            var shown = Shown(output.ToString());
            Check(chosen.Label == "No" && shown.Contains("Deploy the rest without Legacy?") && shown.Contains("Its errors are not from your changes.") &&
                shown.Contains("Legacy.dll on the webfarm stays as it is") && shown.Contains("markup could run against that old DLL"),
                "the offer explains the consequences and defaults to No");
        }
        using (var output = new StringWriter())
            Check(new ActionPicker(SkipOffer.Actions).Show(offer.Title, LiveConsole(output), Script(Press(ConsoleKey.DownArrow), Press(ConsoleKey.Enter, '\r'))).Label == SkipOffer.Accept,
                "the offer's other choice leaves the project out");
        var without = await offer.Plan(planner, committed, [], Maps);
        Check(without is not null && without.CanDeploy && without.ExcludedPaths.SequenceEqual(["Legacy/Value.cs"]) &&
            without.Projects.Select(project => project.Assembly).SequenceEqual(["Healthy"]) && without.Frontend.Single().Source == "Legacy/View.ascx" &&
            !Terminal.ActionableChanges(without).Any(item => item.Action.Contains("Legacy.dll")),
            "accepting re-plans with exactly the project's changed code excluded: one DLL build fewer, its frontend still deploys");
        Directory.CreateDirectory(Path.Combine(settings.WebfarmRoot, "bin"));
        File.WriteAllText(Path.Combine(settings.WebfarmRoot, @"bin\Legacy.dll"), "webfarm legacy dll");
        try
        {
            var state = await pipeline.Activate(without ?? throw new InvalidOperationException("TEST FAILED: no plan without Legacy"), _ => { }, watch: false);
            Check(state.ExcludedPaths.SequenceEqual(["Legacy/Value.cs"]) &&
                state.Updated.Select(file => (file.RelativePath, file.Change)).SequenceEqual([(@"bin\Healthy.dll", UpdatedFile.Built), (@"modules\View.ascx", UpdatedFile.Synced)]) &&
                File.ReadAllText(Path.Combine(settings.WebfarmRoot, @"bin\Legacy.dll")) == "webfarm legacy dll",
                "confirming deploys the rest, records the exclusions for automatic updates, and leaves the project's DLL on the webfarm alone");
        }
        finally { await bridge.Call("Restore"); }

        var original = Diagnostics.Describe(failure).Text;
        var broken = new Plan
        {
            Target = plan.Target, Commit = plan.Commit, Fingerprint = plan.Fingerprint, Baseline = new string('0', 40),
            Changes = plan.Changes, Projects = plan.Projects, Frontend = plan.Frontend
        };
        var unexplained = new InvalidOperationException("MSBuild.exe failed (1).\n" + Path.Combine(settings.RepositoryRoot, @"Legacy\Value.cs") +
            "(3,14): error CS0260: Missing partial modifier on declaration of type 'LegacyView'; another partial declaration of this type exists");
        Diagnostics.Attach(unexplained, new("Build") { Project = @"Legacy\Legacy.csproj", Snapshot = settings.RepositoryRoot, ExitCode = 1, Tool = "MSBuild.exe" });
        var before = Diagnostics.Describe(unexplained).Text;
        await pipeline.Explain(unexplained, broken);
        var after = Diagnostics.Describe(unexplained);
        Check(after.Text == before && after.Origins.Count == 0 && after.Hints.Any(hint => hint.StartsWith(FixHint)) && original.Contains(unchangedNote),
            "when Git cannot compare the lines, the original report stays exactly as it was");
        var restore = new InvalidOperationException("MSBuild.exe failed (1).\nLegacy.csproj : error NU1101: Unable to find package Missing.");
        Diagnostics.Attach(restore, new("Package restore") { Project = @"Legacy\Legacy.csproj", Snapshot = settings.RepositoryRoot, ExitCode = 1 });
        await pipeline.Explain(restore, plan);
        Check(SkipOffer.Of(plan, Diagnostics.Describe(restore)) is null && Diagnostics.Describe(restore).Origins.Count == 0,
            "a restore failure is not offered as a project to leave out");

        (plan, _, report) = await Fail(new Target("alone", null, "refs/heads/alone"), "the broken project alone fails too");
        Check(report.Origins.SequenceEqual([unchangedNote]) && SkipOffer.Of(plan, report) is null && !report.Hints.Any(hint => hint.Contains("--exclude")),
            "when the failed project is the whole plan there is nothing else to deploy, so no offer or exclusion is suggested");

        (plan, _, report) = await Fail(new Target("introduced", null, "refs/heads/introduced"), "an error the branch introduces stops the activation");
        Check(report.Context.Project == @"Healthy\Healthy.csproj" && report.Errors.Any(error => error.Contains("CS0103")) &&
            report.Origins.Count == 0 && !report.Unchanged && report.Hints.Any(hint => hint.StartsWith(FixHint)) && !report.Text.Contains("Not from your changes"),
            "an error on a line the branch changed keeps the fix-it hint and is not called someone else's");
        Check(SkipOffer.Of(plan, report) is { Unchanged: false } introduced && introduced.Exclusions.SequenceEqual(["Healthy/Value.cs"]),
            "any single project's compile failure is offered, without claiming its errors are not from your changes");

        var worktree = new Target("worktree", settings.RepositoryRoot, "refs/heads/worktree");
        Save(@"Legacy\Value.cs", secure + "\n\npublic class LegacyView { }\n");
        Save(@"Healthy\Value.cs", "public class HealthyValue { public static int Value => 3; }\n");
        (plan, _, report) = await Fail(worktree, "a worktree's unsaved-to-Git edits hit the same broken project");
        Check(report.Origins.SequenceEqual([unchangedNote]) && report.Unchanged && SkipOffer.Of(plan, report)?.Exclusions.SequenceEqual(["Legacy/Value.cs"]) == true,
            "a worktree's saved but uncommitted edits are compared line by line too");
        Save(@"Legacy\Value.cs", "public class LegacyLink { public static int Url; public static int Url; }\n\npublic class LegacyView { }\n");
        (plan, _, report) = await Fail(worktree, "a worktree with its own error and the inherited one fails");
        Check(report.Errors.Any(error => error.Contains("CS0102")) && report.Errors.Any(error => error.Contains("CS0260")) &&
            report.Origins.SequenceEqual([unchangedNote]) && !report.Unchanged && report.Hints.Any(hint => hint.StartsWith(FixHint)) &&
            !report.Hints.Any(hint => hint.Contains("probably does not compile")),
            "with errors on changed and unchanged lines, only the unchanged one is marked and the fix-it hint stays");
        await G("checkout", "--", ".");

        var cli = new Target("cli", null, "refs/heads/cli");
        var expected = await planner.Create(cli, excludedPaths: ["Legacy/Value.cs"]);
        var settingsFile = Path.Combine(root, "settings.json");
        Files.WriteJson(settingsFile, settings);
        async Task<(int Code, string Output, string Error)> Command(params string[] args)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            var (savedOut, savedError) = (Console.Out, Console.Error);
            Console.SetOut(output);
            Console.SetError(error);
            try { return (await Program.Main([.. args, "--settings", settingsFile]), output.ToString(), error.ToString()); }
            finally
            {
                Console.SetOut(savedOut);
                Console.SetError(savedError);
            }
        }
        var (code, json, _) = await Command("plan", "--branch", "cli", "--exclude", @"legacy\value.cs");
        var printed = JsonNode.Parse(json)!;
        Check(code == 0 && printed["ExcludedPaths"]!.AsArray().Select(path => path!.ToString()).SequenceEqual(expected.ExcludedPaths) &&
            printed["Projects"]!.AsArray().Select(project => project!["Assembly"]!.ToString()).SequenceEqual(["Healthy"]) &&
            printed["BuildFingerprint"]!.ToString() == expected.BuildFingerprint,
            "cultivate plan --exclude plans exactly what the terminal UI's exclusions plan");
        var (refused, _, message) = await Command("plan", "--branch", "cli", "--exclude", "Legacy/Missing.cs");
        Check(refused == 1 && message.Contains("--exclude names a file cli did not change: Legacy/Missing.cs"), "cultivate plan refuses an exclusion that is not a changed file");
        (refused, _, message) = await Command("activate", "--branch", "cli", "--exclude", "Legacy/Missing.cs", "--yes", "--once", "--no-warmup");
        Check(refused == 1 && message.Contains("--exclude names a file cli did not change") && (await bridge.Status())?.Active != true,
            "cultivate activate refuses an unknown exclusion before deploying anything");
        (refused, _, message) = await Command("plan", "--branch", "cli", "--exclude");
        Check(refused == 1 && message.Contains("--exclude needs a repository-relative path"), "--exclude without a path is refused");
    }
}
