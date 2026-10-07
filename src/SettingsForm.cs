namespace Wallsets;

// Settings opened by the gear button. Every change applies at once; "Готово" only closes the window.
internal sealed class SettingsForm : Form
{
    readonly MainForm main;
    readonly Settings settings;
    readonly ToolTip tips = new();
    readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Window };
    readonly CheckBox music = Check("Проигрывать с музыкой");
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, TickStyle = TickStyle.None, AutoSize = false, BackColor = Ui.Surface };
    readonly Label volumeText = Ui.Caption("");
    readonly Dictionary<string, (HotkeyBox Box, Label Status)> hotkeys = [];
    // Result of the "taken combinations" check: a wrapped label that scrolls when the list is long.
    readonly Panel takenBox = new() { AutoScroll = true, BackColor = Color.FromArgb(246, 248, 249) };
    readonly Label taken = new() { AutoSize = true, Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Color.FromArgb(246, 248, 249), UseMnemonic = false, Location = new Point(Ui.S(8), Ui.S(6)) };
    readonly Dictionary<string, string> notes = [];
    int y;
    bool updating;
    static int CardWidth => Ui.S(600);

    public SettingsForm(MainForm main)
    {
        this.main = main; settings = main.Settings;
        Text = "Настройки · Wallsets"; Icon = main.Icon; Font = Ui.Body; BackColor = Ui.Window; ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        int width = CardWidth + Ui.S(40) + SystemInformation.VerticalScrollBarWidth;
        var area = Screen.FromControl(main).WorkingArea;
        ClientSize = new Size(width, Math.Min(Ui.S(780), area.Height - Ui.S(80)));

        var footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(60), BackColor = Ui.Window };
        var done = Ui.TextButton("Готово", Close, accent: true);
        done.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        footer.Controls.Add(done);
        footer.Layout += (_, _) => done.Location = new Point(footer.ClientSize.Width - done.Width - Ui.S(20), (footer.Height - done.Height) / 2);
        Controls.Add(content); Controls.Add(footer);
        AcceptButton = done; CancelButton = done;

        y = Ui.S(16);
        Preview(); Scaling(); Sound(); Energy(); Startup(); Shortcuts(); Hotkeys();
        content.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(1, Ui.S(4)), BackColor = Ui.Window });

        main.StateChanged += SyncSound;
        FormClosed += (_, _) => { main.StateChanged -= SyncSound; main.RegisterHotkeys(); };
        RefreshHotkeys();
    }

    // ---- building blocks ----
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, BackColor = Ui.Surface, ForeColor = Ui.Text, Font = Ui.Body, UseMnemonic = false, Cursor = Cursors.Hand };
    sealed class Section(Card card)
    {
        public readonly Card Card = card;
        public int Y = card.HeadingHeight + Ui.S(4);
        public readonly int Left = Ui.S(20), Width = card.Width - Ui.S(40);
        public T Add<T>(T control, int indent = 0, int gap = 8) where T : Control
        {
            control.Location = new Point(Left + indent, Y);
            if (control.AutoSize) control.Size = control.PreferredSize;
            Card.Controls.Add(control);
            Y += control.Height + Ui.S(gap);
            return control;
        }
        // Wrapped secondary text under an option.
        public Label Hint(string text, int indent = 0, int gap = 10)
        {
            var label = new Label { Text = text, Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Surface, AutoSize = false, UseMnemonic = false };
            label.Size = new Size(Width - indent, TextRenderer.MeasureText(text, Ui.Small, new Size(Width - indent, 0), TextFormatFlags.WordBreak).Height + Ui.S(2));
            Y -= Ui.S(4);
            return Add(label, indent, gap);
        }
    }
    Section Begin(string heading) => new(new Card { Heading = heading, Location = new Point(Ui.S(20), y), Width = CardWidth });
    void End(Section section)
    {
        section.Card.Height = section.Y + Ui.S(8);
        content.Controls.Add(section.Card);
        y += section.Card.Height + Ui.S(14);
    }
    static int Indent => Ui.S(22);

    // ---- sections ----
    void Preview()
    {
        var s = Begin("Превью в очереди");
        var names = s.Add(Check("Показывать имена файлов"));
        var sets = s.Add(Check("Показывать, из какого набора файл"), gap: 12);
        names.Checked = settings.ShowNames; sets.Checked = settings.ShowSetNames;
        names.CheckedChanged += (_, _) => main.SetPreview(names.Checked, sets.Checked);
        sets.CheckedChanged += (_, _) => main.SetPreview(names.Checked, sets.Checked);
        End(s);
    }
    void Scaling()
    {
        var s = Begin("Масштаб фона");
        (string Mode, string Title, string Hint)[] modes =
        [
            ("fit", "По размеру фона", "Картинка видна целиком, без обрезки. Если пропорции не совпадают с экраном, по краям остаются поля."),
            ("stretch", "Растянуть", "Картинка растягивается на весь экран без сохранения пропорций."),
            ("fill", "Заполнение", "Картинка заполняет весь экран с сохранением пропорций, лишнее по краям обрезается."),
            ("tile", "Замостить", "Картинка в своём размере повторяется плиткой от левого верхнего угла. Картинка или видео больше экрана показываются в своём размере без повтора."),
        ];
        int gap = Ui.S(8), w = (s.Width - gap * 3) / 4;
        var row = new Panel { Size = new Size(s.Width, Ui.S(40)), BackColor = Ui.Surface };
        var hint = new Label { Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Surface, AutoSize = false, UseMnemonic = false };
        for (int i = 0; i < modes.Length; i++)
        {
            var (mode, title, text) = modes[i];
            var option = new RadioButton { Text = title, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(i * (w + gap), 0, w, Ui.S(40)), BackColor = Ui.Surface, ForeColor = Ui.Text, Cursor = Cursors.Hand, UseMnemonic = false, Checked = settings.Scaling == mode };
            option.FlatAppearance.BorderColor = Ui.Border; option.FlatAppearance.CheckedBackColor = Ui.AccentLight;
            option.FlatAppearance.MouseOverBackColor = Ui.Hover; option.FlatAppearance.MouseDownBackColor = Ui.Pressed;
            option.CheckedChanged += (_, _) =>
            {
                option.FlatAppearance.BorderColor = option.Checked ? Ui.Accent : Ui.Border;
                option.Font = option.Checked ? Ui.Strong : Ui.Body;
                if (!option.Checked) return;
                hint.Text = text; main.SetScaling(mode);
            };
            option.FlatAppearance.BorderColor = option.Checked ? Ui.Accent : Ui.Border; option.Font = option.Checked ? Ui.Strong : Ui.Body;
            row.Controls.Add(option);
        }
        s.Add(row, gap: 10);
        hint.Text = modes.First(m => m.Mode == settings.Scaling).Hint;
        int tallest = modes.Max(m => TextRenderer.MeasureText(m.Hint, Ui.Small, new Size(s.Width, 0), TextFormatFlags.WordBreak).Height);
        hint.Size = new Size(s.Width, tallest + Ui.S(2));
        s.Add(hint, gap: 10);
        End(s);
    }
    void Sound()
    {
        var s = Begin("Звук");
        s.Add(music, gap: 10);
        var row = new Panel { Size = new Size(s.Width, Ui.S(34)), BackColor = Ui.Surface };
        var label = Ui.Caption("Громкость");
        label.Location = new Point(0, (row.Height - label.PreferredHeight) / 2);
        volume.Bounds = new Rectangle(Ui.S(96), 0, s.Width - Ui.S(160), Ui.S(34));
        volumeText.Location = new Point(volume.Right + Ui.S(8), (row.Height - volumeText.PreferredHeight) / 2);
        row.Controls.AddRange([label, volume, volumeText]);
        s.Add(row, gap: 10);
        s.Hint("Своя громкость обоев: громкость Windows и других программ не меняется (общая громкость Windows действует поверх). В микшере громкости Windows обои подписаны «Wallsets». Пауза останавливает и музыку.");
        music.CheckedChanged += (_, _) => { if (!updating) main.SetSound(music.Checked); };
        volume.ValueChanged += (_, _) => { volumeText.Text = volume.Value + "%"; if (!updating) main.SetVolume(volume.Value); };
        SyncSound(this, EventArgs.Empty);
        End(s);
    }
    void Energy()
    {
        var s = Begin("Экономия энергии");
        var covered = s.Add(Check("Пауза за развёрнутым окном"), gap: 4);
        s.Hint("Видео и музыка останавливаются, пока развёрнутое или полноэкранное окно закрывает рабочий стол основного монитора — даже если поверх него открыто маленькое окно или активна панель задач.", Indent);
        var battery = s.Add(Check("Пауза от батареи"), gap: 4);
        s.Hint("Пауза, пока ноутбук работает без зарядки; с зарядкой воспроизведение продолжается.", Indent);
        s.Hint("На паузе декодирование и отрисовка останавливаются: нагрузка на процессор и видеокарту падает почти до нуля. При блокировке компьютера и выключенном экране пауза включается всегда. Таймер смены обоев продолжает идти.");
        covered.Checked = settings.AutoPause; battery.Checked = settings.PauseOnBattery;
        covered.CheckedChanged += (_, _) => main.SetAutoPause(covered.Checked);
        battery.CheckedChanged += (_, _) => main.SetBatteryPause(battery.Checked);
        End(s);
    }
    void Startup()
    {
        var s = Begin("Запуск вместе с Windows");
        var autostart = s.Add(Check("Запускать вместе с Windows"), gap: 4);
        s.Hint("При входе в Windows Wallsets запускается в трее, без окна, и сразу включает обои.", Indent);
        var frame = s.Add(Check("Сразу показывать обои при включении"), gap: 4);
        s.Hint("При выключении компьютера последний кадр обоев ставится фоном Windows. После включения сразу видна та же картинка вместо фона Windows, а через несколько секунд она оживает. «Отключить и выйти» возвращает ваш прежний фон Windows.", Indent);
        try { autostart.Checked = Desktop.Autostart; } catch (Exception ex) { Program.Log(ex); }
        frame.Checked = settings.WindowsFrame;
        autostart.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try { Desktop.Autostart = autostart.Checked; }
            catch (Exception ex)
            {
                Program.Log(ex); updating = true; autostart.Checked = !autostart.Checked; updating = false;
                MessageBox.Show(this, "Не удалось изменить автозапуск: " + ex.Message, "Wallsets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        frame.CheckedChanged += (_, _) => main.SetWindowsFrame(frame.Checked);
        End(s);
    }
    void Shortcuts()
    {
        var s = Begin("Добавить ярлык на рабочий стол");
        int gap = Ui.S(10), w = (s.Width - gap) / 2;
        var grid = new Panel { Size = new Size(s.Width, Ui.S(40) * 2 + gap), BackColor = Ui.Surface };
        var result = new Label { Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Surface, AutoSize = false, Size = new Size(s.Width, Ui.S(20)), UseMnemonic = false, Text = "Ярлык появится на рабочем столе; повторное нажатие обновит его." };
        for (int i = 0; i < Desktop.Shortcuts.Length; i++)
        {
            var (id, name, _, _, description) = Desktop.Shortcuts[i];
            var button = Ui.TextButton(name, () =>
            {
                try { Desktop.CreateShortcut(id); result.ForeColor = Ui.Good; result.Text = $"Ярлык «{name}» добавлен на рабочий стол."; }
                catch (Exception ex) { Program.Log(ex); result.ForeColor = Ui.Danger; result.Text = "Не удалось создать ярлык: " + ex.Message; }
            });
            button.AutoSize = false; button.Bounds = new Rectangle(i % 2 * (w + gap), i / 2 * (Ui.S(40) + gap), w, Ui.S(40));
            tips.SetToolTip(button, description);
            grid.Controls.Add(button);
        }
        s.Add(grid, gap: 8);
        s.Add(result, gap: 10);
        End(s);
    }
    void Hotkeys()
    {
        var s = Begin("Горячие клавиши и мини-пульт");
        s.Hint("Щёлкните по полю и нажмите новое сочетание; Backspace убирает сочетание. Wallsets сразу проверяет, не занято ли оно другой программой.");
        foreach (var (id, title, _) in HotkeyActions.All)
        {
            var row = new Panel { Size = new Size(s.Width, Ui.S(34)), BackColor = Ui.Surface };
            var label = Ui.Caption(title); label.Location = new Point(0, (row.Height - label.PreferredHeight) / 2);
            var box = new HotkeyBox { Font = Ui.Body, Bounds = new Rectangle(Ui.S(180), Ui.S(2), Ui.S(200), Ui.S(30)), BackColor = Color.FromArgb(246, 248, 249), BorderStyle = BorderStyle.FixedSingle };
            var status = new Label { Font = Ui.Small, BackColor = Ui.Surface, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Bounds = new Rectangle(Ui.S(392), 0, s.Width - Ui.S(392), row.Height), TextAlign = ContentAlignment.MiddleLeft };
            box.Enter += (_, _) => { main.SuspendHotkeys(); notes.Remove(id); box.Text = "Нажмите сочетание…"; };
            box.Leave += (_, _) => { main.RegisterHotkeys(); RefreshHotkeys(); };
            box.Captured += (_, key) => Captured(id, key);
            row.Controls.AddRange([label, box, status]);
            hotkeys[id] = (box, status);
            s.Add(row, gap: 6);
        }
        s.Y += Ui.S(4);
        var buttons = new FlowLayoutPanel { Size = new Size(s.Width, Ui.S(40)), BackColor = Ui.Surface, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(Ui.TextButton("По умолчанию", () => { settings.Hotkeys.Clear(); main.SaveSettings(); main.RegisterHotkeys(); RefreshHotkeys(); }));
        buttons.Controls.Add(Ui.TextButton("Какие сочетания заняты?", ShowTaken));
        s.Add(buttons, gap: 8);
        takenBox.Size = new Size(s.Width, Ui.S(66));
        taken.MaximumSize = new Size(s.Width - Ui.S(16) - SystemInformation.VerticalScrollBarWidth, 0);
        taken.Text = "Проверяются Ctrl+Alt, Ctrl+Shift, Alt+Shift и Ctrl+Alt+Shift с буквами, цифрами и F1–F12.";
        takenBox.Controls.Add(taken);
        s.Add(takenBox, gap: 14);
        var floating = s.Add(Check("Мини-пульт поверх окон"), gap: 4);
        s.Hint("Маленькое окно с кнопками «предыдущие», «пауза» и «следующие», которое всегда видно.", Indent);
        floating.Checked = settings.Floating;
        floating.CheckedChanged += (_, _) => main.SetFloating(floating.Checked);
        End(s);
    }

    // ---- behaviour ----
    void SyncSound(object? sender, EventArgs e)
    {
        updating = true;
        music.Checked = settings.Music; volume.Value = settings.MusicVolume; volumeText.Text = settings.MusicVolume + "%";
        updating = false;
    }
    void Captured(string id, Hotkey key)
    {
        var (box, status) = hotkeys[id];
        void Say(string text, Color color) { status.Text = text; status.ForeColor = color; tips.SetToolTip(status, text); }
        // A refused combination keeps its reason on screen after the field loses focus.
        void Refuse(string reason) { notes[id] = $"{reason} — оставлено {HotkeyActions.Get(settings, id).Display}"; Say(reason, reason.StartsWith("Добавьте") ? Ui.Warning : Ui.Danger); }
        if (key.IsEmpty) { settings.Hotkeys[id] = ""; main.SaveSettings(); box.Text = key.Display; Say("Сочетание убрано", Ui.Muted); return; }
        box.Text = key.Display;
        if (!key.IsValid) { Refuse("Добавьте Ctrl, Alt или Win"); return; }
        var other = HotkeyActions.All.FirstOrDefault(a => a.Id != id && HotkeyActions.Get(settings, a.Id) == key);
        if (other.Id != null) { Refuse("Уже назначено: " + other.Title); return; }
        if (!HotkeyActions.IsFree(Handle, key)) { Refuse($"{key.Display} занято другой программой"); return; }
        notes.Remove(id);
        settings.Hotkeys[id] = key.ToString(); main.SaveSettings();
        Say("Свободно — назначено", Ui.Good);
    }
    void RefreshHotkeys()
    {
        foreach (var (id, (box, status)) in hotkeys)
        {
            var key = HotkeyActions.Get(settings, id);
            if (!box.Focused) box.Text = key.Display;
            var (text, color) = notes.TryGetValue(id, out var note) ? (note, Ui.Danger) : key.IsEmpty ? ("Не назначено", Ui.Muted) : main.HotkeyWorks(id) ? ("Работает", Ui.Good) : ("Занято другой программой — выберите другое", Ui.Danger);
            status.Text = text; status.ForeColor = color; tips.SetToolTip(status, text);
        }
    }
    void ShowTaken()
    {
        Cursor = Cursors.WaitCursor;
        var list = HotkeyActions.Taken(Handle, HotkeyActions.All.Select(a => HotkeyActions.Get(settings, a.Id)));
        Cursor = Cursors.Default;
        taken.ForeColor = Ui.Text;
        taken.Text = list.Count == 0
            ? "Среди Ctrl+Alt, Ctrl+Shift, Alt+Shift и Ctrl+Alt+Shift с буквами, цифрами и F1–F12 занятых другими программами нет."
            : $"Заняты другими программами ({list.Count}): " + string.Join(", ", list) + ". Сочетания с Win почти все заняты самой Windows.";
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}
