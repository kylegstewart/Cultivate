using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>What the dev site answered when it was requested after a deployment.</summary>
/// <param name="Outcome">One of the <see cref="WarmupOutcome"/> values.</param>
internal sealed record WarmupResult(string Url, string Outcome, bool Ready, int? Status, double Seconds, int Attempts, string Summary,
    string Detail = "", AspNetError? Error = null);

internal static class WarmupOutcome
{
    internal const string Ready = "Ready", Failed = "Failed", StillWarming = "StillWarming", Skipped = "Skipped";
}

/// <summary>The details an ASP.NET error page gives a developer. <paramref name="Hidden"/> means customErrors hid them from remote requests.</summary>
internal sealed record AspNetError(string Title, string Description, string Message, string File, int? Line, bool Hidden);

/// <summary>The request in flight and, in plain words, the last thing the site said.</summary>
internal sealed record WarmupProgress(int Attempt, string Said);

/// <summary>Timing for <see cref="Warmup.Run"/>; the clock and delay are injectable so tests never wait.</summary>
internal sealed record WarmupOptions(TimeSpan Limit, TimeSpan AttemptTimeout, TimeSpan Pause, Func<TimeSpan> Clock,
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    // Longer than ASP.NET's 110 second executionTimeout, so the "Request timed out" page arrives instead of a client timeout.
    internal static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(150);
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(300);

    internal static WarmupOptions Real(TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        return new(limit, DefaultAttemptTimeout, TimeSpan.FromSeconds(2), () => stopwatch.Elapsed, Task.Delay);
    }
}

/// <summary>Requests the dev site after a deployment, so Cultivate waits for the recompilation instead of the developer's first page load.</summary>
internal static class Warmup
{
    private enum Kind { Ready, Retry, Failed }
    private sealed record Answer(Kind Kind, int? Status, string Said, string Summary = "", string Detail = "", AspNetError? Error = null);

    internal static string HistoryPath => Path.Combine(Files.Home, "warmup-history.json");
    internal static string SnakePath => Path.Combine(Files.Home, "snake.json");

