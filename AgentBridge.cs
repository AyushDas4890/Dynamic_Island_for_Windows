using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>What Claude Code is doing, as shown in the island.</summary>
public sealed record AgentEvent(string Kind, string Project, string Activity, string Detail, string Diff = "");

/// <summary>A permission request waiting for Allow / Deny in the island.</summary>
public sealed class ApprovalRequest
{
    public required string Project { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string AllowLabel { get; init; } = "Allow";
    public string DenyLabel { get; init; } = "Deny";
    public DateTime Time { get; } = DateTime.Now;
    public string TimeText => Time.ToString("t");
    internal readonly TaskCompletionSource<bool?> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Allow() => Decision.TrySetResult(true);
    public void Deny() => Decision.TrySetResult(false);
}

/// <summary>
/// Receives Claude Code HTTP hooks on 127.0.0.1 (see ClaudeHooks). Every request must carry the
/// per-install token header. PermissionRequest holds the connection open until the island answers;
/// if nobody answers before the hook times out, Claude Code falls back to its own prompt.
/// </summary>
public sealed class AgentBridge
{
    public const int Port = 47321;

    private readonly Dispatcher _ui;
    private readonly string _token;
    private TcpListener? _listener;

    public event Action<AgentEvent>? Activity;
    public event Action<ApprovalRequest>? ApprovalNeeded;
    public event Action<ApprovalRequest>? ApprovalClosed;

