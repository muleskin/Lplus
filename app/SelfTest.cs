using System.Text;
using System.Text.Json.Nodes;

namespace Lplus;

/// <summary>
/// "--selftest &lt;report.txt&gt; [model]": starts the model server the way the app does, lists models, asks one short
/// question, saves a chat round-trip through the store, stops the server it started and writes a report.
/// </summary>
static class SelfTest
{
    /// <summary>
    /// "--toolrun &lt;report.txt&gt; &lt;model&gt; &lt;folder&gt; &lt;question&gt;": asks one question with the tools on and
    /// writes down every tool step and the answer. Files are never written (every write is declined).
    /// </summary>
    public static async Task<int> ToolRun(string report, string model, string folder, string question)
    {
        var sb = new StringBuilder();
        void P(string s) { sb.AppendLine(s); File.WriteAllText(report, sb.ToString()); }
        try
        {
            var client = new ServerClient();
            using var server = new ServerManager(client, new Settings());
            await server.Start();
            var caps = await client.Capabilities(model);
            bool canCall = caps.Contains("tools");
            P($"model {model}; can call tools: {canCall}; folder: {folder}");
            var msgs = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Tools.SystemNote(canCall, folder) },
                new JsonObject { ["role"] = "user", ["content"] = question },
            };
            async Task<string> RunTool(string name, JsonNode? args)
            {
                var result = await Tools.Run(name, args, folder, (f, e) => { P($"  (declined write of {f})"); return Task.FromResult(false); });
                P($"TOOL {name} {args?.ToJsonString()} -> {result.Length} chars: {(result.Length > 300 ? result[..300] + "…" : result).Replace("\n", " | ")}");
                return result;
            }
            var said = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await Tools.Converse(client, model, msgs, caps.Contains("thinking") ? JsonValue.Create(false) : null,
                canCall ? Tools.Definitions(folder) : null, RunTool, (c, _) => said.Append(c), cts.Token);
            P("ANSWER:");
            P(said.ToString());
            return 0;
        }
        catch (Exception ex) { P("FAILED: " + ex.Message); return 1; }
    }

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

            // Tools: the clock, and files inside a throwaway project folder.
            var proj = Path.Combine(Path.GetTempPath(), "lplus-selftest-proj-" + Environment.ProcessId);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            Directory.CreateDirectory(Path.Combine(proj, "bin"));
            File.WriteAllText(Path.Combine(proj, "src", "app.py"), "def greet():\n    return 'hello from the old app'\n");
            File.WriteAllText(Path.Combine(proj, "bin", "skip.txt"), "greet");
            Task<bool> Yes(string f, bool e) => Task.FromResult(true);
            Task<bool> No(string f, bool e) => Task.FromResult(false);
            JsonNode A(params (string k, string v)[] kv) { var o = new JsonObject(); foreach (var (k, v2) in kv) o[k] = v2; return o; }

            var clock = await Tools.Run("get_current_time", null, "", Yes);
            Check(clock.Contains(DateTime.Now.Year.ToString()) && clock.Contains("UTC"), "clock tool: " + clock);
            Check(Tools.Definitions("").Count == 1 && Tools.Definitions(proj).Count == 5, "tools offered: clock only without a project folder, 5 with one");
            var asText = Tools.TextCalls("{\n  \"name\": \"list_files\",\n  \"arguments\": {}\n}\n\n{\n  \"name\": \"read_file\",\n  \"arguments\": {\n    \"path\": \"a {b}.js\"\n  }\n}", Tools.Definitions(proj));
            Check(asText.Count == 2 && asText[1]["function"]?["arguments"]?["path"]?.GetValue<string>() == "a {b}.js"
                && Tools.TextCalls("{\"name\": \"list_files\"} and some words", Tools.Definitions(proj)).Count == 0
                && Tools.TextCalls("{\"answer\": 42}", Tools.Definitions(proj)).Count == 0,
                "tool requests written as text are recognised (several in a row, spread over lines); other text is left alone");
            var listed = await Tools.Run("list_files", A(), proj, Yes);
            Check(listed.Contains("src/app.py") && !listed.Contains("skip.txt"), "list_files: " + listed.Replace("\n", " | "));
            var read = await Tools.Run("read_file", A(("path", "src/app.py")), proj, Yes);
            Check(read.Contains("hello from the old app"), "read_file returns the file's text");
            var found = await Tools.Run("search_files", A(("query", "GREET")), proj, Yes);
            Check(found.Contains("src/app.py:1:") && !found.Contains("skip.txt"), "search_files: " + found.Replace("\n", " | "));
            var refused = await Tools.Run("write_file", A(("path", "src/app.py"), ("content", "x")), proj, No);
            Check(refused.StartsWith("The user declined") && File.ReadAllText(Path.Combine(proj, "src", "app.py")).Contains("old app"), "write_file leaves the file alone when declined");
            var wrote = await Tools.Run("write_file", A(("path", "src/new/util.py"), ("content", "X = 1\n")), proj, Yes);
            Check(wrote.StartsWith("Created") && File.ReadAllText(Path.Combine(proj, "src", "new", "util.py")) == "X = 1\n", "write_file: " + wrote);
            var outside = await Tools.Run("read_file", A(("path", "..\\..\\outside.txt")), proj, Yes);
            var outside2 = await Tools.Run("write_file", A(("path", "C:\\Windows\\lplus-test.txt"), ("content", "x")), proj, Yes);
            Check(outside.Contains("outside the project folder") && outside2.Contains("outside the project folder"), "paths outside the project folder are refused");

            // Live: a model that can call tools reads the clock and the project's code.
            if (caps.Contains("tools"))
            {
                var used = new List<string>();
                async Task<string> RunTool(string name, JsonNode? args) { used.Add(name); return await Tools.Run(name, args, proj, No); }
                JsonNode? noThink = caps.Contains("thinking") ? JsonValue.Create(false) : null;

                var ask = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = Tools.SystemNote(true, proj) },
                    new JsonObject { ["role"] = "user", ["content"] = "What time is it right now?" },
                };
                var said = new StringBuilder();
                await Tools.Converse(client, model, ask, noThink, Tools.Definitions(proj), RunTool, (c, _) => said.Append(c), cts.Token);
                Check(used.Contains("get_current_time") && said.Length > 0, $"model asked for the time with [{string.Join(", ", used)}] and said: \"{said.ToString().Trim().Replace("\n", " ")}\"");

                used.Clear();
                said.Clear();
                ask = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = Tools.SystemNote(true, proj) },
                    new JsonObject { ["role"] = "user", ["content"] = "Look at my project and tell me exactly what text the greet function in app.py returns." },
                };
                await Tools.Converse(client, model, ask, noThink, Tools.Definitions(proj), RunTool, (c, _) => said.Append(c), cts.Token);
                Check(used.Any(u => u is "read_file" or "search_files" or "list_files") && said.Length > 0, $"model looked at the project with [{string.Join(", ", used)}] and said: \"{said.ToString().Trim().Replace("\n", " ")}\"");
            }
            else P($"INFO {model} cannot call tools, so the live tool checks were skipped (it is told the time instead)");
            try { Directory.Delete(proj, true); } catch { }

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

            // Copying from the conversation and the Up/Down prompt history. (Controls are created only here,
            // after the last await: see the note above.)
            // They need a UI (single-threaded apartment) thread of their own: by now this code runs on a pool thread.
            var ui = new Thread(() =>
            {
              try
              {
            using (var view = new ChatView { Size = new Size(700, 500) })
            {
                var para = "A first paragraph that is long enough to wrap over more than one line of the column, so that copying it has to join the wrapped lines back into one line.";
                view.SetMessages(new List<ChatMessage>
                {
                    new() { Role = "user", Content = "show me" },
                    new() { Role = "assistant", Model = "m", Tools = { "Checked the time" }, Content = para + "\n\n- one\n- two\n\n```python\ndef f():\n    return 1\n\nprint(f())\n```" },
                });
                Check(!view.HasSelection && view.SelectedText() == "", "nothing is selected to begin with");
                view.SelectAll();
                var copied = view.SelectedText();
                Check(copied.StartsWith("show me\n\n") && copied.Contains("Checked the time") && copied.Contains(para) && copied.Contains("one") && copied.Contains("two")
                    && copied.Contains("def f():\n    return 1\n\nprint(f())") && !copied.Contains("Copy") && !copied.Contains("python"),
                    "select all copies the conversation's text, wrapped lines joined and code kept line for line: " + copied.Replace("\n", "⏎"));
            }
            var hdb = Path.Combine(Path.GetTempPath(), "lplus-selftest-h-" + Environment.ProcessId + ".sqlite");
            using (var store = new Store(hdb))
            using (var box = new Composer { Width = 600 })
            {
                var id = store.CreateChat("History");
                foreach (var q in new[] { "first", "second", "first", "third\nline two" })
                    store.SaveMessage(id, new ChatMessage { Role = "user", Content = q });
                store.SaveMessage(id, new ChatMessage { Role = "assistant", Content = "an answer" });
                var past = store.RecentPrompts();
                Check(string.Join("|", past) == "third\nline two|first|second", "earlier prompts, newest first, each once: " + string.Join(" | ", past).Replace("\n", "⏎"));
                box.History = () => new List<string> { "newest", "middle", "oldest" };
                var walk = new List<string>();
                void Step(bool up) { box.BrowsePast(up); walk.Add(box.Text.Length == 0 ? "(empty)" : box.Text); }
                Step(true); Step(true); Step(true); Step(true); Step(false); Step(false); Step(false);
                Check(string.Join(" ", walk) == "newest middle oldest oldest middle newest (empty)",
                    "Up walks back through earlier prompts and Down comes forward again: " + string.Join(" → ", walk));
                box.Text = "typed by hand";
                Check(!box.BrowsePast(true) && box.Text == "typed by hand", "Up leaves a message that is being typed alone");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(hdb); } catch { }
              }
              catch (Exception ex) { P("FAIL exception in the window checks: " + ex.Message); fails++; }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
        }
        catch (Exception ex) { P("FAIL exception: " + ex); fails++; }
        P(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
