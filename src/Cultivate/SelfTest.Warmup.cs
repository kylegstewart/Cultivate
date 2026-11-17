using System.Net;
using System.Text.Json;

namespace Cultivate;

internal static partial class SelfTest
{
    private static readonly Uri FixtureSite = new("https://mywebfarm-dev02.example.test/");

    /// <summary>Answers each request with the next scripted response; never touches the network.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests.Add(request);
            var response = await respond(request, Requests.Count, cancel);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private static HttpResponseMessage Page(int status, string html, string? finalUrl = null) => new((HttpStatusCode)status)
    {
        Content = new StringContent(html),
        RequestMessage = finalUrl is null ? null : new HttpRequestMessage(HttpMethod.Get, finalUrl)
    };

    private static string ErrorPage(string title, string body) => $$"""
        <!DOCTYPE html>
        <html>
            <head>
                <title>{{title}}</title>
                <meta name="viewport" content="width=device-width" />
            </head>
            <body bgcolor="white">
                    <span><H1>Server Error in '/' Application.<hr width=100% size=1 color=silver></H1>
                    <h2> <i>{{title}}</i> </h2></span>
                    <font face="Arial, Helvetica, Geneva, SunSans-Regular, sans-serif ">
                    {{body}}
            </body>
        </html>
        """;

    private static readonly string TimedOutPage = ErrorPage("Request timed out.", """
        <b> Description: </b>An unhandled exception occurred during the execution of the current web request. Please review the stack trace for more information about the error and where it originated in the code.
        <br><br>
        <b> Exception Details: </b>System.Web.HttpException: Request timed out.<br><br>
        """);

    private static readonly string CompilationPage = ErrorPage("Compilation Error", """
        <b> Description: </b>An error occurred during the compilation of a resource required to service this request. Please review the following specific error details and modify your source code appropriately.
        <br><br>
        <b> Compiler Error Message: </b>CS0103: The name &#39;orderTotal&#39; does not exist in the current context<br><br>
        <b>Source Error:</b> <br><br>
        <table width=100% bgcolor="#ffffcc"><tr><td><code><pre>
        Line 41:  {
        <font color=red>Line 42:      lblTotal.Text = orderTotal.ToString();
        </font>Line 43:  }</pre></code></td></tr></table>
        <br>
        <b> Source File: </b> c:\inetpub\MyMeyer\App_Code\OrderTotals.cs<b> &nbsp;&nbsp; Line: </b> 42
        <br><br>
        """);

    private static readonly string ParserPage = ErrorPage("Parser Error", """
        <b> Description: </b>An error occurred during the parsing of a resource required to service this request. Please review the following specific parse error details and modify your source file appropriately.
        <br><br>
        <b> Parser Error Message: </b>Could not load type &#39;MiscApps.PriceGroupMatrix&#39;.<br><br>
        <b> Source File: </b> /DesktopModules/PriceGroupMatrix/PriceGroupMatrix.ascx<b> &nbsp;&nbsp; Line: </b> 1
        <br><br>
        """);

    private static readonly string RemoteRuntimePage = ErrorPage("Runtime Error", """
        <b> Description: </b>An application error occurred on the server. The current custom error settings for this application prevent the details of the application error from being viewed remotely (for security reasons). It could, however, be viewed by browsers running on the local server machine.
        <br><br>
        <b>Details:</b> To enable the details of this specific error message to be viewable on remote machines, please create a &lt;customErrors&gt; tag.<br><br>
        """);

    private const string HomePage = "<html><head><title>My Meyer Home</title></head><body>DNN</body></html>";

