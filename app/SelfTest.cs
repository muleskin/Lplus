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
    /// "--mcp-check &lt;report.txt&gt;": connects to every server in mcp.json and writes down how each one
    /// answered (its protocol, what it offers and its tools). No tool is run.
    /// </summary>
    public static async Task<int> McpCheck(string report)
    {
        var sb = new StringBuilder();
        void P(string s) { sb.AppendLine(s); File.WriteAllText(report, sb.ToString()); }
        using var hub = new McpHub();
        P("Servers in " + Paths.McpConfig);
        await hub.Load(Paths.McpConfig);
        if (hub.ConfigError != null) P(hub.ConfigError);
        foreach (var c in hub.Configs)
        {
            var (status, ok, _) = hub.StatusOf(c);
            var conn = hub.Connection(c.Name);
            P($"{c.Name}: {(ok ? "connected" : status)}");
            P($"  {(c.IsHttp ? "Address" : "Command")}: {c.Summary}");
            if (conn is not { Ready: true }) continue;
            P($"  Server: {conn.ServerName}; protocol {conn.ProtocolVersion}; offers {string.Join(", ", conn.Capabilities)}");
            P($"  Tools ({conn.Tools.Count}): {string.Join(", ", conn.Tools.Select(t => t.name))}");
        }
        return hub.Configs.Count > 0 && hub.Configs.Where(c => !c.Disabled).All(c => hub.StatusOf(c).ok) ? 0 : 1;
    }

    static JsonArray WithMcp(JsonArray tools, McpHub hub)
    {
        foreach (var d in hub.Definitions()) tools.Add(d?.DeepClone());
        return tools;
    }

    /// <summary>
    /// "--toolrun &lt;report.txt&gt; &lt;model&gt; &lt;folder&gt; &lt;question&gt;": asks one question with the tools on and
    /// writes down every tool step and the answer. Web lookups are allowed; no file is written and no command is run, unless the word "apply" follows the question (for a throwaway folder).
    /// </summary>
    public static async Task<int> ToolRun(string report, string model, string folder, string question, bool apply = false)
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
            // The MCP servers in mcp.json take part too; their tools run only with "apply".
            using var hub = new McpHub();
            await hub.Load(Paths.McpConfig);
            if (hub.ConfigError != null) P(hub.ConfigError);
            foreach (var (name, status, _) in hub.Status()) P($"MCP server {name}: {status}");
            var msgs = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Tools.SystemNote(canCall, folder, true) },
                new JsonObject { ["role"] = "user", ["content"] = question },
            };
            bool changedFiles = false;
            async Task<string> RunTool(string name, JsonNode? args)
            {
                if (hub.Find(name) is { } mt)
                {
                    var answer = apply ? await mt.Server.Call(mt.Name, args) : Tools.Declined;
                    P($"MCP {mt.Server.Config.Name}: {mt.Name} {args?.ToJsonString()} -> {(answer.Length > 300 ? answer[..300] + "…" : answer).Replace("\n", " | ")}");
                    return answer;
                }
                var result = await Tools.Run(name, args, folder, (f, e) => { P($"  ({(apply ? "allowed" : "declined")} write of {f})"); return Task.FromResult(apply); },
                    true, (c, f) => { P($"  ({(apply ? "allowed" : "declined")} command {c})"); return Task.FromResult(apply); },
                    f => changedFiles = true);
                P($"TOOL {name} {args?.ToJsonString()} -> {result.Length} chars: {(result.Length > 300 ? result[..300] + "…" : result).Replace("\n", " | ")}");
                return result;
            }
            var said = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            async Task<string?> Check()
            {
                if (!apply || !changedFiles) return null;
                changedFiles = false;
                var problem = await Tools.Verify(folder, (c, f) => Task.FromResult(true), text => { if (!text.EndsWith("…")) P("CHECK " + text); });
                if (problem != null) P("  (sent back to the model: " + problem.Split('\n')[0] + ")");
                return problem;
            }
            await Tools.Converse(client, model, msgs, caps.Contains("thinking") ? JsonValue.Create(false) : null,
                canCall ? WithMcp(Tools.Definitions(folder, true), hub) : null, RunTool, (c, _) => said.Append(c), cts.Token,
                Tools.HasFolder(folder) ? Check : null, Tools.HasFolder(folder) ? 16384 : 0);
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
            Check(Tools.Definitions("").Count == 1 && Tools.Definitions("", web: true).Count == 3 && Tools.Definitions(proj).Count == 7 && Tools.Definitions(proj, web: true).Count == 9,
                "tools offered: the clock alone, +2 with web access, +6 with a project folder");
            var asText = Tools.TextCalls("{\n  \"name\": \"list_files\",\n  \"arguments\": {}\n}\n\n{\n  \"name\": \"read_file\",\n  \"arguments\": {\n    \"path\": \"a {b}.js\"\n  }\n}", Tools.Definitions(proj));
            Check(asText.Count == 2 && asText[1]["function"]?["arguments"]?["path"]?.GetValue<string>() == "a {b}.js"
                && Tools.TextCalls("{\"name\": \"list_files\"} and some words", Tools.Definitions(proj)).Count == 0
                && Tools.TextCalls("{\"answer\": 42}", Tools.Definitions(proj)).Count == 0,
                "tool requests written as text are recognised (several in a row, spread over lines); other text is left alone");
            var mixed = Tools.EmbeddedCalls("Let me look.\n\n```json\n{\n  \"name\": \"read_file\",\n  \"arguments\": { \"path\": \"a.py\" }\n}\n```\n\nThen I will list:\n{\"name\": \"list_files\", \"arguments\": {}}Done. An example: {\"name\": \"read_file\"} and {\"other\": 1}", Tools.Definitions(proj));
            Check(mixed.Count == 2 && mixed[0]["function"]?["arguments"]?["path"]?.GetValue<string>() == "a.py" && mixed[1]["function"]?["name"]?.GetValue<string>() == "list_files",
                "tool requests mixed in with talk are found, and JSON in the middle of a sentence is not taken for one");
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

            // Changing part of a file.
            var edited = await Tools.Run("edit_file", A(("path", "src/app.py"), ("old_text", "    return 'hello from the old app'"), ("new_text", "    return 'hello from the new app'")), proj, Yes);
            Check(edited.StartsWith("Edited src/app.py at line 2") && File.ReadAllText(Path.Combine(proj, "src", "app.py")) == "def greet():\n    return 'hello from the new app'\n", "edit_file: " + edited);
            File.WriteAllText(Path.Combine(proj, "src", "win.cs"), "class A\r\n{\r\n    int X = 1;\r\n    int Y = 2;\r\n}\r\n");
            var loose = await Tools.Run("edit_file", A(("path", "win.cs"), ("old_text", "int X = 1;\nint Y = 2;"), ("new_text", "    int X = 10;\n    int Y = 20;")), proj, Yes);
            Check(loose.StartsWith("Edited") && File.ReadAllText(Path.Combine(proj, "src", "win.cs")) == "class A\r\n{\r\n    int X = 10;\r\n    int Y = 20;\r\n}\r\n",
                "edit_file finds the lines despite different indentation and keeps the file's line endings: " + loose);
            var missing = await Tools.Run("edit_file", A(("path", "src/app.py"), ("old_text", "not in the file"), ("new_text", "x")), proj, Yes);
            File.WriteAllText(Path.Combine(proj, "src", "twice.txt"), "same\nsame\n");
            var twice = await Tools.Run("edit_file", A(("path", "src/twice.txt"), ("old_text", "same"), ("new_text", "x")), proj, Yes);
            var editNo = await Tools.Run("edit_file", A(("path", "src/app.py"), ("old_text", "new app"), ("new_text", "x")), proj, No);
            Check(missing.Contains("was not found") && twice.Contains("more than once") && editNo == Tools.Declined && File.ReadAllText(Path.Combine(proj, "src", "twice.txt")) == "same\nsame\n",
                "edit_file refuses text that is missing or appears twice, and leaves the file alone when declined");
            File.WriteAllText(Path.Combine(proj, "src", "app.py"), "def greet():\n    return 'hello from the old app'\n");

            // Running a command in the project folder.
            Task<bool> YesCmd(string c, string f) => Task.FromResult(true);
            var ran = await Tools.Run("run_command", A(("command", "echo built ok && cd")), proj, Yes, false, YesCmd);
            Check(ran.StartsWith("Exit code 0 (success)") && ran.Contains("built ok") && ran.Contains(Path.GetFileName(proj)), "run_command runs in the project folder: " + ran.Replace("\r", "").Replace("\n", " | "));
            var failed = await Tools.Run("run_command", A(("command", "echo oops 1>&2 && exit /b 3")), proj, Yes, false, YesCmd);
            Check(failed.StartsWith("Exit code 3 (failed)") && failed.Contains("oops"), "run_command reports a failure and its error output: " + failed.Replace("\r", "").Replace("\n", " | "));
            var slow = await Tools.RunCommand(proj, "ping -n 30 127.0.0.1", TimeSpan.FromSeconds(2));
            Check(slow.StartsWith("Stopped:"), "a command that runs too long is stopped: " + slow.Split('\n')[0]);
            var notAsked = await Tools.Run("run_command", A(("command", "echo hi")), proj, Yes);
            var cmdNo = await Tools.Run("run_command", A(("command", "echo hi")), proj, Yes, false, (c, f) => Task.FromResult(false));
            Check(notAsked == Tools.Declined && cmdNo == Tools.Declined, "run_command does nothing unless the person agrees");
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")))
            {
                var cs = Path.Combine(proj, "cs");
                Directory.CreateDirectory(cs);
                File.WriteAllText(Path.Combine(cs, "t.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(cs, "Program.cs"), "System.Console.WriteLine(1 +);\n");
                var bad = await Tools.Run("run_command", A(("command", "dotnet build cs")), proj, Yes, false, YesCmd);
                await Tools.Run("edit_file", A(("path", "cs/Program.cs"), ("old_text", "1 +"), ("new_text", "1 + 1")), proj, Yes);
                var good = await Tools.Run("run_command", A(("command", "dotnet build cs")), proj, Yes, false, YesCmd);
                Check(bad.Contains("(failed)") && bad.Contains("error CS") && good.Contains("(success)"),
                    "a compiler run shows the error, and passes after the fix: " + bad.Split('\n').FirstOrDefault(l => l.Contains("error CS"))?.Trim());
            }
            else P("INFO the .NET SDK is not installed, so the compiler check was skipped");

            // Working on a project as a whole: what kind it is, which commands are ordinary build tools, undo, trimming.
            var kinds = Path.Combine(proj, "kinds");
            string Kind(string name, params (string file, string text)[] files)
            {
                var d = Path.Combine(kinds, name);
                foreach (var (file, text) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(d, file))!); File.WriteAllText(Path.Combine(d, file), text); }
                var pi = Tools.Project(d);
                return $"{pi.Kind}|{pi.Build}|{pi.Test}";
            }
            var k1 = Kind("net", ("app/App.csproj", "<Project/>"), ("tests/T.csproj", "<PackageReference Include=\"Microsoft.NET.Test.Sdk\"/>"));
            var k2 = Kind("net1", ("One.csproj", "<Project/>"));
            var k3 = Kind("node", ("package.json", "{\"scripts\":{\"build\":\"tsc\",\"test\":\"echo \\\"Error: no test specified\\\" && exit 1\"}}"));
            var k4 = Kind("py", ("main.py", "print(1)"), ("tests/test_a.py", "def test_a(): pass"));
            var k5 = Kind("rust", ("Cargo.toml", "[package]"));
            var k6 = Kind("over", ("Cargo.toml", "[package]"), (".lplus.json", "{\"build\":\"cargo check\",\"test\":\"\"}"));
            var k7 = Kind("none", ("notes.txt", "x"));
            Check(k1 == ".NET|dotnet build app/App.csproj|dotnet test tests/T.csproj" && k2 == ".NET|dotnet build|" && k3 == "Node.js|npm run build|"
                && k4 == "Python|python -m compileall -q .|python -m pytest -q" && k5 == "Rust|cargo build|cargo test" && k6 == "Rust|cargo check|" && k7 == "||",
                $"projects are recognised with their build and test commands: {k1}; {k2}; {k3}; {k4}; {k5}; overridden {k6}");
            var rustDir = Path.Combine(kinds, "rust");
            bool Ok(string c) => Tools.IsBuildCommand(c, rustDir);
            Check(Ok("cargo build") && Ok("cargo test -- --nocapture") && Ok("dotnet build app/App.csproj") && Ok("npm run build") && Ok("python -m pytest -q") && Ok("python tools/gen.py") && Ok("make")
                && !Ok("cargo build && del /q *") && !Ok("dotnet build > out.txt") && !Ok("del /q src") && !Ok("npm install left-pad") && !Ok("python -c \"import os\"") && !Ok("cargo publish")
                && !Ok("dotnet build ..\\other\\x.csproj") && !Ok("dotnet build C:\\other\\x.csproj") && !Ok("powershell -c x") && !Ok("git push") && !Ok("curl http://x"),
                "ordinary build and test commands are told apart from everything else");
            var undoDir = Path.Combine(proj, "undo-copy");
            var appPy = Path.Combine(proj, "src", "app.py");
            var before = File.ReadAllText(appPy);
            void Keep(string f) => Tools.KeepCopy(undoDir, proj, f);
            await Tools.Run("edit_file", A(("path", "src/app.py"), ("old_text", "old app"), ("new_text", "changed once")), proj, Yes, false, null, Keep);
            await Tools.Run("edit_file", A(("path", "src/app.py"), ("old_text", "changed once"), ("new_text", "changed twice")), proj, Yes, false, null, Keep);
            await Tools.Run("write_file", A(("path", "src/made/new.py"), ("content", "x = 1\n")), proj, Yes, false, null, Keep);
            var listed2 = Tools.ChangedFiles(undoDir);
            bool wasChanged = File.ReadAllText(appPy).Contains("changed twice") && File.Exists(Path.Combine(proj, "src", "made", "new.py"));
            int undone = Tools.UndoChanges(undoDir);
            Check(wasChanged && listed2.SequenceEqual(new[] { "src/app.py", "src/made/new.py" }) && undone == 2 && File.ReadAllText(appPy) == before && !File.Exists(Path.Combine(proj, "src", "made", "new.py")) && !Directory.Exists(undoDir),
                "a reply's file changes are undone: the changed file is as it was and the created one is gone");
            var part = await Tools.Run("read_file", A(("path", "src/app.py"), ("start_line", "2"), ("end_line", "2")), proj, Yes);
            Check(part.StartsWith("(Lines 2 to 2 of") && part.Contains("return 'hello from the old app'") && !part.Contains("def greet"), "read_file reads a range of lines: " + part.Replace("\n", " | "));
            var longTalk = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "the task" } };
            for (int i = 0; i < 12; i++)
            {
                longTalk.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray { new JsonObject { ["function"] = new JsonObject { ["name"] = "write_file", ["arguments"] = new JsonObject { ["path"] = "a.txt", ["content"] = new string('c', 3000) } } } } });
                longTalk.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = "write_file", ["content"] = new string('r', 4000) + i });
            }
            Tools.Compact(longTalk, 20000);
            int size = longTalk.Sum(m => m!["content"]!.GetValue<string>().Length + (m["tool_calls"]?.ToJsonString().Length ?? 0));
            Check(size < 60000 && longTalk[0]!["content"]!.GetValue<string>() == "the task" && longTalk[^1]!["content"]!.GetValue<string>().EndsWith("11") && longTalk[2]!["content"]!.GetValue<string>().Contains("dropped to save space"),
                $"a long job is trimmed to fit the model's memory: old results shortened, the task and the latest steps kept ({size} characters left)");

            // The web: reading a results page and a web page (no network), then the real thing.
            var sample = "<div><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fdocs%3Fa%3D1&amp;rut=x\">Example <b>Docs</b></a>"
                + "<a class=\"result__snippet\" href=\"x\">How to &amp; why</a></div>";
            var parsed = Tools.ParseSearch(sample);
            Check(parsed.Count == 1 && parsed[0] == ("Example Docs", "https://example.org/docs?a=1", "How to & why"), "search results are read from the results page");
            var pageText = Tools.PageText("<html><head><title>Docs</title><style>p{}</style></head><body><script>var x=1;</script><h1>Title</h1><p>One&nbsp;two <b>three</b></p><ul><li>a</li><li>b</li></ul></body></html>");
            Check(pageText == "Docs\nTitle\nOne two three\na\nb", "a web page is reduced to its text: " + pageText.Replace("\n", " | "));
            var off = await Tools.Run("web_search", A(("query", "x")), proj, Yes);
            var local = await Tools.Run("fetch_url", A(("url", "http://127.0.0.1:11434/api/tags")), proj, Yes, true);
            var local2 = await Tools.Run("fetch_url", A(("url", "http://192.168.1.1/")), proj, Yes, true);
            var notWeb = await Tools.Run("fetch_url", A(("url", "file:///C:/Windows/win.ini")), proj, Yes, true);
            Check(off.Contains("turned off") && local.Contains("not available") && local2.Contains("not available") && notWeb.Contains("not a web address"),
                "web tools are refused when web access is off, and never open this PC, the local network or files");
            var page = await Tools.Run("fetch_url", A(("url", "https://example.com")), proj, Yes, true);
            if (page.StartsWith("Error: could not open")) P("INFO this PC seems to be offline, so the live web checks were skipped: " + page);
            else
            {
                Check(page.Contains("Example Domain"), "fetch_url reads a live page: " + page.Replace("\n", " | "));
                var hits = await Tools.Run("web_search", A(("query", "dotnet build command documentation")), proj, Yes, true);
                Check(hits.StartsWith("1. ") && hits.Contains("http"), "web_search returns live results: " + hits.Split('\n')[0] + " | " + hits.Split('\n')[1].Trim());
            }

            // MCP servers: one started as a program (this exe's own test server), one over HTTP, one that cannot start.
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; }
            using var web = new System.Net.HttpListener();
            web.Prefixes.Add($"http://localhost:{port}/mcp/");
            web.Start();
            bool sawSession = false, sawHeader = false, sawVersion = false;
            var serving = Task.Run(async () =>
            {
                while (web.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await web.GetContextAsync(); } catch { break; }
                    using var body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var msg = JsonNode.Parse(await body.ReadToEndAsync())!;
                    sawSession |= ctx.Request.Headers["Mcp-Session-Id"] == "session-1";
                    sawHeader |= ctx.Request.Headers["X-Test-Key"] == "abc";
                    sawVersion |= ctx.Request.Headers["MCP-Protocol-Version"] == McpConnection.Protocol;
                    var reply = McpTestServer.Handle(msg);
                    ctx.Response.Headers["Mcp-Session-Id"] = "session-1";
                    if (reply == null) { ctx.Response.StatusCode = 202; ctx.Response.Close(); continue; }
                    // Tool answers go out as an event stream, the rest as plain JSON: a client must take both.
                    bool stream = msg["method"]?.GetValue<string>() == "tools/call";
                    var bytes = Encoding.UTF8.GetBytes(stream ? "event: message\ndata: " + reply.ToJsonString() + "\n\n" : reply.ToJsonString());
                    ctx.Response.ContentType = stream ? "text/event-stream" : "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
            var mcpFile = Path.Combine(proj, "mcp.json");
            File.WriteAllText(mcpFile, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["local"] = new JsonObject { ["command"] = Environment.ProcessPath, ["args"] = new JsonArray("--mcp-test-server") },
                    ["remote"] = new JsonObject { ["url"] = $"http://localhost:{port}/mcp/", ["headers"] = new JsonObject { ["X-Test-Key"] = "abc" } },
                    ["broken"] = new JsonObject { ["command"] = "no-such-program-lplus-test" },
                    ["off"] = new JsonObject { ["command"] = "whatever", ["disabled"] = true },
                },
            }.ToJsonString());
            using (var hub = new McpHub())
            {
                await hub.Load(mcpFile);
                var st = hub.Status();
                Check(st.Count == 3 && st.Any(x => x.name == "local" && x.ok && x.status == "3 tools") && st.Any(x => x.name == "remote" && x.ok) && st.Any(x => x.name == "broken" && !x.ok),
                    "MCP servers started (a program and a web address) and a broken one is reported: " + string.Join("; ", st.Select(x => $"{x.name} = {x.status}")));
                Check(hub.Tools.Count == 6 && hub.Find("local_add") != null && hub.Find("remote_echo") != null && hub.Definitions().Count == 6,
                    "their tools are offered as: " + string.Join(", ", hub.Tools.Select(t => t.Exposed)));
                if (hub.Find("local_add") is { } add && hub.Find("remote_echo") is { } echo && hub.Find("local_fail") is { } fail && hub.Find("remote_add") is { } radd)
                {
                    var sum = await add.Server.Call(add.Name, new JsonObject { ["a"] = 2, ["b"] = 3 });
                    var back = await echo.Server.Call(echo.Name, new JsonObject { ["text"] = "héllo ✓" });
                    var rsum = await radd.Server.Call(radd.Name, new JsonObject { ["a"] = 40, ["b"] = 2.5 });
                    var bad = await fail.Server.Call(fail.Name, null);
                    Check(sum == "5" && back == "héllo ✓" && rsum == "42.5" && bad.StartsWith("Error:"), $"MCP tools run: add = {sum}, echo over HTTP = {back}, add over HTTP = {rsum}, fail = {bad}");
                    Check(sawSession && sawHeader && sawVersion, "the HTTP server got the session id, the protocol version and the header from mcp.json");

                    if (caps.Contains("tools"))
                    {
                        var usedMcp = new List<string>();
                        async Task<string> RunMcp(string name, JsonNode? args)
                        {
                            usedMcp.Add(name);
                            return hub.Find(name) is { } t ? await t.Server.Call(t.Name, args) : "Error: no such tool.";
                        }
                        var askMcp = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Use the local_add tool to add 1234 and 4321, then tell me the sum." } };
                        var saidMcp = new StringBuilder();
                        await Tools.Converse(client, model, askMcp, caps.Contains("thinking") ? JsonValue.Create(false) : null, hub.Definitions(), RunMcp, (c, _) => saidMcp.Append(c), cts.Token);
                        Check(usedMcp.Contains("local_add"),
                            $"model used an MCP tool [{string.Join(", ", usedMcp)}] and said: \"{saidMcp.ToString().Trim().Replace("\n", " ")}\"");
                    }
                }
            }
            web.Stop();

            // Changing the server list: saved to the file, one server connected and disconnected on its own.
            var listFile = Path.Combine(proj, "mcp-list.json");
            File.WriteAllText(listFile, "{ \"theme\": \"dark\", \"mcpServers\": { \"local\": { \"command\": \"x\", \"timeout\": 30, \"disabled\": true } } }");
            using (var hub = new McpHub())
            {
                await hub.Load(listFile);
                var mine = hub.Configs[0];
                mine.Command = Environment.ProcessPath!;
                mine.Args = new() { "--mcp-test-server" };
                mine.Disabled = false;
                hub.Configs.Add(new McpServerConfig { Name = "web one", Url = "https://example.com/mcp", Headers = { ["Authorization"] = "Bearer k" }, Disabled = true });
                hub.Save();
                var again = McpConfig.Load(listFile, out _);
                var kept = JsonNode.Parse(File.ReadAllText(listFile))!;
                Check(again.Count == 2 && again[0].Args.SequenceEqual(new[] { "--mcp-test-server" }) && !again[0].Disabled && again[1].Url == "https://example.com/mcp" && again[1].Headers["Authorization"] == "Bearer k" && again[1].Disabled
                    && kept["theme"]?.GetValue<string>() == "dark" && kept["mcpServers"]?["local"]?["timeout"]?.GetValue<int>() == 30,
                    "servers are saved to mcp.json and read back, and settings Lplus does not know are kept");
                Check(hub.StatusOf(mine).status == "not connected" && hub.Tools.Count == 0, "a server added on the page waits to be connected");
                await hub.Connect(mine);
                var on = hub.StatusOf(mine);
                hub.Disconnect("local");
                Check(on.ok && on.status == "3 tools" && hub.Tools.Count == 0 && hub.StatusOf(mine).status == "not connected" && hub.StatusOf(hub.Configs[1]).status == "switched off",
                    "one server is connected and disconnected on its own: " + on.status);
            }

            // Servers set up for other programs are found for importing.
            var fakeHome = Path.Combine(proj, "home");
            Directory.CreateDirectory(Path.Combine(fakeHome, "roaming", "Claude"));
            File.WriteAllText(Path.Combine(fakeHome, ".claude.json"), "{ \"mcpServers\": { \"remote-tool\": { \"type\": \"stdio\", \"command\": \"ssh\", \"args\": [\"-o\", \"BatchMode=yes\", \"me@host\", \"/opt/tool\", \"serve\"], \"env\": {} } },"
                + " \"projects\": { \"C:/work/app\": { \"mcpServers\": { \"docs\": { \"type\": \"http\", \"url\": \"https://example.com/mcp\" } } }, \"C:/work/other\": { \"mcpServers\": {} } } }");
            File.WriteAllText(Path.Combine(fakeHome, "roaming", "Claude", "claude_desktop_config.json"), "{ \"mcpServers\": { \"remote-tool\": { \"command\": \"ssh\", \"args\": [\"-o\", \"BatchMode=yes\", \"me@host\", \"/opt/tool\", \"serve\"] }, \"notes\": { \"command\": \"npx\", \"args\": [\"-y\", \"notes-mcp\"] } } }");
            File.WriteAllText(Path.Combine(fakeHome, ".mcp.json"), "{ broken");
            var elsewhere = McpConfig.FoundElsewhere(fakeHome, Path.Combine(fakeHome, "roaming"));
            Check(elsewhere.Count == 3 && elsewhere[0].server.Summary == "ssh -o BatchMode=yes me@host /opt/tool serve" && elsewhere[0].from == "Claude Code"
                && elsewhere[1].server.Url == "https://example.com/mcp" && elsewhere[1].from == "Claude Code, project app" && elsewhere[2].server.Name == "notes" && elsewhere[2].from == "Claude desktop app",
                "servers set up for other programs are found, each once: " + string.Join("; ", elsewhere.Select(x => $"{x.server.Name} ({x.from})")));

            // Finding servers in the public registry.
            var reg = McpRegistry.Parse("{\"servers\":[{\"server\":{\"name\":\"io.example/weather\",\"description\":\"Forecasts\",\"version\":\"1.2.0\",\"repository\":{\"url\":\"https://example.com/w\"},"
                + "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"weather-mcp\",\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"},\"runtimeArguments\":[{\"type\":\"positional\",\"value\":\"-y\"}],"
                + "\"packageArguments\":[{\"type\":\"named\",\"name\":\"--units\",\"value\":\"metric\"}],"
                + "\"environmentVariables\":[{\"name\":\"KEY\",\"isRequired\":true,\"description\":\"Your key\"},{\"name\":\"REGION\",\"default\":\"us\"},{\"name\":\"EXTRA\"}]}]}},"
                + "{\"server\":{\"name\":\"io.example/weather\",\"version\":\"1.1.0\"}},"
                + "{\"server\":{\"name\":\"io.example/notes\",\"remotes\":[{\"type\":\"sse\",\"url\":\"https://old\"},{\"type\":\"streamable-http\",\"url\":\"https://example.com/mcp\",\"headers\":[{\"name\":\"Authorization\",\"description\":\"Your token\"}]}]}},"
                + "{\"server\":{\"name\":\"io.example/odd\",\"packages\":[{\"registryType\":\"mcpb\",\"identifier\":\"x\"}]}}]}");
            var wx = reg.Count > 0 ? reg[0].Suggested : null;
            Check(reg.Count == 3 && wx != null && wx.Name == "weather" && wx.Summary == "npx -y weather-mcp --units metric" && wx.Env.Count == 2 && wx.Env["KEY"] == "" && wx.Env["REGION"] == "us" && reg[0].Notes.Count == 3
                && reg[1].Suggested is { Url: "https://example.com/mcp" } nt && nt.Headers.ContainsKey("Authorization") && reg[2].Suggested == null,
                "registry entries become servers ready to add: " + string.Join("; ", reg.Select(r => r.Name + " = " + (r.Suggested?.Summary ?? "cannot be added"))));
            try
            {
                var live = await McpRegistry.Search("github");
                Check(live.Count > 0 && live.Any(r => r.Suggested != null), $"the public registry answers a search: {live.Count} servers for \"github\", first: {live[0].Name}");
            }
            catch (Exception ex) { P("INFO the public MCP registry could not be reached, so the live search was skipped: " + ex.Message); }
            var (parsed2, parseError) = (McpConfig.Load(mcpFile, out var e1), e1);
            File.WriteAllText(mcpFile, "{ not json");
            McpConfig.Load(mcpFile, out var e2);
            Check(parsed2.Count == 4 && parseError == null && parsed2[3].Disabled && e2 != null, "mcp.json is read, and a broken file is reported: " + e2);

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

            // The add/edit server form: what is typed becomes a server, and mistakes are pointed out.
            using (var form = new McpServerDialog(new McpServerConfig(), new List<string> { "files" }, isNew: true, notes: null))
            {
                form.TypeForTest("files", true, "", new string[0], new string[0], "", new string[0]);
                var taken = form.ReadForTest() == null ? form.Problem : "";
                form.TypeForTest("notes", true, " npx ", new[] { "-y", "", " notes-mcp " }, new[] { "KEY=a=b", "bad line" }, "", new string[0]);
                var badEnv = form.ReadForTest() == null ? form.Problem : "";
                form.TypeForTest("notes", true, "npx", new[] { "-y", "notes-mcp" }, new[] { "KEY=a=b", " MODE = fast " }, "", new string[0]);
                var prog = form.ReadForTest();
                form.TypeForTest("remote", false, "", new string[0], new string[0], "example.com/mcp", new string[0]);
                var badUrl = form.ReadForTest() == null ? form.Problem : "";
                form.TypeForTest("remote", false, "", new string[0], new string[0], "https://example.com/mcp", new[] { "Authorization: Bearer a:b" });
                var remote = form.ReadForTest();
                Check(taken.Contains("already") && badEnv.Contains("NAME=value") && badUrl.Contains("https://")
                    && prog is { Command: "npx" } && prog.Args.SequenceEqual(new[] { "-y", "notes-mcp" }) && prog.Env["KEY"] == "a=b" && prog.Env["MODE"] == "fast"
                    && remote is { Url: "https://example.com/mcp" } && remote.Headers["Authorization"] == "Bearer a:b" && remote.Command == "",
                    "the add-server form reads a program and a web address, and points out a taken name, a bad environment line and a bad address");
            }
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
