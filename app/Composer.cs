using System.Drawing.Drawing2D;

namespace Lplus;

/// <summary>The rounded message box: text, attached files, "+" button, Chat/Code switch, thinking button, model picker and send/stop.</summary>
sealed class Composer : Control
{
    readonly TextBox _box;
    readonly Label _placeholder;
    public readonly List<Attachment> Attachments = new();
    public string ModelName = "";
    public bool Streaming;
    /// <summary>"" = the model can't think, "toggle" = on/off, "level" = Low/Medium/High.</summary>
    public string ThinkMode = "";
    public bool ThinkOn = true;
    public string ThinkLevel = "medium";
    public bool Editing;
    /// <summary>Code mode: the model works in the project folder and "+" chooses that folder. Chat mode: "+" attaches files.</summary>
    public bool CodeMode;
    public string ProjectFolder = "";

    Rectangle _attachR, _sendR, _modelR, _thinkR, _modeR, _folderR;
    readonly List<(Rectangle r, Attachment a)> _chipX = new();
    string? _hover;

    public event Action? SendClicked;
    public event Action? StopClicked;
    public event Action<Point>? ModelClicked;
    public event Action? AttachClicked;
    public event Action<bool>? ModeClicked;   // true = Code
    public event Action<Point>? ThinkClicked;
    public event Action? CancelEdit;
    public event Action? HeightChanged;

    /// <summary>Earlier prompts, newest first, for the Up and Down arrows. Asked for each time browsing starts.</summary>
    public Func<List<string>>? History;
    List<string> _past = new();
    int _pastAt = -1;          // -1 = not browsing; otherwise the index into _past that is shown
    string _draft = "";        // what was typed before browsing started
    bool _showingPast;         // true while the box is being filled from history

    public bool HasTextSelection => _box.SelectionLength > 0;