    public AgentBridge(Dispatcher ui, string token) { _ui = ui; _token = token; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            _ = Handle(client);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (path, headers, body) = await ReadRequest(stream);
                if (!headers.TryGetValue("x-island-token", out var token) || token != _token)
                {
                    await Respond(stream, 403, "");
                    return;
                }
                if (path.StartsWith("/mcp"))
                {
                    var (status, mcpReply) = await Mcp(body);
                    await Respond(stream, status, mcpReply);
                    return;
                }
                string reply = await Dispatch(path, body);
                await Respond(stream, 200, reply);
            }
            catch (Exception)
            {
                // Client went away mid-request: nothing to do.
            }
        }
    }

    private async Task<string> Dispatch(string path, string body)
    {
        JsonObject json;
        try { json = JsonNode.Parse(body) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return ""; } // not ours to judge: let Claude Code carry on
        string evt = (string?)json["hook_event_name"] ?? path.Split('/').Last();
        string project = Path.GetFileName(((string?)json["cwd"] ?? "").TrimEnd('\\', '/'));
        if (project.Length == 0) project = "Claude Code";

        switch (evt)
        {
            case "UserPromptSubmit":
                Post(new AgentEvent("working", project, "Thinking…", ""));
                return "";

            case "PreToolUse":
            {
                var input = json["tool_input"] as JsonObject;
                var (activity, detail) = Describe((string?)json["tool_name"] ?? "", input);
                Post(new AgentEvent("working", project, activity, detail, DiffOf((string?)json["tool_name"] ?? "", input)));
                return "";
            }

            case "Notification":
                // permission prompts are handled by PermissionRequest; this covers "waiting for your input".
                if ((string?)json["notification_type"] != "permission_prompt")
                    Post(new AgentEvent("waiting", project, "Needs your input", (string?)json["message"] ?? ""));
                return "";

            case "Stop":
                Post(new AgentEvent("done", project, "Done", OneLine((string?)json["last_assistant_message"] ?? "", 160)));
                return "";

            case "PermissionRequest":
                return await AskPermission(project, (string?)json["tool_name"] ?? "", json["tool_input"] as JsonObject);
        }
        return "";
    }

    private async Task<string> AskPermission(string project, string tool, JsonObject? input)
    {
        var (_, detail) = Describe(tool, input);
        if (AppSettings.Current.AutoAllowReadOnly && tool is "Read" or "Grep" or "Glob" or "WebSearch")
            return Decision(true);

        var request = new ApprovalRequest { Project = project, Title = PermissionTitle(tool), Detail = detail };
        await _ui.InvokeAsync(() => ApprovalNeeded?.Invoke(request));

        var wait = TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.ApprovalTimeoutSeconds, 10, 280)); // hook timeout is 300 s
        var winner = await Task.WhenAny(request.Decision.Task, Task.Delay(wait));
        await _ui.InvokeAsync(() => ApprovalClosed?.Invoke(request));
        if (winner != request.Decision.Task || request.Decision.Task.Result is not bool allow)
            return ""; // no answer: let Claude Code ask in its own UI
        return Decision(allow);
    }

    private static string Decision(bool allow)
    {
        var decision = new JsonObject { ["behavior"] = allow ? "allow" : "deny" };
        if (!allow) decision["message"] = "The user denied this action from the Dynamic Island.";
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }

    // ---- MCP server (streamable HTTP, JSON responses): tools Claude can call to use the island ----

    private static readonly JsonArray McpTools = JsonNode.Parse("""
    [
      { "name": "island_notify",
        "description": "Show a short message in the user's Dynamic Island at the top of their Windows screen. Use it to tell the user something important without them watching the terminal (e.g. a long task finished, a result to look at).",
        "inputSchema": { "type": "object", "properties": {
            "title": { "type": "string", "description": "Short headline, a few words" },
            "message": { "type": "string", "description": "One or two sentences" } },
          "required": ["title", "message"] } },
      { "name": "island_ask",
        "description": "Ask the user a quick question in their Dynamic Island with two answer buttons, and wait for their click (up to two minutes). Returns the label they chose, or 'no answer'. Use for a decision you need before continuing.",
        "inputSchema": { "type": "object", "properties": {
            "question": { "type": "string" },
            "detail": { "type": "string", "description": "Optional context shown under the question" },
            "yes_label": { "type": "string", "description": "Label of the primary button, default 'Yes'" },
            "no_label": { "type": "string", "description": "Label of the secondary button, default 'No'" } },
          "required": ["question"] } },
      { "name": "island_progress",
        "description": "Show what you are working on right now in the user's Dynamic Island, e.g. 'Running tests 3/10'.",
        "inputSchema": { "type": "object", "properties": {
            "text": { "type": "string" } },
          "required": ["text"] } }
    ]
    """)!.AsArray();

    private async Task<(int Status, string Body)> Mcp(string body)
    {
        JsonObject req;
        try { req = JsonNode.Parse(body) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return (400, ""); }

        var id = req["id"]?.DeepClone();
        string method = (string?)req["method"] ?? "";
        if (id == null) return (202, ""); // notification (e.g. notifications/initialized)

        JsonNode? result;
        switch (method)
        {
            case "initialize":
                result = new JsonObject
                {
                    ["protocolVersion"] = (string?)req["params"]?["protocolVersion"] ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "dynamic-island", ["version"] = "1.1.0" },
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = McpTools.DeepClone() };
                break;
            case "tools/call":
                result = await CallTool((string?)req["params"]?["name"] ?? "", req["params"]?["arguments"] as JsonObject ?? new JsonObject());
                break;
            default:
                return (200, new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method },
                }.ToJsonString());
        }
        return (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
    }

    private async Task<JsonObject> CallTool(string name, JsonObject args)
    {
        string S(string k) => (string?)args[k] ?? "";
        string text;
        switch (name)
        {
            case "island_notify":
                Post(new AgentEvent("notify", "Claude", S("title"), OneLine(S("message"), 200)));
                text = "Shown in the island.";
                break;
            case "island_progress":
                Post(new AgentEvent("working", "Claude", OneLine(S("text"), 80), ""));
                text = "Shown in the island.";
                break;
            case "island_ask":
            {
                var request = new ApprovalRequest
                {
                    Project = "Claude asks",
                    Title = OneLine(S("question"), 90),
                    Detail = OneLine(S("detail"), 200),
                    AllowLabel = S("yes_label") is { Length: > 0 } y ? y : "Yes",
                    DenyLabel = S("no_label") is { Length: > 0 } n ? n : "No",
                };
                await _ui.InvokeAsync(() => ApprovalNeeded?.Invoke(request));
                var winner = await Task.WhenAny(request.Decision.Task, Task.Delay(TimeSpan.FromMinutes(2)));
                await _ui.InvokeAsync(() => ApprovalClosed?.Invoke(request));
                text = winner == request.Decision.Task && request.Decision.Task.Result is bool yes
                    ? (yes ? request.AllowLabel : request.DenyLabel)
                    : "no answer";
                break;
            }
            default:
                return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Unknown tool " + name }) };
        }
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    }

    private void Post(AgentEvent e) => _ui.InvokeAsync(() => Activity?.Invoke(e));

    /// <summary>Human wording for a tool call: ("Editing", "MainWindow.xaml").</summary>
    internal static (string Activity, string Detail) Describe(string tool, JsonObject? input)
    {
        string S(string key) => (string?)input?[key] ?? "";
        string File(string key) => Path.GetFileName(S(key));
        return tool switch
        {
            "Read" => ("Reading…", File("file_path")),
            "Edit" or "MultiEdit" => ("Editing…", File("file_path")),
            "Write" => ("Writing…", File("file_path")),
            "NotebookEdit" => ("Editing…", File("notebook_path")),
            "Bash" or "PowerShell" => ("Running…", OneLine(S("command"), 140)),
            "Grep" => ("Searching…", OneLine(S("pattern"), 80)),
            "Glob" => ("Finding files…", OneLine(S("pattern"), 80)),
            "WebFetch" => ("Browsing…", OneLine(S("url"), 100)),
            "WebSearch" => ("Searching the web…", OneLine(S("query"), 100)),
            "Task" or "Agent" => ("Delegating…", OneLine(S("description"), 100)),
            _ when tool.StartsWith("mcp__") => ("Using a tool…", tool.Split("__").Last()),
            _ => ("Using " + tool + "…", ""),
        };
    }

    /// <summary>A two-line preview of the change: "- old" / "+ new" (first changed line of each).</summary>
    private static string DiffOf(string tool, JsonObject? input)
    {
        static string First(string? s) => OneLine((s ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "", 70);
        switch (tool)
        {
            case "Edit":
                return "- " + First((string?)input?["old_string"]) + "\n+ " + First((string?)input?["new_string"]);
            case "MultiEdit" when (input?["edits"] is JsonArray { Count: > 0 } edits):
                return "- " + First((string?)edits[0]?["old_string"]) + "\n+ " + First((string?)edits[0]?["new_string"]);
            case "Write":
                return "+ " + First((string?)input?["content"]);
            case "Bash" or "PowerShell":
                return "$ " + OneLine((string?)input?["command"] ?? "", 120);
            default:
                return "";
        }
    }

    private static string OneLine(string s, int max)
    {
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string PermissionTitle(string tool) => tool switch
    {
        "Bash" or "PowerShell" => "Wants to run a command",
        "Edit" or "MultiEdit" or "NotebookEdit" => "Wants to edit a file",
        "Write" => "Wants to create a file",
        "Read" => "Wants to read a file",
        "WebFetch" => "Wants to open a web page",
        "WebSearch" => "Wants to search the web",
        _ when tool.StartsWith("mcp__") => "Wants to use " + tool.Split("__").ElementAtOrDefault(1),
        _ => "Wants to use " + tool,
    };

    // ---- minimal HTTP/1.1 ----

    private static async Task<(string Path, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) throw new IOException("closed");
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (buffer.Length > 1 << 20) throw new IOException("too large");
        }

        var all = buffer.ToArray();
        var lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
        string path = lines[0].Split(' ').ElementAtOrDefault(1) ?? "/";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }

        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (length > 4 << 20) throw new IOException("too large");
        var body = new MemoryStream();
        body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
        while (body.Length < length)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) break;
            body.Write(chunk, 0, n);
        }
        return (path, headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
        => data.AsSpan(0, length).IndexOf(pattern);

    private static async Task Respond(NetworkStream stream, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", _ => "Forbidden" };
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }
}

