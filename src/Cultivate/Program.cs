using System.Text;
using System.Text.Json;

namespace Cultivate;

internal static class Program
{
    private static string Help => $"""
        Cultivate {Files.Version}
        Preview, build, and activate a MyMeyer development version on your dev webfarm.

        Usage: cultivate [command] [options]

          (no command)                            Open the interactive terminal UI
          list                                    List worktrees and branches (JSON)
          status                                  Show the active deployment (JSON)
          plan --worktree PATH | --branch NAME    Preview a deployment plan (JSON)
          activate --worktree PATH | --branch NAME --yes [--once]
                                                  Build and deploy; --once disables automatic updates
          restore --yes                           Stop updates and restore the original webfarm files
          setup                                   Configure interactively, or pass any of:
                [--repository PATH] [--webfarm PATH] [--base-branch NAME]
                [--mapping-assembly PATH] [--msbuild PATH] [--watch true|false]
                [--worktrees PATH]
          update [--check] [--version X.Y.Z] [--yes]
                                                  Install the latest (or a specific) release in place
          config                                  Show the settings file and its values
          doctor                                  Check prerequisites, hooks, and settings
          self-test                               Run fixture-only tests (never touches a real webfarm)
          version                                 Show the installed version

        Settings: {Settings.FilePath}
        """;

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
                    var targets = await git.Targets();
                    var target = targets.FirstOrDefault(x => worktree is not null && x.Worktree is not null && Files.Comparer.Equals(Files.Full(x.Worktree), Files.Full(worktree)) ||
                        worktree is null && reference is not null && x.Worktree is null && (x.Branch == reference || x.Ref == reference))
                        ?? throw new InvalidOperationException("Select a known --worktree <absolute path> or --branch <name>. Use list to see choices.");
                    var plan = await new Planner(settings, git, bridge).Create(target);
                    if (args[0] == "plan") { Print(plan); return plan.CanDeploy ? 0 : 2; }
                    if (!args.Contains("--yes")) throw new InvalidOperationException("Activation requires --yes or an interactive confirmation.");
                    Print(await new Pipeline(settings, git, bridge).Activate(plan, message => Console.Error.WriteLine(message), !args.Contains("--once") && settings.WatchSavedFiles));
                    break;
                case "restore":
                    if (!args.Contains("--yes")) throw new InvalidOperationException("Restoration requires --yes or the interactive menu.");
                    Print(await bridge.Call("Restore"));
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
