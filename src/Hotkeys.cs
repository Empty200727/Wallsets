using System.Runtime.InteropServices;
using System.Text;

namespace Wallsets;

// A global key combination. Key holds the key code plus the Control/Alt/Shift flags; Win is separate
// because Windows Forms has no modifier flag for it.
internal readonly record struct Hotkey(Keys Key, bool Win)
{
    public static readonly Hotkey None = new(Keys.None, false);
    public Keys Code => Key & Keys.KeyCode;
    public bool IsEmpty => Code == Keys.None;
    public uint Modifiers => (Key.HasFlag(Keys.Alt) ? 1u : 0) | (Key.HasFlag(Keys.Control) ? 2u : 0) | (Key.HasFlag(Keys.Shift) ? 4u : 0) | (Win ? 8u : 0);
    bool HasModifier => Key.HasFlag(Keys.Control) || Key.HasFlag(Keys.Alt) || Win;
    // Plain keys would break typing everywhere; function and media keys may be used alone.
    public bool IsValid => !IsEmpty && !IsModifierKey(Code) && (HasModifier || Code is >= Keys.F1 and <= Keys.F24 or Keys.Pause or Keys.MediaNextTrack or Keys.MediaPreviousTrack or Keys.MediaPlayPause or Keys.MediaStop);
    public static bool IsModifierKey(Keys code) => code is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    // Stored form, e.g. "Ctrl+Alt+P" or "Ctrl+Shift+F5"; the key part is the Keys name.
    public override string ToString() => IsEmpty ? "" : Prefix() + Code;
    public string Display => IsEmpty ? "Не назначено" : Prefix() + KeyName(Code);
    string Prefix() => (Key.HasFlag(Keys.Control) ? "Ctrl+" : "") + (Key.HasFlag(Keys.Alt) ? "Alt+" : "") + (Key.HasFlag(Keys.Shift) ? "Shift+" : "") + (Win ? "Win+" : "");
    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        Keys key = Keys.None; bool win = false;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": key |= Keys.Control; break;
                case "alt": key |= Keys.Alt; break;
                case "shift": key |= Keys.Shift; break;
                case "win": win = true; break;
                default:
                    if (!Enum.TryParse<Keys>(part, true, out var code) || (code & ~Keys.KeyCode) != 0) return None;
                    key |= code; break;
            }
        }
        var result = new Hotkey(key, win);
        return result.IsValid ? result : None;
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int param, StringBuilder text, int size);
    static string KeyName(Keys code)
    {
        if (code is >= Keys.D0 and <= Keys.D9) return ((char)('0' + (code - Keys.D0))).ToString();
        if (code is >= Keys.A and <= Keys.Z || code is >= Keys.F1 and <= Keys.F24) return code.ToString();
        if (code is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num " + (code - Keys.NumPad0);
        var known = code switch
        {
            Keys.MediaNextTrack => "Следующий трек", Keys.MediaPreviousTrack => "Предыдущий трек", Keys.MediaPlayPause => "Воспроизведение/пауза", Keys.MediaStop => "Стоп",
            Keys.Left => "Влево", Keys.Right => "Вправо", Keys.Up => "Вверх", Keys.Down => "Вниз", Keys.PageUp => "PgUp", Keys.PageDown => "PgDn", _ => null
        };
        if (known != null) return known;
        try
        {
            // Localized name of punctuation and other keys from the active keyboard layout.
            var name = new StringBuilder(64);
            uint scan = MapVirtualKey((uint)code, 0);
            if (scan != 0 && GetKeyNameText((int)(scan << 16), name, name.Capacity) > 0) return name.ToString();
        }
        catch (EntryPointNotFoundException) { }
        return code.ToString();
    }
}

