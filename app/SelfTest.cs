using System.Text;
using System.Text.Json.Nodes;

namespace Lplus;

/// <summary>
/// "--selftest &lt;report.txt&gt; [model]": starts the model server the way the app does, lists models, asks one short
/// question, saves a chat round-trip through the store, stops the server it started and writes a report.
/// </summary>
static class SelfTest
{
    public static async Task<int> Run(string report, string? model)
    {
        var sb = new StringBuilder();
        void P(string s) { sb.AppendLine(s); File.WriteAllText(report, sb.ToString()); }
        int fails = 0;
        void Check(bool ok, string what) { P((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

        try
        {
            var env = ServerManager.Environment_(new Settings { Expose = true, Models = "", ContextLength = 8192, CloudEnabled = false });
            Check(env["OLLAMA_HOST"] == "0.0.0.0" && env["OLLAMA_CONTEXT_LENGTH"] == "8192" && env["OLLAMA_NO_CLOUD"] == "1" && !env.ContainsKey("OLLAMA_MODELS"),
                "server environment from settings: " + string.Join(", ", env.Select(kv => kv.Key + "=" + kv.Value)));

            var client = new ServerClient();
            using var server = new ServerManager(client, new Settings());
            await server.Start();
            var v = await client.Version();
            Check(v != null, $"server answers at {client.BaseUrl} (version {v ?? "none"}, {(server.External ? "already running" : "started by Lplus")}) {server.Error}");
            if (v == null) return 1;

            var models = await client.Models();
            Check(models.Count > 0, $"models listed: {string.Join(", ", models.Select(m => m.Name))}");
            model ??= models.OrderBy(m => m.Size).FirstOrDefault(m => !m.Name.Contains("embed"))?.Name;
            if (model == null) return 1;

            var caps = await client.Capabilities(model);
            P($"INFO capabilities of {model}: {string.Join(", ", caps)}");

            var reply = new StringBuilder();
            var msgs = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with exactly one word: ready" } };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var t0 = DateTime.Now;
            await client.Chat(model, msgs, caps.Contains("thinking") ? JsonValue.Create(false) : null, (c, _) => reply.Append(c), cts.Token);
            Check(reply.Length > 0, $"streamed reply from {model} in {(DateTime.Now - t0).TotalSeconds:0.0}s: \"{reply.ToString().Trim()}\"");

            var loaded = await GpuStatus.Loaded(client);
            var devices = GpuStatus.Devices();
            // (No control is created here: that would make the awaits below wait on this blocked thread.)
            var (_, barLeft, barRight, _) = StatusBar.Describe(loaded, devices);
            P($"INFO status bar would read: \"{barLeft}\" | \"{barRight}\"");
            var lm = loaded?.FirstOrDefault();
            Check(lm != null, $"status bar data: loaded={lm?.Name} size={lm?.Size} in GPU memory={lm?.SizeVram} " +
                $"({(lm == null || lm.Size == 0 ? 0 : 100 * lm.SizeVram / lm.Size)}% GPU); devices: {string.Join("; ", devices.Select(d => d.Description + " " + d.Library + " " + d.Total))}");

            var db = Path.Combine(Path.GetTempPath(), "lplus-selftest-" + Environment.ProcessId + ".sqlite");
            using (var store = new Store(db))
            {
                var id = store.CreateChat("Self test");
                store.SaveMessage(id, new ChatMessage { Role = "user", Content = "hi" });
                store.SaveMessage(id, new ChatMessage { Role = "assistant", Content = reply.ToString(), Model = model });
                var back = store.Messages(id);
                Check(back.Count == 2 && back[1].Content == reply.ToString(), "chat saved and read back from the database");
                var s = new Settings { ContextLength = 32768, Expose = true };
                store.SaveSettings(s);
                var s2 = store.LoadSettings();
                Check(s2.ContextLength == 32768 && s2.Expose, "settings saved and read back");
                store.DeleteChat(id);
                Check(store.Chats().Count == 0, "chat deleted");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }

            var blocks = Markdown.Parse("# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n\n```python\nprint(1)\n```");
            Check(blocks.Count == 5 && blocks[0].Kind == BlockKind.Heading && blocks[4].Kind == BlockKind.Code && blocks[1].Runs.Any(r => r.Style == Span.Bold),
                "markdown blocks: " + string.Join(", ", blocks.Select(b => b.Kind)));

            bool ours = server.Owned;
            server.Dispose();
            await Task.Delay(1500);
            if (ours) Check(await client.Version() == null, "server stopped on quit");
        }
        catch (Exception ex) { P("FAIL exception: " + ex); fails++; }
        P(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
