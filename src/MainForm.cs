using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Wallsets;

internal sealed class MainForm : Form
{
    readonly string setsRoot = Path.Combine(Program.Root, "Наборы");
    readonly string settingsPath = Path.Combine(Program.Root, "settings.json");
    readonly Settings settings;
    readonly Player player = new(Program.Root);
    // Plain check boxes: a set is chosen only by its tick, there is no separate highlighted row.
    readonly FlowLayoutPanel sets = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
    readonly ThumbnailGrid files = new() { Dock = DockStyle.Fill };
    readonly ComboBox queueView = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    readonly ComboBox thumbnailSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly ComboBox sorting = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly NumericUpDown interval = new() { Minimum = 5, Maximum = 86400, Increment = 5, Width = 105, ThousandsSeparator = true };
    readonly Label current = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(65, 88, 85) };
    readonly CheckBox sound = new() { Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(36, 42), Margin = new Padding(0, 0, 5, 0), AccessibleName = "Проигрывать с музыкой" };
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, TickFrequency = 10, TickStyle = TickStyle.None, AutoSize = false, Height = 32, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Громкость музыки" };
    readonly Label volumeText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolTip tips = new();
    readonly NotifyIcon tray = new() { Text = "Wallsets", Icon = SystemIcons.Application, Visible = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 700 };
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    readonly List<FileSystemWatcher> watchers = [];
    readonly CancellationTokenSource cancellation = new();
    readonly SemaphoreSlim actions = new(1);
    readonly bool startInTray;
    readonly Button pause;
    readonly Button next;
    readonly ToolStripMenuItem trayPause = new("Пауза видео");
    readonly ToolStripMenuItem traySound = new("Проигрывать с музыкой");
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    List<MediaSet> library = [];
    List<string> queue = [];
    string? currentFile;
    bool updating, busy, exiting, userPaused, autoPaused, locked, displayOff;
    long nextChange;
    bool playbackFault, volumeSending, volumePending;
    string? problem;
    Form? floating;
    Button? floatingPause;
    IntPtr powerNotification;
    long nextHealth;

    public MainForm(bool startInTray)
    {
        this.startInTray = startInTray;
        Directory.CreateDirectory(setsRoot);
        settings = Settings.Load(settingsPath);
        Text = "Wallsets · Живые обои";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 247);
        ForeColor = Color.FromArgb(28, 35, 38);
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.Absolute, 50));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 92));
        layout.RowStyles.Add(new(SizeType.Absolute, 64));
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        Controls.Add(layout);
        var heading = new Label { Text = "Живые обои", Font = new Font("Segoe UI Semibold", 20), Dock = DockStyle.Fill };
        layout.Controls.Add(heading, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 12) };
        body.ColumnStyles.Add(new(SizeType.Absolute, 270)); body.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(body, 0, 1);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0, 0, 18, 0) };
        left.RowStyles.Add(new(SizeType.Absolute, 34)); left.RowStyles.Add(new(SizeType.Percent, 100)); left.RowStyles.Add(new(SizeType.Absolute, 44));
        left.Controls.Add(new Label { Text = "НАБОРЫ", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 0);
        sets.BackColor = BackColor; left.Controls.Add(sets, 0, 1);
        var setTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        setTools.Controls.Add(IconButton("\uE8B7", "Открыть папку наборов", () => OpenFolder(setsRoot)));
        setTools.Controls.Add(IconButton("\uE8F4", "Создать набор", CreateSet));
        setTools.Controls.Add(IconButton("\uE72C", "Обновить наборы", () => Run(Rescan)));
        left.Controls.Add(setTools, 0, 2); body.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        right.RowStyles.Add(new(SizeType.Absolute, 76)); right.RowStyles.Add(new(SizeType.Percent, 100)); right.RowStyles.Add(new(SizeType.Absolute, 44));
        var queueTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        queueView.Items.AddRange(["Общая очередь", "Выбранный набор"]); queueView.SelectedIndex = settings.SingleSet ? 1 : 0;
        tips.SetToolTip(queueView, "Общая очередь: можно отметить несколько наборов, их обои идут одной очередью и их можно перемешать между собой.\nВыбранный набор: отмечается только один набор.");
        queueTools.Controls.Add(queueView);
        sorting.Items.AddRange(["По умолчанию", "Вручную", "Перемешать"]); queueTools.Controls.Add(sorting);
        queueTools.Controls.Add(IconButton("\uE8B1", "Перемешать заново", () => ChangeSort(true)));
        queueTools.Controls.Add(new Label { Text = "Превью", AutoSize = true, Margin = new Padding(10, 7, 4, 0) });
        thumbnailSize.Items.AddRange(["Мелкие", "Средние", "Крупные", "Очень крупные"]); thumbnailSize.SelectedIndex = settings.ThumbnailSize; files.SizeIndex = settings.ThumbnailSize;
        queueTools.Controls.Add(thumbnailSize);
        right.Controls.Add(queueTools, 0, 0);
        right.Controls.Add(files, 0, 1);
        var fileTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        fileTools.Controls.Add(IconButton("\uE72B", "Раньше в очереди", () => MoveSelected(-1)));
        fileTools.Controls.Add(IconButton("\uE72A", "Позже в очереди", () => MoveSelected(1)));
        var show = new Button { Text = "Показать выбранные обои", AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat };
        show.Click += (_, _) => ShowSelected(); fileTools.Controls.Add(show);
        fileTools.Controls.Add(IconButton("\uE8B7", "Открыть папку выбранных обоев", () => { if (files.SelectedPath is { } f) OpenFolder(Path.GetDirectoryName(f)!); else OpenFolder(ViewSet?.Directory ?? setsRoot); }));
        right.Controls.Add(fileTools, 0, 2); body.Controls.Add(right, 1, 0);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        options.RowStyles.Add(new(SizeType.Percent, 50)); options.RowStyles.Add(new(SizeType.Percent, 50));
        var timing = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        timing.Controls.Add(new Label { Text = "Менять каждые", AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
        interval.Value = (decimal)settings.IntervalSeconds; timing.Controls.Add(interval);
        timing.Controls.Add(new Label { Text = "секунд", AutoSize = true, Margin = new Padding(8, 7, 24, 0) });
        var floatCheck = new CheckBox { Text = "Мини-пульт", Checked = settings.Floating, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        timing.Controls.Add(floatCheck); options.Controls.Add(timing, 0, 0);
        var energy = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var eco = new CheckBox { Text = "Пауза за развёрнутым окном", Checked = settings.AutoPause, AutoSize = true };
        var battery = new CheckBox { Text = "Пауза от батареи", Checked = settings.PauseOnBattery, AutoSize = true, Margin = new Padding(20, 3, 0, 0) };
        tips.SetToolTip(eco, "Останавливает видео и музыку, пока развёрнутое или полноэкранное окно закрывает рабочий стол основного монитора. После его сворачивания воспроизведение продолжается. Таймер смены работает.");
        tips.SetToolTip(battery, "Останавливает видео и музыку при работе от батареи. При подключении зарядки воспроизведение продолжается. Таймер смены работает.");
        energy.Controls.AddRange([eco, battery]); options.Controls.Add(energy, 0, 1); layout.Controls.Add(options, 0, 2);

        var playback = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, Padding = new Padding(0, 8, 0, 4) };
        foreach (var width in new[] { 48, 48, 48, 48, 150, 58 }) playback.ColumnStyles.Add(new(SizeType.Absolute, width));
        playback.ColumnStyles.Add(new(SizeType.Percent, 100)); playback.ColumnStyles.Add(new(SizeType.Absolute, 232));
        var previous = IconButton("\uE892", "Предыдущие обои (Ctrl+Alt+B)", () => Run(Previous));
        pause = IconButton("\uE769", "Пауза видео (Ctrl+Alt+P)", TogglePause);
        next = IconButton("\uE893", "Следующие обои (Ctrl+Alt+N)", () => Run(Next));
        previous.Height = pause.Height = next.Height = 42; playback.Controls.Add(previous, 0, 0); playback.Controls.Add(pause, 1, 0); playback.Controls.Add(next, 2, 0);
        sound.Font = new Font(Program.IconFont, 13); sound.FlatAppearance.BorderSize = 0; sound.FlatAppearance.CheckedBackColor = Color.FromArgb(214, 236, 233);
        sound.Checked = player.Sound = settings.Music; volume.Value = player.Volume = settings.MusicVolume; volume.BackColor = BackColor;
        tips.SetToolTip(volume, "Громкость музыки обоев. Меняет только звук обоев: громкость Windows и других программ не трогает.");
        playback.Controls.Add(sound, 3, 0); playback.Controls.Add(volume, 4, 0); playback.Controls.Add(volumeText, 5, 0); playback.Controls.Add(current, 6, 0);
        var stop = new Button { Text = "Отключить и выйти", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(145, 43, 47), Margin = new Padding(12, 0, 0, 0) };
        stop.Click += (_, _) => Exit(); playback.Controls.Add(stop, 7, 0); layout.Controls.Add(playback, 0, 3); layout.Controls.Add(status, 0, 4);
        UpdateSound();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Wallsets", null, (_, _) => ShowMain());
        menu.Items.Add(trayPause); trayPause.Click += (_, _) => TogglePause();
        menu.Items.Add("Предыдущие обои", null, (_, _) => Run(Previous));
        menu.Items.Add("Следующие обои", null, (_, _) => Run(Next));
        menu.Items.Add(traySound); traySound.Click += (_, _) => sound.Checked = !sound.Checked;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Отключить и вернуть Windows", null, (_, _) => Exit());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => ShowMain();

        sets.Resize += (_, _) => { foreach (Control box in sets.Controls) box.Width = SetWidth; };
        sorting.SelectedIndexChanged += (_, _) => { if (!updating) ChangeSort(false); };
        queueView.SelectedIndexChanged += (_, _) => Run(ChangeQueueMode);
        thumbnailSize.SelectedIndexChanged += (_, _) => { files.SizeIndex = settings.ThumbnailSize = thumbnailSize.SelectedIndex; Save(); };
        files.SizeIndexChanged += (_, _) => thumbnailSize.SelectedIndex = files.SizeIndex;
        Move += (_, _) => files.UpdateMetrics();
        sound.CheckedChanged += (_, _) => SetSound(sound.Checked);
        volume.ValueChanged += (_, _) => { settings.MusicVolume = player.Volume = volume.Value; UpdateSound(); ApplyVolume(); saveTimer.Stop(); saveTimer.Start(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        interval.ValueChanged += (_, _) => { settings.IntervalSeconds = (double)interval.Value; ResetDeadline(); Save(); };
        eco.CheckedChanged += (_, _) => { settings.AutoPause = eco.Checked; Save(); };
        battery.CheckedChanged += (_, _) => { settings.PauseOnBattery = battery.Checked; Save(); };
        floatCheck.CheckedChanged += (_, _) => { settings.Floating = floatCheck.Checked; UpdateFloating(); Save(); };
        files.ItemActivated += (_, _) => ShowSelected();
        files.OrderChanged += (_, _) => StoreViewOrder(files.Items.ToList(), "manual");
        timer.Tick += (_, _) => { if (!busy && !exiting) Run(Tick); };
        scanTimer.Tick += (_, _) => { scanTimer.Stop(); if (busy || files.IsDragging) { scanTimer.Start(); return; } Run(Rescan); };
        Shown += (_, _) => Run(async () => { await Rescan(); timer.Start(); UpdateFloating(); if (startInTray) Hide(); });
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else Cleanup(); };
        SystemEvents.SessionSwitch += SessionChanged;
        _ = Handle;
        Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.P);
        Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.N);
        Native.RegisterHotKey(Handle, 3, 0x4003, (uint)Keys.B);
        var displayGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        powerNotification = Native.RegisterPowerSettingNotification(Handle, ref displayGuid, 0);
        _ = Task.Run(Server);
    }

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
    Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button { Text = glyph, Font = new Font(Program.IconFont, 13), Size = new Size(36, 34), FlatStyle = FlatStyle.Flat, AccessibleName = tip, Margin = new Padding(0, 0, 5, 0) };
        b.FlatAppearance.BorderSize = 0; tips.SetToolTip(b, tip); b.Click += (_, _) => action(); return b;
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
                var box = new CheckBox { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Width = SetWidth, Height = 30, Margin = new Padding(0, 0, 0, 2), TextAlign = ContentAlignment.MiddleLeft };
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
        updating = false;
    }
    async Task Rescan()
    {
        bool first = library.Count == 0 && !File.Exists(settingsPath);
        library = Library.Scan(setsRoot);
        if (first) settings.Selected = library.Take(2).Select(s => s.Name).ToList();
        RefreshSets(); await Rebuild(); SetupWatchers(); Save();
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
        if (mode == "shuffle") Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(order));
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
        using var dialog = new Form { Text = "Новый набор", Font = Font, ClientSize = new Size(400, 128), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var input = new TextBox { Left = 18, Top = 20, Width = 364, PlaceholderText = "Название набора" };
        var ok = new Button { Text = "Создать", Left = 272, Top = 70, Width = 110, DialogResult = DialogResult.OK };
        dialog.Controls.AddRange([input, ok]); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = input.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) { MessageBox.Show(this, "Введите допустимое название папки.", "Новый набор"); return; }
        Run(async () => { var path = Path.Combine(setsRoot, name); Directory.CreateDirectory(path); await Rescan(); OpenFolder(path); });
    }
    void ResetDeadline() => nextChange = Environment.TickCount64 + (long)(settings.IntervalSeconds * 1000);
    bool ShouldAutoPause() => locked || displayOff || settings.AutoPause && Native.DesktopCovered() || settings.PauseOnBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
    async Task Play(string path)
    {
        if (!File.Exists(path)) { ScheduleScan(); return; }
        autoPaused = ShouldAutoPause();
        try { await player.Load(path, userPaused || autoPaused); }
        catch { playbackFault = true; player.Stop(); throw; }
        currentFile = path; ResetDeadline(); problem = null; playbackFault = false;
        nextHealth = Environment.TickCount64 + 5000;
        current.Text = Path.GetFileNameWithoutExtension(path); tips.SetToolTip(current, path);
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
    void SetSound(bool on)
    {
        if (settings.Music == on) return;
        settings.Music = player.Sound = on; UpdateSound(); Save();
        Run(async () => { if (player.Running) await player.SetSound(settings.Music); });
    }
    void UpdateSound()
    {
        sound.Text = settings.Music ? "\uE767" : "\uE74F";
        tips.SetToolTip(sound, settings.Music ? "Проигрывать с музыкой: включено. Нажмите, чтобы выключить звук" : "Проигрывать с музыкой: выключено. Нажмите, чтобы включить звук");
        if (sound.Checked != settings.Music) sound.Checked = settings.Music;
        traySound.Checked = settings.Music;
        volumeText.Text = settings.MusicVolume + "%";
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
        Run(async () =>
        {
            if (player.Running && player.Attached) return;
            player.Stop(); playbackFault = false;
            await Play(currentFile != null && queue.Contains(currentFile) ? currentFile : queue[0]);
        });
    }
    async Task Tick()
    {
        if (queue.Count == 0 || playbackFault) return;
        if (!player.Running || !player.Attached)
        {
            playbackFault = true;
            player.Stop();
            problem = "Подключение к рабочему столу потеряно. Нажмите «Следующие обои», чтобы повторить.";
            Program.Log(new IOException(problem));
            return;
        }
        if (!player.Running) return;
        bool automatic = ShouldAutoPause();
        if (automatic != autoPaused) { autoPaused = automatic; await player.Pause(userPaused || autoPaused); }
        if (Environment.TickCount64 >= nextChange) { await Next(); return; }
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
    void UpdateStatus()
    {
        pause.Text = userPaused ? "\uE768" : "\uE769";
        pause.AccessibleName = userPaused ? "Продолжить видео" : "Пауза видео";
        trayPause.Text = userPaused ? "Продолжить видео" : "Пауза видео";
        if (floatingPause != null) floatingPause.Text = pause.Text;
        if (queue.Count == 0) current.Text = "Набор не выбран или пуст";
        var remaining = Math.Max(0, (int)Math.Ceiling((nextChange - Environment.TickCount64) / 1000d));
        string mode = userPaused ? "Видео на паузе" : autoPaused ? "Автопауза · экономия энергии" : "Воспроизведение";
        string music = settings.Music ? $"    ·    С музыкой {settings.MusicVolume}%" : "";
        status.Text = problem ?? (queue.Count == 0 ? "Выберите набор" : $"{mode}{music}    ·    {queue.Count} обоев    ·    Смена через {remaining / 60:00}:{remaining % 60:00}");
    }
    void UpdateFloating()
    {
        if (!settings.Floating) { floating?.Close(); floating = null; floatingPause = null; return; }
        if (floating != null) return;
        floating = new Form { Text = "Пульт Wallsets", FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false, TopMost = true, ClientSize = new Size(130, 40), StartPosition = FormStartPosition.Manual, BackColor = BackColor, MaximizeBox = false, MinimizeBox = false };
        var b = Screen.PrimaryScreen!.WorkingArea; floating.Location = new Point(b.Right - 154, b.Bottom - 105);
        var p = IconButton("\uE892", "Предыдущие обои", () => Run(Previous)); p.Location = new Point(4, 3);
        floatingPause = IconButton(pause.Text, "Пауза / продолжить", TogglePause); floatingPause.Location = new Point(46, 3);
        var n = IconButton("\uE893", "Следующие обои", () => Run(Next)); n.Location = new Point(88, 3);
        floating.Controls.AddRange([p, floatingPause, n]); floating.FormClosed += (_, _) => { floating = null; floatingPause = null; }; floating.Show();
    }
    void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void SessionChanged(object? sender, SessionSwitchEventArgs e)
    {
        if (exiting) return;
        BeginInvoke(() => { if (e.Reason == SessionSwitchReason.SessionLock) locked = true; if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false; });
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) { if (m.WParam.ToInt32() == 1) TogglePause(); if (m.WParam.ToInt32() == 2) Run(Next); if (m.WParam.ToInt32() == 3) Run(Previous); }
        if (taskbarCreated != 0 && m.Msg == (int)taskbarCreated) RecoverDesktop();
        if (m.Msg == 0x218 && m.WParam.ToInt32() == 0x8013 && m.LParam != IntPtr.Zero) displayOff = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 20) == 0;
        base.WndProc(ref m);
    }
    object Snapshot() => new { running = !exiting, currentFile, count = queue.Count, queue, selected = settings.Selected, paused = userPaused, autoPaused, intervalSeconds = settings.IntervalSeconds, remainingSeconds = Math.Max(0, (nextChange - Environment.TickCount64) / 1000d), playerPid = player.Pid, playerPipe = player.PipeName, attached = player.Attached, starts = player.Starts, renderer = player.WindowInfo, playbackFault, columns = files.Columns, thumbnailSize = settings.ThumbnailSize, singleSet = settings.SingleSet, music = settings.Music, musicVolume = settings.MusicVolume, problem };
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
    void Exit() { if (exiting) return; exiting = true; Cleanup(); Close(); }
    void Cleanup()
    {
        exiting = true; timer.Stop(); scanTimer.Stop(); saveTimer.Stop(); cancellation.Cancel(); Save();
        foreach (var w in watchers) w.Dispose(); watchers.Clear();
        SystemEvents.SessionSwitch -= SessionChanged;
        Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2);
        Native.UnregisterHotKey(Handle, 3);
        if (powerNotification != IntPtr.Zero) { Native.UnregisterPowerSettingNotification(powerNotification); powerNotification = IntPtr.Zero; }
        floating?.Close(); player.Dispose(); tray.Visible = false; tray.Dispose();
    }
}
