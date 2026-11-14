namespace Lplus;

/// <summary>
/// The form that says where the model server is: on this PC (Lplus starts it), or on another computer
/// such as a rented cloud pod, given by its address and, when it needs one, a key.
/// </summary>
sealed class ServerDialog : Form
{
    readonly RadioButton _here = new() { Text = "This PC (Lplus starts the model server)", AutoSize = true };
    readonly RadioButton _there = new() { Text = "Another computer or a cloud pod", AutoSize = true };
    readonly TextBox _address = new() { Dock = DockStyle.Fill, Font = Theme.Body, PlaceholderText = "https://your-pod-id-11434.proxy.runpod.net" };
    readonly TextBox _key = new() { Dock = DockStyle.Fill, Font = Theme.Body, UseSystemPasswordChar = true, PlaceholderText = "Leave empty unless the server asks for one" };
    readonly Label _result = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Height = 44 };
    readonly Button _test = new() { Text = "Test", AutoSize = true };

    public string Address { get; private set; } = "";
    public string Key { get; private set; } = "";

    public ServerDialog(string address, string key)
    {
        Text = "Model server";
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Theme.S(560), Theme.S(330));
        Padding = new Padding(Theme.S(16));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(90)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Wide(Control c, int bottom = 6) { c.Margin = new Padding(0, Theme.S(2), 0, Theme.S(bottom)); grid.Controls.Add(c); grid.SetColumnSpan(c, 2); }
        void Row(string label, Control field, string hint)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(Theme.S(20), Theme.S(7), 0, 0), ForeColor = Theme.TextBody });
            field.Margin = new Padding(0, Theme.S(3), 0, 0);
            grid.Controls.Add(field);
            grid.Controls.Add(new Label { AutoSize = true, Height = 1 });
            grid.Controls.Add(new Label { Text = hint, AutoSize = true, Font = Theme.Tiny, ForeColor = Theme.Faint, Margin = new Padding(0, Theme.S(2), 0, Theme.S(8)) });
        }
        Wide(_here);
        Wide(_there, 4);
        Row("Address", _address, "The server's web address, or host:port. A RunPod pod's address looks like https://<pod id>-11434.proxy.runpod.net");
        Row("Key", _key, "Sent as \"Authorization: Bearer <key>\". Most self-hosted servers have none.");
        var testRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        testRow.Controls.Add(_test);
        grid.Controls.Add(new Label { AutoSize = true, Height = 1 });
        grid.Controls.Add(testRow);
        Wide(_result, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, Theme.S(8), 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        var ok = new Button { Text = "Use this server", AutoSize = true, Padding = new Padding(Theme.S(8), Theme.S(2), Theme.S(8), Theme.S(2)) };
        ok.Click += (_, _) => { if (Read()) { DialogResult = DialogResult.OK; Close(); } };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
        Controls.Add(buttons);
        grid.BringToFront();

        _address.Text = address;
        _key.Text = key;
        (address.Trim().Length > 0 ? _there : _here).Checked = true;
        _here.CheckedChanged += (_, _) => ShowKind();
        _test.Click += async (_, _) => await Test();
        ShowKind();
    }

    void ShowKind()
    {
        _address.Enabled = _key.Enabled = _test.Enabled = _there.Checked;
        _result.Text = _there.Checked ? "" : "Lplus starts the model server installed on this PC and stops it when you quit.";
        _result.ForeColor = Theme.Muted;
    }

    bool Read()
    {
        if (_here.Checked) { Address = ""; Key = ""; return true; }
        var a = _address.Text.Trim();
        if (a.Length == 0 || ServerClient.Normalize(a) == null)
        {
            _result.ForeColor = Theme.ErrorText;
            _result.Text = "Give the server's address, for example https://abc123-11434.proxy.runpod.net or 203.0.113.5:11434";
            return false;
        }
        Address = a;
        Key = _key.Text.Trim();
        return true;
    }

    /// <summary>Asks the server for its version and its models, and says what came back.</summary>
    async Task Test()
    {
        if (!Read()) return;
        _test.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "Asking " + ServerClient.Normalize(Address) + " …";
        try
        {
            var probe = new ServerClient();
            probe.SetServer(Address, Key);
            var version = await probe.Version(TimeSpan.FromSeconds(15));
            if (IsDisposed) return;
            if (version == null)
            {
                _result.ForeColor = Theme.ErrorText;
                _result.Text = "No answer from " + probe.BaseUrl + ". Check that the pod is running, that the model server on it listens on all addresses (OLLAMA_HOST=0.0.0.0) and that its port is open.";
                return;
            }
            int models = 0;
            try { models = (await probe.Models()).Count; } catch { }
            if (IsDisposed) return;
            _result.ForeColor = Color.FromArgb(21, 128, 61);
            _result.Text = $"It answered: version {version}, {models} model{(models == 1 ? "" : "s")} installed."
                + (models == 0 ? " Pick a model's name in Lplus to download one onto it." : "");
        }
        finally { if (!IsDisposed) _test.Enabled = _there.Checked; }
    }
}
