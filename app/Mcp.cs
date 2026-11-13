using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lplus;

/// <summary>One server from mcp.json: a program started and spoken to over its input and output, or a web address.</summary>
sealed class McpServerConfig
{
    public string Name = "";
    public string Command = "";
    public List<string> Args = new();
    public Dictionary<string, string> Env = new();
    public string Url = "";
    public Dictionary<string, string> Headers = new();
    public bool Disabled;
    public JsonObject? Raw;   // the entry as it stands in the file, so settings Lplus doesn't know are kept
    public bool IsHttp => Url.Length > 0;

    /// <summary>What it is, in a line: the command and its arguments, or the address.</summary>
    public string Summary => IsHttp ? Url : (Command + " " + string.Join(" ", Args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a))).Trim();

    public McpServerConfig Clone() => new()
    {
        Name = Name, Command = Command, Args = Args.ToList(), Env = new(Env), Url = Url, Headers = new(Headers), Disabled = Disabled,
        Raw = (JsonObject?)Raw?.DeepClone(),
    };
}

/// <summary>A tool a server offers. Exposed is the name the model sees: server and tool joined, made unique.</summary>
sealed record McpTool(McpConnection Server, string Name, string Exposed, string Description, JsonNode? Schema);

/// <summary>
/// Reads mcp.json. The layout is the usual one:
/// { "mcpServers": { "name": { "command": "...", "args": [...], "env": {...} }, "other": { "url": "https://...", "headers": {...} } } }
/// </summary>
static class McpConfig
{
    public const string Empty = "{\n  \"mcpServers\": {\n  }\n}\n";

