using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lplus;

class Attachment
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "text"; // "image" or "text"
    public string? Path { get; set; }          // image file in the images folder
    public string? Text { get; set; }          // text file contents
}

class ChatMessage
{
    public long Id { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Thinking { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? ThinkStart { get; set; }
    public DateTime? ThinkEnd { get; set; }
    public List<Attachment> Attachments { get; set; } = new();
    public string? Error { get; set; }

    // Live state, not stored.
    public bool Streaming;
    public bool ThinkingOpen;
    public string? DownloadStatus;
    public long DownloadDone, DownloadTotal;
}

class ChatInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
}

class Settings
{
    public bool Expose;
    public bool Browser;
    public string Models = "";
    public int ContextLength = 0;        // 0 = server default
    public bool CloudEnabled = true;
    public bool ThinkEnabled = false;    // think_enabled DEFAULT 0
    public string ThinkLevel = "medium";
    public string SelectedModel = "";
    public bool SidebarOpen = true;
    public int WindowWidth = 1000;
    public int WindowHeight = 720;

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>The settings that change how the model server is started.</summary>
    public bool ServerDiffers(Settings o) =>
        Expose != o.Expose || Browser != o.Browser || Models != o.Models ||
        ContextLength != o.ContextLength || CloudEnabled != o.CloudEnabled;
}

/// <summary>SQLite store for chats, messages and settings.</summary>
sealed class Store : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _gate = new();

    public Store(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Exec(@"
CREATE TABLE IF NOT EXISTS chats (
  id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
  role TEXT NOT NULL, content TEXT NOT NULL DEFAULT '', thinking TEXT NOT NULL DEFAULT '',
  model TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL,
  thinking_time_start TEXT, thinking_time_end TEXT,
  attachments TEXT NOT NULL DEFAULT '[]', error TEXT);
CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id, id);
CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }

    void Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    static string Ts(DateTime d) => d.ToString("o");
    static DateTime ParseTs(object o) => o is string s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.Now;

