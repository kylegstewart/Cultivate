using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// Screens that redraw as keys are pressed. A paint rewrites only the lines that changed, in one write, and keys already queued,
/// such as a held arrow's repeats, are all handled before the next paint: redrawing the whole screen for every key cost 20 to 35 ms
/// on the longer lists, and a held arrow kept moving after it was let go.
/// </summary>
internal static class KeyScreen
{
    /// <summary>
    /// Paints <paramref name="render"/>'s frame and hands each key, with the terminal's height, to <paramref name="handle"/> until it returns true.
    /// A screen clears the terminal and paints from its top, losing its last lines when the terminal is too short. With lines
    /// <paramref name="above"/> it, it loses their top lines first instead, as scrolling would, and keeps them when it closes so
    /// what follows appears below them. An <paramref name="inline"/> screen paints below what the terminal already shows and
    /// erases itself when it closes. The screen paints again when <paramref name="update"/> completes, and closes at once if it fails.
    /// <paramref name="readKey"/> waits up to the given milliseconds for a key.
    /// </summary>
    internal static void Show(IAnsiConsole console, Func<int, int, IRenderable> render, Func<ConsoleKeyInfo, int, bool> handle,
        Func<int, ConsoleKeyInfo?> readKey, bool inline = false, Func<int, int, IRenderable>? above = null, Task? update = null)
    {
        if (!console.Profile.Capabilities.Ansi)
        {
            Live(console, render, handle, readKey, inline, above, update);
            return;
        }
        if (!inline) console.Clear();
        console.Cursor.Hide();
        var painter = new Painter(console, inline);
        var kept = 0;
        try
        {
            var size = (Width: 0, Height: 0);
            var dirty = true;
            // The lines above do not change with keys, so they are rendered again only for a new size or an update.
            List<SegmentLine>? top = null;
            while (true)
            {
                if (size != (console.Profile.Width, console.Profile.Height))
                {
                    painter.Reset();
                    size = (console.Profile.Width, console.Profile.Height);
                    (dirty, top) = (true, null);
                }
                if (update is { IsCompleted: true })
                {
                    if (!update.IsCompletedSuccessfully) return;
                    (update, dirty, top) = (null, true, null);
                }
                if (dirty)
                {
                    top ??= above is null ? [] : Lines(console, above(size.Width, size.Height));
                    var lines = top.Concat(Lines(console, render(size.Width, size.Height))).ToList();
                    var dropped = above is null ? 0 : Math.Max(0, lines.Count - size.Height);
                    kept = Math.Max(0, top.Count - dropped);
                    painter.Paint(lines.Skip(dropped).Take(size.Height).ToList(), size.Width);
                    dirty = false;
                }
                // An update is checked between short waits; keys still wake the wait at once.
                if (readKey(update is null ? 200 : 20) is not { } key) continue;
                for (ConsoleKeyInfo? next = key; next is { } pressed; next = readKey(0))
                    if (handle(pressed, size.Height)) return;
                dirty = true;
            }
        }
        finally
        {
            if (inline || above is not null) painter.Close(kept);
            else console.Clear();
            console.Cursor.Show();
        }
    }

