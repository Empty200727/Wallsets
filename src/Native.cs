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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
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

    internal static bool DesktopCovered()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var foreground = GetForegroundWindow();
        switch (foreground == IntPtr.Zero ? "" : WindowClass(foreground))
        {
            case "Progman" or "WorkerW": return false;
            case "" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd": break;
            default:
                if (!IsOwn(foreground) && !IsCloaked(foreground)) return GetWindowRect(foreground, out var r) && Covers(r, area);
                break;
        }
        // The taskbar, this program or nothing is active: the top application window below decides.
        bool covered = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = WindowClass(h);
            if (cls is "Progman" or "WorkerW") return false;
            if (IsIconic(h) || IsOwn(h) || IsCloaked(h) || cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            // Skip always-on-top overlays, tool palettes and click-through windows.
            if ((GetWindowLong(h, -20) & (0x8 | 0x80 | 0x20)) != 0 || !GetWindowRect(h, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
            covered = Covers(r, area);
            return false;
        }, IntPtr.Zero);
        return covered;
    }
    static bool Covers(RECT r, Rectangle b) => r.Left <= b.Left + 2 && r.Top <= b.Top + 2 && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
    static bool IsOwn(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid == Environment.ProcessId; }
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