    /// <summary>
    /// The dev site's address from IIS's applicationHost.config on the webfarm server, or a guess from the share's server name.
    /// Bounded to a few seconds and never throws: it runs whenever the setting is empty.
    /// </summary>
    internal static string SuggestSiteUrl(string webfarmRoot)
    {
        if (!Uri.TryCreate(webfarmRoot, UriKind.Absolute, out var root) || !root.IsUnc) return "";
        var fallback = $"http://{root.Host}/";
        try
        {
            if (LocalPath(webfarmRoot) is not { } local) return fallback;
            var config = $@"\\{root.Host}\C$\Windows\System32\inetsrv\config\applicationHost.config";
            var read = Task.Run(() => File.ReadAllText(config));
            if (!read.Wait(TimeSpan.FromSeconds(3))) return fallback;
            return SiteUrlFromConfig(read.Result, local, root.Host) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>The server's own path for a UNC admin share, e.g. \\server\C$\inetpub\MyMeyer is C:\inetpub\MyMeyer.</summary>
    internal static string? LocalPath(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', 3);
        if (parts.Length < 2 || !Regex.IsMatch(parts[1], @"^[A-Za-z]\$$")) return null;
        return parts[1][0] + @":\" + (parts.Length == 3 ? parts[2] : "");
    }

    /// <summary>
    /// The address of the IIS site whose root folder is <paramref name="localPath"/>, from applicationHost.config text.
    /// Bindings are preferred when their host names the server, then https, then a host header over a blank one.
    /// </summary>
    internal static string? SiteUrlFromConfig(string configXml, string localPath, string server)
    {
        XDocument document;
        try { document = XDocument.Parse(configXml); }
        catch (XmlException) { return null; }
        var serverName = server.Split('.')[0];
        var site = document.Descendants("sites").Elements("site").FirstOrDefault(site =>
            site.Elements("application").FirstOrDefault(app => (string?)app.Attribute("path") == "/")?
                .Elements("virtualDirectory").FirstOrDefault(directory => (string?)directory.Attribute("path") == "/")?
                .Attribute("physicalPath")?.Value is { } physical && SamePath(physical, localPath));
        var best = site?.Element("bindings")?.Elements("binding")
            .Select(binding => Address((string?)binding.Attribute("protocol"), (string?)binding.Attribute("bindingInformation"), server))
            .OfType<(string Scheme, string Host, int Port, bool Named)>()
            .Select((address, index) => (address, index, score:
                (address.Named && address.Host.Contains(serverName, StringComparison.OrdinalIgnoreCase) ? 4 : 0) +
                (address.Scheme == "https" ? 2 : 0) + (address.Named ? 1 : 0)))
            .OrderByDescending(item => item.score).ThenBy(item => item.index).FirstOrDefault();
        if (best?.address is not { } chosen) return null;
        return new UriBuilder(chosen.Scheme, chosen.Host, chosen.Port, "/").Uri.ToString();
    }

    private static bool SamePath(string physical, string local)
    {
        static string Normal(string path) => Regex.Replace(path, "%SystemDrive%", "C:", RegexOptions.IgnoreCase).Replace('/', '\\').TrimEnd('\\');
        return Files.Comparer.Equals(Normal(physical), Normal(local));
    }

    // bindingInformation is "ip:port:host"; the ip can be an IPv6 address in brackets, so split from the right.
    private static (string Scheme, string Host, int Port, bool Named)? Address(string? protocol, string? information, string server)
    {
        if (protocol is not ("http" or "https") || information is null) return null;
        var hostAt = information.LastIndexOf(':');
        var portAt = hostAt > 0 ? information.LastIndexOf(':', hostAt - 1) : -1;
        if (portAt < 0 || !int.TryParse(information[(portAt + 1)..hostAt], out var port)) return null;
        var host = information[(hostAt + 1)..];
        var ip = information[..portAt];
        var named = host.Length != 0;
        var address = named ? host : ip is "" or "*" ? server : ip.Trim('[', ']');
        return (protocol, address, port, named);
    }

    /// <summary>Requests <paramref name="url"/> until the site gives a settled answer, the overall limit passes, or <paramref name="cancel"/> fires.</summary>
    internal static async Task<WarmupResult> Run(Uri url, HttpMessageHandler handler, WarmupOptions options, Action<WarmupProgress> report,
        CancellationToken cancel)
    {
        var start = options.Clock();
        TimeSpan Elapsed() => options.Clock() - start;
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var attempt = 0;
        var said = "requesting the site";
        int? status = null;
        WarmupResult Finish(string outcome, int? code, string summary, string detail = "", AspNetError? error = null) =>
            new(url.ToString(), outcome, outcome == WarmupOutcome.Ready, code, Math.Round(Elapsed().TotalSeconds, 1), attempt, summary, detail, error);
        WarmupResult Skipped() => Finish(WarmupOutcome.Skipped, status,
            $"Stopped waiting after {Elapsed().TotalSeconds:0} s; the site keeps compiling by itself.");
        while (true)
        {
            var remaining = options.Limit - Elapsed();
            if (cancel.IsCancellationRequested) return Skipped();
            if (remaining <= TimeSpan.Zero)
                return Finish(WarmupOutcome.StillWarming, status, $"Still warming up after {Elapsed().TotalSeconds:0} s.",
                    $"Last answer: {said}. The site keeps compiling by itself; refresh it in a minute.");
            attempt++;
            report(new(attempt, said));
            var timeout = remaining < options.AttemptTimeout ? remaining : options.AttemptTimeout;
            using var attemptCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            attemptCancel.CancelAfter(timeout);
            Answer answer;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd($"Cultivate/{Files.Version.Split('+')[0]}");
                request.Headers.Accept.ParseAdd("text/html");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCancel.Token);
                var body = await response.Content.ReadAsStringAsync(attemptCancel.Token);
                answer = Classify((int)response.StatusCode, response.ReasonPhrase, body, response.RequestMessage?.RequestUri ?? url);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return Skipped();
            }
            catch (OperationCanceledException)
            {
                answer = new(Kind.Retry, null, $"no answer within {timeout.TotalSeconds:0} s");
            }
            catch (HttpRequestException exception)
            {
                answer = Classify(exception, url);
            }
            catch (IOException)
            {
                answer = new(Kind.Retry, null, "the connection was reset");
            }
            status = answer.Status ?? status;
            if (answer.Kind == Kind.Ready)
                return Finish(WarmupOutcome.Ready, answer.Status, $"The site is ready: {answer.Summary} after {Elapsed().TotalSeconds:0} s " +
                    $"and {attempt} request{(attempt == 1 ? "" : "s")}.", answer.Detail);
            if (answer.Kind == Kind.Failed) return Finish(WarmupOutcome.Failed, answer.Status, answer.Summary, answer.Detail, answer.Error);
            said = answer.Said;
            report(new(attempt, said));
            var pause = options.Limit - Elapsed();
            if (pause > options.Pause) pause = options.Pause;
            if (pause <= TimeSpan.Zero) continue;
            try { await options.Delay(pause, cancel); }
            catch (OperationCanceledException) { return Skipped(); }
        }
    }

