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
        ("pause", "Обои - пауза", PauseCommand, "Пауза / продолжить живые обои"),
        ("next", "Обои - следующие", "--next", "Следующие живые обои"),
        ("previous", "Обои - предыдущие", "--previous", "Предыдущие живые обои"),
    ];
    const string PauseCommand = "--pause";
    // Creates or updates the shortcut on the desktop and returns its path. The pause shortcut shows the state of the
    // wallpaper (see ShowPlaying), the others the program icon.
    public static string CreateShortcut(string id)
    {
        var item = Shortcuts.First(s => s.Id == id);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), item.Name + ".lnk");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Exe);
            link.SetArguments(item.Arguments);
            link.SetWorkingDirectory(Program.Root);
            link.SetDescription(item.Description);
            link.SetIconLocation(item.Arguments == PauseCommand ? PauseIcon(Playing) : Exe, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
        Changed(path);
        return path;
    }

    // ---- The pause shortcut shows the state of the wallpaper ----
    // Two bars while the wallpaper plays (a click pauses it), a triangle while it is paused or Wallsets is off (a click
    // plays it). Every shortcut on the desktop that runs Wallsets.exe --pause changes, also one the user made or
    // renamed. Explorer shows the new icon once the shortcut points to another icon file and is told it changed.
    public static bool Playing { get; private set; } = true;
    static readonly object iconLock = new();
    // Runs in the background unless wait is set (at exit); the latest state wins.
    public static void ShowPlaying(bool playing, bool wait = false)
    {
        Playing = playing;
        void Apply() { lock (iconLock) { try { SetPauseIcons(PauseIcon(Playing)); } catch (Exception ex) { Program.Log(ex); } } }
        if (wait) Apply(); else Task.Run(Apply);
    }
    static void SetPauseIcons(string icon)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var dir = Environment.GetFolderPath(folder);
            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
            foreach (var link in Directory.EnumerateFiles(dir, "*.lnk"))
                // The desktop of all users is often not writable without administrator rights: such a shortcut stays.
                try { SetPauseIcon(link, icon); } catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
        }
    }
    static void SetPauseIcon(string path, string icon)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024); link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(1024); link.GetArguments(arguments, arguments.Capacity);
            if (!IsWallsets(target.ToString()) || arguments.ToString().Trim() != PauseCommand) return;
            var current = new StringBuilder(1024); link.GetIconLocation(current, current.Capacity, out var index);
            if (index == 0 && string.Equals(current.ToString(), icon, StringComparison.OrdinalIgnoreCase)) return;
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
        Changed(path);
    }
    // Tells Explorer to read the shortcut again.
    static void Changed(string path) => Native.SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
    // Drawn once; the number in the name changes with the drawing, so Windows' icon cache never shows an older one.
    static string PauseIcon(bool playing)
    {
        var path = Path.Combine(Program.DataFolder("icons"), (playing ? "pause" : "play") + "-1.ico");
        if (!File.Exists(path)) File.WriteAllBytes(path, IcoFile(size => StateIcon(size, playing)));
        return path;
    }
    // In the colours of the program icon: two white bars (playing) or a white triangle (paused).
    internal static Bitmap StateIcon(int size, bool playing)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var r = new RectangleF(size * .04f, size * .04f, size * .92f, size * .92f);
        using (var shape = Ui.RoundedPath(Rectangle.Round(r), Math.Max(2, (int)(size * .2f))))
        using (var fill = new LinearGradientBrush(new PointF(0, r.Top), new PointF(0, r.Bottom + 1), Color.FromArgb(51, 163, 150), Color.FromArgb(24, 97, 91)))
            g.FillPath(fill, shape);
        if (playing)
        {
            // On whole pixels, so the bars stay sharp at 16 px.
            int height = (int)Math.Round(size * .46), width = Math.Max(2, (int)Math.Round(size * .13)), gap = Math.Max(2, (int)Math.Round(size * .12));
            int top = (size - height) / 2, left = (size - 2 * width - gap) / 2;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(Brushes.White, left, top, width, height);
            g.FillRectangle(Brushes.White, left + width + gap, top, width, height);
        }
        else
        {
            // Pointing right, a little right of the middle so it looks centred.
            float height = size * .48f, width = height * .87f, x = size / 2f + size * .04f, y = size / 2f;
            g.FillPolygon(Brushes.White, new[] { new PointF(x - width / 2, y - height / 2), new PointF(x + width / 2, y), new PointF(x - width / 2, y + height / 2) });
        }
        return bitmap;
    }
    // An .ico file: plain 32-bit bitmaps up to 128 px, which every Windows reads, and PNG at 256 px. Windows reads PNG
    // reliably only at that size: an icon of PNGs alone (as app.ico was) can show blank on some computers.
    internal static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    internal static byte[] IcoFile(Func<int, Bitmap> draw)
    {
        var images = new List<(int Size, byte[] Data)>();
        foreach (var size in IconSizes)
        {
            using var bitmap = draw(size);
            images.Add((size, size >= 256 ? Png(bitmap) : Dib(bitmap)));
        }
        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, Encoding.UTF8, true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (size, data) in images)
            {
                w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(data.Length); w.Write(offset); offset += data.Length;
            }
            foreach (var (_, data) in images) w.Write(data);
        }
        return file.ToArray();
    }
    static byte[] Png(Bitmap bitmap) { using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png); return png.ToArray(); }
    // BITMAPINFOHEADER with twice the height (picture and mask), BGRA rows from the bottom up, then the 1-bit mask
    // (set where the picture is fully transparent), each mask row padded to 4 bytes.
    static byte[] Dib(Bitmap bitmap)
    {
        int size = bitmap.Width, maskRow = (size + 31) / 32 * 4;
        var pixels = new byte[size * size * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < size; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size * 4, size * 4); }
        finally { bitmap.UnlockBits(data); }
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(size * size * 4 + maskRow * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);
            for (int y = size - 1; y >= 0; y--)
            {
                var row = new byte[maskRow];
                for (int x = 0; x < size; x++) if (pixels[(y * size + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
        }
        return stream.ToArray();
    }

    // ---- Start with Windows ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    const string RunValue = "Wallsets";
    static string StartCommand => "\"" + Exe + "\" --tray";
    public enum StartState { Off, On, DisabledInTaskManager }
    // One way Windows starts a Wallsets.exe at sign-in, as Task Manager lists it on "Startup apps": a value of the
    // Run key of the user or of all users, or a shortcut in the user's or the common Startup folder. Path is the
    // program it starts; Enabled is false when Task Manager switched it off (an odd first byte in StartupApproved).
    public sealed record StartEntry(bool AllUsers, string? Value, string? Shortcut, string Path, bool Enabled)
    {
        public bool Ours => string.Equals(System.IO.Path.GetFullPath(Path), System.IO.Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
    }
    public static List<StartEntry> StartEntries()
    {
        var result = new List<StartEntry>();
        foreach (var (hive, allUsers) in new[] { (Registry.CurrentUser, false), (Registry.LocalMachine, true) })
        {
            using var run = hive.OpenSubKey(RunKey);
            using var approved = hive.OpenSubKey(ApprovedRun);
            foreach (var name in run?.GetValueNames() ?? [])
                if (ProgramOf(run!.GetValue(name) as string) is { } program)
                    result.Add(new(allUsers, name, null, program, !Disabled(approved, name)));
        }
        foreach (var (folder, allUsers) in new[] { (Environment.SpecialFolder.Startup, false), (Environment.SpecialFolder.CommonStartup, true) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
            using var approved = (allUsers ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(ApprovedFolder);
            foreach (var link in Directory.EnumerateFiles(dir, "*.lnk"))
                if (ShortcutTarget(link) is { } target && IsWallsets(target))
                    result.Add(new(allUsers, null, link, target, !Disabled(approved, System.IO.Path.GetFileName(link))));
        }
        return result;
    }
    static bool Disabled(RegistryKey? approved, string name) => approved?.GetValue(name) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
    static bool IsWallsets(string path) => string.Equals(System.IO.Path.GetFileName(path), "Wallsets.exe", StringComparison.OrdinalIgnoreCase);
    // The program of a Run command: quoted, or everything up to ".exe".
    internal static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (command.StartsWith('"')) { int end = command.IndexOf('"', 1); path = end > 0 ? command[1..end] : command[1..]; }
        else { int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); path = end > 0 ? command[..(end + 4)] : command; }
        return IsWallsets(path) ? path : null;
    }
    static string? ShortcutTarget(string link)
    {
        var shell = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(link, 0);
            var path = new StringBuilder(1024);
            shell.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.Length > 0 ? path.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return null; }
        finally { Marshal.ReleaseComObject(shell); }
    }
    // On while any entry is on (Windows will start Wallsets), whatever folder the program is in.
    public static StartState Autostart => StateOf(StartEntries());
    public static StartState StateOf(List<StartEntry> entries) => entries.Any(e => e.Enabled) ? Desktop.StartState.On : entries.Count > 0 ? Desktop.StartState.DisabledInTaskManager : Desktop.StartState.Off;
    // Removes every entry of the user or of all users that starts a Wallsets.exe and, when on, writes the one for
    // this program, with Task Manager's mark cleared, so exactly what the checkbox shows happens at sign-in.
    public static void WriteAutostart(RegistryKey hive, bool on)
    {
        bool allUsers = hive == Registry.LocalMachine;
        foreach (var entry in StartEntries().Where(e => e.AllUsers == allUsers))
        {
            if (entry.Value != null)
            {
                using (var run = hive.OpenSubKey(RunKey, true)) run?.DeleteValue(entry.Value, false);
                using var approved = hive.OpenSubKey(ApprovedRun, true);
                approved?.DeleteValue(entry.Value, false);
            }
            else if (entry.Shortcut != null)
            {
                File.Delete(entry.Shortcut);
                using var approved = hive.OpenSubKey(ApprovedFolder, true);
                approved?.DeleteValue(System.IO.Path.GetFileName(entry.Shortcut), false);
            }
        }
        if (!on) return;
        using (var run = hive.CreateSubKey(RunKey)) run.SetValue(RunValue, StartCommand);
        using var mark = hive.OpenSubKey(ApprovedRun, true);
        mark?.DeleteValue(RunValue, false);
    }
    // Turns start with Windows on or off. Normally no rights are needed (the user's own list); entries for all
    // users, or a locked user's list, need administrator rights, which Windows asks for.
    public static void SetAutostart(bool on)
    {
        bool allUsers = StartEntries().Any(e => e.AllUsers);
        try
        {
            WriteAutostart(Registry.CurrentUser, on);
            // An entry for all users (made with administrator rights, or by hand) has to go as well.
            if (allUsers) Elevated("--autostart-off");
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
