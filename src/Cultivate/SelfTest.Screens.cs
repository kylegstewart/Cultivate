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
            var keys = Keys(4, enter);
            var chosen = new ActionPicker(Terminal.HomeActions).Show("Actions", LiveConsole(output), _ => keys.Dequeue());
            var text = output.ToString();
            var paints = Paints(text);
            var drawn = Capture(new ActionPicker(Terminal.HomeActions).Render("Actions", 30), 100, 30).Split('\n').Length;
            Check(chosen == Terminal.HomeActions[5] && paints.Length == 3 && Rows(text).Length == 0 && Touched(paints[0]) == drawn && !text.Contains("\u001b[2J"),
                "an action menu paints below what the terminal shows, moving only between its own lines");
            Check(Touched(paints[1]) == 3 && Touched(paints[2]) == 3, "moving the menu's highlight rewrites the two rows and the explanation");
            Check(text.Split("\u001b[?2026l").Last().StartsWith($"\r\u001b[{drawn - 1}A\u001b[J", StringComparison.Ordinal),
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
    }
}
