using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Wallsets;

// Where copies of a picture land on the screen for each scaling mode, the same way the player places them.
internal static class WallpaperLayout
{
    public static string Title(string scaling) => scaling switch { "fit" => "По размеру фона", "stretch" => "Растянуть", "tile" => "Замостить", _ => "Заполнение" };
    public static List<Rectangle> Place(string scaling, Size media, Rectangle box)
    {
        if (media.Width <= 0 || media.Height <= 0 || box.Width <= 0 || box.Height <= 0) return [];
        if (scaling == "stretch") return [box];
        if (scaling == "tile")
        {
            // A larger picture is shown at its own size from the top left corner, a smaller one is repeated.
            if (media.Width >= box.Width && media.Height >= box.Height) return [new(box.Location, media)];
            int columns = Math.Clamp((box.Width + media.Width - 1) / media.Width, 1, 64), rows = Math.Clamp((box.Height + media.Height - 1) / media.Height, 1, 64);
            var tiles = new List<Rectangle>();
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                    tiles.Add(new(box.X + column * media.Width, box.Y + row * media.Height, media.Width, media.Height));
            return tiles;
        }
        double scale = scaling == "fit" ? Math.Min(box.Width / (double)media.Width, box.Height / (double)media.Height) : Math.Max(box.Width / (double)media.Width, box.Height / (double)media.Height);
        int width = (int)Math.Round(media.Width * scale), height = (int)Math.Round(media.Height * scale);
        return [new(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height)];
    }
}