    public Composer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Background;
        _box = new TextBox
        {
            Multiline = true, BorderStyle = BorderStyle.None, Font = Theme.Body, ForeColor = Theme.Text,
            BackColor = Color.White, AcceptsReturn = true, ScrollBars = ScrollBars.None, WordWrap = true,
        };
        _placeholder = new Label
        {
            Text = "Send a message", Font = Theme.Body, ForeColor = Theme.Faint, BackColor = Color.White,
            AutoSize = true, Cursor = Cursors.IBeam,
        };
        _placeholder.Click += (_, _) => _box.Focus();
        Controls.Add(_placeholder);
        Controls.Add(_box);
        _placeholder.BringToFront();
        _box.TextChanged += (_, _) =>
        {
            if (!_showingPast) _pastAt = -1;   // typing leaves the history
            _placeholder.Visible = _box.TextLength == 0; Relayout(); Invalidate();
        };
        _box.KeyDown += OnBoxKey;
        AllowDrop = true;
        _box.AllowDrop = true;
        DragEnter += OnDragEnter; _box.DragEnter += OnDragEnter;
        DragDrop += OnDragDrop; _box.DragDrop += OnDragDrop;
        Height = PreferredHeightFor(Width);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull] public override string Text { get => _box.Text; set { _box.Text = value ?? ""; _box.SelectionStart = _box.TextLength; } }
    public void FocusBox() => _box.Focus();

    void OnBoxKey(object? s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            if (!Streaming) SendClicked?.Invoke();
        }
        else if (e.KeyCode is Keys.Up or Keys.Down && e.Modifiers == Keys.None && BrowsePast(e.KeyCode == Keys.Up))
        {
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape && Editing)
        {
            e.SuppressKeyPress = true;
            CancelEdit?.Invoke();
        }
        else if (e.KeyCode == Keys.V && e.Control && Clipboard.ContainsImage())
        {
            e.SuppressKeyPress = true;
            using var img = Clipboard.GetImage();
            if (img != null)
            {
                var path = Path.Combine(Paths.ImageDir, Guid.NewGuid().ToString("N") + ".png");
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                AddAttachment(new Attachment { Name = "pasted-image.png", Kind = "image", Path = path });
            }
        }
    }

    /// <summary>
    /// Up shows the prompt before, Down the one after (and finally what was being typed). Works from an
    /// empty box or while already browsing, and only from the first line (Up) or last line (Down), so the
    /// arrows still move through a message of several lines.
    /// </summary>
    internal bool BrowsePast(bool up)
    {
        if (_pastAt < 0)
        {
            if (!up || _box.TextLength > 0) return false;
            _past = History?.Invoke() ?? new();
            _draft = _box.Text;
        }
        int line = _box.GetLineFromCharIndex(_box.SelectionStart);
        int lastLine = _box.GetLineFromCharIndex(_box.TextLength);
        if (up ? line > 0 : line < lastLine) return false;
        int next = _pastAt + (up ? 1 : -1);
        if (next >= _past.Count) return true;          // already at the oldest
        if (next < -1) return false;
        _showingPast = true;
        try { Text = next < 0 ? _draft : _past[next]; }
        finally { _showingPast = false; }
        _pastAt = next;
        return true;
    }

    static void OnDragEnter(object? s, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    void OnDragDrop(object? s, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    public void AddFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try
            {
                var ext = Path.GetExtension(f);
                if (ImageExt.Contains(ext))
                {
                    var dest = Path.Combine(Paths.ImageDir, Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
                    File.Copy(f, dest);
                    AddAttachment(new Attachment { Name = Path.GetFileName(f), Kind = "image", Path = dest });
                }
                else
                {
                    var bytes = File.ReadAllBytes(f);
                    if (bytes.Length > 2_000_000) { MessageBox.Show(this, $"{Path.GetFileName(f)} is too large (over 2 MB).", "Lplus"); continue; }
                    if (bytes.Take(8000).Contains((byte)0)) { MessageBox.Show(this, $"{Path.GetFileName(f)} isn't a text file or picture.", "Lplus"); continue; }
                    AddAttachment(new Attachment { Name = Path.GetFileName(f), Kind = "text", Text = System.Text.Encoding.UTF8.GetString(bytes) });
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lplus"); }
        }
    }

    public void AddAttachment(Attachment a)
    {
        Attachments.Add(a);
        Relayout();
        Invalidate();
    }

    public void ClearInput()
    {
        _pastAt = -1;
        _box.Clear();
        Attachments.Clear();
        Relayout();
        Invalidate();
    }

    int ChipsH => Attachments.Count > 0 ? Theme.S(40) : 0;
    int BarH => Theme.S(48);

    int TextLines()
    {
        if (_box.TextLength == 0) return 1;
        int lines = _box.GetLineFromCharIndex(_box.TextLength) + 1;
        if (_box.Text.EndsWith("\n")) lines++;
        return Math.Clamp(lines, 1, 8);
    }

    public int PreferredHeightFor(int width) =>
        Theme.S(14) + ChipsH + TextLines() * _box.Font.Height + Theme.S(4) + BarH;

    void Relayout()
    {
        int pad = Theme.S(18);
        int lineH = _box.Font.Height;
        int textH = TextLines() * lineH + Theme.S(4);
        _box.SetBounds(pad, Theme.S(14) + ChipsH, Math.Max(10, Width - pad * 2), textH);
        _box.ScrollBars = _box.GetLineFromCharIndex(_box.TextLength) + 1 > 8 ? ScrollBars.Vertical : ScrollBars.None;
        _placeholder.Location = new Point(pad - 1, Theme.S(14) + ChipsH);
        _placeholder.Text = Editing ? "Edit your message" : CodeMode ? "Describe the change to your project" : "Send a message";
        int h = PreferredHeightFor(Width);
        if (h != Height) { Height = h; HeightChanged?.Invoke(); }
        LayoutButtons();
    }

    void LayoutButtons()
    {
        int b = Theme.S(34);
        int y = Height - BarH + (BarH - b) / 2 - Theme.S(2);
        _attachR = new Rectangle(Theme.S(10), y, b, b);
        _sendR = new Rectangle(Width - Theme.S(10) - b, y, b, b);
        var modelText = string.IsNullOrEmpty(ModelName) ? "Select model" : ModelName;
        int mw = Math.Min(Theme.Measure(modelText, Theme.Small).Width + Theme.S(34), Math.Max(Theme.S(80), Width / 2));
        _modelR = new Rectangle(_sendR.X - Theme.S(6) - mw, y, mw, b);
        if (ThinkMode != "")
        {
            var tt = ThinkLabel();
            int tw = Theme.Measure(tt, Theme.Small).Width + Theme.S(36);
            _thinkR = new Rectangle(_modelR.X - Theme.S(6) - tw, y, tw, b);
        }
        else _thinkR = Rectangle.Empty;

        // Chat | Code switch next to "+", then (in Code mode) the project folder's name.
        int half = Math.Max(Theme.Measure("Chat", Theme.Small).Width, Theme.Measure("Code", Theme.Small).Width) + Theme.S(20);
        int mh = Theme.S(28);
        _modeR = new Rectangle(_attachR.Right + Theme.S(6), y + (b - mh) / 2, half * 2, mh);
        int limit = (_thinkR.IsEmpty ? _modelR.X : _thinkR.X) - Theme.S(8);
        int fx = _modeR.Right + Theme.S(8);
        int fw = Math.Min(Theme.Measure(FolderLabel(), Theme.Small).Width + Theme.S(12), limit - fx);
        _folderR = CodeMode && fw > Theme.S(48) ? new Rectangle(fx, y, fw, b) : Rectangle.Empty;
    }

    string FolderLabel()
    {
        if (string.IsNullOrEmpty(ProjectFolder)) return "Choose a project folder";
        var name = Path.GetFileName(ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : ProjectFolder;
    }

    string ThinkLabel() => ThinkMode == "level"
        ? char.ToUpper(ThinkLevel[0]) + ThinkLevel[1..]
        : "Think";

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_box != null) Relayout();
    }

    public void Sync() { Relayout(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        // Soft shadow.
        for (int i = 3; i >= 1; i--)
            Theme.FillRound(g, Color.FromArgb(6, 0, 0, 0), new RectangleF(r.X - i + 1, r.Y + i, r.Width + 2 * i - 2, r.Height), Theme.S(24) + i);
        Theme.FillRound(g, Color.White, r, Theme.S(24));
        Theme.DrawRound(g, Theme.Border, r, Theme.S(24));

        // Attachment chips.
        _chipX.Clear();
        int cx = Theme.S(14), cy = Theme.S(12);
        foreach (var a in Attachments)
        {
            var label = (a.Kind == "image" ? "▣ " : "▤ ") + a.Name;
            int cw = Math.Min(Theme.Measure(label, Theme.Small).Width + Theme.S(40), Theme.S(220));
            var cr = new Rectangle(cx, cy, cw, Theme.S(30));
            Theme.FillRound(g, Theme.UserBubble, cr, Theme.S(10));
            Theme.DrawEllipsis(g, label, Theme.Small, Theme.TextBody, new Rectangle(cr.X + Theme.S(10), cr.Y, cw - Theme.S(36), cr.Height));
            var xr = new Rectangle(cr.Right - Theme.S(24), cr.Y + Theme.S(7), Theme.S(16), Theme.S(16));
            Theme.Draw(g, "×", Theme.Body, Theme.Muted, xr.X + 2, xr.Y - Theme.S(3));
            _chipX.Add((xr, a));
            cx += cw + Theme.S(6);
        }

        // "+" attach.
        if (_hover == "attach") Theme.FillRound(g, Theme.Hover, _attachR, _attachR.Height / 2f);
        using (var pen = new Pen(Theme.Muted, Theme.Sf(1.6f)))
        {
            float mx = _attachR.X + _attachR.Width / 2f, my = _attachR.Y + _attachR.Height / 2f, k = Theme.Sf(7);
            g.DrawLine(pen, mx - k, my, mx + k, my);
            g.DrawLine(pen, mx, my - k, mx, my + k);
        }

        // Chat | Code switch.
        Theme.FillRound(g, Theme.UserBubble, _modeR, _modeR.Height / 2f);
        int hw = _modeR.Width / 2;
        var onR = new Rectangle(_modeR.X + (CodeMode ? hw : 0) + Theme.S(2), _modeR.Y + Theme.S(2), hw - Theme.S(4), _modeR.Height - Theme.S(4));
        Theme.FillRound(g, Color.White, onR, onR.Height / 2f);
        Theme.DrawRound(g, Theme.Border, onR, onR.Height / 2f);
        for (int i = 0; i < 2; i++)
        {
            var word = i == 0 ? "Chat" : "Code";
            var ws = Theme.Measure(word, Theme.Small);
            Theme.Draw(g, word, Theme.Small, (i == 1) == CodeMode ? Theme.Text : Theme.Muted,
                _modeR.X + i * hw + (hw - ws.Width) / 2, _modeR.Y + (_modeR.Height - ws.Height) / 2);
        }
        if (!_folderR.IsEmpty)
            Theme.DrawEllipsis(g, FolderLabel(), Theme.Small, string.IsNullOrEmpty(ProjectFolder) ? Theme.Blue : Theme.Muted, _folderR);

        // Thinking button.
        if (ThinkMode != "")
        {
            bool on = ThinkMode == "level" || ThinkOn;
            var fill = on ? Color.FromArgb(239, 246, 255) : (_hover == "think" ? Theme.Hover : Color.White);
            Theme.FillRound(g, fill, _thinkR, _thinkR.Height / 2f);
            Theme.DrawRound(g, on ? Color.FromArgb(191, 219, 254) : Theme.Border, _thinkR, _thinkR.Height / 2f);
            var col = on ? Theme.Blue : Theme.Muted;
            DrawBulb(g, col, _thinkR.X + Theme.S(16), _thinkR.Y + _thinkR.Height / 2f);
            var lab = ThinkLabel();
            var ls = Theme.Measure(lab, Theme.Small);
            Theme.Draw(g, lab, Theme.Small, col, _thinkR.X + Theme.S(26), _thinkR.Y + (_thinkR.Height - ls.Height) / 2);
        }

        // Model picker.
        if (_hover == "model") Theme.FillRound(g, Theme.Hover, _modelR, _modelR.Height / 2f);
        var mt = string.IsNullOrEmpty(ModelName) ? "Select model" : ModelName;
        Theme.DrawEllipsis(g, mt, Theme.Small, Theme.TextBody, new Rectangle(_modelR.X + Theme.S(12), _modelR.Y, _modelR.Width - Theme.S(30), _modelR.Height));
        DrawChevron(g, Theme.Muted, _modelR.Right - Theme.S(14), _modelR.Y + _modelR.Height / 2f);

        // Send / stop.
        bool canSend = Streaming || _box.TextLength > 0 || Attachments.Count > 0;
        var sendFill = canSend ? Theme.Accent : Color.FromArgb(212, 212, 212);
        g.FillEllipse(new SolidBrush(sendFill), _sendR);
        float sx = _sendR.X + _sendR.Width / 2f, sy = _sendR.Y + _sendR.Height / 2f;
        if (Streaming)
        {
            float k = Theme.Sf(5);
            Theme.FillRound(g, Color.White, new RectangleF(sx - k, sy - k, 2 * k, 2 * k), Theme.Sf(1.5f));
        }
        else
        {
            using var pen = new Pen(Color.White, Theme.Sf(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float k = Theme.Sf(6);
            g.DrawLine(pen, sx, sy + k, sx, sy - k);
            g.DrawLines(pen, new[] { new PointF(sx - k, sy - 0.2f), new PointF(sx, sy - k), new PointF(sx + k, sy - 0.2f) });
        }
    }

    static void DrawChevron(Graphics g, Color c, float x, float y)
    {
        using var pen = new Pen(c, Theme.Sf(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float k = Theme.Sf(3.5f);
        g.DrawLines(pen, new[] { new PointF(x - k, y - k / 2), new PointF(x, y + k / 2), new PointF(x + k, y - k / 2) });
    }

    static void DrawBulb(Graphics g, Color c, float x, float y)
    {
        using var pen = new Pen(c, Theme.Sf(1.4f));
        float r = Theme.Sf(4.5f);
        g.DrawEllipse(pen, x - r, y - r - Theme.Sf(1.5f), 2 * r, 2 * r);
        g.DrawLine(pen, x - Theme.Sf(2.2f), y + r + Theme.Sf(0.5f), x + Theme.Sf(2.2f), y + r + Theme.Sf(0.5f));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? h = _attachR.Contains(e.Location) ? "attach" : _modeR.Contains(e.Location) ? "mode" : _folderR.Contains(e.Location) ? "folder" : _modelR.Contains(e.Location) ? "model" :
            _thinkR.Contains(e.Location) ? "think" : _sendR.Contains(e.Location) ? "send" :
            _chipX.Any(c => c.r.Contains(e.Location)) ? "chip" : null;
        Cursor = h != null ? Cursors.Hand : Cursors.IBeam;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var (r, a) in _chipX)
            if (r.Contains(e.Location)) { Attachments.Remove(a); Relayout(); Invalidate(); return; }
        if (_attachR.Contains(e.Location) || _folderR.Contains(e.Location)) AttachClicked?.Invoke();
        else if (_modeR.Contains(e.Location)) ModeClicked?.Invoke(e.X >= _modeR.X + _modeR.Width / 2);
        else if (_modelR.Contains(e.Location)) ModelClicked?.Invoke(PointToScreen(new Point(_modelR.X, _modelR.Bottom + 4)));
        else if (_thinkR.Contains(e.Location)) ThinkClicked?.Invoke(PointToScreen(new Point(_thinkR.X, _thinkR.Bottom + 4)));
        else if (_sendR.Contains(e.Location)) { if (Streaming) StopClicked?.Invoke(); else SendClicked?.Invoke(); }
        else _box.Focus();
    }
}

/// <summary>Drop-down list of models with a "Find model..." search box.</summary>
sealed class ModelPicker : ToolStripDropDown
{
    readonly TextBox _search;
    readonly ListBox _list;
    List<ModelInfo> _models = new();
    string _current = "";
    bool _loading;
    public event Action<string, bool>? Picked; // name, installed

    // Shown when nothing matches what is installed (a short list of popular models).
    static readonly string[] Suggested = { "gemma3", "llama3.2", "qwen3", "deepseek-r1", "gpt-oss:20b", "mistral" };

    public ModelPicker()
    {
        Padding = new Padding(Theme.S(6));
        var panel = new Panel { Size = new Size(Theme.S(300), Theme.S(340)), BackColor = Color.White };
        _search = new TextBox { PlaceholderText = "Find model...", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body, Dock = DockStyle.Top };
        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Theme.S(34), Font = Theme.Body, IntegralHeight = false,
        };
        var gap = new Panel { Dock = DockStyle.Top, Height = Theme.S(6), BackColor = Color.White };
        panel.Controls.Add(_list);
        panel.Controls.Add(gap);
        panel.Controls.Add(_search);
        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size };
        Items.Add(host);
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.SelectedIndex = 0; e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Choose(_list.Items.Count > 0 ? 0 : -1); }
        };
        _list.DrawItem += DrawRow;
        _list.MouseClick += (_, e) => Choose(_list.IndexFromPoint(e.Location));
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Choose(_list.SelectedIndex); };
    }

    sealed record Row(string Name, string Detail, bool Installed, bool Header = false);

    public void Open(Point screen, List<ModelInfo>? models, string current, bool loading)
    {
        _models = models ?? new();
        _current = current;
        _loading = loading;
        _search.Text = "";
        Fill();
        Show(screen);
        _search.Focus();
    }

    void Fill()
    {
        _list.Items.Clear();
        var q = _search.Text.Trim();
        if (_loading && _models.Count == 0) { _list.Items.Add(new Row("Loading models…", "", false, true)); return; }
        var installed = _models.Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var m in installed)
            _list.Items.Add(new Row(m.Name, m.ParameterSize, true));
        var names = new HashSet<string>(_models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var sugg = Suggested.Where(s => !names.Contains(s) && !names.Contains(s + ":latest") &&
                                        (q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (q.Length > 0 && !names.Contains(q) && !names.Contains(q + ":latest") && !sugg.Contains(q, StringComparer.OrdinalIgnoreCase))
            sugg.Insert(0, q);
        if (sugg.Count > 0)
        {
            _list.Items.Add(new Row("Download", "", false, true));
            foreach (var s in sugg) _list.Items.Add(new Row(s, "", false));
        }
        if (_list.Items.Count == 0) _list.Items.Add(new Row("No models found", "", false, true));
    }

    void Choose(int i)
    {
        if (i < 0 || i >= _list.Items.Count || _list.Items[i] is not Row r || r.Header) return;
        Close();
        Picked?.Invoke(r.Name, r.Installed);
    }

    void DrawRow(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var r = (Row)_list.Items[e.Index];
        var g = e.Graphics;
        Theme.Smooth(g);
        g.FillRectangle(Brushes.White, e.Bounds);
        if (r.Header)
        {
            Theme.DrawEllipsis(g, r.Name, Theme.Small, Theme.Faint, new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y, e.Bounds.Width - Theme.S(20), e.Bounds.Height));
            return;
        }
        bool sel = (e.State & DrawItemState.Selected) != 0;
        if (sel) Theme.FillRound(g, Theme.Hover, new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4), Theme.S(8));
        var nameRect = new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y, e.Bounds.Width - Theme.S(90), e.Bounds.Height);
        Theme.DrawEllipsis(g, r.Name, Theme.Body, Theme.Text, nameRect);
        var right = r.Installed ? (r.Name == _current ? "✓" : r.Detail) : "↓";
        if (right.Length > 0)
        {
            var rs = Theme.Measure(right, Theme.Small);
            Theme.Draw(g, right, Theme.Small, r.Name == _current ? Theme.Text : Theme.Faint, e.Bounds.Right - Theme.S(12) - rs.Width, e.Bounds.Y + (e.Bounds.Height - rs.Height) / 2);
        }
    }
}
