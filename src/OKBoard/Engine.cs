using System.Runtime.InteropServices;
using static OKBoard.Native;

namespace OKBoard;

public sealed record CorrectionEntry(DateTime Time, string App, string From, string To, string Kind);

/// <summary>
/// Core of the switcher: watches keystrokes, keeps a buffer of the current line, auto-corrects words typed in
/// the wrong layout and executes hotkey actions. Everything runs on the UI thread (hooks are delivered there).
/// </summary>
internal sealed class Engine : IDisposable
{
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_CAPITAL = 0x14,
        VK_CANCEL = 0x03, VK_CONTROL_L = 0xA2, VK_C = 0x43, VK_V = 0x56, VK_INSERT = 0x2D;

    private readonly Settings _s;
    private readonly LanguageDetector _detector;
    private readonly LayoutManager _layouts;
    private readonly SynchronizationContext _ui;
    private readonly HashSet<string> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Hotkey, HotkeyAction> _hotkeys = new();

    private readonly List<KeyStroke> _line = [];
    private readonly HashSet<ushort> _held = [];
    private bool _caps, _capsKeyDown, _swallowCapsUp;
    private bool _shiftDown, _shiftAlone;
    private long _lastShiftTap;
    private Lang? _wordStartLang;
    private AutoFix? _lastAuto;
    private bool _busy;
    private bool _savePending;
    private readonly DelayedClipboard _clipboard = new();
    private const int MaxSelectionChars = 200_000, MaxReselectChars = 5_000;

    private long _fullscreenCheckedAt;
    private bool _fullscreenCached;
    private IntPtr _procCacheHwnd;
    private string? _procCacheName;

    private IntPtr _kbHook, _mouseHook, _fgHook;
    private int _lastHookTick = Environment.TickCount, _lastReinstallTick;
    private readonly LowLevelProc _kbProc, _mouseProc;
    private readonly WinEventProc _fgProc;

    public DateTime DndUntil { get; private set; } = DateTime.MinValue;
    public bool IsDnd => DateTime.Now < DndUntil;
    public bool IsFullscreenPaused => _s.PauseInFullscreen && IsFullscreen();
    /// <summary>Set while a settings hotkey box has focus, so hotkeys reach it.</summary>
    public bool Suspended { get; set; }

    public List<CorrectionEntry> Log { get; } = [];
    /// <summary>Diagnostics sink (errors, skipped operations).</summary>
    public static Action<string>? Trace;
    public event Action<string>? Notify;
    public event Action? StateChanged;

    private sealed record AutoFix(List<KeyStroke> OriginalKeys, int Count, Lang From, bool Switched, string Original, string Converted);

    public Engine(Settings settings, LanguageDetector detector, LayoutManager layouts)
    {
        _s = settings;
        _detector = detector;
        _layouts = layouts;
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _detector.IsException = w => _exceptions.Contains(w);
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _fgProc = ForegroundProc;
        _caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        ReloadSettings();
    }

    public void Start()
    {
        InstallInputHooks();
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (_kbHook == IntPtr.Zero) throw new InvalidOperationException("Не удалось установить перехват клавиатуры: " + Marshal.GetLastWin32Error());
    }

    private void InstallInputHooks()
    {
        var mod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    /// <summary>
    /// Windows silently removes a low-level hook if it ever exceeds LowLevelHooksTimeout. If the system saw
    /// input recently but our hooks did not, reinstall them. Called periodically from the UI timer.
    /// </summary>
    public void CheckHookHealth()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        int sinceInputSeen = (int)(info.dwTime - (uint)_lastHookTick);
        if (sinceInputSeen < 3000 || Environment.TickCount - _lastReinstallTick < 30000) return;
        _lastReinstallTick = Environment.TickCount;
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        InstallInputHooks();
        _lastHookTick = Environment.TickCount;
        ResetLine();
        Trace?.Invoke("hooks reinstalled");
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        _kbHook = _mouseHook = _fgHook = IntPtr.Zero;
        _clipboard.Dispose();
    }