// A frame along the edges of the wallpaper area, over all windows. The mouse goes through it.
internal sealed class AreaFrame : Form
{
    static int Thickness => Ui.S(6);
    public AreaFrame()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; BackColor = Color.FromArgb(255, 176, 0); Opacity = 0.92; Text = "Область обоев";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
    protected override bool ShowWithoutActivation => true;
    // Click-through (the window is layered because of Opacity), no taskbar button, never activated.
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x20 | 0x80 | 0x08000000; return p; } }
    public void ShowArea(Rectangle area)
    {
        Bounds = area;
        var region = new Region(new Rectangle(0, 0, area.Width, area.Height));
        region.Exclude(new Rectangle(Thickness, Thickness, Math.Max(0, area.Width - 2 * Thickness), Math.Max(0, area.Height - 2 * Thickness)));
        var old = Region; Region = region; old?.Dispose();
        if (!Visible) Show();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        // A dark inner line keeps the frame visible on light wallpapers too.
        e.Graphics.Clear(BackColor);
        using var pen = new Pen(Color.FromArgb(40, 30, 0), 1);
        e.Graphics.DrawRectangle(pen, Thickness - 1, Thickness - 1, Width - 2 * Thickness + 1, Height - 2 * Thickness + 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// The whole picture of the current wallpaper at a reduced scale, with the screen and the wallpaper area:
// the parts that do not fit are tinted and outlined in red.
internal sealed class CropPreview : Form
{
    static readonly HashSet<string> Readable = new(".jpg .jpeg .png .bmp .gif .tif .tiff".Split(' '), StringComparer.OrdinalIgnoreCase);
    static readonly Color Cut = Color.FromArgb(230, 40, 40);
    static readonly Color AreaColor = Color.FromArgb(64, 214, 190);
    readonly MainForm main;
    Bitmap? image;
    string? loaded, message;
    int Legend => Ui.S(104);

    public CropPreview(MainForm main)
    {
        this.main = main;
        Text = "Как обои обрезаются · Wallsets"; Icon = main.Icon; Font = Ui.Body; BackColor = Color.FromArgb(36, 40, 44); ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; MinimizeBox = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        var area = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(area.Width * 7 / 10, area.Height * 7 / 10 + Legend / 2);
        MinimumSize = Ui.S(520, 400);
        main.LayoutChanged += Changed;
        FormClosed += (_, _) => main.LayoutChanged -= Changed;
        Reload();
    }
    void Changed(object? sender, EventArgs e) { if (main.CurrentPath != loaded) Reload(); Invalidate(); }
    async void Reload()
    {
        var path = loaded = main.CurrentPath;
        if (path == null) { SetImage(null, "Сейчас обои не показываются: отметьте набор в главном окне."); return; }
        message = "Загрузка кадра…"; Invalidate();
        var frame = await Task.Run(() => Frame(path));
        if (IsDisposed || path != loaded) { frame?.Dispose(); return; }
        SetImage(frame, frame == null ? "Не удалось получить кадр этих обоев." : null);
    }
    void SetImage(Bitmap? frame, string? text) { image?.Dispose(); image = frame; message = text; Invalidate(); }
    // Pictures Windows can read are opened directly; video and other formats give one frame through mpv.
    static Bitmap? Frame(string path)
    {
        if (Readable.Contains(Path.GetExtension(path)))
        {
            try { using var stream = new MemoryStream(File.ReadAllBytes(path)); using var picture = Image.FromStream(stream); return new Bitmap(picture); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or OutOfMemoryException) { }
        }
        return Thumbnails.EngineImage(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var screen = main.ScreenBounds;
        var box = main.WallpaperBox;
        bool boxed = box != screen;
        var scaling = main.Settings.Scaling;
        List<Rectangle> places = image == null ? [] : WallpaperLayout.Place(scaling, image.Size, box);
        var canvas = Rectangle.FromLTRB(Ui.S(16), Ui.S(30), ClientSize.Width - Ui.S(16), ClientSize.Height - Legend);
        if (canvas.Width < 20 || canvas.Height < 20) return;
        // Everything that is drawn fits: the screen and the whole picture, also the parts outside the screen.
        var world = screen;
        foreach (var p in places) world = Rectangle.Union(world, p);
        int pad = Math.Max(world.Width, world.Height) / 30;
        world.Inflate(pad, pad);
        float scale = Math.Min(canvas.Width / (float)world.Width, canvas.Height / (float)world.Height);
        float ox = canvas.X + (canvas.Width - world.Width * scale) / 2 - world.X * scale, oy = canvas.Y + (canvas.Height - world.Height * scale) / 2 - world.Y * scale;
        RectangleF Map(Rectangle r) => new(ox + r.X * scale, oy + r.Y * scale, r.Width * scale, r.Height * scale);
        var screenRect = Map(screen); var boxRect = Map(box);

        // Outside the wallpaper the desktop is black.
        g.FillRectangle(Brushes.Black, screenRect);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        if (image != null)
        {
            // The whole picture, dimmed; then the visible part at full brightness.
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4f });
            foreach (var p in places)
            {
                var r = Map(p);
                g.DrawImage(image, [r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom)], new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
            }
            g.SetClip(boxRect);
            foreach (var p in places) g.DrawImage(image, Map(p));
            g.ResetClip();
            // What is cut off: tinted red outside the area, with the picture's outline dashed.
            using (var outside = new Region(ClientRectangle))
            {
                outside.Exclude(boxRect);
                g.Clip = outside;
                using var tint = new SolidBrush(Color.FromArgb(70, Cut));
                using var dashed = new Pen(Cut, 2) { DashStyle = DashStyle.Dash };
                foreach (var p in places) { var r = Map(p); g.FillRectangle(tint, r); g.DrawRectangle(dashed, r.X, r.Y, r.Width, r.Height); }
                g.ResetClip();
            }
            // Solid red lines where the picture is cut at the edges of the area.
            using var line = new Pen(Cut, 3);
            foreach (var p in places)
            {
                int top = Math.Max(p.Top, box.Top), bottom = Math.Min(p.Bottom, box.Bottom), left = Math.Max(p.Left, box.Left), right = Math.Min(p.Right, box.Right);
                if (top >= bottom || left >= right) continue;
                if (p.Left < box.Left) g.DrawLine(line, ox + box.Left * scale, oy + top * scale, ox + box.Left * scale, oy + bottom * scale);
                if (p.Right > box.Right) g.DrawLine(line, ox + box.Right * scale, oy + top * scale, ox + box.Right * scale, oy + bottom * scale);
                if (p.Top < box.Top) g.DrawLine(line, ox + left * scale, oy + box.Top * scale, ox + right * scale, oy + box.Top * scale);
                if (p.Bottom > box.Bottom) g.DrawLine(line, ox + left * scale, oy + box.Bottom * scale, ox + right * scale, oy + box.Bottom * scale);
            }
        }
        using (var screenPen = new Pen(Color.FromArgb(200, 205, 210), 1)) g.DrawRectangle(screenPen, screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height);
        Label(g, $"Экран {screen.Width} × {screen.Height}", screenRect, Color.FromArgb(200, 205, 210));
        if (boxed)
        {
            using var areaPen = new Pen(AreaColor, 2);
            g.DrawRectangle(areaPen, boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);
            Label(g, $"Область обоев {box.Width} × {box.Height}", boxRect, AreaColor);
        }
        var legend = Rectangle.FromLTRB(Ui.S(16), ClientSize.Height - Legend + Ui.S(8), ClientSize.Width - Ui.S(16), ClientSize.Height - Ui.S(8));
        TextRenderer.DrawText(g, message == null ? Describe(scaling, places, box) : "", Font, legend, Color.White, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        if (message != null) TextRenderer.DrawText(g, message, Ui.Strong, canvas, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
    void Label(Graphics g, string text, RectangleF over, Color color)
    {
        var size = TextRenderer.MeasureText(text, Ui.Small);
        var at = new Point((int)over.X, (int)over.Y - size.Height - 2);
        if (at.Y < 2) at = new Point((int)over.X + 4, (int)over.Y + 4);
        TextRenderer.DrawText(g, text, Ui.Small, at, color, TextFormatFlags.NoPrefix);
    }
    string Describe(string scaling, List<Rectangle> places, Rectangle box)
    {
        if (image == null || places.Count == 0) return "";
        var name = main.CurrentPath is { } path ? Path.GetFileNameWithoutExtension(path) : "";
        var lines = new List<string> { $"{name} · {image.Width} × {image.Height} · масштаб «{WallpaperLayout.Title(scaling)}»" };
        int cutLeft = places.Max(p => box.Left - p.Left), cutTop = places.Max(p => box.Top - p.Top), cutRight = places.Max(p => p.Right - box.Right), cutBottom = places.Max(p => p.Bottom - box.Bottom);
        var cut = Sides(cutLeft, cutTop, cutRight, cutBottom);
        if (scaling == "stretch") lines.Add("Картинка растянута на всю область без сохранения пропорций: ничего не обрезано, но изображение может быть сжато или вытянуто.");
        else if (cut.Length > 0) lines.Add("Красным — что обрезано и не видно: " + cut + " (в пикселях экрана).");
        else lines.Add("Ничего не обрезано: картинка видна целиком.");
        if (scaling == "tile" && places.Count > 1) lines.Add($"Повторяется плиткой: {places.Count} копий.");
        if (places.Count == 1 && Sides(-cutLeft, -cutTop, -cutRight, -cutBottom) is { Length: > 0 } bars) lines.Add("Чёрные поля: " + bars + ".");
        return string.Join(Environment.NewLine, lines);
    }
    static string Sides(int left, int top, int right, int bottom) =>
        string.Join(", ", new[] { ("слева", left), ("сверху", top), ("справа", right), ("снизу", bottom) }.Where(s => s.Item2 > 0).Select(s => $"{s.Item1} {s.Item2}"));
    protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }
}
