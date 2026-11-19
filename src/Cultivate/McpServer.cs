using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Spectre.Console;

namespace Cultivate;

/// <summary>A compiler or MSBuild error, with paths relative to the selected checkout where possible.</summary>
internal sealed record CompileError(string? File, int? Line, int? Column, string Code, string Message, string? Project);

/// <summary>
/// <c>cultivate mcp [--read-only]</c>: a Model Context Protocol server on stdin/stdout, so coding agents can drive Cultivate
/// through typed tools. It speaks the stateless 2026-07-28 revision and the earlier initialize handshake.
/// </summary>
internal sealed partial class McpServer
{
    internal const string Modern = "2026-07-28";
    internal static readonly string[] Legacy = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private const string VersionKey = "io.modelcontextprotocol/protocolVersion";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static SafeFileHandle? nul;

    private const string Instructions = """
        Cultivate builds and deploys a MyMeyer worktree or branch to the user's dev IIS webfarm, then requests the dev site until it answers.
        Usual order: list_targets or ticket_workspaces to find the worktree (or pass ticket), plan to see what would deploy, build to check that only the changed projects compile (deploys nothing), activate with confirm: true to deploy, warmup if the activation's warm-up was skipped, deployment_status to see what is live, and restore with confirm: true when done.
        There is one dev webfarm with one active deployment. Activating replaces whatever is active, including another agent's or the user's own; check deployment_status first and say what you replaced. Restore puts back the original webfarm files Cultivate journaled and ends the deployment.
        Only projects owning changed code are built; unchanged dependencies are never rebuilt. Activate and restore need the user's agreement: pass confirm: true only when the user asked for the deployment or restore.
        Report only what the results confirm: a deployment is verified when activate returns its updated files, and the site is verified when warmUp.ready is true.
        """;

    private const string LookupNote = " If the target has changed frontend files (.ascx, .aspx, .js, .css, images), Cultivate looks up their webfarm " +
        "destinations in the Code Release Manager database: a read-only query Cultivate itself makes under the user's Windows identity.";

    private const string TargetProperties = """
        "worktree": { "type": "string", "description": "Absolute path of a MyMeyer worktree; its saved files are used, committed or not." },
        "branch": { "type": "string", "description": "A MyMeyer branch without a worktree (local, or remote like origin/name); its last commit is used." },
        "ticket": { "type": "string", "description": "Ticket number: selects the MyMeyer worktree whose branch is that ticket (12345 or 12345-2)." },
        "libraries": { "type": "array", "items": { "type": "string" }, "description": "Matching library worktree paths to build and deploy with this version (plan lists them)." },
        "allLibraries": { "type": "boolean", "description": "Include every detected matching library worktree." }
        """;

    private const string PlanOutput = """
        "target": { "type": "object" }, "commit": { "type": "string" }, "canDeploy": { "type": "boolean" },
        "frontendMappingsChecked": { "type": "boolean" }, "errors": { "type": "array", "items": { "type": "string" } },
        "notes": { "type": "array", "items": { "type": "string" } }, "changeCount": { "type": "integer" },
        "changes": { "type": "array", "items": { "type": "string" } }, "frontend": { "type": "array", "items": { "type": "object" } },
        "projects": { "type": "array", "items": { "type": "object" } }, "libraries": { "type": "array", "items": { "type": "object" } }
        """;

    private const string WarmUpOutput = """
        "url": { "type": "string" }, "outcome": { "type": "string", "enum": ["Ready", "Failed", "StillWarming", "Skipped"] },
        "ready": { "type": "boolean" }, "status": { "type": "integer" }, "seconds": { "type": "number" }, "attempts": { "type": "integer" },
        "summary": { "type": "string" }, "detail": { "type": "string" }, "error": { "type": "object" }
        """;

    private sealed record Hints(bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld);
    private sealed record Tool(string Name, string Title, string Description, string Input, string Output, Hints Hints, bool ChangesFarm,
        Func<Call, Task<Result>> Run);
    private sealed record Result(string Text, object? Structured = null, bool Error = false);
    private sealed class Refusal(string message) : Exception(message);

    private sealed class Call(McpServer server, JsonObject arguments, JsonNode? token, CancellationToken cancel)
    {
        private int step;
        public CancellationToken Cancel => cancel;

        public string? Text(string name) => arguments[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text.Trim().Length == 0 ? null : text.Trim(),
            _ => throw new Refusal($"{name} must be a string.")
        };

        public bool Flag(string name, bool fallback = false) => arguments[name] switch
        {
            null => fallback,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => throw new Refusal($"{name} must be true or false.")
        };

        public int? Number(string name) => arguments[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            _ => throw new Refusal($"{name} must be a whole number.")
        };

        public List<string> Texts(string name) => arguments[name] switch
        {
            null => [],
            JsonArray items when items.All(item => item is JsonValue value && value.TryGetValue<string>(out _)) => items.Select(item => item!.GetValue<string>()).ToList(),
            _ => throw new Refusal($"{name} must be an array of strings.")
        };

