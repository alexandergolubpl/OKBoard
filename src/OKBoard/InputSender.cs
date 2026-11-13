using static OKBoard.Native;

namespace OKBoard;

/// <summary>Synthesizes keyboard input. Every event is tagged so our own hook ignores it.</summary>
internal static class InputSender
{
    private static INPUT Key(ushort vk, bool up, ushort scan = 0, uint extra = 0) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk, wScan = scan,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | extra,
                dwExtraInfo = InjectMarker,
            },
        },
    };

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public static void Tap(ushort vk, int times = 1)
    {
        var list = new List<INPUT>(times * 2);
        bool ext = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2E or 0x24 or 0x23; // arrows, Del, Home, End
        for (int i = 0; i < times; i++)
        {
            list.Add(Key(vk, false, 0, ext ? KEYEVENTF_EXTENDEDKEY : 0));
            list.Add(Key(vk, true, 0, ext ? KEYEVENTF_EXTENDEDKEY : 0));
        }
        Send(list);
    }

    public static void Backspace(int times) { if (times > 0) Tap(0x08, times); }

    /// <summary>Types arbitrary text as Unicode, independent of the active layout. Newlines become Enter.</summary>
    public static void Text(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') continue;
            if (c == '\n')
            {
                list.Add(Key(0x0D, false));
                list.Add(Key(0x0D, true));
                continue;
            }
            list.Add(Key(0, false, c, KEYEVENTF_UNICODE));
            list.Add(Key(0, true, c, KEYEVENTF_UNICODE));
        }
        Send(list);
    }

    /// <summary>Press a combination like Ctrl+C.</summary>
    public static void Combo(ushort modifier, ushort vk, bool extended = false)
    {
        uint ext = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        Send([Key(modifier, false), Key(vk, false, 0, ext), Key(vk, true, 0, ext), Key(modifier, true)]);
    }

    /// <summary>Shift+Left N times (re-select text that was just pasted).</summary>
    public static void SelectLeft(int count)
    {
        if (count <= 0) return;
        var list = new List<INPUT> { Key(0xA0, false) };
        for (int i = 0; i < count; i++)
        {
            list.Add(Key(0x25, false, 0, KEYEVENTF_EXTENDEDKEY));
            list.Add(Key(0x25, true, 0, KEYEVENTF_EXTENDEDKEY));
        }
        list.Add(Key(0xA0, true));
        Send(list);
    }

    /// <summary>
    /// Release modifiers the user is physically holding (they would otherwise turn Backspace into Ctrl+Backspace,
    /// Ctrl+C into Ctrl+Shift+C, etc.). A dummy key is sent before releasing Alt/Win so the app doesn't open its menu.
    /// </summary>
    public static void ReleaseModifiers(IEnumerable<ushort> held)
    {
        var list = new List<INPUT>();
        var keys = held.ToList();
        if (keys.Any(k => k is 0xA4 or 0xA5 or 0x5B or 0x5C))
        {
            list.Add(Key(0xE8, false));
            list.Add(Key(0xE8, true));
        }
        foreach (var vk in keys)
            list.Add(Key(vk, true, 0, vk is 0xA3 or 0xA5 or 0x5B or 0x5C ? KEYEVENTF_EXTENDEDKEY : 0));
        Send(list);
    }
}

/// <summary>Finds the installed EN/RU/UK layouts and reads/changes the layout of the focused window.</summary>
internal sealed class LayoutManager
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, System.Text.StringBuilder buf, int size, uint flags, IntPtr hkl);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);

    private readonly Dictionary<Lang, IntPtr> _hkl = new();
    private (Lang lang, long tick, IntPtr hwnd)? _pending;
    /// <summary>When OKBoard itself last changed a layout (to tell it apart from the user doing it).</summary>
    public long LastOwnSwitchTick { get; private set; }

    /// <summary>Supported layouts installed in Windows, in the order of the system language list.</summary>
    public List<Lang> Installed { get; } = [];

    public LayoutManager() => Refresh();

    public static Lang? LangOf(IntPtr hkl) => ((int)((long)hkl & 0xFFFF) & 0x3FF) switch
    {
        0x09 => Lang.En,
        0x19 => Lang.Ru,
        0x22 => Lang.Uk,
        _ => null,
    };

    public void Refresh()
    {
        _hkl.Clear();
        Installed.Clear();
        int n = GetKeyboardLayoutList(0, null);
        var arr = new IntPtr[n];
        GetKeyboardLayoutList(n, arr);
        foreach (var h in arr)
            if (LangOf(h) is { } l && _hkl.TryAdd(l, h))
            {
                Installed.Add(l);
                LoadKeyMap(l, h);
            }
    }

    /// <summary>Read what the installed layout really types for every key we know (layout variants differ).</summary>
    private static void LoadKeyMap(Lang l, IntPtr hkl)
    {
        var state = new byte[256];
        var buf = new System.Text.StringBuilder(8);
        foreach (var vk in LayoutMap.Keys.ToList())
        {
            char? Get(bool shift)
            {
                Array.Clear(state);
                if (shift) state[0x10] = 0x80;
                buf.Clear();
                // flag 4: don't touch the keyboard state (dead keys) of this thread
                int r = ToUnicodeEx(vk, MapVirtualKeyEx(vk, 0, hkl), state, buf, buf.Capacity, 4, hkl);
                return r == 1 && !char.IsControl(buf[0]) ? buf[0] : null;
            }
            if (Get(false) is { } normal && Get(true) is { } shifted) LayoutMap.Override(l, vk, normal, shifted);
        }
    }

    public bool Has(Lang l) => _hkl.ContainsKey(l);
    public bool HasLatinAndCyrillic => Has(Lang.En) && (Has(Lang.Ru) || Has(Lang.Uk));

    public static IntPtr FocusWindow(out uint threadId)
    {
        var fg = GetForegroundWindow();
        threadId = GetWindowThreadProcessId(fg, out _);
        var gti = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (threadId != 0 && GetGUIThreadInfo(threadId, ref gti) && gti.hwndFocus != IntPtr.Zero)
            return gti.hwndFocus;
        return fg;
    }

    /// <summary>Layout of the focused window (null if it isn't one we support).</summary>
    public Lang? Current()
    {
        var hwnd = FocusWindow(out var tid);
        // A layout change we requested may not be applied yet; trust it for a short while.
        if (_pending is { } p && Environment.TickCount64 - p.tick < 400 && p.hwnd == hwnd) return p.lang;
        return LangOf(GetKeyboardLayout(tid));
    }

    public void Switch(Lang lang, IntPtr? target = null)
    {
        if (!_hkl.TryGetValue(lang, out var hkl)) return;
        var hwnd = target ?? FocusWindow(out _);
        PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        _pending = (lang, Environment.TickCount64, hwnd);
        LastOwnSwitchTick = Environment.TickCount64;
    }

    public static bool IsPasswordField()
    {
        var hwnd = FocusWindow(out _);
        if (hwnd == IntPtr.Zero) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        if (!cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return false;
        return ((long)GetWindowLongPtr(hwnd, GWL_STYLE) & ES_PASSWORD) != 0;
    }
}
