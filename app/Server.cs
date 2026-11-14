using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lplus;

record ModelInfo(string Name, long Size, string ParameterSize, string Family, bool Installed);

/// <summary>
/// Talks to the model server's HTTP API. The server is the one on this PC unless an address is set in
/// Settings (or in the OLLAMA_HOST environment variable): then it is another computer, such as a cloud pod.
/// </summary>
sealed class ServerClient
{
    public const string Local = "http://127.0.0.1:11434";
    public string BaseUrl { get; private set; }
    /// <summary>True when the server is on another computer: Lplus then neither starts nor stops it.</summary>
    public bool IsRemote { get; private set; }
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ServerClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lplus/1.0");
        BaseUrl = Local;
        SetServer("", "");
    }

    /// <summary>
    /// Points the client at a server. An empty address means the OLLAMA_HOST variable, and without that
    /// this PC. The key, when given, is sent with every request as "Authorization: Bearer key".
    /// </summary>
    public void SetServer(string? address, string? key)
    {
        var chosen = string.IsNullOrWhiteSpace(address) ? Environment.GetEnvironmentVariable("OLLAMA_HOST") : address;
        BaseUrl = Normalize(chosen) ?? Local;
        var host = new Uri(BaseUrl).Host;
        IsRemote = !(host is "127.0.0.1" or "localhost" or "::1" or "[::1]");
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(key) ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
    }

    /// <summary>
    /// A typed address as a full one, or null when it is not an address. "host" and "host:port" are taken
    /// as plain http with the usual port 11434; a name with dots and no port is a web address (https), which
    /// is what a cloud pod's proxy address looks like. An address with http:// or https:// is kept as it is.
    /// </summary>
    public static string? Normalize(string? address)
    {
        var a = (address ?? "").Trim().TrimEnd('/');
        if (a.Length == 0) return Local;
        try
        {
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var u = new Uri(a);
                var h = u.Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : u.Host;
                var path = u.AbsolutePath.TrimEnd('/');
                return $"{u.Scheme}://{h}{(u.IsDefaultPort ? "" : ":" + u.Port)}{path}";
            }
            var bare = new Uri("http://" + a);
            if (bare.Host.Length == 0) return null;
            var host = bare.Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : bare.Host;
            bool hasPort = System.Text.RegularExpressions.Regex.IsMatch(a, @":\d+$");
            bool isName = host.Contains('.') && !System.Net.IPAddress.TryParse(host, out _);
            if (!hasPort && isName) return "https://" + host;
            return $"http://{host}:{(hasPort ? bare.Port : 11434)}";
        }
        catch { return null; }
    }

    async Task<JsonNode?> GetJson(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await _http.GetAsync(BaseUrl + path, cts.Token);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
    }

    public async Task<string?> Version(TimeSpan? timeout = null)
    {
        try { return (await GetJson("/api/version", timeout ?? TimeSpan.FromSeconds(IsRemote ? 8 : 2)))?["version"]?.GetValue<string>(); }
        catch { return null; }
    }

    public async Task<List<ModelInfo>> Models()
    {
        var node = await GetJson("/api/tags", TimeSpan.FromSeconds(10));
        var list = new List<ModelInfo>();
        foreach (var m in node?["models"]?.AsArray() ?? new JsonArray())
        {
            if (m == null) continue;
            list.Add(new ModelInfo(
                m["name"]?.GetValue<string>() ?? m["model"]?.GetValue<string>() ?? "",
                m["size"]?.GetValue<long>() ?? 0,
                m["details"]?["parameter_size"]?.GetValue<string>() ?? "",
                m["details"]?["family"]?.GetValue<string>() ?? "",
                true));
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Model capabilities from /api/show: "completion", "vision", "thinking", "tools"...</summary>
    public async Task<HashSet<string>> Capabilities(string model)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.PostAsJsonAsync(BaseUrl + "/api/show", new { model }, cts.Token);
            if (!resp.IsSuccessStatusCode) return set;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            foreach (var c in node?["capabilities"]?.AsArray() ?? new JsonArray())
                if (c != null) set.Add(c.GetValue<string>());
        }
        catch { }
        return set;
    }

    /// <summary>
    /// Streams /api/chat. onDelta(contentDelta, thinkingDelta). Returns the tool calls the model asked for
    /// (empty when it just answered).
    /// </summary>
    public async Task<List<JsonNode>> Chat(string model, JsonArray messages, JsonNode? think,
        Action<string, string> onDelta, CancellationToken ct, JsonArray? tools = null, int contextLength = 0)
    {
        var calls = new List<JsonNode>();
        var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone(), ["stream"] = true };
        if (contextLength > 0) body["options"] = new JsonObject { ["num_ctx"] = contextLength };
        if (think != null) body["think"] = think.DeepClone();
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();
        await Stream("/api/chat", body, line =>
        {
            var err = line["error"]?.GetValue<string>();
            if (err != null) throw new InvalidOperationException(err);
            var msg = line["message"];
            var c = msg?["content"]?.GetValue<string>() ?? "";
            var t = msg?["thinking"]?.GetValue<string>() ?? "";
            if (c.Length > 0 || t.Length > 0) onDelta(c, t);
            if (msg?["tool_calls"] is JsonArray tc)
                foreach (var call in tc) if (call != null) calls.Add(call.DeepClone());
        }, ct);
        return calls;
    }

    /// <summary>Streams /api/pull (downloading a model). onProgress(status, completed, total).</summary>
    public Task Pull(string model, Action<string, long, long> onProgress, CancellationToken ct) =>
        Stream("/api/pull", new JsonObject { ["model"] = model, ["stream"] = true }, line =>
        {
            var err = line["error"]?.GetValue<string>();
            if (err != null) throw new InvalidOperationException(err);
            onProgress(line["status"]?.GetValue<string>() ?? "",
                line["completed"]?.GetValue<long>() ?? 0, line["total"]?.GetValue<long>() ?? 0);
        }, ct);

    async Task Stream(string path, JsonObject body, Action<JsonNode> onLine, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text; } catch { }
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? $"HTTP {(int)resp.StatusCode}" : msg);
        }
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(s);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node != null) onLine(node);
        }
    }
}