    private static string ApplicationHost(string sites) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
            <system.applicationHost>
                <sites>
                    {sites}
                    <siteDefaults><logFile logFormat="W3C" /></siteDefaults>
                    <applicationDefaults applicationPool="DefaultAppPool" />
                    <virtualDirectoryDefaults allowSubDirConfig="true" />
                </sites>
            </system.applicationHost>
        </configuration>
        """;

    private static string Site(string name, string root, params string[] bindings) => $"""
        <site name="{name}" id="3">
            <application path="/" applicationPool="{name}">
                <virtualDirectory path="/" physicalPath="{root}" />
                <virtualDirectory path="/ProductImages" physicalPath="\\FS03\Media\WebImages" />
            </application>
            <application path="/AutoPrint" applicationPool="ASPDotNet2.0">
                <virtualDirectory path="/" physicalPath="{root}\AutoPrint" />
            </application>
            <bindings>
                {string.Concat(bindings.Select(binding => $"<binding protocol=\"{binding.Split(' ')[0]}\" bindingInformation=\"{binding.Split(' ')[1]}\" />"))}
            </bindings>
        </site>
        """;

    private static async Task CheckWarmup(string home)
    {
        CheckSiteDiscovery();
        await CheckWarmupProbe();
        CheckWarmupHistory(home);
        CheckSnakeGame();
        CheckWarmupScreen(home);
        await CheckWarmupCommands(home);
    }

    private static void CheckSiteDiscovery()
    {
        var real = Site("MyMeyer", @"C:\inetpub\MyMeyer", "http *:80:mywebfarm-lb-dev.meyerdistributing.com", "http *:6001:",
            "http *:80:mywebfarm-dev02.meyerdistributing.com", "https *:443:mywebfarm-dev02.meyerdistributing.com",
            "https *:443:mywebfarm-lb-dev.meyerdistributing.com");
        var others = Site("MyMeyer4", @"C:\inetpub\MyMeyer-4", "https *:443:mywebfarm-dev02-4.meyerdistributing.com") +
            Site("Online", @"C:\inetpub\Online", "https *:443:onlinewebfarm-dev02.meyerdistributing.com");
        var config = ApplicationHost(others + real);
        Check(Warmup.SiteUrlFromConfig(config, @"C:\inetpub\MyMeyer", "webfarm-dev02") == "https://mywebfarm-dev02.meyerdistributing.com/",
            "site discovery picks the https binding that names the server from the site whose root folder is the webfarm root");
        Check(Warmup.SiteUrlFromConfig(config, @"c:\INETPUB\MyMeyer\", "webfarm-dev02.meyerdistributing.com") ==
            "https://mywebfarm-dev02.meyerdistributing.com/", "site discovery compares folders case-insensitively and matches a fully qualified server");
        Check(Warmup.SiteUrlFromConfig(config, @"C:\inetpub\MyMeyer-4", "webfarm-dev02") == "https://mywebfarm-dev02-4.meyerdistributing.com/",
            "site discovery matches the exact root folder, not one that starts the same way");
        Check(Warmup.SiteUrlFromConfig(ApplicationHost(Site("Blank", @"C:\inetpub\Blank", "http *:6001:")), @"C:\inetpub\Blank", "webfarm-dev02") ==
            "http://webfarm-dev02:6001/", "a blank host header means the server's own name and the binding's port");
        Check(Warmup.SiteUrlFromConfig(ApplicationHost(Site("Mixed", @"C:\inetpub\Mixed", "http *:8080:", "http *:80:dev.example.test")),
            @"C:\inetpub\Mixed", "webfarm-dev02") == "http://dev.example.test/", "a host header is preferred over a blank one");
        Check(Warmup.SiteUrlFromConfig(ApplicationHost(Site("System", @"%SystemDrive%\inetpub\wwwroot", "net.tcp 808:*", "http 10.1.2.3:8081:")),
            @"C:\inetpub\wwwroot", "webfarm-dev02") == "http://10.1.2.3:8081/",
            "site discovery expands %SystemDrive%, skips non-web bindings, and uses a binding's own IP address");
        Check(Warmup.SiteUrlFromConfig(config, @"C:\inetpub\Elsewhere", "webfarm-dev02") is null, "no site is suggested when no site's root is the webfarm root");
        Check(Warmup.SiteUrlFromConfig("<configuration><system.applicationHost><sites>", @"C:\inetpub\MyMeyer", "webfarm-dev02") is null &&
            Warmup.SiteUrlFromConfig("", @"C:\inetpub\MyMeyer", "webfarm-dev02") is null, "malformed applicationHost.config is ignored");
        Check(Warmup.LocalPath(@"\\webfarm-dev02\C$\inetpub\MyMeyer") == @"C:\inetpub\MyMeyer" &&
            Warmup.LocalPath(@"\\webfarm-dev02\d$\sites") == @"d:\sites" && Warmup.LocalPath(@"\\webfarm-dev02\inetpub\MyMeyer") is null,
            "only administrative drive shares map to a folder on the server");
        Check(Warmup.SuggestSiteUrl(@"C:\inetpub\MyMeyer") == "" && Warmup.SuggestSiteUrl("") == "",
            "a local or empty webfarm root suggests no site address");
        Check(Warmup.SuggestSiteUrl(@"\\webfarm-dev02\inetpub\MyMeyer") == "http://webfarm-dev02/",
            "a share that cannot be mapped to the server's folder falls back to the server name");
    }

    private static async Task CheckWarmupProbe()
    {
        async Task<(WarmupResult Result, ScriptedHandler Handler, List<WarmupProgress> Reports, TimeSpan Waited)> Probe(
            Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? limit = null,
            Func<TimeSpan, TimeSpan>? spend = null, CancellationToken cancel = default)
        {
            var now = TimeSpan.Zero;
            var waited = TimeSpan.Zero;
            var handler = new ScriptedHandler(async (request, call, token) =>
            {
                now += spend?.Invoke(now) ?? TimeSpan.FromSeconds(5);
                return await respond(request, call, token);
            });
            var reports = new List<WarmupProgress>();
            var options = new WarmupOptions(limit ?? TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(2), () => now,
                (delay, _) =>
                {
                    now += delay;
                    waited += delay;
                    return Task.CompletedTask;
                });
            var result = await Warmup.Run(FixtureSite, handler, options, reports.Add, cancel);
            return (result, handler, reports, waited);
        }

        var (result, handler, reports, waited) = await Probe((_, call, _) => Task.FromResult(call switch
        {
            1 => Page(500, TimedOutPage),
            2 => Page(503, "<html><title>Service Unavailable</title></html>"),
            _ => Page(200, HomePage)
        }));
        Check(result is { Ready: true, Outcome: WarmupOutcome.Ready, Status: 200, Attempts: 3 } && result.Seconds == 19 && waited == TimeSpan.FromSeconds(4),
            "a timed-out request and a restarting app pool are retried until the site answers");
        Check(reports.Any(report => report.Said == "recompiling; the last request timed out") &&
            reports.Any(report => report.Said == "app pool restarting (503)"), "progress says in plain words what the site last answered");
        Check(handler.Requests.All(request => request.Method == HttpMethod.Get && request.RequestUri == FixtureSite),
            "the warm-up only sends GET requests to the configured address");
        Check(result.Summary.Contains("200 OK") && result.Summary.Contains("3 requests"), "a ready result reports its status and request count");

        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(200, "<html>Sign in</html>",
            "https://mywebfarm-dev02.example.test/DesktopModules/AuthenticationServices/ActiveDirectory/WindowsSignin.aspx?portalid=0")));
        Check(result.Ready && result.Detail.Contains("WindowsSignin.aspx"), "a redirect to a sign-in page that answers counts as ready");

        (result, handler, _, _) = await Probe((_, _, _) => Task.FromResult(Page(500, CompilationPage)));
        Check(result is { Ready: false, Outcome: WarmupOutcome.Failed, Status: 500, Attempts: 1 } && handler.Requests.Count == 1,
            "a compilation error stops the warm-up instead of retrying");
        Check(result.Error is { Title: "Compilation Error", Line: 42, Hidden: false } &&
            result.Error.Message == "CS0103: The name 'orderTotal' does not exist in the current context" &&
            result.Error.File == @"c:\inetpub\MyMeyer\App_Code\OrderTotals.cs",
            "a compilation error page yields its title, compiler message, source file, and line");
        Check(result.Summary == "Compilation Error: CS0103: The name 'orderTotal' does not exist in the current context" &&
            result.Detail.StartsWith(@"c:\inetpub\MyMeyer\App_Code\OrderTotals.cs, line 42"), "a compilation error is summarized for a developer");
        var card = Capture(Warmup.ResultCard(result), 100, 30);
        Check(card.Contains("SITE ERROR") && card.Contains("CS0103") && card.Contains("OrderTotals.cs, line 42"),
            "the result card shows the compile error's message, file, and line");

        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(500, ParserPage)));
        Check(result.Error is { Title: "Parser Error", Line: 1 } && result.Error.Message == "Could not load type 'MiscApps.PriceGroupMatrix'." &&
            result.Error.File == "/DesktopModules/PriceGroupMatrix/PriceGroupMatrix.ascx", "a parser error page yields its message, file, and line");

        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(500, RemoteRuntimePage)));
        Check(result is { Outcome: WarmupOutcome.Failed, Error.Hidden: true } && result.Summary.Contains("hides the details") &&
            result.Detail.Contains("on the server itself") && result.Detail.Contains("DNN log"),
            "the generic remote Runtime Error page says the details are hidden and where to find them");

        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(200, HomePage, "https://mywebfarm-dev02.example.test/ErrorPage.aspx?aspxerrorpath=/Default.aspx")));
        Check(result is { Outcome: WarmupOutcome.Failed } && result.Summary.Contains("error page"), "a redirect to the custom error page is not ready");

        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(401, "<html><title>401 - Unauthorized</title></html>")));
        Check(result is { Outcome: WarmupOutcome.Failed, Status: 401 } && result.Summary.Contains("could not see a page"),
            "401 means the site answered but Cultivate could not see a page");
        (result, _, _, _) = await Probe((_, _, _) => Task.FromResult(Page(404, "<html><title>404 - File or directory not found.</title></html>")));
        Check(result is { Outcome: WarmupOutcome.Failed, Status: 404, Attempts: 1 } && result.Summary.Contains("404") &&
            result.Detail.Contains("File or directory not found"), "other error statuses are reported with the page's title");

        (result, _, reports, _) = await Probe((_, call, _) => call switch
        {
            1 => throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it."),
            2 => throw new TaskCanceledException("The request timed out."),
            3 => Task.FromResult(Page(502, "Bad Gateway")),
            _ => Task.FromResult(Page(200, HomePage))
        });
        Check(result is { Ready: true, Attempts: 4 } && reports.Any(report => report.Said == "not accepting connections yet") &&
            reports.Any(report => report.Said.StartsWith("no answer within 150 s")),
            "refused connections, attempt timeouts, and gateway errors are retried");

        (result, handler, _, _) = await Probe((_, _, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));
        Check(result is { Outcome: WarmupOutcome.Failed, Attempts: 1 } && result.Summary.Contains("could not be found") && handler.Requests.Count == 1,
            "an address that does not resolve fails at once");

        (result, handler, _, _) = await Probe((_, _, _) => Task.FromResult(Page(500, TimedOutPage)), spend: _ => TimeSpan.FromSeconds(110));
        Check(result is { Outcome: WarmupOutcome.StillWarming, Ready: false, Attempts: 3 } && result.Summary.StartsWith("Still warming up after 33") &&
            result.Detail.Contains("timed out"), "the overall limit ends the warm-up as still warming");

        using (var cancel = new CancellationTokenSource())
        {
            (result, _, _, _) = await Probe(async (_, _, token) =>
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return Page(200, HomePage);
            }, cancel: cancel.Token);
            Check(result is { Outcome: WarmupOutcome.Skipped, Ready: false } && result.Summary.Contains("keeps compiling"),
                "stopping the warm-up records a skipped result");
        }
        var json = JsonSerializer.Deserialize<WarmupResult>(JsonSerializer.Serialize(result, Files.Json), Files.Json);
        Check(json == result, "a warm-up result round-trips through JSON");
    }

    private static void CheckWarmupHistory(string home)
    {
        var path = Path.Combine(home, "warmup", "warmup-history.json");
        var history = WarmupHistory.Load(path);
        Check(history.Estimate(true) is null && history.Estimate(false) is null, "there is no estimate before the first warm-up");
        foreach (var seconds in new[] { 200.0, 60, 70, 80, 65 }) history.Record(true, seconds, path);
        history.Record(false, 8, path);
        var loaded = WarmupHistory.Load(path);
        Check(loaded.Estimate(true) == TimeSpan.FromSeconds(70) && loaded.Estimate(false) == TimeSpan.FromSeconds(8),
            "estimates are the median of recent warm-ups, kept apart for DLL and frontend deployments");
        for (var index = 0; index < 20; index++) loaded.Record(true, 30, path);
        Check(WarmupHistory.Load(path).Compiled.Count == 10 && WarmupHistory.Load(path).Estimate(true) == TimeSpan.FromSeconds(30),
            "the history keeps only recent warm-ups");
        File.WriteAllText(path, "{ not json");
        Check(WarmupHistory.Load(path).Estimate(true) is null, "an unreadable history starts over instead of failing");

        var estimate = TimeSpan.FromSeconds(60);
        Check(WarmupHistory.Fraction(TimeSpan.FromSeconds(10), null) is null, "without history the bar is indeterminate");
        Check(WarmupHistory.Fraction(TimeSpan.Zero, estimate) == 0 && Math.Abs(WarmupHistory.Fraction(TimeSpan.FromSeconds(30), estimate)!.Value - 0.45) < 1e-9 &&
            Math.Abs(WarmupHistory.Fraction(estimate, estimate)!.Value - 0.9) < 1e-9, "the bar fills toward the estimate");
        var late = new[] { 61, 90, 120, 600, 6000 }.Select(seconds => WarmupHistory.Fraction(TimeSpan.FromSeconds(seconds), estimate)!.Value).ToList();
        Check(late.Zip(late.Skip(1)).All(pair => pair.First <= pair.Second) && late.All(value => value is > 0.9 and < 1),
            "past the estimate the bar keeps creeping but never reaches 100% before the site answers");
    }

    private static void CheckSnakeGame()
    {
        var game = new SnakeGame(10, 8, _ => 0);
        Check(game.Body.SequenceEqual([(5, 4), (4, 4), (3, 4)]) && game.Heading == Heading.Right && game.Seed == (0, 0) && game.Score == 0,
            "a new game starts with a three-cell vine heading right and a seed");
        game.Step();
        Check(game.Body.SequenceEqual([(6, 4), (5, 4), (4, 4)]), "a step moves the vine forward without growing");
        game.Turn(Heading.Left);
        game.Step();
        Check(game.Heading == Heading.Right && game.Body[0] == (7, 4), "turning back into the vine is ignored");
        game.Turn(Heading.Up);
        game.Step();
        Check(game.Heading == Heading.Up && game.Body[0] == (7, 3), "turning steers on the next step");
        game.TogglePause();
        Check(!game.Step() && game.Body[0] == (7, 3) && game.Paused, "a paused game does not move");
        game.TogglePause();
        game.Step();
        Check(game.Body[0] == (7, 2), "resuming carries on");
        game.Step();
        game.Step();
        game.Step();
        Check(game is { Over: true, Ending: "The vine hit the fence." } && !game.Step(), "hitting the fence ends the game");
        game.Turn(Heading.Down);
        game.Restart();
        Check(!game.Over && game.Score == 0 && game.Body.Count == 3 && game.Heading == Heading.Right, "restarting starts a fresh game");

        // Plants every seed directly ahead of the vine so it grows on each step.
        SnakeGame? growing = null;
        var feed = true;
        int Ahead(int count)
        {
            if (growing is null || !feed) return 0;
            var (x, y) = growing.Body[0];
            var target = growing.Heading switch { Heading.Up => (x, y - 1), Heading.Down => (x, y + 1), Heading.Left => (x - 1, y), _ => (x + 1, y) };
            var free = Enumerable.Range(0, growing.Rows).SelectMany(row => Enumerable.Range(0, growing.Columns).Select(column => (column, row)))
                .Where(cell => !growing.Body.Contains(cell)).ToList();
            return Math.Max(0, free.IndexOf(target));
        }
        growing = new SnakeGame(10, 8, Ahead);
        growing.Restart();
        var interval = growing.Interval;
        growing.Step();
        Check(growing.Score == 1 && growing.Body.Count == 4 && growing.Body[0] == (6, 4) && !growing.Body.Contains(growing.Seed),
            "eating a seed grows the vine, scores, and plants the next seed off the vine");
        Check(growing.Interval < interval, "the vine speeds up as it grows");
        feed = false;
        growing.Turn(Heading.Up);
        growing.Step();
        growing.Turn(Heading.Left);
        growing.Step();
        growing.Turn(Heading.Down);
        growing.Step();
        Check(!growing.Over && growing.Body[0] == (5, 4), "the head may follow into the cell the tail is leaving");
        growing = new SnakeGame(10, 8, Ahead);
        feed = true;
        growing.Restart();
        growing.Step();
        growing.Step();
        growing.Step();
        feed = false;
        growing.Turn(Heading.Up);
        growing.Step();
        growing.Turn(Heading.Left);
        growing.Step();
        growing.Turn(Heading.Down);
        growing.Step();
        Check(growing is { Over: true, Ending: "The vine ran into itself.", Score: 3 }, "running into itself ends the game");

        var full = new SnakeGame(4, 1, _ => 0);
        Check(full.Seed == (3, 0), "the only free cell gets the seed");
        full.Step();
        Check(full is { Over: true, Score: 1, Ending: "The whole plot is planted." }, "filling the plot ends the game");

        var random = new Random(7);
        var seedOnVine = false;
        for (var round = 0; round < 40; round++)
        {
            var play = new SnakeGame(12, 8, random.Next);
            for (var step = 0; step < 200 && !play.Over; step++)
            {
                if (step % 5 == 0) play.Turn((Heading)random.Next(4));
                play.Step();
                seedOnVine |= !play.Over && play.Body.Contains(play.Seed);
            }
        }
        Check(!seedOnVine, "a seed is never planted on the vine");
    }

    private static void CheckWarmupScreen(string home)
    {
        var best = Path.Combine(home, "warmup", "snake.json");
        var longUrl = new Uri("https://mywebfarm-dev02.meyerdistributing.com/" + new string('x', 150) + "/");
        static ConsoleKeyInfo Key(char character, ConsoleKey key) => new(character, key, false, false, false);
        static ConsoleKeyInfo Letter(char character) =>
            new(character, Enum.Parse<ConsoleKey>(char.ToUpperInvariant(character).ToString()), char.IsUpper(character), false, false);
        var escape = Key('\u001b', ConsoleKey.Escape);

        foreach (var (width, height) in new[] { (60, 24), (80, 24), (120, 35) })
        {
            var counts = new List<int>();
            foreach (var screen in new[]
            {
                new WarmupScreen(FixtureSite, null, best) { Elapsed = TimeSpan.FromSeconds(3) },
                new WarmupScreen(longUrl, TimeSpan.FromSeconds(66), best) { Elapsed = TimeSpan.FromSeconds(41) },
            })
            {
                screen.Report(new(2, "recompiling; the last request timed out " + new string('.', 200)));
                var lines = Capture(screen.Render(width, height), width, height).Split('\n');
                counts.Add(lines.Length);
                Check(lines.Length < height && lines.All(line => line.Length <= width) && lines.Any(line => line.Contains('█')) &&
                    lines.Any(line => line.Contains("Esc")) && lines.Any(line => line.Contains("request 2")),
                    $"the warm-up screen fits {width}x{height} with its bar, request count, and Esc");
            }
            Check(counts.Distinct().Count() == 1, $"long addresses and messages do not wrap at {width}x{height}");
            var game = new WarmupScreen(FixtureSite, null, best);
            foreach (var character in "snake") game.HandleKey(Letter(character), width, height);
            var screenLines = Capture(game.Render(width, height), width, height).Split('\n');
            Check(game.Playing && game.Game is not null && screenLines.Length < height && screenLines.All(line => line.Length <= width) &&
                screenLines.Any(line => line.Contains("GARDEN SNAKE")) && screenLines.Any(line => line.Contains("▐▌")) &&
                screenLines.Count(line => line.StartsWith('│')) == game.Game.Rows, $"the game fits {width}x{height} with a full board");
        }
        var estimated = new WarmupScreen(FixtureSite, TimeSpan.FromSeconds(60), best) { Elapsed = TimeSpan.FromSeconds(30) };
        var progress = Capture(estimated.Render(80, 24), 80, 24);
        Check(progress.Contains("45%") && progress.Contains("usually ready in about 60 s") && progress.Contains("30 s") &&
            progress.Contains("Requesting the site"), "the bar shows progress toward the usual warm-up time");
        Check(Capture(new WarmupScreen(FixtureSite, null, best).Render(80, 24), 80, 24).Contains("first warm-up, no estimate yet"),
            "the first warm-up says it has no estimate");
        var screenText = Capture(new WarmupScreen(FixtureSite, null, best).Render(120, 35), 120, 35);
        Check(!screenText.Contains("snake", StringComparison.OrdinalIgnoreCase) && !screenText.Contains("GARDEN"),
            "the progress screen gives no hint of the game");

        var typing = new WarmupScreen(FixtureSite, null, best);
        foreach (var character in "snaxesnak") typing.HandleKey(Letter(character), 80, 24);
        typing.HandleKey(Key('\r', ConsoleKey.Enter), 80, 24);
        typing.HandleKey(Letter('e'), 80, 24);
        Check(!typing.Playing, "other typing does not start the game");
        foreach (var character in "xxSNaKe") typing.HandleKey(Letter(character), 80, 24);
        Check(typing.Playing, "typing snake in any case starts the game");
        Check(!typing.HandleKey(escape, 80, 24) && !typing.Playing, "Esc leaves the game and returns to the progress screen");
        Check(typing.HandleKey(escape, 80, 24), "Esc on the progress screen stops waiting");

        var small = new WarmupScreen(FixtureSite, null, best);
        foreach (var character in "snake") small.HandleKey(Letter(character), 22, 12);
        Check(small.Playing && small.Game is null && Capture(small.Render(22, 12), 22, 12).Contains("Make the terminal"),
            "a terminal too small for the board says so instead of drawing it");

        var player = new WarmupScreen(FixtureSite, null, best, _ => 0);
        foreach (var character in "snake") player.HandleKey(Letter(character), 80, 24);
        player.HandleKey(Key('\0', ConsoleKey.UpArrow), 80, 24);
        player.Step();
        Check(player.Game!.Heading == Heading.Up, "arrow keys steer the vine");
        player.HandleKey(Letter('a'), 80, 24);
        player.Step();
        Check(player.Game.Heading == Heading.Left, "WASD steers the vine");
        player.HandleKey(Key('p', ConsoleKey.P), 80, 24);
        Check(player.Game.Paused && Capture(player.Render(80, 24), 80, 24).Contains("Paused"), "P pauses the game");
        player.HandleKey(Key(' ', ConsoleKey.Spacebar), 80, 24);
        Check(!player.Game.Paused, "Space resumes the game");
        while (!player.Game.Over) player.Step();
        Check(Capture(player.Render(80, 24), 80, 24).Contains("Enter plays again"), "a finished game offers another round");
        player.HandleKey(Key('\r', ConsoleKey.Enter), 80, 24);
        Check(!player.Game.Over && player.Game.Score == 0, "Enter plays again");

        var scorer = new WarmupScreen(FixtureSite, null, best, _ => 0);
        foreach (var character in "snake") scorer.HandleKey(Letter(character), 80, 24);
        var board = scorer.Game!;
        while (board.Score == 0 && !board.Over)
        {
            var (x, y) = board.Body[0];
            scorer.HandleKey(Key('\0', board.Seed.Y < y ? ConsoleKey.UpArrow : board.Seed.X < x ? ConsoleKey.LeftArrow : ConsoleKey.DownArrow), 80, 24);
            scorer.Step();
        }
        while (!board.Over) scorer.Step();
        Check(board.Score == 1 && scorer.Best == 1 && SnakeGame.LoadBest(best) == 1 && new WarmupScreen(FixtureSite, null, best).Best == 1,
            "the best score is kept between games");

        var status = new WarmupScreen(FixtureSite, null, best) { Elapsed = TimeSpan.FromSeconds(42) };
        Check(status.GameStatus() == "warming up, 42 s", "the game's status line counts the warm-up's seconds");
        status.Result = new(FixtureSite.ToString(), WarmupOutcome.Ready, true, 200, 48, 2, "ready");
        Check(status.GameStatus() == "site is ready (48 s), Esc returns", "the game's status line says when the site is ready");

        double clock = 0;
        TimeSpan Clock() => TimeSpan.FromMilliseconds(clock);
        void Wait(TimeSpan _) => clock += 20;
        using (var output = new StringWriter())
        using (var cancel = new CancellationTokenSource())
        {
            var screen = new WarmupScreen(FixtureSite, null, best);
            var probe = Task.Run(async () =>
            {
                try { await Task.Delay(Timeout.Infinite, cancel.Token); }
                catch (OperationCanceledException) { }
                return new WarmupResult(FixtureSite.ToString(), WarmupOutcome.Skipped, false, null, 1, 1, "Stopped waiting.");
            });
            var polls = 0;
            var result = screen.Show(LiveConsole(output), () => ++polls switch
            {
                1 => null,
                2 => escape,
                _ => throw new InvalidOperationException("TEST FAILED: the warm-up screen kept waiting after Esc")
            }, probe, cancel, Clock, Wait);
            Check(cancel.IsCancellationRequested && result.Outcome == WarmupOutcome.Skipped && output.ToString().Contains("DEV SITE WARM-UP"),
                "Esc stops waiting and returns the skipped result");
        }

        using (var output = new StringWriter())
        using (var cancel = new CancellationTokenSource())
        {
            var screen = new WarmupScreen(FixtureSite, null, best, _ => 0);
            var finished = new TaskCompletionSource<WarmupResult>();
            var ready = new WarmupResult(FixtureSite.ToString(), WarmupOutcome.Ready, true, 200, 48, 2, "The site is ready.");
            var script = new Queue<object?>();
            foreach (var character in "snake") script.Enqueue(Letter(character));
            script.Enqueue(null);
            for (var index = 0; index < 10; index++) script.Enqueue(null);
            script.Enqueue(new Action(() => Check(screen.Playing && output.ToString().Contains("GARDEN SNAKE") &&
                output.ToString().Contains("warming up,"), "typing snake on the progress screen switches to the game")));
            script.Enqueue(new Action(() => finished.SetResult(ready)));
            for (var index = 0; index < 20; index++) script.Enqueue(null);
            script.Enqueue(new Action(() => Check(screen.Playing && screen.Result == ready && output.ToString().Contains("site is ready (48 s), Esc returns"),
                "the game keeps going after the site is ready and says so")));
            script.Enqueue(escape);
            ConsoleKeyInfo? Poll()
            {
                while (script.Count != 0)
                {
                    var next = script.Dequeue();
                    if (next is Action action)
                    {
                        action();
                        continue;
                    }
                    return (ConsoleKeyInfo?)next;
                }
                throw new InvalidOperationException("TEST FAILED: the game did not return after Esc");
            }
            var result = screen.Show(LiveConsole(output), Poll, finished.Task, cancel, Clock, Wait);
            Check(result == ready && !cancel.IsCancellationRequested, "Esc in the game after the site finished returns its result");
        }
    }

    private static async Task CheckWarmupCommands(string home)
    {
        var settings = new Settings { SiteUrl = FixtureSite.ToString(), WarmUpAfterDeploy = true };
        DeploymentState Activated(params string[] paths) => new() { Updated = paths.Select(path => new UpdatedFile(path, UpdatedFile.Synced)).ToList() };
        Check(Warmup.ShouldWarmUp(settings, Activated(@"bin\MiscApps.dll"), out _) &&
            !Warmup.ShouldWarmUp(settings, Activated(), out _) &&
            !Warmup.ShouldWarmUp(new Settings { SiteUrl = FixtureSite.ToString(), WarmUpAfterDeploy = false }, Activated("a.ascx"), out _) &&
            !Warmup.ShouldWarmUp(new Settings { WarmUpAfterDeploy = true }, Activated("a.ascx"), out _),
            "a warm-up follows only enabled activations with a site address that changed webfarm files");
        Check(Warmup.ChangedBin(Activated(@"BIN\MiscApps.dll", "a.ascx")) && !Warmup.ChangedBin(Activated(@"DesktopModules\a.ascx")),
            "DLL deployments are told apart from frontend-only ones");

        var disabled = Activated("a.ascx");
        await Warmup.AfterActivation(new Settings { SiteUrl = FixtureSite.ToString(), WarmUpAfterDeploy = false }, disabled);
        Check(disabled.WarmUp is null, "a disabled warm-up does nothing after an activation");

        var history = Path.Combine(home, "warmup", "command-history.json");
        var created = 0;
        HttpMessageHandler Handlers(Uri _)
        {
            created++;
            return new ScriptedHandler((_, call, _) => Task.FromResult(call == 1 ? Page(500, TimedOutPage) : Page(200, HomePage)));
        }
        var now = TimeSpan.Zero;
        var options = new WarmupOptions(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(2), () => now,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            });
        var skipped = Activated(@"bin\MiscApps.dll");
        using (var progress = new StringWriter())
        {
            await Warmup.AfterActivationCommand(settings, skipped, ["activate", "--yes", "--no-warmup"], Handlers, options, progress, history);
            Check(skipped.WarmUp is null && created == 0 && progress.ToString().Length == 0, "--no-warmup skips the warm-up after activate");
        }
        var activated = Activated(@"bin\MiscApps.dll");
        using (var progress = new StringWriter())
        {
            await Warmup.AfterActivationCommand(settings, activated, ["activate", "--yes"], Handlers, options, progress, history);
            Check(activated.WarmUp is { Ready: true, Attempts: 2 } && progress.ToString().Contains("Warm-up: recompiling; the last request timed out") &&
                progress.ToString().Contains("Warm-up: The site is ready"), "activate warms up the site and writes its progress to stderr");
            Check(WarmupHistory.Load(history).Compiled.Count == 1 && WarmupHistory.Load(history).Frontend.Count == 0,
                "a successful warm-up after a DLL deployment is remembered for later estimates");
        }

        await Reject(() => Task.FromResult(Warmup.CommandTarget(new Settings(), ["warmup"])), "the warmup command needs an address");
        await Reject(() => Task.FromResult(Warmup.CommandTarget(settings, ["warmup", "--url", "ftp://site/"])), "the warmup command rejects a non-web address");
        await Reject(() => Task.FromResult(Warmup.CommandTarget(settings, ["warmup", "--timeout", "soon"])), "the warmup command rejects a bad timeout");
        var (url, limit) = Warmup.CommandTarget(settings, ["warmup", "--url", "https://other.example.test/", "--timeout", "45", "--json"]);
        Check(url == new Uri("https://other.example.test/") && limit == TimeSpan.FromSeconds(45), "--url and --timeout override the saved address and limit");

        using var output = new StringWriter();
        using var errors = new StringWriter();
        now = TimeSpan.Zero;
        var answer = await Warmup.CommandJson(FixtureSite, new ScriptedHandler((_, _, _) => Task.FromResult(Page(500, CompilationPage))),
            options, output, errors);
        var parsed = JsonSerializer.Deserialize<WarmupResult>(output.ToString(), Files.Json);
        Check(parsed is { Outcome: WarmupOutcome.Failed, Error.Line: 42 } && !answer.Ready && !output.ToString().Contains("Warm-up:") &&
            errors.ToString().Contains("Warm-up: request 1"), "warmup --json writes the result to stdout and its progress to stderr");
    }
}
