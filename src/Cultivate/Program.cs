using System.Text;
using System.Text.Json;

namespace Cultivate;

internal static class Program
{
    internal static string Help => $"""
        Cultivate {Files.Version}
        Preview, build, and activate a MyMeyer development version on your dev webfarm.

        Usage: cultivate [command] [options]

          (no command)                            Open the interactive terminal UI
          list                                    List worktrees and branches (JSON)
          status                                  Show the active deployment (JSON)
          plan --worktree PATH | --branch NAME    Preview a deployment plan (JSON)
          activate --worktree PATH | --branch NAME --yes [--once] [--no-warmup] [--stop-legacy-watcher]
                                                  Build and deploy; --once disables automatic updates;
                                                  --stop-legacy-watcher ends the old WebfarmFileSync watcher first
              plan | activate --pr NUMBER ...     Use a pull request instead: its current head is fetched
                                                  from origin, with no branch or worktree created
              plan | activate ... [--library PATH]... [--libraries]
                                                  Also build and deploy a matching library worktree
                                                  (repeatable), or every detected match
              plan | activate ... [--exclude PATH]...
                                                  Leave a changed file (repository-relative, repeatable) out
                                                  of this deployment and its automatic updates, like the
                                                  terminal UI's Choose files to deploy; an excluded build
                                                  input is built at its baseline contents
          restore --yes                           Stop updates and restore the original webfarm files
          release --pr NUMBER [--yes] [--name NAME] [--out FOLDER] [--clean] [--reuse-dependencies] [--no-pull]
                                                  Build a merged pull request's release files from master into
                                                  a folder named after its ticket (JSON); without --yes only
                                                  previews. Building also pulls master, fast-forward only, when
                                                  Git can without touching local changes; --no-pull leaves it.
                                                  --clean first removes files an earlier release wrote there;
                                                  --reuse-dependencies compiles against your checkout's DLLs
                                                  instead of building unchanged dependencies
          online-release [--yes] [--allow-local-changes]
                                                  Build the Meyer Online web package (JSON): switch the MeyerOnline
                                                  checkout to master and pull it (fast-forward only), publish
                                                  Meyer.Online.Server with FolderProfile, npm run build-prod, copy
                                                  www in as wwwroot, and zip it to ReleaseRoot\MORelease\MORelease.zip;
                                                  without --yes only previews. Modified tracked files stop it unless
                                                  --allow-local-changes
          warmup [--url URL] [--timeout SECONDS] [--json]
                                                  Request the dev site until it answers and report errors
          tickets                                 List each ticket's worktrees across repositories (JSON)
          worktrees                               List every worktree with its size and age (JSON)
          cleanup [--yes] [--worktrees] [--days N]
                                                  List Cultivate's old files and worktrees unused for N days
                                                  that lose nothing when deleted (JSON); --yes removes the files,
                                                  and with --worktrees also those worktrees (branches are kept)
          setup                                   Configure interactively, or pass any of:
                [--repository PATH] [--webfarm PATH] [--base-branch NAME]
                [--mapping-assembly PATH] [--msbuild PATH] [--watch true|false]
                [--worktrees PATH] [--site-url URL] [--warmup true|false]
                [--sql-mirror PATH] [--stop-legacy-watcher true|false]
                [--library-repositories PATH;PATH|none] [--cleanup-days N] [--release-root PATH]
                [--meyeronline PATH]
          update [--check] [--version X.Y.Z] [--yes]
                                                  Install the latest (or a specific) release in place
          config                                  Show the settings file and its values
          doctor                                  Check prerequisites, hooks, and settings
          mcp [--read-only]                       Serve Cultivate's tools to coding agents over MCP (stdio);
                                                  --read-only leaves out activate and restore
          self-test                               Run fixture-only tests (never touches a real webfarm)
          version                                 Show the installed version

        Settings: {Settings.FilePath}
        """;

