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
        public bool Ui;        // a label or button of the app (Copy, Retry...), not part of the conversation's text
        public string? Join;   // how this joins the text before it when copied: null = by position, " " or "" = same line continued
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

    // Text selected with the mouse: from one place (message, fragment, character) to another.
    (ChatMessage m, int frag, int ch)? _selFrom, _selTo;
    bool _selecting;

    public event Action<ChatMessage>? EditRequested;
    public event Action<ChatMessage>? RetryRequested;
    public event Action<ChatMessage>? UndoRequested;

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
        _selFrom = _selTo = null;
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
        string? join = null;   // set when the next fragment only continues a wrapped line
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
                if (x > x0 && x + bareW > x0 + width) { y += lh; x = x0; join = " "; if (bare.Length == 0) continue; }
                if (bareW > width)
                {
                    // A very long word: break it by characters.
                    var part = "";
                    foreach (var ch in bare)
                    {
                        if (Theme.Measure(part + ch, font).Width > x0 + width - x && part.Length > 0)
                        {
                            Add(part); y += lh; x = x0; part = ""; join = "";
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
                        InlineCode = run.Style == Span.Code, Join = join,
                    });
                    join = null;
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
        var key = $"{W}|{m.Content.Length}|{m.Thinking.Length}|{thinkOpen}|{m.Streaming}|{m.DownloadStatus}|{m.DownloadDone}|{m.Error}|{m.Attachments.Count}|{m.Tools.Count}|{string.Join("", m.Tools.TakeLast(2))}|{m.BackupDir != null && Directory.Exists(m.BackupDir)}|{(_copied == m ? 1 : 0)}|{_copiedCode}";
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
            AddText(l, "Downloading model", Theme.BodyBold, Theme.Text, Theme.S(16), y + Theme.S(12), ui: true);
            var detail = m.DownloadTotal > 0
                ? $"{Bytes(m.DownloadDone)} / {Bytes(m.DownloadTotal)} ({Math.Floor(100.0 * m.DownloadDone / m.DownloadTotal)}%)"
                : m.DownloadStatus;
            AddText(l, detail, Theme.Small, Theme.Muted, Theme.S(16), y + Theme.S(36), ui: true);
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
            AddText(l, chevron, Theme.Body, Theme.Muted, Theme.S(2), y + (thinkOpen ? -Theme.S(4) : 0), ui: true);
            var hs = Theme.Measure(head, Theme.Body);
            AddText(l, head, Theme.Body, Theme.Muted, Theme.S(22), y + (lh - hs.Height) / 2, ui: true);
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

        // What the model did on this PC for this reply (clock, project files), one muted line each.
        if (m.Tools.Count > 0)
        {
            foreach (var step in m.Tools)
                y = Flow(l, new List<Run> { new("✓  " + step, Span.Normal) }, Theme.Small, Theme.Muted, Theme.S(2), W - Theme.S(2), y);
            y += Theme.S(8);
        }

        if (m.Content.Length > 0) y = LayoutMarkdown(l, m.Content, W, y);
        else if (m.Streaming && m.DownloadStatus == null && !(m.Thinking.Length > 0))
        {
            AddText(l, new string('●', 1) + new string('●', Math.Max(0, _dots % 3)), Theme.Small, Theme.Faint, 0, y + Theme.S(4), ui: true);
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
            AddText(l, retry, Theme.Small, Theme.Muted, 0, y, ui: true);
            l.Hits.Add(new Hit { R = new Rectangle(0, y - 2, rs.Width + 4, rs.Height + 4), Act = () => RetryRequested?.Invoke(m) });
            y += rs.Height + Theme.S(4);
        }

        if (!m.Streaming && m.Content.Length > 0)
        {
            y += Theme.S(6);
            var copy = _copied == m ? "Copied!" : "Copy";
            var cs = Theme.Measure(copy, Theme.Small);
            AddText(l, copy, Theme.Small, Theme.Faint, 0, y, ui: true);
            l.Hits.Add(new Hit
            {
                R = new Rectangle(-2, y - 2, cs.Width + 6, cs.Height + 4),
                Act = () => { CopyText(m.Content); _copied = m; _copiedAt = DateTime.Now; Refresh(m); }
            });
            int after = cs.Width + Theme.S(16);
            if (!string.IsNullOrEmpty(m.Model))
            {
                AddText(l, m.Model, Theme.Small, Theme.Faint, after, y, ui: true);
                after += Theme.Measure(m.Model, Theme.Small).Width + Theme.S(16);
            }
            if (m.BackupDir != null && Directory.Exists(m.BackupDir))
            {
                var undo = "Undo file changes";
                var us = Theme.Measure(undo, Theme.Small);
                AddText(l, undo, Theme.Small, Theme.Blue, after, y, ui: true);
                l.Hits.Add(new Hit { R = new Rectangle(after - 2, y - 2, us.Width + 6, us.Height + 4), Act = () => UndoRequested?.Invoke(m) });
            }
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
            bool cut = false;
            do
            {
                var part = line.Length > perLine ? line[..perLine] : line;
                line = line.Length > perLine ? line[perLine..] : "";
                if (part.Length > 0)
                {
                    var sz = Theme.Measure(part, Theme.Mono);
                    frags.Add(new Frag { X = pad, Y = ty, W = sz.Width, H = sz.Height, Text = part, Font = Theme.Mono, Color = Theme.Text, Join = cut ? "" : null });
                }
                cut = true;
                ty += lh;
            } while (line.Length > 0);
        }
        int h = ty - top + pad - Theme.S(4);
        l.Boxes.Add(new Box { R = new Rectangle(0, top, W, h), Fill = Theme.CodeBg, Radius = Theme.S(12) });
        if (isCode)
        {
            AddText(l, string.IsNullOrEmpty(b.Lang) ? "code" : b.Lang, Theme.Small, Theme.Muted, pad, top + Theme.S(8), ui: true);
            var code = b.Code;
            var label = _copiedCode == code ? "Copied!" : "Copy";
            var cs = Theme.Measure(label, Theme.Small);
            AddText(l, label, Theme.Small, Theme.Muted, W - pad - cs.Width, top + Theme.S(8), ui: true);
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

    static void AddText(Laid l, string text, Font f, Color c, int x, int y, bool ui = false)
    {
        var sz = Theme.Measure(text, f);
        l.Frags.Add(new Frag { X = x, Y = y, W = sz.Width, H = sz.Height, Text = text, Font = f, Color = c, Ui = ui });
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
            for (int fi = 0; fi < l.Frags.Count; fi++)
            {
                var f = l.Frags[fi];
                if (f.InlineCode)
                    Theme.FillRound(g, Theme.InlineCodeBg, new RectangleF(f.X + ox - 3, f.Y + baseY - 1, f.W + 6, f.H + 2), 4);
                if (SelectedPart(m, l, fi) is var (c0, c1))
                {
                    int x0 = c0 == 0 ? 0 : Theme.Measure(f.Text[..c0], f.Font).Width;
                    int x1 = c1 >= f.Text.Length ? f.W : Theme.Measure(f.Text[..c1], f.Font).Width;
                    // Reach back over the space before a word so a selected line reads as one band.
                    int lead = c0 == 0 && fi > 0 && Math.Abs(l.Frags[fi - 1].Y - f.Y) < f.H && SelectedPart(m, l, fi - 1) is var (_, p1) && p1 >= l.Frags[fi - 1].Text.Length
                        ? Math.Max(0, f.X - (l.Frags[fi - 1].X + l.Frags[fi - 1].W)) : 0;
                    using var sel = new SolidBrush(Theme.Selection);
                    g.FillRectangle(sel, f.X + ox + x0 - lead, f.Y + baseY - 1, x1 - x0 + lead, f.H + 2);
                }
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

    // ---------- selecting and copying text ----------

    /// <summary>The place in the text nearest to a point of the window; null when there is no text at all.</summary>
    (ChatMessage m, int frag, int ch)? PlaceAt(Point p)
    {
        if (_placed.Count == 0) return null;
        int cx = p.X - ColumnX, cy = p.Y - AutoScrollPosition.Y;
        // The message under the point, else the nearest one above or below.
        var (top, m, l) = _placed.FirstOrDefault(t => cy <= t.top + t.laid.Height, _placed[^1]);
        int best = -1;
        long bestD = long.MaxValue;
        for (int i = 0; i < l.Frags.Count; i++)
        {
            var f = l.Frags[i];
            if (f.Ui) continue;
            int fy = top + f.Y;
            long dy = cy < fy ? fy - cy : cy > fy + f.H ? cy - (fy + f.H) : 0;
            long dx = cx < f.X ? f.X - cx : cx > f.X + f.W ? cx - (f.X + f.W) : 0;
            long d = dy * 10000 + dx;   // the line matters first, then the distance along it
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return null;
        var hit = l.Frags[best];
        int ch = 0;
        if (cx >= hit.X + hit.W) ch = hit.Text.Length;
        else if (cx > hit.X)
            for (ch = 0; ch < hit.Text.Length; ch++)
            {
                int w0 = ch == 0 ? 0 : Theme.Measure(hit.Text[..ch], hit.Font).Width;
                int w1 = Theme.Measure(hit.Text[..(ch + 1)], hit.Font).Width;
                if (cx - hit.X < (w0 + w1) / 2) break;
            }
        return (m, best, ch);
    }

    /// <summary>The selection in reading order, or null when nothing is selected.</summary>
    ((int msg, int frag, int ch) a, (int msg, int frag, int ch) b)? Selection()
    {
        if (_selFrom is not { } f || _selTo is not { } t) return null;
        int fm = Messages.IndexOf(f.m), tm = Messages.IndexOf(t.m);
        if (fm < 0 || tm < 0) return null;
        var a = (fm, f.frag, f.ch);
        var b = (tm, t.frag, t.ch);
        if (a.CompareTo(b) == 0) return null;
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    public bool HasSelection => Selection() != null;

    /// <summary>The selected characters of one fragment (from, to), or null when none of it is selected.</summary>
    (int from, int to)? SelectedPart(ChatMessage m, Laid l, int fi)
    {
        if (Selection() is not var (a, b) || l.Frags[fi].Ui) return null;
        int mi = Messages.IndexOf(m);
        var here = (mi, fi);
        if (here.CompareTo((a.msg, a.frag)) < 0 || here.CompareTo((b.msg, b.frag)) > 0) return null;
        int len = l.Frags[fi].Text.Length;
        int from = here == (a.msg, a.frag) ? Math.Min(a.ch, len) : 0;
        int to = here == (b.msg, b.frag) ? Math.Min(b.ch, len) : len;
        return to > from ? (from, to) : null;
    }

    /// <summary>The selected text, with line breaks where the conversation has them (not where lines only wrap).</summary>
    public string SelectedText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (_, m, l) in _placed)
        {
            Frag? prev = null;
            bool any = false;
            for (int fi = 0; fi < l.Frags.Count; fi++)
            {
                var f = l.Frags[fi];
                if (f.Ui) continue;
                if (SelectedPart(m, l, fi) is var (from, to))
                {
                    if (!any && sb.Length > 0) sb.Append("\n\n");                 // next message
                    else if (any && prev != null)
                    {
                        if (f.Join != null) sb.Append(f.Join);                      // a wrapped line goes on
                        else if (f.Y >= prev.Y + prev.H * 0.6)                      // a new line; blank lines kept
                            sb.Append('\n', Math.Clamp((int)Math.Round((f.Y - prev.Y) / (double)Math.Max(1, LineHeight(f.Font) - (f.Font == Theme.Mono ? Theme.S(4) : 0))), 1, 3));
                        else if (f.X > prev.X + prev.W + 1) sb.Append(' ');
                    }
                    sb.Append(f.Text, from, to - from);
                    any = true;
                }
                prev = f;
            }
        }
        return sb.ToString();
    }

    public bool CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0) return false;
        CopyText(text);
        return true;
    }

    public void SelectAll()
    {
        var first = _placed.FirstOrDefault(p => p.laid.Frags.Any(f => !f.Ui));
        var last = _placed.LastOrDefault(p => p.laid.Frags.Any(f => !f.Ui));
        if (first.m == null || last.m == null) return;
        int lf = last.laid.Frags.FindLastIndex(f => !f.Ui);
        _selFrom = (first.m, first.laid.Frags.FindIndex(f => !f.Ui), 0);
        _selTo = (last.m, lf, last.laid.Frags[lf].Text.Length);
        Invalidate();
    }

    /// <summary>Everything a message holds, for "Copy message": thinking is left out, an error is included.</summary>
    static string WholeText(ChatMessage m) =>
        m.Error == null ? m.Content : m.Content.Length == 0 ? m.Error : m.Content + "\n\n" + m.Error;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || HitTest(e.Location).hit != null) return;
        var at = PlaceAt(e.Location);
        if (ModifierKeys == Keys.Shift && _selFrom != null) _selTo = at;
        else { _selFrom = at; _selTo = at; }
        _selecting = at != null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_selecting && e.Button == MouseButtons.Left)
        {
            // Dragging past the top or bottom edge scrolls the conversation along.
            if (e.Y < 0 || e.Y > ClientSize.Height)
                AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + (e.Y < 0 ? e.Y : e.Y - ClientSize.Height));
            _selTo = PlaceAt(e.Location) ?? _selTo;
            Invalidate();
            return;
        }
        var (m, hit) = HitTest(e.Location);
        Cursor = hit != null ? Cursors.Hand : m != null ? Cursors.IBeam : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _selecting = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // Double-click selects the word under the pointer.
        if (e.Button != MouseButtons.Left || HitTest(e.Location).hit != null || PlaceAt(e.Location) is not { } at) return;
        var laid = _placed.First(p => p.m == at.m).laid;
        var text = laid.Frags[at.frag].Text;
        int a = Math.Min(at.ch, Math.Max(0, text.Length - 1)), b = a;
        while (a > 0 && !char.IsWhiteSpace(text[a - 1])) a--;
        while (b < text.Length && !char.IsWhiteSpace(text[b])) b++;
        _selFrom = (at.m, at.frag, a);
        _selTo = (at.m, at.frag, b);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var (m, hit) = HitTest(e.Location);
        if (e.Button == MouseButtons.Left) { hit?.Act(); return; }
        if (e.Button == MouseButtons.Right && (m != null || HasSelection))
        {
            var menu = new ContextMenuStrip();
            if (HasSelection) menu.Items.Add("Copy selection", null, (_, _) => CopySelection());
            if (m != null) menu.Items.Add("Copy message", null, (_, _) => CopyText(WholeText(m)));
            if (m != null && m.Thinking.Length > 0) menu.Items.Add("Copy thinking", null, (_, _) => CopyText(m.Thinking));
            menu.Items.Add("Copy whole chat", null, (_, _) => CopyText(string.Join("\n\n",
                Messages.Where(x => WholeText(x).Length > 0).Select(x => (x.Role == "user" ? "You: " : "Reply: ") + WholeText(x)))));
            menu.Items.Add("Select all", null, (_, _) => SelectAll());
            if (m != null && m.Role == "user" && !Messages.Any(x => x.Streaming))
                menu.Items.Add("Edit", null, (_, _) => EditRequested?.Invoke(m));
            if (m != null && m.BackupDir != null && Directory.Exists(m.BackupDir) && !Messages.Any(x => x.Streaming))
                menu.Items.Add("Undo file changes", null, (_, _) => UndoRequested?.Invoke(m));
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
