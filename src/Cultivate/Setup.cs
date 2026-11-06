using Spectre.Console;

namespace Cultivate;

/// <summary>First-run and later configuration of settings.json, interactively or from command-line options.</summary>
internal static class Setup
{
    private static string Escape(string value) => Markup.Escape(value);

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
            if (name is not ("--base-branch" or "--watch") && !Path.IsPathFullyQualified(value))
                throw new InvalidOperationException($"{name} needs an absolute path, not '{value}'.");
            return value;
        }
        var settings = await Settings.Read();
        var options = new[] { "--repository", "--webfarm", "--base-branch", "--mapping-assembly", "--msbuild", "--watch", "--worktrees" };
        if (!options.Any(args.Contains))
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("Run setup in an interactive terminal, or pass options. See cultivate --help.");
            await Interactive(settings, await ActiveDeployment(settings), firstRun: !File.Exists(Settings.FilePath));
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
        GuardActive(active, previousFarm, settings.WebfarmRoot);
        var problems = settings.Problems();
        if (problems.Count != 0) throw new InvalidOperationException("Settings were not saved:\n  " + string.Join("\n  ", problems));
        settings.Save();
        Console.WriteLine($"Saved {Settings.FilePath}");
        return 0;
    }

    /// <summary>Prompts for each setting (Enter keeps the current value), validates, and saves in place.</summary>
    public static async Task<Settings> Interactive(Settings settings, DeploymentState? active, bool firstRun = false)
    {
        AnsiConsole.Clear();
        // The prompts below take about 14 lines; on a shorter terminal the compact header leaves them more room.
        AnsiConsole.Write(new Banner(firstRun ? "WELCOME" : "SETTINGS",
            firstRun ? "Let's connect Cultivate to your MyMeyer checkout and dev webfarm." : "Press Enter to keep a value.", 14));
        AnsiConsole.MarkupLine($"[{TerminalStyle.Muted}]Saved to {Escape(Settings.FilePath)}[/]\n");
        var previousFarm = settings.WebfarmRoot;
        var previousRepository = settings.RepositoryRoot;

        settings.RepositoryRoot = Files.Full(Ask("Normal [bold]MyMeyer checkout[/] (folder containing MyMeyer.sln)", settings.RepositoryRoot,
            value => !Path.IsPathFullyQualified(value) ? "Enter an absolute path." :
                File.Exists(Path.Combine(value, "MyMeyer.sln")) ? null : "MyMeyer.sln was not found in that folder."));
        settings.BaseBranch = Ask("Baseline [bold]branch[/] that changes are compared against", settings.BaseBranch, _ => null);
        settings.WebfarmRoot = Files.Full(Ask(@"Dev [bold]webfarm root[/] (for example \\webfarm-dev02\C$\inetpub\MyMeyer)", settings.WebfarmRoot,
            value => Path.IsPathFullyQualified(value) ? null : "Enter an absolute path or UNC share."));
        GuardActive(active, previousFarm, settings.WebfarmRoot);
        if (!Directory.Exists(settings.WebfarmRoot))
            AnsiConsole.MarkupLine($"[{TerminalStyle.Gold}]Not reachable right now. Saved anyway; check your VPN/share access before deploying.[/]");

        if (!Files.Comparer.Equals(previousRepository, settings.RepositoryRoot) || settings.MappingAssembly.Length == 0)
            settings.MappingAssembly = DefaultMapping(settings.RepositoryRoot);
        settings.MappingAssembly = Files.Full(Ask("Code Release Manager [bold]mapping assembly[/] (MeyerObjects.dll)", settings.MappingAssembly,
            value => Path.IsPathFullyQualified(value) ? null : "Enter an absolute path."));
        if (!File.Exists(settings.MappingAssembly))
            AnsiConsole.MarkupLine($"[{TerminalStyle.Gold}]Not built yet. Frontend deployments need it; build MeyerObjects in your normal checkout.[/]");

        settings.MSBuild = Files.Full(Ask("Visual Studio [bold]MSBuild.exe[/]", settings.MSBuild.Length != 0 ? settings.MSBuild : await Settings.FindMSBuild() ?? "",
            value => Path.IsPathFullyQualified(value) && File.Exists(value) ? null : "MSBuild.exe was not found there."));
        settings.WatchSavedFiles = AnsiConsole.Confirm("Automatically deploy later saved changes by default?", settings.WatchSavedFiles);
        var worktrees = Ask("Default [bold]worktrees folder[/] (Open solution lists its worktrees from any repo; blank for none)",
            settings.WorktreesRoot, value => Path.IsPathFullyQualified(value) ? null : "Enter an absolute path.", optional: true);
        settings.WorktreesRoot = worktrees.Length == 0 ? "" : Files.Full(worktrees);

        var problems = settings.Problems();
        if (problems.Count != 0) throw new InvalidOperationException("Settings were not saved:\n" + string.Join("\n", problems));
        settings.Save();
        AnsiConsole.MarkupLine($"\n[{TerminalStyle.Accent}]Saved.[/] [{TerminalStyle.Muted}]Run `cultivate doctor` any time to check prerequisites.[/]");
        if (firstRun)
        {
            AnsiConsole.MarkupLine($"[{TerminalStyle.Gold}]Press any key[/] [{TerminalStyle.Muted}]to continue[/]");
            Console.ReadKey(true);
        }
        return settings;
    }

    private static string Ask(string prompt, string current, Func<string, string?> validate, bool optional = false)
    {
        var text = new TextPrompt<string>(prompt).PromptStyle(new Style(Color.FromHex(TerminalStyle.Accent)))
            .Validate(value =>
            {
                value = value.Trim().Trim('"');
                var error = value.Length == 0 ? optional ? null : "A value is required." : validate(value);
                return error is null ? ValidationResult.Success() : ValidationResult.Error($"[red]{Escape(error)}[/]");
            });
        if (current.Length != 0) text.DefaultValue(current);
        else if (optional) text.AllowEmpty();
        return AnsiConsole.Prompt(text).Trim().Trim('"');
    }

    private static string DefaultMapping(string repository) =>
        repository.Length == 0 ? "" : Path.Combine(repository, "bin", "MeyerObjects.dll");

    private static void GuardActive(DeploymentState? active, string previousFarm, string farm)
    {
        if (active?.Active == true && previousFarm.Length != 0 && !Files.Comparer.Equals(Files.Full(previousFarm), Files.Full(farm)))
            throw new InvalidOperationException("Restore the active deployment before changing its destination.");
    }

    private static async Task<DeploymentState?> ActiveDeployment(Settings settings)
    {
        if (settings.WebfarmRoot.Length == 0 || !Path.IsPathFullyQualified(settings.WebfarmRoot) || !Path.IsPathFullyQualified(settings.StateRoot))
            return null;
        try { return await new Bridge(settings).Status(); }
        catch (InvalidOperationException) { return null; }
    }
}
