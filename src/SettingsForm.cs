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
    // "Отключить и выйти" was pressed: the main window exits once this window is closed.
    public bool ExitRequested { get; private set; }
    AreaFrame? frame;

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
        var help = Ui.TextButton("?", () => { using var window = new HelpForm(Icon); window.ShowDialog(this); });
        help.AutoSize = false; help.Size = Ui.S(40, 36); help.Font = Ui.Heading; help.Padding = Padding.Empty;
        help.AccessibleName = "Справка"; tips.SetToolTip(help, "Справка: как пользоваться Wallsets");
        footer.Controls.AddRange([done, help]);
        footer.Layout += (_, _) =>
        {
            done.Location = new Point(footer.ClientSize.Width - done.Width - Ui.S(20), (footer.Height - done.Height) / 2);
            help.Location = new Point(Ui.S(20), (footer.Height - help.Height) / 2);
        };
        Controls.Add(content); Controls.Add(footer);
        AcceptButton = done; CancelButton = done;

        y = Ui.S(16);
        Startup(); Preview(); Scaling(); WallpaperArea(); Sound(); Energy(); Shortcuts(); Hotkeys(); Quit();
        content.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(1, Ui.S(4)), BackColor = Ui.Window });

        main.StateChanged += SyncSound;
        FormClosed += (_, _) => { main.StateChanged -= SyncSound; main.RegisterHotkeys(); frame?.Close(); };
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
    // The wallpaper can take only a part of the screen: four coordinates, a frame on the screen that follows
    // them while they are typed, and a window that shows what the scaling cuts off.
    void WallpaperArea()
    {
        var s = Begin("Область обоев");
        s.Hint("Обои можно показать не на весь экран, а в его части. Координаты — в пикселях от левого верхнего угла экрана: где проходят верхняя, нижняя, левая и правая стороны области. Обои меняются сразу; вне области экран чёрный.");
        var screen = main.ScreenBounds;
        var area = settings.Area ?? new ScreenArea { Right = screen.Width, Bottom = screen.Height };
        (string Title, int Max, int Value)[] sides = [("Верх", screen.Height, area.Top), ("Низ", screen.Height, area.Bottom), ("Лево", screen.Width, area.Left), ("Право", screen.Width, area.Right)];
        var row = new Panel { Size = new Size(s.Width, Ui.S(34)), BackColor = Ui.Surface };
        var fields = new NumericUpDown[sides.Length];
        int w = s.Width / sides.Length;
        for (int i = 0; i < sides.Length; i++)
        {
            var label = Ui.Caption(sides[i].Title); label.Location = new Point(i * w, (row.Height - label.PreferredHeight) / 2);
            fields[i] = new NumericUpDown { Minimum = 0, Maximum = sides[i].Max, Value = Math.Clamp(sides[i].Value, 0, sides[i].Max), Font = Ui.Body, TextAlign = HorizontalAlignment.Right, Bounds = new Rectangle(i * w + Ui.S(52), Ui.S(2), w - Ui.S(64), Ui.S(30)) };
            row.Controls.AddRange([label, fields[i]]);
        }
        s.Add(row, gap: 6);
        var state = new Label { Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Surface, AutoSize = false, Size = new Size(s.Width, Ui.S(20)), UseMnemonic = false };
        s.Add(state, gap: 8);
        var buttons = new FlowLayoutPanel { Size = new Size(s.Width, Ui.S(40)), BackColor = Ui.Surface, WrapContents = false, Margin = Padding.Empty };
        Button? show = null;
        void ShowFrame()
        {
            if (frame == null) return;
            frame.ShowArea(main.WallpaperBox);
        }
        void HideFrame() { frame?.Close(); frame = null; if (show != null) show.Text = "Показать область текущих обоев"; }
        void ShowState()
        {
            state.ForeColor = Ui.Muted;
            state.Text = main.AreaRect is { } r ? $"Обои в области {r.Width} × {r.Height} пикселей." : $"Обои на весь экран {screen.Width} × {screen.Height}.";
        }
        // Applied while the numbers are typed, not only when the field is left.
        void Apply()
        {
            if (updating) return;
            var v = new int[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                if (!int.TryParse(fields[i].Text.Trim(), out v[i]) || v[i] < 0 || v[i] > sides[i].Max) { state.ForeColor = Ui.Danger; state.Text = $"«{sides[i].Title}»: число от 0 до {sides[i].Max}."; return; }
            var chosen = new ScreenArea { Top = v[0], Bottom = v[1], Left = v[2], Right = v[3] };
            if (!chosen.Valid) { state.ForeColor = Ui.Danger; state.Text = $"Нижняя сторона должна быть ниже верхней, правая — правее левой, хотя бы на {ScreenArea.Smallest} пикселя."; return; }
            main.SetArea(chosen); ShowState(); ShowFrame();
        }
        foreach (var field in fields) { field.ValueChanged += (_, _) => Apply(); field.TextChanged += (_, _) => Apply(); }
        show = Ui.TextButton("Показать область текущих обоев", () =>
        {
            if (frame != null) { HideFrame(); return; }
            frame = new AreaFrame(); ShowFrame(); show!.Text = "Скрыть рамку";
        });
        tips.SetToolTip(show, "Рамка по краям области, где показываются обои. Она меняется сразу, пока вы вводите координаты.");
        var reset = Ui.TextButton("Вернуть обои на весь экран", () =>
        {
            main.SetArea(null); HideFrame();
            updating = true;
            for (int i = 0; i < fields.Length; i++) fields[i].Value = i switch { 1 => screen.Height, 3 => screen.Width, _ => 0 };
            updating = false;
            ShowState();
        });
        buttons.Controls.AddRange([show, reset]);
        s.Add(buttons, gap: 6);
        var crop = Ui.TextButton("Показать, как обои обрезаются", () =>
        {
            if (OwnedForms.OfType<CropPreview>().FirstOrDefault() is { } open) { open.Activate(); return; }
            new CropPreview(main).Show(this);
        });
        tips.SetToolTip(crop, "Окно в уменьшенном масштабе: картинка или видео целиком, экран и область обоев. Красным — то, что обрезается.");
        s.Add(crop, gap: 10);
        ShowState();
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
        s.Hint("На паузе декодирование и отрисовка останавливаются: нагрузка на процессор и видеокарту падает почти до нуля. При блокировке компьютера и выключенном экране пауза включается всегда. Таймер смены обоев продолжает идти.");
        covered.Checked = settings.AutoPause;
        covered.CheckedChanged += (_, _) => main.SetAutoPause(covered.Checked);
        End(s);
    }
    void Startup()
    {
        var s = Begin("Автозапуск");
        var autostart = s.Add(Check("Запускать Wallsets вместе с Windows"), gap: 4);
        string[] texts =
        [
            "Включено: при входе в Windows Wallsets запускается в трее, без окна, и сразу включает обои. В диспетчере задач он виден на вкладке «Автозагрузка» под именем Wallsets.",
            "Выключено. После включения Wallsets появится в диспетчере задач на вкладке «Автозагрузка».",
            "Отключено в диспетчере задач. Поставьте галочку, чтобы включить снова.",
        ];
        var state = s.Hint(texts.MaxBy(t => t.Length)!, Indent, gap: 6);
        var open = Ui.TextButton("Открыть автозагрузку в диспетчере задач", () =>
        {
            try { Desktop.OpenTaskManagerStartup(); } catch (Exception ex) { Program.Log(ex); }
        });
        s.Add(open, Indent, gap: 12);
        var frame = s.Add(Check("Сразу показывать обои при включении"), gap: 4);
        s.Hint("При выключении компьютера последний кадр обоев ставится фоном Windows. После включения сразу видна та же картинка вместо фона Windows, а через несколько секунд она оживает. «Отключить и выйти» возвращает ваш прежний фон Windows.", Indent);
        void Show()
        {
            var current = Desktop.StartState.Off;
            try { current = Desktop.Autostart; } catch (Exception ex) { Program.Log(ex); }
            updating = true; autostart.Checked = current == Desktop.StartState.On; updating = false;
            state.Text = current switch { Desktop.StartState.On => texts[0], Desktop.StartState.DisabledInTaskManager => texts[2], _ => texts[1] };
            state.ForeColor = current == Desktop.StartState.DisabledInTaskManager ? Ui.Warning : Ui.Muted;
        }
        Show();
        // Re-read after coming back from Task Manager, where it can be switched too.
        Activated += (_, _) => Show();
        autostart.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try { Desktop.SetAutostart(autostart.Checked); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show(this, "Для автозапуска Windows попросила права администратора, но они не были даны. Автозапуск не изменён.", "Wallsets", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                MessageBox.Show(this, "Не удалось изменить автозапуск: " + ex.Message, "Wallsets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Show();
        };
        frame.Checked = settings.WindowsFrame;
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

    void Quit()
    {
        var s = Begin("Отключить Wallsets");
        var exit = Ui.TextButton("Отключить и выйти", () => { ExitRequested = true; Close(); }, danger: true);
        s.Add(exit, gap: 8);
        s.Hint("Останавливает обои, закрывает Wallsets (и значок в трее) и возвращает обычный фон Windows. Крестик главного окна только прячет его — обои продолжают работать.");
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
