using System.Runtime.InteropServices;
using System.Text;

namespace Wallsets;

internal static class Native
{
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder cls, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr RegisterPowerSettingNotification(IntPtr hwnd, ref Guid setting, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBINFO info, uint size);
    [DllImport("kernel32.dll")] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct BASICLIMIT { public long PerProcess, PerJob; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] internal struct IOCOUNTERS { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] internal struct JOBINFO { public BASICLIMIT Basic; public IOCOUNTERS Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }

    // The desktop of the primary monitor is hidden when the active window covers it (this also catches
    // always-on-top full-screen games and players) or when any ordinary window below the active one does
    // (a small window on top of a maximized one, the taskbar or Start menu being active).
    internal static bool DesktopCovered()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !IsDesktop(WindowClass(foreground)) && WindowClass(foreground) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            && IsWindowVisible(foreground) && !IsIconic(foreground) && !IsCloaked(foreground) && GetWindowRect(foreground, out var active) && Covers(active, area))
            return true;
        bool covered = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = WindowClass(h);
            if (IsDesktop(cls)) return false;
            if (IsIconic(h) || IsCloaked(h) || cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            // Skip always-on-top overlays, tool palettes, click-through and fully transparent windows.
            int ex = GetWindowLong(h, -20);
            if ((ex & (0x8 | 0x80 | 0x20)) != 0) return true;
            if ((ex & 0x80000) != 0 && GetLayeredWindowAttributes(h, out uint colorKey, out var alpha, out var flags) && (flags & 2) != 0 && alpha == 0) return true;
            if (!GetWindowRect(h, out var r) || !Covers(r, area)) return true;
            covered = true;
            return false;
        }, IntPtr.Zero);
        return covered;
    }
    // The desktop itself: "#32769" is the root desktop window, active when everything is minimized.
    static bool IsDesktop(string cls) => cls is "Progman" or "WorkerW" or "#32769";
    static bool Covers(RECT r, Rectangle b) => r.Left <= b.Left + 2 && r.Top <= b.Top + 2 && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
    // Hidden UWP frames and windows on other virtual desktops are "cloaked" but still report as visible.
    static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    internal static string WindowClass(IntPtr handle)
    {
        var name = new StringBuilder(256);
        GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }
    internal static IntPtr FindDesktopPlayer(int pid)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (WindowClass(top) is "Progman" or "WorkerW")
                EnumChildWindows(top, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var owner);
                    if (owner == pid && WindowClass(child) == "mpv") result = child;
                    return result == IntPtr.Zero;
                }, IntPtr.Zero);
            return result == IntPtr.Zero;
        }, IntPtr.Zero);
        return result;
    }
    internal static object DescribeWindow(IntPtr hwnd)
    {
        var parent = GetParent(hwnd);
        GetWindowRect(hwnd, out var r);
        return new { handle = hwnd.ToInt64(), windowClass = WindowClass(hwnd), parent = parent.ToInt64(), parentClass = WindowClass(parent), visible = IsWindowVisible(hwnd), x = r.Left, y = r.Top, width = r.Right - r.Left, height = r.Bottom - r.Top };
    }
}