    private static Answer Classify(HttpRequestException exception, Uri url)
    {
        var reason = exception.InnerException?.Message ?? exception.Message;
        return exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => new(Kind.Failed, null, "", $"The address {url.Host} could not be found.",
                "Check the site address in `cultivate setup` and that you are on the network or VPN."),
            HttpRequestError.SecureConnectionError => new(Kind.Failed, null, "", "The secure connection to the site failed.",
                reason + "\nCultivate keeps certificate checks on. Use the address the site's certificate names."),
            HttpRequestError.ConnectionError => new(Kind.Retry, null, "not accepting connections yet"),
            _ => new(Kind.Retry, null, "the connection was reset")
        };
    }

    /// <summary>What one answer means: ready, still warming (retry), or a failure to report.</summary>
    private static Answer Classify(int status, string? reason, string body, Uri final)
    {
        var phrase = $"{status} {(string.IsNullOrWhiteSpace(reason) ? ((HttpStatusCode)status).ToString() : reason)}";
        if (ParseErrorPage(body) is { } error)
        {
            if (IsTimeout(error)) return new(Kind.Retry, status, "recompiling; the last request timed out");
            if (error.Hidden)
                return new(Kind.Failed, status, "", $"{error.Title}: the site hides the details from remote requests.",
                    "customErrors hides this error from requests that do not come from the server. Browse the site on the server itself, " +
                    @"or check the DNN log (Portals\_default\Logs) for the details.", error);
            var detail = new List<string>();
            if (error.File.Length != 0) detail.Add(error.Line is { } line ? $"{error.File}, line {line}" : error.File);
            if (error.Description.Length != 0) detail.Add(error.Description);
            return new(Kind.Failed, status, "", error.Message.Length == 0 ? error.Title : $"{error.Title}: {error.Message}",
                string.Join("\n", detail), error);
        }
        if (final.Query.Contains("aspxerrorpath=", StringComparison.OrdinalIgnoreCase))
            return new(Kind.Failed, status, "", "The site redirected to its error page.",
                "customErrors hides the error from remote requests. Browse the site on the server itself, " +
                @"or check the DNN log (Portals\_default\Logs) for the details.");
        if (status is >= 200 and < 300)
            return new(Kind.Ready, status, "", phrase,
                final.AbsolutePath != "/" && final.AbsolutePath.Length > 1 ? $"Answered from {final}" : "");
        if (status == 503) return new(Kind.Retry, status, "app pool restarting (503)");
        if (status is 502 or 504) return new(Kind.Retry, status, $"the server's gateway is not answering yet ({status})");
        var title = Clean(Regex.Match(body, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
        if (status is 401 or 403)
            return new(Kind.Failed, status, "", $"The site answered {phrase}, but Cultivate could not see a page.",
                "The site is running. Check that your Windows sign-in can open it in a browser.");
        return new(Kind.Failed, status, "", $"The site answered {phrase}.", title);
    }

    private static bool IsTimeout(AspNetError error) =>
        error.Title.StartsWith("Request timed out", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("Request timed out", StringComparison.OrdinalIgnoreCase);

    /// <summary>The details of an ASP.NET "Server Error in '/' Application" page, or null when <paramref name="html"/> is not one.</summary>
    internal static AspNetError? ParseErrorPage(string html)
    {
        if (!Regex.IsMatch(html, @"Server Error in '[^']*' Application", RegexOptions.IgnoreCase)) return null;
        string Find(string pattern) => Clean(Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
        var title = Find(@"<h2>\s*<i>(.*?)</i>\s*</h2>");
        if (title.Length == 0) title = Find(@"<title>(.*?)</title>");
        title = title.TrimEnd('.');
        var description = Find(@"<b>\s*Description:\s*</b>(.*?)<br");
        var message = Find(@"<b>\s*(?:Compiler Error Message|Parser Error Message|Exception Details):\s*</b>(.*?)<br");
        var file = Find(@"<b>\s*Source File:\s*</b>(.*?)<b>");
        int? line = int.TryParse(Find(@"<b>[\s\u00A0]*(?:&nbsp;)*[\s\u00A0]*Line:\s*</b>\s*(\d+)"), out var number) ? number : null;
        var hidden = title.Equals("Runtime Error", StringComparison.OrdinalIgnoreCase) &&
            description.Contains("custom error settings", StringComparison.OrdinalIgnoreCase);
        return new(title.Length == 0 ? "Server Error" : title, description, message, file, line, hidden);
    }

    private static string Clean(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    /// <summary>Whether an activation is followed by a warm-up: enabled, an address is set, and the webfarm actually changed.</summary>
    internal static bool ShouldWarmUp(Settings settings, DeploymentState activated, out Uri url)
    {
        url = null!;
        return settings.WarmUpAfterDeploy && activated.Updated.Count != 0 &&
            Uri.TryCreate(settings.SiteUrl, UriKind.Absolute, out url!) && url.Scheme is "http" or "https";
    }

    /// <summary>Whether the activation changed DLLs, which recycles the app domain and recompiles every control.</summary>
    internal static bool ChangedBin(DeploymentState activated) => activated.Updated.Any(file =>
        file.Change != UpdatedFile.Kept && file.RelativePath.StartsWith(@"bin\", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Signs in as the current Windows user, also after the redirect to DNN's WindowsSignin.aspx: .NET only reuses credentials
    /// across a redirect when they come from a <see cref="CredentialCache"/>, which also keeps them to this one site.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(Uri url)
    {
        var site = new Uri(url.GetLeftPart(UriPartial.Authority) + "/");
        var credentials = new CredentialCache();
        foreach (var scheme in new[] { "Negotiate", "NTLM" }) credentials.Add(site, scheme, CredentialCache.DefaultNetworkCredentials);
        return new()
        {
            Credentials = credentials, AllowAutoRedirect = true, MaxAutomaticRedirections = 10,
            UseCookies = true, CookieContainer = new CookieContainer(), ConnectTimeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>Shown in the terminal UI after an activation's summary.</summary>
    internal static Task AfterActivation(Settings settings, DeploymentState activated)
    {
        try
        {
            if (!ShouldWarmUp(settings, activated, out var url)) return Task.CompletedTask;
            var compiled = ChangedBin(activated);
            var history = WarmupHistory.Load(HistoryPath);
            var result = Interactive(url, WarmupOptions.DefaultLimit, history.Estimate(compiled));
            activated.WarmUp = result;
            if (result.Ready) history.Record(compiled, result.Seconds, HistoryPath);
            AnsiConsole.Write(ResultCard(result));
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[{TerminalStyle.Gold}]The site warm-up stopped:[/] [{TerminalStyle.Muted}]{Markup.Escape(exception.Message)}[/]");
        }
        return Task.CompletedTask;
    }

    /// <summary>Runs after <c>cultivate activate</c>, before its JSON result is printed.</summary>
    internal static async Task AfterActivationCommand(Settings settings, DeploymentState activated, string[] args)
    {
        try
        {
            await AfterActivationCommand(settings, activated, args, CreateHandler, WarmupOptions.Real(WarmupOptions.DefaultLimit), Console.Error, HistoryPath);
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine("The site warm-up stopped: " + exception.Message); }
            catch (IOException) { }
        }
    }

    internal static async Task AfterActivationCommand(Settings settings, DeploymentState activated, string[] args,
        Func<Uri, HttpMessageHandler> handlers, WarmupOptions options, TextWriter progress, string historyPath)
    {
        if (args.Contains("--no-warmup") || !ShouldWarmUp(settings, activated, out var url)) return;
        using var handler = handlers(url);
        var compiled = ChangedBin(activated);
        var result = await Run(url, handler, options, Writer(progress, url), CancellationToken.None);
        activated.WarmUp = result;
        progress.WriteLine(Describe(result));
        if (result.Ready) WarmupHistory.Load(historyPath).Record(compiled, result.Seconds, historyPath);
    }

    /// <summary><c>cultivate warmup [--url URL] [--timeout SECONDS] [--json]</c></summary>
    internal static async Task<int> Command(Settings settings, string[] args)
    {
        var (url, limit) = CommandTarget(settings, args);
        WarmupResult result;
        if (args.Contains("--json") || Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            using var handler = CreateHandler(url);
            result = await CommandJson(url, handler, WarmupOptions.Real(limit), Console.Out, Console.Error);
        }
        else
        {
            result = Interactive(url, limit, WarmupHistory.Load(HistoryPath).Estimate(compiled: true));
            AnsiConsole.Write(ResultCard(result));
        }
        return result.Ready ? 0 : 2;
    }

    /// <summary>The address and overall limit the warmup command uses: <c>--url</c> or the saved address, and <c>--timeout</c> seconds.</summary>
    internal static (Uri Url, TimeSpan Limit) CommandTarget(Settings settings, string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1] : throw new InvalidOperationException($"{name} needs a value.");
        }
        var address = Option("--url") ?? settings.SiteUrl;
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("No dev site address is configured. Pass --url https://your-dev-site/ or save one with `cultivate setup --site-url URL`.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"'{address}' is not an http or https address.");
        var limit = WarmupOptions.DefaultLimit;
        if (Option("--timeout") is { } seconds)
            limit = int.TryParse(seconds, out var value) && value > 0 ? TimeSpan.FromSeconds(value)
                : throw new InvalidOperationException("--timeout expects a number of seconds greater than zero.");
        return (url, limit);
    }

    /// <summary>The warmup command's redirected form: progress lines to <paramref name="progress"/>, the result as JSON to <paramref name="output"/>.</summary>
    internal static async Task<WarmupResult> CommandJson(Uri url, HttpMessageHandler handler, WarmupOptions options, TextWriter output, TextWriter progress)
    {
        var result = await Run(url, handler, options, Writer(progress, url), CancellationToken.None);
        progress.WriteLine(Describe(result));
        output.WriteLine(JsonSerializer.Serialize(result, Files.Json));
        return result;
    }

    /// <summary>Plain progress lines for redirected output: one when each request starts and one for each answer that means waiting longer.</summary>
    private static Action<WarmupProgress> Writer(TextWriter progress, Uri url)
    {
        var started = 0;
        return update =>
        {
            if (update.Attempt == started)
            {
                progress.WriteLine($"Warm-up: {update.Said}");
                return;
            }
            started = update.Attempt;
            progress.WriteLine($"Warm-up: request {update.Attempt} to {url}");
        };
    }

    /// <summary>The progress screen, with the probe running on a background task until the site answers or Esc stops it.</summary>
    private static WarmupResult Interactive(Uri url, TimeSpan limit, TimeSpan? estimate)
    {
        using var handler = CreateHandler(url);
        using var cancel = new CancellationTokenSource();
        var screen = new WarmupScreen(url, estimate, SnakePath);
        var probe = Task.Run(() => Run(url, handler, WarmupOptions.Real(limit), screen.Report, cancel.Token));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return screen.Show(AnsiConsole.Console, () => Console.KeyAvailable ? Console.ReadKey(true) : null, probe, cancel,
                () => stopwatch.Elapsed, Thread.Sleep);
        }
        finally
        {
            // Normally the probe has already finished; if the screen failed, stop it before its handler is disposed.
            cancel.Cancel();
            try { probe.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
    }

    /// <summary>One plain line, for stderr.</summary>
    internal static string Describe(WarmupResult result) =>
        "Warm-up: " + result.Summary + (result.Detail.Length == 0 ? "" : "\n  " + result.Detail.Replace("\n", "\n  "));

    /// <summary>The short result printed after the progress screen closes.</summary>
    internal static IRenderable ResultCard(WarmupResult result)
    {
        var (title, color) = result.Outcome switch
        {
            WarmupOutcome.Ready => ("SITE READY", TerminalStyle.Accent),
            WarmupOutcome.Failed => ("SITE ERROR", "#F0A3A3"),
            WarmupOutcome.StillWarming => ("STILL WARMING UP", TerminalStyle.Gold),
            _ => ("WARM-UP SKIPPED", TerminalStyle.Muted)
        };
        var lines = new List<string> { $"[bold {color}]{Markup.Escape(result.Summary)}[/]" };
        if (result.Detail.Length != 0) lines.Add(Markup.Escape(result.Detail));
        lines.Add($"[{TerminalStyle.Muted}]{Markup.Escape(result.Url)}  ·  {result.Attempts} request{(result.Attempts == 1 ? "" : "s")}  ·  {result.Seconds:0} s[/]");
        return TerminalStyle.Card(new Markup(string.Join("\n", lines)), title, color);
    }
}
