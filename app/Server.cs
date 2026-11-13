using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lplus;

record ModelInfo(string Name, long Size, string ParameterSize, string Family, bool Installed);

/// <summary>Talks to the local model server's HTTP API.</summary>
sealed class ServerClient
{
    public string BaseUrl { get; }
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ServerClient()
    {
        BaseUrl = ResolveBase();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lplus/1.0");
    }

    static string ResolveBase()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(host)) return "http://127.0.0.1:11434";
        host = host.Trim();
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;
        var u = new Uri(host);
        var h = u.Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : u.Host;
        var port = u.IsDefaultPort && !host.Contains(":" + u.Port) ? 11434 : u.Port;
        return $"{u.Scheme}://{h}:{port}";
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
        try { return (await GetJson("/api/version", timeout ?? TimeSpan.FromSeconds(2)))?["version"]?.GetValue<string>(); }
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

    /// <summary>Streams /api/chat. onDelta(contentDelta, thinkingDelta).</summary>
    public async Task Chat(string model, JsonArray messages, JsonNode? think,
        Action<string, string> onDelta, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = true };
        if (think != null) body["think"] = think;
        await Stream("/api/chat", body, line =>
        {
            var err = line["error"]?.GetValue<string>();
            if (err != null) throw new InvalidOperationException(err);
            var msg = line["message"];
            var c = msg?["content"]?.GetValue<string>() ?? "";
            var t = msg?["thinking"]?.GetValue<string>() ?? "";
            if (c.Length > 0 || t.Length > 0) onDelta(c, t);
        }, ct);
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
        _settings = s.Clone();
        if (!changed) return true;
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

