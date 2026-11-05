using System.Runtime.InteropServices;
using static OKBoard.Native;

namespace OKBoard;

public sealed record CorrectionEntry(DateTime Time, string App, string From, string To, string Kind);

/// <summary>
/// Core of the switcher: watches keystrokes, keeps a buffer of the current line, auto-corrects words typed in
/// the wrong layout, expands abbreviations and executes hotkey actions. Everything runs on the UI thread
/// (low-level hooks are delivered there).
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
    private readonly Dictionary<string, Snippet> _snippets = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<KeyStroke> _line = [];
    private readonly HashSet<ushort> _held = [];
    private bool _capsKeyDown, _swallowCapsUp;
    private bool _shiftDown, _shiftAlone;
    private long _lastShiftTap;
    private Lang? _wordStartLang;
    private Lang? _lastCyrillic;
    private AutoFix? _lastAuto;
    private ManualCycle? _cycle;
    private bool _busy;
    private bool _savePending;
    private readonly DelayedClipboard _clipboard = new();
    private const int MaxSelectionChars = 200_000, MaxReselectChars = 5_000, MaxTypedSnippet = 300;

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
    /// <summary>Set while one of our own input windows has focus (hotkey box, clipboard popup).</summary>
    public bool Suspended { get; set; }
    /// <summary>True while we are copying/pasting on the user's behalf (clipboard history ignores those copies).</summary>
    public bool Busy => _busy;

    public List<CorrectionEntry> Log { get; } = [];
    /// <summary>Diagnostics sink (errors, skipped operations). Never receives typed or copied text.</summary>
    public static Action<string>? Trace;
    public event Action<string>? Notify;
    public event Action? StateChanged;
    /// <summary>The clipboard history hotkey was pressed; argument is the window to paste into.</summary>
    public event Action<IntPtr>? ClipboardHistoryRequested;

    /// <param name="ConvertedLength">Characters to delete to undo (newlines count as one).</param>
    private sealed record AutoFix(List<KeyStroke> OriginalKeys, int ConvertedLength, Lang From, bool Switched,
        string Original, string Converted, bool IsSnippet);

    /// <summary>State of repeated Pause presses on the same word: cycle through the installed layouts.</summary>
    private sealed record ManualCycle(int Start, int Count, List<Lang> Sequence, int Index);

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
        _snippets.Clear();
        foreach (var sn in _s.Snippets)
            if (!string.IsNullOrWhiteSpace(sn.Abbr) && !string.IsNullOrEmpty(sn.Text)) _snippets.TryAdd(sn.Abbr.Trim(), sn);
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

    /// <summary>The Cyrillic layout to use when converting Latin text: the one used last, else the first installed.</summary>
    private Lang PreferredCyrillic() =>
        _lastCyrillic is { } l && _layouts.Has(l) ? l : _layouts.Installed.FirstOrDefault(LayoutMap.IsCyrillicLang, Lang.Ru);

    // ───────────────────────────── hooks ─────────────────────────────

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            _lastHookTick = Environment.TickCount;
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (k.dwExtraInfo != InjectMarker)
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
        if (name != null && _s.AppLayouts.TryGetValue(name, out var l) && Settings.ParseLang(l) is { } lang)
        {
            // Give the window a moment to finish activating before changing its layout.
            Task.Delay(120).ContinueWith(_ => _ui.Post(_ => _layouts.Switch(lang, hwnd), null));
        }
        StateChanged?.Invoke();
    }

    private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
    /// <summary>
    /// Real Caps Lock state. Counting CapsLock presses drifts: "Shift turns off Caps Lock" (Windows setting),
    /// other keyboard tools, the lock screen and dropped hook events all change it without us seeing a press.
    /// GetKeyState inside the low-level hook reflects the system toggle state (verified).
    /// </summary>
    private static bool CapsOn => (GetKeyState(VK_CAPITAL) & 1) != 0;

    /// <summary>
    /// Drop modifiers we think are held but the system says are up — a missed key-up (Win+Shift+S, lock screen,
    /// UAC prompt, our own injected release) would otherwise make every following letter look shifted, or make
    /// every key look like a Ctrl-shortcut and silently stop auto-correction.
    /// </summary>
    private void SyncModifiers()
    {
        if (_held.Count == 0) return;
        _held.RemoveWhere(vk => (GetAsyncKeyState(vk) & 0x8000) == 0);
    }

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
        SyncModifiers();

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

        _cycle = null; // any other key ends a Pause-cycle

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
            if (WordRange().count == 0)
            {
                _wordStartLang = _layouts.Current();
                if (_wordStartLang is { } l && LayoutMap.IsCyrillicLang(l)) _lastCyrillic = l;
            }
            _line.Add(new KeyStroke(vk, ShiftHeld, CapsOn));
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
        _cycle = null;
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

    private static int VisibleLength(string s) => s.Replace("\r\n", "\n").Length;

    // ───────────────────────────── auto-correction ─────────────────────────────

    private string? ForegroundProcess()
    {
        var fg = GetForegroundWindow();
        if (fg != _procCacheHwnd) { _procCacheHwnd = fg; _procCacheName = GetProcessName(fg); }
        return _procCacheName;
    }

    public bool IsExcludedApp(string? proc) =>
        proc != null && _s.ExcludedApps.Any(a => a.Trim().Equals(proc, StringComparison.OrdinalIgnoreCase));

    private bool InPasswordField() => _s.SkipPasswordFields && LayoutManager.IsPasswordField();

    private bool AutoAllowed() =>
        _s.AutoSwitch && !IsDnd && !IsExcludedApp(ForegroundProcess()) && !InPasswordField();

    private bool SnippetsAllowed() => _s.SnippetsEnabled && _snippets.Count > 0 && !IsDnd && !InPasswordField();

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
        if (count > 0 && SnippetsAllowed() && TryExpandSnippet(start, count, vk)) return true;

        bool swallow = false;
        // A single token followed by Enter/Tab is how passwords and logins are typed (browsers don't expose
        // password fields to us). Never rewrite it — the user can still press the hotkey manually.
        bool singleTokenSubmit = vk != VK_SPACE && _s.SkipSingleWordBeforeEnter && start == 0;
        if (count > 0 && !singleTokenSubmit && AutoAllowed() && _layouts.Current() is { } cur && (_wordStartLang == null || _wordStartLang == cur))
        {
            var word = _line.GetRange(start, count);
            var d = _detector.AnalyzeBest(word, cur, _layouts.Installed, PreferredCyrillic());
            var newKeys = word;
            var renderLang = cur;
            var kinds = new List<string>();
            if (d.Convert)
            {
                renderLang = d.Target;
                kinds.Add("раскладка");
            }
            var text = LayoutMap.Render(newKeys, renderLang);
            bool capsOff = false;
            if (_s.FixCapsLockAccident && CapsOn && word.All(k => k.Caps) && CaseTools.LooksLikeCapsAccident(text))
            {
                // flip the case of letters only — punctuation keys must stay as they are
                newKeys = newKeys.Select(k => LayoutMap.CharFor(k, renderLang) is { } c && char.IsLetter(c)
                    ? LayoutMap.WithCase(k, !char.IsUpper(c)) : k).ToList();
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
            var oldText = LayoutMap.Render(word, cur);
            if (newText != oldText)
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
                    _lastAuto = new AutoFix(word, count, cur, renderLang != cur, oldText, newText, false);
                AddLog(oldText, newText, string.Join(", ", kinds));
                if (_s.SoundOnAutoSwitch) Task.Run(() => Console.Beep(1400, 25));
            }
        }
        _wordStartLang = null;
        if (vk == VK_SPACE) _line.Add(new KeyStroke(VK_SPACE, false));
        else { var keep = _lastAuto; ResetLine(); _lastAuto = keep; }
        return swallow;
    }

    /// <summary>Abbreviation typed in any installed layout (so "мк" works even if typed as "vr").</summary>
    private (Snippet sn, string typed)? FindSnippet(List<KeyStroke> keys)
    {
        var cur = _layouts.Current();
        var order = cur is { } c ? _layouts.Installed.OrderBy(l => l == c ? 0 : 1) : _layouts.Installed.AsEnumerable();
        foreach (var l in order)
        {
            var typed = LayoutMap.Render(keys, l);
            if (_snippets.TryGetValue(typed, out var sn)) return (sn, LayoutMap.Render(keys, cur ?? l));
        }
        return null;
    }

    private bool TryExpandSnippet(int start, int count, ushort vk)
    {
        var word = _line.GetRange(start, count);
        if (FindSnippet(word) is not { } found) return false;
        var (sn, typed) = found;
        var text = Snippets.Expand(sn.Text);
        bool needsPaste = text.Contains('\n') || text.Length > MaxTypedSnippet;
        var fg = GetForegroundWindow();
        if (needsPaste && IsTerminal(fg)) return false; // a newline pasted into a shell would execute it

        InputSender.ReleaseModifiers(_held.Where(v => v is 0xA0 or 0xA1));
        InputSender.Backspace(count);
        if (needsPaste)
        {
            _ui.Post(async _ =>
            {
                try
                {
                    _busy = true;
                    if (!await PasteViaClipboard(text, fg))
                    {
                        // Couldn't paste (clipboard locked / window changed): put the abbreviation back.
                        if (GetForegroundWindow() == fg) InputSender.Text(typed);
                        Notify?.Invoke("Не удалось вставить текст автозамены");
                        return;
                    }
                    InputSender.Tap(vk);
                }
                finally { _busy = false; }
            }, null);
        }
        else
        {
            InputSender.Text(text);
            InputSender.Tap(vk);
        }

        ResetLine();
        if (vk == VK_SPACE)
            _lastAuto = new AutoFix(word, VisibleLength(text), _layouts.Current() ?? Lang.En, false, typed, text, true);
        AddLog(typed, Shorten(text), "автозамена");
        return true;
    }

    private void UndoAuto()
    {
        var a = _lastAuto!;
        _lastAuto = null;
        InputSender.Backspace(a.ConvertedLength + 1);
        InputSender.Text(a.Original + " ");
        if (a.Switched) _layouts.Switch(a.From);
        if (a.IsSnippet)
        {
            ResetLine();
            _line.AddRange(a.OriginalKeys);
            _line.Add(new KeyStroke(VK_SPACE, false));
            AddLog(Shorten(a.Converted), a.Original, "отмена автозамены");
            return;
        }
        int start = _line.Count - 1 - a.OriginalKeys.Count;
        if (start >= 0)
        {
            _line.RemoveRange(start, a.OriginalKeys.Count);
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
        var installed = _layouts.Installed;
        if (installed.Count == 0) return;
        var cur = _layouts.Current();
        int i = cur is { } c ? installed.IndexOf(c) : -1;
        _layouts.Switch(installed[(i + 1) % installed.Count]);
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
                    if (!RetypeLast(phrase: false)) await TransformSelection(ConvertSelectionText, true, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ConvertPhrase:
                    if (!RetypeLast(phrase: true)) await TransformSelection(ConvertSelectionText, true, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ConvertSelection:
                    await TransformSelection(ConvertSelectionText, true, fg, explicitSelection: true);
                    break;
                case HotkeyAction.CycleCase:
                    if (!TransformLastWord(CaseTools.Cycle)) await TransformSelection(CaseTools.Cycle, false, fg, explicitSelection: false);
                    break;
                case HotkeyAction.Transliterate:
                    string Tr(string t) => Transliterator.Transliterate(t, PreferredCyrillic());
                    if (!TransformLastWord(Tr)) await TransformSelection(Tr, false, fg, explicitSelection: false);
                    break;
                case HotkeyAction.ToggleDoNotDisturb:
                    ToggleDnd();
                    break;
                case HotkeyAction.AddException:
                    AddLastWordToExceptions();
                    break;
                case HotkeyAction.ClipboardHistory:
                    ClipboardHistoryRequested?.Invoke(fg);
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

    /// <summary>Selected text typed in the wrong layout: Latin words go to the Cyrillic layout that fits best.</summary>
    private string ConvertSelectionText(string text)
    {
        var cyrillic = _layouts.Installed.Where(LayoutMap.IsCyrillicLang).ToList();
        var target = PreferredCyrillic();
        if (cyrillic.Count > 1)
        {
            // Score each candidate by how many words become real words, preferring the last used one on ties.
            target = cyrillic
                .OrderByDescending(l => _detector.CountValidWords(LayoutMap.ConvertText(text, Lang.En, l), l))
                .ThenBy(l => l == target ? 0 : 1)
                .First();
        }
        return LayoutMap.ConvertText(text, target);
    }

    private void AddLastWordToExceptions()
    {
        var (start, count) = LastWordWithSpaces();
        if (count == 0) { Notify?.Invoke("Нет последнего слова для исключения"); return; }
        var keys = _line.GetRange(start, count).Where(k => !k.IsSpace).ToList();
        var words = _layouts.Installed.Select(l => LanguageDetector.Core(LayoutMap.Render(keys, l), l))
            .Where(w => w.Length > 0).Distinct().ToList();
        foreach (var w in words) AddException(w);
        Notify?.Invoke($"Больше не исправляются: {string.Join(" / ", words.Select(w => $"«{w}»"))}");
    }

    /// <summary>Order in which repeated Pause presses try layouts: the most likely first, the original last.</summary>
    private List<Lang> BuildCycle(List<KeyStroke> keys, Lang cur)
    {
        var word = keys.Where(k => !k.IsSpace).ToList();
        var pref = PreferredCyrillic();
        var others = _layouts.Installed.Where(l => l != cur)
            .OrderByDescending(l =>
            {
                var d = _detector.Analyze(word, cur, l);
                if (d.Convert) return d.Strength + 10;
                return _detector.IsValid(LanguageDetector.Core(d.Alternative, l), l) ? 5 : 0;
            })
            .ThenBy(l => LayoutMap.IsCyrillicLang(l) == LayoutMap.IsCyrillicLang(cur) ? 1 : 0) // Latin↔Cyrillic first
            .ThenBy(l => l == pref ? 0 : 1)
            .ToList();
        if (others.Count == 0) others.Add(cur == Lang.En ? pref : Lang.En);
        others.Add(cur);
        return others;
    }

    /// <summary>Re-type the last word (or whole line) in another layout. False if the buffer is empty.</summary>
    private bool RetypeLast(bool phrase)
    {
        var (start, count) = phrase ? (0, _line.Count) : LastWordWithSpaces();
        if (count == 0 || _line.Skip(start).All(k => k.IsSpace)) return false;
        var keys = _line.GetRange(start, count);
        var cur = _layouts.Current() ?? Lang.En;

        Lang target;
        if (_cycle is { } c && c.Start == start && c.Count == count)
        {
            int idx = (c.Index + 1) % c.Sequence.Count;
            target = c.Sequence[idx];
            _cycle = c with { Index = idx };
        }
        else
        {
            var seq = BuildCycle(keys, cur);
            target = seq[0];
            _cycle = new ManualCycle(start, count, seq, 0);
        }
        var before = LayoutMap.Render(keys, cur);
        var after = LayoutMap.Render(keys, target);

        // Converting back a word we auto-corrected a moment ago means the detector was wrong: learn from it.
        if (!phrase && _lastAuto is { Switched: true, IsSnippet: false } a && _s.LearnFromUndo)
            AddException(LanguageDetector.Core(a.Original, a.From));
        _lastAuto = null;

        InputSender.ReleaseModifiers(_held);
        InputSender.Backspace(count);
        InputSender.Text(after);
        _layouts.Switch(target);
        if (LayoutMap.IsCyrillicLang(target)) _lastCyrillic = target;
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

    // ───────────────────────────── selection & clipboard ─────────────────────────────

    private static readonly string[] TerminalClasses =
        ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "VirtualConsoleClass"];

    public static bool IsTerminal(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return TerminalClasses.Any(c => cls.Equals(c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Paste text into <paramref name="fg"/> and then give the user's clipboard back. The text is offered with
    /// delayed rendering, so the original clipboard is restored only after the app has really read our text.
    /// </summary>
    /// <returns>True if the app read the text.</returns>
    private async Task<bool> PasteViaClipboard(string text, IntPtr fg, ClipboardBackup? backup = null)
    {
        if (backup == null)
        {
            await _clipboard.WaitAvailableAsync();
            backup = ClipboardBackup.Save();
        }
        var requested = await _clipboard.OfferAsync(text);
        if (requested == null) { Trace?.Invoke("paste: clipboard locked"); backup.Restore(); return false; }
        // Clipboard managers (ClipAngel, Ditto...) grab every change at once and keep the clipboard open for a
        // moment; if Ctrl+V arrives then, the target app silently fails to open the clipboard. Let them finish.
        await Task.Delay(40);
        await _clipboard.WaitAvailableAsync(300);
        if (GetForegroundWindow() != fg) { Trace?.Invoke("paste: foreground changed"); if (_clipboard.IsOwner) backup.Restore(); return false; }
        // If such a manager already pulled the data, the "rendered" signal is used up: fall back to a fixed wait.
        bool renderedEarly = requested.IsCompleted;
        InputSender.Combo(VK_CONTROL_L, VK_V);
        bool pasted;
        if (renderedEarly)
        {
            Trace?.Invoke("paste: data was read early by another clipboard monitor");
            await Task.Delay(500);
            pasted = true;
        }
        else
        {
            pasted = await Task.WhenAny(requested, Task.Delay(3000)) == requested;
            if (!pasted) Trace?.Invoke($"paste: not requested within 3s (owner={_clipboard.IsOwner})");
        }
        await Task.Delay(60);
        if (_clipboard.IsOwner)
        {
            await _clipboard.WaitAvailableAsync();
            backup.Restore(); // only if nobody copied something new meanwhile
        }
        return pasted;
    }

    /// <summary>Paste a clipboard-history item: it becomes the current clipboard content, like Win+V.</summary>
    public async Task PasteFromHistory(string text, IntPtr fg)
    {
        if (_busy || fg == IntPtr.Zero) return;
        _busy = true;
        try
        {
            SetForegroundWindow(fg);
            for (int i = 0; i < 10 && GetForegroundWindow() != fg; i++) await Task.Delay(20);
            if (GetForegroundWindow() != fg) return;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text, TextDataFormat.UnicodeText); break; }
                catch (ExternalException) { await Task.Delay(30); }
            }
            InputSender.ReleaseModifiers(_held);
            InputSender.Combo(VK_CONTROL_L, VK_V);
        }
        finally
        {
            _busy = false;
            ResetLine();
        }
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
            await _clipboard.WaitAvailableAsync();
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
            if (await PasteViaClipboard(result, fg, backup) && GetForegroundWindow() == fg)
            {
                int len = VisibleLength(result);
                if (_s.ReselectAfterConvert && len <= MaxReselectChars) InputSender.SelectLeft(len);
                if (switchLayout && LayoutMap.DetectScript(result, PreferredCyrillic()) is { } target)
                {
                    _layouts.Switch(target);
                    if (LayoutMap.IsCyrillicLang(target)) _lastCyrillic = target;
                }
                AddLog(Shorten(text), Shorten(result), "выделение");
            }
        }
        finally
        {
            _busy = false;
            ResetLine();
        }
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ");
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}
