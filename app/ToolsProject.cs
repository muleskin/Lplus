using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lplus;

/// <summary>What kind of project a folder holds and the commands that build and test it (null when there is none).</summary>
sealed record ProjectInfo(string Kind, string? Build, string? Test);

/// <summary>
/// Working on a project as a whole: finding out how it is built and tested, checking the build and the
/// tests after the model changed something, deciding which commands are ordinary build tools, keeping
/// copies of changed files so the changes can be undone, and keeping a long job within the model's memory.
/// </summary>
static partial class Tools
{
    public const int MaxChecks = 6;   // times in one reply the model is sent back to fix a failing build or test

    // ---------- what kind of project is this ----------

    /// <summary>
    /// Looks at the folder and says how to build and test it. A ".lplus.json" in the folder
    /// ({ "build": "...", "test": "..." }) overrides what is found; an empty string there means "none".
    /// </summary>
    public static ProjectInfo Project(string folder)
    {
        var found = Detect(folder);
        try
        {
            var over = Path.Combine(folder, ".lplus.json");
            if (File.Exists(over) && JsonNode.Parse(File.ReadAllText(over)) is JsonObject o)
            {
                string? Pick(string key, string? was) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Trim().Length > 0 ? s.Trim() : null) : was;
                found = found with { Build = Pick("build", found.Build), Test = Pick("test", found.Test) };
            }
        }
        catch { }
        return found;
    }

    static ProjectInfo Detect(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return new("", null, null);
            bool Has(string name) => File.Exists(Path.Combine(folder, name));
            string[] Top(string pattern) => Directory.GetFiles(folder, pattern);
            // One level down as well: a solution or project often sits in a subfolder such as "app" or "src".
            string[] Near(string pattern) => Top(pattern).Concat(Directory.GetDirectories(folder)
                .Where(d => !SkipDirs.Contains(Path.GetFileName(d)))
                .SelectMany(d => { try { return Directory.GetFiles(d, pattern); } catch { return Array.Empty<string>(); } })).ToArray();
            string Arg(string file) { var rel = Rel(folder, file); return rel.Contains(' ') ? "\"" + rel + "\"" : rel; }

            var slns = Top("*.sln").Concat(Top("*.slnx")).ToArray();
            var projs = Near("*.csproj").Concat(Near("*.fsproj")).Concat(Near("*.vbproj")).ToArray();
            if (slns.Length > 0 || projs.Length > 0)
            {
                // "dotnet build" alone works when the folder holds exactly one solution or project file.
                var here = slns.Concat(Top("*.csproj")).Concat(Top("*.fsproj")).Concat(Top("*.vbproj")).ToArray();
                string target = here.Length == 1 ? "" : " " + Arg(slns.Length > 0 ? slns[0] : projs[0]);
                bool IsTest(string p) { try { return File.ReadAllText(p).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
                var testProj = projs.FirstOrDefault(IsTest);
                // Without a solution, the program is built from its own project and the tests from theirs.
                if (here.Length != 1 && slns.Length == 0)
                    return new(".NET", "dotnet build " + Arg(projs.FirstOrDefault(p => !IsTest(p)) ?? projs[0]), testProj != null ? "dotnet test " + Arg(testProj) : null);
                return new(".NET", "dotnet build" + target, testProj != null ? "dotnet test" + target : null);
            }
            if (Has("Cargo.toml")) return new("Rust", "cargo build", "cargo test");
            if (Has("go.mod")) return new("Go", "go build ./...", "go test ./...");
            if (Has("package.json"))
            {
                var scripts = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "package.json")))?["scripts"] as JsonObject;
                string? build = scripts?["build"] != null ? "npm run build" : null;
                var t = scripts?["test"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
                return new("Node.js", build, t.Length > 0 && !t.Contains("no test specified") ? "npm test" : null);
            }
            if (Has("pom.xml")) return new("Java (Maven)", "mvn -q compile", "mvn -q test");
            if (Has("build.gradle") || Has("build.gradle.kts")) return new("Java (Gradle)", Has("gradlew.bat") ? "gradlew build -x test" : "gradle build -x test", Has("gradlew.bat") ? "gradlew test" : "gradle test");
            if (Has("pyproject.toml") || Has("setup.py") || Has("requirements.txt") || Near("*.py").Length > 0)
            {
                bool tests = Directory.Exists(Path.Combine(folder, "tests")) || Directory.Exists(Path.Combine(folder, "test")) || Near("test_*.py").Length > 0 || Near("*_test.py").Length > 0;
                return new("Python", "python -m compileall -q .", tests ? "python -m pytest -q" : null);
            }
            if (Has("CMakeLists.txt")) return new("C/C++ (CMake)", Directory.Exists(Path.Combine(folder, "build")) ? "cmake --build build" : null, null);
            if (Has("Makefile") || Has("makefile"))
            {
                var mk = File.ReadAllText(Path.Combine(folder, Has("Makefile") ? "Makefile" : "makefile"));
                return new("Make", "make", Regex.IsMatch(mk, @"^test\s*:", RegexOptions.Multiline) ? "make test" : null);
            }
        }
        catch { }
        return new("", null, null);
    }

    /// <summary>What the model is told about the project and about the checking Lplus does.</summary>
    public static string ProjectNote(string folder)
    {
        var p = Project(folder);
        if (p.Build == null && p.Test == null) return "";
        var sb = new StringBuilder(p.Kind.Length > 0 ? $" This is a {p.Kind} project." : "");
        if (p.Build != null) sb.Append($" Build it with: {p.Build}");
        if (p.Test != null) sb.Append($"{(p.Build != null ? "; test" : " Test")} it with: {p.Test}");
        sb.Append(". When you say you are finished, the project is built" + (p.Test != null ? " and tested" : "")
            + " automatically and you are told about any errors, so keep fixing until it passes. Do not stop to ask whether to continue.");
        return sb.ToString();
    }

    // ---------- which commands are ordinary build tools ----------

    static readonly Dictionary<string, string[]?> BuildTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = new[] { "build", "test", "run", "restore", "clean", "msbuild", "format" },
        ["npm"] = new[] { "test", "run", "t" },
        ["pnpm"] = new[] { "test", "run", "build" },
        ["yarn"] = new[] { "test", "run", "build" },
        ["cargo"] = new[] { "build", "test", "check", "run", "clippy", "fmt" },
        ["go"] = new[] { "build", "test", "vet", "run", "fmt" },
        ["mvn"] = null, ["gradle"] = null, ["gradlew"] = null, ["make"] = null, ["nmake"] = null, ["msbuild"] = null,
        ["cmake"] = new[] { "--build" }, ["ctest"] = null, ["tsc"] = null, ["pytest"] = null,
        ["python"] = null, ["py"] = null, ["python3"] = null, ["node"] = null, ["npx"] = new[] { "tsc", "jest", "vitest", "eslint", "mocha" },
    };

    /// <summary>
    /// True for a plain build, test or run command: one known tool, no shell tricks (pipes, redirects,
    /// several commands in one line) and nothing that reaches outside the project. Only these may run
    /// without asking when "work without asking" is on; everything else still asks.
    /// </summary>
    public static bool IsBuildCommand(string command, string folder)
    {
        command = command.Trim();
        if (command.Length == 0 || command.IndexOfAny(new[] { '&', '|', '>', '<', ';', '`', '^', '%', '\n', '\r' }) >= 0) return false;
        var p = Project(folder);
        if (command.Equals(p.Build, StringComparison.OrdinalIgnoreCase) || command.Equals(p.Test, StringComparison.OrdinalIgnoreCase)) return true;
        if (command.Contains("..") || Regex.IsMatch(command, @"(^|\s)[""']?([A-Za-z]:[\\/]|[\\/]{1,2})")) return false;   // a path outside the folder
        var words = Regex.Matches(command, @"""[^""]*""|\S+").Select(m => m.Value.Trim('"')).ToList();
        var tool = Path.GetFileNameWithoutExtension(words[0]);
        if (words[0].Contains('\\') || words[0].Contains('/') || !BuildTools.TryGetValue(tool, out var allowed)) return false;
        if (tool.ToLowerInvariant() is "python" or "py" or "python3")
        {
            if (words.Count < 2) return false;
            if (words[1] == "-m") return words.Count > 2 && words[2] is "pytest" or "unittest" or "compileall" or "py_compile";
            return words[1].EndsWith(".py", StringComparison.OrdinalIgnoreCase);     // the project's own script
        }
        if (tool.Equals("node", StringComparison.OrdinalIgnoreCase)) return words.Count > 1 && Regex.IsMatch(words[1], @"\.(m?js|cjs)$", RegexOptions.IgnoreCase);
        return allowed == null || (words.Count > 1 && allowed.Contains(words[1], StringComparer.OrdinalIgnoreCase));
    }

    // ---------- checking the work ----------

    /// <summary>
    /// Builds and tests the project. Returns null when everything passes (or there is nothing to run, or the
    /// person said no); otherwise the message that sends the model back to fix it. step(text) reports progress.
    /// </summary>
    public static async Task<string?> Verify(string folder, Func<string, string, Task<bool>> confirmCommand, Action<string> step)
    {
        if (!HasFolder(folder)) return null;
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var p = Project(root);
        foreach (var (what, command) in new[] { ("build", p.Build), ("tests", p.Test) })
        {
            if (command == null) continue;
            if (!await confirmCommand(command, root)) { step($"Not checked (you said no): {command}"); return null; }
            step($"Checking the {what}: {command} …");
            var result = await RunCommand(root, command);
            bool ok = result.StartsWith("Exit code 0");
            step($"Checked the {what}: {command} — {(ok ? "passed" : "failed")}");
            if (!ok)
                return $"I ran `{command}` in the project and it failed.\n{result}\n\nFind the cause in the code, fix it with edit_file, and say when you are done. It will be checked again.";
        }
        return null;
    }

    // ---------- undoing file changes ----------

    /// <summary>
    /// Keeps a copy of a file as it was before its first change in this reply (or notes that it did not
    /// exist), in a folder of its own, so the whole reply's changes can be taken back.
    /// </summary>
    public static void KeepCopy(string backupDir, string root, string file)
    {
        root = Path.GetFullPath(root).TrimEnd('\\', '/');
        Directory.CreateDirectory(backupDir);
        var listFile = Path.Combine(backupDir, "changes.json");
        var list = File.Exists(listFile) ? JsonNode.Parse(File.ReadAllText(listFile)) as JsonObject ?? new JsonObject() : new JsonObject();
        list["root"] = root;
        var files = list["files"] as JsonObject ?? new JsonObject();
        list["files"] = files;
        var rel = Rel(root, file);
        if (files.ContainsKey(rel)) return;   // the first copy is the one that counts
        bool existed = File.Exists(file);
        if (existed)
        {
            var copy = Path.Combine(backupDir, "files", rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, true);
        }
        files[rel] = existed;
        File.WriteAllText(listFile, list.ToJsonString());
    }

    /// <summary>The files a reply changed, as kept by KeepCopy (project-relative paths).</summary>
    public static List<string> ChangedFiles(string? backupDir)
    {
        try
        {
            var listFile = Path.Combine(backupDir ?? "", "changes.json");
            if (backupDir != null && File.Exists(listFile) && JsonNode.Parse(File.ReadAllText(listFile))?["files"] is JsonObject files)
                return files.Select(kv => kv.Key).ToList();
        }
        catch { }
        return new();
    }

    /// <summary>Puts every file a reply changed back as it was, and removes the ones it created. Returns how many.</summary>
    public static int UndoChanges(string backupDir)
    {
        var list = JsonNode.Parse(File.ReadAllText(Path.Combine(backupDir, "changes.json")))!;
        var root = list["root"]!.GetValue<string>();
        int n = 0;
        foreach (var (rel, existed) in (JsonObject)list["files"]!)
        {
            var file = Path.Combine(root, rel.Replace('/', '\\'));
            if (existed!.GetValue<bool>()) File.Copy(Path.Combine(backupDir, "files", rel.Replace('/', '\\')), file, true);
            else if (File.Exists(file)) File.Delete(file);
            n++;
        }
        Directory.Delete(backupDir, true);
        return n;
    }

    // ---------- keeping a long job within the model's memory ----------

    /// <summary>
    /// When the conversation has grown past the budget (in characters), shortens the oldest tool results
    /// and the file contents in old tool requests. The latest steps and everything the person wrote stay whole.
    /// </summary>
    public static void Compact(JsonArray messages, int budget)
    {
        int Size() => messages.Sum(m => (m?["content"]?.GetValue<string>().Length ?? 0) + (m?["tool_calls"]?.ToJsonString().Length ?? 0));
        if (Size() <= budget) return;
        const int keep = 6;   // the newest messages are never touched
        for (int i = 0; i < messages.Count - keep && Size() > budget; i++)
        {
            var m = messages[i] as JsonObject;
            var role = m?["role"]?.GetValue<string>();
            if (role == "tool")
            {
                var text = m!["content"]?.GetValue<string>() ?? "";
                if (text.Length > 500) m["content"] = text[..300] + "\n… (the rest of this older result was dropped to save space; run the tool again if it is needed)";
            }
            else if (role == "assistant" && m!["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                    if (call?["function"]?["arguments"] is JsonObject args)
                        foreach (var key in new[] { "content", "new_text", "old_text" })
                            if (args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 300)
                                args[key] = s[..200] + "… (shortened)";
        }
    }
}
