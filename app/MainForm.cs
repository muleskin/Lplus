using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Lplus;

sealed class MainForm : Form
{
    readonly Store _store;
    Settings _settings;
    readonly ServerClient _client = new();
    readonly ServerManager _server;
    readonly string? _screenshot;

    readonly Sidebar _sidebar = new() { Dock = DockStyle.Left };
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    readonly Panel _chatPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    readonly ChatView _chat = new() { Dock = DockStyle.Fill };
    readonly Panel _bottom = new() { Dock = DockStyle.Bottom, BackColor = Theme.Background };
    readonly Composer _composer = new();
    readonly SettingsView _settingsView = new() { Dock = DockStyle.Fill, Visible = false };
    readonly ModelPicker _picker = new();
    readonly StatusBar _status = new() { Dock = DockStyle.Bottom };
    readonly System.Windows.Forms.Timer _gpuTimer = new() { Interval = 3000 };
    bool _gpuPolling;
    readonly Label _banner = new()
    {
        Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(255, 251, 235), ForeColor = Color.FromArgb(146, 64, 14), Font = Theme.Small,
    };
    NotifyIcon? _tray;

    string? _chatId;
    List<ModelInfo>? _models;
    bool _modelsLoading;
    HashSet<string> _caps = new();
    CancellationTokenSource? _cts;
    ChatMessage? _editing;
    bool _quitting;
    int _frames;
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 50 };
    ChatMessage? _dirty;

    public MainForm(Store store, string? screenshot)
    {
        _store = store;
        _screenshot = screenshot;
        _settings = store.LoadSettings();
        _server = new ServerManager(_client, _settings);

        Text = "Lplus";
        Icon = Theme.MakeIcon(32);
        BackColor = Theme.Background;
        Font = Theme.Body;
        MinimumSize = new Size(Theme.S(560), Theme.S(420));
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(Math.Max(MinimumSize.Width, Theme.S(_settings.WindowWidth)), Math.Max(MinimumSize.Height, Theme.S(_settings.WindowHeight)));
        KeyPreview = true;

        _bottom.Controls.Add(_composer);
        _chatPage.Controls.Add(_chat);
        _chatPage.Controls.Add(_bottom);
        _chatPage.Controls.Add(_banner);
        _content.Controls.Add(_chatPage);
        _content.Controls.Add(_settingsView);
        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_status); // added last so it spans the whole width under the sidebar too

        _chatPage.Resize += (_, _) => LayoutChatPage();
        _composer.HeightChanged += LayoutChatPage;
        _composer.SendClicked += () => _ = Send();
        _composer.StopClicked += () => _cts?.Cancel();
        _composer.AttachClicked += () => { if (_composer.CodeMode) PickProjectFolder(); else PickFiles(); };
        _composer.ModeClicked += SetMode;
        _composer.History = () => _store.RecentPrompts();
        _composer.ModelClicked += p => _ = OpenPicker(p);
        _composer.ThinkClicked += ThinkMenu;
        _composer.CancelEdit += CancelEdit;
        _picker.Picked += (name, installed) => SelectModel(name);
        _chat.EditRequested += BeginEdit;
        _chat.RetryRequested += m => _ = Retry(m);

        _sidebar.NewChat += NewChat;
        _sidebar.OpenChat += OpenChat;
        _sidebar.OpenSettings += ShowSettings;
        _sidebar.RenameChat += (id, title) => { _store.RenameChat(id, title); ReloadSidebar(); };
        _sidebar.DeleteChat += id =>
        {
            if (id == _chatId) { _cts?.Cancel(); NewChat(); }
            _store.DeleteChat(id);
            ReloadSidebar();
        };
        _settingsView.Changed += s => _ = ApplySettings(s);
        _settingsView.ViewLogs += ViewLogs;

        _uiTimer.Tick += (_, _) => { if (_dirty != null) { var m = _dirty; _dirty = null; _chat.Refresh(m); } };
        _uiTimer.Start();
        _gpuTimer.Tick += (_, _) => _ = PollGpu();

        if (_screenshot == null) SetupTray();
        ReloadSidebar();
        NewChat();
        _composer.ModelName = _settings.SelectedModel;
        _composer.CodeMode = _settings.Mode == "code";
        _composer.ProjectFolder = _settings.ProjectFolder;
        _composer.Sync();
    }

    // ---------- window / screenshot ----------

    protected override bool ShowWithoutActivation => _screenshot != null;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_screenshot != null) cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_screenshot != null)
        {
            SetWindowPos(Handle, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
            var t = new System.Windows.Forms.Timer { Interval = 16 };
            t.Tick += (_, _) =>
            {
                _frames++;
                Invalidate(true);
                if (_frames >= 120) { t.Stop(); SaveScreenshot(); }
            };
            t.Start();
            _ = LoadModels();   // read-only: never starts a server
            _ = PollGpu();
            // Test views for checking the layout (never saved): LPLUS_SHOT_PAGE=settings or demo.
            var page = Environment.GetEnvironmentVariable("LPLUS_SHOT_PAGE");
            if (page == "settings") ShowSettings();
            else if (page == "demo") { ShowDemo(); if (Environment.GetEnvironmentVariable("LPLUS_SHOT_SELECT") == "1") _chat.SelectAll(); }
            return;
        }
        _composer.FocusBox();
        _server.StateChanged += () => BeginInvoke(UpdateServerState);
        UpdateServerState();
        await _server.Start();
        await LoadModels();
        _ = PollGpu();
        _gpuTimer.Start();
    }

    void ShowDemo()
    {
        var now = DateTime.Now;
        _chat.SetMessages(new List<ChatMessage>
        {
            new() { Role = "user", Content = "How do I reverse a list in Python?" },
            new()
            {
                Role = "assistant", Model = "sample-model", Thinking = "The user wants list reversal.",
                ThinkStart = now.AddSeconds(-4.2), ThinkEnd = now,
                Content = "There are two common ways:\n\n## In place\n\nUse **`list.reverse()`** — it changes the list itself:\n\n```python\nitems = [1, 2, 3]\nitems.reverse()\nprint(items)  # [3, 2, 1]\n```\n\n## As a copy\n\n- `items[::-1]` makes a *new* reversed list\n- `reversed(items)` gives an iterator\n\n> Tip: use the copy when you still need the original order.",
            },
            new() { Role = "user", Content = "Thanks!" },
            new() { Role = "assistant", Model = "sample-model", Content = "You're welcome", Streaming = true },
        });
        LayoutChatPage();
    }

    void SaveScreenshot()
    {
        try
        {
            // PrintWindow renders what the window really shows, even when it's behind other windows.
            using var client = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (var g = Graphics.FromImage(client))
            {
                var hdc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(Handle, hdc, 0x1 | 0x2 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok) DrawToBitmap(client, new Rectangle(0, 0, client.Width, client.Height));
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(_screenshot!));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            client.Save(_screenshot!, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Log.Error("screenshot failed: " + ex.Message); }
        _quitting = true;
        Application.Exit();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (_screenshot != null || WindowState != FormWindowState.Normal) return;
        _settings.WindowWidth = (int)(Width / Theme.Scale);
        _settings.WindowHeight = (int)(Height / Theme.Scale);
        _store.SaveSettings(_settings);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps the server running in the tray.
        if (!_quitting && _screenshot == null && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _server.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N)) { NewChat(); return true; }
        // Ctrl+C copies text selected in the conversation, unless text is selected in the message box.
        if (keyData == (Keys.Control | Keys.C) && _chatPage.Visible && _chat.HasSelection && !_composer.HasTextSelection && _chat.CopySelection()) return true;
        if (keyData == (Keys.Control | Keys.Oemcomma)) { ShowSettings(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void BringToFrontFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        SetForegroundWindow(Handle);
    }

    public void QuitFromSignal() => Quit();

    void Quit()
    {
        _quitting = true;
        Close();
        Application.Exit();
    }

    // ---------- tray ----------

    void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Lplus", null, (_, _) => BringToFrontFromTray());
        menu.Items.Add("Settings", null, (_, _) => { BringToFrontFromTray(); ShowSettings(); });
        menu.Items.Add("View logs", null, (_, _) => ViewLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lplus", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = Theme.MakeIcon(16), Text = "Lplus", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) BringToFrontFromTray(); };
    }

    static void ViewLogs()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Refreshes the status bar: loaded models' GPU share (/api/ps) and the detected graphics card.</summary>
    async Task PollGpu()
    {
        if (_gpuPolling || IsDisposed) return;
        _gpuPolling = true;
        try
        {
            var loaded = await GpuStatus.Loaded(_client);
            var devices = await Task.Run(GpuStatus.Devices);
            if (!IsDisposed) _status.Update(loaded, devices);
        }
        finally { _gpuPolling = false; }
    }

    // ---------- server / settings ----------

    void UpdateServerState()
    {
        _ = RefreshServerLabels();
    }

    async Task RefreshServerLabels()
    {
        var v = await _client.Version();
        if (IsDisposed) return;
        _settingsView.ServerStatus = v != null ? $"Running (version {v})" : "Not running";
        _settingsView.ServerDetail = v == null ? (_server.Error ?? "") : _server.External ? "started by another app" : "started by Lplus";
        _settingsView.Invalidate();
        SetBanner(v == null ? (_server.Error ?? "Starting the model server…") : null);
    }

    void SetBanner(string? text)
    {
        _banner.Text = text ?? "";
        _banner.Height = text == null ? 0 : Theme.S(30);
    }

    async Task ApplySettings(Settings s)
    {
        s.SelectedModel = _settings.SelectedModel;
        s.ThinkEnabled = _settings.ThinkEnabled;
        s.ThinkLevel = _settings.ThinkLevel;
        s.WindowWidth = _settings.WindowWidth;
        s.WindowHeight = _settings.WindowHeight;
        s.Mode = _settings.Mode;
        bool serverChange = _settings.ServerDiffers(s);
        _settings = s;
        _store.SaveSettings(_settings);
        _composer.ProjectFolder = _settings.ProjectFolder;
        _composer.Sync();
        if (!serverChange || _screenshot != null) return;
        if (_cts != null) { _settingsView.ApplyNote = "Changes will apply after the current reply finishes and the model server restarts."; }
        var ok = await _server.Apply(_settings);
        _settingsView.ApplyNote = ok ? null : "The model server was started by another app. Quit it and reopen Lplus to apply these changes.";
        await RefreshServerLabels();
        await LoadModels();
    }

    // ---------- models ----------

    async Task LoadModels()
    {
        _modelsLoading = true;
        try { _models = await _client.Models(); }
        catch { _models ??= null; }
        finally { _modelsLoading = false; }
        if (IsDisposed) return;
        if (_models is { Count: > 0 } && (string.IsNullOrEmpty(_settings.SelectedModel) || !_models.Any(m => m.Name == _settings.SelectedModel)))
        {
            if (string.IsNullOrEmpty(_settings.SelectedModel)) SelectModel(_models[0].Name);
        }
        await RefreshCaps();
    }

    async Task OpenPicker(Point screen)
    {
        _picker.Open(screen, _models, _settings.SelectedModel, _modelsLoading || _models == null);
        if (_models == null && !_modelsLoading)
        {
            await LoadModels();
            if (_picker.Visible) _picker.Open(screen, _models, _settings.SelectedModel, false);
        }
    }

    void SelectModel(string name)
    {
        _settings.SelectedModel = name;
        if (_screenshot == null) _store.SaveSettings(_settings);
        _composer.ModelName = name;
        _composer.Sync();
        _ = RefreshCaps();
    }

    static bool LevelModel(string model) => model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase);

    async Task RefreshCaps()
    {
        var model = _settings.SelectedModel;
        _caps = string.IsNullOrEmpty(model) ? new() : await _client.Capabilities(model);
        if (IsDisposed || model != _settings.SelectedModel) return;
        _composer.ThinkMode = !_caps.Contains("thinking") ? "" : LevelModel(model) ? "level" : "toggle";
        _composer.ThinkOn = _settings.ThinkEnabled;
        _composer.ThinkLevel = _settings.ThinkLevel;
        _composer.Sync();
    }

    void ThinkMenu(Point screen)
    {
        if (_composer.ThinkMode == "toggle")
        {
            _settings.ThinkEnabled = !_settings.ThinkEnabled;
            _store.SaveSettings(_settings);
            _composer.ThinkOn = _settings.ThinkEnabled;
            _composer.Sync();
            return;
        }
        var menu = new ContextMenuStrip();
        foreach (var lvl in new[] { "low", "medium", "high" })
        {
            var item = new ToolStripMenuItem(char.ToUpper(lvl[0]) + lvl[1..]) { Checked = _settings.ThinkLevel == lvl };
            item.Click += (_, _) =>
            {
                _settings.ThinkLevel = lvl;
                _store.SaveSettings(_settings);
                _composer.ThinkLevel = lvl;
                _composer.Sync();
            };
            menu.Items.Add(item);
        }
        menu.Show(screen);
    }

    // ---------- pages ----------

    void LayoutChatPage()
    {
        int w = _chatPage.ClientSize.Width;
        int colW = Math.Max(Theme.S(240), Math.Min(w - Theme.S(48), Theme.S(768)));
        _composer.Width = colW;
        int ch = _composer.PreferredHeightFor(colW);
        _composer.Height = ch;
        bool empty = _chat.Messages.Count == 0;
        if (empty)
        {
            // New chat: the message box sits in the middle of the page.
            _bottom.Height = _chatPage.ClientSize.Height - _banner.Height;
            _chat.Visible = false;
            int top = Math.Max(Theme.S(20), (_bottom.Height - ch) / 2);
            _composer.Location = new Point((w - colW) / 2, top);
        }
        else
        {
            bool wasHidden = !_chat.Visible;
            _chat.Visible = true;
            _bottom.Height = ch + Theme.S(24);
            _composer.Location = new Point((w - colW) / 2, Theme.S(6));
            if (wasHidden) BeginInvoke(_chat.ScrollToBottom);
        }
        _bottom.Invalidate();
    }

    void ShowChatPage()
    {
        _settingsView.Visible = false;
        _chatPage.Visible = true;
        _sidebar.SettingsSelected = false;
        _sidebar.Invalidate();
        LayoutChatPage();
    }

    void ShowSettings()
    {
        _settingsView.Load(_settings);
        _settingsView.Visible = true;
        _chatPage.Visible = false;
        _sidebar.SettingsSelected = true;
        _sidebar.Invalidate();
        _ = RefreshServerLabels();
    }

    void ReloadSidebar()
    {
        _sidebar.SelectedChatId = _chatId;
        _sidebar.SetChats(_store.Chats());
    }

    void NewChat()
    {
        if (_cts != null) return; // finish or stop the current reply first
        _chatId = null;
        _editing = null;
        _composer.Editing = false;
        _chat.SetMessages(new List<ChatMessage>());
        ReloadSidebar();
        ShowChatPage();
        _composer.FocusBox();
    }

    void OpenChat(string id)
    {
        if (_cts != null && id != _chatId) return;
        _chatId = id;
        _editing = null;
        _composer.Editing = false;
        _chat.SetMessages(_store.Messages(id));
        ReloadSidebar();
        ShowChatPage();
        BeginInvoke(_chat.ScrollToBottom);
        _composer.FocusBox();
    }

    void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Upload multiple files", Multiselect = true,
            Filter = "Pictures and text files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.css;*.js;*.ts;*.py;*.cs;*.java;*.go;*.rs;*.c;*.cpp;*.h;*.sql;*.yaml;*.yml;*.toml;*.log|All files|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _composer.AddFiles(dlg.FileNames);
    }

    /// <summary>Chat mode or Code mode (the switch in the message box).</summary>
    void SetMode(bool code)
    {
        if (code == (_settings.Mode == "code")) return;
        _settings.Mode = code ? "code" : "chat";
        if (_screenshot == null) _store.SaveSettings(_settings);
        _composer.CodeMode = code;
        _composer.Sync();
        _composer.FocusBox();
    }

    /// <summary>Code mode's "+": choose the folder the model works in.</summary>
    bool PickProjectFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Project folder the model may work in", UseDescriptionForTitle = true };
        if (Tools.HasFolder(_settings.ProjectFolder)) dlg.InitialDirectory = _settings.ProjectFolder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        _settings.ProjectFolder = dlg.SelectedPath;
        _store.SaveSettings(_settings);
        _composer.ProjectFolder = _settings.ProjectFolder;
        _composer.Sync();
        return true;
    }

    void BeginEdit(ChatMessage m)
    {
        _editing = m;
        _composer.Editing = true;
        _composer.Text = m.Content;
        _composer.Attachments.Clear();
        _composer.Attachments.AddRange(m.Attachments);
        _composer.Sync();
        _composer.FocusBox();
    }

    void CancelEdit()
    {
        _editing = null;
        _composer.Editing = false;
        _composer.ClearInput();
        _composer.Sync();
    }

    // ---------- sending ----------

    async Task Send()
    {
        var text = _composer.Text.Trim();
        if ((text.Length == 0 && _composer.Attachments.Count == 0) || _cts != null) return;
        var model = _settings.SelectedModel;
        if (string.IsNullOrEmpty(model))
        {
            MessageBox.Show(this, "Pick a model first (the button next to send).", "Lplus");
            return;
        }
        if (_settings.Mode == "code")
        {
            if (!_settings.ToolsEnabled)
            {
                MessageBox.Show(this, "Code mode needs Tools turned on in Settings.", "Lplus");
                return;
            }
            if (_caps.Count > 0 && !_caps.Contains("tools"))
            {
                MessageBox.Show(this, "This model cannot use tools, so it cannot see your project. Pick another model for Code mode.", "Lplus");
                return;
            }
            if (!Tools.HasFolder(_settings.ProjectFolder) && !PickProjectFolder()) return;
        }
        if (_composer.Attachments.Any(a => a.Kind == "image") && _caps.Count > 0 && !_caps.Contains("vision"))
        {
            MessageBox.Show(this, "This model does not support images", "Lplus");
            return;
        }

        if (_chatId == null)
        {
            var title = text.Length > 0 ? text.Replace('\n', ' ') : _composer.Attachments[0].Name;
            if (title.Length > 60) title = title[..60].TrimEnd() + "…";
            _chatId = _store.CreateChat(title);
        }
        var chatId = _chatId;

        if (_editing != null)
        {
            var idx = _chat.Messages.IndexOf(_editing);
            if (idx >= 0)
            {
                _store.DeleteMessagesFrom(chatId, _editing.Id);
                _chat.Messages.RemoveRange(idx, _chat.Messages.Count - idx);
            }
            _editing = null;
            _composer.Editing = false;
        }

        var user = new ChatMessage { Role = "user", Content = text, Model = model, Attachments = _composer.Attachments.ToList() };
        _store.SaveMessage(chatId, user);
        _chat.Messages.Add(user);
        _composer.ClearInput();
        await Reply(chatId, model);
    }

    /// <summary>Asks before a model creates or replaces a file in the project folder.</summary>
    Task<bool> ConfirmWrite(string file, bool exists)
    {
        var ask = exists
            ? $"The model wants to replace this file:\n\n{file}\n\nReplace it?"
            : $"The model wants to create this file:\n\n{file}\n\nCreate it?";
        return Task.FromResult(MessageBox.Show(this, ask, "Lplus", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
    }

    async Task Retry(ChatMessage failed)
    {
        if (_cts != null || _chatId == null) return;
        var idx = _chat.Messages.IndexOf(failed);
        if (idx < 0) return;
        _store.DeleteMessagesFrom(_chatId, failed.Id);
        _chat.Messages.RemoveRange(idx, _chat.Messages.Count - idx);
        await Reply(_chatId, _settings.SelectedModel);
    }

    async Task Reply(string chatId, string model)
    {
        var history = _chat.Messages.Where(m => m.Error == null || m.Content.Length > 0).ToList();
        var bot = new ChatMessage { Role = "assistant", Model = model, Streaming = true };
        _chat.Messages.Add(bot);
        _chat.SetMessages(_chat.Messages);
        ReloadSidebar();
        LayoutChatPage();
        _cts = new CancellationTokenSource();
        _composer.Streaming = true;
        _composer.Sync();
        var ct = _cts.Token;
        try
        {
            // Download the model first if it isn't installed ("Downloading model").
            if (_models != null && !_models.Any(m => m.Name == model || m.Name == model + ":latest"))
            {
                bot.DownloadStatus = "pulling manifest";
                _dirty = bot;
                await _client.Pull(model, (status, done, total) =>
                {
                    bot.DownloadStatus = status;
                    if (total > 0) { bot.DownloadDone = done; bot.DownloadTotal = total; }
                    _dirty = bot;
                }, ct);
                bot.DownloadStatus = null;
                await LoadModels();
            }

            var msgs = new JsonArray();
            foreach (var m in history)
            {
                var content = m.Content;
                foreach (var a in m.Attachments.Where(a => a.Kind == "text"))
                    content += $"\n\n--- {a.Name} ---\n{a.Text}";
                var o = new JsonObject { ["role"] = m.Role, ["content"] = content };
                var imgs = m.Attachments.Where(a => a.Kind == "image" && a.Path != null && File.Exists(a.Path))
                    .Select(a => (JsonNode)Convert.ToBase64String(File.ReadAllBytes(a.Path!))).ToArray();
                if (imgs.Length > 0) o["images"] = new JsonArray(imgs);
                if (m.Role == "assistant" && m.Thinking.Length > 0) o["thinking"] = m.Thinking;
                msgs.Add(o);
            }

            JsonNode? think = null;
            if (_caps.Contains("thinking"))
                think = LevelModel(model) ? JsonValue.Create(_settings.ThinkLevel) : JsonValue.Create(_settings.ThinkEnabled);

            // What the model may do on this PC: models that can call tools get the clock and the project
            // folder; the others are simply told the time.
            JsonArray? tools = null;
            var folder = _settings.Mode == "code" ? _settings.ProjectFolder : "";   // files only in Code mode
            if (_settings.ToolsEnabled)
            {
                if (_caps.Contains("tools")) tools = Tools.Definitions(folder);
                msgs.Insert(0, new JsonObject { ["role"] = "system", ["content"] = Tools.SystemNote(tools != null, folder) });
            }

            async Task<string> RunTool(string name, JsonNode? args)
            {
                var result = await Tools.Run(name, args, folder, ConfirmWrite);
                bool declined = name == "write_file" && result.StartsWith("The user declined");
                bot.Tools.Add(result.StartsWith("Error:") ? $"{Tools.Describe(name, args)} — {result[7..].TrimEnd('.')}"
                    : declined ? $"Did not save {args?["path"]} (you said no)" : Tools.Describe(name, args));
                if (bot.Content.Length > 0 && !bot.Content.EndsWith("\n\n")) bot.Content += "\n\n";
                _dirty = bot;
                return result;
            }

            await Tools.Converse(_client, model, msgs, think, tools, RunTool, (c, t) =>
            {
                if (t.Length > 0)
                {
                    bot.ThinkStart ??= DateTime.Now;
                    bot.Thinking += t;
                }
                if (c.Length > 0)
                {
                    if (bot.ThinkStart != null && bot.ThinkEnd == null) bot.ThinkEnd = DateTime.Now;
                    bot.Content += c;
                }
                _dirty = bot;
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { bot.Error = "Connection lost. Is the model server running?"; }
        catch (Exception ex) { bot.Error = ex.Message; }
        finally
        {
            if (bot.ThinkStart != null && bot.ThinkEnd == null) bot.ThinkEnd = DateTime.Now;
            bot.Streaming = false;
            bot.DownloadStatus = null;
            if (bot.Content.Length == 0 && bot.Thinking.Length == 0 && bot.Error == null && ct.IsCancellationRequested)
                bot.Error = "Stopped.";
            _store.SaveMessage(chatId, bot);
            _cts.Dispose();
            _cts = null;
            _composer.Streaming = false;
            _composer.Sync();
            _dirty = null;
            if (_chatId == chatId) _chat.Refresh(bot);
            ReloadSidebar();
            _ = PollGpu();
        }
    }
}