    public void ReloadSettings()
    {
        _exceptions.Clear();
        foreach (var w in _s.ExceptionWords) _exceptions.Add(w.Trim());
        _hotkeys.Clear();
        foreach (var (action, text) in _s.Hotkeys)
            if (Hotkey.Parse(text) is { IsSafe: true } hk) _hotkeys.TryAdd(hk, action);
    }

    // ───────────────────────────── public commands (tray menu etc.) ─────────────────────────────

    public void SetDnd(TimeSpan? duration)
    {
        DndUntil = duration switch
        {
            null => DateTime.MinValue,
            { } d when d == TimeSpan.MaxValue => DateTime.MaxValue,
            { } d => DateTime.Now + d,
        };
        ResetLine();
        StateChanged?.Invoke();
    }

    public void ToggleDnd()
    {
        SetDnd(IsDnd ? null : TimeSpan.MaxValue);
        Notify?.Invoke(IsDnd ? "Не беспокоить: автопереключение приостановлено" : "Автопереключение снова работает");
    }

    public void AddException(string word)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0 || !_exceptions.Add(word)) return;
        _s.ExceptionWords.Add(word);
        SaveSoon();
    }

    /// <summary>
    /// Save settings outside of the hook callback: disk I/O inside a low-level hook can exceed
    /// LowLevelHooksTimeout, after which Windows silently removes the hook.
    /// </summary>
    private void SaveSoon()
    {
        if (_savePending) return;
        _savePending = true;
        _ui.Post(_ =>
        {
            _savePending = false;
            try { _s.Save(); } catch (Exception ex) { Trace?.Invoke("save failed: " + ex.Message); }
        }, null);
    }

    public Lang? CurrentLang => _layouts.Current();

    // ───────────────────────────── hooks ─────────────────────────────

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            _lastHookTick = Environment.TickCount;
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (k.dwExtraInfo == InjectMarker)
            {
                if (k.vkCode == VK_CAPITAL && down) _caps = !_caps;
            }
            else
            {
                try
                {
                    if (HandleKey((ushort)k.vkCode, down)) return 1;
                }
                catch (Exception ex)
                {
                    Trace?.Invoke(ex.ToString());
                    ResetLine();
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _lastHookTick = Environment.TickCount;
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            ResetLine();
            _shiftAlone = false;
            _lastShiftTap = 0;
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void ForegroundProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        ResetLine();
        var name = GetProcessName(hwnd);
        if (name != null && _s.AppLayouts.TryGetValue(name, out var l))
        {
            var lang = l.Trim().ToLowerInvariant() == "ru" ? Lang.Ru : Lang.En;
            // Give the window a moment to finish activating before changing its layout.
            Task.Delay(120).ContinueWith(_ => _ui.Post(_ => _layouts.Switch(lang, hwnd), null));
        }
        StateChanged?.Invoke();
    }

    private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
    private bool ShiftHeld => _held.Contains(0xA0) || _held.Contains(0xA1) || _held.Contains(0x10);
    private bool CtrlHeld => _held.Contains(0xA2) || _held.Contains(0xA3) || _held.Contains(0x11);
    private bool AltHeld => _held.Contains(0xA4) || _held.Contains(0xA5) || _held.Contains(0x12);
    private bool WinHeld => _held.Contains(0x5B) || _held.Contains(0x5C);

    /// <returns>true to swallow the key.</returns>
    private bool HandleKey(ushort vk, bool down)
    {
        if (IsModifier(vk))
        {
            if (down) _held.Add(vk); else _held.Remove(vk);
            HandleShiftTap(vk, down);
            return false;
        }

        if (!down)
        {
            if (vk == VK_CAPITAL)
            {
                _capsKeyDown = false;
                if (_swallowCapsUp) { _swallowCapsUp = false; return true; }
            }
            return false;
        }

        _shiftAlone = false;
        _lastShiftTap = 0;

        if (vk == VK_CAPITAL)
        {
            bool first = !_capsKeyDown;
            _capsKeyDown = true;
            if (_s.CapsLockSwitchesLayout && !ShiftHeld && !CtrlHeld && !AltHeld && !Suspended)
            {
                if (first) ToggleLayout();
                _swallowCapsUp = true;
                return true;
            }
            if (first) _caps = !_caps;
            return false;
        }

        if (Suspended) return false;
        if (IsFullscreenPaused) { ResetLine(); return false; }

        // Hotkeys
        var key = vk == VK_CANCEL ? Keys.Pause : (Keys)vk; // Ctrl+Pause arrives as VK_CANCEL
        if (_hotkeys.TryGetValue(new Hotkey(key, CtrlHeld, AltHeld, ShiftHeld, WinHeld), out var action))
        {
            Trace?.Invoke($"hotkey {action}");
            // We swallow the key, so the app would see a lone Alt/Win press+release and open its menu / Start.
            if (AltHeld || WinHeld) InputSender.Tap(0xE8);
            var fg = GetForegroundWindow();
            _ui.Post(_ => Run(action, fg), null);
            return true;
        }

        if (CtrlHeld || AltHeld || WinHeld)
        {
            ResetLine(); // a shortcut (Ctrl+V, Alt+Tab...) — we no longer know what is around the caret
            return false;
        }

        if (vk == VK_BACK)
        {
            if (_lastAuto != null && _s.UndoWithBackspace)
            {
                UndoAuto();
                return true;
            }
            _lastAuto = null;
            if (_line.Count > 0) _line.RemoveAt(_line.Count - 1);
            return false;
        }

        if (vk is VK_SPACE or VK_RETURN or VK_TAB)
            return OnBoundary(vk);

        if (LayoutMap.IsCharKey(vk))
        {
            _lastAuto = null;
            if (WordRange().count == 0) _wordStartLang = _layouts.Current();
            _line.Add(new KeyStroke(vk, ShiftHeld, _caps));
            if (_line.Count > 400) _line.RemoveRange(0, 200);
            return false;
        }

        ResetLine(); // arrows, Home/End, Delete, Esc, F-keys, numpad...
        return false;
    }

    private void HandleShiftTap(ushort vk, bool down)
    {
        if (vk is not (0xA0 or 0xA1 or 0x10))
        {
            if (down) { _shiftAlone = false; _lastShiftTap = 0; }
            return;
        }
        if (down)
        {
            if (!_shiftDown) { _shiftDown = true; _shiftAlone = !CtrlHeld && !AltHeld && !WinHeld; }
            return;
        }
        _shiftDown = false;
        if (!_shiftAlone || !_s.DoubleShiftConverts || Suspended || IsFullscreenPaused) { _shiftAlone = false; return; }
        _shiftAlone = false;
        var now = Environment.TickCount64;
        if (_lastShiftTap != 0 && now - _lastShiftTap < 400)
        {
            _lastShiftTap = 0;
            var fg = GetForegroundWindow();
            _ui.Post(_ => Run(HotkeyAction.ConvertLastWord, fg), null);
        }
        else _lastShiftTap = now;
    }

    private void ResetLine()
    {
        _line.Clear();
        _lastAuto = null;
        _wordStartLang = null;
    }

    // ───────────────────────────── buffer helpers ─────────────────────────────

    /// <summary>Range of the word right before the caret (excluding trailing spaces).</summary>
    private (int start, int count) WordRange()
    {
        int i = _line.Count;
        while (i > 0 && !_line[i - 1].IsSpace) i--;
        return (i, _line.Count - i);
    }

    /// <summary>The last word together with any spaces typed after it.</summary>
    private (int start, int count) LastWordWithSpaces()
    {
        int i = _line.Count;
        while (i > 0 && _line[i - 1].IsSpace) i--;
        int wordEnd = i;
        while (i > 0 && !_line[i - 1].IsSpace) i--;
        return wordEnd == i ? (0, 0) : (i, _line.Count - i);
    }

    // ───────────────────────────── auto-correction ─────────────────────────────

    private string? ForegroundProcess()
    {
        var fg = GetForegroundWindow();
        if (fg != _procCacheHwnd) { _procCacheHwnd = fg; _procCacheName = GetProcessName(fg); }
        return _procCacheName;
    }

    public bool IsExcludedApp(string? proc) =>
        proc != null && _s.ExcludedApps.Any(a => a.Trim().Equals(proc, StringComparison.OrdinalIgnoreCase));

    private bool AutoAllowed() =>
        _s.AutoSwitch && !IsDnd && !IsExcludedApp(ForegroundProcess()) &&
        !(_s.SkipPasswordFields && LayoutManager.IsPasswordField());

    private bool IsFullscreen()
    {
        var now = Environment.TickCount64;
        if (now - _fullscreenCheckedAt > 1500)
        {
            _fullscreenCheckedAt = now;
            // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
            _fullscreenCached = SHQueryUserNotificationState(out var st) == 0 && st is 2 or 3 or 4;
        }
        return _fullscreenCached;
    }

    private bool OnBoundary(ushort vk)
    {
        _lastAuto = null;
        var (start, count) = WordRange();
        bool swallow = false;
        // A single token followed by Enter/Tab is how passwords and logins are typed (browsers don't expose
        // password fields to us). Never rewrite it — the user can still press the hotkey manually.
        bool singleTokenSubmit = vk != VK_SPACE && _s.SkipSingleWordBeforeEnter && start == 0;
        if (count > 0 && !singleTokenSubmit && AutoAllowed() && _layouts.Current() is { } cur && (_wordStartLang == null || _wordStartLang == cur))
        {
            var word = _line.GetRange(start, count);
            var d = _detector.Analyze(word, cur);
            var newKeys = word;
            var renderLang = cur;
            var kinds = new List<string>();
            if (d.Convert)
            {
                renderLang = LayoutMap.Other(cur);
                kinds.Add("раскладка");
            }
            var text = LayoutMap.Render(newKeys, renderLang);
            bool capsOff = false;
            if (_s.FixCapsLockAccident && _caps && CaseTools.LooksLikeCapsAccident(text))
            {
                newKeys = newKeys.Select(k => LayoutMap.WithCase(k, !char.IsUpper(LayoutMap.CharFor(k, renderLang) ?? 'a'))).ToList();
                capsOff = true;
                kinds.Add("Caps Lock");
            }
            else if (_s.FixDoubleCapital && CaseTools.LooksLikeDoubleCapital(text) && !_detector.IsValid(text, renderLang))
            {
                newKeys = [.. newKeys];
                newKeys[1] = LayoutMap.WithCase(newKeys[1], false);
                kinds.Add("ДВе заглавные");
            }
            var newText = LayoutMap.Render(newKeys, renderLang);
            if (newText != d.Current)
            {
                InputSender.ReleaseModifiers(_held.Where(v => v is 0xA0 or 0xA1));
                InputSender.Backspace(count);
                InputSender.Text(newText);
                if (renderLang != cur) _layouts.Switch(renderLang);
                if (capsOff) InputSender.Tap(VK_CAPITAL);
                InputSender.Tap(vk);
                swallow = true;

                _line.RemoveRange(start, count);
                _line.InsertRange(start, newKeys);
                if (vk == VK_SPACE)
                    _lastAuto = new AutoFix(word, count, cur, renderLang != cur, d.Current, newText);
                AddLog(d.Current, newText, string.Join(", ", kinds));
                if (_s.SoundOnAutoSwitch) Task.Run(() => Console.Beep(1400, 25));
            }
        }
        _wordStartLang = null;
        if (vk == VK_SPACE) _line.Add(new KeyStroke(VK_SPACE, false));
        else { var keep = _lastAuto; ResetLine(); _lastAuto = keep; }
        return swallow;
    }

    private void UndoAuto()
    {
        var a = _lastAuto!;
        _lastAuto = null;
        InputSender.Backspace(a.Count + 1);
        InputSender.Text(a.Original + " ");
        if (a.Switched) _layouts.Switch(a.From);
        int start = _line.Count - 1 - a.Count;
        if (start >= 0)
        {
            _line.RemoveRange(start, a.Count);
            _line.InsertRange(start, a.OriginalKeys);
        }
        if (a.Switched && _s.LearnFromUndo)
        {
            AddException(LanguageDetector.Core(a.Original, a.From));
            AddLog(a.Converted, a.Original, "отмена → в исключения");
        }
        else AddLog(a.Converted, a.Original, "отмена");
    }

    private void AddLog(string from, string to, string kind)
    {
        Log.Insert(0, new CorrectionEntry(DateTime.Now, ForegroundProcess() ?? "?", from, to, kind));
        if (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }

    // ───────────────────────────── hotkey actions ─────────────────────────────

    public void ToggleLayout()
    {
        var cur = _layouts.Current() ?? Lang.Ru;
        _layouts.Switch(LayoutMap.Other(cur));
        StateChanged?.Invoke();
    }

    private async void Run(HotkeyAction action, IntPtr fg)
    {
        if (_busy) return;
        bool editing = action is not (HotkeyAction.ToggleDoNotDisturb or HotkeyAction.AddException);
        // The user switched windows between the key press and now: don't type into the wrong window.
        if (editing && GetForegroundWindow() != fg) return;
        try
        {
            switch (action)
            {
                case HotkeyAction.ConvertLastWord:
                    if (!RetypeLast(phrase: false)) await TransformSelection(s => LayoutMap.ConvertText(s), true, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ConvertPhrase:
                    if (!RetypeLast(phrase: true)) await TransformSelection(s => LayoutMap.ConvertText(s), true, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ConvertSelection:
                    await TransformSelection(s => LayoutMap.ConvertText(s), true, fg, explicitSelection: true);
                    break;
                case HotkeyAction.CycleCase:
                    if (!TransformLastWord(CaseTools.Cycle)) await TransformSelection(CaseTools.Cycle, false, fg, explicitSelection: false);
                    break;
                case HotkeyAction.Transliterate:
                    if (!TransformLastWord(Transliterator.Transliterate)) await TransformSelection(Transliterator.Transliterate, false, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ToggleDoNotDisturb:
                    ToggleDnd();
                    break;
                case HotkeyAction.AddException:
                    AddLastWordToExceptions();
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke(ex.ToString());
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    private void AddLastWordToExceptions()
    {
        var (start, count) = LastWordWithSpaces();
        if (count == 0) { Notify?.Invoke("Нет последнего слова для исключения"); return; }
        var keys = _line.GetRange(start, count).Where(k => !k.IsSpace).ToList();
        var cur = _layouts.Current() ?? Lang.En;
        var w1 = LanguageDetector.Core(LayoutMap.Render(keys, cur), cur);
        var w2 = LanguageDetector.Core(LayoutMap.Render(keys, LayoutMap.Other(cur)), LayoutMap.Other(cur));
        AddException(w1);
        AddException(w2);
        Notify?.Invoke($"«{w1}» / «{w2}» больше не будут исправляться");
    }

    /// <summary>Re-type the last word (or whole line) in the other layout. False if the buffer is empty.</summary>
    private bool RetypeLast(bool phrase)
    {
        var (start, count) = phrase ? (0, _line.Count) : LastWordWithSpaces();
        if (count == 0 || _line.Skip(start).All(k => k.IsSpace)) return false;
        var keys = _line.GetRange(start, count);
        var cur = _layouts.Current() ?? Lang.En;
        var target = LayoutMap.Other(cur);
        var before = LayoutMap.Render(keys, cur);
        var after = LayoutMap.Render(keys, target);

        // Converting back a word we auto-corrected a moment ago means the detector was wrong: learn from it.
        if (!phrase && _lastAuto is { Switched: true } a && _s.LearnFromUndo)
            AddException(LanguageDetector.Core(a.Original, a.From));
        _lastAuto = null;

        InputSender.ReleaseModifiers(_held);
        InputSender.Backspace(count);
        InputSender.Text(after);
        _layouts.Switch(target);
        AddLog(before, after, phrase ? "фраза (вручную)" : "слово (вручную)");
        return true;
    }

    /// <summary>Apply a text transformation to the last word (case, transliteration). False if buffer is empty.</summary>
    private bool TransformLastWord(Func<string, string> f)
    {
        var (start, count) = LastWordWithSpaces();
        if (count == 0) return false;
        var keys = _line.GetRange(start, count);
        var cur = _layouts.Current() ?? Lang.En;
        var text = LayoutMap.Render(keys, cur);
        var trimmed = text.TrimEnd(' ');
        var result = f(trimmed) + text[trimmed.Length..];
        if (result == text) return true;

        InputSender.ReleaseModifiers(_held);
        InputSender.Backspace(count);
        InputSender.Text(result);

        if (result.Length == text.Length)
        {
            // Same length (case change): keep the buffer in sync so repeated presses keep cycling.
            for (int i = 0; i < count; i++)
                if (char.IsLetter(result[i])) _line[start + i] = LayoutMap.WithCase(_line[start + i], char.IsUpper(result[i]));
        }
        else
        {
            var keep = _line.GetRange(0, start);
            ResetLine();
            _line.AddRange(keep);
        }
        return true;
    }

    // ───────────────────────────── selection (clipboard) ─────────────────────────────

    private static readonly string[] TerminalClasses =
        ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "VirtualConsoleClass"];

    private static bool IsTerminal(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return TerminalClasses.Any(c => cls.Equals(c, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="explicitSelection">
    /// False when we only fell back to the selection because nothing was typed. Then copied text ending with a
    /// newline most likely means "no selection — the editor copied the whole line" (VS Code, JetBrains...).
    /// </param>
    private async Task TransformSelection(Func<string, string> f, bool switchLayout, IntPtr fg, bool explicitSelection)
    {
        // In a terminal Ctrl+C is "interrupt" (kills the running program) and pasting text with a newline
        // executes it. Never synthesize clipboard shortcuts there.
        if (IsTerminal(fg))
        {
            Notify?.Invoke("В терминале работают только действия с только что набранным текстом");
            return;
        }

        _busy = true;
        try
        {
            InputSender.ReleaseModifiers(_held);
            var backup = ClipboardBackup.Save();
            uint seq = GetClipboardSequenceNumber();
            // Ctrl+Insert first: copies in almost every app, and is never an "interrupt".
            InputSender.Combo(VK_CONTROL_L, VK_INSERT, extended: true);
            for (int i = 0; i < 15 && GetClipboardSequenceNumber() == seq; i++) await Task.Delay(20);
            if (GetClipboardSequenceNumber() == seq && GetForegroundWindow() == fg)
            {
                InputSender.Combo(VK_CONTROL_L, VK_C);
                for (int i = 0; i < 15 && GetClipboardSequenceNumber() == seq; i++) await Task.Delay(20);
            }
            if (GetClipboardSequenceNumber() == seq) return; // nothing selected, clipboard untouched

            string text = "";
            for (int i = 0; i < 5; i++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; break; }
                catch (ExternalException) { await Task.Delay(30); }
            }
            if (text.Length > MaxSelectionChars) Notify?.Invoke("Слишком большое выделение");
            bool abort = text.Length == 0
                         || text.Length > MaxSelectionChars
                         || (!explicitSelection && text.EndsWith('\n'))
                         || GetForegroundWindow() != fg;
            if (abort) { backup.Restore(); return; }

            var result = f(text);
            var requested = _clipboard.Offer(result);
            if (requested == null) { backup.Restore(); return; }
            if (GetForegroundWindow() != fg) { if (_clipboard.IsOwner) backup.Restore(); return; }
            InputSender.Combo(VK_CONTROL_L, VK_V);

            // Wait until the app has actually read our text (or give up); only then restore the user's clipboard.
            bool pasted = await Task.WhenAny(requested, Task.Delay(3000)) == requested;
            await Task.Delay(60);
            if (pasted && GetForegroundWindow() == fg)
            {
                int len = result.Replace("\r\n", "\n").Length;
                if (_s.ReselectAfterConvert && len <= MaxReselectChars) InputSender.SelectLeft(len);
                if (switchLayout && LayoutMap.DetectScript(result) is { } target) _layouts.Switch(target);
                AddLog(Shorten(text), Shorten(result), "выделение");
            }
            if (_clipboard.IsOwner) backup.Restore(); // don't clobber something the user copied meanwhile
        }
        finally
        {
            _busy = false;
            ResetLine();
        }
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
