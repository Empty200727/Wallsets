using System.Drawing.Drawing2D;

namespace Wallsets;

// Colors, fonts, DPI scaling and small styled controls shared by the windows.
internal static class Ui
{
    // Layout sizes are written for 96 DPI (100%) and scaled to the screen scaling of Windows.
    public static readonly float Scale = SystemDpi() / 96f;
    static float SystemDpi() { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiX; }
    public static int S(int value) => (int)Math.Round(value * Scale);
    public static Size S(int width, int height) => new(S(width), S(height));
    public static Padding P(int all) => new(S(all));
    public static Padding P(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));

    public static readonly Color Window = Color.FromArgb(239, 242, 245);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(221, 226, 231);
    public static readonly Color Text = Color.FromArgb(27, 34, 38);
    public static readonly Color Muted = Color.FromArgb(96, 108, 115);
    public static readonly Color Accent = Color.FromArgb(31, 124, 112);
    public static readonly Color AccentDark = Color.FromArgb(23, 98, 89);
    public static readonly Color AccentLight = Color.FromArgb(222, 240, 237);
    public static readonly Color Hover = Color.FromArgb(235, 239, 242);
    public static readonly Color Pressed = Color.FromArgb(220, 226, 231);
    public static readonly Color Danger = Color.FromArgb(168, 44, 52);
    public static readonly Color Good = Color.FromArgb(28, 125, 70);
    public static readonly Color Warning = Color.FromArgb(178, 104, 16);

    // Point sizes already follow the Windows scaling.
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11.5f);
    public static Font Icons(float size) => new(Program.IconFont, size);

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Button IconButton(string glyph, string tip, ToolTip tips, Action action, float size = 12.5f)
    {
        var b = Styled(new Button { Text = glyph, Font = Icons(size), Size = S(38, 36), Margin = P(0, 0, 4, 0), AccessibleName = tip });
        b.FlatAppearance.BorderSize = 0;
        tips.SetToolTip(b, tip); b.Click += (_, _) => action(); return b;
    }
    public static Button TextButton(string text, Action action, bool accent = false, bool danger = false)
    {
        var b = Styled(new Button { Text = text, Font = Body, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, S(36)), Padding = P(12, 0, 12, 0), Margin = P(0, 0, 8, 0), UseMnemonic = false });
        b.FlatAppearance.BorderSize = accent ? 0 : 1;
        b.FlatAppearance.BorderColor = danger ? Color.FromArgb(232, 190, 193) : Border;
        if (accent) { b.BackColor = Accent; b.ForeColor = Color.White; b.FlatAppearance.MouseOverBackColor = AccentDark; b.FlatAppearance.MouseDownBackColor = AccentDark; }
        if (danger) { b.ForeColor = Danger; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(252, 238, 239); b.FlatAppearance.MouseDownBackColor = Color.FromArgb(246, 222, 224); }
        b.Click += (_, _) => action(); return b;
    }
    static Button Styled(Button b)
    {
        b.FlatStyle = FlatStyle.Flat; b.BackColor = Surface; b.ForeColor = Text; b.Cursor = Cursors.Hand; b.UseVisualStyleBackColor = false;
        b.FlatAppearance.MouseOverBackColor = Hover; b.FlatAppearance.MouseDownBackColor = Pressed;
        return b;
    }
    // Toggle that looks like a button and stays tinted while switched on.
    public static CheckBox ToggleButton(string glyph, string name)
    {
        var t = new CheckBox { Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Text = glyph, Font = Icons(13), TextAlign = ContentAlignment.MiddleCenter, Size = S(40, 38), Margin = P(0, 0, 4, 0), BackColor = Surface, ForeColor = Text, Cursor = Cursors.Hand, AccessibleName = name, UseVisualStyleBackColor = false };
        t.FlatAppearance.BorderSize = 0; t.FlatAppearance.CheckedBackColor = AccentLight;
        t.FlatAppearance.MouseOverBackColor = Hover; t.FlatAppearance.MouseDownBackColor = Pressed;
        return t;
    }
    public static Label Caption(string text, Font? font = null, Color? color = null) =>
        new() { Text = text, Font = font ?? Body, ForeColor = color ?? Text, AutoSize = true, UseMnemonic = false, BackColor = Surface };
}

// White panel with rounded corners and an optional heading.
internal class Card : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Heading { get; init; }
    public int HeadingHeight => Heading == null ? 0 : Ui.S(40);
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Ui.Surface; ForeColor = Ui.Text; Font = Ui.Body;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Ui.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Ui.RoundedPath(r, Ui.S(10));
        using var fill = new SolidBrush(Ui.Surface); e.Graphics.FillPath(fill, path);
        using var pen = new Pen(Ui.Border); e.Graphics.DrawPath(pen, path);
        if (Heading != null) TextRenderer.DrawText(e.Graphics, Heading, Ui.Heading, new Rectangle(Ui.S(18), Ui.S(12), Width - Ui.S(36), Ui.S(26)), Ui.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