        public void Progress(string message)
        {
            Console.Error.WriteLine(message);
            if (token is null || cancel.IsCancellationRequested) return;
            server.Send(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                ["params"] = new JsonObject { ["progressToken"] = token.DeepClone(), ["progress"] = Interlocked.Increment(ref step), ["message"] = message }
            });
        }
    }

    private readonly Stream output;
    private readonly bool readOnly;
    private readonly object writing = new();
    private readonly SemaphoreSlim farm = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> calls = new();
    private readonly ConcurrentDictionary<Task, bool> pending = new();
    private readonly List<Tool> tools;

    private McpServer(Stream output, bool readOnly)
    {
        this.output = output;
        this.readOnly = readOnly;
        tools = Tools().Where(tool => !readOnly || !tool.ChangesFarm).ToList();
    }

    public static async Task<int> Run(bool readOnly)
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var log = Console.Error;
        Isolate();
        Console.SetOut(log);
        Console.SetIn(TextReader.Null);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(log)
        });
        var server = new McpServer(output, readOnly);
        log.WriteLine($"Cultivate {Files.Version} MCP server{(readOnly ? " (read-only)" : "")} on stdio. Settings: {Settings.FilePath}");
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line)
        {
            try { server.Receive(line); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                log.WriteLine("Could not handle a message: " + exception);
                server.Fail(null, -32603, "Internal error: " + exception.Message);
            }
        }
        foreach (var call in server.calls.Values) call.Cancel();
        await Task.WhenAll(server.pending.Keys);
        return 0;
    }

    // Children (Git, MSBuild and its reused nodes, PowerShell, the saved-file worker) inherit every inheritable handle. A copy of the
    // protocol pipes could write into stdout or keep the client's pipes open after this server exits, so they get NUL instead.
    private static void Isolate()
    {
        const int input = -10, outputHandle = -11, error = -12;
        nul = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        SetHandleInformation(nul.DangerousGetHandle(), 1, 1);
        foreach (var which in new[] { input, outputHandle, error })
        {
            SetHandleInformation(GetStdHandle(which), 1, 0);
            SetStdHandle(which, nul.DangerousGetHandle());
        }
    }

    private void Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Json) + "\n");
        lock (writing)
        {
            try
            {
                output.Write(bytes);
                output.Flush();
            }
            catch (IOException exception) { Console.Error.WriteLine("The client closed stdout: " + exception.Message); }
        }
    }

    private void Reply(JsonNode? id, JsonObject result) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private void Fail(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error });
    }

    private static JsonNode? Field(JsonNode? node, string name) => node is JsonObject item && item.TryGetPropertyValue(name, out var value) ? value : null;

    internal void Receive(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException)
        {
            Fail(null, -32700, "Parse error: each line must be one JSON-RPC message.");
            return;
        }
        if (message is not JsonObject request || Field(request, "jsonrpc")?.ToString() != "2.0")
        {
            Fail(null, -32600, "Invalid request: expected one JSON-RPC 2.0 object per line.");
            return;
        }
        var id = Field(request, "id");
        var method = Field(request, "method") is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
        if (method is null)
        {
            if (id is null) Fail(null, -32600, "Invalid request: method is missing.");
            return; // A response; this server sends no requests.
        }
        var parameters = Field(request, "params");
        if (id is null)
        {
            if (method == "notifications/cancelled" && Field(parameters, "requestId") is { } cancelled &&
                calls.TryGetValue(cancelled.ToJsonString(), out var call))
            {
                Console.Error.WriteLine($"Cancellation requested for request {cancelled.ToJsonString()}.");
                call.Cancel();
            }
            return;
        }
        var requested = Field(Field(parameters, "_meta"), VersionKey)?.ToString();
        if (requested is not null && requested != Modern)
        {
            Fail(id, -32022, "Unsupported protocol version", new JsonObject { ["supported"] = new JsonArray(Modern), ["requested"] = requested });
            return;
        }
        JsonObject Complete(JsonObject result)
        {
            if (requested is not null) result["resultType"] = "complete";
            return result;
        }
        switch (method)
        {
            case "initialize":
                var asked = Field(parameters, "protocolVersion")?.ToString();
                Reply(id, new JsonObject
                {
                    ["protocolVersion"] = Legacy.Contains(asked) ? asked : Legacy[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = ServerInfo(), ["instructions"] = Instructions
                });
                break;
            case "server/discover":
                Reply(id, new JsonObject
                {
                    ["resultType"] = "complete", ["supportedVersions"] = new JsonArray(Modern),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["instructions"] = Instructions,
                    ["ttlMs"] = 3_600_000, ["cacheScope"] = "private",
                    ["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = ServerInfo() }
                });
                break;
            case "ping": Reply(id, Complete(new JsonObject())); break;
            case "tools/list":
                var list = Complete(new JsonObject { ["tools"] = new JsonArray(tools.Select(Describe).ToArray()) });
                if (requested is not null)
                {
                    list["ttlMs"] = 3_600_000;
                    list["cacheScope"] = "private";
                }
                Reply(id, list);
                break;
            case "tools/call": Start(id, parameters, Complete); break;
            default: Fail(id, -32601, $"Method not found: {method}"); break;
        }
    }

    private static JsonObject ServerInfo() => new() { ["name"] = "cultivate", ["title"] = "Cultivate", ["version"] = Files.Version };

    private static JsonObject Describe(Tool tool) => new()
    {
        ["name"] = tool.Name, ["title"] = tool.Title, ["description"] = tool.Description,
        ["inputSchema"] = JsonNode.Parse(tool.Input), ["outputSchema"] = JsonNode.Parse(tool.Output),
        ["annotations"] = new JsonObject
        {
            ["title"] = tool.Title, ["readOnlyHint"] = tool.Hints.ReadOnly, ["destructiveHint"] = tool.Hints.Destructive,
            ["idempotentHint"] = tool.Hints.Idempotent, ["openWorldHint"] = tool.Hints.OpenWorld
        }
    };

    private void Start(JsonNode id, JsonNode? parameters, Func<JsonObject, JsonObject> complete)
    {
        var name = Field(parameters, "name")?.ToString();
        var tool = tools.FirstOrDefault(item => item.Name == name);
        if (tool is null)
        {
            Fail(id, -32602, $"Unknown tool: {name}." + (readOnly && Tools().Any(item => item.Name == name)
                ? " This server runs with --read-only, which leaves out the tools that change the webfarm." : ""));
            return;
        }
        var key = id.ToJsonString();
        var cancel = new CancellationTokenSource();
        if (!calls.TryAdd(key, cancel))
        {
            Fail(id, -32600, $"Request id {key} is already in use.");
            return;
        }
        // Taken before the call starts, so calls run in the order they arrived.
        if (tool.ChangesFarm && !farm.Wait(0))
        {
            calls.TryRemove(key, out _);
            Reply(id, complete(Render(new("Another activate or restore is still running in this server. Wait for its result, then check deployment_status.", Error: true))));
            return;
        }
        var arguments = Field(parameters, "arguments");
        var token = Field(Field(parameters, "_meta"), "progressToken");
        var task = Task.Run(async () =>
        {
            Result result;
            try
            {
                result = arguments is null or JsonObject
                    ? await tool.Run(new Call(this, arguments as JsonObject ?? new JsonObject(), token, cancel.Token))
                    : new("arguments must be an object.", Error: true);
            }
            catch (Refusal refusal) { result = new(refusal.Message, Error: true); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result = new("Cancelled.", Error: true); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = Diagnostics.Record(exception, tool.Title);
                result = new($"{report.Label}\n{report.Text}", Error: true);
            }
            finally
            {
                if (tool.ChangesFarm) farm.Release();
                calls.TryRemove(key, out _);
            }
            if (cancel.IsCancellationRequested) Console.Error.WriteLine($"{tool.Name} ({key}) ended after cancellation: {result.Text.Split('\n')[0]}");
            else Reply(id, complete(Render(result)));
        });
        pending.TryAdd(task, true);
        task.ContinueWith(done => pending.TryRemove(done, out _), TaskScheduler.Default);
    }

    private static JsonObject Render(Result result)
    {
        var structured = result.Structured is null ? null : JsonSerializer.SerializeToNode(result.Structured, Json);
        var text = structured is null ? result.Text : result.Text + "\n" + structured.ToJsonString(Json);
        var rendered = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (structured is not null) rendered["structuredContent"] = structured;
        if (result.Error) rendered["isError"] = true;
        return rendered;
    }

    private static string Input(string properties = "", params string[] required) =>
        $$"""{ "type": "object", "properties": { {{properties}} }, "required": [{{string.Join(", ", required.Select(name => $"\"{name}\""))}}], "additionalProperties": false }""";

    private static string Output(string properties) => $$"""{ "type": "object", "properties": { {{properties}} } }""";

    private static IEnumerable<Tool> Tools() =>
    [
        new("list_targets", "List MyMeyer worktrees and branches",
            "List the MyMeyer worktrees (with the ticket each branch belongs to) that plan, build and activate accept, and optionally the " +
            "branches without a worktree. Read-only; about a second.",
            Input("""
                "includeBranches": { "type": "boolean", "description": "Also list local and remote branches without a worktree." },
                "filter": { "type": "string", "description": "Only entries whose branch or path contains this text." }
                """),
            Output("""
                "worktrees": { "type": "array", "items": { "type": "object", "properties": { "branch": { "type": "string" }, "worktree": { "type": "string" }, "ticket": { "type": "string" } } } },
                "branches": { "type": "array", "items": { "type": "string" } }
                """),
            new(true, false, true, false), false, ListTargets),
        new("deployment_status", "Show the active deployment",
            "What is deployed on the dev webfarm right now: whether a deployment is active, its branch and worktree, the webfarm files it " +
            "replaced, and whether automatic updates are on. Check it before activating, since activating replaces it. Read-only; a few seconds.",
            Input(),
            Output("""
                "active": { "type": "boolean" }, "branch": { "type": "string" }, "worktree": { "type": "string" }, "sourceRef": { "type": "string" },
                "automaticUpdates": { "type": "boolean" }, "lastError": { "type": "string" }, "heartbeat": { "type": "string" },
                "files": { "type": "array", "items": { "type": "string" } }, "libraries": { "type": "array", "items": { "type": "string" } },
                "excludedPaths": { "type": "array", "items": { "type": "string" } }
                """),
            new(true, false, true, false), false, DeploymentStatus),
        new("ticket_workspaces", "Find a ticket's checkouts",
            "Find a ticket's checkouts across every repository (MyMeyer, libraries, SQL and others): branch, path, uncommitted and unpushed " +
            "counts, and solutions; for one ticket also its SQL files compared with the local SQL mirror. pullRequests: true adds each " +
            "branch's GitHub pull requests through the gh CLI (slower). Read-only; a few seconds.",
            Input("""
                "ticket": { "type": "string", "description": "Ticket number or branch name; omit to list every ticket." },
                "pullRequests": { "type": "boolean", "description": "Also look up pull requests with the gh CLI." }
                """),
            Output("""
                "workspaces": { "type": "array", "items": { "type": "object" } }
                """),
            new(true, false, true, true), false, TicketWorkspacesTool),
        new("worktrees", "List every worktree",
            "List every linked worktree of the user's repositories with its ticket, uncommitted and unpushed work, and whether removing it " +
            "would lose anything; sizes: true also measures disk use (slow on large worktrees). Read-only.",
            Input("""
                "ticket": { "type": "string", "description": "Only worktrees of this ticket." },
                "sizes": { "type": "boolean", "description": "Measure each worktree's size and last write." }
                """),
            Output("""
                "worktrees": { "type": "array", "items": { "type": "object" } }
                """),
            new(true, false, true, false), false, Worktrees),
        new("doctor", "Check Cultivate's setup",
            "Check Cultivate's prerequisites (runtime files, Git, MSBuild hooks, webfarm reachability, deployment journal) and return the " +
            "settings Cultivate uses. Works before Cultivate is set up; use it when a tool says Cultivate is not set up. Read-only; 5 to 25 seconds.",
            Input(),
            Output("""
                "ready": { "type": "boolean" }, "checks": { "type": "array", "items": { "type": "object", "properties": { "level": { "type": "string" }, "message": { "type": "string" } } } },
                "settings": { "type": "object" }, "settingsError": { "type": "string" }
                """),
            new(true, false, true, true), false, Doctor),
        new("last_failure", "Show the last failure",
            "The diagnosis Cultivate recorded for its most recent failure, from any command, the terminal UI, or this server: stage, " +
            "project, first errors, next checks and log paths. Read-only; instant.",
            Input(),
            Output("""
                "found": { "type": "boolean" }, "path": { "type": "string" }, "recordedUtc": { "type": "string" },
                "report": { "type": "string" }, "exception": { "type": "string" }
                """),
            new(true, false, true, false), false, LastFailure),
        new("plan", "Preview a deployment",
            "Preview what activating a MyMeyer worktree or branch would deploy: changed files, the projects that would be built (only those " +
            "owning changed code), frontend destinations, matching library worktrees, notes and blocking errors (canDeploy). Changes nothing; " +
            "a committed branch is read through a temporary snapshot. A few seconds." + LookupNote +
            " Pass frontendMappings: false to skip that lookup; frontend destinations are then not checked.",
            Input(TargetProperties + """
                , "frontendMappings": { "type": "boolean", "description": "Look up frontend destinations (default true). False skips the database lookup." }
                """),
            Output(PlanOutput),
            new(true, false, true, true), false, Plan),
        new("build", "Build the changed projects",
            "Compile only the changed projects of a MyMeyer worktree or branch exactly as activate would, without deploying anything. Use it " +
            "to check that a change compiles instead of building the whole solution. On failure returns structured compile errors (file " +
            "relative to the checkout, line, column, code, message, project). Builds in a temporary snapshot: the checkout, its branches and " +
            "the webfarm are not changed. Roughly 15 to 90 seconds per project. Never looks up frontend mappings or reads a database.",
            Input(TargetProperties),
            Output("""
                "succeeded": { "type": "boolean" }, "target": { "type": "object" }, "projects": { "type": "array", "items": { "type": "object" } },
                "outputs": { "type": "array", "items": { "type": "string" } }, "seconds": { "type": "number" },
                "errors": { "type": "array", "items": { "type": "object", "properties": { "file": { "type": "string" }, "line": { "type": "integer" }, "column": { "type": "integer" }, "code": { "type": "string" }, "message": { "type": "string" }, "project": { "type": "string" } } } },
                "planErrors": { "type": "array", "items": { "type": "string" } }, "stage": { "type": "string" }, "diagnosis": { "type": "string" },
                "logs": { "type": "array", "items": { "type": "string" } }, "runDirectory": { "type": "string" }, "notes": { "type": "array", "items": { "type": "string" } }
                """),
            new(true, false, true, false), false, Build),
        new("activate", "Deploy to the dev webfarm",
            "Deploy a MyMeyer worktree or branch to the user's dev webfarm: build the changed projects in a snapshot, copy their DLLs and " +
            "changed frontend files to the webfarm while journaling the originals, then request the dev site until it answers and report " +
            "compile or server errors (warmUp). Replaces the active deployment, whoever made it. Requires confirm: true, given only when the " +
            "user agreed to deploy. One-time by default; automaticUpdates: true keeps redeploying saved files until restore. Typically 30 to " +
            "180 seconds including the warm-up. Cancelling before the deploy step leaves the webfarm unchanged; once deploying has started " +
            "the activation finishes without a reply, and deployment_status shows the outcome." + LookupNote,
            Input(TargetProperties + """
                , "confirm": { "type": "boolean", "description": "Must be true: the user agreed to change the shared dev webfarm." },
                "automaticUpdates": { "type": "boolean", "description": "Keep deploying saved changes until restore (default false)." },
                "warmup": { "type": "boolean", "description": "Request the dev site after deploying (default true)." },
                "stopLegacyWatcher": { "type": "boolean", "description": "Stop the old WebfarmFileSync watcher if it is running, instead of refusing." }
                """, "confirm"),
            Output("""
                "deployed": { "type": "boolean" }, "branch": { "type": "string" }, "worktree": { "type": "string" }, "sourceRef": { "type": "string" },
                "automaticUpdates": { "type": "boolean" }, "replaced": { "type": "object" }, "legacyWatcher": { "type": "string" },
                "updated": { "type": "array", "items": { "type": "object", "properties": { "path": { "type": "string" }, "change": { "type": "string" } } } },
                "warmUp": { "type": "object" }, "warmUpNote": { "type": "string" }, "siteReady": { "type": "boolean" }
                """),
            new(false, true, false, true), true, Activate),
        new("restore", "Restore the original webfarm files",
            "Put the original webfarm files back and end the active deployment, which also stops its automatic updates. Files changed " +
            "outside Cultivate since deploying are kept and listed. Requires confirm: true. Repeating it is harmless: with nothing active " +
            "it changes nothing. A few seconds.",
            Input("""
                "confirm": { "type": "boolean", "description": "Must be true: the user agreed to restore the shared dev webfarm." }
                """, "confirm"),
            Output("""
                "restored": { "type": "boolean" }, "previous": { "type": "object" }, "kept": { "type": "array", "items": { "type": "string" } }
                """),
            new(false, true, true, true), true, Restore),
        new("warmup", "Request the dev site",
            "Request the dev site until it answers, then report ready, a compile error, or a server error. Use it after activate when its " +
            "warm-up was skipped or stopped, or to check the site at any time. Changes no files, though the request makes the site compile. " +
            "Up to timeoutSeconds (default 300); usually 10 to 120 seconds after a deployment.",
            Input("""
                "url": { "type": "string", "description": "Address to request; defaults to the configured dev site." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Overall limit in seconds (default 300)." }
                """),
            Output(WarmUpOutput),
            new(true, false, true, true), false, WarmupTool)
    ];

    private static async Task<Settings> Configured()
    {
        var settings = await Settings.Read();
        var problems = settings.Problems();
        return problems.Count == 0 ? settings : throw new Refusal("Cultivate is not set up yet:\n  " + string.Join("\n  ", problems) +
            "\nAsk the user to run `cultivate setup` in a terminal, then `cultivate doctor` (or the doctor tool) to check it. " +
            $"Settings file: {Settings.FilePath}");
    }

    private static async Task<Target> Resolve(Settings settings, GitRepository git, Call call)
    {
        var worktree = call.Text("worktree");
        var branch = call.Text("branch");
        var ticket = call.Text("ticket");
        if (new[] { worktree, branch, ticket }.Count(value => value is not null) != 1)
            throw new Refusal("Pass exactly one of worktree, branch, or ticket. list_targets shows the choices.");
        var targets = await git.Targets();
        if (ticket is not null)
        {
            var matches = targets.Where(target => target.Worktree is not null && WorktreeCatalog.TicketOf(target.Branch) == ticket).ToList();
            return matches.Count == 1 ? matches[0] : throw new Refusal(matches.Count == 0
                ? $"No MyMeyer worktree is on a branch of ticket {ticket}. ticket_workspaces lists every repository's checkouts."
                : $"Ticket {ticket} has several MyMeyer worktrees ({string.Join(", ", matches.Select(target => target.Worktree))}). Pass one as worktree.");
        }
        return targets.FirstOrDefault(target => worktree is not null && target.Worktree is not null && Files.Comparer.Equals(Files.Full(target.Worktree), Files.Full(worktree)) ||
            worktree is null && target.Worktree is null && (target.Branch == branch || target.Ref == branch))
            ?? throw new Refusal(worktree is not null
                ? $"{worktree} is not a worktree of the MyMeyer repository {settings.RepositoryRoot}. Library worktrees go in libraries; list_targets shows the choices."
                : $"No MyMeyer branch named {branch} without a worktree. A branch with a worktree is selected by its worktree; list_targets shows the choices.");
    }

    private static List<string> Libraries(Call call) => (call.Flag("allLibraries") ? new List<string> { LibraryLoop.Every } : []).Concat(call.Texts("libraries")).ToList();

    private static Task<Dictionary<string, string>> NoLookup(string _) => Task.FromResult(new Dictionary<string, string>());

    private static void DropMappingErrors(Plan plan) =>
        plan.Errors.RemoveAll(error => error.StartsWith("No Code Release Manager destination for ", StringComparison.Ordinal));

    private static object TargetOf(Target target) => new { target.Branch, target.Worktree, target.Ref };

    private static object Summary(Plan plan, bool mappings) => new
    {
        Target = TargetOf(plan.Target), plan.Commit, plan.CanDeploy, FrontendMappingsChecked = mappings, plan.Errors, plan.Notes,
        ChangeCount = plan.Changes.Count, Changes = plan.Changes.Take(200).Select(change => change.Deleted ? change.Path + " (deleted)" : change.Path),
        Frontend = mappings
            ? plan.Frontend.Select(file => new { file.Source, Destination = (string?)file.RelativePath, file.Delete }).ToList()
            : plan.Changes.Where(change => Planner.FrontendExtensions.Contains(Path.GetExtension(change.Path)) && !Planner.IsExcluded(change.Path) &&
                !plan.ExcludedPaths.Contains(change.Path, Files.Comparer)).Select(change => new { Source = change.Path, Destination = (string?)null, Delete = change.Deleted }).ToList(),
        Projects = plan.Projects.Select(project => new { Project = project.RelativePath, project.Assembly, Packages = project.PackageDependencies.Select(Path.GetFileName) }),
        Libraries = plan.Libraries.Select(library => new
        {
            library.Repository, library.Worktree, library.Branch, library.Included, library.Error, library.Notes, library.Warnings,
            Projects = library.Projects.Select(project => new { Project = project.RelativePath, project.Assembly, project.Reason })
        })
    };

    private static async Task<Result> ListTargets(Call call)
    {
        var settings = await Configured();
        var filter = call.Text("filter");
        var targets = (await new GitRepository(settings).Targets()).Where(target => filter is null ||
            target.Branch.Contains(filter, StringComparison.OrdinalIgnoreCase) || target.Worktree?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true).ToList();
        var worktrees = targets.Where(target => target.Worktree is not null)
            .Select(target => new { target.Branch, target.Worktree, Ticket = WorktreeCatalog.TicketOf(target.Branch) }).ToList();
        var branches = targets.Where(target => target.Worktree is null).Select(target => target.Branch).ToList();
        return new($"{worktrees.Count} MyMeyer worktree(s); {branches.Count} branch(es) without a worktree{(call.Flag("includeBranches") ? "" : " (pass includeBranches to list them)")}.",
            new { Worktrees = worktrees, Branches = call.Flag("includeBranches") ? branches : null });
    }

    private static object Status(DeploymentState? state) => state?.Active != true ? new { Active = false } : (object)new
    {
        Active = true, state.Branch, Worktree = Blank(state.SourceRoot), state.SourceRef, AutomaticUpdates = state.WatchEnabled,
        LastError = Blank(state.LastError), Heartbeat = Blank(state.Heartbeat), Files = (state.Files ?? []).Select(file => file.RelativePath),
        Libraries = (state.Libraries ?? []).Select(library => library.Worktree), state.ExcludedPaths
    };

    // The journal stores missing values as JSON null, which replaces the model's empty defaults.
    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static async Task<Result> DeploymentStatus(Call call)
    {
        var state = await new Bridge(await Configured()).Status();
        return new(state?.Active == true ? $"Active: {state.Branch} ({state.Files?.Count ?? 0} webfarm file(s) replaced)." : "No active deployment; the webfarm has its original files.",
            Status(state));
    }

    private static async Task<Result> TicketWorkspacesTool(Call call)
    {
        var settings = await Configured();
        var ticket = call.Text("ticket");
        var args = new List<string> { "tickets" };
        if (ticket is not null) args.AddRange(["--ticket", ticket]);
        if (call.Flag("pullRequests")) args.Add("--prs");
        using var writer = new StringWriter();
        await TicketWorkspaces.Command(settings, new GitRepository(settings), args.ToArray(), TicketPullRequests.GitHub, writer);
        var parsed = Camel(JsonNode.Parse(writer.ToString()));
        var workspaces = parsed as JsonArray ?? new JsonArray(parsed);
        return new($"{workspaces.Count} workspace(s).", new { Workspaces = workspaces });
    }

    private static JsonNode? Camel(JsonNode? node) => node switch
    {
        JsonObject item => new JsonObject(item.Where(property => property.Value is not null)
            .Select(property => KeyValuePair.Create(JsonNamingPolicy.CamelCase.ConvertName(property.Key), Camel(property.Value)))),
        JsonArray items => new JsonArray(items.Select(Camel).ToArray()),
        _ => node?.DeepClone()
    };

    private static async Task<Result> Worktrees(Call call)
    {
        var settings = await Configured();
        var git = new GitRepository(settings);
        DeploymentState? state = null;
        try { state = await new Bridge(settings).Status(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException) { }
        var ticket = call.Text("ticket");
        var checkouts = (await WorktreeCatalog.Discover(settings, git)).Where(checkout => ticket is null || checkout.Ticket == ticket).ToList();
        var entries = await WorktreeTidy.Entries(git, checkouts, TidyGuard.Current(state), call.Flag("sizes"));
        return new($"{entries.Count} linked worktree(s).", new { Worktrees = entries });
    }

    private static async Task<Result> Doctor(Call call)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Cultivate.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Files.Home,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Cultivate.dll"));
        start.ArgumentList.Add("doctor");
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start cultivate doctor.");
        process.StandardInput.Close();
        var report = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(call.Cancel); }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
        var checks = DoctorChecks(await report);
        object? settings = null;
        string? settingsError = null;
        try
        {
            var read = await Settings.Read();
            settings = new
            {
                File = Settings.FilePath, Saved = File.Exists(Settings.FilePath), read.RepositoryRoot, read.BaseBranch, read.WebfarmRoot, read.SiteUrl,
                read.WorktreesRoot, read.MSBuild, read.StateRoot, read.MappingAssembly, read.WatchSavedFiles, read.WarmUpAfterDeploy,
                read.StopLegacyWatcher, read.LibraryRepositories, Problems = read.Problems()
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            settingsError = exception.Message;
        }
        var failed = checks.Count(check => check.Level == "FAIL");
        var stderr = (await errors).Trim();
        return new(failed == 0 && process.ExitCode == 0 ? "Ready." : $"{failed} problem(s). Fix the FAIL checks." + (stderr.Length == 0 ? "" : "\n" + stderr),
            new { Ready = process.ExitCode == 0, Checks = checks, Settings = settings, SettingsError = settingsError });
    }

    internal sealed record DoctorCheck(string Level, string Message);

    /// <summary>The OK, WARN and FAIL lines of <c>cultivate doctor</c>, joining lines the console wrapped.</summary>
    internal static List<DoctorCheck> DoctorChecks(string report)
    {
        var checks = new List<DoctorCheck>();
        var continuing = false;
        foreach (var line in report.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (DoctorLine().Match(line) is { Success: true } match)
            {
                checks.Add(new(match.Groups["level"].Value, match.Groups["message"].Value.Trim()));
                continuing = true;
            }
            else if (line.Trim().Length == 0) continuing = false;
            else if (continuing) checks[^1] = checks[^1] with { Message = checks[^1].Message + " " + line.Trim() };
        }
        return checks;
    }

    private static Task<Result> LastFailure(Call call)
    {
        var path = Path.Combine(Files.Home, "Logs", "last-error.log");
        if (!File.Exists(path)) return Task.FromResult(new Result("No failure has been recorded.", new { Found = false }));
        var text = File.ReadAllText(path);
        var split = text.IndexOf("\n\nFull exception:\n", StringComparison.Ordinal);
        var report = split < 0 ? text : text[..split];
        var exception = split < 0 ? null : text[(split + "\n\nFull exception:\n".Length)..].Trim();
        if (exception?.Length > 4000) exception = exception[..4000] + "…";
        return Task.FromResult(new Result(report.Trim(), new
        {
            Found = true, Path = path, RecordedUtc = File.GetLastWriteTimeUtc(path).ToString("O"), Report = report.Trim(), Exception = exception
        }));
    }

    private static async Task<Result> Plan(Call call)
    {
        var settings = await Configured();
        var git = new GitRepository(settings);
        var mappings = call.Flag("frontendMappings", true);
        var plan = await new Planner(settings, git, new Bridge(settings)).Create(await Resolve(settings, git, call), mappings ? null : NoLookup, libraries: Libraries(call));
        if (!mappings) DropMappingErrors(plan);
        var verdict = plan.CanDeploy ? $"Deployable: {plan.Projects.Count} project(s) to build, {plan.Frontend.Count} frontend file(s)." :
            "Not deployable:\n" + string.Join("\n", plan.Errors);
        return new(verdict + (mappings ? "" : " Frontend destinations were not looked up."), Summary(plan, mappings));
    }

    private static async Task<Result> Build(Call call)
    {
        var settings = await Configured();
        var git = new GitRepository(settings);
        var target = await Resolve(settings, git, call);
        call.Progress($"Planning {target.Branch}");
        var plan = await new Planner(settings, git, new Bridge(settings)).Create(target, NoLookup, libraries: Libraries(call));
        DropMappingErrors(plan);
        if (!plan.CanDeploy)
            return new("Nothing was built: the plan has errors.\n" + string.Join("\n", plan.Errors),
                new { Succeeded = false, Target = TargetOf(target), PlanErrors = plan.Errors, plan.Notes }, true);
        if (plan.Projects.Count == 0 && !LibraryLoop.Assemblies(plan).Any())
            return new("No changed compiled projects: nothing to build.", new { Succeeded = true, Target = TargetOf(target), Projects = Array.Empty<object>(), plan.Notes });
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Capture.targets")))
            throw new FileNotFoundException("Cultivate's build capture target is missing. Reinstall Cultivate.");
        var run = Path.Combine(Files.Home, "Runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-build-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        Files.WriteJson(Path.Combine(run, "plan.json"), plan);
        var watch = Stopwatch.StartNew();
        var projects = plan.Projects.Select(project => new { Project = project.RelativePath, project.Assembly })
            .Concat(plan.Libraries.SelectMany(library => library.Deployed.Select(project => new { Project = Path.Combine(library.Worktree, project.RelativePath), project.Assembly })))
            .ToList();
        string? snapshot = null;
        try
        {
            call.Progress("Snapshotting the selected version (your checkout stays untouched)");
            snapshot = await git.Snapshot(plan, run);
            call.Cancel.ThrowIfCancellationRequested();
            var files = await new Pipeline(settings, git, new Bridge(settings)).Build(plan, snapshot, run, call.Progress);
            return new($"Built {string.Join(", ", projects.Select(project => project.Assembly))} in {watch.Elapsed.TotalSeconds:0} s. Nothing was deployed.", new
            {
                Succeeded = true, Target = TargetOf(target), Projects = projects, Outputs = files.Select(file => file.RelativePath),
                Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), RunDirectory = run, plan.Notes
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or JsonException)
        {
            var report = Diagnostics.Record(exception, "Build");
            var messages = new List<string>();
            for (var current = exception; current is not null; current = current.InnerException) messages.Add(current.Message);
            return new($"{report.Label}\n{report.Text}", new
            {
                Succeeded = false, Target = TargetOf(target), Projects = projects, Stage = report.Context.Stage, report.Diagnosis,
                Errors = CompileErrors(string.Join("\n", messages), snapshot), report.Logs, RunDirectory = run,
                Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1)
            }, true);
        }
        finally
        {
            if (snapshot is not null && Directory.Exists(snapshot)) await git.Git(settings.RepositoryRoot, "worktree", "remove", "--force", snapshot);
        }
    }

    /// <summary>Compiler and MSBuild errors in MSBuild's output, with paths in <paramref name="snapshot"/> made relative to it.</summary>
    internal static List<CompileError> CompileErrors(string text, string? snapshot)
    {
        var root = snapshot is null ? null : Files.Full(snapshot) + "\\";
        string Local(string path) => root is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
        var errors = new List<CompileError>();
        foreach (var line in text.Split('\n').Select(line => line.Trim()))
        {
            if (ErrorLine().Match(line) is not { Success: true } match) continue;
            var origin = match.Groups["origin"].Value.Trim();
            var location = Location().Match(origin);
            var file = location.Success ? location.Groups["file"].Value : origin.Contains('\\') || origin.Contains('/') ? origin : null;
            var error = new CompileError(file is null ? null : Local(file), location.Success ? int.Parse(location.Groups["line"].Value) : null,
                location.Groups["column"].Success ? int.Parse(location.Groups["column"].Value) : null, match.Groups["code"].Value.ToUpperInvariant(),
                match.Groups["message"].Value.Trim(), match.Groups["project"].Success ? Local(match.Groups["project"].Value.Trim()) : null);
            if (!errors.Contains(error)) errors.Add(error);
        }
        return errors;
    }

    private static async Task<Result> Activate(Call call)
    {
        if (!call.Flag("confirm"))
            throw new Refusal("activate changes the shared dev webfarm, so nothing was deployed. Call it again with confirm: true once the user has agreed to deploy.");
        var settings = await Configured();
        var git = new GitRepository(settings);
        var bridge = new Bridge(settings);
        var target = await Resolve(settings, git, call);
        call.Progress($"Planning {target.Branch}");
        var plan = await new Planner(settings, git, bridge).Create(target, libraries: Libraries(call));
        if (!plan.CanDeploy) throw new Refusal("Nothing was deployed: the plan has errors.\n" + string.Join("\n", plan.Errors));
        string? stopped = null;
        var running = LegacyWatcher.Running(settings.WebfarmRoot);
        switch (LegacyWatcher.Decide(running.Count, settings.StopLegacyWatcher || call.Flag("stopLegacyWatcher"), canAsk: false))
        {
            case LegacyWatcherAction.Refuse:
                throw new Refusal($"The old WebfarmFileSync watcher (process {string.Join(", ", running.Select(process => process.Id))}) is running and could " +
                    "overwrite this deployment, so nothing was deployed. Call activate again with stopLegacyWatcher: true to stop it, or ask the user to run " +
                    "`cultivate setup --stop-legacy-watcher true` to always stop it.");
            case LegacyWatcherAction.Stop:
                stopped = LegacyWatcher.Stop(running);
                call.Progress(stopped);
                break;
        }
        var previous = await bridge.Status();
        call.Cancel.ThrowIfCancellationRequested();
        var state = await new Pipeline(settings, git, bridge).Activate(plan, call.Progress, call.Flag("automaticUpdates") && settings.WatchSavedFiles, cancel: call.Cancel);
        string? note = null;
        if (!call.Flag("warmup", true)) note = "Warm-up skipped (warmup: false).";
        else if (!Warmup.ShouldWarmUp(settings, state, out var url))
            note = settings.SiteUrl.Length == 0 ? "No dev site address is configured, so the site was not requested." :
                !settings.WarmUpAfterDeploy ? "Warm-up after deployments is off in the settings." : "Nothing changed on the webfarm, so the site was not requested.";
        else
        {
            try
            {
                using var handler = Warmup.CreateHandler(url);
                var compiled = Warmup.ChangedBin(state);
                state.WarmUp = await Warmup.Run(url, handler, WarmupOptions.Real(WarmupOptions.DefaultLimit), ReportWarmup(call), call.Cancel);
                if (state.WarmUp.Ready) WarmupHistory.Load(Warmup.HistoryPath).Record(compiled, state.WarmUp.Seconds, Warmup.HistoryPath);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                note = "The site warm-up stopped: " + exception.Message;
            }
        }
        var replaced = previous?.Active == true ? new { previous.Branch, Worktree = Blank(previous.SourceRoot) } : null;
        return new($"Deployed {state.Branch}: {state.Updated.Count} webfarm file(s) updated" +
            (replaced is null ? "" : $", replacing {replaced.Branch}") + ". " + (state.WarmUp is { } warmUp ? Warmup.Describe(warmUp) : note), new
        {
            Deployed = true, state.Branch, Worktree = Blank(state.SourceRoot), state.SourceRef,
            AutomaticUpdates = state.WatchEnabled, Replaced = replaced, LegacyWatcher = stopped,
            Updated = state.Updated.Select(file => new { Path = file.RelativePath, file.Change }),
            state.WarmUp, WarmUpNote = note, SiteReady = state.WarmUp?.Ready
        });
    }

    private static Action<WarmupProgress> ReportWarmup(Call call)
    {
        var started = 0;
        return update =>
        {
            call.Progress(update.Attempt == started ? $"Warm-up: {update.Said}" : $"Warm-up: request {update.Attempt}");
            started = update.Attempt;
        };
    }

    private static async Task<Result> Restore(Call call)
    {
        if (!call.Flag("confirm"))
            throw new Refusal("restore changes the shared dev webfarm, so nothing was restored. Call it again with confirm: true once the user has agreed.");
        var bridge = new Bridge(await Configured());
        var previous = await bridge.Status();
        call.Progress("Restoring the original webfarm files");
        var result = await bridge.Call("Restore");
        var kept = result.TryGetProperty("Kept", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.GetString() ?? "").ToList() : [];
        return new(previous?.Active == true
            ? $"Restored the original webfarm files and ended {previous.Branch}." + (kept.Count == 0 ? "" : $" Kept {kept.Count} file(s) changed outside Cultivate.")
            : "Nothing was active; the webfarm already has its original files.",
            new { Restored = true, Previous = previous?.Active == true ? Status(previous) : null, Kept = kept });
    }

    private static async Task<Result> WarmupTool(Call call)
    {
        var settings = await Configured();
        var args = new List<string> { "warmup" };
        if (call.Text("url") is { } address) args.AddRange(["--url", address]);
        if (call.Number("timeoutSeconds") is { } seconds) args.AddRange(["--timeout", seconds.ToString()]);
        Uri url;
        TimeSpan limit;
        try { (url, limit) = Warmup.CommandTarget(settings, args.ToArray()); }
        catch (InvalidOperationException exception) { throw new Refusal(exception.Message.Replace("--url", "url").Replace("--timeout", "timeoutSeconds")); }
        using var handler = Warmup.CreateHandler(url);
        var result = await Warmup.Run(url, handler, WarmupOptions.Real(limit), ReportWarmup(call), call.Cancel);
        return new(Warmup.Describe(result), result);
    }

    [GeneratedRegex(@"^(?<origin>.*?)\s*:\s*(?:\w+\s+)?error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\[\]]+)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?(?:[,-]\d+)*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Location();

    [GeneratedRegex(@"^\s*(?<level>OK|WARN|FAIL)\s+(?<message>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DoctorLine();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
