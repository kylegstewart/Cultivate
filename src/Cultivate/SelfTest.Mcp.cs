using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cultivate;

internal static partial class SelfTest
{
    /// <summary>A client for a real <c>cultivate mcp</c> process; it records every stdout line that is not a JSON-RPC message.</summary>
    private sealed class McpClient : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Dictionary<string, TaskCompletionSource<JsonObject>> waiting = [];
        private readonly Task reading;
        private readonly StringBuilder errors = new();
        private int next;

        public List<JsonObject> Notifications { get; } = [];
        public List<string> Invalid { get; } = [];
        public string Errors { get { lock (errors) return errors.ToString(); } }

        public McpClient(string home, params string[] args)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Cultivate.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Cultivate.dll"));
            start.ArgumentList.Add("mcp");
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment["CULTIVATE_HOME"] = home;
            process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start cultivate mcp.");
            process.ErrorDataReceived += (_, line) => { lock (errors) errors.AppendLine(line.Data); };
            process.BeginErrorReadLine();
            reading = Task.Run(Read);
        }

        private async Task Read()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonObject? message = null;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { }
                if (message is null || message["jsonrpc"]?.ToString() != "2.0" || message["method"] is null && !message.ContainsKey("id"))
                {
                    lock (Invalid) Invalid.Add(line);
                    continue;
                }
                if (message["method"] is not null)
                {
                    lock (Notifications) Notifications.Add(message);
                    continue;
                }
                TaskCompletionSource<JsonObject>? waiter;
                lock (waiting) waiting.Remove(message["id"]?.ToJsonString() ?? "null", out waiter);
                if (waiter is null) lock (Invalid) Invalid.Add("Unexpected response: " + line);
                waiter?.TrySetResult(message);
            }
        }

        public void Write(string line)
        {
            process.StandardInput.Write(line + "\n");
            process.StandardInput.Flush();
        }

        public Task<JsonObject> Send(string line, string key)
        {
            var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waiting) waiting[key] = waiter;
            Write(line);
            return waiter.Task;
        }

        public Task<JsonObject> Request(string method, JsonObject? parameters = null, int? id = null)
        {
            var key = id ?? Interlocked.Increment(ref next);
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = key, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;
            return Send(message.ToJsonString(), key.ToString()).WaitAsync(TimeSpan.FromMinutes(5));
        }

        public async Task<JsonObject> Call(string tool, JsonObject? arguments = null, string? progress = null)
        {
            var parameters = new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] };
            if (progress is not null) parameters["_meta"] = new JsonObject { ["progressToken"] = progress };
            var response = await Request("tools/call", parameters);
            return response["result"] as JsonObject ?? throw new InvalidOperationException($"TEST FAILED: {tool} returned no result: {response.ToJsonString()}\n{Errors}");
        }

        public List<string> Progress(string token)
        {
            lock (Notifications)
                return Notifications.Where(message => message["method"]?.ToString() == "notifications/progress" &&
                    message["params"]?["progressToken"]?.ToString() == token).Select(message => message["params"]!["message"]!.ToString()).ToList();
        }

        public async Task<int> Close()
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await reading;
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    private static bool IsError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;
    private static string TextOf(JsonObject result) => result["content"]?[0]?["text"]?.ToString() ?? "";
    private static JsonNode Structured(JsonObject result) => result["structuredContent"] ?? throw new InvalidOperationException("TEST FAILED: no structuredContent: " + result.ToJsonString());

    // Spawns the built executable with `mcp` against fixture settings, as a coding agent would, and never against the user's settings.
    private static async Task CheckMcp(string home, string msbuild)
    {
        var root = Path.Combine(home, "mcp");
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var site = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        async Task Answer(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var path = (await reader.ReadLineAsync() ?? "").Split(' ').ElementAtOrDefault(1) ?? "/";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                if (path.StartsWith("/slow", StringComparison.Ordinal))
                {
                    try { await Task.Delay(Timeout.Infinite, stop.Token); }
                    catch (OperationCanceledException) { }
                    return;
                }
                var (status, body) = path.StartsWith("/broken", StringComparison.Ordinal) ? (500, CompilationPage) : (200, "<html><title>Home</title>ready</html>");
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Internal Server Error")}\r\n" +
                    $"Content-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(bytes);
            }
        }
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { _ = Answer(await listener.AcceptTcpClientAsync(stop.Token)); }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            }
        });
        var settings = new Settings
        {
            RepositoryRoot = Path.Combine(root, "repository"), WebfarmRoot = Path.Combine(root, "farm"), StateRoot = Path.Combine(root, "state"),
            MSBuild = msbuild, MappingAssembly = Path.Combine(root, "no-mapping-assembly.dll"), WorktreesRoot = Path.Combine(root, "worktrees"),
            SqlMirrorRoot = Path.Combine(root, "sql"), SiteUrl = site.ToString(), LibraryRepositories = []
        };
        var serverHome = Path.Combine(root, "home");
        Directory.CreateDirectory(settings.RepositoryRoot);
        Directory.CreateDirectory(Path.Combine(settings.WebfarmRoot, "bin"));
        Directory.CreateDirectory(settings.WorktreesRoot);
        Files.WriteJson(Path.Combine(serverHome, "settings.json"), settings);
        var git = new GitRepository(settings);
        async Task G(params string[] args) => _ = await git.Git(settings.RepositoryRoot, args);
        void Save(string checkout, string path, string content)
        {
            var full = Files.Child(checkout, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        await G("init", "-b", "master");
        await G("config", "user.email", "cultivate-test@localhost");
        await G("config", "user.name", "Cultivate isolated tests");
        Save(settings.RepositoryRoot, ".gitignore", "bin/\nobj/\n");
        Save(settings.RepositoryRoot, "MyMeyer.sln", "");
        Save(settings.RepositoryRoot, @"Lib\McpLib.csproj", Project("McpLib"));
        Save(settings.RepositoryRoot, @"Lib\App.config", "<configuration />");
        Save(settings.RepositoryRoot, @"Lib\Value.cs", "public class McpValue { public static int Value => 1; }");
        await G("add", ".");
        await G("commit", "-qm", "baseline");
        var good = Path.Combine(settings.WorktreesRoot, "4242-worktree");
        var broken = Path.Combine(settings.WorktreesRoot, "4243-worktree");
        await G("worktree", "add", "-q", "-b", "4242", good, "master");
        await G("worktree", "add", "-q", "-b", "4243", broken, "master");
        Save(good, @"Lib\Value.cs", "public class McpValue { public static int Value => 2; }");
        Save(broken, @"Lib\Value.cs", "public class McpValue { public static int Value => missingValue; }");
        Save(broken, @"Lib\View.ascx", "unmapped markup");
        var deployed = Path.Combine(settings.WebfarmRoot, @"bin\McpLib.dll");
        File.WriteAllText(deployed, "original fixture dll");
        string[] all = ["list_targets", "deployment_status", "ticket_workspaces", "worktrees", "doctor", "last_failure", "plan", "build", "activate", "restore", "warmup"];
        JsonObject Arguments(params (string Name, JsonNode? Value)[] values) => new(values.Select(value => KeyValuePair.Create(value.Name, value.Value)));

        try
        {
            await using (var client = new McpClient(serverHome))
            {
                var initialized = (await client.Request("initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "self-test", ["version"] = "1" }
                }))["result"]!;
                Check(initialized["protocolVersion"]?.ToString() == "2025-06-18" && initialized["serverInfo"]?["name"]?.ToString() == "cultivate" &&
                    initialized["capabilities"]?["tools"] is not null && initialized["instructions"]?.ToString().Contains("restore") == true,
                    "mcp initialize negotiates the requested protocol version and gives the server's instructions");
                client.Write("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
                var newer = (await client.Request("initialize", new JsonObject { ["protocolVersion"] = "2099-01-01", ["capabilities"] = new JsonObject() }))["result"]!;
                Check(newer["protocolVersion"]?.ToString() == McpServer.Legacy[0], "mcp initialize offers its latest version for an unknown one");
                Check((await client.Request("ping"))["result"] is JsonObject { Count: 0 }, "mcp answers ping");
                var tools = (await client.Request("tools/list"))["result"]!["tools"]!.AsArray().Select(tool => tool!.AsObject()).ToList();
                JsonObject Tool(string name) => tools.Single(tool => tool["name"]?.ToString() == name);
                Check(tools.Select(tool => tool["name"]!.ToString()).Order().SequenceEqual(all.Order()) &&
                    tools.All(tool => tool["inputSchema"]?["type"]?.ToString() == "object" && tool["outputSchema"]?["type"]?.ToString() == "object" &&
                        tool["description"]?.ToString().Length > 40),
                    "mcp lists every tool with a description and input and output schemas");
                bool Hint(string name, string hint) => Tool(name)["annotations"]![hint]!.GetValue<bool>();
                Check(Hint("activate", "destructiveHint") && !Hint("activate", "readOnlyHint") && !Hint("activate", "idempotentHint") &&
                    Hint("restore", "destructiveHint") && Hint("restore", "idempotentHint") && Hint("plan", "readOnlyHint") && Hint("build", "readOnlyHint") &&
                    !Hint("build", "destructiveHint") && Tool("activate")["inputSchema"]!["required"]!.AsArray().Any(item => item!.ToString() == "confirm"),
                    "mcp annotates the farm-changing tools as destructive and requires confirm");
                Check(Tool("plan")["description"]!.ToString().Contains("Code Release Manager database") &&
                    Tool("activate")["description"]!.ToString().Contains("Code Release Manager database") &&
                    !Tool("build")["description"]!.ToString().Contains("Code Release Manager database"),
                    "tools that can look up frontend mappings say they read the Code Release Manager database");
                JsonObject Modern(string? version = McpServer.Modern) =>
                    new() { ["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = version, ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject() } };
                var discovered = (await client.Request("server/discover", Modern()))["result"]!;
                var modernList = (await client.Request("tools/list", Modern()))["result"]!;
                var unsupported = (await client.Request("tools/list", Modern("1999-01-01")))["error"];
                Check(discovered["supportedVersions"]!.AsArray().Any(item => item!.ToString() == McpServer.Modern) &&
                    discovered["_meta"]?["io.modelcontextprotocol/serverInfo"]?["name"]?.ToString() == "cultivate" &&
                    modernList["resultType"]?.ToString() == "complete" && modernList["tools"]!.AsArray().Count == all.Length &&
                    unsupported?["code"]?.GetValue<int>() == -32022 && unsupported["data"]?["requested"]?.ToString() == "1999-01-01",
                    "mcp serves the stateless revision: discovery, per-request versions, and unsupported versions");

                Check(Structured(await client.Call("deployment_status"))["active"]?.GetValue<bool>() == false, "mcp deployment_status reports no active deployment");
                var targets = Structured(await client.Call("list_targets"))["worktrees"]!.AsArray();
                Check(targets.Any(item => Files.Comparer.Equals(item!["worktree"]?.ToString(), good) && item["ticket"]?.ToString() == "4242"),
                    "mcp list_targets lists MyMeyer worktrees with their ticket");
                var workspace = Structured(await client.Call("ticket_workspaces", Arguments(("ticket", "4242"))))["workspaces"]!.AsArray().Single()!;
                Check(workspace["checkouts"]!.AsArray().Any(checkout => Files.Comparer.Equals(Files.Full(checkout!["path"]!.ToString()), good)),
                    "mcp ticket_workspaces finds a ticket's worktree");
                var listed = Structured(await client.Call("worktrees", Arguments(("ticket", "4243"))))["worktrees"]!.AsArray();
                Check(listed.Count == 1 && Files.Comparer.Equals(Files.Full(listed[0]!["path"]!.ToString()), broken), "mcp worktrees filters by ticket");

                var plan = await client.Call("plan", Arguments(("ticket", "4242")));
                Check(!IsError(plan) && Structured(plan)["canDeploy"]!.GetValue<bool>() &&
                    Structured(plan)["projects"]!.AsArray().Single()!["assembly"]?.ToString() == "McpLib",
                    "mcp plan finds a ticket's worktree and plans only its changed project");
                var unmapped = Structured(await client.Call("plan", Arguments(("worktree", broken), ("frontendMappings", false))));
                Check(unmapped["frontendMappingsChecked"]?.GetValue<bool>() == false &&
                    unmapped["frontend"]!.AsArray().Single()!["source"]?.ToString() == "Lib/View.ascx" && unmapped["frontend"]![0]!["destination"] is null &&
                    !unmapped["errors"]!.AsArray().Any(), "mcp plan can skip the frontend mapping lookup and still lists frontend changes");

                var built = await client.Call("build", Arguments(("ticket", "4242")), progress: "build-good");
                Check(!IsError(built) && Structured(built)["succeeded"]!.GetValue<bool>() &&
                    Structured(built)["outputs"]!.AsArray().Select(item => item!.ToString()).SequenceEqual([@"bin\McpLib.dll"]),
                    "mcp build compiles the changed project without deploying");
                Check(client.Progress("build-good").Contains("Building McpLib (1/1)"), "mcp build sends the pipeline's progress messages");
                Check(File.ReadAllText(deployed) == "original fixture dll" && Structured(await client.Call("deployment_status"))["active"]?.GetValue<bool>() == false,
                    "mcp build leaves the webfarm and the journal untouched");
                var failed = await client.Call("build", Arguments(("worktree", broken)));
                var error = Structured(failed)["errors"]!.AsArray().Select(item => item!).FirstOrDefault(item => item["code"]?.ToString() == "CS0103");
                Check(IsError(failed) && Structured(failed)["succeeded"]?.GetValue<bool>() == false && error?["file"]?.ToString() == @"Lib\Value.cs" &&
                    error["line"]?.GetValue<int>() == 1 && error["column"] is not null && error["project"]?.ToString() == @"Lib\McpLib.csproj" &&
                    error["message"]?.ToString().Contains("missingValue") == true && TextOf(failed).Contains("BUILD FAILED"),
                    "mcp build returns a compile error's file, line, code, message and project, without looking up frontend mappings");
                Check(Structured(await client.Call("last_failure"))["report"]?.ToString().Contains("BUILD FAILED") == true, "mcp last_failure returns the recorded diagnosis");

                var unconfirmed = await client.Call("activate", Arguments(("ticket", "4242")));
                Check(IsError(unconfirmed) && TextOf(unconfirmed).Contains("confirm: true") && File.ReadAllText(deployed) == "original fixture dll",
                    "mcp activate without confirm is refused and changes nothing");
                var activating = client.Call("activate", Arguments(("ticket", "4242"), ("confirm", true)), progress: "activate");
                var overlapping = await client.Call("restore", Arguments(("confirm", true)));
                var activated = await activating;
                Check(IsError(overlapping) && TextOf(overlapping).Contains("still running"), "mcp runs one farm-changing call at a time");
                var state = Structured(activated);
                Check(!IsError(activated) && state["deployed"]!.GetValue<bool>() && state["automaticUpdates"]?.GetValue<bool>() == false &&
                    state["updated"]!.AsArray().Any(file => file!["path"]?.ToString() == @"bin\McpLib.dll" && file["change"]?.ToString() == UpdatedFile.Built) &&
                    File.ReadAllText(deployed) != "original fixture dll", "mcp activate deploys once, without automatic updates by default");
                Check(state["warmUp"]?["outcome"]?.ToString() == WarmupOutcome.Ready && state["siteReady"]?.GetValue<bool>() == true &&
                    client.Progress("activate").Any(message => message.StartsWith("Deploying")) && client.Progress("activate").Any(message => message.StartsWith("Warm-up")),
                    "mcp activate warms up the site and reports progress");
                var status = Structured(await client.Call("deployment_status"));
                Check(status["active"]!.GetValue<bool>() && status["branch"]?.ToString() == "4242" &&
                    status["files"]!.AsArray().Any(file => file!.ToString() == @"bin\McpLib.dll"), "mcp deployment_status shows the activated version");
                var siteError = Structured(await client.Call("warmup", Arguments(("url", site + "broken"), ("timeoutSeconds", 30))));
                Check(siteError["outcome"]?.ToString() == WarmupOutcome.Failed && siteError["error"]?["message"]?.ToString().Contains("CS0103") == true,
                    "mcp warmup reports the site's compile error");

                var slow = client.Request("tools/call", new JsonObject
                {
                    ["name"] = "warmup", ["arguments"] = Arguments(("url", site + "slow"), ("timeoutSeconds", 120))
                }, id: 9001);
                await Task.Delay(1500);
                client.Write("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9001,"reason":"self-test"}}""");
                await client.Request("ping");
                await Task.Delay(2000);
                Check(!slow.IsCompleted && client.Errors.Contains("Cancellation requested for request 9001"),
                    "mcp stops a cancelled call and sends no result for it");

                var unrestored = await client.Call("restore");
                Check(IsError(unrestored) && File.ReadAllText(deployed) != "original fixture dll", "mcp restore without confirm is refused");
                var restored = Structured(await client.Call("restore", Arguments(("confirm", true))));
                Check(restored["restored"]!.GetValue<bool>() && restored["previous"]?["branch"]?.ToString() == "4242" &&
                    File.ReadAllText(deployed) == "original fixture dll" && Structured(await client.Call("deployment_status"))["active"]?.GetValue<bool>() == false,
                    "mcp restore returns the original webfarm files");

                var unknown = (await client.Request("tools/call", new JsonObject { ["name"] = "no_such_tool", ["arguments"] = new JsonObject() }))["error"];
                Check(unknown?["code"]?.GetValue<int>() == -32602, "mcp rejects an unknown tool with a protocol error");
                var parse = (await client.Send("this is not JSON", "null"))["error"];
                var method = (await client.Request("no/such/method"))["error"];
                var shape = (await client.Request("tools/call", new JsonObject { ["name"] = "deployment_status", ["arguments"] = "text" }))["result"]!.AsObject();
                var type = await client.Call("plan", Arguments(("worktree", 5)));
                Check(parse?["code"]?.GetValue<int>() == -32700 && method?["code"]?.GetValue<int>() == -32601 &&
                    IsError(shape) && TextOf(shape).Contains("arguments must be an object") && IsError(type) && TextOf(type).Contains("worktree must be a string"),
                    "mcp answers malformed input with errors and keeps serving");
                var doctor = Structured(await client.Call("doctor"));
                Check(doctor["checks"]!.AsArray().Count > 3 && Files.Comparer.Equals(doctor["settings"]?["repositoryRoot"]?.ToString(), settings.RepositoryRoot),
                    "mcp doctor returns its checks and the settings in use");
                Check(await client.Close() == 0, "mcp exits when its input closes");
                Check(client.Invalid.Count == 0, "every line mcp writes to stdout is a JSON-RPC message" +
                    (client.Invalid.Count == 0 ? "" : ": " + string.Join("\n", client.Invalid)));
            }

            await using (var client = new McpClient(serverHome, "--read-only"))
            {
                await client.Request("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject() });
                var names = (await client.Request("tools/list"))["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.ToString()).ToList();
                var refused = (await client.Request("tools/call", new JsonObject { ["name"] = "activate", ["arguments"] = Arguments(("ticket", "4242"), ("confirm", true)) }))["error"];
                Check(!names.Contains("activate") && !names.Contains("restore") && names.Contains("build") && names.Contains("warmup") &&
                    refused?["code"]?.GetValue<int>() == -32602 && refused["message"]?.ToString().Contains("read-only") == true &&
                    File.ReadAllText(deployed) == "original fixture dll", "mcp --read-only leaves out the farm-changing tools");
                Check(await client.Close() == 0 && client.Invalid.Count == 0, "mcp --read-only writes only JSON-RPC to stdout");
            }

            var unconfiguredHome = Path.Combine(root, "unconfigured");
            Files.WriteJson(Path.Combine(unconfiguredHome, "settings.json"), new Settings
            {
                RepositoryRoot = Path.Combine(root, "missing repository"), WebfarmRoot = "not-a-webfarm", MSBuild = Path.Combine(root, "missing", "MSBuild.exe"),
                StateRoot = Path.Combine(root, "missing state"), MappingAssembly = Path.Combine(root, "missing.dll"), WorktreesRoot = Path.Combine(root, "missing worktrees"),
                LibraryRepositories = []
            });
            await using (var client = new McpClient(unconfiguredHome))
            {
                await client.Request("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
                var count = (await client.Request("tools/list"))["result"]!["tools"]!.AsArray().Count;
                var status = await client.Call("deployment_status");
                var plan = await client.Call("plan", Arguments(("ticket", "4242")));
                Check(count == all.Length && IsError(status) && TextOf(status).Contains("cultivate setup") && IsError(plan) && TextOf(plan).Contains("cultivate setup"),
                    "mcp starts and lists its tools before Cultivate is set up, and each tool says to run cultivate setup");
                Check(await client.Close() == 0 && client.Invalid.Count == 0, "an unconfigured mcp writes only JSON-RPC to stdout");
            }
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await serving;
            if (await new Bridge(settings).Status() is { Active: true }) await new Bridge(settings).Call("Restore");
        }
    }
}
