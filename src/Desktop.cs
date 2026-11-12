using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Wallsets;

// Windows integration: desktop shortcuts, start with Windows and the Windows background picture.
internal static class Desktop
{
    static string Exe => Environment.ProcessPath ?? Path.Combine(Program.Root, "Wallsets.exe");

    // ---- Desktop shortcuts ----
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
    public static readonly (string Id, string Name, string Arguments, string Description)[] Shortcuts =
    [
        ("app", "Живые обои", "", "Открыть Wallsets"),
        ("pause", "Обои - пауза", "--pause", "Пауза / продолжить живые обои"),
        ("next", "Обои - следующие", "--next", "Следующие живые обои"),
        ("previous", "Обои - предыдущие", "--previous", "Предыдущие живые обои"),
    ];
    // Creates or updates the shortcut on the desktop and returns its path. Every shortcut, the program's
    // own included, takes its icon from a separate .ico file: a stale or broken entry in the Windows
    // icon cache for Wallsets.exe then cannot leave the shortcut without an icon.
    public static string CreateShortcut(string id)
    {
        var item = Shortcuts.First(s => s.Id == id);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), item.Name + ".lnk");
        var (icon, description) = id == "pause" ? PauseLook(paused) : (IconFile(id), item.Description);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Exe);
            link.SetArguments(item.Arguments);
            link.SetWorkingDirectory(Program.Root);
            link.SetDescription(description);
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
        // Explorer shows the new icon at once; for the program shortcut it also drops the icons it
        // has cached, which may include an empty one for Wallsets.exe.
        ShellChanged(SHCNE_UPDATEITEM, path);
        if (id == "app") ShellChanged(SHCNE_ASSOCCHANGED, null);
        return path;
    }

    // ---- The pause shortcut shows the state ----
    // While the wallpaper plays, "Обои - пауза" shows the pause sign; while it is paused, the play
    // triangle. The two pictures are different files, so Explorer swaps them without stale copies.
    static bool paused;
    static Task pauseLook = Task.CompletedTask;
    static readonly object pauseLock = new();
    static (string Icon, string Description) PauseLook(bool on) => on
        ? (IconFile("resume"), "Живые обои на паузе. Нажмите, чтобы продолжить")
        : (IconFile("pause"), "Живые обои идут. Нажмите, чтобы поставить на паузу");
    // Updates every desktop shortcut that runs Wallsets.exe --pause, in the background and in order.
    public static Task ShowPaused(bool on)
    {
        lock (pauseLock)
        {
            paused = on;
            return pauseLook = pauseLook.ContinueWith(_ => UpdatePauseShortcuts(on), TaskScheduler.Default);
        }
    }
    static void UpdatePauseShortcuts(bool on)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) return;
            var (icon, description) = PauseLook(on);
            foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    ((IPersistFile)link).Load(file, 0);
                    var text = new StringBuilder(1024);
                    link.GetPath(text, text.Capacity, IntPtr.Zero, 0);
                    if (!string.Equals(text.ToString(), Exe, StringComparison.OrdinalIgnoreCase)) continue;
                    text.Clear(); link.GetArguments(text, text.Capacity);
                    if (text.ToString().Trim() != "--pause") continue;
                    text.Clear(); link.GetIconLocation(text, text.Capacity, out _);
                    if (string.Equals(text.ToString(), icon, StringComparison.OrdinalIgnoreCase)) continue;
                    link.SetIconLocation(icon, 0);
                    link.SetDescription(description);
                    ((IPersistFile)link).Save(file, true);
                    ShellChanged(SHCNE_UPDATEITEM, file);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { Program.Log(ex); }
                finally { Marshal.ReleaseComObject(link); }
            }
        }
        catch (Exception ex) { Program.Log(ex); }
    }

    // ---- Icon files ----
    // Shortcut icons in the style of the program icon: a white sign on the same teal rounded square.
    // Pause (two upright bars) and play (a triangle) are drawn as shapes, so they look the same on every
    // Windows; next and previous come from the icon font. Written once; the version in the name changes
    // whenever the pictures do, so Explorer never shows an old cached copy.
    static readonly Dictionary<string, string> Signs = new()
    {
        ["pause"] = "bars",
        ["resume"] = "triangle",
        ["next"] = "",
        ["previous"] = "",
    };
    static readonly object iconLock = new();
    static string IconFile(string id)
    {
        var path = Path.Combine(Program.DataFolder("icons"), id + ".v3.ico");
        lock (iconLock)
        {
            if (File.Exists(path)) return path;
            byte[] bytes;
            if (Signs.TryGetValue(id, out var sign))
            {
                var images = new List<Bitmap>();
                try
                {
                    foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 256 }) images.Add(SignImage(size, sign));
                    bytes = Ico(images);
                }
                finally { foreach (var image in images) image.Dispose(); }
            }
            else
            {
                // The program icon, the same as in Wallsets.exe.
                using var stream = typeof(Desktop).Assembly.GetManifestResourceStream("Wallsets.app.ico") ?? throw new FileNotFoundException("Wallsets.app.ico");
                using var copy = new MemoryStream(); stream.CopyTo(copy); bytes = copy.ToArray();
            }
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, true);
        }
        return path;
    }
    static Bitmap SignImage(int size, string sign)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var r = new RectangleF(size * .04f, size * .04f, size * .92f, size * .92f);
        using var shape = Ui.RoundedPath(Rectangle.Round(r), (int)(size * .2f));
        // The gradient reaches a pixel past the square, so its edge rows do not wrap to the other colour.
        using var fill = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Color.FromArgb(54, 170, 156), Color.FromArgb(22, 92, 86), LinearGradientMode.Vertical);
        g.FillPath(fill, shape);
        float middle = size / 2f;
        if (sign == "bars")
        {
            // Whole pixels, so the bars stay sharp at 16 px.
            float height = MathF.Round(size * .44f), width = MathF.Max(2, MathF.Round(size * .13f)), gap = MathF.Max(2, MathF.Round(size * .11f));
            float top = MathF.Round(middle - height / 2), left = MathF.Round(middle - width - gap / 2);
            foreach (var x in new[] { left, left + width + gap })
            {
                if (size < 48) { g.FillRectangle(Brushes.White, x, top, width, height); continue; }
                using var bar = Ui.RoundedPath(Rectangle.Round(new RectangleF(x, top, width, height)), (int)(width * .3f));
                g.FillPath(Brushes.White, bar);
            }
        }
        else if (sign == "triangle")
        {
            // Pointing right, moved a little right of the middle so it looks centred.
            float height = size * .48f, width = height * .88f, left = middle - width / 2 + size * .03f;
            g.FillPolygon(Brushes.White, new[] { new PointF(left, middle - height / 2), new PointF(left + width, middle), new PointF(left, middle + height / 2) });
        }
        else
        {
            using var font = new Font(Program.IconFont, size * .42f, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(sign, font, Brushes.White, new RectangleF(0, size * .03f, size, size), format);
        }
        return bitmap;
    }
    // ICO file with classic 32-bit bitmaps for the small sizes and PNG only for 256 x 256, the layout
    // that every part of Windows reads (small PNG images can come out blank in Explorer and shortcuts).
    internal static byte[] Ico(IReadOnlyList<Bitmap> images)
    {
        var entries = new List<(int Size, byte[] Data)>();
        foreach (var image in images)
        {
            int size = image.Width;
            if (size >= 256)
            {
                using var png = new MemoryStream(); image.Save(png, ImageFormat.Png);
                entries.Add((size, png.ToArray()));
                continue;
            }
            var pixels = new byte[size * size * 4];
            var data = image.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < size; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size * 4, size * 4); }
            finally { image.UnlockBits(data); }
            int maskStride = (size + 31) / 32 * 4;
            using var dib = new MemoryStream();
            using (var w = new BinaryWriter(dib, Encoding.UTF8, true))
            {
                // BITMAPINFOHEADER: the height counts the colour rows and the transparency mask.
                w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(size * size * 4 + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                // Rows bottom-up, BGRA with straight alpha, then the 1-bit mask (1 = transparent).
                for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);
                for (int y = size - 1; y >= 0; y--)
                {
                    var row = new byte[maskStride];
                    for (int x = 0; x < size; x++) if (pixels[(y * size + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                    w.Write(row);
                }
            }
            entries.Add((size, dib.ToArray()));
        }
        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, Encoding.UTF8, true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)entries.Count);
            int offset = 6 + 16 * entries.Count;
            foreach (var (size, bytes) in entries)
            {
                w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(bytes.Length); w.Write(offset); offset += bytes.Length;
            }
            foreach (var (_, bytes) in entries) w.Write(bytes);
        }
        return file.ToArray();
    }

    // ---- Explorer notifications ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);
    const int SHCNE_UPDATEITEM = 0x2000, SHCNE_ASSOCCHANGED = 0x8000000;
    static void ShellChanged(int what, string? path)
    {
        // SHCNF_PATHW for a file, SHCNF_IDLIST without an item for the association change;
        // SHCNF_FLUSHNOWAIT hands the notice over without waiting for Explorer.
        try { SHChangeNotify(what, (path != null ? 0x0005u : 0u) | 0x3000u, path, IntPtr.Zero); }
        catch (Exception ex) { Program.Log(ex); }
    }

    // ---- Start with Windows ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string RunValue = "Wallsets";
    static string StartCommand => "\"" + Exe + "\" --tray";
    public enum StartState { Off, On, DisabledInTaskManager }
    // The entry is looked for in the user's startup list and in the all-users list (used when the
    // user's list could not be written). Task Manager's "Disable" marks the entry in StartupApproved:
    // an odd first byte means switched off.
    public static StartState Autostart
    {
        get
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var run = hive.OpenSubKey(RunKey);
                if (!string.Equals(run?.GetValue(RunValue) as string, StartCommand, StringComparison.OrdinalIgnoreCase)) continue;
                using var approved = hive.OpenSubKey(ApprovedKey);
                return approved?.GetValue(RunValue) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1 ? StartState.DisabledInTaskManager : StartState.On;
            }
            return StartState.Off;
        }
    }
    // Writes or removes the startup entry; clearing the Task Manager mark makes "on" really on.
    public static void WriteAutostart(RegistryKey hive, bool on)
    {
        using (var run = hive.CreateSubKey(RunKey))
        {
            if (on) run.SetValue(RunValue, StartCommand);
            else run.DeleteValue(RunValue, false);
        }
        using var approved = hive.OpenSubKey(ApprovedKey, true);
        approved?.DeleteValue(RunValue, false);
    }
    // Turns start with Windows on or off. Normally no rights are needed (the user's own list);
    // if that list is locked, Windows asks for administrator rights and the all-users list is used.
    public static void SetAutostart(bool on)
    {
        bool inAllUsers;
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey)) inAllUsers = run?.GetValue(RunValue) != null;
        try
        {
            WriteAutostart(Registry.CurrentUser, on);
            // An all-users entry (made earlier with administrator rights) has to go as well.
            if (!on && inAllUsers) Elevated("--autostart-off");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Elevated(on ? "--autostart-on" : "--autostart-off");
        }
    }
    static void Elevated(string argument)
    {
        // Throws Win32Exception 1223 when the user declines the Windows prompt.
        using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Exe, argument) { UseShellExecute = true, Verb = "runas" })
            ?? throw new InvalidOperationException("Не удалось запустить Wallsets с правами администратора.");
        if (!helper.WaitForExit(60000) || helper.ExitCode != 0) throw new InvalidOperationException("Не удалось изменить автозапуск даже с правами администратора.");
    }
    public static void OpenTaskManagerStartup() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe", "/0 /startup") { UseShellExecute = true });

    // ---- Windows background picture ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, string? value, uint flags);
    const string DesktopKey = @"Control Panel\Desktop";
    // WallpaperStyle/TileWallpaper values of the Windows "Fit", "Stretch", "Fill" and "Tile" options.
    static (string Style, string Tile) WindowsStyle(string scaling) => scaling switch { "fit" => ("6", "0"), "stretch" => ("2", "0"), "tile" => ("0", "1"), _ => ("10", "0") };
    public static string FramesFolder => Program.DataFolder("frames");
    static bool IsFrame(string? path) => path != null && path.StartsWith(FramesFolder, StringComparison.OrdinalIgnoreCase);
    // Shows the frame as the Windows background, remembering the user's own picture the first time.
    public static void ApplyFrame(Settings settings, string frame)
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKey, true);
        if (desktop == null) return;
        var current = desktop.GetValue("WallPaper") as string;
        if (!settings.FrameApplied || !IsFrame(current))
        {
            settings.OriginalWallpaper = current ?? "";
            settings.OriginalWallpaperStyle = desktop.GetValue("WallpaperStyle") as string ?? "10";
            settings.OriginalTileWallpaper = desktop.GetValue("TileWallpaper") as string ?? "0";
        }
        var (style, tile) = WindowsStyle(settings.Scaling);
        desktop.SetValue("WallpaperStyle", style); desktop.SetValue("TileWallpaper", tile);
        if (SystemParametersInfo(0x14, 0, frame, 3)) settings.FrameApplied = true;
    }
    // Puts the user's own Windows background back, unless they have chosen another one since.
    public static void RestoreWallpaper(Settings settings)
    {
        if (!settings.FrameApplied) return;
        using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKey, true);
        if (desktop != null && IsFrame(desktop.GetValue("WallPaper") as string))
        {
            desktop.SetValue("WallpaperStyle", settings.OriginalWallpaperStyle ?? "10");
            desktop.SetValue("TileWallpaper", settings.OriginalTileWallpaper ?? "0");
            SystemParametersInfo(0x14, 0, settings.OriginalWallpaper ?? "", 3);
        }
        settings.FrameApplied = false;
    }
    // Keeps the newest frames and the one Windows currently shows.
    public static void CleanFrames(string keep)
    {
        try
        {
            using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKey);
            var shown = desktop?.GetValue("WallPaper") as string;
            foreach (var file in Directory.GetFiles(FramesFolder, "*.jpg").Where(f => !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, shown, StringComparison.OrdinalIgnoreCase)))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