    /// <summary>Every <c>--exclude PATH</c> of a plan or activate command.</summary>
    internal static List<string> Exclusions(string[] args)
    {
        var paths = new List<string>();
        for (var index = 0; index < args.Length; index++)
            if (args[index] == "--exclude")
                paths.Add(index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++index] : throw new InvalidOperationException("--exclude needs a repository-relative path."));
        return paths;
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Directory.CreateDirectory(Files.Home);
        if (Updater.IsInstalled) Updater.CleanupStaleFiles();
        try
        {
            void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
            // Commands that work before Cultivate is configured.
            switch (args.FirstOrDefault())
            {
                case "self-test": return await SelfTest.Run();
                case "--version": case "-v": case "version":
                    Console.WriteLine($"Cultivate {Files.Version}");
                    return 0;
                case "--help": case "-h": case "help": case "/?":
                    Console.WriteLine(Help);
                    return 0;
                case "doctor": return await Doctor.Run();
                case "mcp": return await McpServer.Run(args.Contains("--read-only"));
                case "setup": return await Setup.Run(args);
                case "update": return await Updater.Run(args);
                case "update-apply": return Updater.ApplyCommand(args);
                case "config":
                    Console.WriteLine(Settings.FilePath);
                    Print(await Settings.Read());
                    return 0;
            }
            string? Option(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
            var settingsPath = Option("--settings");
            Settings settings;
            if (settingsPath is not null)
            {
                settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(settingsPath), Files.Json) ?? throw new InvalidDataException("Invalid worker settings.");
                await settings.FillDefaults();
            }
            else if (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected &&
                     await Settings.Read() is var draft && draft.Problems().Count != 0)
                settings = await Setup.Interactive(draft, null, firstRun: !File.Exists(Settings.FilePath));
            else settings = await Settings.Load();
            var git = new GitRepository(settings);
            var bridge = new Bridge(settings);
            switch (args.FirstOrDefault())
            {
                case "list": Print(await git.Targets()); break;
                case "status": Print(await bridge.Status()); break;
                case "plan":
                case "activate":
                    var worktree = Option("--worktree");
                    var reference = Option("--branch");
                    var pullRequest = Option("--pr");
                    var targets = pullRequest is null ? await git.Targets() : [];
                    var target = pullRequest is not null && worktree is null && reference is null
                        ? await PullRequests.Fetch(settings, git, PullRequests.Number(pullRequest), await bridge.Status()) :
                        targets.FirstOrDefault(x => worktree is not null && x.Worktree is not null && Files.Comparer.Equals(Files.Full(x.Worktree), Files.Full(worktree)) ||
                        worktree is null && reference is not null && x.Worktree is null && (x.Branch == reference || x.Ref == reference))
                        ?? throw new InvalidOperationException("Select one known --worktree <absolute path>, --branch <name>, or --pr <number>. Use list to see choices.");
                    var exclude = Exclusions(args);
                    var plan = await new Planner(settings, git, bridge).Create(target, excludedPaths: exclude, libraries: LibraryLoop.Requested(args));
                    if (Planner.UnknownExclusions(plan, exclude) is { Count: > 0 } unknown)
                        throw new InvalidOperationException($"--exclude names a file {target.Branch} did not change: {string.Join(", ", unknown)}. " +
                            "Pass repository-relative paths from the plan's changes (cultivate plan lists them).");
                    if (args[0] == "plan") { Print(plan); return plan.CanDeploy ? 0 : 2; }
                    if (!args.Contains("--yes")) throw new InvalidOperationException("Activation requires --yes or an interactive confirmation.");
                    if (LegacyWatcher.ForCommand(settings, args.Contains("--stop-legacy-watcher"), LegacyWatcher.Running(settings.WebfarmRoot)) is { } stopped)
                        Console.Error.WriteLine(stopped);
                    var activated = await new Pipeline(settings, git, bridge).Activate(plan, message => Console.Error.WriteLine(message), !args.Contains("--once") && settings.WatchSavedFiles);
                    await Warmup.AfterActivationCommand(settings, activated, args);
                    Print(activated);
                    break;
                case "warmup": return await Warmup.Command(settings, args);
                case "tickets": return await TicketWorkspaces.Command(settings, git, args);
                case "worktrees": return await WorktreeTidy.Command(settings, git, args);
                case "cleanup": return await CleanupCommand.Run(settings, git, bridge, args);
                case "release": return await ReleaseFiles.Command(settings, git, args);
                case "online-release": return await OnlineRelease.Command(settings, git, args);
                case "restore":
                    if (!args.Contains("--yes")) throw new InvalidOperationException("Restoration requires --yes or the interactive menu.");
                    Print(await bridge.Call("Restore"));
                    await PullRequests.Prune(git, settings, null);
                    break;
                case "watch": await Watcher.Run(settings, Option("--token") ?? throw new InvalidOperationException("Worker token is required."), settingsPath); break;
                case null: await new Terminal(settings, git, bridge).Run(); break;
                default: throw new InvalidOperationException($"Unknown command '{args[0]}'. Run cultivate --help.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var report = Diagnostics.Record(exception, args.FirstOrDefault() ?? "Startup");
            if (args.FirstOrDefault() != "watch") Console.Error.WriteLine($"Cultivate: {report.Label}\n{report.Text}");
            if (args.Length == 0 && !Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Press any key to close.");
                Console.ReadKey(true);
            }
            return 1;
        }
    }
}
