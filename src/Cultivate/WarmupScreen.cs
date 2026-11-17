using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// The warm-up progress screen. The probe runs on another task and reports through <see cref="Report"/>; this model only draws
/// and handles keys, so the loop in <see cref="Show"/> can animate without blocking on the keyboard.
/// </summary>
internal sealed class WarmupScreen(Uri url, TimeSpan? estimate, string bestPath, Func<int, int>? random = null)
{
    private const string Secret = "snake";
    private static readonly Color Soil = Color.FromHex("#13261D");
    private static readonly Color Leaf = Color.FromHex("#78B97D");
    private static readonly Color Vine = Color.FromHex("#82C68B");
    private readonly Func<int, int> random = random ?? Random.Shared.Next;
    private WarmupProgress progress = new(0, "requesting the site");
    private string typed = "";
    internal TimeSpan Elapsed { get; set; }
    /// <summary>Set once the probe finishes; the game keeps going until Esc.</summary>
    internal WarmupResult? Result { get; set; }
    internal bool Playing { get; private set; }
    internal SnakeGame? Game { get; private set; }
    internal int Best { get; private set; } = SnakeGame.LoadBest(bestPath);
    internal WarmupProgress Progress => Volatile.Read(ref progress);

    internal void Report(WarmupProgress update) => Volatile.Write(ref progress, update);

    internal static (int Columns, int Rows) BoardSize(int width, int height) => (Math.Min(30, (width - 4) / 2), Math.Min(18, height - 7));

    /// <summary>Handles one key; returns true when the screen should close (Esc while waiting stops the warm-up).</summary>
    internal bool HandleKey(ConsoleKeyInfo key, int width, int height)
    {
        if (Playing) return HandleGameKey(key);
        if (key.Key == ConsoleKey.Escape) return true;
        typed = char.IsLetter(key.KeyChar) ? (typed + char.ToLowerInvariant(key.KeyChar))[^Math.Min(Secret.Length, typed.Length + 1)..] : "";
        if (typed != Secret) return false;
        typed = "";
        Playing = true;
        Fit(width, height);
        return false;
    }

    /// <summary>Starts the game once the terminal is big enough for a board.</summary>
    internal void Fit(int width, int height)
    {
        var (columns, rows) = BoardSize(width, height);
        if (Playing && Game is null && columns >= SnakeGame.MinColumns && rows >= SnakeGame.MinRows)
            Game = new SnakeGame(columns, rows, random);
    }

