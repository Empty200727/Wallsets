using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Wallsets;

internal sealed class MainForm : Form
{
    readonly string setsRoot = Path.Combine(Program.Root, "Наборы");
    readonly string settingsPath = Program.SettingsPath;
    readonly Settings settings;
    readonly Player player = new(Program.Root);
    // Plain check boxes: a set is chosen only by its tick, there is no separate highlighted row.
    readonly FlowLayoutPanel sets = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = Padding.Empty, BackColor = Ui.Surface };
    readonly ThumbnailGrid files = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    readonly ComboBox queueView = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System };
    readonly ComboBox thumbnailSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(140) };
    readonly ComboBox sorting = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(150) };
    readonly NumericUpDown interval = new() { Minimum = 5, Maximum = 86400, Increment = 5, Width = Ui.S(82), ThousandsSeparator = true, TextAlign = HorizontalAlignment.Right };
    readonly Label count = Ui.Caption("", Ui.Body, Ui.Muted);
    // Next to the playback buttons only the countdown is shown (or a problem, in red); the current
    // wallpaper's name is in its tooltip and in the tray icon's tooltip.
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.Strong, ForeColor = Ui.Text, BackColor = Ui.Surface, UseMnemonic = false, Margin = Ui.P(10, 0, 10, 0) };
    string? statusTip;
    readonly CheckBox sound = Ui.ToggleButton("", "Проигрывать с музыкой");
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, TickStyle = TickStyle.None, AutoSize = false, Height = Ui.S(30), Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Ui.Surface, AccessibleName = "Громкость музыки" };
    readonly Label volumeText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Surface, ForeColor = Ui.Muted };
    readonly ToolTip tips = new();
    readonly NotifyIcon tray = new() { Text = "Wallsets", Visible = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 700 };
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    readonly System.Windows.Forms.Timer pauseCheck = new() { Interval = 150 };
    readonly List<FileSystemWatcher> watchers = [];
    readonly CancellationTokenSource cancellation = new();
    readonly SemaphoreSlim actions = new(1);
    readonly bool startInTray;
    readonly Button pause, next, previous;
    readonly ToolStripMenuItem trayPause = new("Пауза");
    readonly ToolStripMenuItem traySound = new("Проигрывать с музыкой");
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly Dictionary<string, bool> hotkeyState = [];
    readonly Native.WinEventProc windowEvents;
    IntPtr windowHook;
    List<MediaSet> library = [];
    List<string> queue = [];
    string? currentFile, lastFrame;
    bool updating, busy, exiting, cleaned, userPaused, autoPaused, locked, displayOff, allowShow;
    long nextChange, nextHealth, nextFrame;
    int attachRetries = 10;
    bool playbackFault, volumeSending, volumePending;
    string? problem, hotkeyProblem;
    Form? floating;
    Button? floatingPause;
    IntPtr powerNotification;
    internal Settings Settings => settings;
    internal event EventHandler? StateChanged;

    public MainForm(bool startInTray)
    {
        this.startInTray = startInTray;
        Directory.CreateDirectory(setsRoot);
        settings = Settings.Load(settingsPath);
        player.Sound = settings.Music; player.Volume = settings.MusicVolume; player.Scaling = settings.Scaling;
        Text = "Wallsets · Живые обои";
        AutoScaleMode = AutoScaleMode.None; Font = Ui.Body; BackColor = Ui.Window; ForeColor = Ui.Text;
        Icon = LoadIcon(0); tray.Icon = LoadIcon(SystemInformation.SmallIconSize.Width);
        var area = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(Math.Min(Ui.S(1240), area.Width - Ui.S(40)), Math.Min(Ui.S(780), area.Height - Ui.S(60)));
        MinimumSize = new Size(Math.Min(Ui.S(940), area.Width), Math.Min(Ui.S(600), area.Height));
        StartPosition = FormStartPosition.CenterScreen;

        previous = Ui.IconButton("", "", tips, () => Run(Previous), 13);
        pause = Ui.IconButton("", "", tips, TogglePause, 14);
        next = Ui.IconButton("", "", tips, () => Run(Next), 13);
        BuildLayout();
        BuildTray();

        sets.Resize += (_, _) => { foreach (Control box in sets.Controls) box.Width = SetWidth; };
        sorting.SelectedIndexChanged += (_, _) => { if (!updating) ChangeSort(false); };
        queueView.SelectedIndexChanged += (_, _) => Run(ChangeQueueMode);
        thumbnailSize.SelectedIndexChanged += (_, _) => { files.SizeIndex = settings.ThumbnailSize = thumbnailSize.SelectedIndex; Save(); };
        files.SizeIndexChanged += (_, _) => thumbnailSize.SelectedIndex = files.SizeIndex;
        files.ItemActivated += (_, _) => ShowSelected();
        files.OrderChanged += (_, _) => StoreViewOrder(files.Items.ToList(), "manual");
        Move += (_, _) => files.UpdateMetrics();
        sound.CheckedChanged += (_, _) => SetSound(sound.Checked);
        volume.ValueChanged += (_, _) => { settings.MusicVolume = player.Volume = volume.Value; UpdateSound(); ApplyVolume(); saveTimer.Stop(); saveTimer.Start(); };
        interval.ValueChanged += (_, _) => { settings.IntervalSeconds = (double)interval.Value; ResetDeadline(); Save(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        pauseCheck.Tick += (_, _) => { pauseCheck.Stop(); CheckAutoPause(); };
        timer.Tick += (_, _) => { if (!busy && !exiting) Run(Tick); };
        scanTimer.Tick += (_, _) => { scanTimer.Stop(); if (busy || files.IsDragging) { scanTimer.Start(); return; } Run(Rescan); };
        // Hiding to the tray on the close button; real shutdown happens in Exit or when Windows ends the session.
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => Cleanup();
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        // Foreground and minimize events re-check the automatic pause at once instead of on the next second.
        windowEvents = (_, _, _, _, _, _, _) => { pauseCheck.Stop(); pauseCheck.Start(); };
        windowHook = Native.SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, windowEvents, 0, 0, 0x0002);
        _ = Handle;
        RegisterHotkeys();
        // Combinations taken by another program are reported once, near the clock; the settings show the details.
        if (hotkeyProblem != null) tray.ShowBalloonTip(8000, "Wallsets: горячие клавиши", hotkeyProblem, ToolTipIcon.Info);
        var displayGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        powerNotification = Native.RegisterPowerSettingNotification(Handle, ref displayGuid, 0);
        _ = Task.Run(Server);
        UpdateSound(); UpdateStatus();
        // Start with the keyboard in the wallpaper grid rather than on the first toolbar button.
        ActiveControl = files;
        BeginInvoke(() => Run(async () => { await Rescan(); timer.Start(); UpdateFloating(); }));
    }

    static Icon LoadIcon(int size)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Wallsets.app.ico");
        if (stream == null) return SystemIcons.Application;
        return size > 0 ? new Icon(stream, size, size) : new Icon(stream);
    }

    // ---- layout ----
    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Ui.P(14), BackColor = Ui.Window };
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, Ui.S(78)));
        Controls.Add(root);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Ui.P(0, 0, 0, 12), BackColor = Ui.Window };
        body.ColumnStyles.Add(new(SizeType.Absolute, Ui.S(268))); body.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 0);
        body.Controls.Add(SetsCard(), 0, 0);
        body.Controls.Add(QueueCard(), 1, 0);
        root.Controls.Add(PlayerBar(), 0, 1);
    }
    static TableLayoutPanel Grid(int columns, int rows, Padding padding)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Padding = padding, Margin = Padding.Empty, BackColor = Ui.Surface };
        // A single row fills the height, so controls anchored only left/right sit in the middle.
        if (rows == 1) grid.RowStyles.Add(new(SizeType.Percent, 100));
        return grid;
    }
    // Card padding keeps the content inside the rounded corners.
    static Card CardFor(Control content, Padding margin)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = margin, Padding = Ui.P(5) };
        card.Controls.Add(content); return card;
    }
    Control SetsCard()
    {
        var layout = Grid(1, 3, Ui.P(10, 6, 6, 8));
        layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(44))); layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(42))); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var header = Grid(2, 1, Padding.Empty);
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Наборы", Font = Ui.Heading, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Surface }, 0, 0);
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Ui.Surface, Anchor = AnchorStyles.Right };
        tools.Controls.Add(Ui.IconButton("", "Создать набор", tips, CreateSet));
        tools.Controls.Add(Ui.IconButton("", "Открыть папку наборов", tips, () => OpenFolder(setsRoot)));
        tools.Controls.Add(Ui.IconButton("", "Обновить наборы", tips, () => Run(Rescan)));
        header.Controls.Add(tools, 1, 0);
        layout.Controls.Add(header, 0, 0);
        queueView.Items.AddRange(["Общая очередь", "Выбранный набор"]); queueView.SelectedIndex = settings.SingleSet ? 1 : 0;
        queueView.Anchor = AnchorStyles.Left | AnchorStyles.Right; queueView.Margin = Ui.P(4, 0, 6, 0);
        tips.SetToolTip(queueView, "Общая очередь: можно отметить несколько наборов, их обои идут одной очередью и их можно перемешать между собой.\nВыбранный набор: отмечается только один набор.");
        layout.Controls.Add(queueView, 0, 1);
        sets.Margin = Ui.P(2, 4, 0, 0);
        layout.Controls.Add(sets, 0, 2);
        return CardFor(layout, Ui.P(0, 0, 12, 0));
    }
    Control QueueCard()
    {
        var layout = Grid(1, 3, Ui.P(12, 6, 10, 8));
        layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(48))); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, Ui.S(52)));
        var toolbar = Grid(9, 1, Padding.Empty);
        toolbar.ColumnStyles.Add(new(SizeType.AutoSize)); toolbar.ColumnStyles.Add(new(SizeType.AutoSize)); toolbar.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) toolbar.ColumnStyles.Add(new(SizeType.AutoSize));
        Control Middle(Control c, int right = 6) { c.Anchor = AnchorStyles.Left; c.Margin = new Padding(0, 0, Ui.S(right), 0); return c; }
        toolbar.Controls.Add(Middle(new Label { Text = "Очередь", Font = Ui.Heading, AutoSize = true, BackColor = Ui.Surface }, 10), 0, 0);
        toolbar.Controls.Add(Middle(count), 1, 0);
        toolbar.Controls.Add(Middle(Ui.Caption("Порядок", Ui.Body, Ui.Muted)), 3, 0);
        sorting.Items.AddRange(["По умолчанию", "Вручную", "Перемешать"]);
        toolbar.Controls.Add(Middle(sorting, 2), 4, 0);
        toolbar.Controls.Add(Middle(Ui.IconButton("", "Перемешать заново", tips, () => ChangeSort(true)), 14), 5, 0);
        toolbar.Controls.Add(Middle(Ui.Caption("Превью", Ui.Body, Ui.Muted)), 6, 0);
        thumbnailSize.Items.AddRange(["Мелкие", "Средние", "Крупные", "Очень крупные"]); thumbnailSize.SelectedIndex = settings.ThumbnailSize;
        files.SizeIndex = settings.ThumbnailSize; files.ShowNames = settings.ShowNames; files.ShowSetNames = settings.ShowSetNames;
        tips.SetToolTip(thumbnailSize, "Размер превью; также Ctrl + колесо мыши");
        toolbar.Controls.Add(Middle(thumbnailSize, 14), 7, 0);
        toolbar.Controls.Add(Middle(Ui.IconButton("", "Настройки", tips, OpenSettings, 14), 0), 8, 0);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(files, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = Ui.P(0, 10, 0, 0), Margin = Padding.Empty, BackColor = Ui.Surface };
        var earlier = Ui.TextButton("← Раньше", () => MoveSelected(-1)); tips.SetToolTip(earlier, "Сдвинуть выбранные обои раньше в очереди");
        var later = Ui.TextButton("Позже →", () => MoveSelected(1)); tips.SetToolTip(later, "Сдвинуть выбранные обои позже в очереди");
        var show = Ui.TextButton("Показать на рабочем столе", ShowSelected, accent: true); tips.SetToolTip(show, "Поставить выбранные обои сейчас (или двойной щелчок по превью)");
        footer.Controls.AddRange([earlier, later, show, Ui.IconButton("", "Открыть папку выбранных обоев", tips, () => { if (files.SelectedPath is { } f) OpenFolder(Path.GetDirectoryName(f)!); else OpenFolder(ViewSet?.Directory ?? setsRoot); })]);
        layout.Controls.Add(footer, 0, 2);
        return CardFor(layout, Padding.Empty);
    }
    Control PlayerBar()
    {
        var layout = Grid(9, 1, Ui.P(10, 0, 12, 0));
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize, SizeType.Absolute, SizeType.Absolute, SizeType.AutoSize })
            layout.ColumnStyles.Add(style == SizeType.Percent ? new(SizeType.Percent, 100) : new(style));
        layout.ColumnStyles[6].Width = Ui.S(120); layout.ColumnStyles[7].Width = Ui.S(48);
        previous.Size = next.Size = Ui.S(42, 42); pause.Size = Ui.S(50, 42);
        pause.BackColor = Ui.Accent; pause.ForeColor = Color.White; pause.FlatAppearance.MouseOverBackColor = Ui.AccentDark; pause.FlatAppearance.MouseDownBackColor = Ui.AccentDark;
        foreach (var b in new[] { previous, pause, next }) { b.Anchor = AnchorStyles.Left; b.Margin = Ui.P(0, 0, 6, 0); }
        layout.Controls.Add(previous, 0, 0); layout.Controls.Add(pause, 1, 0); layout.Controls.Add(next, 2, 0);
        layout.Controls.Add(status, 3, 0);
        var change = Grid(3, 1, Padding.Empty); change.Dock = DockStyle.None; change.AutoSize = true; change.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (int i = 0; i < 3; i++) change.ColumnStyles.Add(new(SizeType.AutoSize));
        change.RowStyles.Clear(); change.RowStyles.Add(new(SizeType.AutoSize)); change.Anchor = AnchorStyles.Left; change.Margin = Ui.P(0, 0, 18, 0);
        var every = Ui.Caption("Менять каждые", Ui.Body, Ui.Muted); every.Anchor = AnchorStyles.Left;
        var seconds = Ui.Caption("сек", Ui.Body, Ui.Muted); seconds.Anchor = AnchorStyles.Left;
        interval.Value = (decimal)settings.IntervalSeconds; interval.Anchor = AnchorStyles.Left; interval.Margin = Ui.P(6, 0, 6, 0);
        tips.SetToolTip(interval, "Интервал смены обоев: от 5 секунд до 24 часов");
        change.Controls.Add(every, 0, 0); change.Controls.Add(interval, 1, 0); change.Controls.Add(seconds, 2, 0);
        layout.Controls.Add(change, 4, 0);
        sound.Anchor = AnchorStyles.Left; sound.Checked = settings.Music; volume.Value = settings.MusicVolume;
        tips.SetToolTip(volume, "Громкость музыки обоев. Меняет только звук обоев: громкость Windows и других программ не трогает.");
        layout.Controls.Add(sound, 5, 0); layout.Controls.Add(volume, 6, 0); layout.Controls.Add(volumeText, 7, 0);
        var stop = Ui.TextButton("Отключить и выйти", Exit, danger: true); stop.Anchor = AnchorStyles.Right; stop.Margin = Ui.P(12, 0, 0, 0);
        tips.SetToolTip(stop, "Остановить обои, закрыть Wallsets и вернуть обычный фон Windows");
        layout.Controls.Add(stop, 8, 0);
        return CardFor(layout, Padding.Empty);
    }
    void BuildTray()
    {
        var menu = new ContextMenuStrip { Font = Ui.Body };
        menu.Items.Add("Открыть Wallsets", null, (_, _) => ShowMain());
        menu.Items.Add("Настройки", null, (_, _) => { ShowMain(); OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(trayPause); trayPause.Click += (_, _) => TogglePause();
        menu.Items.Add("Предыдущие обои", null, (_, _) => Run(Previous));
        menu.Items.Add("Следующие обои", null, (_, _) => Run(Next));
        menu.Items.Add(traySound); traySound.Click += (_, _) => sound.Checked = !sound.Checked;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Отключить и вернуть Windows", null, (_, _) => Exit());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => ShowMain();
    }
    void OpenSettings()
    {
        if (OwnedForms.OfType<SettingsForm>().FirstOrDefault() is { } open) { open.Activate(); return; }
        using var dialog = new SettingsForm(this);
        dialog.ShowDialog(this);
    }

    // ---- state used by the settings window ----
    internal void SaveSettings() => Save();
    internal void SetPreview(bool names, bool setNames) { settings.ShowNames = files.ShowNames = names; settings.ShowSetNames = files.ShowSetNames = setNames; Save(); }
    internal void SetScaling(string mode)
    {
        if (settings.Scaling == mode) return;
        settings.Scaling = player.Scaling = mode; Save();
        Run(async () => { if (player.Running) await player.ApplyScaling(currentFile); });
    }
    internal void SetAutoPause(bool on) { settings.AutoPause = on; Save(); CheckAutoPause(); }
    internal void SetBatteryPause(bool on) { settings.PauseOnBattery = on; Save(); CheckAutoPause(); }
    internal void SetFloating(bool on) { settings.Floating = on; UpdateFloating(); Save(); }
    internal void SetVolume(int value) => volume.Value = Math.Clamp(value, 0, 100);
    internal void SetWindowsFrame(bool on)
    {
        settings.WindowsFrame = on;
        if (on) nextFrame = Environment.TickCount64 + 5000;
        else
        {
            try { Desktop.RestoreWallpaper(settings); } catch (Exception ex) { Program.Log(ex); }
            // Changing the Windows background can rebuild the desktop layer: reattach if it did.
            RecoverDesktop();
        }
        Save();
    }
    internal bool HotkeyWorks(string id) => hotkeyState.TryGetValue(id, out var ok) && ok;
    internal void SuspendHotkeys() { for (int i = 1; i <= HotkeyActions.All.Length; i++) Native.UnregisterHotKey(Handle, i); }
    // Registers the configured combinations; a failure means another program owns that combination.
    internal void RegisterHotkeys()
    {
        SuspendHotkeys(); hotkeyState.Clear();
        var failed = new List<string>();
        for (int i = 0; i < HotkeyActions.All.Length; i++)
        {
            var key = HotkeyActions.Get(settings, HotkeyActions.All[i].Id);
            if (key.IsEmpty) continue;
            bool ok = Native.RegisterHotKey(Handle, i + 1, key.Modifiers | 0x4000, (uint)key.Code);
            hotkeyState[HotkeyActions.All[i].Id] = ok;
            if (!ok) failed.Add(key.Display);
        }
        hotkeyProblem = failed.Count == 0 ? null : "Заняты другой программой: " + string.Join(", ", failed) + ". Выберите другие клавиши в настройках.";
        tips.SetToolTip(previous, "Предыдущие обои" + HotkeyHint("previous"));
        tips.SetToolTip(pause, "Пауза / продолжить" + HotkeyHint("pause"));
        tips.SetToolTip(next, "Следующие обои" + HotkeyHint("next"));
        UpdateSound();
        if (!exiting) UpdateStatus();
    }
    string HotkeyHint(string id) { var key = HotkeyActions.Get(settings, id); return HotkeyWorks(id) ? $" ({key.Display})" : ""; }

    // ---- sets and queue ----
    List<MediaSet> ActiveSets => library.Where(s => settings.Selected.Contains(s.Name)).ToList();
    SetOptions CombinedOptions()
    {
        var key = Library.CombinationKey(ActiveSets.Select(s => s.Name));
        if (!settings.Combinations.TryGetValue(key, out var options)) settings.Combinations[key] = options = new();
        return options;
    }
    MediaSet? ViewSet => ActiveSets.Count == 1 ? ActiveSets[0] : null;
    SetOptions ViewOptions => ViewSet is { } s ? Options(s) : CombinedOptions();
    string SetName(string path) => Path.GetRelativePath(setsRoot, path).Split(Path.DirectorySeparatorChar)[0];
    SetOptions Options(MediaSet set)
    {
        if (!settings.Sets.TryGetValue(set.Name, out var o)) settings.Sets[set.Name] = o = new();
        o.Order ??= [];
        return o;
    }
    void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    void Save() { try { settings.Save(settingsPath); } catch (Exception ex) { problem = "Не удалось сохранить настройки: " + ex.Message; Program.Log(ex); } }
    async void Run(Func<Task> action)
    {
        if (exiting) return;
        await actions.WaitAsync();
        if (exiting) { actions.Release(); return; }
        busy = true;
        try { await action(); }
        catch (Exception ex) { Program.Log(ex); problem = ex.Message; }
        finally { busy = false; actions.Release(); if (!exiting) UpdateStatus(); }
    }
    int SetWidth => Math.Max(60, sets.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
    void RefreshSets()
    {
        updating = true;
        var boxes = sets.Controls.OfType<CheckBox>().ToList();
        if (!boxes.Select(b => ((MediaSet)b.Tag!).Name).SequenceEqual(library.Select(s => s.Name)))
        {
            sets.SuspendLayout(); sets.Controls.Clear();
            foreach (var old in boxes) { tips.SetToolTip(old, null); old.Dispose(); }
            boxes = library.Select(_ =>
            {
                var box = new CheckBox { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Width = SetWidth, Height = Ui.S(32), Margin = Ui.P(0, 0, 0, 2), TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Surface, ForeColor = Ui.Text, Cursor = Cursors.Hand };
                box.CheckedChanged += SetChecked; return box;
            }).ToList();
            sets.Controls.AddRange([.. boxes]); sets.ResumeLayout();
        }
        for (int i = 0; i < library.Count; i++)
        {
            boxes[i].Tag = library[i]; boxes[i].Text = library[i].ToString(); tips.SetToolTip(boxes[i], library[i].Directory);
            boxes[i].Checked = settings.Selected.Contains(library[i].Name);
        }
        updating = false; RefreshFiles();
    }
    void SetChecked(object? sender, EventArgs e)
    {
        if (updating || sender is not CheckBox { Tag: MediaSet set } box) return;
        settings.Selected.Remove(set.Name);
        if (box.Checked)
        {
            // "Выбранный набор" works like radio buttons: a new tick replaces the previous one.
            if (settings.SingleSet)
            {
                settings.Selected.Clear(); updating = true;
                foreach (var other in sets.Controls.OfType<CheckBox>()) if (other != box) other.Checked = false;
                updating = false;
            }
            settings.Selected.Add(set.Name);
        }
        Save(); Run(Rebuild);
    }
    async Task ChangeQueueMode()
    {
        settings.SingleSet = queueView.SelectedIndex == 1;
        // Keep the most recently ticked set that still exists.
        if (settings.SingleSet && settings.Selected.Count > 1)
            settings.Selected = settings.Selected.Where(n => library.Any(s => s.Name == n)).TakeLast(1).ToList();
        RefreshSets(); await Rebuild(); Save();
    }
    void RefreshFiles()
    {
        updating = true;
        var options = ViewOptions;
        sorting.SelectedIndex = options.Sort == "manual" ? 1 : options.Sort == "shuffle" ? 2 : 0;
        files.SetItems(queue.Select(p => new ThumbnailItem(p, SetName(p))));
        files.Playing = currentFile;
        count.Text = queue.Count == 0 ? "" : Plural(queue.Count, "файл", "файла", "файлов") + " · слева направо, сверху вниз";
        updating = false;
    }
    static string Plural(int n, string one, string few, string many) =>
        n + " " + (n % 10 == 1 && n % 100 != 11 ? one : n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many);
    async Task Rescan()
    {
        bool first = library.Count == 0 && !File.Exists(settingsPath);
        library = Library.Scan(setsRoot);
        if (first) settings.Selected = library.Take(2).Select(s => s.Name).ToList();
        // Watch the folders and save before starting playback, which may fail (desktop not ready yet).
        RefreshSets(); SetupWatchers(); Save();
        await Rebuild();
    }
    void SetupWatchers()
    {
        foreach (var w in watchers) w.Dispose(); watchers.Clear();
        foreach (var dir in new[] { setsRoot }.Concat(library.Select(s => s.Directory)))
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = dir != setsRoot, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                FileSystemEventHandler changed = (_, _) => ScheduleScan();
                w.Created += changed; w.Deleted += changed; w.Changed += changed; w.Renamed += (_, _) => ScheduleScan(); w.Error += (_, _) => ScheduleScan();
                w.EnableRaisingEvents = true; watchers.Add(w);
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }
    void ScheduleScan()
    {
        if (exiting || IsDisposed) return;
        try { BeginInvoke(() => { scanTimer.Stop(); scanTimer.Start(); }); } catch (InvalidOperationException) { }
    }
    async Task Rebuild()
    {
        queue = library.Where(s => settings.Selected.Contains(s.Name)).SelectMany(s => Library.Ordered(s, Options(s)).Select(f => Path.Combine(s.Directory, f))).ToList();
        if (ActiveSets.Count > 1) queue = Library.Reconcile(queue.Select(p => Path.GetRelativePath(setsRoot, p)).ToList(), CombinedOptions()).Select(p => Path.Combine(setsRoot, p)).ToList();
        RefreshFiles();
        if (queue.Count == 0) { currentFile = null; player.Stop(); return; }
        if (currentFile == null || !queue.Contains(currentFile)) await Play(queue[0]);
    }
    void ChangeSort(bool reshuffle)
    {
        var order = files.Items.ToList();
        string mode = reshuffle ? "shuffle" : sorting.SelectedIndex == 1 ? "manual" : sorting.SelectedIndex == 2 ? "shuffle" : "default";
        if (mode == "shuffle") Random.Shared.Shuffle(CollectionsMarshal.AsSpan(order));
        StoreViewOrder(order, mode);
    }
    void StoreViewOrder(List<ThumbnailItem> order, string mode)
    {
        var options = ViewOptions;
        options.Sort = mode;
        string root = ViewSet?.Directory ?? setsRoot;
        options.Order = order.Select(item => Path.GetRelativePath(root, item.Path)).ToList();
        Save(); Run(Rebuild);
    }
    void MoveSelected(int delta) => MoveItem(files.SelectedIndex, files.SelectedIndex + delta);
    void MoveItem(int from, int to)
    {
        if (from < 0 || to < 0 || from >= files.Items.Count || to >= files.Items.Count || from == to) return;
        var order = files.Items.ToList(); var item = order[from]; order.RemoveAt(from); order.Insert(to, item);
        StoreViewOrder(order, "manual");
        files.SelectedIndex = to;
    }
    void ShowSelected()
    {
        if (files.SelectedPath is not string path) return;
        var s = library.FirstOrDefault(s => s.Name == SetName(path));
        if (s == null) return;
        Run(async () =>
        {
            if (!settings.Selected.Contains(s.Name)) { if (settings.SingleSet) settings.Selected.Clear(); settings.Selected.Add(s.Name); RefreshSets(); await Rebuild(); Save(); }
            await Play(path);
        });
    }
    void CreateSet()
    {
        using var dialog = new Form { Text = "Новый набор", Font = Ui.Body, BackColor = Ui.Surface, AutoScaleMode = AutoScaleMode.None, ClientSize = Ui.S(420, 132), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var input = new TextBox { Left = Ui.S(20), Top = Ui.S(22), Width = Ui.S(380), PlaceholderText = "Название набора" };
        var ok = Ui.TextButton("Создать", () => { }, accent: true); ok.DialogResult = DialogResult.OK;
        ok.AutoSize = false; ok.Bounds = new Rectangle(Ui.S(280), Ui.S(76), Ui.S(120), Ui.S(36));
        dialog.Controls.AddRange([input, ok]); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = input.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) { MessageBox.Show(this, "Введите допустимое название папки.", "Новый набор"); return; }
        Run(async () => { var path = Path.Combine(setsRoot, name); Directory.CreateDirectory(path); await Rescan(); OpenFolder(path); });
    }

    // ---- playback ----
    void ResetDeadline() => nextChange = Environment.TickCount64 + (long)(settings.IntervalSeconds * 1000);
    bool ShouldAutoPause() => locked || displayOff || settings.AutoPause && Native.DesktopCovered() || settings.PauseOnBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
    // Immediate re-check after window, power or settings changes; the one-second timer is the fallback.
    void CheckAutoPause()
    {
        if (exiting || busy || !player.Running) return;
        Run(async () =>
        {
            bool automatic = ShouldAutoPause();
            if (automatic != autoPaused && player.Running) { autoPaused = automatic; await player.Pause(userPaused || autoPaused); }
        });
    }
    async Task Play(string path)
    {
        if (!File.Exists(path)) { ScheduleScan(); return; }
        autoPaused = ShouldAutoPause();
        try { await player.Load(path, userPaused || autoPaused); }
        catch { playbackFault = true; player.Stop(); throw; }
        currentFile = path; ResetDeadline(); problem = null; playbackFault = false;
        nextHealth = Environment.TickCount64 + 5000;
        if (lastFrame == null) nextFrame = Environment.TickCount64 + 10000;
        var title = Path.GetFileNameWithoutExtension(path);
        tray.Text = ("Wallsets · " + title).Length <= 120 ? "Wallsets · " + title : ("Wallsets · " + title)[..119] + "…";
        files.Playing = path;
    }
    async Task Next()
    {
        if (queue.Count == 0) return;
        var index = currentFile == null ? -1 : queue.IndexOf(currentFile);
        await Play(queue[Library.AdjacentIndex(index, queue.Count, 1)]);
    }
    async Task Previous()
    {
        if (queue.Count == 0) return;
        await Play(queue[Library.AdjacentIndex(currentFile == null ? -1 : queue.IndexOf(currentFile), queue.Count, -1)]);
    }
    void TogglePause() => Run(async () => { userPaused = !userPaused; if (player.Running) await player.Pause(userPaused || autoPaused); });
    internal void SetSound(bool on)
    {
        if (settings.Music == on) return;
        settings.Music = player.Sound = on; UpdateSound(); Save();
        Run(async () => { if (player.Running) await player.SetSound(settings.Music); });
    }
    void UpdateSound()
    {
        sound.Text = settings.Music ? "" : "";
        var key = HotkeyActions.Get(settings, "sound");
        tips.SetToolTip(sound, (settings.Music ? "Проигрывать с музыкой: включено" : "Проигрывать с музыкой: выключено") + (HotkeyWorks("sound") ? $" ({key.Display})" : ""));
        if (sound.Checked != settings.Music) sound.Checked = settings.Music;
        if (volume.Value != settings.MusicVolume) volume.Value = settings.MusicVolume;
        traySound.Checked = settings.Music;
        volumeText.Text = settings.MusicVolume + "%";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    // The slider sends many values while it is dragged: only the latest one goes to the player.
    async void ApplyVolume()
    {
        if (volumeSending) { volumePending = true; return; }
        volumeSending = true;
        try
        {
            do { volumePending = false; if (player.Running) await player.SetVolume(settings.MusicVolume); }
            while (volumePending && !exiting);
        }
        catch (Exception ex) { Program.Log(ex); }
        finally { volumeSending = false; }
    }
    // Explorer restarted (crash or manual restart) and took the desktop layer with the video: attach again.
    async void RecoverDesktop()
    {
        await Task.Delay(2000);
        if (exiting || queue.Count == 0 || player.Running && player.Attached) return;
        attachRetries = Math.Max(attachRetries, 3);
        Run(async () =>
        {
            if (player.Running && player.Attached) return;
            player.Stop(); playbackFault = false;
            await Play(currentFile != null && queue.Contains(currentFile) ? currentFile : queue[0]);
        });
    }
    async Task Tick()
    {
        if (queue.Count == 0) return;
        if (playbackFault)
        {
            // Right after Windows starts the desktop may not be ready yet: try again a few times.
            if (attachRetries > 0 && Environment.TickCount64 >= nextHealth)
            {
                attachRetries--; nextHealth = Environment.TickCount64 + 3000;
                player.Stop(); playbackFault = false;
                await Play(currentFile != null && queue.Contains(currentFile) ? currentFile : queue[0]);
            }
            return;
        }
        if (!player.Running || !player.Attached)
        {
            playbackFault = true;
            player.Stop();
            problem = "Подключение к рабочему столу потеряно. Нажмите «Следующие обои», чтобы повторить.";
            Program.Log(new IOException(problem));
            return;
        }
        bool automatic = ShouldAutoPause();
        if (automatic != autoPaused) { autoPaused = automatic; await player.Pause(userPaused || autoPaused); }
        if (Environment.TickCount64 >= nextChange) { await Next(); return; }
        if (settings.WindowsFrame && nextFrame != 0 && Environment.TickCount64 >= nextFrame)
        {
            // A spare frame for the Windows background, refreshed now and then; the final one is taken at shutdown.
            nextFrame = Environment.TickCount64 + 30 * 60 * 1000;
            await CaptureFrame();
        }
        if (Environment.TickCount64 >= nextHealth)
        {
            nextHealth = Environment.TickCount64 + 5000;
            var idle = await player.Command("get_property", "idle-active");
            var ended = await player.OptionalProperty("eof-reached");
            if (idle.ValueKind == JsonValueKind.True || ended.ValueKind == JsonValueKind.True)
            {
                problem = "Файл не воспроизводится; пропуск: " + Path.GetFileName(currentFile);
                Program.Log(new IOException(problem));
                await Next();
            }
        }
    }
    async Task CaptureFrame()
    {
        if (!player.Running) return;
        try
        {
            var file = Path.Combine(Desktop.FramesFolder, $"frame-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            await player.SaveFrame(file);
            if (!File.Exists(file)) return;
            lastFrame = file;
            Desktop.CleanFrames(file);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException) { Program.Log(ex); }
    }
    // Windows is ending the session: show the current frame as the Windows background for the next start.
    void SessionEnding()
    {
        if (!settings.WindowsFrame) return;
        try
        {
            if (player.Running && !cleaned) Task.Run(CaptureFrame).Wait(2500);
            if (lastFrame != null && File.Exists(lastFrame)) { Desktop.ApplyFrame(settings, lastFrame); Save(); }
        }
        catch (Exception ex) { Program.Log(ex); }
    }
    void UpdateStatus()
    {
        pause.Text = userPaused ? "" : "";
        pause.AccessibleName = userPaused ? "Продолжить" : "Пауза";
        trayPause.Text = userPaused ? "Продолжить" : "Пауза";
        if (floatingPause != null) floatingPause.Text = pause.Text;
        var remaining = TimeSpan.FromSeconds(Math.Max(0, (int)Math.Ceiling((nextChange - Environment.TickCount64) / 1000d)));
        string countdown = remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}:{remaining:mm\\:ss}" : $"{remaining:mm\\:ss}";
        status.ForeColor = problem != null ? Ui.Danger : queue.Count == 0 ? Ui.Muted : Ui.Text;
        status.Text = problem ?? (queue.Count == 0 ? "Отметьте набор слева" : "Смена через " + countdown);
        string state = userPaused ? " · на паузе" : autoPaused ? " · автопауза (экономия энергии)" : "";
        var tip = problem ?? (currentFile == null ? "" : "Сейчас: " + Path.GetFileNameWithoutExtension(currentFile) + state + (settings.Music ? $" · звук {settings.MusicVolume}%" : ""));
        if (tip != statusTip) { statusTip = tip; tips.SetToolTip(status, tip); }
    }
    void UpdateFloating()
    {
        if (!settings.Floating) { floating?.Close(); floating = null; floatingPause = null; return; }
        if (floating != null) return;
        floating = new Form { Text = "Пульт Wallsets", FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false, TopMost = true, AutoScaleMode = AutoScaleMode.None, ClientSize = Ui.S(140, 46), StartPosition = FormStartPosition.Manual, BackColor = Ui.Surface, MaximizeBox = false, MinimizeBox = false };
        var b = Screen.PrimaryScreen!.WorkingArea; floating.Location = new Point(b.Right - floating.Width - Ui.S(16), b.Bottom - floating.Height - Ui.S(16));
        var p = Ui.IconButton("", "Предыдущие обои", tips, () => Run(Previous)); p.Location = new Point(Ui.S(4), Ui.S(4));
        floatingPause = Ui.IconButton(pause.Text, "Пауза / продолжить", tips, TogglePause); floatingPause.Location = new Point(Ui.S(51), Ui.S(4));
        var n = Ui.IconButton("", "Следующие обои", tips, () => Run(Next)); n.Location = new Point(Ui.S(98), Ui.S(4));
        floating.Controls.AddRange([p, floatingPause, n]); floating.FormClosed += (_, _) => { floating = null; floatingPause = null; }; floating.Show();
    }
    // Started with --tray (for example by Windows at sign-in): stay hidden until opened from the tray.
    protected override void SetVisibleCore(bool value)
    {
        if (value && startInTray && !allowShow) { value = false; if (!IsHandleCreated) CreateHandle(); }
        base.SetVisibleCore(value);
    }
    void ShowMain() { allowShow = true; Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    void SessionChanged(object? sender, SessionSwitchEventArgs e)
    {
        if (exiting) return;
        BeginInvoke(() =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
            CheckAutoPause();
        });
    }
    void PowerChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (exiting || e.Mode != PowerModes.StatusChange) return;
        try { BeginInvoke(CheckAutoPause); } catch (InvalidOperationException) { }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312)
        {
            switch (HotkeyActions.All.ElementAtOrDefault(m.WParam.ToInt32() - 1).Id)
            {
                case "pause": TogglePause(); break;
                case "next": Run(Next); break;
                case "previous": Run(Previous); break;
                case "sound": sound.Checked = !sound.Checked; break;
            }
        }
        if (taskbarCreated != 0 && m.Msg == (int)taskbarCreated) RecoverDesktop();
        if (m.Msg == 0x218 && m.WParam.ToInt32() == 0x8013 && m.LParam != IntPtr.Zero)
        {
            displayOff = Marshal.ReadInt32(m.LParam, 20) == 0;
            CheckAutoPause();
        }
        if (m.Msg == 0x16 && m.WParam != IntPtr.Zero) SessionEnding();
        base.WndProc(ref m);
    }
    object Snapshot() => new { running = !exiting, currentFile, count = queue.Count, queue, selected = settings.Selected, paused = userPaused, autoPaused, intervalSeconds = settings.IntervalSeconds, remainingSeconds = Math.Max(0, (nextChange - Environment.TickCount64) / 1000d), playerPid = player.Pid, playerPipe = player.PipeName, attached = player.Attached, starts = player.Starts, renderer = player.WindowInfo, playbackFault, columns = files.Columns, thumbnailSize = settings.ThumbnailSize, singleSet = settings.SingleSet, music = settings.Music, musicVolume = settings.MusicVolume, scaling = settings.Scaling, showNames = settings.ShowNames, showSetNames = settings.ShowSetNames, hotkeys = hotkeyState, visible = Visible, problem = problem ?? hotkeyProblem };
    async Task Server()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(5000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var cmd = await reader.ReadLineAsync(timeout.Token);
                var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginInvoke(() =>
                {
                    try
                    {
                        if (cmd == "--next") Run(Next);
                        else if (cmd == "--previous") Run(Previous);
                        else if (cmd == "--pause") TogglePause();
                        else if (cmd == "--show") ShowMain();
                        else if (cmd == "--rescan") Run(Rescan);
                        response.TrySetResult(JsonSerializer.Serialize(Snapshot()));
                    }
                    catch (Exception ex) { response.TrySetException(ex); }
                });
                await writer.WriteLineAsync(await response.Task.WaitAsync(timeout.Token));
                if (cmd == "--stop") BeginInvoke(Exit);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!exiting) Program.Log(ex); }
        }
    }
    // "Отключить и выйти": stop the player and give Windows its own background back.
    void Exit()
    {
        if (exiting) return;
        exiting = true; Cleanup();
        try { Desktop.RestoreWallpaper(settings); Save(); } catch (Exception ex) { Program.Log(ex); }
        Close();
    }
    void Cleanup()
    {
        if (cleaned) return;
        cleaned = exiting = true; timer.Stop(); scanTimer.Stop(); saveTimer.Stop(); pauseCheck.Stop(); cancellation.Cancel(); Save();
        foreach (var w in watchers) w.Dispose(); watchers.Clear();
        SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        if (windowHook != IntPtr.Zero) { Native.UnhookWinEvent(windowHook); windowHook = IntPtr.Zero; }
        if (IsHandleCreated) SuspendHotkeys();
        if (powerNotification != IntPtr.Zero) { Native.UnregisterPowerSettingNotification(powerNotification); powerNotification = IntPtr.Zero; }
        floating?.Close(); player.Dispose(); tray.Visible = false; tray.Dispose();
    }
}
