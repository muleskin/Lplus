using System.Drawing.Drawing2D;

namespace Lplus;

/// <summary>Left column: New chat, chats grouped Today / This week / Older, and Settings at the bottom.</summary>
sealed class Sidebar : Control
{
    sealed class Row
    {
        public Rectangle R;
        public string Kind = "";   // "new", "header", "chat", "settings"
        public string Text = "";
        public ChatInfo? Chat;
    }

    List<ChatInfo> _chats = new();
    readonly List<Row> _rows = new();
    readonly List<Row> _fixed = new();
    int _scroll;
    int _contentH;
    Row? _hover;
    TextBox? _rename;

    public string? SelectedChatId;
    public bool SettingsSelected;
    public bool McpSelected;

    public event Action? NewChat;
    public event Action<string>? OpenChat;
    public event Action? OpenSettings;
    public event Action? OpenMcp;
    public event Action<string, string>? RenameChat;
    public event Action<string>? DeleteChat;

    public Sidebar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sidebar;
        Width = Theme.S(260);
    }

    public void SetChats(List<ChatInfo> chats)
    {
        _chats = chats;
        Relayout();
    }

    /// <summary>Groups: updated today, within the last 7 days, or older.</summary>
    static (List<ChatInfo> today, List<ChatInfo> week, List<ChatInfo> older) Group(List<ChatInfo> chats)
    {
        var now = DateTime.Now;
        var t = new List<ChatInfo>(); var w = new List<ChatInfo>(); var o = new List<ChatInfo>();
        foreach (var c in chats)
        {
            if (c.Updated.Date == now.Date) t.Add(c);
            else if (c.Updated > now.Date.AddDays(-7)) w.Add(c);
            else o.Add(c);
        }
        return (t, w, o);
    }

    int RowH => Theme.S(36);
    int TopH => Theme.S(64);
    int BottomH => Theme.S(94);

    void Relayout()
    {
        _rows.Clear();
        _fixed.Clear();
        int pad = Theme.S(10);
        _fixed.Add(new Row { Kind = "new", Text = "New chat", R = new Rectangle(pad, Theme.S(16), Width - 2 * pad, RowH) });
        int y = 0;
        var (t, w, o) = Group(_chats);
        foreach (var (name, list) in new[] { ("Today", t), ("This week", w), ("Older", o) })
        {
            if (list.Count == 0) continue;
            _rows.Add(new Row { Kind = "header", Text = name, R = new Rectangle(pad, y, Width - 2 * pad, Theme.S(32)) });
            y += Theme.S(32);
            foreach (var c in list)
            {
                _rows.Add(new Row { Kind = "chat", Text = string.IsNullOrWhiteSpace(c.Title) ? "New chat" : c.Title, Chat = c, R = new Rectangle(pad, y, Width - 2 * pad, RowH) });
                y += RowH + Theme.S(2);
            }
            y += Theme.S(8);
        }
        _contentH = y;
        int by = Height - BottomH + Theme.S(10);
        _fixed.Add(new Row { Kind = "mcp", Text = "MCP servers", R = new Rectangle(pad, by, Width - 2 * pad, RowH) });
        by += RowH + Theme.S(2);
        _fixed.Add(new Row { Kind = "settings", Text = "Settings", R = new Rectangle(pad, by, Width - 2 * pad, RowH) });
        ClampScroll();
        Invalidate();
    }

    int ListTop => TopH;
    int ListH => Math.Max(0, Height - TopH - BottomH);

    void ClampScroll() => _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, _contentH - ListH)));

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.Smooth(g);
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);

        // Scrolling list, clipped between the fixed areas.
        var clip = new Rectangle(0, ListTop, Width - 1, ListH);
        g.SetClip(clip);
        foreach (var r in _rows)
        {
            var rr = new Rectangle(r.R.X, r.R.Y + ListTop - _scroll, r.R.Width, r.R.Height);
            if (rr.Bottom < ListTop || rr.Top > ListTop + ListH) continue;
            if (r.Kind == "header")
            {
                Theme.DrawEllipsis(g, r.Text, Theme.SmallBold, Theme.Faint, new Rectangle(rr.X + Theme.S(10), rr.Y + Theme.S(6), rr.Width, rr.Height - Theme.S(6)));
                continue;
            }
            bool sel = r.Chat?.Id == SelectedChatId && !SettingsSelected && !McpSelected;
            if (sel) Theme.FillRound(g, Theme.SidebarSelected, rr, Theme.S(8));
            else if (_hover == r) Theme.FillRound(g, Theme.SidebarHover, rr, Theme.S(8));
            Theme.DrawEllipsis(g, r.Text, Theme.Body, sel ? Theme.Text : Theme.TextBody, new Rectangle(rr.X + Theme.S(10), rr.Y, rr.Width - Theme.S(20), rr.Height));
        }
        g.ResetClip();
        if (_rows.Count == 0)
            Theme.DrawEllipsis(g, "No chats yet", Theme.Small, Theme.Faint, new Rectangle(Theme.S(20), ListTop + Theme.S(4), Width - Theme.S(40), Theme.S(28)));

        foreach (var r in _fixed)
        {
            bool sel = (r.Kind == "settings" && SettingsSelected) || (r.Kind == "mcp" && McpSelected);
            if (sel) Theme.FillRound(g, Theme.SidebarSelected, r.R, Theme.S(8));
            else if (_hover == r) Theme.FillRound(g, Theme.SidebarHover, r.R, Theme.S(8));
            int ix = r.R.X + Theme.S(12), iy = r.R.Y + r.R.Height / 2;
            if (r.Kind == "new") DrawCompose(g, ix, iy); else if (r.Kind == "mcp") DrawPlug(g, ix, iy); else DrawGear(g, ix, iy);
            var ts = Theme.Measure(r.Text, Theme.Body);
            Theme.Draw(g, r.Text, Theme.Body, Theme.Text, ix + Theme.S(26), iy - ts.Height / 2);
            if (r.Kind == "new")
            {
                var k = "Ctrl+N";
                var ks = Theme.Measure(k, Theme.Tiny);
                if (_hover == r) Theme.Draw(g, k, Theme.Tiny, Theme.Faint, r.R.Right - ks.Width - Theme.S(10), iy - ks.Height / 2);
            }
        }
        if (_scroll > 0)
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, ListTop, Width - 1, ListTop);
    }

    static void DrawCompose(Graphics g, int x, int y)
    {
        using var pen = new Pen(Theme.TextBody, Theme.Sf(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float k = Theme.Sf(7);
        g.DrawLines(pen, new[] { new PointF(x + k * 0.3f, y - k), new PointF(x - k, y - k), new PointF(x - k, y + k), new PointF(x + k, y + k), new PointF(x + k, y - k * 0.3f) });
        g.DrawLine(pen, x - k * 0.2f, y + k * 0.2f, x + k * 1.1f, y - k * 1.1f);
    }

    /// <summary>The MCP servers icon: a plug.</summary>
    static void DrawPlug(Graphics g, int x, int y)
    {
        using var pen = new Pen(Theme.TextBody, Theme.Sf(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float k = Theme.Sf(4.5f);
        // Two prongs, the body, and the cable.
        g.DrawLine(pen, x - k / 2, y - k * 1.7f, x - k / 2, y - k * 0.7f);
        g.DrawLine(pen, x + k / 2, y - k * 1.7f, x + k / 2, y - k * 0.7f);
        using (var body = Theme.Round(new RectangleF(x - k, y - k * 0.7f, 2 * k, k * 1.5f), Theme.Sf(2f))) g.DrawPath(pen, body);
        g.DrawLine(pen, x, y + k * 0.8f, x, y + k * 1.7f);
    }

    static void DrawGear(Graphics g, int x, int y)
    {
        using var pen = new Pen(Theme.TextBody, Theme.Sf(1.5f));
        float r = Theme.Sf(6), k = Theme.Sf(2.3f);
        g.DrawEllipse(pen, x - r, y - r, 2 * r, 2 * r);
        g.DrawEllipse(pen, x - k, y - k, 2 * k, 2 * k);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            g.DrawLine(pen, x + (float)Math.Cos(a) * r, y + (float)Math.Sin(a) * r, x + (float)Math.Cos(a) * (r + Theme.Sf(2.2f)), y + (float)Math.Sin(a) * (r + Theme.Sf(2.2f)));
        }
    }

    Row? HitTest(Point p)
    {
        foreach (var r in _fixed) if (r.R.Contains(p)) return r;
        if (p.Y < ListTop || p.Y > ListTop + ListH) return null;
        var lp = new Point(p.X, p.Y - ListTop + _scroll);
        return _rows.FirstOrDefault(r => r.Kind != "header" && r.R.Contains(lp));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        Cursor = h != null ? Cursors.Hand : Cursors.Default;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = null; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= e.Delta / 2;
        ClampScroll();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var r = HitTest(e.Location);
        if (r == null) return;
        if (e.Button == MouseButtons.Right && r.Chat != null)
        {
            var chat = r.Chat;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Rename", null, (_, _) => BeginRename(r));
            menu.Items.Add("Delete", null, (_, _) =>
            {
                if (MessageBox.Show(FindForm(), $"Delete \"{chat.Title}\"?", "Delete chat", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                    DeleteChat?.Invoke(chat.Id);
            });
            menu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        switch (r.Kind)
        {
            case "new": NewChat?.Invoke(); break;
            case "settings": OpenSettings?.Invoke(); break;
            case "mcp": OpenMcp?.Invoke(); break;
            case "chat": OpenChat?.Invoke(r.Chat!.Id); break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var r = HitTest(e.Location);
        if (r?.Chat != null && e.Button == MouseButtons.Left) BeginRename(r);
    }

    void BeginRename(Row r)
    {
        _rename?.Dispose();
        var chat = r.Chat!;
        var tb = new TextBox
        {
            Text = chat.Title, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(r.R.X + Theme.S(4), r.R.Y + ListTop - _scroll + Theme.S(5), r.R.Width - Theme.S(8), r.R.Height - Theme.S(10)),
        };
        _rename = tb;
        Controls.Add(tb);
        tb.Focus();
        tb.SelectAll();
        bool done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            var text = tb.Text.Trim();
            BeginInvoke(() => { tb.Dispose(); if (_rename == tb) _rename = null; });
            if (save && text.Length > 0 && text != chat.Title) RenameChat?.Invoke(chat.Id, text);
        }
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(false); }
        };
        tb.LostFocus += (_, _) => Finish(true);
    }
}
