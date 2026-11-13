using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lplus;

/// <summary>
/// What a model may do on this PC: read the clock, and list, read, search and (after the person confirms)
/// write files inside the project folder. Nothing outside that folder can be reached.
/// </summary>
static class Tools
{
    public const int MaxRounds = 12;      // tool steps allowed in one reply
    const int MaxRead = 60_000;           // characters of one file handed to the model
    const int MaxList = 400;              // entries in one listing
    const int MaxHits = 60;               // lines in one search result
    const long MaxFileBytes = 2_000_000;  // larger files are not read or searched

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "target", "dist", "publish", "__pycache__", ".venv", "venv", "packages",
    };

    /// <summary>The device's local date and time, written so a model can read it back.</summary>
    public static string Now()
    {
        var now = DateTimeOffset.Now;
        var inv = CultureInfo.InvariantCulture;
        var zone = TimeZoneInfo.Local;
        var zoneName = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName;
        return $"{now.ToString("dddd, d MMMM yyyy, h:mm:ss tt", inv)} local time ({zoneName}, UTC{now.ToString("zzz", inv)}). ISO 8601: {now.ToString("yyyy-MM-ddTHH:mm:sszzz", inv)}";
    }

    /// <summary>The note sent ahead of the chat so the model knows what it can do.</summary>
    public static string SystemNote(bool tools, string folder) =>
        !tools ? "Current local date and time on the user's device: " + Now()
        : "You can use tools on the user's PC. Call get_current_time whenever the date or time matters; never guess it."
          + (HasFolder(folder)
              ? " The user's project folder is available through list_files, read_file, search_files and write_file."
                + " When asked to create or change an application, look at the existing files first and update them rather than starting over."
                + " write_file replaces the whole file, so always send the complete new contents. Paths are relative to the project folder."
              : "");

    public static bool HasFolder(string folder) => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);

    /// <summary>Tool definitions for /api/chat. File tools are offered only when a project folder is set.</summary>
    public static JsonArray Definitions(string folder)
    {
        var list = new JsonArray
        {
            Fn("get_current_time", "Get the current local date, time and time zone from the user's device."),
        };
        if (!HasFolder(folder)) return list;
        list.Add(Fn("list_files", "List the files and folders in the user's project folder.",
            ("path", "string", "Folder to list, relative to the project folder. Leave empty for the whole project.", false)));
        list.Add(Fn("read_file", "Read a text file from the user's project folder.",
            ("path", "string", "File to read, relative to the project folder.", true)));
        list.Add(Fn("search_files", "Find text in the project's files. Returns matching lines as path:line: text.",
            ("query", "string", "Text to look for (not case sensitive).", true),
            ("path", "string", "Folder to search, relative to the project folder. Leave empty for the whole project.", false)));
        list.Add(Fn("write_file", "Create a file or replace an existing one in the user's project folder. The user is asked to confirm.",
            ("path", "string", "File to write, relative to the project folder.", true),
            ("content", "string", "The complete new contents of the file.", true)));
        return list;
    }

    static JsonObject Fn(string name, string description, params (string name, string type, string desc, bool required)[] ps)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in ps)
        {
            props[p.name] = new JsonObject { ["type"] = p.type, ["description"] = p.desc };
            if (p.required) required.Add(p.name);
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name, ["description"] = description,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required },
            },
        };
    }

    static string Arg(JsonNode? args, string name)
    {
        try { return args?[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : ""; }
        catch { return ""; }
    }

    /// <summary>One line for the chat, saying what was done.</summary>
    public static string Describe(string name, JsonNode? args) => name switch
    {
        "get_current_time" => "Checked the time",
        "list_files" => Arg(args, "path") is { Length: > 0 } p && p != "." ? $"Listed files in {p}" : "Listed the project's files",
        "read_file" => $"Read {Arg(args, "path")}",
        "search_files" => $"Searched for \"{Arg(args, "query")}\"",
        "write_file" => $"Saved {Arg(args, "path")}",
        _ => name,
    };

    /// <summary>
    /// Runs one tool and returns the text handed back to the model. confirmWrite(fullPath, exists) is asked
    /// before any file is written. Problems come back as "Error: ..." so the model can try something else.
    /// </summary>
    public static async Task<string> Run(string name, JsonNode? args, string folder, Func<string, bool, Task<bool>> confirmWrite)
    {
        try
        {
            if (name == "get_current_time") return Now();
            if (!HasFolder(folder)) return "Error: no project folder is set, so files are not available.";
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            switch (name)
            {
                case "list_files":
                {
                    var dir = Resolve(root, Arg(args, "path"));
                    if (!Directory.Exists(dir)) return "Error: that folder does not exist.";
                    return await Task.Run(() => List(root, dir));
                }
                case "read_file":
                {
                    var file = Resolve(root, Arg(args, "path"));
                    if (File.Exists(file)) return await Task.Run(() => Read(file));
                    var (found, problem) = await Task.Run(() => FindByName(root, file));
                    return found == null ? problem : await Task.Run(() => $"(This file is at {Rel(root, found)}.)\n" + Read(found));
                }
                case "search_files":
                {
                    var query = Arg(args, "query");
                    if (query.Length == 0) return "Error: query is empty.";
                    var dir = Resolve(root, Arg(args, "path"));
                    if (!Directory.Exists(dir) && !File.Exists(dir))
                    {
                        // A file named without its folder: search that file wherever it is.
                        var (found, problem) = await Task.Run(() => FindByName(root, dir));
                        if (found == null) return Path.HasExtension(dir) ? problem : "Error: that folder does not exist. Leave path empty to search the whole project.";
                        dir = found;
                    }
                    return await Task.Run(() => Search(root, dir, query));
                }
                case "write_file":
                {
                    var rel = Arg(args, "path");
                    if (rel.Trim().Length == 0) return "Error: path is empty.";
                    var file = Resolve(root, rel);
                    if (Directory.Exists(file)) return "Error: that path is a folder.";
                    var content = Arg(args, "content");
                    bool exists = File.Exists(file);
                    if (!await confirmWrite(file, exists)) return "The user declined this change. Do not try again unless they ask.";
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, content, new UTF8Encoding(false));
                    int lines = content.Count(c => c == '\n') + (content.Length > 0 && !content.EndsWith('\n') ? 1 : 0);
                    return $"{(exists ? "Replaced" : "Created")} {Rel(root, file)} ({lines} lines).";
                }
                default: return $"Error: there is no tool called {name}.";
            }
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    /// <summary>Full path of a model-supplied path; throws when it leaves the project folder.</summary>
    public static string Resolve(string root, string? rel)
    {
        rel = (rel ?? "").Trim().Trim('"').Replace('/', '\\');
        if (rel is "" or "." or ".\\") return root;
        // The project folder called by its own name means the project folder.
        if (rel.Trim('\\').Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(root, rel)) && !File.Exists(Path.Combine(root, rel))) return root;
        var full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel)).TrimEnd('\\');
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("that path is outside the project folder.");
        return full;
    }

    /// <summary>
    /// A file that is not at the path given: the one file of that name elsewhere in the project is almost
    /// always what was meant. Returns it, or the error to hand back.
    /// </summary>
    static (string? file, string problem) FindByName(string root, string missing)
    {
        var same = Walk(root, files: true)
            .Where(f => Path.GetFileName(f).Equals(Path.GetFileName(missing), StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        if (same.Count == 1) return (same[0], "");
        return (null, same.Count == 0
            ? "Error: that file does not exist. Call list_files to see what is there."
            : "Error: that file does not exist. Files with that name: " + string.Join(", ", same.Select(f => Rel(root, f))));
    }

    static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    static IEnumerable<string> Walk(string dir, bool files)
    {
        if (File.Exists(dir)) { yield return dir; yield break; }
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var d = pending.Pop();
            string[] subs, fs;
            try
            {
                subs = Directory.GetDirectories(d);
                fs = files ? Directory.GetFiles(d) : Array.Empty<string>();
            }
            catch { continue; }
            Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
            foreach (var f in fs) yield return f;
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs.Reverse())
                if (!SkipDirs.Contains(Path.GetFileName(s))) pending.Push(s);
        }
    }

    static string List(string root, string dir)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var f in Walk(dir, files: true))
        {
            if (++n > MaxList) { sb.AppendLine($"… more files not shown. List a subfolder to see them."); break; }
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            sb.AppendLine($"{Rel(root, f)}  ({size} bytes)");
        }
        return n == 0 ? "The folder is empty." : sb.ToString().TrimEnd();
    }

    static bool LooksBinary(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var buf = new byte[4096];
            int got = s.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, got) >= 0;
        }
        catch { return true; }
    }

    static string Read(string file)
    {
        if (new FileInfo(file).Length > MaxFileBytes) return "Error: that file is too large to read (over 2 MB).";
        if (LooksBinary(file)) return "Error: that is not a text file.";
        var text = File.ReadAllText(file);
        return text.Length <= MaxRead ? text : text[..MaxRead] + $"\n… (cut off: the file has {text.Length} characters, only the first {MaxRead} are shown)";
    }

    // Logs and data dumps drown out the code in a search.
    static readonly HashSet<string> SkipInSearch = new(StringComparer.OrdinalIgnoreCase) { ".log", ".jsonl", ".lock", ".map", ".svg", ".csv" };

    /// <summary>
    /// Lines containing the query. When no line has the whole phrase, falls back to lines with all of its
    /// words, then to lines with any of them, and says so.
    /// </summary>
    static string Search(string root, string dir, string query)
    {
        var words = query.Split(new[] { ' ', '\t', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && w is not ("OR" or "AND" or "and" or "the")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tries = new List<(Func<string, bool> match, string note)> { (t => t.Contains(query, StringComparison.OrdinalIgnoreCase), "") };
        if (words.Length > 1 || (words.Length == 1 && !words[0].Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            tries.Add((t => words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)), "No line has that exact phrase. Lines with all of its words:\n"));
            tries.Add((t => words.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)), "No line has that exact phrase. Lines with any of its words:\n"));
        }
        foreach (var (match, note) in tries)
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var f in Walk(dir, files: true))
            {
                if (SkipInSearch.Contains(Path.GetExtension(f))) continue;
                if (match(Path.GetFileName(f)))
                {
                    sb.AppendLine($"{Rel(root, f)}: (file name matches)");
                    if (++hits >= MaxHits) break;
                }
                try
                {
                    if (new FileInfo(f).Length > MaxFileBytes || LooksBinary(f)) continue;
                    int no = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        no++;
                        if (!match(line)) continue;
                        var t = line.Trim();
                        sb.AppendLine($"{Rel(root, f)}:{no}: {(t.Length > 200 ? t[..200] + "…" : t)}");
                        if (++hits >= MaxHits) break;
                    }
                }
                catch { }
                if (hits >= MaxHits) { sb.AppendLine("… more matches not shown. Narrow the search."); break; }
            }
            if (hits > 0) return note + sb.ToString().TrimEnd() + "\n(Only the matching lines are shown. Call read_file to see a whole file.)";
        }
        return "No matches. Search for one word that would appear in the code, or call list_files to see what is there.";
    }

    /// <summary>
    /// Where text that might be a tool request starts: a line that opens with "{", a ```json fence or a
    /// tool_call tag (outside a code block). Everything before it is safe to show; s.Length when there is none.
    /// </summary>
    static int HoldFrom(string s)
    {
        bool fence = false;
        int lineStart = 0;
        while (lineStart <= s.Length)
        {
            int nl = s.IndexOf('\n', lineStart);
            var t = (nl >= 0 ? s[lineStart..nl] : s[lineStart..]).TrimStart();
            if (!fence)
            {
                if (t.StartsWith('{') || t.StartsWith("[{") || t.StartsWith("<tool_call>") || t.StartsWith("```json")) return lineStart;
                // The last, unfinished line may still turn into one of those.
                if (nl < 0 && t.Length is > 0 and < 11 && ("<tool_call>".StartsWith(t) || "```json".StartsWith(t) || t == "[")) return lineStart;
            }
            if (t.StartsWith("```")) fence = !fence;
            if (nl < 0) break;
            lineStart = nl + 1;
        }
        return s.Length;
    }

    /// <summary>
    /// Tool requests written as text ({"name": ..., "arguments": {...}}, alone, fenced, in tool_call tags,
    /// or one per line), in the same shape the server returns them. Empty when the text is something else.
    /// </summary>
    public static List<JsonNode> TextCalls(string text, JsonArray tools)
    {
        var known = tools.Select(t => t?["function"]?["name"]?.GetValue<string>()).Where(n => n != null).ToHashSet();
        var found = new List<JsonNode>();
        var s = text.Replace("<tool_call>", "\n").Replace("</tool_call>", "\n").Replace("```json", "\n").Replace("```", "\n").Trim();
        if (s.Length == 0) return found;

        bool Take(JsonNode? n)
        {
            if (n is JsonArray arr) return arr.Count > 0 && arr.All(Take);
            if (n is not JsonObject o) return false;
            var fn = o["function"] as JsonObject ?? o;
            string? name;
            try { name = fn["name"]?.GetValue<string>(); } catch { return false; }
            if (name == null || !known.Contains(name)) return false;
            var args = fn["arguments"] ?? fn["parameters"];
            found.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args?.DeepClone() ?? new JsonObject() } });
            return true;
        }

        // One request, or several written one after another (on one line each or spread over many).
        foreach (var piece in JsonPieces(s))
        {
            bool ok = false;
            try { ok = piece != null && Take(JsonNode.Parse(piece)); } catch { }
            if (!ok) { found.Clear(); return found; }
        }
        return found;
    }

    /// <summary>
    /// Cuts text into the JSON objects or arrays written in it back to back. Yields null when something
    /// that is not JSON sits between or after them.
    /// </summary>
    static IEnumerable<string?> JsonPieces(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] == ',') { i++; continue; }
            if (s[i] != '{' && s[i] != '[') { yield return null; yield break; }
            int depth = 0, start = i;
            bool inString = false;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c is '{' or '[') depth++;
                else if (c is '}' or ']') { if (--depth == 0) { i++; break; } }
            }
            if (depth != 0) { yield return null; yield break; }
            yield return s[start..i];
        }
    }

    /// <summary>
    /// One whole reply: asks the model, runs the tools it calls, hands the results back and asks again until
    /// it answers in words. run(name, args) does one tool and returns its result.
    /// </summary>
    public static async Task Converse(ServerClient client, string model, JsonArray messages, JsonNode? think, JsonArray? tools,
        Func<string, JsonNode?, Task<string>> run, Action<string, string> onDelta, CancellationToken ct)
    {
        for (int round = 0; ; round++)
        {
            var content = new StringBuilder();
            var thinking = new StringBuilder();
            int shown = 0;   // how much of this round's text has been passed on
            var calls = await client.Chat(model, messages, think, (c, t) =>
            {
                content.Append(c);
                thinking.Append(t);
                // Some models write a tool request as plain text, alone or after a few words. Text that
                // could be one is held until the round ends, so a request never shows up in the chat.
                int upTo = tools == null ? content.Length : Math.Max(shown, HoldFrom(content.ToString()));
                var pending = content.ToString(shown, upTo - shown);
                shown = upTo;
                if (pending.Length > 0 || t.Length > 0) onDelta(pending, t);
            }, ct, tools);
            if (shown < content.Length)
            {
                var held = content.ToString(shown, content.Length - shown);
                var written = calls.Count == 0 && tools != null ? TextCalls(held, tools) : new List<JsonNode>();
                if (written.Count == 0) onDelta(held, "");            // it was part of the answer after all
                else { calls = written; content.Length = shown; }   // a request: run it, don't show it
            }
            if (calls.Count == 0 || tools == null) return;
            if (round >= MaxRounds) throw new InvalidOperationException("Stopped: the model kept using tools without answering.");

            var said = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
            if (thinking.Length > 0) said["thinking"] = thinking.ToString();
            said["tool_calls"] = new JsonArray(calls.Select(c => c.DeepClone()).ToArray());
            messages.Add(said);
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                var fn = call["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"];
                if (args is JsonValue v && v.TryGetValue<string>(out var text))
                    try { args = JsonNode.Parse(text); } catch { args = null; }
                var result = await run(name, args);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = result });
            }
        }
    }
}