    // ---- chats ----
    public List<ChatInfo> Chats()
    {
        var list = new List<ChatInfo>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, title, created_at, updated_at FROM chats ORDER BY updated_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ChatInfo { Id = r.GetString(0), Title = r.GetString(1), Created = ParseTs(r.GetValue(2)), Updated = ParseTs(r.GetValue(3)) });
        }
        return list;
    }

    public string CreateChat(string title)
    {
        var id = Guid.NewGuid().ToString();
        var now = Ts(DateTime.Now);
        Exec("INSERT INTO chats(id,title,created_at,updated_at) VALUES($id,$t,$c,$u)", ("$id", id), ("$t", title), ("$c", now), ("$u", now));
        return id;
    }

    public void RenameChat(string id, string title) => Exec("UPDATE chats SET title=$t WHERE id=$id", ("$t", title), ("$id", id));
    public void TouchChat(string id) => Exec("UPDATE chats SET updated_at=$u WHERE id=$id", ("$u", Ts(DateTime.Now)), ("$id", id));

    public void DeleteChat(string id)
    {
        foreach (var m in Messages(id))
            foreach (var a in m.Attachments)
                if (a.Path != null) try { File.Delete(a.Path); } catch { }
        Exec("DELETE FROM messages WHERE chat_id=$id", ("$id", id));
        Exec("DELETE FROM chats WHERE id=$id", ("$id", id));
    }

    // ---- messages ----
    public List<ChatMessage> Messages(string chatId)
    {
        var list = new List<ChatMessage>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, role, content, thinking, model, created_at, thinking_time_start, thinking_time_end, attachments, error FROM messages WHERE chat_id=$c ORDER BY id";
            cmd.Parameters.AddWithValue("$c", chatId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var m = new ChatMessage
                {
                    Id = r.GetInt64(0), Role = r.GetString(1), Content = r.GetString(2), Thinking = r.GetString(3),
                    Model = r.GetString(4), Created = ParseTs(r.GetValue(5)),
                    ThinkStart = r.IsDBNull(6) ? null : ParseTs(r.GetValue(6)),
                    ThinkEnd = r.IsDBNull(7) ? null : ParseTs(r.GetValue(7)),
                    Error = r.IsDBNull(9) ? null : r.GetString(9),
                };
                try { m.Attachments = JsonSerializer.Deserialize<List<Attachment>>(r.GetString(8)) ?? new(); } catch { }
                list.Add(m);
            }
        }
        return list;
    }

    public void SaveMessage(string chatId, ChatMessage m)
    {
        var att = JsonSerializer.Serialize(m.Attachments);
        if (m.Id == 0)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO messages(chat_id,role,content,thinking,model,created_at,thinking_time_start,thinking_time_end,attachments,error)
VALUES($c,$r,$co,$th,$m,$cr,$ts,$te,$a,$e); SELECT last_insert_rowid();";
                Bind(cmd, chatId, m, att);
                m.Id = (long)cmd.ExecuteScalar()!;
            }
        }
        else
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"UPDATE messages SET role=$r, content=$co, thinking=$th, model=$m, created_at=$cr,
thinking_time_start=$ts, thinking_time_end=$te, attachments=$a, error=$e WHERE id=$id AND chat_id=$c";
                Bind(cmd, chatId, m, att);
                cmd.Parameters.AddWithValue("$id", m.Id);
                cmd.ExecuteNonQuery();
            }
        }
        TouchChat(chatId);
    }

    static void Bind(SqliteCommand cmd, string chatId, ChatMessage m, string att)
    {
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$r", m.Role);
        cmd.Parameters.AddWithValue("$co", m.Content);
        cmd.Parameters.AddWithValue("$th", m.Thinking);
        cmd.Parameters.AddWithValue("$m", m.Model);
        cmd.Parameters.AddWithValue("$cr", Ts(m.Created));
        cmd.Parameters.AddWithValue("$ts", m.ThinkStart.HasValue ? Ts(m.ThinkStart.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$te", m.ThinkEnd.HasValue ? Ts(m.ThinkEnd.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$a", att);
        cmd.Parameters.AddWithValue("$e", (object?)m.Error ?? DBNull.Value);
    }

    public void DeleteMessagesFrom(string chatId, long fromId) =>
        Exec("DELETE FROM messages WHERE chat_id=$c AND id>=$id", ("$c", chatId), ("$id", fromId));

    // ---- settings ----
    public Settings LoadSettings()
    {
        var kv = new Dictionary<string, string>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) kv[r.GetString(0)] = r.GetString(1);
        }
        var s = new Settings();
        bool B(string k, bool d) => kv.TryGetValue(k, out var v) ? v == "1" : d;
        int I(string k, int d) => kv.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
        string T(string k, string d) => kv.TryGetValue(k, out var v) ? v : d;
        s.Expose = B("expose", s.Expose);
        s.Browser = B("browser", s.Browser);
        s.Models = T("models", s.Models);
        s.ContextLength = I("context_length", s.ContextLength);
        s.CloudEnabled = B("cloud_enabled", s.CloudEnabled);
        s.ThinkEnabled = B("think_enabled", s.ThinkEnabled);
        s.ThinkLevel = T("think_level", s.ThinkLevel);
        s.SelectedModel = T("selected_model", s.SelectedModel);
        s.SidebarOpen = B("sidebar_open", s.SidebarOpen);
        s.WindowWidth = I("window_width", s.WindowWidth);
        s.WindowHeight = I("window_height", s.WindowHeight);
        return s;
    }

    public void SaveSettings(Settings s)
    {
        void P(string k, string v) => Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", k), ("$v", v));
        static string B(bool b) => b ? "1" : "0";
        P("expose", B(s.Expose));
        P("browser", B(s.Browser));
        P("models", s.Models);
        P("context_length", s.ContextLength.ToString());
        P("cloud_enabled", B(s.CloudEnabled));
        P("think_enabled", B(s.ThinkEnabled));
        P("think_level", s.ThinkLevel);
        P("selected_model", s.SelectedModel);
        P("sidebar_open", B(s.SidebarOpen));
        P("window_width", s.WindowWidth.ToString());
        P("window_height", s.WindowHeight.ToString());
    }

    public void Dispose() => _db.Dispose();
}