    public static List<McpServerConfig> Load(string path, out string? error)
    {
        error = null;
        var list = new List<McpServerConfig>();
        if (!File.Exists(path)) return list;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var servers = (root?["mcpServers"] ?? root?["servers"]) as JsonObject;
            if (servers == null) return list;
            foreach (var (name, node) in servers)
            {
                if (node is not JsonObject o) continue;
                var c = new McpServerConfig { Name = name, Command = Text(o["command"]), Url = Text(o["url"]), Raw = (JsonObject)o.DeepClone() };
                if (o["args"] is JsonArray args) c.Args = args.Select(Text).ToList();
                if (o["env"] is JsonObject env) foreach (var (k, v) in env) c.Env[k] = Text(v);
                if (o["headers"] is JsonObject headers) foreach (var (k, v) in headers) c.Headers[k] = Text(v);
                c.Disabled = o["disabled"] is JsonValue d && d.TryGetValue<bool>(out var off) && off;
                list.Add(c);
            }
        }
        catch (Exception ex) { error = "mcp.json could not be read: " + ex.Message; }
        return list;
    }

    static string Text(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";

    /// <summary>The servers in one "mcpServers" object, wherever it was found.</summary>
    static IEnumerable<McpServerConfig> From(JsonObject? servers)
    {
        if (servers == null) yield break;
        foreach (var (name, node) in servers)
        {
            if (node is not JsonObject o) continue;
            var c = new McpServerConfig { Name = name, Command = Text(o["command"]), Url = Text(o["url"]) };
            if (o["args"] is JsonArray args) c.Args = args.Select(Text).ToList();
            if (o["env"] is JsonObject env) foreach (var (k, v) in env) c.Env[k] = Text(v);
            if (o["headers"] is JsonObject headers) foreach (var (k, v) in headers) c.Headers[k] = Text(v);
            if (c.Command.Length > 0 || c.Url.Length > 0) yield return c;
        }
    }

    /// <summary>
    /// Servers already set up for other programs on this PC, with where each was found: the Claude Code
    /// settings (for every folder and per project), the Claude desktop app, and a .mcp.json in the home folder.
    /// </summary>
    public static List<(McpServerConfig server, string from)> FoundElsewhere(string? home = null, string? appData = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<(McpServerConfig, string)>();
        JsonObject? Read(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonNode.Parse(File.ReadAllText(path), null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                    : null;
            }
            catch { return null; }
        }
        var code = Read(System.IO.Path.Combine(home, ".claude.json"));
        foreach (var c in From(code?["mcpServers"] as JsonObject)) list.Add((c, "Claude Code"));
        if (code?["projects"] is JsonObject projects)
            foreach (var (folder, p) in projects)
                foreach (var c in From(p?["mcpServers"] as JsonObject)) list.Add((c, "Claude Code, project " + System.IO.Path.GetFileName(folder.TrimEnd('/', '\\'))));
        foreach (var c in From(Read(System.IO.Path.Combine(appData, "Claude", "claude_desktop_config.json"))?["mcpServers"] as JsonObject)) list.Add((c, "Claude desktop app"));
        foreach (var c in From(Read(System.IO.Path.Combine(home, ".mcp.json"))?["mcpServers"] as JsonObject)) list.Add((c, ".mcp.json"));
        // The same server listed in two places is offered once.
        return list.GroupBy(x => x.Item1.Name + "|" + x.Item1.Summary, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Writes the servers back to mcp.json, keeping whatever else the file holds.</summary>
    public static void Save(string path, IEnumerable<McpServerConfig> servers)
    {
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch { root = new JsonObject(); }
        var all = new JsonObject();
        foreach (var c in servers)
        {
            var o = (JsonObject?)c.Raw?.DeepClone() ?? new JsonObject();
            foreach (var key in new[] { "command", "args", "env", "url", "headers", "disabled" }) o.Remove(key);
            if (c.IsHttp)
            {
                o["url"] = c.Url;
                if (c.Headers.Count > 0) o["headers"] = new JsonObject(c.Headers.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            }
            else
            {
                o["command"] = c.Command;
                if (c.Args.Count > 0) o["args"] = new JsonArray(c.Args.Select(a => (JsonNode?)a).ToArray());
                if (c.Env.Count > 0) o["env"] = new JsonObject(c.Env.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            }
            if (c.Disabled) o["disabled"] = true;
            all[c.Name] = o;
        }
        root.Remove("servers");
        root["mcpServers"] = all;
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }
}

/// <summary>
/// A live link to one MCP server (JSON-RPC 2.0): over the program's input and output, one message per line,
/// or over HTTP, where each message is a POST answered by JSON or by an event stream.
/// </summary>
sealed class McpConnection : IDisposable
{
    public const string Protocol = "2025-06-18";
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);   // a first "npx" run downloads the server
    static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(3);
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public McpServerConfig Config { get; }
    public bool Ready { get; private set; }
    public bool Starting { get; private set; }
    public string? Error { get; private set; }
    public string ServerName { get; private set; } = "";
    /// <summary>The protocol version agreed with the server, and what it says it offers (tools, resources, prompts...).</summary>
    public string ProtocolVersion { get; private set; } = "";
    public List<string> Capabilities { get; } = new();
    public List<(string name, string description, JsonNode? schema)> Tools { get; } = new();

    Process? _proc;
    StreamWriter? _stdin;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> _waiting = new();
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly Queue<string> _lastErrors = new();
    long _nextId;
    string? _session;
    string _protocol = Protocol;
    bool _initialized;

    public McpConnection(McpServerConfig config) { Config = config; }

    public async Task Start()
    {
        Starting = true;
        try
        {
            if (!Config.IsHttp)
            {
                if (Config.Command.Length == 0) throw new InvalidOperationException("no \"command\" or \"url\" is given");
                StartProcess();
            }
            var init = await Request("initialize", new JsonObject
            {
                ["protocolVersion"] = Protocol,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Lplus", ["version"] = typeof(McpConnection).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
            }, StartTimeout);
            _protocol = init["protocolVersion"]?.GetValue<string>() ?? Protocol;
            ProtocolVersion = _protocol;
            if (init["capabilities"] is JsonObject offers)
                foreach (var (key, _) in offers)
                    if (key is "tools" or "resources" or "prompts" or "logging" or "completions") Capabilities.Add(key);
            ServerName = init["serverInfo"]?["name"]?.GetValue<string>() ?? Config.Name;
            _initialized = true;
            await Notify("notifications/initialized");

            string? cursor = null;
            do
            {
                var page = await Request("tools/list", cursor == null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, StartTimeout);
                foreach (var t in page["tools"]?.AsArray() ?? new JsonArray())
                {
                    var name = t?["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name)) continue;
                    Tools.Add((name, t?["description"]?.GetValue<string>() ?? "", t?["inputSchema"]?.DeepClone()));
                }
                cursor = page["nextCursor"]?.GetValue<string>();
            } while (!string.IsNullOrEmpty(cursor) && Tools.Count < 500);
            Ready = true;
        }
        catch (Exception ex)
        {
            Error = Short(ex is TimeoutException ? "it did not answer in time" : ex.Message);
            string tail;
            lock (_lastErrors) tail = string.Join(" ", _lastErrors);
            if (tail.Length > 0 && !Config.IsHttp) Error += " — " + Short(tail);
            Stop();
        }
        finally { Starting = false; }
    }

    static string Short(string s) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length > 240 ? s[..240] + "…" : s; }

    // ---------- a program spoken to over its input and output ----------

    void StartProcess()
    {
        var exe = FindOnPath(Config.Command) ?? Config.Command;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // Script launchers (npx.cmd, uvx.bat...) cannot be started directly; they go through the command shell.
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".exe" or ".com")
        {
            psi.FileName = exe;
            foreach (var a in Config.Args) psi.ArgumentList.Add(a);
        }
        else
        {
            // The whole line goes inside one pair of quotes, which "/s" makes the shell take off again; that
            // way a launcher or an argument with spaces in it survives.
            static string Q(string a) => a.Length > 0 && !a.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"" + string.Join(" ", new[] { exe }.Concat(Config.Args).Select(Q)) + "\"";
        }
        foreach (var (k, v) in Config.Env) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_lastErrors) { _lastErrors.Enqueue(e.Data.Trim()); while (_lastErrors.Count > 4) _lastErrors.Dequeue(); }
        };
        p.Exited += (_, _) => FailAll("the server program stopped");
        p.Start();
        p.BeginErrorReadLine();
        _proc = p;
        _stdin = p.StandardInput;
        _stdin.NewLine = "\n";
        _ = Task.Run(() => ReadLoop(p));
    }

    /// <summary>The full path of a command found on PATH (trying the usual program extensions), or null.</summary>
    static string? FindOnPath(string command)
    {
        if (command.Contains('\\') || command.Contains('/')) return File.Exists(command) ? command : null;
        var exts = Path.HasExtension(command) ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try
                {
                    var full = Path.Combine(dir.Trim().Trim('"'), command + ext);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        return null;
    }

    async Task ReadLoop(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;   // anything that is not a message is ignored
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; }
                if (msg != null) await Incoming(msg);
            }
        }
        catch { }
        FailAll("the server program stopped");
    }

    /// <summary>A message from the server: the answer to one of our requests, or a request or notice of its own.</summary>
    async Task Incoming(JsonNode msg)
    {
        var idNode = msg["id"];
        if (msg["method"] is JsonValue method)
        {
            if (idNode == null) return;   // a notice: nothing to answer
            // The server asks us something. "ping" is answered; nothing else is offered.
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone() };
            if (method.GetValue<string>() == "ping") reply["result"] = new JsonObject();
            else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported" };
            try { await Send(reply); } catch { }
            return;
        }
        if (idNode is JsonValue v && v.TryGetValue<long>(out var id) && _waiting.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
    }

    void FailAll(string why)
    {
        foreach (var id in _waiting.Keys.ToList())
            if (_waiting.TryRemove(id, out var tcs)) tcs.TrySetException(new InvalidOperationException(why));
        if (Ready) { Ready = false; Error = why; }
    }

    async Task Send(JsonNode msg)
    {
        if (_stdin == null) throw new InvalidOperationException("the server is not running");
        await _writing.WaitAsync();
        try
        {
            await _stdin.WriteLineAsync(msg.ToJsonString());
            await _stdin.FlushAsync();
        }
        finally { _writing.Release(); }
    }

    // ---------- requests ----------

    async Task<JsonNode> Request(string method, JsonNode prms, TimeSpan timeout)
    {
        long id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms };
        JsonNode answer;
        if (Config.IsHttp) answer = await Post(msg, id, timeout) ?? throw new InvalidOperationException("the server sent no answer");
        else
        {
            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = tcs;
            try
            {
                await Send(msg);
                answer = await tcs.Task.WaitAsync(timeout);
            }
            finally { _waiting.TryRemove(id, out _); }
        }
        if (answer["error"] is JsonObject err)
            throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "the server reported an error");
        return answer["result"] ?? new JsonObject();
    }

    async Task Notify(string method)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (Config.IsHttp) await Post(msg, null, TimeSpan.FromSeconds(30));
        else await Send(msg);
    }

    /// <summary>One message over HTTP. The answer comes back as JSON or as an event stream holding it.</summary>
    async Task<JsonNode?> Post(JsonNode msg, long? id, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, Config.Url)
        {
            Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (_session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        if (_initialized) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);
        foreach (var (k, v) in Config.Headers) req.Headers.TryAddWithoutValidation(k, v);
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault() ?? _session;
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"the server answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (id == null) return null;   // a notice: nothing comes back
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("event-stream"))
                return JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));

            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("data:")) { data.Append(line.AsSpan(5).TrimStart()); data.Append('\n'); continue; }
                if (line.Length != 0 || data.Length == 0) continue;
                JsonNode? ev = null;
                try { ev = JsonNode.Parse(data.ToString()); } catch { }
                data.Clear();
                if (ev?["id"] is JsonValue v && v.TryGetValue<long>(out var got) && got == id && ev["method"] == null) return ev;
            }
            return null;
        }
        catch (OperationCanceledException) { throw new TimeoutException(); }
    }

    /// <summary>Runs one of the server's tools and returns its answer as text for the model.</summary>
    public async Task<string> Call(string tool, JsonNode? args)
    {
        if (!Ready) return "Error: the server is not running" + (Error != null ? " (" + Error + ")." : ".");
        try
        {
            var result = await Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args is JsonObject ? args.DeepClone() : new JsonObject() }, CallTimeout);
            var sb = new StringBuilder();
            foreach (var part in result["content"]?.AsArray() ?? new JsonArray())
            {
                var type = part?["type"]?.GetValue<string>();
                if (type == "text") sb.AppendLine(part?["text"]?.GetValue<string>());
                else if (type == "resource") sb.AppendLine(part?["resource"]?["text"]?.GetValue<string>() ?? $"[{part?["resource"]?["uri"]}]");
                else if (type != null) sb.AppendLine($"[{type} content left out]");
            }
            if (sb.Length == 0 && result["structuredContent"] is JsonNode structured) sb.Append(structured.ToJsonString());
            var text = sb.ToString().Trim();
            if (text.Length > 12_000) text = text[..12_000] + $"\n… (cut off: {text.Length} characters in all)";
            bool failed = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError;
            return failed ? "Error: " + (text.Length > 0 ? text : "the tool failed.") : text.Length > 0 ? text : "(done, nothing returned)";
        }
        catch (TimeoutException) { return "Error: the tool did not answer in time."; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    void Stop()
    {
        Ready = false;
        var p = _proc;
        _proc = null;
        _stdin = null;
        if (p == null) return;
        try { p.StandardInput.Close(); } catch { }
        try { if (!p.WaitForExit(500)) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}

/// <summary>All the MCP servers from mcp.json, the ones that are connected, and the tools they offer.</summary>
sealed class McpHub : IDisposable
{
    /// <summary>Every server in the file, switched on or not.</summary>
    public List<McpServerConfig> Configs { get; private set; } = new();
    /// <summary>The servers that were started (working or not).</summary>
    public List<McpConnection> Servers { get; private set; } = new();
    public List<McpTool> Tools { get; private set; } = new();
    public string? ConfigError { get; private set; }
    public bool Loading { get; private set; }
    public string? Path { get; private set; }
    /// <summary>Raised (on whatever thread finished the work) when a server or the tool list changed.</summary>
    public event Action? Changed;

    /// <summary>Stops what is running, reads the file again and starts every server that is switched on (all at once).</summary>
    public async Task Load(string path)
    {
        Loading = true;
        Path = path;
        try
        {
            foreach (var s in Servers) s.Dispose();
            Configs = McpConfig.Load(path, out var error);
            ConfigError = error;
            var servers = Configs.Where(c => !c.Disabled).Select(c => new McpConnection(c)).ToList();
            Servers = servers;
            Tools = new();
            Changed?.Invoke();
            await Task.WhenAll(servers.Select(s => s.Start()));
            Rebuild();
        }
        finally { Loading = false; }
        Changed?.Invoke();
    }

    /// <summary>The connection for a server of this name, if it was started.</summary>
    public McpConnection? Connection(string name) => Servers.FirstOrDefault(s => s.Config.Name == name);

    /// <summary>Starts one server (again), replacing its old connection.</summary>
    public async Task Connect(McpServerConfig config)
    {
        Disconnect(config.Name);
        var c = new McpConnection(config);
        Servers = Servers.Append(c).ToList();
        Changed?.Invoke();
        await c.Start();
        Rebuild();
        Changed?.Invoke();
    }

    public void Disconnect(string name)
    {
        var old = Connection(name);
        if (old == null) return;
        old.Dispose();
        Servers = Servers.Where(s => s != old).ToList();
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>Writes Configs to the file.</summary>
    public void Save() { if (Path != null) McpConfig.Save(Path, Configs); }

    void Rebuild()
    {
        var tools = new List<McpTool>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // In the file's order, whatever order the servers were connected in.
        foreach (var s in Servers.Where(s => s.Ready).OrderBy(s => Configs.FindIndex(c => c.Name == s.Config.Name)))
            foreach (var (name, description, schema) in s.Tools)
            {
                // The model sees one flat list, so each name says which server it belongs to.
                var exposed = Regex.Replace($"{s.Config.Name}_{name}", "[^A-Za-z0-9_-]", "_");
                if (exposed.Length > 60) exposed = exposed[..60];
                for (int n = 2; !used.Add(exposed); n++) exposed = exposed.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') + n;
                tools.Add(new McpTool(s, name, exposed, description, schema));
            }
        Tools = tools;
    }

    public McpTool? Find(string exposed) => Tools.FirstOrDefault(t => t.Exposed == exposed);

    /// <summary>The servers' tools in the form /api/chat takes.</summary>
    public JsonArray Definitions()
    {
        var list = new JsonArray();
        foreach (var t in Tools)
        {
            var schema = t.Schema is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
            schema["type"] = "object";
            if (schema["properties"] is not JsonObject) schema["properties"] = new JsonObject();
            var about = t.Description.Length > 800 ? t.Description[..800] : t.Description;
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Exposed, ["description"] = about, ["parameters"] = schema },
            });
        }
        return list;
    }

    /// <summary>How one server is doing: (what to say, whether it works, whether it is still starting).</summary>
    public (string status, bool ok, bool starting) StatusOf(McpServerConfig c)
    {
        if (c.Disabled) return ("switched off", false, false);
        var s = Connection(c.Name);
        if (s == null) return ("not connected", false, false);
        if (s.Starting) return ("connecting…", false, true);
        if (s.Ready) return ($"{s.Tools.Count} tool{(s.Tools.Count == 1 ? "" : "s")}", true, false);
        return ("not working: " + (s.Error ?? "unknown"), false, false);
    }

    /// <summary>One line per server that is switched on, for the Settings page: (name, what it is doing, whether it works).</summary>
    public List<(string name, string status, bool ok)> Status()
    {
        var list = new List<(string, string, bool)>();
        foreach (var c in Configs.Where(c => !c.Disabled))
        {
            var (status, ok, _) = StatusOf(c);
            list.Add((c.Name, status, ok));
        }
        return list;
    }

    public void Dispose() { foreach (var s in Servers) s.Dispose(); }
}

