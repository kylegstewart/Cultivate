using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>
    /// Finding, choosing and launching the editor that opens a solution, from fixtures alone: vswhere's JSON, folders holding code.cmd, and
    /// a fake code.cmd that records its arguments. Nothing installed on this machine is used or started.
    /// </summary>
    private static async Task CheckEditors(string home)
    {
        var root = Path.Combine(home, "editors");
        Directory.CreateDirectory(root);
        CheckVisualStudioJson();
        CheckVSCodeLookup(root);

        string Devenv(string folder)
        {
            var path = Path.Combine(root, folder, "devenv.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }
        var newest = new Editor("Visual Studio Professional 2026", Devenv("18"));
        var older = new Editor("Visual Studio Professional 2022", Devenv("17"));
        var unlisted = Devenv("portable");
        var stale = Path.Combine(root, "uninstalled", "devenv.exe");
        var code = new Editor("VS Code", Path.Combine(root, "code.cmd"), VSCode: true);
        var all = new InstalledEditors([newest, older], code);
        var enter = Script(Press(ConsoleKey.Enter, '\r'));
        Editor? Choose(string openWith, InstalledEditors installed, Func<int, ConsoleKeyInfo?>? keys = null)
        {
            using var output = new StringWriter();
            return Terminal.ChooseEditor(openWith, installed, LiveConsole(output), keys ?? (_ => throw new InvalidOperationException("asked")));
        }
        string? Refusal(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) { return exception.Message; }
            return null;
        }

        Check(Choose("", all) == newest && Choose("  ", all) == newest && Choose("Notepad", all) == newest && Choose(@"C:\Tools\code.exe", all) == newest,
            "the automatic choice, and text Cultivate does not know, open the newest installed Visual Studio without asking");
        Check(Choose(older.Path, all) == older && Choose(stale, all) == newest && Choose(unlisted, all) == new Editor("Visual Studio", unlisted),
            "a particular Visual Studio opens while its devenv.exe exists, even one vswhere does not list, and the newest opens once it is gone");
        Check(Choose("VSCode", all) == code && Choose("vscode", all) == code && Choose("ASK", new([], code)) == code,
            "VS Code is chosen whatever the case of the setting, and Ask with only one editor installed uses it without asking");
        Check(Refusal(() => Choose("VSCode", new([newest], null))) ==
            "VS Code was not found. Install VS Code, or choose another editor under Settings → Open solutions with." &&
            Refusal(() => Choose("", new([], code))) ==
            "Visual Studio was not found. Install Visual Studio, or choose VS Code under Settings → Open solutions with." &&
            Refusal(() => Choose(stale, new([], code)))?.StartsWith("Visual Studio was not found.") == true &&
            Refusal(() => Choose("Ask", new([], null))) == "Neither Visual Studio nor VS Code was found. Install one to open solutions.",
            "an editor that is not installed is an error that says what to install or choose instead");
        using (var output = new StringWriter())
        {
            var chosen = Terminal.ChooseEditor("Ask", all, LiveConsole(output), Script(Press(ConsoleKey.DownArrow), Press(ConsoleKey.Enter, '\r')));
            var shown = Shown(output.ToString());
            Check(chosen == older && shown.Contains("Open with which editor?") && shown.Contains(newest.Name) && shown.Contains("VS Code") &&
                shown.Contains("Back") && shown.Contains(newest.Path),
                "Ask lists every installed Visual Studio, then VS Code and Back, each described by its path, and gives the one chosen");
        }
        Check(Choose("Ask", all, enter) == older, "asking again in the same run highlights the editor chosen last time");
        Check(Choose("Ask", all, Script(Press(ConsoleKey.Escape))) is null && Choose("Ask", all, Script(Press(ConsoleKey.End), Press(ConsoleKey.Enter, '\r'))) is null,
            "Esc or Back leaves the question without an editor");

        var labels = Editors.Choices(all).Select(choice => choice.Label).ToList();
        Check(labels.SequenceEqual([Editors.AutomaticLabel, newest.Name, older.Name, "VS Code", "Ask each time"]) &&
            Editors.Choices(new([newest], null)).Select(choice => choice.Value).SequenceEqual(["", "VSCode", "Ask"]),
            "the setting's choices offer a particular Visual Studio only when several are installed, and VS Code even when it is not");
        Check(Editors.Note("", all) is null && Editors.Note("", new([], code)) == "Visual Studio was not found." &&
            Editors.Note(stale, all) == "That Visual Studio is no longer installed, so the newest installed one opens instead." &&
            Editors.Note(older.Path, all) is null && Editors.Note("VSCode", new([newest], null)) == "VS Code was not found. Install it before opening a solution." &&
            Editors.Note("VSCode", all) is null && Editors.Note("Ask", all) is null,
            "the setting is noted when what it names is not installed");
        Check(Editors.Doctor("", all) == (true, $"Opens solutions with: {Editors.AutomaticLabel}, {newest.Name}") &&
            Editors.Doctor(older.Path, all) == (true, "Opens solutions with: " + older.Name) && Editors.Doctor("VSCode", all) == (true, "Opens solutions with: VS Code") &&
            Editors.Doctor("Ask", all).Message.EndsWith($"{newest.Name}, {older.Name}, VS Code") && !Editors.Doctor(stale, all).Ok &&
            !Editors.Doctor("VSCode", new([newest], null)).Ok && !Editors.Doctor("", new([], code)).Ok && !Editors.Doctor("Ask", new([], null)).Ok,
            "doctor says what opens solutions, and warns when the configured editor is not installed");
        Check(Editors.FromOption("visual-studio") == "" && Editors.FromOption("VSCode") == "VSCode" && Editors.FromOption("ask") == "Ask" &&
            Editors.FromOption(older.Path) == older.Path &&
            new[] { "notepad", stale, Path.Combine(root, "code.cmd"), "devenv.exe" }.All(value => Refusal(() => Editors.FromOption(value)) ==
                "--open-with expects visual-studio, vscode, ask, or the full path of an installed devenv.exe."),
            "setup --open-with takes visual-studio, vscode, ask, or an installed devenv.exe, and lists them for anything else");

        CheckCodeCommands();
        await CheckCodeLaunch(root);
    }

    private static void CheckVisualStudioJson()
    {
        static string Install(string name, string version, bool preview, string path) =>
            JsonSerializer.Serialize(new { displayName = name, installationVersion = version, isPrerelease = preview, productPath = path });
        static string Json(params string[] installs) => "[" + string.Join(",", installs) + "]";
        static List<Editor> Parse(string json) => Editors.VisualStudios(json, path => !path.Contains("missing", StringComparison.Ordinal));
        const string vs2026 = @"C:\VS\18\Professional\Common7\IDE\devenv.exe", vs2022 = @"C:\VS\2022\Professional\Common7\IDE\devenv.exe",
            preview = @"C:\VS\18\Preview\Common7\IDE\devenv.exe", insiders = @"C:\VS\18\Insiders\Common7\IDE\devenv.exe";

        Check(Parse(Json(Install("Visual Studio Professional 2026", "18.5.11801.241", false, vs2026))).SequenceEqual(
            [new Editor("Visual Studio Professional 2026", vs2026)]), "one installed Visual Studio is found by its name and devenv.exe");
        var withPreview = Parse(Json(Install("Visual Studio Enterprise 2026", "18.6.1", true, preview), Install("Visual Studio Professional 2022", "17.14.2", false, vs2022)));
        Check(withPreview.Select(studio => studio.Path).SequenceEqual([vs2022, preview]) && withPreview[1].Name == "Visual Studio Enterprise 2026 Preview",
            "a release comes before a newer preview, and a preview's name says so");
        Check(Parse(Json(Install("Visual Studio Professional 2022", "17.14.2", false, vs2022), Install("Visual Studio Professional 2026", "18.10.1", false, vs2026),
            Install("Visual Studio Professional 2026", "18.9.4", false, @"C:\VS\18b\Common7\IDE\devenv.exe"))).Select(studio => studio.Name).SequenceEqual(
            ["Visual Studio Professional 2026 (18.10.1)", "Visual Studio Professional 2026 (18.9.4)", "Visual Studio Professional 2022"]),
            "releases are newest first by version number, and installs that share a name are told apart by their version");
        Check(Parse(Json(Install("Visual Studio Community 2026 Insiders", "18.7.0", true, insiders), Install("Visual Studio Code Preview", "1.0", true, preview)))
            .Select(studio => studio.Name).SequenceEqual(["Visual Studio Community 2026 Insiders", "Visual Studio Code Preview"]),
            "a preview whose name already says Insiders or Preview keeps it");
        Check(Parse(Json(Install("SQL Server Management Studio 22", "22.1", false, @"C:\SSMS\Common7\IDE\Ssms.exe"),
            Install("Visual Studio Professional 2026", "18.5", false, @"C:\missing\Common7\IDE\devenv.exe"), Install("Visual Studio Build Tools 2026", "18.5", false, ""))).Count == 0 &&
            Parse("").Count == 0 && Parse("  ").Count == 0 && Parse("[]").Count == 0,
            "a product that is not devenv.exe or whose devenv.exe is missing is left out, and empty output finds none");
    }

    private static void CheckVSCodeLookup(string root)
    {
        string Folder(string name, bool withCode)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            if (withCode) File.WriteAllText(Path.Combine(folder, "code.cmd"), "");
            return folder;
        }
        var onPath = Folder("on path", true);
        var empty = Folder("empty", false);
        var local = Folder("local app data", true);
        var programs = Folder("program files", true);
        static string Code(string folder) => Path.Combine(folder, "code.cmd");
        Check(Editors.FindVSCode([empty, onPath], [Folder("no local", false), Folder("no programs", false)]) == Code(onPath) &&
            Editors.FindVSCode([empty], [local, programs]) == Code(local) && Editors.FindVSCode([], [empty, programs]) == Code(programs) &&
            Editors.FindVSCode(['"' + onPath + '"'], [local, programs]) == Code(onPath) && Editors.FindVSCode(["", "relative", empty], [empty]) is null,
            "VS Code's code.cmd is found on PATH first, then in each default folder, or not at all");
    }

    private static void CheckCodeCommands()
    {
        const string code = @"C:\Program Files\Microsoft VS Code\bin\code.cmd", root = @"C:\src\R&D (old)\Ticket 1";
        static List<string> Arguments(string command) =>
            Regex.Matches(command["/d /s /c \"".Length..^1], "\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToList();
        var awkward = new List<string> { root + @"\Order & Freight\Page (1).ascx", root + @"\a b\c.cs" };
        var single = SolutionOpener.CodeCommands(code, root, awkward);
        Check(single.Count == 1 && single[0] == $"/d /s /c \"\"{code}\" \"{root}\" \"{awkward[0]}\" \"{awkward[1]}\"\"" &&
            Arguments(single[0]).SequenceEqual([code, root, .. awkward]),
            "VS Code's command quotes code.cmd, the checkout's folder and every file, keeping &, parentheses and spaces intact");
        var many = Enumerable.Range(0, 300).Select(index => $@"{root}\Some Project\A rather long folder name\File number {index:000}.cs").ToList();
        var split = SolutionOpener.CodeCommands(code, root, many);
        var arguments = split.Select(Arguments).ToList();
        Check(split.Count > 1 && split.All(command => command.Length <= SolutionOpener.CommandLimit) &&
            arguments.All(command => command.Take(2).SequenceEqual([code, root])) && arguments.SelectMany(command => command.Skip(2)).SequenceEqual(many),
            "a long list of files is split into commands under cmd's limit, each opening the folder, with every file once and in order");
        Check(SolutionOpener.CodeCommands(code, root, []).SequenceEqual([$"/d /s /c \"\"{code}\" \"{root}\"\""]), "with no files the folder alone opens");
        string? Refusal(string path)
        {
            try { SolutionOpener.CodeCommands(code, root, [path]); }
            catch (InvalidOperationException exception) { return exception.Message; }
            return null;
        }
        Check(Refusal(@"C:\src\100%\a.cs")?.Contains(@"C:\src\100%\a.cs") == true && Refusal("C:\\src\\\"quoted\"\\a.cs") is not null,
            "a path containing % or \" is refused rather than handed to cmd");
    }

    /// <summary>A real launch through cmd.exe of a fake code.cmd that appends each argument it receives to a file.</summary>
    private static async Task CheckCodeLaunch(string root)
    {
        var fakes = Path.Combine(root, "fake VS Code (R&D)");
        Directory.CreateDirectory(fakes);
        var log = Path.Combine(fakes, "arguments.txt");
        var fake = Path.Combine(fakes, "code.cmd");
        File.WriteAllText(fake, $"@echo off\r\n:next\r\nif \"%~1\"==\"\" exit /b 0\r\n>>\"{log}\" echo(%1\r\nshift\r\ngoto next\r\n");
        var checkout = Path.Combine(root, "checkout R&D (x)");
        Directory.CreateDirectory(checkout);
        var files = new List<string> { Path.Combine(checkout, @"Order & Freight\Page (1).ascx"), Path.Combine(checkout, "a b.cs") };
        List<string> Received() => File.ReadAllLines(log).Select(line => line.Trim().Trim('"')).ToList();

        await SolutionOpener.Open(new Editor("VS Code", fake, VSCode: true), Path.Combine(checkout, @"App\App.sln"), checkout, files);
        Check(Received().SequenceEqual([checkout, .. files]),
            "VS Code is given the checkout's folder and the changed files exactly, with spaces, & and parentheses in their paths");

        File.Delete(log);
        var many = Enumerable.Range(0, 120).Select(index => Path.Combine(checkout, $@"A rather long folder name for a project\File number {index:000}.cs")).ToList();
        await SolutionOpener.OpenFolder(fake, checkout, many);
        var received = Received();
        var commands = received.Count(argument => argument == checkout);
        Check(commands > 1 && received.Where(argument => argument != checkout).SequenceEqual(many) && received.Count == many.Count + commands,
            "files too many for one command reach VS Code over several, each opening the same folder");

        var failing = Path.Combine(root, "failing VS Code", "code.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(failing)!);
        File.WriteAllText(failing, "@exit /b 3\r\n");
        string? error = null;
        try { await SolutionOpener.OpenFolder(failing, checkout, files); }
        catch (InvalidOperationException exception) { error = exception.Message; }
        Check(error == "VS Code could not open the folder: code.cmd exited with code 3.",
            "a VS Code command that fails is reported as VS Code not opening the folder");
    }
}
