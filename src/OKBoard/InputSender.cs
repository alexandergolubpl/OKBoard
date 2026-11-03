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

/// <summary>Finds the installed EN/RU layouts and reads/changes the layout of the focused window.</summary>
internal sealed class LayoutManager
{
    private readonly Dictionary<Lang, IntPtr> _hkl = new();
    private (Lang lang, long tick, IntPtr hwnd)? _pending;

    public LayoutManager() => Refresh();

    public void Refresh()
    {
        _hkl.Clear();
        int n = GetKeyboardLayoutList(0, null);
        var arr = new IntPtr[n];
        GetKeyboardLayoutList(n, arr);
        foreach (var h in arr)
        {
            var lang = (int)((long)h & 0xFFFF);
            var primary = lang & 0x3FF;
            if (primary == 0x09) _hkl.TryAdd(Lang.En, h);
            else if (primary == 0x19) _hkl.TryAdd(Lang.Ru, h);
        }
    }

    public bool HasBoth => _hkl.ContainsKey(Lang.En) && _hkl.ContainsKey(Lang.Ru);

    public static IntPtr FocusWindow(out uint threadId)
    {
        var fg = GetForegroundWindow();
        threadId = GetWindowThreadProcessId(fg, out _);
        var gti = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (threadId != 0 && GetGUIThreadInfo(threadId, ref gti) && gti.hwndFocus != IntPtr.Zero)
            return gti.hwndFocus;
        return fg;
    }

    /// <summary>Raw primary language of the focused window's layout (null if neither EN nor RU).</summary>
    public Lang? Current()
    {
        var hwnd = FocusWindow(out var tid);
        // A layout change we requested may not be applied yet; trust it for a short while.
        if (_pending is { } p && Environment.TickCount64 - p.tick < 400 && p.hwnd == hwnd) return p.lang;
        var hkl = GetKeyboardLayout(tid);
        var primary = (int)((long)hkl & 0xFFFF) & 0x3FF;
        return primary switch { 0x09 => Lang.En, 0x19 => Lang.Ru, _ => null };
    }

    public void Switch(Lang lang, IntPtr? target = null)
    {
        if (!_hkl.TryGetValue(lang, out var hkl)) return;
        var hwnd = target ?? FocusWindow(out _);
        PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        _pending = (lang, Environment.TickCount64, hwnd);
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
