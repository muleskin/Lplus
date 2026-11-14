using System.Drawing.Drawing2D;

namespace Lplus;

/// <summary>The Settings page: Cloud, network, model location, tools, MCP servers and context length.</summary>
sealed class SettingsView : Control
{
    public static readonly int[] ContextOptions = { 4096, 8192, 16384, 32768, 65536, 131072, 262144 };
    static readonly string[] ContextLabels = { "4k", "8k", "16k", "32k", "64k", "128k", "256k" };

    Settings _s = new();
    public string ServerStatus = "Checking…";
    public string ServerDetail = "";
    public string? ApplyNote;
    int _scroll, _contentH;
    DateTime _savedAt = DateTime.MinValue;
    readonly System.Windows.Forms.Timer _toast = new() { Interval = 250 };
    readonly List<(Rectangle r, Action act)> _hits = new();
    Rectangle _sliderR;
    bool _dragging;

    public event Action<Settings>? Changed;
    public event Action? ViewLogs;
    public event Action? ManageMcp;
    public event Action? ChangeServer;
    /// <summary>One line per MCP server: its name, what it is doing, and whether it works.</summary>
    public List<(string name, string status, bool ok)> McpLines = new();
    public string? McpNote;

    public SettingsView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        _toast.Tick += (_, _) => { if ((DateTime.Now - _savedAt).TotalSeconds > 2) { _toast.Stop(); Invalidate(); } };
    }

    public void Load(Settings s) { _s = s.Clone(); Invalidate(); }

    /// <summary>Sets where the model server is (from the Model server form) and saves.</summary>
    public void SetServer(string address, string key) { _s.ServerAddress = address; _s.ServerKey = key; Commit(); }
    public (string address, string key) Server => (_s.ServerAddress, _s.ServerKey);
    public void ScrollTo(int pixels) { _scroll = Math.Max(0, pixels); Invalidate(); }

    void Commit()
    {
        _savedAt = DateTime.Now;
        _toast.Start();
        Changed?.Invoke(_s.Clone());
        Invalidate();
    }

    int ColW => Math.Min(Width - Theme.S(64), Theme.S(640));
    int ColX => (Width - ColW) / 2;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.Smooth(g);
        _hits.Clear();
        int x = ColX, w = ColW;
        int y = Theme.S(36) - _scroll;

        Theme.Draw(g, "Settings", Theme.Title, Theme.Text, x, y);
        y += Theme.S(52);

        // Server status card.
        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) =>
            {
                Row(gg, r, "Server", ServerStatus + (ServerDetail.Length > 0 ? " · " + ServerDetail : ""));
                var lr = Button(gg, "View logs", r.Right - Theme.S(16), r.Y + r.Height / 2);
                _hits.Add((lr, () => ViewLogs?.Invoke()));
            },
            (gg, r) =>
            {
                Row(gg, new Rectangle(r.X, r.Y, r.Width - Theme.S(20), r.Height), "Server location",
                    string.IsNullOrWhiteSpace(_s.ServerAddress) ? "This PC" : ServerClient.Normalize(_s.ServerAddress) ?? _s.ServerAddress);
                var cr = Button(gg, "Change", r.Right - Theme.S(16), r.Y + r.Height / 2);
                _hits.Add((cr, () => ChangeServer?.Invoke()));
            },
        }, Theme.S(72));
        if (ApplyNote != null)
        {
            var nr = new Rectangle(x, y - Theme.S(10), w, Theme.S(30));
            Theme.DrawEllipsis(g, ApplyNote, Theme.Small, Theme.Muted, nr);
            y += Theme.S(24);
        }

        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) => ToggleRow(gg, r, "Cloud", "Enable cloud models through the model server.", _s.CloudEnabled, v => _s.CloudEnabled = v),
        }, Theme.S(76));

        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) => ToggleRow(gg, r, "Expose the server to the network", "Allow other devices or services to access the model server.", _s.Expose, v => _s.Expose = v),
            (gg, r) =>
            {
                Row(gg, new Rectangle(r.X, r.Y, r.Width, Theme.S(60)), "Model location", "Location where models are stored.");
                var pr = new Rectangle(r.X + Theme.S(16), r.Y + Theme.S(62), r.Width - Theme.S(132), Theme.S(34));
                Theme.FillRound(gg, Theme.UserBubble, pr, Theme.S(8));
                var path = string.IsNullOrEmpty(_s.Models) ? DefaultModelsDir() : _s.Models;
                Theme.DrawEllipsis(gg, path, Theme.Small, string.IsNullOrEmpty(_s.Models) ? Theme.Muted : Theme.TextBody,
                    new Rectangle(pr.X + Theme.S(10), pr.Y, pr.Width - Theme.S(20), pr.Height));
                var br = Button(gg, "Browse", r.Right - Theme.S(16), pr.Y + pr.Height / 2);
                _hits.Add((br, Browse));
                if (!string.IsNullOrEmpty(_s.Models))
                {
                    var rs = "Use default";
                    var sz = Theme.Measure(rs, Theme.Small);
                    var rr = new Rectangle(pr.X, pr.Bottom + Theme.S(6), sz.Width, sz.Height);
                    Theme.Draw(gg, rs, Theme.Small, Theme.Blue, rr.X, rr.Y);
                    _hits.Add((rr, () => { _s.Models = ""; Commit(); }));
                }
            },
        }, Theme.S(76), Theme.S(128));

        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) => ToggleRow(gg, r, "Tools", "Let models read this PC's clock and, in Code mode, work in your project folder.", _s.ToolsEnabled, v => _s.ToolsEnabled = v),
            (gg, r) => ToggleRow(gg, r, "Web access", "Let models search the web and read pages to look things up.", _s.WebEnabled, v => _s.WebEnabled = v),
            (gg, r) => ToggleRow(gg, r, "Work without asking", "In Code mode, change files and run build and test commands without asking each time.", _s.AutoApprove, v => _s.AutoApprove = v),
            (gg, r) =>
            {
                Row(gg, new Rectangle(r.X, r.Y, r.Width, Theme.S(60)), "Project folder", "In Code mode, models can read this folder, and change files and run commands after you confirm.");
                var pr = new Rectangle(r.X + Theme.S(16), r.Y + Theme.S(62), r.Width - Theme.S(132), Theme.S(34));
                Theme.FillRound(gg, Theme.UserBubble, pr, Theme.S(8));
                bool none = string.IsNullOrEmpty(_s.ProjectFolder);
                Theme.DrawEllipsis(gg, none ? "Not set — models cannot see your files" : _s.ProjectFolder, Theme.Small, none ? Theme.Muted : Theme.TextBody,
                    new Rectangle(pr.X + Theme.S(10), pr.Y, pr.Width - Theme.S(20), pr.Height));
                var br = Button(gg, "Browse", r.Right - Theme.S(16), pr.Y + pr.Height / 2);
                _hits.Add((br, BrowseProject));
                if (!none)
                {
                    var rs = "Remove";
                    var sz = Theme.Measure(rs, Theme.Small);
                    var rr = new Rectangle(pr.X, pr.Bottom + Theme.S(6), sz.Width, sz.Height);
                    Theme.Draw(gg, rs, Theme.Small, Theme.Blue, rr.X, rr.Y);
                    _hits.Add((rr, () => { _s.ProjectFolder = ""; Commit(); }));
                }
            },
        }, Theme.S(76), Theme.S(76), Theme.S(76), Theme.S(128));

        // MCP servers: extra tools from the person's own servers.
        int mcpRows = Math.Max(1, McpLines.Count) + (McpNote != null ? 1 : 0);
        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) =>
            {
                Row(gg, new Rectangle(r.X, r.Y, r.Width - Theme.S(70), Theme.S(60)), "MCP servers", "Extra tools from your own servers, in both modes.");
var mg = Button(gg, "Manage", r.Right - Theme.S(16), r.Y + Theme.S(36));
                _hits.Add((mg, () => ManageMcp?.Invoke()));
                int lx = r.X + Theme.S(16), ly = r.Y + Theme.S(68), lw = r.Width - Theme.S(32), lh = Theme.S(22);
                if (McpNote != null)
                {
                    Theme.DrawEllipsis(gg, McpNote, Theme.Small, Theme.ErrorText, new Rectangle(lx, ly, lw, lh));
                    ly += lh;
                }
                if (McpLines.Count == 0)
                    Theme.DrawEllipsis(gg, "No servers switched on. Click Manage to add or find one.", Theme.Small, Theme.Muted, new Rectangle(lx, ly, lw, lh));
                foreach (var (name, status, ok) in McpLines)
                {
                    using (var dot = new SolidBrush(ok ? Theme.Green : Theme.BorderStrong)) gg.FillEllipse(dot, lx, ly + Theme.S(6), Theme.S(8), Theme.S(8));
                    var ns = Theme.Measure(name, Theme.SmallBold);
                    Theme.DrawEllipsis(gg, name, Theme.SmallBold, Theme.TextBody, new Rectangle(lx + Theme.S(16), ly, ns.Width + Theme.S(4), lh));
                    Theme.DrawEllipsis(gg, status, Theme.Small, Theme.Muted, new Rectangle(lx + Theme.S(24) + ns.Width, ly, lw - Theme.S(24) - ns.Width, lh));
                    ly += lh;
                }
            },
        }, Theme.S(78) + Theme.S(22) * mcpRows);

        y = Card(g, x, y, w, new Action<Graphics, Rectangle>[]
        {
            (gg, r) =>
            {
                Row(gg, new Rectangle(r.X, r.Y, r.Width, Theme.S(76)), "Context length",
                    "Context length determines how much of your conversation local LLMs can remember and use to generate responses.", wrapDesc: true);
                DrawSlider(gg, new Rectangle(r.X + Theme.S(24), r.Y + Theme.S(92), r.Width - Theme.S(48), Theme.S(40)));
            },
        }, Theme.S(150));

        _contentH = y + _scroll + Theme.S(24);

        // "Saved" toast.
        if ((DateTime.Now - _savedAt).TotalSeconds <= 2)
        {
            var t = "Saved";
            var ts = Theme.Measure(t, Theme.SmallBold);
            var tr = new Rectangle(Width - ts.Width - Theme.S(48), Height - Theme.S(56), ts.Width + Theme.S(24), Theme.S(30));
            Theme.FillRound(g, Theme.Green, tr, tr.Height / 2f);
            Theme.Draw(g, t, Theme.SmallBold, Color.White, tr.X + Theme.S(12), tr.Y + (tr.Height - ts.Height) / 2);
        }
    }

    static string DefaultModelsDir() =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    int Card(Graphics g, int x, int y, int w, Action<Graphics, Rectangle>[] rows, params int[] heights)
    {
        int total = 0;
        for (int i = 0; i < rows.Length; i++) total += heights[Math.Min(i, heights.Length - 1)];
        var card = new Rectangle(x, y, w, total);
        Theme.FillRound(g, Color.White, card, Theme.S(14));
        Theme.DrawRound(g, Theme.Border, card, Theme.S(14));
        int ry = y;
        for (int i = 0; i < rows.Length; i++)
        {
            int h = heights[Math.Min(i, heights.Length - 1)];
            if (i > 0) using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, x + Theme.S(16), ry, x + w - Theme.S(16), ry);
            rows[i](g, new Rectangle(x, ry, w, h));
            ry += h;
        }
        return y + total + Theme.S(20);
    }

    void Row(Graphics g, Rectangle r, string title, string desc, bool wrapDesc = false)
    {
        int tx = r.X + Theme.S(16);
        int tw = r.Width - Theme.S(110);
        var ts = Theme.Measure(title, Theme.BodyBold);
        int ty = r.Y + Theme.S(16);
        Theme.Draw(g, title, Theme.BodyBold, Theme.Text, tx, ty);
        var dr = new Rectangle(tx, ty + ts.Height + Theme.S(4), wrapDesc ? r.Width - Theme.S(32) : tw, wrapDesc ? Theme.S(40) : Theme.Small.Height + 4);
        TextRenderer.DrawText(g, desc, Theme.Small, dr, Theme.Muted,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | (wrapDesc ? TextFormatFlags.WordBreak : TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine));
    }

    void ToggleRow(Graphics g, Rectangle r, string title, string desc, bool value, Action<bool> set)
    {
        Row(g, r, title, desc);
        int tw = Theme.S(40), th = Theme.S(22);
        var tr = new Rectangle(r.Right - Theme.S(16) - tw, r.Y + (r.Height - th) / 2, tw, th);
        Theme.FillRound(g, value ? Theme.Accent : Color.FromArgb(212, 212, 212), tr, th / 2f);
        int k = th - Theme.S(4);
        var knob = new Rectangle(value ? tr.Right - k - Theme.S(2) : tr.X + Theme.S(2), tr.Y + Theme.S(2), k, k);
        g.FillEllipse(Brushes.White, knob);
        _hits.Add((r, () => { set(!value); Commit(); }));
    }

    Rectangle Button(Graphics g, string text, int right, int midY)
    {
        var ts = Theme.Measure(text, Theme.Small);
        var br = new Rectangle(right - ts.Width - Theme.S(24), midY - Theme.S(16), ts.Width + Theme.S(24), Theme.S(32));
        Theme.FillRound(g, Color.White, br, Theme.S(8));
        Theme.DrawRound(g, Theme.BorderStrong, br, Theme.S(8));
        Theme.Draw(g, text, Theme.Small, Theme.Text, br.X + Theme.S(12), br.Y + (br.Height - ts.Height) / 2);
        return br;
    }

    void DrawSlider(Graphics g, Rectangle r)
    {
        _sliderR = r;
        int n = ContextOptions.Length;
        int idx = Array.IndexOf(ContextOptions, _s.ContextLength);
        if (idx < 0 && _s.ContextLength > 0) idx = Array.FindLastIndex(ContextOptions, v => v <= _s.ContextLength) is var j and >= 0 ? j : 0;
        if (idx < 0)
        {
            // 0 = not set: the server picks its own default until a size is chosen.
            idx = 0;
            Theme.Draw(g, "Not set — the server uses its default. Pick a size to change it.", Theme.Tiny, Theme.Faint, r.X - Theme.S(8), r.Y - Theme.S(18));
        }
        int trackY = r.Y + Theme.S(8);
        float step = (float)r.Width / (n - 1);
        Theme.FillRound(g, Theme.Border, new RectangleF(r.X, trackY - Theme.S(2), r.Width, Theme.S(4)), Theme.S(2));
        Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, trackY - Theme.S(2), step * idx, Theme.S(4)), Theme.S(2));
        for (int i = 0; i < n; i++)
        {
            float cx = r.X + step * i;
            int d = i == idx ? Theme.S(16) : Theme.S(8);
            var c = i <= idx ? Theme.Accent : Theme.BorderStrong;
            if (i == idx)
            {
                g.FillEllipse(Brushes.White, cx - d / 2f, trackY - d / 2f, d, d);
                using var pen = new Pen(Theme.Accent, Theme.Sf(2.5f));
                g.DrawEllipse(pen, cx - d / 2f, trackY - d / 2f, d, d);
            }
            else using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2f, trackY - d / 2f, d, d);
            var ls = Theme.Measure(ContextLabels[i], Theme.Small);
            Theme.Draw(g, ContextLabels[i], Theme.Small, i == idx ? Theme.Text : Theme.Muted, (int)(cx - ls.Width / 2f), trackY + Theme.S(14));
        }
    }

    void SliderAt(int x)
    {
        int n = ContextOptions.Length;
        float step = (float)_sliderR.Width / (n - 1);
        int i = Math.Clamp((int)Math.Round((x - _sliderR.X) / step), 0, n - 1);
        if (_s.ContextLength != ContextOptions[i]) { _s.ContextLength = ContextOptions[i]; Invalidate(); }
    }

    void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "Location where models are stored", UseDescriptionForTitle = true };
        if (!string.IsNullOrEmpty(_s.Models) && Directory.Exists(_s.Models)) dlg.InitialDirectory = _s.Models;
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { _s.Models = dlg.SelectedPath; Commit(); }
    }

    void BrowseProject()
    {
        using var dlg = new FolderBrowserDialog { Description = "Project folder that models may work in", UseDescriptionForTitle = true };
        if (!string.IsNullOrEmpty(_s.ProjectFolder) && Directory.Exists(_s.ProjectFolder)) dlg.InitialDirectory = _s.ProjectFolder;
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { _s.ProjectFolder = dlg.SelectedPath; Commit(); }
    }

    Rectangle SliderHit => new(_sliderR.X - Theme.S(12), _sliderR.Y - Theme.S(10), _sliderR.Width + Theme.S(24), _sliderR.Height + Theme.S(10));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && SliderHit.Contains(e.Location)) { _dragging = true; SliderAt(e.X); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Commit(); return; }
        if (e.Button != MouseButtons.Left) return;
        foreach (var (r, act) in _hits) if (r.Contains(e.Location)) { act(); return; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) { SliderAt(e.X); return; }
        Cursor = _hits.Any(h => h.r.Contains(e.Location)) || SliderHit.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - e.Delta / 2, 0, Math.Max(0, _contentH - Height));
        Invalidate();
    }
}
