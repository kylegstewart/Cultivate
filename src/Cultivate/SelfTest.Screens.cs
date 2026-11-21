using System.Globalization;
using System.Text.RegularExpressions;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>The pickers and menus repaint only what a key changes, handle queued keys in one paint, and the home menu works while the status is read.</summary>
    private static void CheckScreens()
    {
        static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0') => new(character, key, false, false, false);
        static string[] Paints(string output) => output.Split("\u001b[?2026h").Skip(1).Select(paint => paint.Split("\u001b[?2026l")[0]).ToArray();
        static int[] Rows(string paint) => Regex.Matches(paint, @"\u001b\[(\d+);1H").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        // Each line a screen writes is padded to the terminal's 100 columns.
        static int Touched(string paint) => Regex.Replace(paint, @"\u001b\[[0-9;?]*[A-Za-z]|[\r\n]", "").Length / 100;
        static Queue<ConsoleKeyInfo?> Keys(int held, ConsoleKeyInfo last) =>
            new([null, Key(ConsoleKey.DownArrow), null, .. Enumerable.Repeat<ConsoleKeyInfo?>(Key(ConsoleKey.DownArrow), held), null, last]);
        var enter = Key(ConsoleKey.Enter, '\r');

        var targets = Enumerable.Range(0, 200).Select(index => new Target($"{30000 + index}", $@"C:\fixture\worktrees\w{index:000}", $"refs/heads/{30000 + index}")).ToList();
        using (var output = new StringWriter())
        {
            var keys = Keys(30, enter);
            var chosen = new VersionPicker(targets).Show(LiveConsole(output), _ => keys.Dequeue());
            var paints = Paints(output.ToString());
            var full = Rows(paints[0]);
            Check(chosen == targets[31] && paints.Length == 3 && full.SequenceEqual(Enumerable.Range(1, full.Length)) && full.Length is > 20 and <= 30 &&
                Rows(paints[1]).Length is > 0 and <= 6, "the version picker paints its 200 rows once, then only the lines a key changes");
            Check(paints.Length == 3 && output.ToString().Contains("SELECTED SOURCE"), "a held arrow's queued repeats move the version picker's highlight in one paint");
        }

        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var workspaces = Enumerable.Range(0, 200).Select(index => new TicketWorkspace($"{39000 + index}", $"{39000 + index}",
            [FakeCheckout("MyMeyer", $"{39000 + index}")], now.AddHours(-index))).ToList();
        using (var output = new StringWriter())
        {
            var keys = Keys(30, enter);
            var chosen = new TicketPicker(workspaces, now: now).Show(LiveConsole(output), _ => keys.Dequeue());
            var paints = Paints(output.ToString());
            Check(chosen == workspaces[31] && paints.Length == 3 && Rows(paints[0]).Length is > 20 and <= 30 && Rows(paints[1]).Length is > 0 and <= 6,
                "the ticket list paints its 200 workspaces once, then only the lines a key changes, and a held arrow's repeats in one paint");
        }

        using (var output = new StringWriter())
        {
            var keys = Keys(5, enter);
            var chosen = new ActionPicker(Terminal.HomeActions).Show("Actions", LiveConsole(output), _ => keys.Dequeue());
            var text = output.ToString();
            var paints = Paints(text);
            var drawn = Capture(new ActionPicker(Terminal.HomeActions).Render("Actions", 30), 100, 30).Split('\n').Length;
            Check(chosen == Terminal.HomeActions[6] && paints.Length == 3 && Rows(text).Length == 0 && Touched(paints[0]) == drawn && !text.Contains("\u001b[2J"),
                "an action menu paints below what the terminal shows, moving only between its own lines");
            Check(Touched(paints[1]) == 3 && Touched(paints[2]) == 3, "moving the menu's highlight rewrites the two rows and the explanation");
            // The last paint ends on the explanation, the line above the keys, so closing moves up from there to the menu's first line.
            Check(text.Split("\u001b[?2026l").Last().StartsWith($"\r\u001b[{drawn - 2}A\u001b[J", StringComparison.Ordinal),
                "a chosen action erases the menu and leaves the cursor where it began");
        }

        var settings = new Settings { RepositoryRoot = @"C:\fixture\MyMeyer", WebfarmRoot = @"\\webfarm\C$\inetpub\MyMeyer" };
        var terminal = new Terminal(settings, new GitRepository(settings), new Bridge(settings));
        var live = new DeploymentState
        {
            Active = true, Branch = "38636", SourceRoot = @"C:\fixture\worktrees\38636", Mode = "Cultivate", WatchEnabled = true, Heartbeat = DateTime.UtcNow.ToString("O")
        };
        using (var output = new StringWriter())
        {
            var status = new TaskCompletionSource<DeploymentState?>();
            var keys = new Queue<ConsoleKeyInfo?>([null, Key(ConsoleKey.DownArrow), null, null, enter]);
            var waits = new List<int>();
            ConsoleKeyInfo? Read(int wait)
            {
                waits.Add(wait);
                if (keys.Count == 2) status.SetResult(live);
                return keys.Dequeue();
            }
            var chosen = new ActionPicker(Terminal.HomeActions).Show("What would you like to do?", LiveConsole(output), Read,
                (_, _) => terminal.Home(status.Task.IsCompleted ? status.Task.Result : null, !status.Task.IsCompleted, null, Terminal.HomeActions.Count), status.Task);
            var paints = Paints(output.ToString());
            Check(chosen == Terminal.HomeActions[1] && paints.Length == 3 && paints[0].Contains("Reading the active deployment") &&
                paints[1].Contains("Open solution") && !paints[1].Contains("LIVE") && paints[2].Contains("38636"),
                "the home menu answers keys while the deployment status is read, and its card is filled in when the status arrives");
            Check(waits.SequenceEqual([20, 20, 0, 20, 200]), "the home screen checks for the status between short waits, and only waits for keys once it has arrived");
            var closing = output.ToString().Split("\u001b[?2026l").Last();
            Check(Regex.IsMatch(closing, @"^\u001b\[\d+;1H\u001b\[J") && !closing.Contains("\u001b[2J"), "choosing an action erases the menu and keeps the screen above it");
        }
        using (var output = new StringWriter())
        {
            var failed = Task.FromException<DeploymentState?>(new IOException("Workspace is busy."));
            Check(new ActionPicker(Terminal.HomeActions).Show("What would you like to do?", LiveConsole(output), _ => null,
                (_, _) => terminal.Home(null, true, null, Terminal.HomeActions.Count), failed) is null,
                "a failed status read closes the home menu without a choice, so its error is reported");
        }
        CheckNavigation();
    }

    /// <summary>Esc goes back one screen at once everywhere, never confirms anything, and every menu, question and list shows its keys.</summary>
    private static void CheckNavigation()
    {
        static string LastLine(string screen) => screen.Split('\n').Last(line => line.Trim().Length != 0);
        var esc = Press(ConsoleKey.Escape);
        var enter = Press(ConsoleKey.Enter, '\r');

        using (var output = new StringWriter())
        {
            var actions = Terminal.PreviewActions(true, true, true);
            var chosen = new ActionPicker(actions).Show("Preview only", LiveConsole(output), Script(Press(ConsoleKey.DownArrow), esc));
            Check(chosen == actions[^1] && chosen?.Label == "Back", "Esc leaves a menu at once with its back action, whatever is highlighted");
        }
        using (var output = new StringWriter())
            Check(new ActionPicker(SkipOffer.Actions.Where(action => !action.Back)).Show("No back action", LiveConsole(output), Script(esc)) is null,
                "Esc leaves a menu without a back action with no choice at all");
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var screen = Capture(new ActionPicker(Terminal.PreviewActions(true, true, true)).Render("Preview only", height), width, height);
            Check(LastLine(screen).Contains("Enter choose") && LastLine(screen).TrimEnd().EndsWith("Esc back", StringComparison.Ordinal) &&
                screen.Split('\n').All(line => line.Length <= width), $"an action menu's last line shows its keys, ending with Esc back, at {width}x{height}");
        }

        var home = Terminal.HomeMenu(null, null);
        home.HandleKey(Press(ConsoleKey.DownArrow), 7);
        home.HandleKey(esc, 7);
        Check(home.Selected?.Label == "Exit" && LastLine(Capture(home.Render("What would you like to do?", 30), 100, 30)).Contains("Esc highlights Exit"),
            "Esc on home highlights Exit, and home's keys say so");
        using (var output = new StringWriter())
        {
            var chosen = Terminal.HomeMenu(null, null).Show("What would you like to do?", LiveConsole(output), Script(esc, esc, Press(ConsoleKey.UpArrow), enter));
            Check(chosen?.Label == "Settings", "Esc on home chooses nothing, however often it is pressed; the menu stays open for the next key");
        }
        using (var output = new StringWriter())
        {
            var chosen = Terminal.HomeMenu(null, null).Show("What would you like to do?", LiveConsole(output), Script([.. Typed("tidy"), enter]));
            Check(chosen?.Label == "Update my webfarm", "typing on home neither filters nor moves the highlight");
        }
        var update = new UpdateInfo("9.9.9", "v9.9.9", "https://example.invalid/releases/v9.9.9");
        Check(Terminal.HomeMenu(null, "Ticket workspaces").Selected?.Label == "Ticket workspaces" &&
            Terminal.HomeMenu(update, "Ticket workspaces").Selected?.Label == "Ticket workspaces" &&
            Terminal.HomeMenu(update, "Exit").Matches[^2].Label == "Update Cultivate" && Terminal.HomeMenu(update, "Exit").Selected?.Label == "Exit" &&
            Terminal.HomeMenu(null, "Update Cultivate").Selected?.Label == "Update my webfarm",
            "home keeps the action last chosen highlighted, also once Update Cultivate is inserted before Exit, and starts at the top when it is gone");

        List<MenuAction> repositories = [new("MyMeyer  |  C:\\fixture\\MyMeyer", "One."), new("Warehouse--Tidy  |  C:\\fixture\\Warehouse--Tidy", "Two."),
            new("Back", "Return.", Back: true)];
        using (var output = new StringWriter())
        {
            var menu = new ActionPicker(repositories, search: true);
            var chosen = menu.Show("Which repository's main worktree?", LiveConsole(output), Script([.. Typed("tidy"), null, esc]));
            Check(chosen?.Back == true && menu.Query == "tidy" && menu.Matches.Single() == repositories[1] && Shown(output.ToString()).Contains("/ tidy") &&
                Shown(output.ToString()).Contains("Ctrl+U clear"), "a searchable menu filters as you type, and Esc leaves it at once with the search still typed");
        }
        using (var output = new StringWriter())
            Check(new ActionPicker(repositories, search: true).Show("Which repository?", LiveConsole(output), Script([.. Typed("tidy"), enter])) == repositories[1],
                "Enter in a filtered menu chooses the matching action");
        using (var output = new StringWriter())
            Check(new ActionPicker(repositories.Take(2), search: true).Show(ReleaseScreen.RepositoryQuestion, LiveConsole(output), Script([.. Typed("my"), esc])) is null,
                "Esc leaves the repository question, which has no back action, without a repository");

        bool Answer(bool preset, out string shown, params object?[] keys)
        {
            using var output = new StringWriter();
            var answer = Terminal.Confirm("Deploy [bold]38636[/]?", preset, LiveConsole(output), Script(keys));
            shown = Shown(output.ToString());
            return answer;
        }
        Check(!Answer(true, out var asked, esc) && asked.Contains("Deploy 38636?") && asked.Contains("Y yes N no Enter yes Esc no") && asked.TrimEnd().EndsWith("No", StringComparison.Ordinal),
            "a yes or no question shows its keys and which answer Enter gives, Esc answers No even when the default is yes, and the answer stays on its line");
        Check(Answer(true, out asked, enter) && asked.TrimEnd().EndsWith("Yes", StringComparison.Ordinal), "Enter takes a default of yes");
        Check(!Answer(false, out asked, enter) && asked.Contains("Enter no"), "Enter takes a default of no, and the keys say so");
        Check(Answer(false, out _, Press(ConsoleKey.Y, 'y')) && !Answer(true, out _, Press(ConsoleKey.N, 'n')), "Y answers yes and N answers no, whatever the default");
        Check(Answer(false, out _, Press(ConsoleKey.X, 'x'), Press(ConsoleKey.Spacebar, ' '), null, Press(ConsoleKey.Y, 'y')),
            "other keys are ignored until Y, N, Enter or Esc answers");

        var header = TerminalStyle.Header("SELECT DEPLOYMENT FILES", "Checked files deploy.");
        string[] files = ["Web/Page.aspx", "Web/Page.aspx.cs", "Lib/Code.cs"];
        IReadOnlyList<string>? Pick(params object?[] keys)
        {
            using var output = new StringWriter();
            return new Checklist<string>(header, "Choose files to deploy", files, file => file, [files[1]]).Show(LiveConsole(output), Script(keys));
        }
        var space = Press(ConsoleKey.Spacebar, ' ');
        Check(Pick(space, Press(ConsoleKey.DownArrow), space, enter) is ["Web/Page.aspx"], "Space toggles the highlighted item and Enter applies the checked ones");
        Check(Pick(space, Press(ConsoleKey.DownArrow), space, esc) is null, "Esc leaves a checklist without applying its changes");
        Check(Pick(new ConsoleKeyInfo('\u0001', ConsoleKey.A, false, false, true), enter)?.Count == 3 &&
            Pick(new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true), enter)?.Count == 0, "Ctrl+A checks every item and Ctrl+D clears them all");
        Check(Pick(Press(ConsoleKey.End), space, enter) is ["Web/Page.aspx.cs", "Lib/Code.cs"], "End moves to the last item");
        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var list = new Checklist<string>(header, "Choose files to deploy", Enumerable.Range(0, 40).Select(index => $"Web/Page{index}.aspx").ToList(), file => file, []);
            var lines = Capture(list.Render(width, height), width, height).Split('\n');
            Check(lines.Length < height && lines.All(line => line.Length <= width) && lines[^1].Contains("Space check") && lines[^1].TrimEnd().EndsWith("Esc back", StringComparison.Ordinal) &&
                lines.Any(line => line.Contains("[ ] Web/Page0.aspx")), $"a checklist fits {width}x{height} with its keys on its last line, ending with Esc back");
        }

        var targets = Enumerable.Range(0, 5).Select(index => new Target($"4000{index}", $@"C:\fixture\worktrees\w{index}", $"refs/heads/4000{index}")).ToList();
        var picker = new VersionPicker(targets);
        var previewed = new List<Target>();
        var outputs = new List<StringWriter>();
        var keys = new Queue<Func<int, ConsoleKeyInfo?>>([Script([.. Typed("4000"), Press(ConsoleKey.DownArrow), enter]), Script(enter), Script(enter)]);
        Terminal.Previews(() =>
        {
            var output = new StringWriter();
            outputs.Add(output);
            return Task.FromResult(picker.Show(LiveConsole(output), keys.Dequeue()));
        }, target =>
        {
            previewed.Add(target);
            return Task.FromResult(previewed.Count == 2);
        }).GetAwaiter().GetResult();
        Check(previewed.SequenceEqual([targets[1], targets[1]]) && keys.Count == 1 && picker.Query == "4000" && Shown(outputs[1].ToString()).Contains("/ 4000"),
            "going back from a preview shows the list again with its search and highlight as they were, and a finished preview goes home");
        picker = new VersionPicker(targets);
        previewed.Clear();
        Terminal.Previews(() => Task.FromResult(picker.Show(LiveConsole(new StringWriter()), Script(esc))), target =>
        {
            previewed.Add(target);
            return Task.FromResult(true);
        }).GetAwaiter().GetResult();
        Check(previewed.Count == 0, "Esc in the list goes home without a preview");
    }
}
