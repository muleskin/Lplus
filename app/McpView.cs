using System.Diagnostics;

namespace Lplus;

/// <summary>
/// The MCP servers page: the servers in mcp.json with how each is doing, buttons to add, change, remove,
/// switch on or off and connect them, a box that filters the list, and a search of the public registry
/// for servers to add.
/// </summary>
sealed class McpView : Control
{
    readonly McpHub _hub;
    readonly TextBox _search;
    readonly List<(Rectangle r, Action act)> _hits = new();
    List<McpRegistryEntry>? _found;      // registry results, null until a search was made
    string _foundFor = "";
    string? _findNote;                   // "Searching…" or why the search failed
    int _scroll, _contentH;

    /// <summary>Raised after the servers changed, so other pages can show the new state.</summary>
    public event Action? Changed;

    public McpView(McpHub hub)
    {
        _hub = hub;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        _search = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.UserBubble, PlaceholderText = "Filter your servers, or type what to look for online" };
        _search.TextChanged += (_, _) => { _scroll = 0; Invalidate(); };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = FindOnline(); } };
        Controls.Add(_search);
        // The hub reports from whatever thread finished connecting; painting belongs to this one.
        _hub.Changed += () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate); } catch { } };
    }

    int ColW => Math.Min(Width - Theme.S(64), Theme.S(640));
    int ColX => (Width - ColW) / 2;
    string Filter => _search.Text.Trim();

    bool Matches(McpServerConfig c) =>
        Filter.Length == 0 || c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || c.Summary.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || (_hub.Connection(c.Name)?.Tools.Any(t => t.name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) ?? false);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.Smooth(g);
        _hits.Clear();
        int x = ColX, w = ColW;
        int y = Theme.S(36) - _scroll;

        Theme.Draw(g, "MCP servers", Theme.Title, Theme.Text, x, y);
        var add = Button(g, "Add server", x + w, y + Theme.S(16), primary: true);
        _hits.Add((add, () => Edit(null)));
        var file = Button(g, "Open mcp.json", add.X - Theme.S(8), y + Theme.S(16));
        _hits.Add((file, OpenFile));
        var reload = Button(g, "Reload", file.X - Theme.S(8), y + Theme.S(16));
        _hits.Add((reload, () => _ = Reload()));
        var import = Button(g, "Import", reload.X - Theme.S(8), y + Theme.S(16));
        _hits.Add((import, Import));
        y += Theme.S(46);
        Theme.DrawEllipsis(g, "Servers that give models extra tools. They are used in both Chat and Code mode.", Theme.Small, Theme.Muted, new Rectangle(x, y, w, Theme.S(20)));
        y += Theme.S(30);

        // Search box with its button.
        var find = Button(g, "Find online", x + w, y + Theme.S(18));
        _hits.Add((find, () => _ = FindOnline()));
        var box = new Rectangle(x, y, find.X - Theme.S(8) - x, Theme.S(36));
        Theme.FillRound(g, Theme.UserBubble, box, Theme.S(8));
        _search.SetBounds(box.X + Theme.S(12), box.Y + (box.Height - _search.PreferredHeight) / 2, box.Width - Theme.S(24), _search.PreferredHeight);
        _search.Visible = box.Y > -box.Height;
        y += Theme.S(52);

        if (_hub.ConfigError != null)
        {
            Theme.DrawEllipsis(g, _hub.ConfigError, Theme.Small, Theme.ErrorText, new Rectangle(x, y, w, Theme.S(20)));
            y += Theme.S(26);
        }

        // ---- your servers ----
        Theme.Draw(g, "Your servers", Theme.BodyBold, Theme.Text, x, y);
        y += Theme.S(30);
        var mine = _hub.Configs.Where(Matches).ToList();
        if (mine.Count == 0)
        {
            var none = _hub.Configs.Count == 0 ? "No servers yet. Click Add server, or look one up with Find online." : "None of your servers match.";
            Theme.DrawEllipsis(g, none, Theme.Small, Theme.Muted, new Rectangle(x, y, w, Theme.S(20)));
            y += Theme.S(32);
        }
        foreach (var c in mine) y = ServerCard(g, c, x, y, w);

        // ---- found online ----
        if (_found != null || _findNote != null)
        {
            y += Theme.S(8);
            Theme.Draw(g, _foundFor.Length > 0 ? $"Found online for \"{_foundFor}\"" : "Found online", Theme.BodyBold, Theme.Text, x, y);
            y += Theme.S(30);
            if (_findNote != null)
            {
                Theme.DrawEllipsis(g, _findNote, Theme.Small, _found == null && !_findNote.StartsWith("Searching") ? Theme.ErrorText : Theme.Muted, new Rectangle(x, y, w, Theme.S(20)));
                y += Theme.S(28);
            }
            foreach (var entry in _found ?? new()) y = FoundCard(g, entry, x, y, w);
            if (_found is { Count: > 0 })
            {
                Theme.DrawEllipsis(g, "From the public MCP registry. Anyone can publish there: add only servers you trust.", Theme.Tiny, Theme.Faint, new Rectangle(x, y, w, Theme.S(18)));
                y += Theme.S(22);
            }
        }
        _contentH = y + _scroll + Theme.S(24);
    }

    int ServerCard(Graphics g, McpServerConfig c, int x, int y, int w)
    {
        var (status, ok, starting) = _hub.StatusOf(c);
        var conn = _hub.Connection(c.Name);
        string tools = conn is { Ready: true } && conn.Tools.Count > 0 ? string.Join(", ", conn.Tools.Select(t => t.name)) : "";
        // What the server said about itself when it connected.
        if (conn is { Ready: true } && conn.ProtocolVersion.Length > 0)
        {
            status += $" · protocol {conn.ProtocolVersion}";
            var extra = conn.Capabilities.Where(c => c is "resources" or "prompts").ToList();
            if (extra.Count > 0) status += $" · also offers {string.Join(" and ", extra)} (not used by Lplus)";
        }
        int h = Theme.S(tools.Length > 0 ? 104 : 84);
        var card = new Rectangle(x, y, w, h);
        Theme.FillRound(g, Color.White, card, Theme.S(14));
        Theme.DrawRound(g, Theme.Border, card, Theme.S(14));

        int tx = x + Theme.S(16), ty = y + Theme.S(14);
        using (var dot = new SolidBrush(ok ? Theme.Green : starting ? Theme.Blue : Theme.BorderStrong))
            g.FillEllipse(dot, tx, ty + Theme.S(6), Theme.S(9), Theme.S(9));

        // Buttons, right to left: Remove, Edit, Connect; and the on/off switch.
        int by = y + Theme.S(28);
        var remove = Button(g, "Remove", x + w - Theme.S(16), by);
        _hits.Add((remove, () => Remove(c)));
        var edit = Button(g, "Edit", remove.X - Theme.S(8), by);
        _hits.Add((edit, () => Edit(c)));
        int left = edit.X;
        if (!c.Disabled)
        {
            var connect = Button(g, conn == null ? "Connect" : starting ? "Connecting…" : "Reconnect", edit.X - Theme.S(8), by);
            if (!starting) _hits.Add((connect, () => _ = Connect(c)));
            left = connect.X;
        }
        int tw = Theme.S(40), th = Theme.S(22);
        var sw = new Rectangle(x + w - Theme.S(16) - tw, y + h - th - Theme.S(14), tw, th);
        Theme.FillRound(g, c.Disabled ? Color.FromArgb(212, 212, 212) : Theme.Accent, sw, th / 2f);
        int k = th - Theme.S(4);
        g.FillEllipse(Brushes.White, new Rectangle(c.Disabled ? sw.X + Theme.S(2) : sw.Right - k - Theme.S(2), sw.Y + Theme.S(2), k, k));
        _hits.Add((sw, () => Toggle(c)));
        var swLabel = c.Disabled ? "Off" : "On";
        var sl = Theme.Measure(swLabel, Theme.Small);
        Theme.Draw(g, swLabel, Theme.Small, Theme.Muted, sw.X - sl.Width - Theme.S(8), sw.Y + (th - sl.Height) / 2);

        int textW = left - Theme.S(12) - (tx + Theme.S(18));
        Theme.DrawEllipsis(g, c.Name, Theme.BodyBold, Theme.Text, new Rectangle(tx + Theme.S(18), ty - Theme.S(2), textW, Theme.S(24)));
        Theme.DrawEllipsis(g, (c.IsHttp ? "Web address: " : "Program: ") + c.Summary, Theme.Small, Theme.Muted, new Rectangle(tx + Theme.S(18), ty + Theme.S(24), textW, Theme.S(20)));
        int lineW = sw.X - sl.Width - Theme.S(20) - (tx + Theme.S(18));
        Theme.DrawEllipsis(g, char.ToUpper(status[0]) + status[1..], Theme.Small, ok || starting || c.Disabled || conn == null ? Theme.TextBody : Theme.ErrorText,
            new Rectangle(tx + Theme.S(18), ty + Theme.S(46), lineW, Theme.S(20)));
        if (tools.Length > 0)
            Theme.DrawEllipsis(g, tools, Theme.Tiny, Theme.Faint, new Rectangle(tx + Theme.S(18), ty + Theme.S(68), lineW, Theme.S(18)));
        return y + h + Theme.S(12);
    }

    int FoundCard(Graphics g, McpRegistryEntry e, int x, int y, int w)
    {
        int h = Theme.S(96);
        var card = new Rectangle(x, y, w, h);
        Theme.FillRound(g, Color.White, card, Theme.S(14));
        Theme.DrawRound(g, Theme.Border, card, Theme.S(14));
        int tx = x + Theme.S(16), ty = y + Theme.S(12);
        int right = x + w - Theme.S(16);
        bool have = e.Suggested != null && _hub.Configs.Any(c => c.Name.Equals(e.Suggested.Name, StringComparison.OrdinalIgnoreCase));
        if (e.Suggested != null)
        {
            var add = Button(g, have ? "Added" : "Add", right, y + Theme.S(28), primary: !have);
            if (!have) _hits.Add((add, () => AddFound(e)));
            right = add.X - Theme.S(8);
        }
        if (e.Repository.StartsWith("https://"))
        {
            var page = Button(g, "Web page", right, y + Theme.S(28));
            _hits.Add((page, () => { try { Process.Start(new ProcessStartInfo(e.Repository) { UseShellExecute = true }); } catch { } }));
            right = page.X - Theme.S(8);
        }
        Theme.DrawEllipsis(g, e.Name + (e.Version.Length > 0 ? "   " + e.Version : ""), Theme.BodyBold, Theme.Text, new Rectangle(tx, ty, right - tx, Theme.S(24)));
        TextRenderer.DrawText(g, e.Description, Theme.Small, new Rectangle(tx, ty + Theme.S(24), w - Theme.S(32) - (x + w - Theme.S(16) - right), Theme.S(36)), Theme.TextBody,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        Theme.DrawEllipsis(g, e.How, Theme.Tiny, Theme.Faint, new Rectangle(tx, y + h - Theme.S(26), w - Theme.S(32), Theme.S(18)));
        return y + h + Theme.S(12);
    }

    Rectangle Button(Graphics g, string text, int right, int midY, bool primary = false)
    {
        var ts = Theme.Measure(text, Theme.Small);
        var br = new Rectangle(right - ts.Width - Theme.S(24), midY - Theme.S(16), ts.Width + Theme.S(24), Theme.S(32));
        Theme.FillRound(g, primary ? Theme.Accent : Color.White, br, Theme.S(8));
        if (!primary) Theme.DrawRound(g, Theme.BorderStrong, br, Theme.S(8));
        Theme.Draw(g, text, Theme.Small, primary ? Color.White : Theme.Text, br.X + Theme.S(12), br.Y + (br.Height - ts.Height) / 2);
        return br;
    }

    // ---------- what the buttons do ----------

    void Saved()
    {
        try { _hub.Save(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), "mcp.json could not be saved: " + ex.Message, "Lplus"); }
        Changed?.Invoke();
        Invalidate();
    }

    /// <summary>Adds a server (c == null) or changes one, then connects it.</summary>
    void Edit(McpServerConfig? c, McpRegistryEntry? from = null)
    {
        var start = c?.Clone() ?? from?.Suggested?.Clone() ?? new McpServerConfig();
        var taken = _hub.Configs.Where(x => x != c).Select(x => x.Name).ToList();
        using var dlg = new McpServerDialog(start, taken, isNew: c == null, notes: from?.Notes);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var edited = dlg.Result;
        edited.Raw = c?.Raw;
        if (c == null) _hub.Configs.Add(edited);
        else
        {
            _hub.Disconnect(c.Name);
            _hub.Configs[_hub.Configs.IndexOf(c)] = edited;
        }
        Saved();
        if (!edited.Disabled) _ = Connect(edited);
    }

    void AddFound(McpRegistryEntry e) => Edit(null, e);

    /// <summary>Brings in servers already set up for other programs on this PC.</summary>
    void Import()
    {
        var found = McpConfig.FoundElsewhere()
            .Where(f => !_hub.Configs.Any(c => c.Name.Equals(f.server.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0)
        {
            MessageBox.Show(FindForm(), "No other servers were found. Lplus looks in the Claude Code settings, the Claude desktop app's settings and a .mcp.json in your home folder.", "Import servers");
            return;
        }
        using var dlg = new McpImportDialog(found);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Chosen.Count == 0) return;
        foreach (var c in dlg.Chosen) _hub.Configs.Add(c);
        Saved();
        foreach (var c in dlg.Chosen) _ = Connect(c);
    }

    void Remove(McpServerConfig c)
    {
        if (MessageBox.Show(FindForm(), $"Remove the server \"{c.Name}\"?", "Remove server", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _hub.Disconnect(c.Name);
        _hub.Configs.Remove(c);
        Saved();
    }

    void Toggle(McpServerConfig c)
    {
        c.Disabled = !c.Disabled;
        Saved();
        if (c.Disabled) _hub.Disconnect(c.Name);
        else _ = Connect(c);
    }

    async Task Connect(McpServerConfig c)
    {
        try { await _hub.Connect(c); } catch { }
        if (IsDisposed) return;
        Changed?.Invoke();
        Invalidate();
    }

    async Task Reload()
    {
        if (_hub.Loading || _hub.Path == null) return;
        try { await _hub.Load(_hub.Path); } catch { }
        if (IsDisposed) return;
        Changed?.Invoke();
        Invalidate();
    }

    void OpenFile()
    {
        try
        {
            if (_hub.Path == null) return;
            if (!File.Exists(_hub.Path)) File.WriteAllText(_hub.Path, McpConfig.Empty);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_hub.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Lplus"); }
    }

    /// <summary>Looks the search words up in the public MCP registry.</summary>
    async Task FindOnline()
    {
        var q = Filter;
        if (q.Length == 0) { _found = null; _findNote = "Type what to look for (for example: github, files, database), then click Find online."; _foundFor = ""; Invalidate(); return; }
        _foundFor = q;
        _found = null;
        _findNote = "Searching the MCP registry…";
        Invalidate();
        try
        {
            var found = await McpRegistry.Search(q);
            if (_foundFor != q || IsDisposed) return;
            _found = found;
            _findNote = found.Count == 0 ? "Nothing found. Try one word, such as the product's name." : null;
        }
        catch (Exception ex)
        {
            if (_foundFor != q || IsDisposed) return;
            _findNote = "The registry could not be reached: " + ex.Message;
        }
        Invalidate();
    }

    /// <summary>For checking the page's layout in a screenshot: sample search results.</summary>
    public void ShowSample(List<McpRegistryEntry> found, string query)
    {
        _found = found;
        _foundFor = query;
        _findNote = null;
        Invalidate();
    }

    // ---------- mouse ----------

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var (r, act) in _hits.ToList()) if (r.Contains(e.Location)) { act(); return; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _hits.Any(h => h.r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - e.Delta / 2, 0, Math.Max(0, _contentH - Height));
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { _scroll = 0; Invalidate(); }
    }
}

/// <summary>The list of servers found in other programs' settings, with a tick box each.</summary>
sealed class McpImportDialog : Form
{
    readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true };
    readonly List<(McpServerConfig server, string from)> _found;
    public List<McpServerConfig> Chosen { get; } = new();

    public McpImportDialog(List<(McpServerConfig server, string from)> found)
    {
        _found = found;
        Text = "Import servers";
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(Theme.S(680), Theme.S(360));
        Padding = new Padding(Theme.S(16));
        var head = new Label { Text = "These servers are set up for other programs on this PC. Tick the ones Lplus should use too.", Dock = DockStyle.Top, Height = Theme.S(30), ForeColor = Theme.TextBody };
        foreach (var (server, from) in found) _list.Items.Add($"{server.Name}   —   {server.Summary}   ({from})", true);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, Theme.S(10), 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        var ok = new Button { Text = "Import and connect", AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        ok.Click += (_, _) =>
        {
            foreach (int i in _list.CheckedIndices) Chosen.Add(_found[i].server);
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(_list);
        Controls.Add(head);
        Controls.Add(buttons);
        _list.BringToFront();
    }
}

/// <summary>The form for adding or changing one server.</summary>
sealed class McpServerDialog : Form
{
    readonly TextBox _name = Box(), _command = Box(), _url = Box();
    readonly TextBox _args = Box(lines: 4), _env = Box(lines: 4), _headers = Box(lines: 3);
    readonly RadioButton _isProgram = new() { Text = "A program on this PC", AutoSize = true };
    readonly RadioButton _isWeb = new() { Text = "A web address", AutoSize = true };
    readonly CheckBox _on = new() { Text = "Switched on", AutoSize = true };
    readonly Label _problem = new() { AutoSize = false, ForeColor = Theme.ErrorText, Height = 22, Dock = DockStyle.Top };
    readonly List<string> _taken;
    readonly List<Control> _programRows = new(), _webRows = new();

    public McpServerConfig Result { get; private set; } = new();

    static TextBox Box(int lines = 1) => new()
    {
        Dock = DockStyle.Fill, Font = Theme.Body, Multiline = lines > 1, AcceptsReturn = lines > 1,
        ScrollBars = lines > 1 ? ScrollBars.Vertical : ScrollBars.None, Height = lines > 1 ? 22 * lines : 26, WordWrap = false,
    };

    public McpServerDialog(McpServerConfig c, List<string> taken, bool isNew, List<string>? notes)
    {
        _taken = taken;
        Text = isNew ? "Add MCP server" : "Edit MCP server";
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Theme.S(560), Theme.S(notes is { Count: > 0 } ? 640 : 540));
        Padding = new Padding(Theme.S(16));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(130)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control field, string? hint = null, List<Control>? group = null)
        {
            var l = new Label { Text = label, AutoSize = true, Margin = new Padding(0, Theme.S(7), 0, 0), ForeColor = Theme.TextBody };
            field.Margin = new Padding(0, Theme.S(3), 0, hint == null ? Theme.S(8) : 0);
            grid.Controls.Add(l);
            grid.Controls.Add(field);
            group?.Add(l); group?.Add(field);
            if (hint == null) return;
            var h = new Label { Text = hint, AutoSize = true, Font = Theme.Tiny, ForeColor = Theme.Faint, Margin = new Padding(0, Theme.S(2), 0, Theme.S(8)) };
            var spacer = new Label { AutoSize = true, Height = 1 };
            grid.Controls.Add(spacer);
            grid.Controls.Add(h);
            group?.Add(spacer); group?.Add(h);
        }

        Row("Name", _name, "A short name, such as files or github. Tools are shown to the model as name_tool.");
        var kind = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        kind.Controls.Add(_isProgram);
        kind.Controls.Add(_isWeb);
        Row("Kind", kind);
        Row("Command", _command, "The program to start, for example npx, uvx, docker or the full path of an .exe.", _programRows);
        Row("Arguments", _args, "One per line.", _programRows);
        Row("Environment", _env, "One per line, as NAME=value. Keys and passwords the server needs go here.", _programRows);
        Row("Address", _url, "Starting with https:// (or http:// for a server on your network).", _webRows);
        Row("Headers", _headers, "One per line, as Name: value. For example Authorization: Bearer your-key", _webRows);
        Row("", _on);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, Theme.S(8), 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        var save = new Button { Text = isNew ? "Add and connect" : "Save and connect", AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        save.Click += (_, _) => { if (Read()) { DialogResult = DialogResult.OK; Close(); } };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        AcceptButton = null;   // Enter makes a new line in the lists
        CancelButton = cancel;

        Controls.Add(grid);
        if (notes is { Count: > 0 })
        {
            var about = new TextBox
            {
                Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = Theme.S(96), Font = Theme.Small,
                BackColor = Theme.UserBubble, BorderStyle = BorderStyle.None, TabStop = false,
                Text = "What this server asks for:\r\n" + string.Join("\r\n", notes.Select(n => "• " + n)),
            };
            Controls.Add(about);
        }
        Controls.Add(_problem);
        Controls.Add(buttons);
        grid.BringToFront();

        _name.Text = c.Name;
        _command.Text = c.Command;
        _args.Lines = c.Args.ToArray();
        _env.Lines = c.Env.Select(kv => kv.Key + "=" + kv.Value).ToArray();
        _url.Text = c.Url;
        _headers.Lines = c.Headers.Select(kv => kv.Key + ": " + kv.Value).ToArray();
        _on.Checked = !c.Disabled;
        (c.IsHttp ? _isWeb : _isProgram).Checked = true;
        _isProgram.CheckedChanged += (_, _) => ShowKind();
        ShowKind();
    }

    void ShowKind()
    {
        foreach (var r in _programRows) r.Visible = _isProgram.Checked;
        foreach (var r in _webRows) r.Visible = !_isProgram.Checked;
    }

    /// <summary>Checks what was typed and fills Result; says what is wrong when something is.</summary>
    bool Read()
    {
        var c = new McpServerConfig { Name = _name.Text.Trim(), Disabled = !_on.Checked };
        string? problem = null;
        if (c.Name.Length == 0) problem = "Give the server a name.";
        else if (_taken.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) problem = "Another server already has that name.";
        if (_isProgram.Checked)
        {
            c.Command = _command.Text.Trim().Trim('"');
            c.Args = _args.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            foreach (var line in _env.Lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) { problem ??= $"Environment lines must look like NAME=value (\"{line}\" does not)."; continue; }
                c.Env[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            if (c.Command.Length == 0) problem ??= "Give the command that starts the server.";
        }
        else
        {
            c.Url = _url.Text.Trim();
            foreach (var line in _headers.Lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) { problem ??= $"Header lines must look like Name: value (\"{line}\" does not)."; continue; }
                c.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) problem ??= "The address must start with https:// or http://";
        }
        _problem.Text = problem ?? "";
        if (problem != null) return false;
        Result = c;
        return true;
    }

    /// <summary>For the self-test: fills the form the way a person would and reads it back.</summary>
    internal McpServerConfig? ReadForTest() => Read() ? Result : null;
    internal string Problem => _problem.Text;
    internal void TypeForTest(string name, bool program, string command, string[] args, string[] env, string url, string[] headers)
    {
        _name.Text = name;
        (program ? _isProgram : _isWeb).Checked = true;
        _command.Text = command; _args.Lines = args; _env.Lines = env; _url.Text = url; _headers.Lines = headers;
    }
}
