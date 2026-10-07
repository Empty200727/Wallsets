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
    public static readonly (string Id, string Name, string Arguments, string Glyph, string Description)[] Shortcuts =
    [
        ("app", "Живые обои", "", "", "Открыть Wallsets"),
        ("pause", "Обои - пауза", "--pause", "", "Пауза / продолжить живые обои"),
        ("next", "Обои - следующие", "--next", "", "Следующие живые обои"),
        ("previous", "Обои - предыдущие", "--previous", "", "Предыдущие живые обои"),
    ];
    // Creates or updates the shortcut on the desktop and returns its path.
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
            if (item.Glyph.Length > 0) link.SetIconLocation(IconFile(id, item.Glyph), 0);
            else link.SetIconLocation(Exe, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
        return path;
    }
    // Shortcut icons in the style of the program icon, drawn from the icon font.
    static string IconFile(string id, string glyph)
    {
        var path = Path.Combine(Program.DataFolder("icons"), id + ".ico");
        var images = new List<byte[]>();
        int[] sizes = [16, 24, 32, 48, 64, 256];
        foreach (var size in sizes)
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var r = new RectangleF(size * .04f, size * .04f, size * .92f, size * .92f);
                using var shape = Ui.RoundedPath(Rectangle.Round(r), (int)(size * .2f));
                using var fill = new LinearGradientBrush(r, Color.FromArgb(54, 170, 156), Color.FromArgb(22, 92, 86), LinearGradientMode.Vertical);
                g.FillPath(fill, shape);
                using var font = new Font(Program.IconFont, size * .42f, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph, font, Brushes.White, new RectangleF(0, size * .03f, size, size), format);
            }
            using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png); images.Add(png.ToArray());
        }
        // ICO file with PNG images (supported since Windows Vista).
        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, Encoding.UTF8, true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset); offset += images[i].Length;
            }
            foreach (var image in images) w.Write(image);
        }
        File.WriteAllBytes(path, file.ToArray());
        return path;
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