    /// <summary>The renderable's lines as the terminal shows them; <see cref="Segment.SplitLines(IEnumerable{Segment})"/> drops the blank ones.</summary>
    private static List<SegmentLine> Lines(IAnsiConsole console, IRenderable renderable)
    {
        var lines = new List<SegmentLine> { new() };
        foreach (var segment in renderable.GetSegments(console))
        {
            if (segment.IsLineBreak)
            {
                lines.Add([]);
                continue;
            }
            var parts = segment.Text.Split('\n');
            for (var part = 0; part < parts.Length; part++)
            {
                if (part != 0) lines.Add([]);
                if (parts[part].Length != 0) lines[^1].Add(parts.Length == 1 ? segment : new Segment(parts[part], segment.Style));
            }
        }
        if (lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>The Spectre live display, for terminals without escape codes; it redraws the whole screen for every key.</summary>
    private static void Live(IAnsiConsole console, Func<int, int, IRenderable> render, Func<ConsoleKeyInfo, int, bool> handle,
        Func<int, ConsoleKeyInfo?> readKey, bool inline, Func<int, int, IRenderable>? above, Task? update)
    {
        try { update?.Wait(); }
        catch (AggregateException) { return; }
        if (!inline) console.Clear();
        if (above is not null) console.Write(above(console.Profile.Width, console.Profile.Height));
        console.Live(render(console.Profile.Width, console.Profile.Height))
            .AutoClear(true).Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
            .Start(context =>
            {
                context.Refresh();
                var size = (console.Profile.Width, console.Profile.Height);
                while (true)
                {
                    if (readKey(200) is { } key)
                    {
                        for (ConsoleKeyInfo? next = key; next is { } pressed; next = readKey(0))
                            if (handle(pressed, console.Profile.Height)) return;
                    }
                    else if (size == (console.Profile.Width, console.Profile.Height)) continue;
                    size = (console.Profile.Width, console.Profile.Height);
                    context.UpdateTarget(render(size.Width, size.Height));
                    context.Refresh();
                }
            });
    }

    /// <summary>Writes only the lines of each frame that differ from the last one, together in one write.</summary>
    private sealed class Painter(IAnsiConsole console, bool inline)
    {
        private List<SegmentLine> painted = [];
        // An inline screen moves between its lines from the one the cursor is on, since where it starts on the terminal is not known.
        private int row;

        internal void Reset()
        {
            if (painted.Count == 0) return;
            if (inline) Write([Move(0), Segment.Control("\u001b[J")]);
            else console.Clear();
            painted = [];
        }

        internal void Paint(List<SegmentLine> lines, int width)
        {
            static bool Same(SegmentLine left, SegmentLine right) => left.Count == right.Count &&
                left.Zip(right).All(pair => pair.First.Text == pair.Second.Text && pair.First.Style.Equals(pair.Second.Style));
            // Terminals that support synchronized output show the changed lines together; others ignore it.
            var changes = new List<Segment> { Segment.Control("\u001b[?2026h") };
            for (var line = 0; line < Math.Max(lines.Count, painted.Count); line++)
            {
                if (line < lines.Count && line < painted.Count && Same(lines[line], painted[line])) continue;
                changes.Add(Move(line));
                if (line >= lines.Count)
                {
                    changes.Add(Segment.Control("\u001b[2K"));
                    continue;
                }
                changes.AddRange(lines[line]);
                changes.Add(new Segment(new string(' ', Math.Max(0, width - lines[line].CellCount()))));
            }
            changes.Add(Segment.Control("\u001b[?2026l"));
            if (changes.Count > 2) Write(changes);
            painted = lines;
        }

        /// <summary>Erases the screen from line <paramref name="keep"/> down and leaves the cursor there.</summary>
        internal void Close(int keep)
        {
            if (painted.Count != 0) Write([Move(Math.Min(keep, painted.Count)), Segment.Control("\u001b[J")]);
        }

        private Segment Move(int line)
        {
            if (!inline) return Segment.Control($"\u001b[{line + 1};1H");
            var move = line < row ? $"\r\u001b[{row - line}A" : "\r" + string.Concat(Enumerable.Repeat("\n", line - row));
            row = line;
            return Segment.Control(move);
        }

        private void Write(List<Segment> segments) => console.Write(new Segments(segments));
    }

    private sealed class Segments(List<Segment> segments) : Renderable
    {
        protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => segments;
    }

    /// <summary>
    /// The next key, waiting up to <paramref name="wait"/> milliseconds. It wakes as soon as input arrives instead of sleeping in steps,
    /// which on Windows' 15.6 ms timer added up to 30 ms to every key. <see cref="Console.KeyAvailable"/> discards key releases and other
    /// console events, so the wait does not spin on them.
    /// </summary>
    internal static ConsoleKeyInfo? ReadKey(int wait)
    {
        var until = Environment.TickCount64 + wait;
        while (true)
        {
            if (Console.KeyAvailable) return Console.ReadKey(true);
            var left = until - Environment.TickCount64;
            if (left <= 0) return null;
            WaitForSingleObject(GetStdHandle(-10), (uint)left);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    /// <summary>
    /// Sends each of <paramref name="console"/>'s writes to the terminal in one piece. <see cref="Console.Out"/> passes text on in
    /// 256-character pieces, each a separate and slow console write: a full screen took 36 to 230 ms that way, against under 1 ms as one.
    /// </summary>
    internal static void Buffer(IAnsiConsole console) => console.Profile.Out = new Output(console.Profile.Out,
        TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = true }));

    private sealed class Output(IAnsiConsoleOutput terminal, TextWriter writer) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;
        public bool IsTerminal => terminal.IsTerminal;
        public int Width => terminal.Width;
        public int Height => terminal.Height;
        public void SetEncoding(Encoding encoding) => terminal.SetEncoding(encoding);
    }
}