    private bool HandleGameKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            Keep();
            Playing = false;
            Game = null;
            return Result is not null;
        }
        if (Game is null) return false;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.W:
                Game.Turn(Heading.Up);
                break;
            case ConsoleKey.DownArrow or ConsoleKey.S:
                Game.Turn(Heading.Down);
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.A:
                Game.Turn(Heading.Left);
                break;
            case ConsoleKey.RightArrow or ConsoleKey.D:
                Game.Turn(Heading.Right);
                break;
            case ConsoleKey.P or ConsoleKey.Spacebar:
                Game.TogglePause();
                break;
            case ConsoleKey.Enter when Game.Over:
                Game.Restart();
                break;
        }
        return false;
    }

    /// <summary>Moves the vine one step and keeps a new best score.</summary>
    internal bool Step()
    {
        if (Game is null || !Game.Step()) return false;
        if (Game.Score > Best) Best = Game.Score;
        if (Game.Over) Keep();
        return true;
    }

    private void Keep()
    {
        if (Best > SnakeGame.LoadBest(bestPath)) SnakeGame.SaveBest(bestPath, Best);
    }

    /// <summary>The warm-up's state for the game's status line.</summary>
    internal string GameStatus() => Result switch
    {
        null => $"warming up, {Elapsed.TotalSeconds:0} s",
        { Ready: true } => $"site is ready ({Result.Seconds:0} s), Esc returns",
        { Outcome: WarmupOutcome.Failed } => $"the site answered with an error ({Result.Seconds:0} s), Esc shows it",
        _ => $"still warming after {Result.Seconds:0} s, Esc returns"
    };

    internal IRenderable Render(int width, int height) => Playing ? RenderGame(width, height) : RenderProgress(width);

    private IRenderable RenderProgress(int width)
    {
        var current = Progress;
        var inner = Math.Max(10, width - 4);
        var seconds = Result?.Seconds ?? Elapsed.TotalSeconds;
        var guide = estimate is { } expected ? $"usually ready in about {expected.TotalSeconds:0} s" : "first warm-up, no estimate yet";
        var said = current.Said.Length == 0 ? "" : char.ToUpperInvariant(current.Said[0]) + current.Said[1..];
        var card = TerminalStyle.Card(new Rows(
            Line(inner, ("SITE  ", TerminalStyle.Muted), (url.ToString(), TerminalStyle.Text)),
            new Text(""),
            Bar(inner),
            Line(inner, ($"{seconds:0} s", TerminalStyle.Text), ("   ·   ", TerminalStyle.Border), ($"request {Math.Max(1, current.Attempt)}", TerminalStyle.Text),
                ("   ·   ", TerminalStyle.Border), (guide, TerminalStyle.Muted)),
            new Text(""),
            Line(inner, (said, current.Attempt > 1 || said != "Requesting the site" ? TerminalStyle.Gold : TerminalStyle.Muted))),
            "DEV SITE WARM-UP", TerminalStyle.Accent);
        return new Rows(
            TerminalStyle.Header("WARMING UP", "The site recompiles here, not on your first page load."),
            new Text(""),
            card,
            new Text(""),
            Line(width, ("Esc", TerminalStyle.Gold), (" stop waiting   ", TerminalStyle.Text), ("the site keeps compiling by itself", TerminalStyle.Muted)));
    }

    /// <summary>One line that never wraps: the parts are cut off with an ellipsis where they would pass <paramref name="width"/>.</summary>
    private static Paragraph Line(int width, params (string Text, string Color)[] parts)
    {
        var line = new Paragraph();
        var room = width;
        foreach (var (text, color) in parts)
        {
            if (room <= 0) break;
            var shown = text.Length <= room ? text : text[..Math.Max(0, room - 1)] + "…";
            line.Append(shown, new Style(Color.FromHex(color)));
            room -= shown.Length;
        }
        return line;
    }

    /// <summary>The bar ripens from green to gold toward the history-based estimate, or sweeps back and forth without one.</summary>
    private Paragraph Bar(int width)
    {
        var cells = width - 6;
        var fraction = Result is not null ? 1 : WarmupHistory.Fraction(Elapsed, estimate);
        var bar = new Paragraph();
        var empty = new Style(Color.FromHex(TerminalStyle.Border));
        int first, last;
        if (fraction is { } part)
        {
            first = 0;
            last = (int)Math.Round(part * cells) - 1;
        }
        else
        {
            const int block = 8;
            var travel = Math.Max(1, cells - block);
            var step = (int)(Elapsed.TotalMilliseconds / 60) % (2 * travel);
            first = step < travel ? step : 2 * travel - step;
            last = first + block - 1;
        }
        for (var cell = 0; cell < cells; cell++)
        {
            var ripeness = cells == 1 ? 0 : (float)cell / (cells - 1);
            bar.Append(cell >= first && cell <= last ? "█" : "░", cell >= first && cell <= last
                ? new Style(Color.FromHex(TerminalStyle.Accent).Blend(Color.FromHex(TerminalStyle.Gold), ripeness)) : empty);
        }
        bar.Append(fraction is { } done ? $"{done * 100,5:0}%" : "      ", new Style(Color.FromHex(TerminalStyle.Muted)));
        return bar;
    }

    private IRenderable RenderGame(int width, int height)
    {
        var title = Line(width, ("GARDEN SNAKE", TerminalStyle.Accent), ($"   seeds {Game?.Score ?? 0}", TerminalStyle.Gold), ($"   best {Best}", TerminalStyle.Muted));
        var status = Line(width, (GameStatus(), Result?.Ready == true ? TerminalStyle.Accent : TerminalStyle.Gold));
        if (Game is null || Game.Columns * 2 + 2 > width || Game.Rows + 7 > height)
            return new Rows(title, status, new Text(""), new Text(
                $"Make the terminal at least {SnakeGame.MinColumns * 2 + 4} x {SnakeGame.MinRows + 7} to play. Esc returns."));
        var hint = Game.Over ? $"{Game.Ending} Enter plays again, Esc returns." :
            Game.Paused ? "Paused. P or Space carries on, Esc returns." : "arrows or WASD steer   P pause   Esc back";
        return new Rows(title, status, Board(Game),
            Line(width, (hint, Game.Over || Game.Paused ? TerminalStyle.Gold : TerminalStyle.Muted)));
    }

    // Each cell is two characters wide so the vine moves as far sideways as up and down in a step.
    private static Paragraph Board(SnakeGame game)
    {
        var fence = new Style(Color.FromHex(TerminalStyle.Border));
        var segments = new Dictionary<(int X, int Y), int>();
        for (var index = game.Body.Count - 1; index >= 0; index--) segments[game.Body[index]] = index;
        var board = new Paragraph();
        board.Append("╭" + new string('─', game.Columns * 2) + "╮\n", fence);
        for (var y = 0; y < game.Rows; y++)
        {
            board.Append("│", fence);
            for (var x = 0; x < game.Columns; x++)
            {
                if (segments.TryGetValue((x, y), out var index))
                    board.Append("██", new Style(index == 0 ? Color.FromHex(game.Over ? "#F0A3A3" : TerminalStyle.Accent) : index % 2 == 0 ? Vine : Leaf, Soil));
                else if (game.Seed == (x, y)) board.Append("▐▌", new Style(Color.FromHex(TerminalStyle.Gold), Soil));
                else board.Append("  ", new Style(background: Soil));
            }
            board.Append("│\n", fence);
        }
        board.Append("╰" + new string('─', game.Columns * 2) + "╯", fence);
        return board;
    }

    /// <summary>
    /// Draws until the probe finishes (or, during the game, until Esc), polling keys without blocking so the bar keeps moving.
    /// Esc while waiting cancels <paramref name="cancel"/> and returns the probe's skipped result.
    /// </summary>
    internal WarmupResult Show(IAnsiConsole console, Func<ConsoleKeyInfo?> pollKey, Task<WarmupResult> probe, CancellationTokenSource cancel,
        Func<TimeSpan> clock, Action<TimeSpan> wait)
    {
        var start = clock();
        return console.Live(Render(console.Profile.Width, console.Profile.Height))
            .AutoClear(true).Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                context.Refresh();
                var lastStep = start;
                var lastDraw = start;
                while (true)
                {
                    var now = clock();
                    var (width, height) = (console.Profile.Width, console.Profile.Height);
                    Elapsed = now - start;
                    if (probe.IsCompleted) Result ??= probe.GetAwaiter().GetResult();
                    var changed = false;
                    while (pollKey() is { } key)
                    {
                        changed = true;
                        if (!HandleKey(key, width, height)) continue;
                        if (Result is null)
                        {
                            cancel.Cancel();
                            Result = probe.GetAwaiter().GetResult();
                        }
                        return Result;
                    }
                    if (!Playing && Result is not null) return Result;
                    Fit(width, height);
                    if (Game is not null && now - lastStep >= Game.Interval)
                    {
                        lastStep = now;
                        changed |= Step();
                    }
                    if (changed || now - lastDraw >= TimeSpan.FromMilliseconds(Playing ? 250 : 100))
                    {
                        lastDraw = now;
                        context.UpdateTarget(Render(width, height));
                        context.Refresh();
                    }
                    wait(TimeSpan.FromMilliseconds(15));
                }
            });
    }
}
