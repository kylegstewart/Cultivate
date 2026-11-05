using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// The full-size screen header: the app icon's seedling as pixel art beside a CULTIVATE wordmark that ripens from green
/// to gold, with the screen's name and tagline underneath. When the terminal is too narrow, cannot draw Unicode, or is too
/// short to also show the <paramref name="below"/> lines that follow it, it renders the compact
/// <see cref="TerminalStyle.Header"/> instead.
/// </summary>
internal sealed class Banner(string section, string subtitle, int below = 0) : IRenderable
{
    // ANSI Shadow lettering: solid blocks are the letter faces; the box-drawing strokes are their drop shadows.
    internal static readonly string[] Wordmark =
    [
        " ██████╗██╗   ██╗██╗  ████████╗██╗██╗   ██╗ █████╗ ████████╗███████╗",
        "██╔════╝██║   ██║██║  ╚══██╔══╝██║██║   ██║██╔══██╗╚══██╔══╝██╔════╝",
        "██║     ██║   ██║██║     ██║   ██║██║   ██║███████║   ██║   █████╗  ",
        "██║     ██║   ██║██║     ██║   ██║╚██╗ ██╔╝██╔══██║   ██║   ██╔══╝  ",
        "╚██████╗╚██████╔╝███████╗██║   ██║ ╚████╔╝ ██║  ██║   ██║   ███████╗",
        " ╚═════╝ ╚═════╝ ╚══════╝╚═╝   ╚═╝  ╚═══╝  ╚═╝  ╚═╝   ╚═╝   ╚══════╝",
    ];

    // assets/Cultivate.svg as an 18x14 sprite: S sun, L and R leaves, T stem, 1-4 crop rows. Each terminal cell stacks two
    // pixels with a half block, so the sprite fills 18 columns by 7 lines: the wordmark's height plus the tagline's.
    internal static readonly string[] Sprite =
    [
        "...............SS.",
        "..........RR..SSSS",
        "........RRRRR.SSSS",
        ".LLL...RRRRRR..SS.",
        "LLLLLL.RRRRR......",
        ".LLLLLLRRRR.......",
        "..LLLLLTRR........",
        "....LLLT..........",
        ".......T..........",
        ".......T.111111111",
        "111111111.........",
        "..........22222222",
        "...2222222........",
        "........33333.4444",
    ];

    private static readonly Dictionary<char, Color> Pixels = new()
    {
        ['S'] = Color.FromHex(TerminalStyle.Gold), ['L'] = Color.FromHex("#82C68B"),
        ['R'] = Color.FromHex(TerminalStyle.Accent), ['T'] = Color.FromHex(TerminalStyle.Accent),
        ['1'] = Color.FromHex("#E8EFD4"), ['2'] = Color.FromHex("#A9D58C"),
        ['3'] = Color.FromHex("#78B97D"), ['4'] = Color.FromHex(TerminalStyle.Gold),
    };

    // Letter faces ripen left to right from leaf green through the accent to the sun's gold.
    private static readonly Color[] Ripening = [Color.FromHex("#78B97D"), Color.FromHex(TerminalStyle.Accent), Color.FromHex(TerminalStyle.Gold)];
    private static readonly Color Soil = Color.FromHex("#0E1A15");
    private const int Gap = 3;

    /// <summary>
    /// Lines the banner occupies: the sprite (or the wordmark and tagline) inside the card's borders. A tagline too long for
    /// the space beside the sprite wraps onto one more line; the home screen's never does.
    /// </summary>
    internal static int Height => Sprite.Length / 2 + 2;
    private static int WordmarkWidth => Wordmark[0].Length + 4;
    private static int FullWidth => Sprite[0].Length + Gap + WordmarkWidth;

    /// <summary>Whether the full banner, rather than the compact header, is drawn on <paramref name="console"/>.</summary>
    internal bool FitsOn(IAnsiConsole console) =>
        Fits(console.Profile.Capabilities.Unicode, console.Profile.Width, console.Profile.Height);

    private bool Fits(bool unicode, int width, int height) => unicode && width >= WordmarkWidth && height >= Height + below;

    public Measurement Measure(RenderOptions options, int maxWidth) => Choose(options, maxWidth).Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Choose(options, maxWidth).Render(options, maxWidth);

    private IRenderable Choose(RenderOptions options, int maxWidth)
    {
        if (!Fits(options.Unicode, maxWidth, options.ConsoleSize.Height)) return TerminalStyle.Header(section, subtitle);
        var words = new Rows(Lettering(), new Markup(
            $"[bold {TerminalStyle.Gold}]{Markup.Escape(section)}[/]  [{TerminalStyle.Border}]·[/]  [{TerminalStyle.Muted}]{Markup.Escape(subtitle)}[/]"));
        // A grid pads every line to the same width, so centering moves the block as one instead of centering each line.
        var art = new Grid();
        var seedling = maxWidth >= FullWidth;
        if (seedling) art.AddColumn(new GridColumn { NoWrap = true, Padding = new(0, 0, Gap, 0) });
        art.AddColumn(new GridColumn { Padding = new(0) });
        art.AddRow(seedling ? [Seedling(), words] : [words]);
        return new Panel(Align.Center(art)).Border(BoxBorder.Rounded).BorderColor(Color.FromHex(TerminalStyle.Border)).Expand();
    }

    private static Paragraph Lettering()
    {
        var text = new Paragraph();
        var last = Wordmark[0].Length - 1;
        for (var row = 0; row < Wordmark.Length; row++)
        {
            if (row != 0) text.Append("\n");
            for (var column = 0; column <= last; column++)
            {
                var glyph = Wordmark[row][column];
                if (glyph == ' ') { text.Append(" "); continue; }
                var position = (float)column / last * (Ripening.Length - 1);
                var stop = Math.Min((int)position, Ripening.Length - 2);
                var face = Ripening[stop].Blend(Ripening[stop + 1], position - stop);
                text.Append(glyph.ToString(), new Style(glyph == '█' ? face : face.Blend(Soil, 0.55f)));
            }
        }
        return text;
    }

    private static Paragraph Seedling()
    {
        var art = new Paragraph();
        for (var row = 0; row < Sprite.Length; row += 2)
        {
            if (row != 0) art.Append("\n");
            for (var column = 0; column < Sprite[row].Length; column++)
            {
                Color? top = Pixels.TryGetValue(Sprite[row][column], out var upper) ? upper : null;
                Color? bottom = Pixels.TryGetValue(Sprite[row + 1][column], out var lower) ? lower : null;
                if (top is null && bottom is null) art.Append(" ");
                else if (top is null) art.Append("▄", new Style(bottom));
                else if (bottom is null || bottom == top) art.Append(bottom is null ? "▀" : "█", new Style(top));
                else art.Append("▀", new Style(top, bottom));
            }
        }
        return art;
    }
}
