using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lplus;

record LoadedModel(string Name, long Size, long SizeVram);
record ComputeDevice(string Library, string Description, string Total, string Available);

/// <summary>
/// What the GPU is doing: the devices the server found (the server log's "inference compute" lines)
/// and the models loaded right now with how much of each sits in GPU memory (/api/ps size vs size_vram).
/// </summary>
static class GpuStatus
{
    public static async Task<List<LoadedModel>?> Loaded(ServerClient client)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var node = JsonNode.Parse(await http.GetStringAsync(client.BaseUrl + "/api/ps"));
            var list = new List<LoadedModel>();
            foreach (var m in node?["models"]?.AsArray() ?? new JsonArray())
                if (m != null)
                    list.Add(new LoadedModel(m["name"]?.GetValue<string>() ?? "", m["size"]?.GetValue<long>() ?? 0, m["size_vram"]?.GetValue<long>() ?? 0));
            return list;
        }
        catch { return null; } // server not answering
    }

    static readonly Regex Field = new(@"(\w+)=(""[^""]*""|\S+)");

    /// <summary>Devices from the newest server log: Lplus's own, or the one the server's own installation writes.</summary>
    public static List<ComputeDevice> Devices()
    {
        var logs = new[]
        {
            Paths.ServerLog,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ServerManager.ServerFolder, "server.log"),
        }.Where(File.Exists).OrderByDescending(File.GetLastWriteTime);
        foreach (var log in logs)
        {
            var lines = Tail(log, 512 * 1024).Where(l => l.Contains("msg=\"inference compute\"")).ToList();
            if (lines.Count == 0) continue;
            // Keep the newest report for each device id.
            var byId = new Dictionary<string, ComputeDevice>();
            foreach (var line in lines)
            {
                var f = Field.Matches(line).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'), StringComparer.Ordinal);
                var id = f.GetValueOrDefault("id", "0");
                byId[id] = new ComputeDevice(f.GetValueOrDefault("library", ""), f.GetValueOrDefault("description", f.GetValueOrDefault("name", "")),
                    f.GetValueOrDefault("total", ""), f.GetValueOrDefault("available", ""));
            }
            return byId.Values.ToList();
        }
        return new();
    }

    static IEnumerable<string> Tail(string path, int bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n');
        }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>The bar along the bottom of the window: GPU use on the left, the detected graphics card on the right.</summary>
sealed class StatusBar : Control
{
    public enum State { NotRunning, Idle, Gpu, Split, Cpu, Checking }
    State _state = State.Checking;
    string _left = "Checking GPU…";
    string _right = "";
    float _gpuShare = -1; // 0..1 for the loaded model, -1 when no model is loaded

    public StatusBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sidebar;
        Height = Theme.S(28);
    }

    static string Gb(long b) => $"{b / 1_000_000_000.0:0.0} GB";

    /// <summary>Works out the bar's state and both texts (no drawing, so it can be tested on its own).</summary>
    public static (State state, string left, string right, float share) Describe(List<LoadedModel>? loaded, List<ComputeDevice> devices, string? remote = null)
    {
        var gpus = devices.Where(d => !string.Equals(d.Library, "cpu", StringComparison.OrdinalIgnoreCase)).ToList();
        // A server on another computer: its graphics card cannot be seen from here, so the server is named instead.
        var right = remote != null ? "Server: " + remote : gpus.Count > 0
            ? string.Join("  ·  ", gpus.Select(d => $"{d.Description} ({d.Library}{(d.Total.Length > 0 ? ", " + d.Total : "")})"))
            : devices.Count > 0 ? "No GPU found — models run on the CPU" : "Graphics card: not reported yet";
        if (loaded == null) return (State.NotRunning, "The model server isn't running", right, -1);
        if (loaded.Count == 0) return (State.Idle, gpus.Count > 0 ? "GPU ready · no model loaded" : "No model loaded", right, -1);
        long size = loaded.Sum(m => m.Size), vram = loaded.Sum(m => m.SizeVram);
        float share = size > 0 ? Math.Clamp((float)vram / size, 0, 1) : 0;
        int pct = (int)Math.Round(share * 100);
        var names = string.Join(", ", loaded.Select(m => m.Name));
        if (vram <= 0) return (State.Cpu, $"GPU not used · {names} on CPU", right, share);
        if (pct >= 100) return (State.Gpu, $"GPU in use · {names} · 100% GPU · {Gb(vram)} in GPU memory", right, share);
        return (State.Split, $"GPU partly used · {names} · {pct}% GPU / {100 - pct}% CPU", right, share);
    }

    public void Update(List<LoadedModel>? loaded, List<ComputeDevice> devices, string? remote = null)
    {
        (_state, _left, _right, _gpuShare) = Describe(loaded, devices, remote);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.Smooth(g);
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, 0, Width, 0);
        var dot = _state switch
        {
            State.Gpu => Theme.Green,
            State.Split => Color.FromArgb(245, 158, 11),
            State.Cpu => Color.FromArgb(239, 68, 68),
            State.NotRunning => Theme.Faint,
            _ => Color.FromArgb(163, 163, 163),
        };
        int d = Theme.S(8), cy = Height / 2;
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, Theme.S(12), cy - d / 2f, d, d);
        int x = Theme.S(28);

        // Small meter for the loaded model's GPU share.
        if (_gpuShare >= 0)
        {
            var mr = new Rectangle(x, cy - Theme.S(3), Theme.S(48), Theme.S(6));
            Theme.FillRound(g, Theme.Border, mr, Theme.S(3));
            if (_gpuShare > 0) Theme.FillRound(g, dot, new RectangleF(mr.X, mr.Y, Math.Max(mr.Height, mr.Width * _gpuShare), mr.Height), Theme.S(3));
            x = mr.Right + Theme.S(8);
        }
        var rs = Theme.Measure(_right, Theme.Small);
        int rightW = Math.Min(rs.Width, Width / 2);
        Theme.DrawEllipsis(g, _left, Theme.Small, Theme.TextBody, new Rectangle(x, 0, Width - x - rightW - Theme.S(28), Height));
        Theme.DrawEllipsis(g, _right, Theme.Small, Theme.Muted, new Rectangle(Width - rightW - Theme.S(12), 0, rightW, Height));
    }
}