/// <summary>A server found in the public MCP registry, and how it would be set up here.</summary>
sealed class McpRegistryEntry
{
    public string Name = "", Description = "", Version = "", Repository = "";
    public string How = "";                    // "npm package x (runs with npx)", "web address", or why it cannot be added
    public McpServerConfig? Suggested;         // null when Lplus cannot run it
    public List<string> Notes = new();         // settings the server asks for, one line each
}

/// <summary>Looks servers up in the public MCP registry (registry.modelcontextprotocol.io).</summary>
static class McpRegistry
{
    public const string Address = "https://registry.modelcontextprotocol.io/v0/servers";
    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lplus/1.0");
        return http;
    }

    public static async Task<List<McpRegistryEntry>> Search(string query)
    {
        var json = await Http.GetStringAsync($"{Address}?search={Uri.EscapeDataString(query.Trim())}&version=latest&limit=30");
        return Parse(json);
    }

    /// <summary>The registry's answer as entries, one per server name.</summary>
    public static List<McpRegistryEntry> Parse(string json)
    {
        var list = new List<McpRegistryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in JsonNode.Parse(json)?["servers"]?.AsArray() ?? new JsonArray())
        {
            var sv = item?["server"] ?? item;
            var name = T(sv?["name"]);
            if (name.Length == 0 || !seen.Add(name)) continue;
            var e = new McpRegistryEntry
            {
                Name = name, Description = T(sv?["description"]), Version = T(sv?["version"]), Repository = T(sv?["repository"]?["url"]),
            };
            // The short name used in mcp.json: the last part of "publisher/server".
            var shortName = Regex.Replace(name[(name.LastIndexOf('/') + 1)..], "[^A-Za-z0-9_-]", "-").Trim('-');
            if (shortName.Length == 0) shortName = "server";

            var remote = (sv?["remotes"] as JsonArray)?.FirstOrDefault(r => T(r?["type"]) is "streamable-http" or "http");
            var pkg = (sv?["packages"] as JsonArray)?.FirstOrDefault(p => T(p?["registryType"]) is "npm" or "pypi" or "oci" && T(p?["transport"]?["type"]) is "stdio" or "");
            if (pkg != null)
            {
                var type = T(pkg["registryType"]);
                var id = T(pkg["identifier"]);
                var c = new McpServerConfig { Name = shortName };
                (c.Command, e.How) = type switch
                {
                    "npm" => ("npx", $"Package {id} from npm (runs with npx; needs Node.js)"),
                    "pypi" => ("uvx", $"Python package {id} (runs with uvx; needs uv)"),
                    _ => ("docker", $"Container image {id} (runs with Docker)"),
                };
                if (type == "npm") c.Args.Add("-y");
                if (type == "oci") c.Args.AddRange(new[] { "run", "-i", "--rm" });
                foreach (var a in pkg["runtimeArguments"] as JsonArray ?? new JsonArray())
                    AddArg(c, a, e, skip: "-y");
                c.Args.Add(id);
                foreach (var a in pkg["packageArguments"] as JsonArray ?? new JsonArray()) AddArg(c, a, e, skip: null);
                foreach (var v in pkg["environmentVariables"] as JsonArray ?? new JsonArray())
                {
                    var key = T(v?["name"]);
                    if (key.Length == 0) continue;
                    bool required = v?["isRequired"] is JsonValue rq && rq.TryGetValue<bool>(out var b) && b;
                    if (required || T(v?["default"]).Length > 0) c.Env[key] = T(v?["default"]);
                    e.Notes.Add($"{key}{(required ? " (required)" : "")}: {T(v?["description"])}");
                }
                e.Suggested = c;
            }
            else if (remote != null)
            {
                var c = new McpServerConfig { Name = shortName, Url = T(remote["url"]) };
                foreach (var h in remote["headers"] as JsonArray ?? new JsonArray())
                {
                    var key = T(h?["name"]);
                    if (key.Length == 0) continue;
                    c.Headers[key] = T(h?["value"]);
                    e.Notes.Add($"{key} header: {T(h?["description"])}");
                }
                e.How = "Web address";
                e.Suggested = c;
            }
            else e.How = "Cannot be added from here (it is offered in a form Lplus does not run)";
            list.Add(e);
        }
        return list;
    }

    static void AddArg(McpServerConfig c, JsonNode? a, McpRegistryEntry e, string? skip)
    {
        var value = T(a?["value"]);
        if (value.Length == 0) value = T(a?["default"]);
        var name = T(a?["name"]);
        if (T(a?["type"]) == "named" && name.Length > 0) { if (name != skip && !c.Args.Contains(name)) c.Args.Add(name); if (value.Length > 0) c.Args.Add(value); }
        else if (value.Length > 0) { if (value != skip && !(skip != null && c.Args.Contains(value))) c.Args.Add(value); }
        else if (T(a?["description"]).Length > 0) e.Notes.Add("argument: " + T(a?["description"]));
    }

    static string T(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
}

