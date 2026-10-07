using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;

namespace Wallsets;

internal sealed record ThumbnailItem(string Path, string SetName)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed class ThumbnailGrid : ScrollableControl
{
    readonly Thumbnails thumbnails = new();
    readonly Dictionary<string, Image?> images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PointF> positions = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer animation = new() { Interval = 16 };
    readonly ToolTip tooltip = new();
    // Tiles per row in a maximized main window for each size: small, medium, large, extra large.
    public static readonly int[] ColumnsWhenMaximized = [12, 9, 6, 4];
    List<ThumbnailItem> items = [];
    List<ThumbnailItem>? preview;
    string? selected, dragged, hovered, playing;
    Point press, pointer, grab;
    bool dragging, committing;
    int sizeIndex = 1, tileWidth;
    bool showNames = true, showSetNames = true;
    Font? iconFont, subFont;
    public event EventHandler? OrderChanged;
    public event EventHandler? SizeIndexChanged;
    public event EventHandler? ItemActivated;
    public IReadOnlyList<ThumbnailItem> Items => items;
    public string? SelectedPath => selected;
    public bool IsDragging => dragging;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => items.FindIndex(i => i.Path == selected); set { selected = value >= 0 && value < items.Count ? items[value].Path : null; Invalidate(); } }
    float ScaleFactor => DeviceDpi / 96f;
    int Gap => (int)(10 * ScaleFactor);
    int TileWidth => tileWidth > 0 ? tileWidth : tileWidth = MeasureTileWidth();
    int ImageHeight => (int)((TileWidth - 8) * 9f / 16);
    // Optional caption lines under the picture: file name and set name.
    int TileHeight => 8 + ImageHeight + (showNames || showSetNames ? (int)(6 * ScaleFactor) : 0) + (showNames ? (int)(22 * ScaleFactor) : 0) + (showSetNames ? (int)(19 * ScaleFactor) : 0);
    public int Columns => Math.Max(1, (ClientSize.Width - Gap) / (TileWidth + Gap));
    List<ThumbnailItem> VisualItems => preview ?? items;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SizeIndex { get => sizeIndex; set { CancelDrag(); sizeIndex = Math.Clamp(value, 0, ColumnsWhenMaximized.Length - 1); tileWidth = 0; positions.Clear(); UpdateExtent(); Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowNames { get => showNames; set { if (showNames != value) { showNames = value; Relayout(); } } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSetNames { get => showSetNames; set { if (showSetNames != value) { showSetNames = value; Relayout(); } } }
    void Relayout() { CancelDrag(); positions.Clear(); UpdateExtent(); Invalidate(); }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Playing { get => playing; set { if (playing != value) { playing = value; Invalidate(); } } }
    public ThumbnailGrid()
    {
        AutoScroll = true; TabStop = true; BackColor = Ui.Surface; ForeColor = Ui.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        animation.Tick += (_, _) => Animate();
        AccessibleName = "Обои: слева направо, сверху вниз";
    }
    public static int TileWidthFor(int maximizedWidth, int columns, int gap, int minimum) => Math.Max(minimum, (maximizedWidth - gap) / columns - gap);
    int MeasureTileWidth()
    {
        // Tiles are sized from the width the grid gets in a maximized window, so every size keeps
        // its column count there on any screen resolution and scaling.
        int width = ClientSize.Width;
        if (FindForm() is { WindowState: not FormWindowState.Minimized } form && form.ClientSize.Width > 0)
            width = Screen.FromRectangle(form.Bounds).WorkingArea.Width - (form.ClientSize.Width - Width) - SystemInformation.VerticalScrollBarWidth;
        return TileWidthFor(width, ColumnsWhenMaximized[sizeIndex], Gap, (int)(72 * ScaleFactor));
    }
    public void UpdateMetrics()
    {
        int width = MeasureTileWidth();
        if (width == tileWidth) return;
        CancelDrag(); tileWidth = width; positions.Clear(); UpdateExtent(); Invalidate();
    }
    public void SetItems(IEnumerable<ThumbnailItem> value)
    {
        var list = value.ToList();
        // The same order coming back after a drop keeps the landing animation running.
        if (list.SequenceEqual(items)) { Invalidate(); return; }
        if (dragging) CancelDrag();
        items = list;
        if (!items.Any(i => i.Path == selected)) selected = null;
        var paths = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in images.Keys.Where(p => !paths.Contains(p)).ToList()) { images[path]?.Dispose(); images.Remove(path); }
        positions.Clear(); UpdateExtent(); Invalidate();
    }
    void UpdateExtent() => AutoScrollMinSize = new Size(0, Gap + (int)Math.Ceiling(items.Count / (double)Columns) * (TileHeight + Gap));
    Rectangle Slot(int index) => new(Gap + index % Columns * (TileWidth + Gap), Gap + index / Columns * (TileHeight + Gap), TileWidth, TileHeight);
    Rectangle ScreenRect(Rectangle rectangle) { rectangle.Offset(AutoScrollPosition); return rectangle; }
    int Hit(Point point)
    {
        point.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);
        int col = (point.X - Gap) / (TileWidth + Gap), row = (point.Y - Gap) / (TileHeight + Gap);
        int index = row * Columns + col;
        return point.X >= Gap && point.Y >= Gap && col < Columns && index < VisualItems.Count && Slot(index).Contains(point) ? index : -1;
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); CancelDrag(); UpdateMetrics(); positions.Clear(); UpdateExtent(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var list = VisualItems;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i]; var slot = Slot(i);
            if (item.Path == dragged && dragging)
            {
                // Where the dragged tile will land.
                var placeholder = ScreenRect(slot); placeholder.Inflate(-2, -2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Ui.RoundedPath(placeholder, (int)(7 * ScaleFactor));
                using var fill = new SolidBrush(Color.FromArgb(232, 244, 242)); e.Graphics.FillPath(fill, path);
                using var pen = new Pen(Ui.Accent, 2) { DashStyle = DashStyle.Dash }; e.Graphics.DrawPath(pen, path);
                e.Graphics.SmoothingMode = SmoothingMode.None;
                continue;
            }
            var pos = positions.TryGetValue(item.Path, out var animated) ? animated : new PointF(slot.X, slot.Y);
            var rect = ScreenRect(new Rectangle((int)pos.X, (int)pos.Y, slot.Width, slot.Height));
            if (rect.IntersectsWith(ClientRectangle)) DrawTile(e.Graphics, item, rect, i, false);
        }
        if (dragging && dragged != null)
        {
            var item = list.First(i => i.Path == dragged);
            DrawTile(e.Graphics, item, new Rectangle(pointer.X - grab.X, pointer.Y - grab.Y, TileWidth, TileHeight), list.IndexOf(item), true);
        }
        if (items.Count == 0) TextRenderer.DrawText(e.Graphics, "Отметьте набор слева — здесь появятся его обои", Font, ClientRectangle, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
    void DrawTile(Graphics g, ThumbnailItem item, Rectangle rect, int index, bool ghost)
    {
        if (!images.ContainsKey(item.Path)) { images[item.Path] = null; LoadThumbnail(item.Path); }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = (int)(7 * ScaleFactor);
        if (ghost)
        {
            var shadow = rect; shadow.Offset(4, 6);
            using var shadowPath = Ui.RoundedPath(shadow, radius); using var shadowBrush = new SolidBrush(Color.FromArgb(55, Color.Black)); g.FillPath(shadowBrush, shadowPath);
        }
        bool marked = item.Path == selected || ghost;
        using (var tilePath = Ui.RoundedPath(rect, radius))
        using (var bg = new SolidBrush(marked ? Ui.AccentLight : Color.FromArgb(244, 246, 248)))
            g.FillPath(bg, tilePath);
        g.SmoothingMode = SmoothingMode.None;
        var imageRect = new Rectangle(rect.X + 4, rect.Y + 4, rect.Width - 8, ImageHeight);
        if (images.TryGetValue(item.Path, out var image) && image != null)
        {
            if (ghost)
            {
                using var attributes = new ImageAttributes(); attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.88f });
                g.DrawImage(image, imageRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
            else g.DrawImage(image, imageRect);
        }
        else
        {
            using var placeholder = new SolidBrush(Color.FromArgb(226, 231, 235)); g.FillRectangle(placeholder, imageRect);
            iconFont ??= new Font(Program.IconFont, 22);
            TextRenderer.DrawText(g, "\uEB9F", iconFont, imageRect, Color.FromArgb(113, 128, 135), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        var numberRect = new Rectangle(imageRect.X + 4, imageRect.Y + 4, (int)(30 * ScaleFactor), (int)(20 * ScaleFactor));
        using (var badge = new SolidBrush(Color.FromArgb(185, 20, 25, 27))) g.FillRectangle(badge, numberRect);
        TextRenderer.DrawText(g, (index + 1).ToString(), Font, numberRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        int y = imageRect.Bottom + (int)(5 * ScaleFactor);
        if (showNames)
        {
            var label = new Rectangle(rect.X + 6, y, rect.Width - 12, (int)(22 * ScaleFactor));
            TextRenderer.DrawText(g, item.Title, Font, label, ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            y = label.Bottom;
        }
        if (showSetNames)
        {
            var sub = new Rectangle(rect.X + 6, y, rect.Width - 12, (int)(19 * ScaleFactor));
            subFont ??= new Font(Font.FontFamily, Math.Max(8, Font.Size - 1));
            TextRenderer.DrawText(g, item.SetName, subFont, sub, Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
        if (item.Path == selected || item.Path == playing || ghost)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(item.Path == playing && !ghost ? Color.FromArgb(206, 128, 32) : Ui.Accent, ghost ? 3 : 2);
            rect.Inflate(-1, -1); using var border = Ui.RoundedPath(rect, radius); g.DrawPath(pen, border);
            g.SmoothingMode = SmoothingMode.None;
        }
    }
    async void LoadThumbnail(string path)
    {
        var bytes = await thumbnails.Get(path);
        if (IsDisposed || Disposing || !images.ContainsKey(path)) return;
        if (bytes != null)
        {
            try { using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream); images[path] = new Bitmap(source); }
            catch (ArgumentException) { }
        }
        Invalidate();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        Focus(); var index = Hit(e.Location); selected = index >= 0 ? items[index].Path : null;
        if (index >= 0) { press = pointer = e.Location; var rect = ScreenRect(Slot(index)); grab = new Point(e.X - rect.X, e.Y - rect.Y); dragged = selected; Capture = true; }
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointer = e.Location;
        if (dragged != null && e.Button == MouseButtons.Left)
        {
            if (!dragging && Math.Abs(pointer.X - press.X) + Math.Abs(pointer.Y - press.Y) > SystemInformation.DragSize.Width)
            {
                dragging = true; preview = [..items]; tooltip.Hide(this); tooltip.SetToolTip(this, ""); hovered = null; Cursor = Cursors.SizeAll;
                for (int i = 0; i < items.Count; i++) positions[items[i].Path] = Slot(i).Location;
                animation.Start();
            }
            if (dragging) { UpdateTarget(); Invalidate(); }
        }
        else
        {
            var index = Hit(e.Location); var path = index >= 0 ? items[index].Path : null;
            if (hovered != path) { hovered = path; tooltip.SetToolTip(this, path == null ? "" : Path.GetFileName(path)); }
        }
    }
    void UpdateTarget()
    {
        if (preview == null || dragged == null) return;
        int x = Math.Clamp(pointer.X - AutoScrollPosition.X - Gap, 0, Math.Max(0, Columns * (TileWidth + Gap) - 1));
        int y = Math.Max(0, pointer.Y - AutoScrollPosition.Y - Gap);
        int target = Math.Clamp(y / (TileHeight + Gap) * Columns + x / (TileWidth + Gap), 0, preview.Count - 1);
        int from = preview.FindIndex(i => i.Path == dragged);
        if (from != target) { var item = preview[from]; preview.RemoveAt(from); preview.Insert(target, item); }
    }
    void Animate()
    {
        bool moving = false;
        if (dragging)
        {
            int delta = pointer.Y < 32 ? -14 : pointer.Y > ClientSize.Height - 32 ? 14 : 0;
            if (delta != 0) { AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y + delta)); UpdateTarget(); }
        }
        for (int i = 0; i < VisualItems.Count; i++)
        {
            string key = VisualItems[i].Path; var target = Slot(i).Location;
            var old = positions.TryGetValue(key, out var p) ? p : target;
            float dx = target.X - old.X, dy = target.Y - old.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 1) { positions[key] = new PointF(old.X + dx * .3f, old.Y + dy * .3f); moving = true; }
            else positions[key] = target;
        }
        Invalidate(); if (!dragging && !moving) animation.Stop();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
        bool changed = dragging && preview != null && !items.SequenceEqual(preview);
        if (dragging && dragged != null)
        {
            // The dropped tile glides from the cursor into its slot.
            positions[dragged] = new PointF(pointer.X - grab.X - AutoScrollPosition.X, pointer.Y - grab.Y - AutoScrollPosition.Y);
            animation.Start();
        }
        if (changed) items = preview!;
        committing = true; preview = null; dragging = false; dragged = null; Capture = false; Cursor = Cursors.Default; committing = false;
        if (changed) OrderChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !committing) CancelDrag(); }
    void CancelDrag() { dragging = false; dragged = null; preview = null; animation.Stop(); positions.Clear(); Cursor = Cursors.Default; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Ctrl + wheel changes the thumbnail size, like in Explorer.
        if ((ModifierKeys & Keys.Control) == 0) { base.OnMouseWheel(e); return; }
        int value = Math.Clamp(sizeIndex + Math.Sign(e.Delta), 0, ColumnsWhenMaximized.Length - 1);
        if (value != sizeIndex) { SizeIndex = value; SizeIndexChanged?.Invoke(this, EventArgs.Empty); }
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    protected override void OnFontChanged(EventArgs e) { subFont?.Dispose(); subFont = null; base.OnFontChanged(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (Hit(e.Location) >= 0) ItemActivated?.Invoke(this, EventArgs.Empty); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { CancelDrag(); Capture = false; e.Handled = true; }
        if (e.KeyCode == Keys.Enter) { ItemActivated?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        int delta = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Up ? -Columns : e.KeyCode == Keys.Down ? Columns : 0;
        if (delta != 0 && items.Count > 0) { SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, items.Count - 1); var r = Slot(SelectedIndex); if (ScreenRect(r).Top < 0 || ScreenRect(r).Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, Math.Max(0, r.Y - Gap)); e.Handled = true; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { animation.Dispose(); tooltip.Dispose(); thumbnails.Dispose(); iconFont?.Dispose(); subFont?.Dispose(); foreach (var image in images.Values) image?.Dispose(); images.Clear(); }
        base.Dispose(disposing);
    }
}
