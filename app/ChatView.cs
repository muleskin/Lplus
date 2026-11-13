namespace Lplus;

/// <summary>The scrolling conversation: user bubbles on the right, replies as formatted text on the left.</summary>
sealed class ChatView : ScrollableControl
{
    sealed class Frag
    {
        public int X, Y, W, H;
        public string Text = "";
        public Font Font = Theme.Body;
        public Color Color;
        public bool InlineCode;
    }

    sealed class Box
    {
        public Rectangle R;
        public Color Fill;
        public Color? Stroke;
        public int Radius;
        public float? Progress; // progress bar fill fraction
    }

    sealed class Hit
    {
        public Rectangle R;
        public Action Act = () => { };
    }

    sealed class Laid
    {
        public int Height;
        public List<Box> Boxes = new();
        public List<Frag> Frags = new();
        public List<Hit> Hits = new();
        public List<(Point a, Point b, Color c)> Lines = new();
        public string Key = "";
    }

    public List<ChatMessage> Messages { get; private set; } = new();
    readonly Dictionary<ChatMessage, Laid> _cache = new();
    readonly Dictionary<(Font, Span), Font> _fonts = new();
    readonly List<(int top, ChatMessage m, Laid laid)> _placed = new();
    ChatMessage? _copied;
    DateTime _copiedAt;
    string? _copiedCode;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 400 };
    int _dots;

    public event Action<ChatMessage>? EditRequested;
    public event Action<ChatMessage>? RetryRequested;

    public ChatView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        AutoScroll = true;
        _anim.Tick += (_, _) =>
        {
            if (Messages.Any(m => m.Streaming)) { _dots = (_dots + 1) % 4; Invalidate(); }
            if (_copied != null && (DateTime.Now - _copiedAt).TotalSeconds > 1.5) { _copied = null; _copiedCode = null; Invalidate(); }
        };
        _anim.Start();
    }

    public int ColumnWidth => Math.Max(Theme.S(200), Math.Min(ClientSize.Width - Theme.S(48), Theme.S(768)));
    int ColumnX => (ClientSize.Width - ColumnWidth) / 2;

    public void SetMessages(List<ChatMessage> msgs)
    {
        Messages = msgs;
        _cache.Clear();
        Relayout();
        ScrollToBottom();
    }

    public bool AtBottom => -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - Theme.S(40);

    public void ScrollToBottom()
    {
        AutoScrollPosition = new Point(0, Math.Max(0, AutoScrollMinSize.Height));
        Invalidate();
    }

    /// <summary>Call after a message changed (streaming); keeps the view pinned to the bottom if it was.</summary>
    public void Refresh(ChatMessage m)
    {
        bool stick = AtBottom;
        _cache.Remove(m);
        Relayout();
        if (stick) ScrollToBottom(); else Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool stick = AtBottom;
        Relayout();
        if (stick) ScrollToBottom();
    }

    void Relayout()
    {
        _placed.Clear();
        int y = Theme.S(28);
        foreach (var m in Messages)
        {
            var laid = LayoutMessage(m);
            _placed.Add((y, m, laid));
            y += laid.Height + Theme.S(m.Role == "user" ? 24 : 28);
        }
        y += Theme.S(16);
        AutoScrollMinSize = new Size(0, y);
        Invalidate();
    }

    Font F(Font baseFont, Span s)
    {
        if (s is Span.Normal or Span.Link) return baseFont;
        if (s == Span.Code) return Theme.Mono;
        if (_fonts.TryGetValue((baseFont, s), out var f)) return f;
        var style = s switch { Span.Bold => FontStyle.Bold, Span.Italic => FontStyle.Italic, _ => FontStyle.Bold | FontStyle.Italic };
        f = new Font(baseFont.FontFamily, baseFont.Size, baseFont.Style | style);
        _fonts[(baseFont, s)] = f;
        return f;
    }

    static int LineHeight(Font f) => (int)Math.Ceiling(f.GetHeight() * 1.5f);

    /// <summary>Word-wraps runs into fragments from (x0, y); returns the y after the last line.</summary>
    int Flow(Laid l, List<Run> runs, Font baseFont, Color color, int x0, int width, int y)
    {
        int lh = LineHeight(baseFont);
        int x = x0;
        bool any = false;
        foreach (var run in runs)
        {
            var font = F(baseFont, run.Style);
            var col = run.Style == Span.Link ? Theme.Blue : color;
            // Split into words that keep their trailing spaces.
            var words = System.Text.RegularExpressions.Regex.Matches(run.Text, @"\S+\s*|\s+");
            foreach (System.Text.RegularExpressions.Match w in words)
            {
                var word = w.Value;
                var bare = word.TrimEnd();
                int bareW = bare.Length == 0 ? 0 : Theme.Measure(bare, font).Width;
                int fullW = Theme.Measure(word.Replace(' ', ' '), font).Width;
                if (x > x0 && x + bareW > x0 + width) { y += lh; x = x0; if (bare.Length == 0) continue; }
                if (bareW > width)
                {
                    // A very long word: break it by characters.
                    var part = "";
                    foreach (var ch in bare)
                    {
                        if (Theme.Measure(part + ch, font).Width > x0 + width - x && part.Length > 0)
                        {
                            Add(part); y += lh; x = x0; part = "";
                        }
                        part += ch;
                    }
                    Add(part);
                    x += Theme.Measure(part, font).Width;
                    continue;
                }
                Add(bare);
                x += fullW;

                void Add(string t)
                {
                    if (t.Length == 0) return;
                    var sz = Theme.Measure(t, font);
                    l.Frags.Add(new Frag
                    {
                        X = x, Y = y + (lh - sz.Height) / 2, W = sz.Width, H = sz.Height, Text = t, Font = font, Color = col,
                        InlineCode = run.Style == Span.Code,
                    });
                    any = true;
                }
            }
        }
        return y + (any || runs.Count == 0 ? lh : lh);
    }

    Laid LayoutMessage(ChatMessage m)
    {
        int W = ColumnWidth;
        bool thinkOpen = m.ThinkingOpen || (m.Streaming && m.Content.Length == 0 && m.Thinking.Length > 0);
        var key = $"{W}|{m.Content.Length}|{m.Thinking.Length}|{thinkOpen}|{m.Streaming}|{m.DownloadStatus}|{m.DownloadDone}|{m.Error}|{m.Attachments.Count}|{(_copied == m ? 1 : 0)}|{_copiedCode}";
        if (_cache.TryGetValue(m, out var cached) && cached.Key == key) return cached;
        var l = new Laid { Key = key };
        int y = 0;

        if (m.Role == "user")
        {
            // Attachments as chips, right aligned above the bubble.
            if (m.Attachments.Count > 0)
            {
                int cx = W;
                foreach (var a in m.Attachments.AsEnumerable().Reverse())
                {
                    var label = (a.Kind == "image" ? "▣ " : "▤ ") + a.Name;
                    var sz = Theme.Measure(label, Theme.Small);
                    int cw = Math.Min(sz.Width + Theme.S(20), W / 2);
                    cx -= cw;
                    l.Boxes.Add(new Box { R = new Rectangle(cx, y, cw, Theme.S(30)), Fill = Color.White, Stroke = Theme.Border, Radius = Theme.S(10) });
                    l.Frags.Add(new Frag { X = cx + Theme.S(10), Y = y + (Theme.S(30) - sz.Height) / 2, W = cw - Theme.S(20), H = sz.Height, Text = label, Font = Theme.Small, Color = Theme.Muted });
                    cx -= Theme.S(6);
                }
                y += Theme.S(38);
            }
            int maxW = (int)(W * 0.75);
            int pad = Theme.S(16), padY = Theme.S(10);
            // Measure the bubble: lay out into a temp then shift right.
            var tmp = new Laid();
            int ty = padY;
            int widest = 0;
            foreach (var line in m.Content.Replace("\r\n", "\n").Split('\n'))
                ty = Flow(tmp, new List<Run> { new(line, Span.Normal) }, Theme.Body, Theme.Text, pad, maxW - pad * 2, ty);
            foreach (var f in tmp.Frags) widest = Math.Max(widest, f.X + f.W);
            int bw = Math.Min(maxW, Math.Max(widest + pad, Theme.S(40)));
            int bh = ty + padY;
            int bx = W - bw;
            l.Boxes.Add(new Box { R = new Rectangle(bx, y, bw, bh), Fill = Theme.UserBubble, Radius = Theme.S(18) });
            foreach (var f in tmp.Frags) { f.X += bx; f.Y += y; l.Frags.Add(f); }
            y += bh;
            l.Height = y;
            _cache[m] = l;
            return l;
        }

        // ---- assistant ----
        if (m.DownloadStatus != null)
        {
            int bh = Theme.S(76);
            l.Boxes.Add(new Box { R = new Rectangle(0, y, W, bh), Fill = Color.White, Stroke = Theme.Border, Radius = Theme.S(12) });
            AddText(l, "Downloading model", Theme.BodyBold, Theme.Text, Theme.S(16), y + Theme.S(12));
            var detail = m.DownloadTotal > 0
                ? $"{Bytes(m.DownloadDone)} / {Bytes(m.DownloadTotal)} ({Math.Floor(100.0 * m.DownloadDone / m.DownloadTotal)}%)"
                : m.DownloadStatus;
            AddText(l, detail, Theme.Small, Theme.Muted, Theme.S(16), y + Theme.S(36));
            float frac = m.DownloadTotal > 0 ? (float)m.DownloadDone / m.DownloadTotal : 0f;
            l.Boxes.Add(new Box { R = new Rectangle(Theme.S(16), y + bh - Theme.S(16), W - Theme.S(32), Theme.S(4)), Fill = Theme.Border, Radius = Theme.S(2), Progress = frac });
            y += bh + Theme.S(12);
        }

        if (m.Thinking.Length > 0)
        {
            string head;
            if (m.Streaming && m.Content.Length == 0) head = "Thinking...";
            else if (m.ThinkStart.HasValue && m.ThinkEnd.HasValue)
            {
                var secs = (m.ThinkEnd.Value - m.ThinkStart.Value).TotalSeconds;
                head = secs < 2 ? "Thought for a moment" : $"Thought for {secs:0.0} seconds";
            }
            else head = "Thinking...";
            var chevron = thinkOpen ? "⌄" : "›";
            int lh = LineHeight(Theme.Body);
            AddText(l, chevron, Theme.Body, Theme.Muted, Theme.S(2), y + (thinkOpen ? -Theme.S(4) : 0));
            var hs = Theme.Measure(head, Theme.Body);
            AddText(l, head, Theme.Body, Theme.Muted, Theme.S(22), y + (lh - hs.Height) / 2);
            l.Hits.Add(new Hit { R = new Rectangle(0, y, hs.Width + Theme.S(26), lh), Act = () => { m.ThinkingOpen = !thinkOpen; Refresh(m); } });
            y += lh;
            if (thinkOpen)
            {
                int top = y + Theme.S(4);
                int ty = top;
                foreach (var b in Markdown.Parse(m.Thinking))
                {
                    if (b.Kind == BlockKind.Code) { ty = Flow(l, new List<Run> { new(b.Code, Span.Normal) }, Theme.Small, Theme.Muted, Theme.S(22), W - Theme.S(22), ty); continue; }
                    if (b.GapBefore) ty += Theme.S(6);
                    ty = Flow(l, b.Runs, Theme.Small, Theme.Muted, Theme.S(22), W - Theme.S(22), ty);
                }
                l.Lines.Add((new Point(Theme.S(7), top), new Point(Theme.S(7), ty), Theme.Border));
                y = ty + Theme.S(10);
            }
            else y += Theme.S(8);
        }

        if (m.Content.Length > 0) y = LayoutMarkdown(l, m.Content, W, y);
        else if (m.Streaming && m.DownloadStatus == null && !(m.Thinking.Length > 0))
        {
            AddText(l, new string('●', 1) + new string('●', Math.Max(0, _dots % 3)), Theme.Small, Theme.Faint, 0, y + Theme.S(4));
            y += LineHeight(Theme.Body);
        }

        if (m.Error != null)
        {
            var tmp = new Laid();
            int ey = Flow(tmp, new List<Run> { new(m.Error, Span.Normal) }, Theme.Body, Theme.ErrorText, Theme.S(14), W - Theme.S(28), Theme.S(10));
            l.Boxes.Add(new Box { R = new Rectangle(0, y + Theme.S(4), W, ey + Theme.S(10)), Fill = Theme.ErrorBg, Radius = Theme.S(10) });
            foreach (var f in tmp.Frags) { f.Y += y + Theme.S(4); l.Frags.Add(f); }
            y += ey + Theme.S(18);
            var retry = "Retry";
            var rs = Theme.Measure(retry, Theme.Small);
            AddText(l, retry, Theme.Small, Theme.Muted, 0, y);
            l.Hits.Add(new Hit { R = new Rectangle(0, y - 2, rs.Width + 4, rs.Height + 4), Act = () => RetryRequested?.Invoke(m) });
            y += rs.Height + Theme.S(4);
        }

        if (!m.Streaming && m.Content.Length > 0)
        {
            y += Theme.S(6);
            var copy = _copied == m ? "Copied!" : "Copy";
            var cs = Theme.Measure(copy, Theme.Small);
            AddText(l, copy, Theme.Small, Theme.Faint, 0, y);
            l.Hits.Add(new Hit
            {
                R = new Rectangle(-2, y - 2, cs.Width + 6, cs.Height + 4),
                Act = () => { CopyText(m.Content); _copied = m; _copiedAt = DateTime.Now; Refresh(m); }
            });
            if (!string.IsNullOrEmpty(m.Model))
                AddText(l, m.Model, Theme.Small, Theme.Faint, cs.Width + Theme.S(16), y);
            y += cs.Height;
        }

        l.Height = y;
        _cache[m] = l;
        return l;
    }

    int LayoutMarkdown(Laid l, string text, int W, int y)
    {
        bool first = true;
        foreach (var b in Markdown.Parse(text))
        {
            if (!first) y += b.GapBefore || b.Kind is BlockKind.Heading or BlockKind.Code or BlockKind.Table ? Theme.S(10) : Theme.S(2);
            first = false;
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    var hf = b.Level == 1 ? Theme.H1 : b.Level == 2 ? Theme.H2 : Theme.H3;
                    y = Flow(l, b.Runs, hf, Theme.Text, 0, W, y);
                    break;
                case BlockKind.Bullet:
                    int indent = Theme.S(22) * (b.Level + 1);
                    var ms = Theme.Measure(b.Marker, Theme.Body);
                    AddText(l, b.Marker, Theme.Body, Theme.Muted, indent - ms.Width - Theme.S(7), y + (LineHeight(Theme.Body) - ms.Height) / 2);
                    y = Flow(l, b.Runs, Theme.Body, Theme.TextBody, indent, W - indent, y);
                    break;
                case BlockKind.Quote:
                    int qtop = y;
                    y = Flow(l, b.Runs, Theme.BodyItalic, Theme.Muted, Theme.S(16), W - Theme.S(16), y);
                    l.Lines.Add((new Point(Theme.S(3), qtop + 3), new Point(Theme.S(3), y - 3), Theme.BorderStrong));
                    break;
                case BlockKind.Rule:
                    y += Theme.S(8);
                    l.Lines.Add((new Point(0, y), new Point(W, y), Theme.Border));
                    y += Theme.S(8);
                    break;
                case BlockKind.Code:
                case BlockKind.Table:
                    y = LayoutCode(l, b, W, y);
                    break;
                default:
                    y = Flow(l, b.Runs, Theme.Body, Theme.TextBody, 0, W, y);
                    break;
            }
        }
        return y;
    }

    int LayoutCode(Laid l, Block b, int W, int y)
    {
        bool isCode = b.Kind == BlockKind.Code;
        int pad = Theme.S(14);
        int top = y;
        int headH = isCode ? Theme.S(30) : 0;
        int charW = Math.Max(1, Theme.Measure("MMMMMMMMMM", Theme.Mono).Width / 10);
        int perLine = Math.Max(10, (W - pad * 2) / charW);
        int lh = LineHeight(Theme.Mono) - Theme.S(4);
        int ty = top + headH + (isCode ? Theme.S(2) : pad);
        var frags = new List<Frag>();
        foreach (var raw in b.Code.Replace("\t", "    ").Split('\n'))
        {
            var line = raw;
            do
            {
                var part = line.Length > perLine ? line[..perLine] : line;
                line = line.Length > perLine ? line[perLine..] : "";
                if (part.Length > 0)
                {
                    var sz = Theme.Measure(part, Theme.Mono);
                    frags.Add(new Frag { X = pad, Y = ty, W = sz.Width, H = sz.Height, Text = part, Font = Theme.Mono, Color = Theme.Text });
                }
                ty += lh;
            } while (line.Length > 0);
        }
        int h = ty - top + pad - Theme.S(4);
        l.Boxes.Add(new Box { R = new Rectangle(0, top, W, h), Fill = Theme.CodeBg, Radius = Theme.S(12) });
        if (isCode)
        {
            AddText(l, string.IsNullOrEmpty(b.Lang) ? "code" : b.Lang, Theme.Small, Theme.Muted, pad, top + Theme.S(8));
            var code = b.Code;
            var label = _copiedCode == code ? "Copied!" : "Copy";
            var cs = Theme.Measure(label, Theme.Small);
            AddText(l, label, Theme.Small, Theme.Muted, W - pad - cs.Width, top + Theme.S(8));
            l.Hits.Add(new Hit
            {
                R = new Rectangle(W - pad - cs.Width - 4, top + Theme.S(6), cs.Width + 8, cs.Height + 4),
                Act = () =>
                {
                    CopyText(code); _copiedCode = code; _copiedAt = DateTime.Now;
                    _copied = null; _cache.Clear(); Relayout();
                }
            });
        }
        l.Frags.AddRange(frags);
        return top + h;
    }

    static void AddText(Laid l, string text, Font f, Color c, int x, int y)
    {
        var sz = Theme.Measure(text, f);
        l.Frags.Add(new Frag { X = x, Y = y, W = sz.Width, H = sz.Height, Text = text, Font = f, Color = c });
    }

    static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }

    static void CopyText(string s)
    {
        try { Clipboard.SetText(s); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.Smooth(g);
        int ox = ColumnX, oy = AutoScrollPosition.Y;
        foreach (var (top, m, l) in _placed)
        {
            int baseY = top + oy;
            if (baseY > ClientSize.Height || baseY + l.Height < 0) continue;
            foreach (var b in l.Boxes)
            {
                var r = new RectangleF(b.R.X + ox, b.R.Y + baseY, b.R.Width, b.R.Height);
                Theme.FillRound(g, b.Fill, r, b.Radius);
                if (b.Progress is float p && p > 0)
                    Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * Math.Min(1f, p)), r.Height), b.Radius);
                if (b.Stroke is Color sc) Theme.DrawRound(g, sc, r, b.Radius);
            }
            foreach (var (a, b2, c) in l.Lines)
            {
                using var pen = new Pen(c, Math.Max(1f, Theme.Sf(2f)));
                g.DrawLine(pen, a.X + ox, a.Y + baseY, b2.X + ox, b2.Y + baseY);
            }
            foreach (var f in l.Frags)
            {
                if (f.InlineCode)
                    Theme.FillRound(g, Theme.InlineCodeBg, new RectangleF(f.X + ox - 3, f.Y + baseY - 1, f.W + 6, f.H + 2), 4);
                Theme.Draw(g, f.Text, f.Font, f.Color, f.X + ox, f.Y + baseY);
            }
        }
    }

    (ChatMessage? m, Hit? hit) HitTest(Point p)
    {
        int cx = p.X - ColumnX, cy = p.Y - AutoScrollPosition.Y;
        foreach (var (top, m, l) in _placed)
        {
            if (cy < top || cy > top + l.Height) continue;
            foreach (var h in l.Hits)
                if (h.R.Contains(cx, cy - top)) return (m, h);
            return (m, null);
        }
        return (null, null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = HitTest(e.Location).hit != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var (m, hit) = HitTest(e.Location);
        if (e.Button == MouseButtons.Left) { hit?.Act(); return; }
        if (e.Button == MouseButtons.Right && m != null)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy", null, (_, _) => CopyText(m.Content));
            if (m.Role == "user" && !Messages.Any(x => x.Streaming))
                menu.Items.Add("Edit", null, (_, _) => EditRequested?.Invoke(m));
            menu.Show(this, e.Location);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }
}
