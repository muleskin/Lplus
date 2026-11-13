using System.Text;
using System.Text.RegularExpressions;

namespace Lplus;

enum Span { Normal, Bold, Italic, BoldItalic, Code, Link }

record Run(string Text, Span Style);

enum BlockKind { Paragraph, Heading, Bullet, Code, Rule, Quote, Table }

class Block
{
    public BlockKind Kind;
    public List<Run> Runs = new();
    public int Level;          // heading level, or list indent level
    public string Marker = ""; // "•" or "1."
    public string Code = "";
    public string Lang = "";
    public bool GapBefore;     // a blank line came before it
}

/// <summary>A small Markdown reader for chat replies: headings, lists, code, quotes, rules, tables, bold/italic/code.</summary>
static class Markdown
{
    static readonly Regex Ordered = new(@"^(\s*)(\d+)[.)]\s+(.*)$");
    static readonly Regex Unordered = new(@"^(\s*)[-*+]\s+(.*)$");
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");

    public static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool gap = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                var lang = trimmed[3..].Trim();
                var sb = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(lines[i]);
                    i++;
                }
                blocks.Add(new Block { Kind = BlockKind.Code, Code = sb.ToString(), Lang = lang, GapBefore = gap });
                gap = false;
                continue;
            }
            if (trimmed.Length == 0) { gap = true; continue; }
            if (Regex.IsMatch(trimmed, @"^(\*\s*\*\s*\*|-\s*-\s*-|_\s*_\s*_)[\s*_-]*$"))
            {
                blocks.Add(new Block { Kind = BlockKind.Rule, GapBefore = gap });
                gap = false;
                continue;
            }
            if (trimmed.StartsWith("|"))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    var row = lines[i].Trim();
                    if (!Regex.IsMatch(row, @"^\|[\s:|-]+\|?$"))
                    {
                        var cells = row.Trim('|').Split('|').Select(c => StripInline(c.Trim()));
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(string.Join("  │  ", cells));
                    }
                    i++;
                }
                i--;
                blocks.Add(new Block { Kind = BlockKind.Table, Code = sb.ToString(), GapBefore = gap });
                gap = false;
                continue;
            }
            Match m;
            if ((m = Heading.Match(trimmed)).Success)
                blocks.Add(new Block { Kind = BlockKind.Heading, Level = m.Groups[1].Length, Runs = Inline(m.Groups[2].Value.TrimEnd('#', ' ')), GapBefore = gap });
            else if ((m = Unordered.Match(line)).Success)
                blocks.Add(new Block { Kind = BlockKind.Bullet, Level = m.Groups[1].Value.Replace("\t", "  ").Length / 2, Marker = "•", Runs = Inline(m.Groups[2].Value), GapBefore = gap });
            else if ((m = Ordered.Match(line)).Success)
                blocks.Add(new Block { Kind = BlockKind.Bullet, Level = m.Groups[1].Value.Replace("\t", "  ").Length / 2, Marker = m.Groups[2].Value + ".", Runs = Inline(m.Groups[3].Value), GapBefore = gap });
            else if (trimmed.StartsWith(">"))
                blocks.Add(new Block { Kind = BlockKind.Quote, Runs = Inline(trimmed.TrimStart('>', ' ')), GapBefore = gap });
            else
                blocks.Add(new Block { Kind = BlockKind.Paragraph, Runs = Inline(line.Trim()), GapBefore = gap });
            gap = false;
        }
        return blocks;
    }

    public static string StripInline(string s) => string.Concat(Inline(s).Select(r => r.Text));

    /// <summary>Splits **bold**, *italic*, ***both***, `code` and [text](link).</summary>
    public static List<Run> Inline(string s)
    {
        var runs = new List<Run>();
        var sb = new StringBuilder();
        void Flush(Span st) { if (sb.Length > 0) { runs.Add(new Run(sb.ToString(), st)); sb.Clear(); } }
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i) { Flush(Span.Normal); runs.Add(new Run(s[(i + 1)..end], Span.Code)); i = end + 1; continue; }
            }
            if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))))
            {
                int n = 0;
                while (i + n < s.Length && s[i + n] == c && n < 3) n++;
                var marker = new string(c, n);
                int end = s.IndexOf(marker, i + n, StringComparison.Ordinal);
                if (end > i + n && !char.IsWhiteSpace(s[i + n]))
                {
                    Flush(Span.Normal);
                    var style = n == 3 ? Span.BoldItalic : n == 2 ? Span.Bold : Span.Italic;
                    foreach (var r in Inline(s[(i + n)..end]))
                        runs.Add(new Run(r.Text, r.Style == Span.Normal ? style : r.Style));
                    i = end + n;
                    continue;
                }
            }
            if (c == '[')
            {
                var m = Regex.Match(s[i..], @"^\[([^\]]+)\]\(([^)\s]+)\)");
                if (m.Success) { Flush(Span.Normal); runs.Add(new Run(m.Groups[1].Value, Span.Link)); i += m.Length; continue; }
            }
            sb.Append(c);
            i++;
        }
        Flush(Span.Normal);
        return runs;
    }
}