/// <summary>
/// A tiny MCP server used to check the client: "Lplus.exe --mcp-test-server" speaks over its input and
/// output, and the self-test also serves the same answers over HTTP. Tools: add, echo, fail.
/// </summary>
static class McpTestServer
{
    public static int RunStdio()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        while (input.ReadLine() is { } line)
        {
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            if (msg != null && Handle(msg) is { } reply) output.WriteLine(reply.ToJsonString());
        }
        return 0;
    }

    /// <summary>The answer to one message, or null for a notice.</summary>
    public static JsonNode? Handle(JsonNode msg)
    {
        if (msg["id"] == null) return null;
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone() };
        var prms = msg["params"];
        switch (msg["method"]?.GetValue<string>())
        {
            case "initialize":
                reply["result"] = new JsonObject
                {
                    ["protocolVersion"] = McpConnection.Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "lplus-test", ["version"] = "1.0.0" },
                };
                break;
            case "ping": reply["result"] = new JsonObject(); break;
            case "tools/list":
                reply["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        Tool("add", "Add two numbers and return the sum.", ("a", "number", "First number"), ("b", "number", "Second number")),
                        Tool("echo", "Return the text that was sent.", ("text", "string", "Text to send back")),
                        Tool("fail", "Always fails. For testing."),
                    },
                };
                break;
            case "tools/call":
                var name = prms?["name"]?.GetValue<string>();
                var args = prms?["arguments"];
                string text; bool error = false;
                if (name == "add") text = (Number(args?["a"]) + Number(args?["b"])).ToString(System.Globalization.CultureInfo.InvariantCulture);
                else if (name == "echo") text = args?["text"]?.GetValue<string>() ?? "";
                else if (name == "fail") { text = "this tool always fails"; error = true; }
                else { reply["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool: " + name }; break; }
                reply["result"] = new JsonObject
                {
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                    ["isError"] = error,
                };
                break;
            default:
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
                break;
        }
        return reply;
    }

    static double Number(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        }
        return 0;
    }

    static JsonObject Tool(string name, string description, params (string name, string type, string desc)[] ps)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in ps) { props[p.name] = new JsonObject { ["type"] = p.type, ["description"] = p.desc }; required.Add(p.name); }
        return new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required },
        };
    }
}
