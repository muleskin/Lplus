using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Lplus;

/// <summary>
/// The tools that reach outside the project's files: looking things up on the web, and running build
/// commands in the project folder (each command only after the person agrees to it).
/// </summary>
static partial class Tools
{
    const int MaxPage = 8_000;         // characters of one web page handed to the model
    const int MaxDownload = 3_000_000; // bytes read from one web address
    const int MaxOutput = 12_000;      // characters of a command's output handed to the model
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        // A plain desktop-browser identity: search pages refuse clients that give none.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,text/plain;q=0.9,*/*;q=0.5");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return http;
    }

    // ---------- the web ----------

    /// <summary>Searches the web and returns the first results as title, address and a line of text each.</summary>
    public static async Task<string> WebSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return "Error: query is empty.";
        string html;
        try
        {
            using var resp = await Http.GetAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            html = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode && !html.Contains("result__a")) return $"Error: the search service answered {(int)resp.StatusCode}. Try again in a moment.";
        }
        catch (Exception ex) { return "Error: could not reach the search service (" + ex.Message + "). The PC may be offline."; }

        var results = ParseSearch(html);
        if (results.Count == 0) return "No results. Try other words.";
        var sb = new StringBuilder();
        int n = 0;
        foreach (var (title, url, text) in results.Take(8))
            sb.AppendLine($"{++n}. {title}\n   {url}\n   {text}");
        sb.Append("(Call fetch_url with one of these addresses to read the page.)");
        return sb.ToString();
    }

    /// <summary>Results out of the search page's HTML: (title, address, snippet).</summary>
    public static List<(string title, string url, string text)> ParseSearch(string html)
    {
        var list = new List<(string, string, string)>();
        var links = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
        var snippets = Regex.Matches(html, @"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline);
        for (int i = 0; i < links.Count; i++)
        {
            var url = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            // Results link through a redirect that carries the real address in "uddg".
            var m = Regex.Match(url, @"[?&]uddg=([^&]+)");
            if (m.Success) url = Uri.UnescapeDataString(m.Groups[1].Value);
            if (url.StartsWith("//")) url = "https:" + url;
            if (url.Contains("duckduckgo.com/y.js")) continue;   // an advertisement
            var title = Plain(links[i].Groups[2].Value);
            var text = i < snippets.Count ? Plain(snippets[i].Groups[1].Value) : "";
            if (title.Length > 0 && url.StartsWith("http")) list.Add((title, url, text));
        }
        return list;
    }

    /// <summary>Reads a web page and returns its text (no markup), cut to a size a model can take.</summary>
    public static async Task<string> FetchUrl(string address)
    {
        address = address.Trim();
        if (!address.Contains("://")) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Error: that is not a web address (it must start with http:// or https://).";
        if (IsLocal(uri.Host)) return "Error: addresses on this PC or the local network are not available.";
        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return $"Error: the site answered {(int)resp.StatusCode} {resp.ReasonPhrase}.";
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && !type.StartsWith("text/") && !type.Contains("json") && !type.Contains("xml") && !type.Contains("javascript"))
                return $"Error: that address is not a page of text (it is {type}).";
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buf = new MemoryStream();
            var chunk = new byte[16384];
            int got;
            while (buf.Length < MaxDownload && (got = await stream.ReadAsync(chunk)) > 0) buf.Write(chunk, 0, got);
            var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"');
            Encoding enc;
            try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); } catch { enc = Encoding.UTF8; }
            var body = enc.GetString(buf.GetBuffer(), 0, (int)buf.Length);
            var text = type.Contains("html") || body.TrimStart().StartsWith('<') ? PageText(body) : body.Trim();
            if (text.Length == 0) return "Error: the page has no readable text (it may need a browser to show anything).";
            return text.Length <= MaxPage ? text : text[..MaxPage] + $"\n… (cut off: the page has {text.Length} characters, only the first {MaxPage} are shown)";
        }
        catch (TaskCanceledException) { return "Error: the site took too long to answer."; }
        catch (Exception ex) { return "Error: could not open that address (" + ex.Message + ")."; }
    }

    /// <summary>True for this PC and private-network addresses, which a web lookup has no business reading.</summary>
    static bool IsLocal(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':')) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    /// <summary>A page's readable text: scripts, styles and tags removed, one block per line.</summary>
    public static string PageText(string html)
    {
        var s = Regex.Replace(html, @"<!--.*?-->|<(script|style|noscript|svg|head|nav|footer|form|iframe)\b.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(br|/p|/div|/li|/tr|/h[1-6]|/pre|/section|/article|/table|/ul|/ol|/blockquote)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => Regex.Replace(l, @"[ \t\r\f\v ]+", " ").Trim()).Where(l => l.Length > 0);
        var text = string.Join("\n", lines);
        // The page's title goes first when the text doesn't already open with it.
        var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var name = title.Success ? Plain(title.Groups[1].Value) : "";
        return name.Length > 0 && !text.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? name + "\n" + text : text;
    }

    static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", "")), @"\s+", " ").Trim();

    // ---------- build commands ----------

    /// <summary>
    /// Runs one command line in the project folder (through cmd.exe) and returns its exit code and output.
    /// The caller asks the person first; this only runs it.
    /// </summary>
    public static async Task<string> RunCommand(string root, string command, TimeSpan? timeout = null)
    {
        var limit = timeout ?? CommandTimeout;
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);
        return await RunStarted(psi, limit);
    }

    /// <summary>The languages run_script takes, with the file ending each script is saved under.</summary>
    public static readonly Dictionary<string, string> ScriptKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = ".ps1", ["python"] = ".py", ["cmd"] = ".cmd", ["bash"] = ".sh",
    };

    /// <summary>The language a script file is in, from its ending ("" when it is not one Lplus runs).</summary>
    public static string ScriptKindOf(string file) =>
        Path.GetExtension(file).ToLowerInvariant() switch { ".ps1" => "powershell", ".py" => "python", ".cmd" or ".bat" => "cmd", ".sh" => "bash", _ => "" };

    /// <summary>The program that runs a kind of script on this PC, or null when it is not installed.</summary>
    public static string? Interpreter(string language)
    {
        static string? OnPath(params string[] names)
        {
            foreach (var name in names)
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try { var full = Path.Combine(dir.Trim().Trim('"'), name); if (File.Exists(full)) return full; } catch { }
                }
            return null;
        }
        switch (language.ToLowerInvariant())
        {
            case "powershell": return OnPath("pwsh.exe", "powershell.exe");
            case "python": return OnPath("python.exe", "py.exe", "python3.exe");
            case "cmd": return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            case "bash":
                // The bash that comes with Git works on Windows paths; the one in System32 opens Linux instead, so it is the last choice.
                foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
                    foreach (var rel in new[] { @"Git\bin\bash.exe", @"Programs\Git\bin\bash.exe" })
                    {
                        var full = Path.Combine(Environment.GetFolderPath(root), rel);
                        if (File.Exists(full)) return full;
                    }
                return OnPath("bash.exe");
            default: return null;
        }
    }

    /// <summary>
    /// Runs a script in the project folder and returns its exit code and output. The script is either the
    /// text given (saved to a throwaway file first) or a file already in the project. The caller asks the
    /// person first; this only runs it.
    /// </summary>
    public static async Task<string> RunScript(string root, string language, string? code, string? file, string arguments, TimeSpan? timeout = null)
    {
        var exe = Interpreter(language);
        if (exe == null) return $"Error: {language} is not installed on this PC. Use one of the others: powershell, python, cmd, bash.";
        string? temp = null;
        if (file == null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "lplus-scripts");
            Directory.CreateDirectory(dir);
            temp = file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ScriptKinds[language]);
            // Line ends the way each language wants them; PowerShell needs a mark at the front to read other alphabets.
            var text = (code ?? "").Replace("\r\n", "\n");
            if (language.Equals("bash", StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            else await File.WriteAllTextAsync(file, text.Replace("\n", "\r\n"), new UTF8Encoding(language.Equals("powershell", StringComparison.OrdinalIgnoreCase)));
        }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            var more = System.Text.RegularExpressions.Regex.Matches(arguments ?? "", "\"[^\"]*\"|\\S+").Select(m => m.Value.Trim('"'));
            switch (language.ToLowerInvariant())
            {
                case "powershell":
                    foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) psi.ArgumentList.Add(a);
                    foreach (var a in more) psi.ArgumentList.Add(a);
                    break;
                case "python":
                    if (Path.GetFileName(exe).Equals("py.exe", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add("-3");
                    psi.ArgumentList.Add(file);
                    foreach (var a in more) psi.ArgumentList.Add(a);
                    psi.Environment["PYTHONIOENCODING"] = "utf-8";
                    psi.Environment["PYTHONUNBUFFERED"] = "1";
                    break;
                case "bash":
                    psi.ArgumentList.Add(file.Replace('\\', '/'));
                    foreach (var a in more) psi.ArgumentList.Add(a);
                    break;
                default:   // cmd
                    psi.Arguments = "/d /s /c \"\"" + file + "\" " + arguments + "\"";
                    break;
            }
            return await RunStarted(psi, timeout ?? CommandTimeout);
        }
        finally { if (temp != null) try { File.Delete(temp); } catch { } }
    }

    /// <summary>Starts the prepared program, waits for it (up to the limit) and reports how it ended and what it printed.</summary>
    static async Task<string> RunStarted(ProcessStartInfo psi, TimeSpan limit)
    {
        // Build tools print plainer, shorter output when they know nobody is watching a console.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CI"] = "1";
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        var output = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        var clock = Stopwatch.StartNew();
        p.Start();
        p.StandardInput.Close();   // a command that waits for typing gets end-of-input instead of hanging
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool timedOut = false;
        using (var cts = new CancellationTokenSource(limit))
        {
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        if (!timedOut) p.WaitForExit();   // lets the last output lines arrive
        string text;
        lock (output) text = output.ToString().TrimEnd();
        // The end of the output is where errors and the summary are, so that is the part kept.
        if (text.Length > MaxOutput) text = text[..2000] + $"\n… ({text.Length - MaxOutput} characters left out) …\n" + text[^(MaxOutput - 2000)..];
        var head = timedOut
            ? "Stopped: the command was still running after " + (limit.TotalSeconds < 90 ? $"{limit.TotalSeconds:0} seconds." : $"{limit.TotalMinutes:0.#} minutes.")
            : $"Exit code {p.ExitCode}{(p.ExitCode == 0 ? " (success)" : " (failed)")}, {clock.Elapsed.TotalSeconds:0.0} s.";
        return head + (text.Length > 0 ? "\nOutput:\n" + text : "\n(no output)");
    }
}