internal static class HotkeyActions
{
    public static readonly (string Id, string Title, string Default)[] All =
    [
        ("pause", "Пауза / продолжить", "Ctrl+Alt+P"),
        ("next", "Следующие обои", "Ctrl+Alt+N"),
        ("previous", "Предыдущие обои", "Ctrl+Alt+B"),
        ("sound", "Музыка вкл/выкл", "Ctrl+Alt+M"),
    ];
    public static Hotkey Get(Settings settings, string id) =>
        Hotkey.Parse(settings.Hotkeys.TryGetValue(id, out var text) ? text : All.First(a => a.Id == id).Default);
    public static string Title(string id) => All.First(a => a.Id == id).Title;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    // Windows only lets one program own a combination: if registration fails, someone else holds it.
    public static bool IsFree(IntPtr hwnd, Hotkey key)
    {
        const int probe = 0xBF00;
        if (!RegisterHotKey(hwnd, probe, key.Modifiers | 0x4000, (uint)key.Code)) return false;
        UnregisterHotKey(hwnd, probe);
        return true;
    }
    // Combinations of the usual modifier groups that other programs have already taken.
    public static List<string> Taken(IntPtr hwnd, IEnumerable<Hotkey> own)
    {
        var mine = own.Where(k => !k.IsEmpty).ToHashSet();
        var keys = Enumerable.Range(0, 26).Select(i => Keys.A + i).Concat(Enumerable.Range(0, 10).Select(i => Keys.D0 + i)).Concat(Enumerable.Range(0, 12).Select(i => Keys.F1 + i));
        var groups = new[] { Keys.Control | Keys.Alt, Keys.Control | Keys.Shift, Keys.Alt | Keys.Shift, Keys.Control | Keys.Alt | Keys.Shift };
        return groups.SelectMany(g => keys.Select(k => new Hotkey(g | k, false))).Where(k => !mine.Contains(k) && !IsFree(hwnd, k)).Select(k => k.Display).ToList();
    }
}

// Read-only box that records the next key combination pressed in it. While it has focus a low-level
// keyboard hook reads the keys first: a combination owned by another program never reaches the window,
// and this way the box can still say that it is taken.
internal sealed class HotkeyBox : TextBox
{
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool HideCaret(IntPtr hwnd);
    readonly HookProc proc;
    IntPtr hook;
    public event EventHandler<Hotkey>? Captured;
    public HotkeyBox() { ReadOnly = true; ShortcutsEnabled = false; Cursor = Cursors.Hand; TextAlign = HorizontalAlignment.Center; proc = KeyboardHook; }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); if (hook == IntPtr.Zero) hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); }
    // The box is not typed into, so no blinking text cursor.
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); HideCaret(Handle); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); HideCaret(Handle); }
    protected override void OnLeave(EventArgs e) { Unhook(); base.OnLeave(e); }
    protected override void Dispose(bool disposing) { Unhook(); base.Dispose(disposing); }
    void Unhook() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    static bool Down(int key) => GetAsyncKeyState(key) < 0;
    IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x100 or 0x104 && Focused)
        {
            var key = (Keys)Marshal.ReadInt32(data);
            if (Hotkey.IsModifierKey(key)) return CallNextHookEx(hook, code, message, data);
            var modifiers = (Down(0x11) ? Keys.Control : 0) | (Down(0x12) ? Keys.Alt : 0) | (Down(0x10) ? Keys.Shift : 0);
            bool win = Down(0x5B) || Down(0x5C);
            // Plain Tab still moves to the next field; plain Enter and Esc close the window as usual.
            if (key is Keys.Tab or Keys.Enter or Keys.Escape && modifiers == 0 && !win) return CallNextHookEx(hook, code, message, data);
            BeginInvoke(() => Take(key | modifiers, win));
            return 1;
        }
        return CallNextHookEx(hook, code, message, data);
    }
    void Take(Keys keyData, bool win)
    {
        var code = keyData & Keys.KeyCode;
        if (code is Keys.Back or Keys.Delete && (keyData & Keys.Modifiers) == 0 && !win) Captured?.Invoke(this, Hotkey.None);
        else Captured?.Invoke(this, new Hotkey(keyData, win));
    }
    // Used when the hook could not be installed.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0 || code is Keys.Enter or Keys.Escape && (keyData & Keys.Modifiers) == 0) return base.ProcessCmdKey(ref msg, keyData);
        if (hook != IntPtr.Zero || Hotkey.IsModifierKey(code)) return true;
        Take(keyData, GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0);
        return true;
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) != Keys.Tab || base.IsInputKey(keyData);
}