/// <summary>Registers the island's MCP server with Claude Code (user scope) through the `claude` CLI.</summary>
public static class ClaudeMcp
{
    public const string Name = "dynamic-island";

    public static async Task<(bool Ok, string Output)> Run(params string[] args)
    {
        var exe = AskClaude.ClaudeCodePath();
        if (exe == null) return (false, "Claude Code isn't installed.");
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode == 0, output.Trim());
    }

    public static async Task<bool> IsAdded() => (await Run("mcp", "get", Name)).Ok;

    public static Task<(bool Ok, string Output)> Add(string token) =>
        Run("mcp", "add", "--transport", "http", "--scope", "user", Name,
            $"http://127.0.0.1:{AgentBridge.Port}/mcp", "--header", "X-Island-Token: " + token);

    public static Task<(bool Ok, string Output)> Remove() => Run("mcp", "remove", "--scope", "user", Name);
}

/// <summary>Adds or removes the island's HTTP hooks in ~/.claude/settings.json.</summary>
public static class ClaudeHooks
{
    /// <summary>~/.claude/settings.json; overridable for `--write-hooks &lt;file&gt;` testing.</summary>
    internal static string SettingsPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    private static string Url => $"http://127.0.0.1:{AgentBridge.Port}/claude";
    private static readonly string[] Events = { "UserPromptSubmit", "PreToolUse", "PermissionRequest", "Notification", "Stop" };

    public static bool IsInstalled()
    {
        try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Url); }
        catch (IOException) { return false; }
    }

    public static void Install(string token)
    {
        var root = Load();
        Remove(root);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        foreach (var evt in Events)
        {
            var list = hooks[evt] as JsonArray ?? new JsonArray();
            hooks[evt] = list;
            var handler = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"{Url}/{evt}",
                ["timeout"] = evt == "PermissionRequest" ? 300 : 3,
                ["headers"] = new JsonObject { ["X-Island-Token"] = token },
            };
            var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
            if (evt is "PreToolUse" or "PermissionRequest") group["matcher"] = "*";
            list.Add(group);
        }
        Save(root);
    }

    public static void Uninstall()
    {
        if (!File.Exists(SettingsPath)) return;
        var root = Load();
        Remove(root);
        Save(root);
    }

    /// <summary>Removes only matcher groups whose handlers point at the island.</summary>
    private static void Remove(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray list) continue;
            foreach (var group in list.ToList())
                if (group?["hooks"] is JsonArray hs && hs.Any(h => ((string?)h?["url"])?.StartsWith(Url) == true))
                    list.Remove(group);
            if (list.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
               ?? throw new InvalidDataException("~/.claude/settings.json is not a JSON object");
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var backup = SettingsPath + ".island-backup";
        if (File.Exists(SettingsPath) && !File.Exists(backup)) File.Copy(SettingsPath, backup);
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
