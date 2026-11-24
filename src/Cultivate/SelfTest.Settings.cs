using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>The Settings screen and the first run's questions, driven with scripted keys and saved to a file in the test folder.</summary>
    private static void CheckSettings(string home)
    {
        var root = Path.Combine(home, "settings-screen");
        var repository = Path.Combine(root, "MyMeyer");
        var other = Path.Combine(root, "Other MyMeyer");
        var farm = Path.Combine(root, "farm");
        var msbuild = Path.Combine(root, "MSBuild.exe");
        foreach (var checkout in new[] { repository, other })
        {
            Directory.CreateDirectory(checkout);
            File.WriteAllText(Path.Combine(checkout, "MyMeyer.sln"), "");
        }
        Directory.CreateDirectory(farm);
        File.WriteAllText(msbuild, "");
        var path = Path.Combine(root, "settings.json");
        Settings Fixture() => new()
        {
            RepositoryRoot = repository, WebfarmRoot = farm, MSBuild = msbuild, MappingAssembly = Path.Combine(repository, "bin", "MeyerObjects.dll"),
            StateRoot = Path.Combine(root, "state"), SiteUrl = "http://fixture.example/", ReleaseRoot = Path.Combine(root, "Releases"), LibraryRepositories = []
        };
        static string Json(Settings settings) => JsonSerializer.Serialize(settings, Files.Json);
        Settings? Saved() => File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Files.Json) : null;
        var enter = Press(ConsoleKey.Enter, '\r');
        var escape = Press(ConsoleKey.Escape);
        var clear = new ConsoleKeyInfo('\u0015', ConsoleKey.U, false, false, true);
        Check(Fixture().Problems().Count == 0 && SettingField.All.Count == 16, "the settings fixture has no problems and there are sixteen settings");

        var settings = Fixture();
        var screen = new SettingsScreen(settings, null, path);
        screen.Checking.Wait();
        var names = SettingField.All.Select(field => field.Name).ToList();
        var large = Capture(screen.Render(160, 50), 160, 50);
        Check(names.All(large.Contains) && large.Split('\n').Length <= 50 && large.Contains("none") && large.Contains("Esc back") &&
            large.Split('\n').Any(line => line.Contains("Open solutions with") && line.Contains(Editors.AutomaticLabel)),
            "the Settings screen lists every setting with its value, a blank optional one as none and a choice by its label, on a large terminal");
        var fits = true;
        var reached = new HashSet<string>();
        foreach (var _ in names)
        {
            var lines = Capture(screen.Render(80, 24), 80, 24).Split('\n');
            fits &= lines.Length <= 24 && lines.All(line => line.Length <= 80) && lines[^1].Contains("Esc back");
            reached.UnionWith(names.Where(name => lines.Any(line => line.Contains("> " + name))));
            screen.HandleKey(Press(ConsoleKey.DownArrow), SettingsScreen.PageSize(24));
        }
        Check(fits && reached.SetEquals(names) && screen.Index == 0,
            "at 80x24 the Settings screen fits with its keys on the last line, and every setting can be highlighted by scrolling");
        screen.HandleKey(Press(ConsoleKey.End), SettingsScreen.PageSize(24));
        Check(screen.Highlighted.Name == "Release files folder" && Capture(screen.Render(80, 24), 80, 24).Contains("Build release files makes a folder"),
            "End highlights the last setting and its card explains it");

        using (var output = new StringWriter())
        {
            var before = Json(settings);
            var reads = 0;
            new SettingsScreen(settings, null, path).Show(LiveConsole(output), _ => ++reads == 1 ? escape : throw new InvalidOperationException("read again"));
            Check(reads == 1 && Json(settings) == before && !File.Exists(path), "Esc leaves the Settings screen at once, with nothing changed or saved");
        }

        using (var output = new StringWriter())
        {
            screen = new SettingsScreen(settings, null, path);
            screen.Show(LiveConsole(output), Script([Press(ConsoleKey.DownArrow), enter, clear, .. Typed("dvelopX"), Press(ConsoleKey.Backspace),
                Press(ConsoleKey.Home), Press(ConsoleKey.RightArrow), .. Typed("e"), Press(ConsoleKey.End), Press(ConsoleKey.LeftArrow),
                Press(ConsoleKey.LeftArrow), Press(ConsoleKey.Delete), .. Typed("o"), enter, null, escape]));
            Check(settings.BaseBranch == "develop" && Saved()?.BaseBranch == "develop" && screen.Field is null && Shown(output.ToString()).Contains("Saved Base branch."),
                "a text setting is edited in place with typing, Backspace, Delete, the caret keys and Ctrl+U, and saved at once with Enter");
        }

        screen = new SettingsScreen(settings, null, path);
        var pageSize = SettingsScreen.PageSize(24);
        screen.HandleKey(Press(ConsoleKey.End), pageSize);
        var releases = Path.Combine(root, string.Join(@"\", Enumerable.Repeat("a rather long folder name for release files", 4)), "Releases");
        foreach (var key in new[] { enter, clear }.Concat(Typed('"' + releases + '"').Cast<ConsoleKeyInfo>())) screen.HandleKey(key, pageSize);
        var editing = Capture(screen.Render(80, 24), 80, 24).Split('\n');
        Check(editing.Length <= 24 && editing.All(line => line.Length <= 80) && editing.Any(line => line.Contains("…") && line.Contains("Releases\"")) &&
            editing[^1].Contains("Enter save") && editing[^1].Contains("Esc cancel"),
            "a pasted long path scrolls sideways in its field around the caret at 80 columns, and the keys say Enter saves and Esc cancels");
        screen.HandleKey(Press(ConsoleKey.Home), pageSize);
        Check(Capture(screen.Render(80, 24), 80, 24).Contains("\"" + root[..10]), "Home scrolls the field back to the start of the path");
        screen.HandleKey(enter, pageSize);
        Check(settings.ReleaseRoot == releases && Saved()?.ReleaseRoot == releases && screen.Field is null,
            "Enter saves the pasted path without its quotes");

        var saved = File.ReadAllText(path);
        screen.HandleKey(Press(ConsoleKey.Home), pageSize);
        for (var index = 0; index < 4; index++) screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        foreach (var key in new[] { enter, clear }.Concat(Typed(@"relative\MSBuild.exe").Cast<ConsoleKeyInfo>()).Append(enter)) screen.HandleKey(key, pageSize);
        Check(screen.Highlighted.Name == "MSBuild" && screen.Field is not null && screen.Error == "MSBuild.exe was not found there." &&
            Capture(screen.Render(80, 24), 80, 24).Contains("MSBuild.exe was not found there.") && settings.MSBuild == msbuild,
            "an invalid value keeps its field open with the reason under it, and changes nothing");
        screen.HandleKey(escape, pageSize);
        Check(screen.Field is null && screen.Error is null && settings.MSBuild == msbuild && File.ReadAllText(path) == saved,
            "Esc then discards the edit and returns to the list with nothing saved");
        screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        screen.HandleKey(enter, pageSize);
        Check(screen.Highlighted.Name == "Automatic updates" && !settings.WatchSavedFiles && Saved()?.WatchSavedFiles == false && screen.Field is null,
            "Enter turns a yes/no setting the other way and saves it, with nothing to confirm");
        screen.HandleKey(Press(ConsoleKey.Spacebar, ' '), pageSize);
        Check(settings.WatchSavedFiles && Saved()?.WatchSavedFiles == true, "Space turns it back");

        screen.HandleKey(Press(ConsoleKey.Home), pageSize);
        foreach (var key in new[] { enter, clear }.Concat(Typed(other).Cast<ConsoleKeyInfo>()).Append(enter)) screen.HandleKey(key, pageSize);
        Check(settings.RepositoryRoot == other && settings.MappingAssembly == Path.Combine(other, "bin", "MeyerObjects.dll") &&
            Saved()?.MappingAssembly == settings.MappingAssembly, "changing the MyMeyer checkout sets the mapping assembly to the new checkout's");
        for (var index = 0; index < 3; index++) screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        Check(screen.Highlighted.Name == "Mapping assembly" && Capture(screen.Render(100, 30), 100, 30).Contains(SettingField.NotBuilt),
            "a mapping assembly that is not built yet is noted on its card");
        screen.HandleKey(Press(ConsoleKey.UpArrow), pageSize);
        foreach (var key in new[] { enter, clear }.Concat(Typed(@"\\localhost\cultivate-fixture\MyMeyer").Cast<ConsoleKeyInfo>()).Append(enter))
            screen.HandleKey(key, pageSize);
        Check(settings.WebfarmRoot == @"\\localhost\cultivate-fixture\MyMeyer" && settings.SiteUrl == "http://localhost/" && Saved()?.SiteUrl == "http://localhost/",
            "changing the webfarm root suggests the site address again");
        Check(Capture(screen.Render(100, 30), 100, 30).Contains(SettingField.NotReachable), "a webfarm root that cannot be reached is noted on its card");
        for (var index = 0; index < 6; index++) screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        foreach (var key in new[] { enter, clear, enter }) screen.HandleKey(key, pageSize);
        screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        var unused = Capture(screen.Render(100, 30), 100, 30);
        var warm = settings.WarmUpAfterDeploy;
        screen.HandleKey(enter, pageSize);
        Check(settings.SiteUrl == "" && screen.Highlighted.Name == "Warm up after deploying" && unused.Contains("not used") &&
            unused.Contains("Not used without a dev site address.") && !unused.Split('\n')[^1].Contains("toggle") && settings.WarmUpAfterDeploy == warm,
            "with no site address the warm-up is shown as not used, and Enter leaves it alone");

        var activeSettings = Fixture();
        var activePath = Path.Combine(root, "active.json");
        var guarded = new SettingsScreen(activeSettings, new DeploymentState { Active = true }, activePath);
        guarded.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        guarded.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        foreach (var key in new[] { enter, clear }.Concat(Typed(Path.Combine(root, "other farm")).Cast<ConsoleKeyInfo>()).Append(enter))
            guarded.HandleKey(key, pageSize);
        Check(guarded.Error == Setup.ActiveMoved && guarded.Field is not null && activeSettings.WebfarmRoot == farm && !File.Exists(activePath) &&
            Capture(guarded.Render(100, 30), 100, 30).Contains("Restore the active deployment"),
            "changing the webfarm root while a deployment is active is refused on its row, not with an error report");

        var broken = Fixture();
        broken.StateRoot = "relative";
        var fixing = new SettingsScreen(broken, null, activePath);
        fixing.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        foreach (var key in Typed("-2").Cast<ConsoleKeyInfo>().Prepend(enter).Append(enter)) fixing.HandleKey(key, pageSize);
        Check(broken.BaseBranch == "master-2" && File.Exists(activePath), "a change that adds no problem is saved even when the settings already have one");
        File.Delete(activePath);

        CheckOpenWithSetting(root, Fixture());
        CheckFirstRun(root, repository, farm, msbuild);
    }

    /// <summary>The Open solutions with setting on the Settings screen, against fixture editors.</summary>
    private static void CheckOpenWithSetting(string root, Settings settings)
    {
        var path = Path.Combine(root, "open-with.json");
        Settings? Saved() => File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Files.Json) : null;
        string Devenv(string folder)
        {
            var devenv = Path.Combine(root, "editors", folder, "devenv.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(devenv)!);
            File.WriteAllText(devenv, "");
            return devenv;
        }
        var newest = new Editor("Visual Studio Professional 2026", Devenv("18"));
        var older = new Editor("Visual Studio Professional 2022", Devenv("17"));
        var code = new Editor("VS Code", Path.Combine(root, "editors", "code.cmd"), VSCode: true);
        var field = SettingField.All.Single(setting => setting.Name == "Open solutions with");
        var pageSize = SettingsScreen.PageSize(24);
        Check(SettingField.All.ToList().IndexOf(field) == SettingField.All.ToList().FindIndex(setting => setting.Name == "Worktrees folder") + 1 &&
            field.Choice && SettingField.Wrap(field.Description, 76).Count <= 3,
            "Open solutions with is a choice setting right after Worktrees folder, explained in three lines at 80 columns");

        Editors.Use(new([newest, older], code));
        var screen = new SettingsScreen(settings, null, path);
        screen.Checking.Wait();
        while (screen.Highlighted != field) screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        var shown = new List<string>();
        var saved = new List<string?>();
        foreach (var key in new[] { Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Spacebar, ' '), Press(ConsoleKey.Enter, '\r'), Press(ConsoleKey.Spacebar, ' '),
            Press(ConsoleKey.Enter, '\r') })
        {
            screen.HandleKey(key, pageSize);
            var lines = Capture(screen.Render(80, 24), 80, 24).Split('\n');
            shown.Add(lines.FirstOrDefault(line => line.Contains("> Open solutions with"))?.Trim() ?? "");
            saved.Add(Saved()?.OpenWith);
        }
        Check(saved.SequenceEqual([newest.Path, older.Path, "VSCode", "Ask", ""]) && settings.OpenWith == "" &&
            shown.Zip(new[] { newest.Name, older.Name, "VS Code", "Ask each time", Editors.AutomaticLabel }).All(pair => pair.First.EndsWith(pair.Second)) &&
            Capture(screen.Render(80, 24), 80, 24).Split('\n')[^1].Contains("Enter/Space next choice") && screen.Notice == "Saved Open solutions with.",
            "Enter and Space step through every editor, each particular Visual Studio by name, and back to the first, saving each at once");

        Editors.Use(new([newest], code));
        Check(field.Choices!(settings).Select(choice => choice.Label).SequenceEqual([Editors.AutomaticLabel, "VS Code", "Ask each time"]),
            "with one Visual Studio installed there is no particular one to choose");

        settings.OpenWith = Path.Combine(root, "editors", "uninstalled", "devenv.exe");
        screen = new SettingsScreen(settings, null, path);
        screen.Checking.Wait();
        while (screen.Highlighted != field) screen.HandleKey(Press(ConsoleKey.DownArrow), pageSize);
        var stale = Capture(screen.Render(100, 30), 100, 30);
        Check(stale.Contains("uninstalled") && stale.Contains("That Visual Studio is no longer installed, so the newest installed one opens instead."),
            "a Visual Studio that is no longer installed shows as its path, with a note that the newest opens instead");
        screen.HandleKey(Press(ConsoleKey.Enter, '\r'), pageSize);
        Check(settings.OpenWith == "" && Saved()?.OpenWith == "", "the next choice after a value that is none of them is the first");

        Editors.Use(new([newest], null));
        settings.OpenWith = "VSCode";
        Check(new SettingsScreen(settings, null, path).Checking.Result[SettingField.All.ToList().IndexOf(field)] ==
            "VS Code was not found. Install it before opening a solution.", "VS Code chosen but not installed is noted");
        Editors.Use(new([], null));
        File.Delete(path);
    }

    private static void CheckFirstRun(string root, string repository, string farm, string msbuild)
    {
        var path = Path.Combine(root, "first-run.json");
        var enter = Press(ConsoleKey.Enter, '\r');
        var escape = Press(ConsoleKey.Escape);
        Settings Blank() => new() { MSBuild = msbuild, StateRoot = Path.Combine(root, "state"), ReleaseRoot = Path.Combine(root, "Releases") };
        static string Json(Settings settings) => JsonSerializer.Serialize(settings, Files.Json);

        var screen = new FirstRunScreen(Blank(), null, true, path);
        var lines = Capture(screen.Render(80, 24), 80, 24).Split('\n');
        Check(lines.Length <= 24 && lines.All(line => line.Length <= 80) && lines[^1].Contains("Esc leave setup") && lines.Any(line => line.Contains("Step 1 of 15")),
            "the first run asks fifteen questions, leaving the choice of editor at its default, and its first fits 80x24 with Esc leaving setup");
        foreach (var key in Typed(repository).Cast<ConsoleKeyInfo>().Append(enter)) screen.HandleKey(key);
        Check(screen.Step == 1 && Capture(screen.Render(80, 24), 80, 24).Split('\n')[^1].Contains("Esc previous question"),
            "Enter accepts a typed answer and asks the next question");
        screen.HandleKey(escape);
        Check(screen.Step == 0 && screen.Field?.Text == repository, "Esc returns to the previous question with its answer");
        foreach (var key in new[] { enter, enter }.Concat(Typed(farm).Cast<ConsoleKeyInfo>()).Append(enter)) screen.HandleKey(key);
        Check(screen.Step == 3 && screen.Field?.Text == Path.Combine(repository, "bin", "MeyerObjects.dll") && screen.Notice is null,
            "the mapping assembly question starts with the checkout's");
        screen.HandleKey(enter);
        Check(screen.Notice == "Mapping assembly: " + SettingField.NotBuilt, "a mapping assembly that is not built yet is noted above the next question");
        var clear = new ConsoleKeyInfo('\u0015', ConsoleKey.U, false, false, true);
        foreach (var key in Typed(@"relative\MSBuild.exe").Cast<ConsoleKeyInfo>().Prepend(clear).Append(enter)) screen.HandleKey(key);
        lines = Capture(screen.Render(80, 24), 80, 24).Split('\n');
        Check(screen.Step == 4 && screen.Error == "MSBuild.exe was not found there." && lines.Length <= 24 && lines.All(line => line.Length <= 80) &&
            lines.Any(line => line.Contains("MSBuild.exe was not found there.")) && lines.Any(line => line.Contains("Not built yet")) &&
            lines[^1].Contains("Esc previous question"),
            "an invalid answer keeps its question with the reason under it, and still fits 80x24 with a note above it");
        foreach (var key in Typed(msbuild).Cast<ConsoleKeyInfo>().Prepend(clear).Append(enter)) screen.HandleKey(key);
        Check(screen.Question.Name == "Automatic updates" && screen.Field is null, "a yes/no question has no field");
        screen.HandleKey(Press(ConsoleKey.X, 'x'));
        Check(screen.Step == 5, "a yes/no question ignores other keys");
        screen.HandleKey(Press(ConsoleKey.N, 'n'));
        screen.HandleKey(escape);
        Check(screen.Step == 5 && !screen.Draft.WatchSavedFiles, "N answers no in one key, and Esc on the next question returns to it");
        screen.HandleKey(Press(ConsoleKey.Y, 'y'));
        Check(screen.Step == 6 && screen.Draft.WatchSavedFiles, "Y answers yes in one key");
        foreach (var key in new[] { enter, enter }) screen.HandleKey(key);
        Check(screen.Draft.SiteUrl == "" && screen.Question.Name == "SQL files folder", "with no site address the warm-up question is skipped");
        screen.HandleKey(escape);
        Check(screen.Question.Name == "Dev site address", "and Esc skips it on the way back");

        var settings = Blank();
        var before = Json(settings);
        Settings? done;
        using (var output = new StringWriter())
        {
            done = Setup.Guided(settings, null, true, LiveConsole(output), Script([.. Typed(repository), enter, escape, escape]), path);
            Check(done is null && !File.Exists(path) && Json(settings) == before && Shown(output.ToString()).Contains("Setup was left. Nothing was saved."),
                "Esc on the first question leaves setup with nothing saved and the settings unchanged, even after answering a question");
        }
        using (var output = new StringWriter())
        {
            done = Setup.Guided(settings, null, true, LiveConsole(output), Script([.. Typed(repository), enter, enter, .. Typed(farm), enter, enter, enter,
                Press(ConsoleKey.N, 'n'), enter, enter, enter, enter, enter, enter, enter, enter]), path);
            var saved = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Files.Json) : null;
            Check(done == settings && settings.RepositoryRoot == repository && settings.WebfarmRoot == farm && !settings.WatchSavedFiles &&
                settings.MappingAssembly == Path.Combine(repository, "bin", "MeyerObjects.dll") && saved is not null && Json(saved) == Json(settings) &&
                Shown(output.ToString()).Contains("Saved."),
                "answering the last question saves every answer and gives them to the settings");
        }
        var problem = new FirstRunScreen(Blank(), new DeploymentState { Active = true }, false, path);
        Check(Capture(problem.Render(80, 24), 80, 24).Contains("Some settings need fixing"), "settings that need fixing are asked for with their own heading");
    }
}
