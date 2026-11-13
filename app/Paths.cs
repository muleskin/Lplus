namespace Lplus;

/// <summary>Where Lplus keeps its files. Its own folder, shared with no other app.</summary>
static class Paths
{
    public static string DataDir { get; } = Init();
    public static string Database => Path.Combine(DataDir, "db.sqlite");
    public static string AppLog => Path.Combine(DataDir, "app.log");
    public static string ServerLog => Path.Combine(DataDir, "server.log");
    public static string PidFile => Path.Combine(DataDir, "server.pid");
    public static string ImageDir => Directory.CreateDirectory(Path.Combine(DataDir, "images")).FullName;

    static string Init()
    {
        var env = Environment.GetEnvironmentVariable("LPLUS_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lplus");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Keep app.log, app-1.log .. app-5.log.</summary>
    public static void Rotate(string file, int keep = 5)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) return;
            var dir = Path.GetDirectoryName(file)!;
            var name = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            string Nth(int n) => Path.Combine(dir, $"{name}-{n}{ext}");
            if (File.Exists(Nth(keep))) File.Delete(Nth(keep));
            for (int i = keep - 1; i >= 1; i--)
                if (File.Exists(Nth(i))) File.Move(Nth(i), Nth(i + 1));
            File.Move(file, Nth(1));
        }
        catch { /* a locked log is not worth failing over */ }
    }
}

static class Log
{
    static readonly object Gate = new();
    public static bool Enabled = true;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
                File.AppendAllText(Paths.AppLog, $"time={DateTime.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} level={level} msg=\"{msg.Replace("\"", "'")}\"{Environment.NewLine}");
        }
        catch { }
    }
}
