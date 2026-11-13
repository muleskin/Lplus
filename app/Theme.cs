using System.Drawing.Drawing2D;

namespace Lplus;

/// <summary>Colours and fonts: a light, neutral look.</summary>
static class Theme
{
    public static float Scale = 1f;
    public static int S(int v) => (int)Math.Round(v * Scale);
    public static float Sf(float v) => v * Scale;

    public static readonly Color Background = Color.White;
    public static readonly Color Sidebar = Color.FromArgb(250, 250, 250);
    public static readonly Color SidebarHover = Color.FromArgb(238, 238, 238);
    public static readonly Color SidebarSelected = Color.FromArgb(229, 229, 229);
    public static readonly Color Border = Color.FromArgb(229, 229, 229);       // neutral-200
    public static readonly Color BorderStrong = Color.FromArgb(212, 212, 212); // neutral-300
    public static readonly Color Text = Color.FromArgb(38, 38, 38);            // neutral-800
    public static readonly Color TextBody = Color.FromArgb(64, 64, 64);        // neutral-700
    public static readonly Color Muted = Color.FromArgb(115, 115, 115);        // neutral-500
    public static readonly Color Faint = Color.FromArgb(163, 163, 163);        // neutral-400
    public static readonly Color UserBubble = Color.FromArgb(245, 245, 245);   // neutral-100
    public static readonly Color CodeBg = Color.FromArgb(245, 245, 245);
    public static readonly Color InlineCodeBg = Color.FromArgb(240, 240, 240);
    public static readonly Color Accent = Color.FromArgb(23, 23, 23);          // near-black buttons
    public static readonly Color Green = Color.FromArgb(34, 197, 94);
    public static readonly Color ErrorBg = Color.FromArgb(254, 242, 242);
    public static readonly Color ErrorText = Color.FromArgb(185, 28, 28);
    public static readonly Color Hover = Color.FromArgb(243, 243, 243);
    public static readonly Color Blue = Color.FromArgb(59, 130, 246);

    public const string FontName = "Segoe UI";
    public const string MonoName = "Cascadia Mono";

    public static readonly Font Body = new(FontName, 10.5f);
    public static readonly Font BodyBold = new(FontName, 10.5f, FontStyle.Bold);
    public static readonly Font BodyItalic = new(FontName, 10.5f, FontStyle.Italic);
    public static readonly Font BodyBoldItalic = new(FontName, 10.5f, FontStyle.Bold | FontStyle.Italic);
    public static readonly Font Small = new(FontName, 9f);
    public static readonly Font SmallBold = new(FontName, 9f, FontStyle.Bold);
    public static readonly Font Tiny = new(FontName, 8f);
    public static readonly Font TinyBold = new(FontName, 8f, FontStyle.Bold);
    public static readonly Font H1 = new(FontName, 15f, FontStyle.Bold);
    public static readonly Font H2 = new(FontName, 13f, FontStyle.Bold);
    public static readonly Font H3 = new(FontName, 11.5f, FontStyle.Bold);
    public static readonly Font Title = new(FontName, 16f, FontStyle.Bold);
    public static readonly Font Mono = CreateMono(9.5f);

    static Font CreateMono(float size)
    {
        using var test = new Font(MonoName, size);
        return test.Name == MonoName ? new Font(MonoName, size) : new Font("Consolas", size);
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var b = new SolidBrush(c);
        using var p = Round(r, radius);
        g.FillPath(b, p);
    }

    public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
    {
        using var pen = new Pen(c, width);
        using var p = Round(r, radius);
        g.DrawPath(pen, p);
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    public static Size Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

    public static void Draw(Graphics g, string text, Font font, Color color, int x, int y) =>
        TextRenderer.DrawText(g, text, font, new Point(x, y), color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

    public static void DrawEllipsis(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

    /// <summary>The app's own icon: a dark rounded square with a white "L+".</summary>
    public static Icon MakeIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            Smooth(g);
            g.Clear(Color.Transparent);
            FillRound(g, Accent, new RectangleF(1, 1, size - 2, size - 2), size * 0.26f);
            using var f = new Font(FontName, size * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("L+", f, Brushes.White, new RectangleF(0, 0, size, size + size * 0.04f), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
