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
    readonly CheckBox byLength = new() { Text = "по длине видео", AutoSize = true, BackColor = Ui.Surface, ForeColor = Ui.Text, Font = Ui.Body, UseMnemonic = false, Cursor = Cursors.Hand };
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
    readonly Button pause, next, previous, deleteQueue;
    readonly ToolStripMenuItem trayPause = new("Пауза");
    readonly ToolStripMenuItem traySound = new("Проигрывать с музыкой");
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly Dictionary<string, bool> hotkeyState = [];
    readonly Native.WinEventProc windowEvents;
    IntPtr windowHook;
    List<MediaSet> library = [];
    // A file can be in the queue several times; position is the index of the copy on screen.
    List<MediaFile> queue = [];
    MediaFile? current;
    int position = -1, currentCopy;
    // Seconds left of the current video when wallpapers change at the end of each video.
    double? videoLeft;
    string? lastFrame;
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
        player.Volume = settings.MusicVolume; player.Scaling = settings.Scaling; player.Loop = !settings.ByVideoLength;
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
        deleteQueue = Ui.IconButton("\uE74D", "Удалить сохранённую очередь", tips, DeleteQueue);
        BuildLayout();
        BuildTray();

        sets.Resize += (_, _) => { foreach (Control box in sets.Controls) box.Width = SetWidth; };
        sorting.SelectedIndexChanged += (_, _) => { if (!updating) ChangeSort(false); };
        queueView.SelectedIndexChanged += (_, _) => Run(ChangeQueueMode);
        thumbnailSize.SelectedIndexChanged += (_, _) => { files.SizeIndex = settings.ThumbnailSize = thumbnailSize.SelectedIndex; Save(); };
        files.SizeIndexChanged += (_, _) => thumbnailSize.SelectedIndex = files.SizeIndex;
        files.ItemActivated += (_, _) => ShowSelected();
        files.OrderChanged += (_, _) => StoreViewOrder(KeysOf(files.Items), EditMode);
        files.ItemMenu += (_, point) => ShowTileMenu(point);
        Move += (_, _) => files.UpdateMetrics();
        sound.CheckedChanged += (_, _) => SetSound(sound.Checked);
        byLength.CheckedChanged += (_, _) => SetByVideoLength(byLength.Checked);
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
        var toolbar = Grid(10, 1, Padding.Empty);
        toolbar.ColumnStyles.Add(new(SizeType.AutoSize)); toolbar.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) toolbar.ColumnStyles.Add(new(SizeType.AutoSize));
        Control Middle(Control c, int right = 6) { c.Anchor = AnchorStyles.Left; c.Margin = new Padding(0, 0, Ui.S(right), 0); return c; }
        toolbar.Controls.Add(Middle(new Label { Text = "Очередь", Font = Ui.Heading, AutoSize = true, BackColor = Ui.Surface }, 10), 0, 0);
        toolbar.Controls.Add(Middle(Ui.Caption("Порядок", Ui.Body, Ui.Muted)), 2, 0);
        sorting.Items.AddRange(["По умолчанию", "Вручную", "Перемешать"]);
        tips.SetToolTip(sorting, "Порядок очереди. Сохранённые очереди показываются здесь, когда отмечены те же наборы, для которых они сохранены.");
        toolbar.Controls.Add(Middle(sorting, 2), 3, 0);
        toolbar.Controls.Add(Middle(Ui.IconButton("", "Перемешать заново", tips, () => ChangeSort(true)), 2), 4, 0);
        deleteQueue.Visible = false;
        toolbar.Controls.Add(Middle(deleteQueue, 2), 5, 0);
        var saveQueue = Ui.TextButton("Сохранить очередь", SaveQueue);
        tips.SetToolTip(saveQueue, "Сохранить текущую очередь под своим названием. Она появится в списке «Порядок», когда отмечены эти же наборы.");
        toolbar.Controls.Add(Middle(saveQueue, 14), 6, 0);
        toolbar.Controls.Add(Middle(Ui.Caption("Превью", Ui.Body, Ui.Muted)), 7, 0);
        thumbnailSize.Items.AddRange(["Мелкие", "Средние", "Крупные", "Очень крупные"]); thumbnailSize.SelectedIndex = settings.ThumbnailSize;
        files.SizeIndex = settings.ThumbnailSize; files.ShowNames = settings.ShowNames; files.ShowSetNames = settings.ShowSetNames;
        tips.SetToolTip(thumbnailSize, "Размер превью; также Ctrl + колесо мыши");
        toolbar.Controls.Add(Middle(thumbnailSize, 14), 8, 0);
        toolbar.Controls.Add(Middle(Ui.IconButton("", "Настройки", tips, OpenSettings, 14), 0), 9, 0);
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
        var layout = Grid(8, 1, Ui.P(10, 0, 12, 0));
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize, SizeType.Absolute, SizeType.Absolute })
            layout.ColumnStyles.Add(style == SizeType.Percent ? new(SizeType.Percent, 100) : new(style));
        layout.ColumnStyles[6].Width = Ui.S(120); layout.ColumnStyles[7].Width = Ui.S(48);
        previous.Size = next.Size = Ui.S(42, 42); pause.Size = Ui.S(50, 42);
        pause.BackColor = Ui.Accent; pause.ForeColor = Color.White; pause.FlatAppearance.MouseOverBackColor = Ui.AccentDark; pause.FlatAppearance.MouseDownBackColor = Ui.AccentDark;
        foreach (var b in new[] { previous, pause, next }) { b.Anchor = AnchorStyles.Left; b.Margin = Ui.P(0, 0, 6, 0); }
        layout.Controls.Add(previous, 0, 0); layout.Controls.Add(pause, 1, 0); layout.Controls.Add(next, 2, 0);
        layout.Controls.Add(status, 3, 0);
        var change = Grid(4, 1, Padding.Empty); change.Dock = DockStyle.None; change.AutoSize = true; change.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (int i = 0; i < 4; i++) change.ColumnStyles.Add(new(SizeType.AutoSize));
        change.RowStyles.Clear(); change.RowStyles.Add(new(SizeType.AutoSize)); change.Anchor = AnchorStyles.Left; change.Margin = Ui.P(0, 0, 18, 0);
        var every = Ui.Caption("Менять каждые", Ui.Body, Ui.Muted); every.Anchor = AnchorStyles.Left;
        var seconds = Ui.Caption("сек", Ui.Body, Ui.Muted); seconds.Anchor = AnchorStyles.Left;
        interval.Value = (decimal)settings.IntervalSeconds; interval.Anchor = AnchorStyles.Left; interval.Margin = Ui.P(6, 0, 6, 0);
        tips.SetToolTip(interval, "Интервал смены обоев: от 5 секунд до 24 часов");
        byLength.Checked = settings.ByVideoLength; byLength.Anchor = AnchorStyles.Left; byLength.Margin = Ui.P(10, 0, 0, 0);
        tips.SetToolTip(byLength, "Менять видео, когда оно доиграет до конца, а не через заданное время. Картинки меняются через заданный интервал.");
        change.Controls.Add(every, 0, 0); change.Controls.Add(interval, 1, 0); change.Controls.Add(seconds, 2, 0); change.Controls.Add(byLength, 3, 0);
        layout.Controls.Add(change, 4, 0);
        sound.Anchor = AnchorStyles.Left; sound.Checked = settings.Music; volume.Value = settings.MusicVolume;
        tips.SetToolTip(volume, "Громкость музыки обоев. Меняет только звук обоев: громкость Windows и других программ не трогает.");
        layout.Controls.Add(sound, 5, 0); layout.Controls.Add(volume, 6, 0); layout.Controls.Add(volumeText, 7, 0);
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
        // "Отключить и выйти" in the settings: the window is closed first, then Wallsets.
        if (dialog.ExitRequested) Exit();
    }

    // ---- state used by the settings window ----
    internal void SaveSettings() => Save();
    internal void SetPreview(bool names, bool setNames) { settings.ShowNames = files.ShowNames = names; settings.ShowSetNames = files.ShowSetNames = setNames; Save(); }
    internal void SetScaling(string mode)
    {
        if (settings.Scaling == mode) return;
        settings.Scaling = player.Scaling = mode; Save();
        Run(async () => { if (player.Running) await player.ApplyScaling(current?.Path); });
    }
    internal void SetAutoPause(bool on) { settings.AutoPause = on; Save(); CheckAutoPause(); }
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
    const int MaxCopies = 99;
    List<MediaSet> ActiveSets => library.Where(s => settings.Selected.Contains(s.Id)).ToList();
    string ActiveKey => Library.CombinationKey(ActiveSets.Select(s => s.Id));
    SetOptions CombinedOptions()
    {
        if (!settings.Combinations.TryGetValue(ActiveKey, out var options)) settings.Combinations[ActiveKey] = options = new();
        return options;
    }
    MediaSet? ViewSet => ActiveSets.Count == 1 ? ActiveSets[0] : null;
    SetOptions ViewOptions => ViewSet is { } s ? Options(s.Id) : CombinedOptions();
    // Saved queues made for exactly the ticked sets, in the order they were saved.
    List<SavedQueue> ViewQueues => settings.Queues.Where(q => Library.CombinationKey(q.Sets) == ActiveKey).ToList();
    SavedQueue? Saved(SetOptions options) => options.Sort.StartsWith("saved:") ? settings.Queues.FirstOrDefault(q => "saved:" + q.Id == options.Sort) : null;
    // Dragging and moving tiles edit the saved queue that is shown, otherwise the manual order.
    string EditMode => Saved(ViewOptions) != null ? ViewOptions.Sort : "manual";
    SetOptions Options(string setId)
    {
        if (!settings.Sets.TryGetValue(setId, out var o)) settings.Sets[setId] = o = new();
        o.Order ??= []; o.Counts ??= new(); o.Sound ??= [];
        return o;
    }
    int CountOf(MediaFile file) => Options(file.SetId).Counts.TryGetValue(file.FileId, out var n) ? Math.Clamp(n, 1, MaxCopies) : 1;
    // The sound switch in the player bar turns sound on for every wallpaper; a single wallpaper can have its own.
    bool SoundFor(MediaFile? file) => settings.Music || file != null && Options(file.SetId).Sound.Contains(file.FileId);
    static List<string> KeysOf(IEnumerable<ThumbnailItem> items) => items.Select(i => i.Key).ToList();
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
            boxes[i].Checked = settings.Selected.Contains(library[i].Id);
        }
        updating = false; RefreshFiles();
    }
    void SetChecked(object? sender, EventArgs e)
    {
        if (updating || sender is not CheckBox { Tag: MediaSet set } box) return;
        settings.Selected.Remove(set.Id);
        if (box.Checked)
        {
            // "Выбранный набор" works like radio buttons: a new tick replaces the previous one.
            if (settings.SingleSet)
            {
                settings.Selected.Clear(); updating = true;
                foreach (var other in sets.Controls.OfType<CheckBox>()) if (other != box) other.Checked = false;
                updating = false;
            }
            settings.Selected.Add(set.Id);
        }
        Save(); Run(Rebuild);
    }
    async Task ChangeQueueMode()
    {
        settings.SingleSet = queueView.SelectedIndex == 1;
        // Keep the most recently ticked set that still exists.
        if (settings.SingleSet && settings.Selected.Count > 1)
            settings.Selected = settings.Selected.Where(id => library.Any(s => s.Id == id)).TakeLast(1).ToList();
        RefreshSets(); await Rebuild(); Save();
    }
    void RefreshFiles()
    {
        updating = true;
        var options = ViewOptions;
        var queues = ViewQueues;
        string[] modes = ["По умолчанию", "Вручную", "Перемешать", .. queues.Select(q => "«" + q.Name + "»")];
        if (!sorting.Items.Cast<string>().SequenceEqual(modes)) { sorting.Items.Clear(); sorting.Items.AddRange(modes); }
        var saved = Saved(options);
        int savedIndex = saved == null ? -1 : queues.IndexOf(saved);
        sorting.SelectedIndex = savedIndex >= 0 ? 3 + savedIndex : options.Sort == "manual" || saved != null ? 1 : options.Sort == "shuffle" ? 2 : 0;
        deleteQueue.Visible = savedIndex >= 0;
        if (saved != null) tips.SetToolTip(deleteQueue, $"Удалить сохранённую очередь «{saved.Name}»");
        var copies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        files.SetItems(queue.Select(f => { int copy = copies.GetValueOrDefault(f.Key); copies[f.Key] = copy + 1; return new ThumbnailItem(f.Path, f.SetName, f.Key, copy); }));
        files.Playing = current != null && position >= 0 && position < queue.Count ? TileId(position) : null;
        updating = false;
    }
    async Task Rescan()
    {
        bool first = library.Count == 0 && !File.Exists(settingsPath);
        // Reading the file ids can take a moment the first time: keep the window responsive.
        library = await Task.Run(() => Library.Scan(setsRoot));
        Migrate();
        if (first) settings.Selected = library.Take(2).Select(s => s.Id).ToList();
        // Watch the folders and save before starting playback, which may fail (desktop not ready yet).
        RefreshSets(); SetupWatchers(); Save();
        await Rebuild();
    }
    // Older versions named sets by their folder: move those settings to the set ids.
    void Migrate()
    {
        var ids = library.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>();
        foreach (var set in library) byName.TryAdd(set.Name, set.Id);
        string Id(string name) => ids.Contains(name) ? name : byName.TryGetValue(name, out var id) ? id : name;
        settings.Selected = settings.Selected.Select(Id).Distinct().ToList();
        foreach (var set in library)
            if (!settings.Sets.ContainsKey(set.Id) && settings.Sets.Remove(set.Name, out var old)) settings.Sets[set.Id] = old;
        foreach (var (key, options) in settings.Combinations.ToList())
        {
            List<string>? names;
            try { names = JsonSerializer.Deserialize<List<string>>(key); } catch (JsonException) { continue; }
            if (names == null) continue;
            var moved = Library.CombinationKey(names.Select(Id));
            if (moved != key && !settings.Combinations.ContainsKey(moved)) { settings.Combinations.Remove(key); settings.Combinations[moved] = options; }
        }
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
    // Puts the files in the order the options ask for. legacyPath gives the path older versions stored
    // instead of a key; such entries are replaced by keys.
    List<MediaFile> Arrange(List<MediaFile> files, SetOptions options, Func<MediaFile, string> legacyPath)
    {
        var saved = Saved(options);
        // The saved queue was deleted: keep the order it had.
        if (saved == null && options.Sort.StartsWith("saved:")) options.Sort = "manual";
        if (options.Sort == "default") return files;
        var order = saved?.Order ?? options.Order;
        var byKey = new Dictionary<string, MediaFile>(StringComparer.OrdinalIgnoreCase);
        var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) { byKey.TryAdd(file.Key, file); legacy.TryAdd(legacyPath(file), file.Key); }
        for (int i = 0; i < order.Count; i++)
            if (!byKey.ContainsKey(order[i]) && legacy.TryGetValue(order[i], out var key)) order[i] = key;
        return Library.Reconcile(files.Select(f => f.Key).ToList(), order).Select(k => byKey[k]).ToList();
    }
    // A set's files in its own order, each repeated as many times as set in the tile menu.
    List<MediaFile> SetFiles(MediaSet set) =>
        Arrange(set.Files.SelectMany(f => Enumerable.Repeat(f, CountOf(f))).ToList(), Options(set.Id), f => f.Relative);
    async Task Rebuild()
    {
        var active = ActiveSets;
        queue = active.SelectMany(SetFiles).ToList();
        if (active.Count > 1) queue = Arrange(queue, CombinedOptions(), f => Path.Combine(f.SetName, f.Relative));
        // The same wallpaper stays on screen, also when its file or set was renamed or the order changed.
        int index = current == null ? -1 : Locate(current.Key, currentCopy);
        if (index >= 0) { position = index; current = queue[index]; } else position = -1;
        RefreshFiles();
        if (queue.Count == 0) { current = null; player.Stop(); return; }
        if (current == null) await Play(ResumeIndex());
        else if (index < 0) await Play(0);
    }
    // Index of the given copy of a file in the queue (the last copy if there are fewer now), or -1.
    int Locate(string? key, int copy)
    {
        if (key == null) return -1;
        var found = Enumerable.Range(0, queue.Count).Where(i => string.Equals(queue[i].Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count == 0 ? -1 : found[Math.Clamp(copy, 0, found.Count - 1)];
    }
    int CopyAt(int index) => queue.Take(index).Count(f => string.Equals(f.Key, queue[index].Key, StringComparison.OrdinalIgnoreCase));
    string TileId(int index) => queue[index].Key + "#" + CopyAt(index);
    // Where to continue: the wallpaper on screen, or after a restart the one that was on screen before.
    int ResumeIndex()
    {
        if (current != null && position >= 0 && position < queue.Count) return position;
        int index = Locate(settings.CurrentFile, settings.CurrentCopy);
        if (index < 0 && settings.CurrentPath is { } path) index = queue.FindIndex(f => string.Equals(Path.Combine(f.SetName, f.Relative), path, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }
    void ChangeSort(bool reshuffle)
    {
        if (!reshuffle && sorting.SelectedIndex >= 3)
        {
            var queues = ViewQueues;
            int index = sorting.SelectedIndex - 3;
            if (index < queues.Count) { ViewOptions.Sort = "saved:" + queues[index].Id; Save(); Run(Rebuild); }
            return;
        }
        var order = KeysOf(files.Items);
        string mode = reshuffle ? "shuffle" : sorting.SelectedIndex == 1 ? "manual" : sorting.SelectedIndex == 2 ? "shuffle" : "default";
        if (mode == "shuffle") Library.Shuffle(order);
        StoreViewOrder(order, mode);
    }
    void StoreViewOrder(List<string> order, string mode)
    {
        var options = ViewOptions;
        if (mode == options.Sort && Saved(options) is { } saved) saved.Order = order;
        else { options.Sort = mode; options.Order = order; }
        Save(); Run(Rebuild);
    }
    void MoveSelected(int delta) => MoveItem(files.SelectedIndex, files.SelectedIndex + delta);
    void MoveItem(int from, int to)
    {
        if (from < 0 || to < 0 || from >= files.Items.Count || to >= files.Items.Count || from == to) return;
        var order = KeysOf(files.Items); var item = order[from]; order.RemoveAt(from); order.Insert(to, item);
        StoreViewOrder(order, EditMode);
        files.SelectedIndex = to;
    }
    void ShowSelected()
    {
        int index = files.SelectedIndex;
        if (index < 0 || index >= queue.Count) return;
        var key = queue[index].Key;
        Run(async () => { if (index < queue.Count && queue[index].Key == key) await Play(index); });
    }
    // Asks for a name in a small dialog; null when cancelled.
    string? AskName(string title, string placeholder, string action, string text = "")
    {
        using var dialog = new Form { Text = title, Font = Ui.Body, BackColor = Ui.Surface, AutoScaleMode = AutoScaleMode.None, ClientSize = Ui.S(420, 132), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var input = new TextBox { Left = Ui.S(20), Top = Ui.S(22), Width = Ui.S(380), PlaceholderText = placeholder, Text = text };
        var ok = Ui.TextButton(action, () => { }, accent: true); ok.DialogResult = DialogResult.OK;
        ok.AutoSize = false; ok.Bounds = new Rectangle(Ui.S(260), Ui.S(76), Ui.S(140), Ui.S(36));
        dialog.Controls.AddRange([input, ok]); dialog.AcceptButton = ok;
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
    }
    void CreateSet()
    {
        if (AskName("Новый набор", "Название набора", "Создать") is not { } name) return;
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) { MessageBox.Show(this, "Введите допустимое название папки.", "Новый набор"); return; }
        Run(async () => { var path = Path.Combine(setsRoot, name); Directory.CreateDirectory(path); await Rescan(); OpenFolder(path); });
    }
    // Saves the queue as it is now under a name; it belongs to the ticked sets.
    void SaveQueue()
    {
        if (queue.Count == 0) { MessageBox.Show(this, "Очередь пуста: отметьте набор слева.", "Сохранить очередь"); return; }
        var options = ViewOptions;
        if (AskName("Сохранить очередь", "Название очереди", "Сохранить", Saved(options)?.Name ?? "") is not { } name) return;
        if (name.Length == 0) { MessageBox.Show(this, "Введите название очереди.", "Сохранить очередь"); return; }
        if (name.Length > 60) name = name[..60];
        var existing = ViewQueues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null && existing != Saved(options) && MessageBox.Show(this, $"Очередь «{existing.Name}» для этих наборов уже есть. Заменить её?", "Сохранить очередь", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var saved = existing ?? new SavedQueue { Sets = ActiveSets.Select(s => s.Id).ToList() };
        if (existing == null) settings.Queues.Add(saved);
        saved.Name = name; saved.Order = KeysOf(files.Items);
        options.Sort = "saved:" + saved.Id;
        Save(); Run(Rebuild);
    }
    void DeleteQueue()
    {
        if (Saved(ViewOptions) is not { } saved) return;
        if (MessageBox.Show(this, $"Удалить сохранённую очередь «{saved.Name}»? Обои останутся в том же порядке как «Вручную».", "Wallsets", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        settings.Queues.Remove(saved);
        foreach (var options in settings.Sets.Values.Concat(settings.Combinations.Values))
            if (options.Sort == "saved:" + saved.Id) { options.Sort = "manual"; options.Order = [.. saved.Order]; }
        Save(); Run(Rebuild);
    }
    // Right click on a tile: how many times the wallpaper is in the queue, and its own sound.
    void ShowTileMenu(Point location)
    {
        int index = files.SelectedIndex;
        if (index < 0 || index >= queue.Count) return;
        var file = queue[index];
        var menu = new ContextMenuStrip { Font = Ui.Body, ShowImageMargin = false, ShowCheckMargin = true, ShowItemToolTips = true };
        var name = Path.GetFileNameWithoutExtension(file.Path);
        var title = new ToolStripLabel(name.Length > 48 ? name[..47] + "…" : name) { Font = Ui.Strong, ForeColor = Ui.Text, ToolTipText = file.Relative };
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ProfessionalColors.ToolStripDropDownBackground, Margin = Padding.Empty, Padding = Ui.P(0, 2, 0, 2) };
        var label = new Label { Text = "Сколько раз в очереди", AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Body, ForeColor = Ui.Text, BackColor = row.BackColor, UseMnemonic = false, Margin = Ui.P(0, 0, 10, 0) };
        var number = new NumericUpDown { Minimum = 1, Maximum = MaxCopies, Value = CountOf(file), Width = Ui.S(64), TextAlign = HorizontalAlignment.Right, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        row.Controls.Add(label, 0, 0); row.Controls.Add(number, 1, 0);
        var count = new ToolStripControlHost(row) { AutoSize = true, Margin = Ui.P(0, 2, 0, 2) };
        var sound = new ToolStripMenuItem("Проигрывать со звуком") { CheckOnClick = true, Checked = Options(file.SetId).Sound.Contains(file.FileId) };
        if (settings.Music) sound.ToolTipText = "Сейчас звук включён для всех обоев кнопкой с динамиком; эта галочка действует, когда он выключен.";
        sound.CheckedChanged += (_, _) => SetFileSound(file, sound.Checked);
        var show = new ToolStripMenuItem("Показать на рабочем столе", null, (_, _) => ShowSelected());
        menu.Items.AddRange([title, new ToolStripSeparator(), count, sound, new ToolStripSeparator(), show]);
        // Enter and Esc would otherwise be taken by the menu itself, before the number field sees them.
        bool cancelled = false;
        foreach (Control part in number.Controls) part.PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Escape) e.IsInputKey = true; };
        number.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            cancelled = e.KeyCode == Keys.Escape; e.SuppressKeyPress = true; menu.Close();
        };
        menu.Opened += (_, _) => { number.Focus(); number.Select(0, number.Text.Length); };
        // The number is applied when the menu closes; Esc closes it without a change.
        menu.Closed += (_, e) =>
        {
            int value = cancelled || e.CloseReason == ToolStripDropDownCloseReason.Keyboard ? CountOf(file) : (int)number.Value;
            BeginInvoke(() => { menu.Dispose(); SetCount(file, index, value); });
        };
        menu.Show(location);
    }
    // More copies go right after the tile that was clicked; fewer copies are taken from the end of the queue
    // (Arrange keeps the first copies of a file). The number is kept in the set's options.
    void SetCount(MediaFile file, int index, int value)
    {
        int old = CountOf(file);
        value = Math.Clamp(value, 1, MaxCopies);
        if (value == old || exiting) return;
        if (index >= queue.Count || queue[index].Key != file.Key) index = queue.FindLastIndex(f => f.Key == file.Key);
        var options = Options(file.SetId);
        var view = ViewOptions;
        var set = library.FirstOrDefault(s => s.Id == file.SetId);
        if (value > old && index >= 0)
        {
            if (view.Sort != "default")
            {
                var order = Library.InsertCopies(queue.Select(f => f.Key).ToList(), index, file.Key, value - old);
                if (Saved(view) is { } saved) saved.Order = order; else view.Order = order;
            }
            else if (ViewSet == null && options.Sort != "default" && set != null)
            {
                // The ticked sets follow each other: the copies go into the set's own order.
                var own = SetFiles(set).Select(f => f.Key).ToList();
                int copy = CopyAt(index), at = -1;
                for (int i = 0, seen = 0; i < own.Count && at < 0; i++) if (own[i] == file.Key && seen++ == copy) at = i;
                var order = Library.InsertCopies(own, at < 0 ? own.Count - 1 : at, file.Key, value - old);
                if (Saved(options) is { } saved) saved.Order = order; else options.Order = order;
            }
        }
        if (value == 1) options.Counts.Remove(file.FileId); else options.Counts[file.FileId] = value;
        Save(); Run(Rebuild);
    }
    void SetFileSound(MediaFile file, bool on)
    {
        var list = Options(file.SetId).Sound;
        list.Remove(file.FileId);
        if (on) list.Add(file.FileId);
        Save(); UpdateSound();
        Run(async () => { if (player.Running) await player.SetSound(SoundFor(current)); });
    }

    // ---- playback ----
    void ResetDeadline() => nextChange = Environment.TickCount64 + (long)(settings.IntervalSeconds * 1000);
    // Videos wait for their end in this mode; pictures always use the interval.
    bool ChangesAtEnd => settings.ByVideoLength && current != null && !Library.IsPicture(current.Path);
    bool ShouldAutoPause() => locked || displayOff || settings.AutoPause && Native.DesktopCovered();
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
    async Task Play(int index)
    {
        var file = queue[index];
        var path = file.Path;
        if (!File.Exists(path)) { ScheduleScan(); return; }
        autoPaused = ShouldAutoPause();
        try { await player.Load(path, userPaused || autoPaused, SoundFor(file)); }
        catch { playbackFault = true; player.Stop(); throw; }
        current = file; position = index; currentCopy = CopyAt(index); videoLeft = null;
        ResetDeadline(); problem = null; playbackFault = false;
        nextHealth = Environment.TickCount64 + 5000;
        if (lastFrame == null) nextFrame = Environment.TickCount64 + 10000;
        var title = Path.GetFileNameWithoutExtension(path);
        tray.Text = ("Wallsets · " + title).Length <= 120 ? "Wallsets · " + title : ("Wallsets · " + title)[..119] + "…";
        files.Playing = TileId(index);
        // Remembered so that the same wallpaper comes back after a restart.
        settings.CurrentFile = file.Key; settings.CurrentCopy = currentCopy; settings.CurrentPath = Path.Combine(file.SetName, file.Relative);
        Save();
    }
    async Task Next()
    {
        if (queue.Count == 0) return;
        await Play(Library.AdjacentIndex(current == null ? -1 : position, queue.Count, 1));
    }
    async Task Previous()
    {
        if (queue.Count == 0) return;
        await Play(Library.AdjacentIndex(current == null ? -1 : position, queue.Count, -1));
    }
    void TogglePause() => Run(async () => { userPaused = !userPaused; if (player.Running) await player.Pause(userPaused || autoPaused); });
    internal void SetSound(bool on)
    {
        if (settings.Music == on) return;
        settings.Music = on; UpdateSound(); Save();
        Run(async () => { if (player.Running) await player.SetSound(SoundFor(current)); });
    }
    internal void SetByVideoLength(bool on)
    {
        if (byLength.Checked != on) byLength.Checked = on;
        if (settings.ByVideoLength == on) return;
        settings.ByVideoLength = on; videoLeft = null; ResetDeadline(); Save();
        Run(async () => await player.SetLoop(!on));
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
            await Play(ResumeIndex());
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
                await Play(ResumeIndex());
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
        if (ChangesAtEnd)
        {
            // Without looping mpv stops on the last frame of the video: then the next wallpaper comes.
            if ((await player.OptionalProperty("eof-reached")).ValueKind == JsonValueKind.True) { await Next(); return; }
            var left = await player.OptionalProperty("playtime-remaining");
            videoLeft = left.ValueKind == JsonValueKind.Number ? left.GetDouble() : null;
        }
        else if (Environment.TickCount64 >= nextChange) { await Next(); return; }
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
            if (idle.ValueKind == JsonValueKind.True || ended.ValueKind == JsonValueKind.True && !ChangesAtEnd)
            {
                problem = "Файл не воспроизводится; пропуск: " + Path.GetFileName(current?.Path);
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
        var remaining = TimeSpan.FromSeconds(Math.Max(0, (int)Math.Ceiling(RemainingSeconds)));
        string countdown = remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}:{remaining:mm\\:ss}" : $"{remaining:mm\\:ss}";
        status.ForeColor = problem != null ? Ui.Danger : queue.Count == 0 ? Ui.Muted : Ui.Text;
        status.Text = problem ?? (queue.Count == 0 ? "Отметьте набор слева" : ChangesAtEnd && videoLeft == null ? "Смена в конце видео" : "Смена через " + countdown);
        string state = userPaused ? " · на паузе" : autoPaused ? " · автопауза (экономия энергии)" : "";
        var tip = problem ?? (current == null ? "" : "Сейчас: " + Path.GetFileNameWithoutExtension(current.Path) + state + (SoundFor(current) ? $" · звук {settings.MusicVolume}%" : ""));
        if (tip != statusTip) { statusTip = tip; tips.SetToolTip(status, tip); }
    }
    double RemainingSeconds => ChangesAtEnd ? videoLeft ?? 0 : (nextChange - Environment.TickCount64) / 1000d;
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
    object Snapshot() => new { running = !exiting, currentFile = current?.Path, position, count = queue.Count, queue = queue.Select(f => f.Path), selected = settings.Selected, paused = userPaused, autoPaused, intervalSeconds = settings.IntervalSeconds, byVideoLength = settings.ByVideoLength, remainingSeconds = Math.Max(0, RemainingSeconds), playerPid = player.Pid, playerPipe = player.PipeName, attached = player.Attached, starts = player.Starts, renderer = player.WindowInfo, playbackFault, columns = files.Columns, thumbnailSize = settings.ThumbnailSize, singleSet = settings.SingleSet, music = settings.Music, musicVolume = settings.MusicVolume, scaling = settings.Scaling, showNames = settings.ShowNames, showSetNames = settings.ShowSetNames, hotkeys = hotkeyState, visible = Visible, problem = problem ?? hotkeyProblem };
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
    // "Отключить и выйти" (in the settings): stop the player and give Windows its own background back.
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
        SystemEvents.SessionSwitch -= SessionChanged;
        if (windowHook != IntPtr.Zero) { Native.UnhookWinEvent(windowHook); windowHook = IntPtr.Zero; }
        if (IsHandleCreated) SuspendHotkeys();
        if (powerNotification != IntPtr.Zero) { Native.UnregisterPowerSettingNotification(powerNotification); powerNotification = IntPtr.Zero; }
        floating?.Close(); player.Dispose(); tray.Visible = false; tray.Dispose();
    }
}
