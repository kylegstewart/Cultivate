using System.Text;
using System.Text.Json;

namespace Cultivate;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Directory.CreateDirectory(Files.Home);
        try
        {
            if (args.FirstOrDefault() == "self-test") return await SelfTest.Run();
            string? Option(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
            var settingsPath = Option("--settings");
            var settings = settingsPath is null ? await Settings.Load() :
                JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(settingsPath), Files.Json) ?? throw new InvalidDataException("Invalid worker settings.");
            var git = new GitRepository(settings);
            var bridge = new Bridge(settings);
            void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
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
                case "watch": await Watcher.Run(settings, Option("--token") ?? throw new InvalidOperationException("Worker token is required.")); break;
                case "--help":
                case "help":
                    Console.WriteLine("Cultivate\n  (no arguments)  Interactive version picker\n  list | status  Read-only JSON\n  plan --worktree PATH | --branch NAME\n  activate --worktree PATH | --branch NAME --yes [--once]\n  restore --yes\n  self-test");
                    break;
                case null: await new Terminal(settings, git, bridge).Run(); break;
                default: throw new InvalidOperationException("Unknown command. Use Cultivate --help.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var log = Path.Combine(Files.Home, "Logs", "last-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            await File.WriteAllTextAsync(log, exception.ToString());
            if (args.FirstOrDefault() != "watch") Console.Error.WriteLine($"Cultivate: {exception.Message}\nDetails: {log}");
            if (args.Length == 0 && !Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Press any key to close.");
                Console.ReadKey(true);
            }
            return 1;
        }
    }
}
