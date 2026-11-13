namespace Lplus;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? screenshot = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--screenshot" && i + 1 < args.Length) screenshot = args[++i];
        if (args.Contains("--quit"))
        {
            // Ask a running copy to quit like the tray's "Quit Lplus" (it also stops the server it started).
            if (EventWaitHandle.TryOpenExisting(@"Local\Lplus.Quit", out var q)) q.Set();
            return 0;
        }
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0 && st + 1 < args.Length)
        {
            Log.Enabled = false;
            return SelfTest.Run(args[st + 1], st + 2 < args.Length ? args[st + 2] : null).GetAwaiter().GetResult();
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;

        Mutex? single = null;
        EventWaitHandle? showSignal = null;
        if (screenshot == null)
        {
            // Single instance: a second launch brings the running window forward (CheckAndFocusExistingInstance).
            single = new Mutex(true, @"Local\Lplus.SingleInstance", out bool first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Lplus.Show");
            if (!first)
            {
                showSignal.Set();
                return 0;
            }
            Paths.Rotate(Paths.AppLog);
            Log.Info("starting Lplus");
        }
        else Log.Enabled = false;

        // A screenshot run gets its own throwaway database so it never changes the person's chats or settings.
        string dbPath = Paths.Database;
        if (screenshot != null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "lplus-shot-" + Environment.ProcessId + ".sqlite");
            if (File.Exists(Paths.Database))
                try
                {
                    using var src = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Paths.Database};Mode=ReadOnly");
                    src.Open();
                    using var dst = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp}");
                    dst.Open();
                    src.BackupDatabase(dst);
                }
                catch { }
            dbPath = tmp;
        }

        using var store = new Store(dbPath);
        var form = new MainForm(store, screenshot);

        if (showSignal != null)
        {
            var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Lplus.Quit");
            new Thread(() =>
            {
                if (quitSignal.WaitOne()) try { form.BeginInvoke(form.QuitFromSignal); } catch { }
            }) { IsBackground = true }.Start();
            var t = new Thread(() =>
            {
                while (showSignal.WaitOne())
                {
                    try { form.BeginInvoke(form.BringToFrontFromTray); } catch { return; }
                }
            }) { IsBackground = true };
            t.Start();
        }

        Application.Run(form);
        Log.Info("Lplus exited");
        single?.ReleaseMutex();
        if (screenshot != null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
        return 0;
    }
}
