using Spectre.Console;

namespace Cultivate;

/// <summary>First-run and later configuration of settings.json, interactively (see <see cref="SettingsScreen"/>) or from command-line options.</summary>
internal static class Setup
{
    /// <summary><c>cultivate setup [--repository PATH] [--webfarm PATH] ...</c></summary>
    public static async Task<int> Run(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"{name} needs a value.");
            var value = args[index + 1].Trim().Trim('"');
            if (name is not ("--base-branch" or "--watch" or "--site-url" or "--warmup" or "--stop-legacy-watcher" or "--library-repositories" or "--cleanup-days") &&
                !Path.IsPathFullyQualified(value))
                throw new InvalidOperationException($"{name} needs an absolute path, not '{value}'.");
            return value;
        }
        var settings = await Settings.Read();
        var options = new[] { "--repository", "--webfarm", "--base-branch", "--mapping-assembly", "--msbuild", "--watch", "--worktrees",
            "--site-url", "--warmup", "--sql-mirror", "--stop-legacy-watcher", "--library-repositories", "--cleanup-days", "--release-root", "--meyeronline" };
        if (!options.Any(args.Contains))
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("Run setup in an interactive terminal, or pass options. See cultivate --help.");
            var current = await ActiveDeployment(settings);
            if (File.Exists(Settings.FilePath)) new SettingsScreen(settings, current).Show();
            else Guided(settings, current, firstRun: true);
            return 0;
        }
        var active = await ActiveDeployment(settings);
        var previousFarm = settings.WebfarmRoot;
        var previousRepository = settings.RepositoryRoot;
        if (Option("--repository") is { } repository) settings.RepositoryRoot = Files.Full(repository);
        if (Option("--webfarm") is { } webfarm) settings.WebfarmRoot = Files.Full(webfarm);
        if (Option("--base-branch") is { } branch) settings.BaseBranch = branch;
        if (Option("--msbuild") is { } msbuild) settings.MSBuild = Files.Full(msbuild);
        if (Option("--worktrees") is { } worktrees) settings.WorktreesRoot = Files.Full(worktrees);
        if (Option("--watch") is { } watch)
            settings.WatchSavedFiles = bool.TryParse(watch, out var value) ? value : throw new InvalidOperationException("--watch expects true or false.");
        if (Option("--mapping-assembly") is { } mapping) settings.MappingAssembly = Files.Full(mapping);
        else if (!Files.Comparer.Equals(previousRepository, settings.RepositoryRoot)) settings.MappingAssembly = DefaultMapping(settings.RepositoryRoot);
        if (Option("--site-url") is { } site) settings.SiteUrl = site;
        else if (!Files.Comparer.Equals(previousFarm, settings.WebfarmRoot)) settings.SiteUrl = Warmup.SuggestSiteUrl(settings.WebfarmRoot);
        if (Option("--warmup") is { } warmup)
            settings.WarmUpAfterDeploy = bool.TryParse(warmup, out var enabled) ? enabled : throw new InvalidOperationException("--warmup expects true or false.");
        if (Option("--sql-mirror") is { } mirror) settings.SqlMirrorRoot = Files.Full(mirror);
        if (Option("--meyeronline") is { } online) settings.MeyerOnlineRoot = Files.Full(online);
        if (Option("--stop-legacy-watcher") is { } stopLegacy)
            settings.StopLegacyWatcher = bool.TryParse(stopLegacy, out var stop) ? stop : throw new InvalidOperationException("--stop-legacy-watcher expects true or false.");
        if (Option("--library-repositories") is { } libraries) settings.LibraryRepositories = LibraryLoop.ParseRepositories(libraries);
        if (Option("--cleanup-days") is { } cleanupDays)
            settings.CleanupDays = int.TryParse(cleanupDays, out var days) && days >= 1 ? days : throw new InvalidOperationException("--cleanup-days expects a whole number of days, 1 or more.");
        if (Option("--release-root") is { } releaseRoot) settings.ReleaseRoot = Files.Full(releaseRoot);
        GuardActive(active, previousFarm, settings.WebfarmRoot);
        var problems = settings.Problems();
        if (problems.Count != 0) throw new InvalidOperationException("Settings were not saved:\n  " + string.Join("\n  ", problems));
        settings.Save();
        Console.WriteLine($"Saved {Settings.FilePath}");
        return 0;
    }

    /// <summary>
    /// The guided questions, for a first run or settings that need fixing before Cultivate can start. Saves to <paramref name="path"/>
    /// (normally the settings file) and gives <paramref name="settings"/> the answers only once the last question is answered; returns
    /// null, with nothing saved or changed, when Esc leaves from the first question.
    /// </summary>
    internal static Settings? Guided(Settings settings, DeploymentState? active, bool firstRun,
        IAnsiConsole? console = null, Func<int, ConsoleKeyInfo?>? readKey = null, string? path = null)
    {
        console ??= AnsiConsole.Console;
        readKey ??= KeyScreen.ReadKey;
        path ??= Settings.FilePath;
        var screen = new FirstRunScreen(settings, active, firstRun, path);
        KeyScreen.Show(console, screen.Render, (key, _) => screen.HandleKey(key), readKey);
        if (!screen.Completed)
        {
            console.MarkupLine($"[{TerminalStyle.Muted}]Setup was left. Nothing was saved.[/]");
            return null;
        }
        var problems = screen.Draft.Problems();
        if (problems.Count != 0) throw new InvalidOperationException("Settings were not saved:\n" + string.Join("\n", problems));
        Files.WriteJson(path, screen.Draft);
        SettingField.Apply(screen.Draft, settings);
        console.MarkupLine($"[{TerminalStyle.Accent}]Saved.[/] [{TerminalStyle.Muted}]Run `cultivate doctor` any time to check prerequisites.[/]");
        if (firstRun)
        {
            console.MarkupLine($"[{TerminalStyle.Gold}]Press any key[/] [{TerminalStyle.Muted}]to continue[/]");
            while (readKey(200) is null) { }
        }
        return settings;
    }

    internal static string DefaultMapping(string repository) =>
        repository.Length == 0 ? "" : Path.Combine(repository, "bin", "MeyerObjects.dll");

    internal const string ActiveMoved = "Restore the active deployment before changing its destination.";

    /// <summary>Whether changing the webfarm root from <paramref name="previousFarm"/> to <paramref name="farm"/> would move an active deployment's destination.</summary>
    internal static bool MovesActive(DeploymentState? active, string previousFarm, string farm) =>
        active?.Active == true && previousFarm.Length != 0 && !Files.Comparer.Equals(Files.Full(previousFarm), Files.Full(farm));

    private static void GuardActive(DeploymentState? active, string previousFarm, string farm)
    {
        if (MovesActive(active, previousFarm, farm)) throw new InvalidOperationException(ActiveMoved);
    }

    private static async Task<DeploymentState?> ActiveDeployment(Settings settings)
    {
        if (settings.WebfarmRoot.Length == 0 || !Path.IsPathFullyQualified(settings.WebfarmRoot) || !Path.IsPathFullyQualified(settings.StateRoot))
            return null;
        try { return await new Bridge(settings).Status(); }
        catch (InvalidOperationException) { return null; }
    }
}
