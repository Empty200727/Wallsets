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
    List<ThumbnailItem> items = [];
    List<ThumbnailItem>? preview;
    string? selected, dragged, hovered, playing;
    Point press, pointer, grab;
    bool dragging, committing;
    int sizeIndex = 1;
    public event EventHandler? OrderChanged;
    public event EventHandler? ItemActivated;
    public IReadOnlyList<ThumbnailItem> Items => items;
    public string? SelectedPath => selected;
    public bool IsDragging => dragging;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => items.FindIndex(i => i.Path == selected); set { selected = value >= 0 && value < items.Count ? items[value].Path : null; Invalidate(); } }
    float ScaleFactor => DeviceDpi / 96f;
    int Gap => (int)(10 * ScaleFactor);
    int TileWidth => (int)(new[] { 100, 132, 180, 236 }[sizeIndex] * ScaleFactor);
    int TileHeight => (int)(TileWidth * 9f / 16) + (int)(53 * ScaleFactor);
    public int Columns => Math.Max(1, (ClientSize.Width - Gap) / (TileWidth + Gap));
    List<ThumbnailItem> VisualItems => preview ?? items;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SizeIndex { get => sizeIndex; set { CancelDrag(); sizeIndex = Math.Clamp(value, 0, 3); positions.Clear(); UpdateExtent(); Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Playing { get => playing; set { if (playing != value) { playing = value; Invalidate(); } } }
    public ThumbnailGrid()
    {
        AutoScroll = true; TabStop = true; BackColor = Color.White;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        animation.Tick += (_, _) => Animate();
        AccessibleName = "Обои: слева направо, сверху вниз";
    }
    public void SetItems(IEnumerable<ThumbnailItem> value)
    {
        if (dragging) CancelDrag();
        items = value.ToList();
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
    protected override void OnResize(EventArgs e) { base.OnResize(e); CancelDrag(); positions.Clear(); UpdateExtent(); }
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
                using var fill = new SolidBrush(Color.FromArgb(227, 240, 238)); e.Graphics.FillRectangle(fill, ScreenRect(slot));
                using var pen = new Pen(Color.FromArgb(31, 124, 112), 2) { DashStyle = DashStyle.Dash }; var placeholder = ScreenRect(slot); placeholder.Inflate(-2, -2); e.Graphics.DrawRectangle(pen, placeholder);
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
        if (items.Count == 0) TextRenderer.DrawText(e.Graphics, "Нет обоев", Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    void DrawTile(Graphics g, ThumbnailItem item, Rectangle rect, int index, bool ghost)
    {
        if (!images.ContainsKey(item.Path)) { images[item.Path] = null; LoadThumbnail(item.Path); }
        if (ghost)
        {
            using var shadow = new SolidBrush(Color.FromArgb(65, Color.Black)); var r = rect; r.Offset(5, 7); g.FillRectangle(shadow, r);
        }
        using var bg = new SolidBrush(item.Path == selected || ghost ? Color.FromArgb(224, 239, 237) : Color.FromArgb(245, 247, 248));
        g.FillRectangle(bg, rect);
        var imageRect = new Rectangle(rect.X + 3, rect.Y + 3, rect.Width - 6, (int)((rect.Width - 6) * 9f / 16));
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
            using var placeholder = new SolidBrush(Color.FromArgb(225, 230, 234)); g.FillRectangle(placeholder, imageRect);
            using var iconFont = new Font("Segoe Fluent Icons", 22);
            TextRenderer.DrawText(g, "\uEB9F", iconFont, imageRect, Color.FromArgb(113, 128, 135), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        var numberRect = new Rectangle(imageRect.X + 4, imageRect.Y + 4, (int)(32 * ScaleFactor), (int)(20 * ScaleFactor));
        using var badge = new SolidBrush(Color.FromArgb(180, 20, 25, 27)); g.FillRectangle(badge, numberRect);
        TextRenderer.DrawText(g, (index + 1).ToString(), Font, numberRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var label = new Rectangle(rect.X + 4, imageRect.Bottom + 4, rect.Width - 8, (int)(22 * ScaleFactor));
        TextRenderer.DrawText(g, item.Title, Font, label, ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var sub = new Rectangle(label.X, label.Bottom, label.Width, (int)(19 * ScaleFactor));
        using var subFont = new Font(Font.FontFamily, Math.Max(8, Font.Size - 1));
        TextRenderer.DrawText(g, item.SetName, subFont, sub, Color.DimGray, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (item.Path == selected || item.Path == playing || ghost)
        {
            using var pen = new Pen(item.Path == playing ? Color.FromArgb(183, 113, 30) : Color.FromArgb(31, 124, 112), ghost ? 3 : 2);
            rect.Inflate(-1, -1); g.DrawRectangle(pen, rect);
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
                dragging = true; preview = [..items]; tooltip.Hide(this); Cursor = Cursors.SizeAll;
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
        if (changed) items = preview!;
        committing = true; preview = null; dragging = false; dragged = null; Capture = false; Cursor = Cursors.Default; committing = false;
        if (changed) OrderChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !committing) CancelDrag(); }
    void CancelDrag() { dragging = false; dragged = null; preview = null; animation.Stop(); positions.Clear(); Cursor = Cursors.Default; Invalidate(); }
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
        if (disposing) { animation.Dispose(); tooltip.Dispose(); thumbnails.Dispose(); foreach (var image in images.Values) image?.Dispose(); images.Clear(); }
        base.Dispose(disposing);
    }
}
