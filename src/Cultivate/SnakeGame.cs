using System.Text.Json;

namespace Cultivate;

internal enum Heading { Up, Down, Left, Right }

/// <summary>
/// The rules of the garden snake: a vine that grows across a fenced plot eating seeds. Pure and deterministic given
/// its random source, which returns a number from 0 up to (not including) its argument.
/// </summary>
internal sealed class SnakeGame
{
    internal const int MinColumns = 10, MinRows = 8;
    private readonly Func<int, int> random;
    private readonly List<(int X, int Y)> body = [];
    private Heading? turn;
    internal int Columns { get; }
    internal int Rows { get; }
    /// <summary>The head first.</summary>
    internal IReadOnlyList<(int X, int Y)> Body => body;
    internal (int X, int Y) Seed { get; private set; }
    internal Heading Heading { get; private set; }
    internal int Score { get; private set; }
    internal bool Over { get; private set; }
    /// <summary>Why the game ended: the fence, the vine itself, or a full plot.</summary>
    internal string Ending { get; private set; } = "";
    internal bool Paused { get; private set; }
    /// <summary>Time between steps; the vine speeds up a little with each seed.</summary>
    internal TimeSpan Interval => TimeSpan.FromMilliseconds(Math.Max(70, 150 - Score * 4));

    internal SnakeGame(int columns, int rows, Func<int, int> random)
    {
        Columns = columns;
        Rows = rows;
        this.random = random;
        Restart();
    }

    internal void Restart()
    {
        body.Clear();
        var x = Columns / 2;
        var y = Rows / 2;
        body.AddRange([(x, y), (x - 1, y), (x - 2, y)]);
        Heading = Heading.Right;
        turn = null;
        Score = 0;
        Over = false;
        Ending = "";
        Paused = false;
        PlantSeed();
    }

    /// <summary>Steers on the next step. Turning back into the vine is ignored.</summary>
    internal void Turn(Heading heading)
    {
        if (Over || heading == Opposite(Heading)) return;
        turn = heading;
    }

    internal void TogglePause()
    {
        if (!Over) Paused = !Paused;
    }

    /// <summary>Moves one cell; returns whether anything changed.</summary>
    internal bool Step()
    {
        if (Over || Paused) return false;
        if (turn is { } next) Heading = next;
        turn = null;
        var (x, y) = body[0];
        var head = Heading switch
        {
            Heading.Up => (x, y - 1),
            Heading.Down => (x, y + 1),
            Heading.Left => (x - 1, y),
            _ => (x + 1, y)
        };
        if (head.Item1 < 0 || head.Item2 < 0 || head.Item1 >= Columns || head.Item2 >= Rows)
        {
            End("The vine hit the fence.");
            return true;
        }
        var grows = head == Seed;
        // The tail moves out of the way this step unless the vine grows.
        if (body.Take(grows ? body.Count : body.Count - 1).Contains(head))
        {
            End("The vine ran into itself.");
            return true;
        }
        body.Insert(0, head);
        if (!grows)
        {
            body.RemoveAt(body.Count - 1);
            return true;
        }
        Score++;
        PlantSeed();
        return true;
    }

    private void End(string ending)
    {
        Over = true;
        Ending = ending;
    }

    private void PlantSeed()
    {
        var free = new List<(int X, int Y)>();
        for (var y = 0; y < Rows; y++)
            for (var x = 0; x < Columns; x++)
                if (!body.Contains((x, y))) free.Add((x, y));
        if (free.Count == 0)
        {
            End("The whole plot is planted.");
            return;
        }
        Seed = free[random(free.Count)];
    }

    private static Heading Opposite(Heading heading) => heading switch
    {
        Heading.Up => Heading.Down,
        Heading.Down => Heading.Up,
        Heading.Left => Heading.Right,
        _ => Heading.Left
    };

    /// <summary>The best score kept under the data folder; 0 when there is none yet or it cannot be read.</summary>
    internal static int LoadBest(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Best", out var best) && best.TryGetInt32(out var value) ? value : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    internal static void SaveBest(string path, int best)
    {
        try { Files.WriteJson(path, new { Best = best }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
