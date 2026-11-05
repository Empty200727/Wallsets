namespace Wallsets;

// What the optimizer has done: videos with a lighter copy on the left, the ones still to do on the right, as
// small numbered tiles like in the main window. It follows the optimizer while it works.
internal sealed class OptimizerForm : Form
{
    readonly MainForm main;
    readonly Label heading = new() { Dock = DockStyle.Fill, Font = Ui.Heading, ForeColor = Ui.Text, BackColor = Ui.Window, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly Label status = new() { Dock = DockStyle.Fill, Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Window, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
    readonly Label doneTitle = Title(), todoTitle = Title();
    readonly ThumbnailGrid done = Grid("Облегчённых копий пока нет"), todo = Grid("Всё облегчено");
    readonly ComboBox size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(140), Anchor = AnchorStyles.Right };
    readonly Label note = new() { Dock = DockStyle.Fill, Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Window, UseMnemonic = false };

    public OptimizerForm(MainForm main)
    {
        this.main = main;
        Text = "Облегчение видео · Wallsets"; Icon = main.Icon; Font = Ui.Body; BackColor = Ui.Window; ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false; MinimizeBox = false;
        var area = Screen.FromControl(main).WorkingArea;
        ClientSize = new Size(Math.Min(Ui.S(1100), area.Width - Ui.S(60)), Math.Min(Ui.S(680), area.Height - Ui.S(80)));
        MinimumSize = new Size(Ui.S(620), Ui.S(420));
        // Shown without a dialog, so it is centred by hand over the window that opened it.
        Load += (_, _) =>
        {
            var over = Owner?.Bounds ?? area;
            Location = new Point(Math.Clamp(over.X + (over.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(over.Y + (over.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = Ui.P(16, 10, 16, 12), BackColor = Ui.Window };
        layout.ColumnStyles.Add(new(SizeType.Percent, 50)); layout.ColumnStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(36))); layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(26)));
        layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(32))); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(44)));
        layout.Controls.Add(heading, 0, 0);
        // Tile size, like "Превью" in the main window (Ctrl + wheel over the tiles works too).
        var sizeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, BackColor = Ui.Window, Margin = Padding.Empty };
        var sizeLabel = Ui.Caption("Превью", Ui.Body, Ui.Muted); sizeLabel.BackColor = Ui.Window; sizeLabel.Anchor = AnchorStyles.Left; sizeLabel.Margin = Ui.P(0, 6, 8, 0);
        size.Items.AddRange(["Мелкие", "Средние", "Крупные"]);
        size.SelectedIndex = Math.Clamp(main.Settings.OptimizerThumbnails, 0, 2);
        sizeRow.Controls.AddRange([sizeLabel, size]);
        layout.Controls.Add(sizeRow, 1, 0);
        done.SizeIndex = todo.SizeIndex = size.SelectedIndex;
        size.SelectedIndexChanged += (_, _) => SetSize(size.SelectedIndex);
        done.SizeIndexChanged += (_, _) => SetSize(done.SizeIndex);
        todo.SizeIndexChanged += (_, _) => SetSize(todo.SizeIndex);
        layout.Controls.Add(status, 0, 1); layout.SetColumnSpan(status, 2);
        layout.Controls.Add(doneTitle, 0, 2); layout.Controls.Add(todoTitle, 1, 2);
        layout.Controls.Add(Framed(done, Ui.P(0, 0, 6, 0)), 0, 3); layout.Controls.Add(Framed(todo, Ui.P(6, 0, 0, 0)), 1, 3);
        layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 2);
        Controls.Add(layout);

        main.StateChanged += Changed;
        FormClosed += (_, _) => main.StateChanged -= Changed;
        Shown += (_, _) => { done.UpdateMetrics(); todo.UpdateMetrics(); Fill(); };
        Fill();
    }
    static Label Title() => new() { Dock = DockStyle.Fill, Font = Ui.Strong, ForeColor = Ui.Text, BackColor = Ui.Window, TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false, Padding = Ui.P(2, 0, 0, 4) };
    static ThumbnailGrid Grid(string empty) => new() { EmptyText = empty, Dock = DockStyle.Fill, Margin = Padding.Empty, ReadOnly = true, SizeIndex = 0, ShowNames = false, ShowSetNames = false };
    static Card Framed(Control grid, Padding margin)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = margin, Padding = Ui.P(5) };
        card.Controls.Add(grid);
        return card;
    }
    void Changed(object? sender, EventArgs e) { if (!IsDisposed) Fill(); }
    // The grids know four sizes; this window offers the first three.
    void SetSize(int index)
    {
        index = Math.Clamp(index, 0, 2);
        if (done.SizeIndex != index) done.SizeIndex = index;
        if (todo.SizeIndex != index) todo.SizeIndex = index;
        if (size.SelectedIndex != index) size.SelectedIndex = index;
        if (main.Settings.OptimizerThumbnails == index) return;
        main.Settings.OptimizerThumbnails = index; main.SaveSettings();
    }

    void Fill()
    {
        int fps = main.Settings.MaxFps;
        heading.Text = fps > 0 ? $"До {fps} кадров в секунду" : "Каждый кадр видео (частота не ограничена)";
        var screen = Screen.PrimaryScreen!.Bounds;
        status.Text = $"Копии делаются под экран {screen.Width}×{screen.Height}. " + main.OptimizerStatus;
        var items = main.OptimizerItems;
        var ready = items.Where(i => i.State == Optimizer.State.Ready).ToList();
        // The one being made first, then the queue, the unchecked ones and those that failed.
        var rest = items.Where(i => i.State is Optimizer.State.Working or Optimizer.State.Waiting or Optimizer.State.Unknown or Optimizer.State.Failed)
            .OrderBy(i => i.State switch { Optimizer.State.Working => 0, Optimizer.State.Waiting => 1, Optimizer.State.Unknown => 2, _ => 3 }).ToList();
        done.SetItems(ready.Select(i => Tile(i, null, false, "Готова облегчённая копия")));
        todo.SetItems(rest.Select(i => i.State switch
        {
            Optimizer.State.Working => Tile(i, i.Percent + "%", false, "Сейчас пережимается"),
            Optimizer.State.Failed => Tile(i, "ошибка", true, "Не удалось облегчить: играет оригинал (подробности в errors.log)"),
            Optimizer.State.Unknown => Tile(i, "?", false, "Ещё не проверено"),
            _ => Tile(i, null, false, "В очереди на облегчение"),
        }));
        todo.Playing = rest.FirstOrDefault(i => i.State == Optimizer.State.Working) is { Path: { } path } ? path + "#0" : null;
        doneTitle.Text = $"Облегчены — {ready.Count}";
        todoTitle.Text = $"Не облегчены — {rest.Count}";
        int light = items.Count(i => i.State == Optimizer.State.Light), kept = items.Count(i => i.State == Optimizer.State.Kept);
        var parts = new List<string>();
        if (!main.Settings.Optimize) parts.Add("Облегчение выключено: все видео играют как есть.");
        if (light > 0) parts.Add($"Ещё {light} видео облегчать не нужно: они не больше экрана" + (fps > 0 ? $" и не чаще {fps} кадров в секунду." : "."));
        if (kept > 0) parts.Add($"{kept} видео (HDR или повёрнутые) всегда играют как есть.");
        parts.Add("Наведите на превью, чтобы увидеть имя файла.");
        note.Text = string.Join(" ", parts);
    }
    static ThumbnailItem Tile(Optimizer.Item item, string? badge, bool alert, string tip) =>
        new(item.Path, "", item.Path, 0, badge, alert, tip);
}
