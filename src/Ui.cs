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

    // Colors of the light theme; UseTheme switches them all to the dark one before the first window is made.
    public static Color Window { get; private set; } = Color.FromArgb(239, 242, 245);
    public static Color Surface { get; private set; } = Color.White;
    public static Color Border { get; private set; } = Color.FromArgb(221, 226, 231);
    public static Color Text { get; private set; } = Color.FromArgb(27, 34, 38);
    public static Color Muted { get; private set; } = Color.FromArgb(96, 108, 115);
    public static Color Accent { get; private set; } = Color.FromArgb(31, 124, 112);
    public static Color AccentDark { get; private set; } = Color.FromArgb(23, 98, 89);
    public static Color AccentLight { get; private set; } = Color.FromArgb(222, 240, 237);
    public static Color Hover { get; private set; } = Color.FromArgb(235, 239, 242);
    public static Color Pressed { get; private set; } = Color.FromArgb(220, 226, 231);
    public static Color Danger { get; private set; } = Color.FromArgb(168, 44, 52);
    public static Color Good { get; private set; } = Color.FromArgb(28, 125, 70);
    public static Color Warning { get; private set; } = Color.FromArgb(178, 104, 16);
    // Text fields, tiles and their empty pictures, the drop place of a dragged tile, menus, "danger" buttons.
    public static Color Field { get; private set; } = Color.FromArgb(246, 248, 249);
    public static Color Tile { get; private set; } = Color.FromArgb(244, 246, 248);
    public static Color Placeholder { get; private set; } = Color.FromArgb(226, 231, 235);
    public static Color PlaceholderIcon { get; private set; } = Color.FromArgb(113, 128, 135);
    public static Color DropTarget { get; private set; } = Color.FromArgb(232, 244, 242);
    public static Color Menu { get; private set; } = Color.FromArgb(250, 251, 252);
    public static Color DangerBorder { get; private set; } = Color.FromArgb(232, 190, 193);
    public static Color DangerHover { get; private set; } = Color.FromArgb(252, 238, 239);
    public static Color DangerPressed { get; private set; } = Color.FromArgb(246, 222, 224);
    public static bool Dark { get; private set; }

    // "dark", "light" or "system" (the app mode of Windows: Settings > Personalization > Colors).
    public static bool WantsDark(string theme) => theme switch
    {
        "dark" => true,
        "light" => false,
        _ => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };
    // Graphite surfaces with the same teal accent, a little lighter so it reads on dark.
    public static void UseTheme(bool dark)
    {
        Dark = dark;
        if (!dark) return;
        Window = Color.FromArgb(22, 24, 27); Surface = Color.FromArgb(32, 35, 39); Border = Color.FromArgb(52, 57, 63);
        Text = Color.FromArgb(229, 233, 236); Muted = Color.FromArgb(148, 157, 165);
        Accent = Color.FromArgb(38, 150, 136); AccentDark = Color.FromArgb(30, 124, 112); AccentLight = Color.FromArgb(29, 66, 62);
        Hover = Color.FromArgb(43, 47, 52); Pressed = Color.FromArgb(53, 58, 64);
        Danger = Color.FromArgb(236, 112, 120); Good = Color.FromArgb(98, 196, 136); Warning = Color.FromArgb(232, 170, 80);
        Field = Color.FromArgb(25, 28, 31); Tile = Color.FromArgb(40, 44, 49); Placeholder = Color.FromArgb(50, 55, 61); PlaceholderIcon = Color.FromArgb(128, 138, 146);
        DropTarget = Color.FromArgb(28, 58, 54); Menu = Color.FromArgb(36, 39, 44);
        DangerBorder = Color.FromArgb(104, 56, 60); DangerHover = Color.FromArgb(58, 38, 41); DangerPressed = Color.FromArgb(70, 44, 47);
        ToolStripManager.Renderer = new MenuRenderer();
    }
    sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Text : Muted; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Text; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The check mark of Windows is black: drawn in the text color instead.
            var r = e.ImageRectangle; r.Inflate(2, 2);
            using (var fill = new SolidBrush(AccentLight)) e.Graphics.FillRectangle(fill, r);
            using var font = Icons(9);
            TextRenderer.DrawText(e.Graphics, "\uE73E", font, r, Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Menu;
        public override Color ImageMarginGradientBegin => Menu;
        public override Color ImageMarginGradientMiddle => Menu;
        public override Color ImageMarginGradientEnd => Menu;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Pressed;
        public override Color MenuItemPressedGradientEnd => Pressed;
        public override Color CheckBackground => AccentLight;
        public override Color CheckSelectedBackground => AccentLight;
        public override Color CheckPressedBackground => AccentLight;
        public override Color ButtonSelectedBorder => Accent;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
    // Dark title bar, scroll bars and fields for a window and everything put into it later. Windows draws message
    // boxes itself, so they stay in its own colors.
    public static void Dress(Form form)
    {
        if (!Dark) return;
        form.HandleCreated += (_, _) =>
        {
            int on = 1;
            // 20 on Windows 10 2004 and later, 19 before.
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        };
        Dress((Control)form);
    }
    public static void Dress(Control control)
    {
        if (!Dark) return;
        Style(control);
        foreach (Control child in control.Controls) Dress(child);
        control.ControlAdded += (_, e) => { if (e.Control != null) Dress(e.Control); };
    }
    static void Style(Control control)
    {
        switch (control)
        {
            case TextBoxBase or NumericUpDown or DomainUpDown or ListBox:
                control.BackColor = Field; control.ForeColor = Text; break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Field; combo.ForeColor = Text; break;
            case TrackBar track when track.Parent != null:
                track.BackColor = track.Parent.BackColor; break;
        }
        // Scroll bars, drop-down lists and check boxes of the dark Explorer style.
        if (control is ScrollableControl or ComboBox or ListBox or TextBoxBase or CheckBox or RadioButton or TrackBar)
        {
            if (control.IsHandleCreated) SetWindowTheme(control.Handle, control is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer", null);
            else control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, control is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer", null);
        }
    }
    // Tooltips in the colors of the theme (Windows draws them light otherwise).
    public static ToolTip Tips(ToolTip tips)
    {
        if (!Dark) return tips;
        tips.OwnerDraw = true;
        tips.Popup += (_, e) =>
        {
            var text = e.AssociatedControl == null ? "" : tips.GetToolTip(e.AssociatedControl);
            var size = TextRenderer.MeasureText(text, Small);
            e.ToolTipSize = new Size(size.Width + S(14), size.Height + S(10));
        };
        tips.Draw += (_, e) =>
        {
            using (var fill = new SolidBrush(Menu)) e.Graphics.FillRectangle(fill, e.Bounds);
            using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var inner = Rectangle.Inflate(e.Bounds, -S(7), -S(5));
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, inner, Text, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        };
        return tips;
    }

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
        b.FlatAppearance.BorderColor = danger ? DangerBorder : Border;
        if (accent) { b.BackColor = Accent; b.ForeColor = Color.White; b.FlatAppearance.MouseOverBackColor = AccentDark; b.FlatAppearance.MouseDownBackColor = AccentDark; }
        if (danger) { b.ForeColor = Danger; b.FlatAppearance.MouseOverBackColor = DangerHover; b.FlatAppearance.MouseDownBackColor = DangerPressed; }
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

// Panel with rounded corners in the surface color and an optional heading.
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