/// <summary>
/// Starts and watches the model server ("serve"):
/// the settings become the server's environment variables (see Environment_).
/// </summary>
sealed class ServerManager : IDisposable
{
    public const int RestartDelaySeconds = 5;
    public const int WaitSeconds = 10;

    readonly ServerClient _client;
    Process? _proc;
    Settings _settings;
    bool _stopping;
    public bool External { get; private set; }  // a server we didn't start was already answering
    public string? Error { get; private set; }
    public event Action? StateChanged;

    public ServerManager(ServerClient client, Settings settings) { _client = client; _settings = settings.Clone(); }

    public bool Owned => _proc is { HasExited: false };

    /// <summary>The server program this app starts, and the folder its installer uses.</summary>
    const string ServerExe = "ollama.exe";
    public const string ServerFolder = "Ollama";

    public static string? FindServerExe()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, ServerExe),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ServerFolder, ServerExe),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), ServerExe));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static Dictionary<string, string> Environment_(Settings s)
    {
        var env = new Dictionary<string, string>();
        if (s.Expose) env["OLLAMA_HOST"] = "0.0.0.0";
        if (s.Browser) env["OLLAMA_ORIGINS"] = "*";
        if (!string.IsNullOrWhiteSpace(s.Models))
        {
            if (Directory.Exists(s.Models)) env["OLLAMA_MODELS"] = s.Models;
            else Log.Warn($"models directory does not exist, ignoring: {s.Models}");
        }
        if (s.ContextLength > 0) env["OLLAMA_CONTEXT_LENGTH"] = s.ContextLength.ToString();
        env["OLLAMA_NO_CLOUD"] = s.CloudEnabled ? "0" : "1";
        return env;
    }

    public async Task Start()
    {
        Error = null;
        if (_client.IsRemote)
        {
            // A server on another computer: nothing is started here, it only has to answer.
            External = true;
            var v = await _client.Version(TimeSpan.FromSeconds(15));
            if (v == null) Error = $"The model server at {_client.BaseUrl} is not answering.";
            Log.Info(v == null ? "remote model server is not answering" : $"using the remote model server (version {v})");
            StateChanged?.Invoke();
            return;
        }
        if (await _client.Version() != null)
        {
            External = _proc == null;
            Log.Info(External ? "a model server is already running" : "model server is ready");
            StateChanged?.Invoke();
            return;
        }
        External = false;
        var exe = FindServerExe();
        if (exe == null)
        {
            Error = "The model server program was not found.";
            Log.Error(Error);
            StateChanged?.Invoke();
            return;
        }
        Launch(exe);
        var until = DateTime.Now.AddSeconds(WaitSeconds);
        while (DateTime.Now < until)
        {
            if (await _client.Version(TimeSpan.FromSeconds(1)) != null) { Log.Info("model server is ready"); break; }
            await Task.Delay(250);
        }
        if (await _client.Version() == null) Error = "The model server didn't start. See View logs.";
        StateChanged?.Invoke();
    }

    void Launch(string exe)
    {
        Paths.Rotate(Paths.ServerLog);
        var psi = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var (k, v) in Environment_(_settings)) psi.Environment[k] = v;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StreamWriter(new FileStream(Paths.ServerLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.Exited += async (_, _) =>
        {
            lock (log) log.Dispose();
            if (_stopping || !ReferenceEquals(_proc, p)) return;
            Log.Warn($"model server exited, restarting in {RestartDelaySeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(RestartDelaySeconds));
            if (!_stopping && ReferenceEquals(_proc, p)) { _proc = null; await Start(); }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        try { File.WriteAllText(Paths.PidFile, p.Id.ToString()); } catch { }
        Log.Info($"started model server pid={p.Id}");
    }

    /// <summary>Applies new settings. Returns false when the running server isn't ours to restart.</summary>
    public async Task<bool> Apply(Settings s)
    {
        var changed = _settings.ServerDiffers(s);
        bool moved = _settings.ServerAddress != s.ServerAddress || _settings.ServerKey != s.ServerKey;
        _settings = s.Clone();
        if (!changed) return true;
        if (moved)
        {
            // A different server: the one Lplus started here (if any) is stopped, and the new one is looked for.
            _client.SetServer(s.ServerAddress, s.ServerKey);
            StopProcess();
            _stopping = false;
            External = false;
            await Start();
            return true;
        }
        if (_client.IsRemote) return true;   // the other settings are for a server started here
        if (External) return false;
        StopProcess();
        _stopping = false;
        await Start();
        return true;
    }

    void StopProcess()
    {
        _stopping = true;
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch { }
        try { File.Delete(Paths.PidFile); } catch { }
        Log.Info("stopped model server");
    }

    public void Dispose() => StopProcess();
}

