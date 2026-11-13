using System.Runtime.InteropServices;
using static OKBoard.Native;

namespace OKBoard;

public sealed record CorrectionEntry(DateTime Time, string App, string From, string To, string Kind);

/// <summary>
/// Core of the switcher: watches keystrokes, keeps a buffer of the current line, auto-corrects words typed in
/// the wrong layout, expands abbreviations and executes hotkey actions. Everything runs on the UI thread
/// (low-level hooks are delivered there). A watchdog re-installs the hooks if Windows drops them.
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
    private List<SwitchRule> _rules = [];
    private Lang? _prevWordLang;      // layout the previous word of this line ended up in
    private bool _midWordTried;       // the while-typing check already ran (and converted or not) for this word
    private bool _midWordFixed;       // ...and converted it: the word is settled, don't second-guess it at the space
    private Lang? _lastLangSeen;
    private long _userSwitchTick = long.MinValue / 2, _wordStartTick;
    private readonly Dictionary<string, int> _manualCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTime time, string reason, bool converted)> _decisions = new();
    private long _lastSelfTest = Environment.TickCount64, _lastStatsLog = Environment.TickCount64;

    /// <summary>OS spell checkers, for the periodic self-test and diagnostics.</summary>
    public List<SpellChecker> SpellCheckers { get; } = [];
    /// <summary>Message to show once at start-up (e.g. what the legacy cleanup removed).</summary>
    public string? StartupNotice { get; private set; }
    private readonly Dictionary<string, int> _undoCounts = new(StringComparer.OrdinalIgnoreCase);
    private const int UndoWindowMs = 3000, UndoesToLearn = 2;
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
    private int _lastHookTick = Environment.TickCount;
    private long _lastReinstall = long.MinValue / 2;
    private KeyboardWitness? _witness;
    private System.Windows.Forms.Timer? _watchdog;
    private long _witnessAtLastKey;   // raw-input key count when the hook last saw a key
    private int _suspectChecks;       // consecutive watchdog checks that found keys the hook missed
    private IntPtr _suspendRoot;

    public int HookReinstalls { get; private set; }
    public string? LastReinstallReason { get; private set; }
    public DateTime LastReinstallTime { get; private set; }
    public long LastHookKeyTick { get; private set; }
    public bool WitnessActive => _witness?.Active == true;
    private readonly LowLevelProc _kbProc, _mouseProc;
    private readonly WinEventProc _fgProc;

    public DateTime DndUntil { get; private set; } = DateTime.MinValue;
    public bool IsDnd => DateTime.Now < DndUntil;
    public bool IsFullscreenPaused => _s.PauseInFullscreen && IsFullscreen();
    /// <summary>
    /// Pause processing while one of our own input windows (hotkey box, clipboard popup) is in front.
    /// Tied to that window: if it is left open in the background (or a Leave event never comes), typing in
    /// every other app keeps working. A global flag here used to switch the program off everywhere.
    /// </summary>
    public void SuspendFor(IntPtr hwnd) => _suspendRoot = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
    public void Resume() => _suspendRoot = IntPtr.Zero;

    public bool IsSuspended
    {
        get
        {
            var root = _suspendRoot;
            if (root == IntPtr.Zero) return false;
            if (!IsWindow(root)) { _suspendRoot = IntPtr.Zero; return false; }
            return GetAncestor(GetForegroundWindow(), GA_ROOT) == root;
        }
    }
    private const uint GA_ROOT = 2;
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
        string Original, string Converted, bool IsSnippet)
    {
        /// <summary>When the correction happened: Backspace undoes it only shortly afterwards.</summary>
        public long Tick { get; } = Environment.TickCount64;
    }

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
        _witness = new KeyboardWitness(InjectMarker); // before the main hook, see KeyboardWitness
        InstallInputHooks();
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (_kbHook == IntPtr.Zero) throw new InvalidOperationException("Не удалось установить перехват клавиатуры: " + Marshal.GetLastWin32Error());

        _watchdog = new System.Windows.Forms.Timer { Interval = 500 };
        _watchdog.Tick += (_, _) => CheckHookHealth();
        _watchdog.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Trace?.Invoke($"started (key witness: {_witness.Active})");
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            _ui.Post(_ => ReinstallHooks("session " + e.Reason), null);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _ui.Post(_ => ReinstallHooks("resume"), null);
    }

    private void InstallInputHooks()
    {
        var mod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    /// <summary>
    /// Windows silently removes a low-level hook if its callback ever exceeds LowLevelHooksTimeout (e.g. our
    /// thread stalled for a few seconds on a slow clipboard owner). Two independent checks, run every 500 ms:
    /// the witness hook saw key presses the main hook did not; or the system saw input that neither hook did.
    /// </summary>
    public void CheckHookHealth()
    {
        var nowTick = Environment.TickCount64;
        if (nowTick - _lastSelfTest > 10 * 60_000)
        {
            _lastSelfTest = nowTick;
            foreach (var sc in SpellCheckers) sc.SelfTest();
        }
        if (nowTick - _lastStatsLog > 60 * 60_000)
        {
            _lastStatsLog = nowTick;
            Trace?.Invoke("last hour: " + string.Join("; ", DecisionSummary(TimeSpan.FromHours(1))).Replace("  ", ""));
        }
        // 1) Keys the witness hook (own thread) counted since the main hook last saw one. The witness is called
        //    after the main hook, so a healthy main hook keeps this at 0..1; require it on two checks in a row.
        long missed = _witness is { Active: true } w ? w.KeyDowns - _witnessAtLastKey : 0;
        _suspectChecks = missed >= 3 ? _suspectChecks + 1 : 0;
        if (_suspectChecks >= 2)
        {
            ReinstallHooks($"keyboard hook missed {missed} keys");
            return;
        }
        // 2) Fallback without raw input (also covers the mouse hook).
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        int sinceInputSeen = (int)(info.dwTime - (uint)_lastHookTick); // uint arithmetic: safe across the 49.7-day wrap
        if (sinceInputSeen >= 3000) ReinstallHooks("input not seen by hooks");
    }

    /// <summary>Tests: simulate Windows silently removing the keyboard hook (the handle is kept, as in real life).</summary>
    internal void DropKeyboardHookForTest() => UnhookWindowsHookEx(_kbHook);

    /// <summary>Unhook and hook again (cheap; safe to call any time on the UI thread).</summary>
    public void ReinstallHooks(string reason)
    {
        // Throttle automatic re-installs (e.g. elevated windows, where UIPI hides input from our hook).
        var now = Environment.TickCount64;
        if (!reason.StartsWith("manual") && now - _lastReinstall < (reason.StartsWith("input not seen") ? 30000 : 5000)) { _suspectChecks = 0; _witnessAtLastKey = _witness?.KeyDowns ?? 0; return; }
        _lastReinstall = now;
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        InstallInputHooks();
        _lastHookTick = Environment.TickCount;
        _suspectChecks = 0;
        _witnessAtLastKey = _witness?.KeyDowns ?? 0;
        _held.Clear();
        ResetLine();
        HookReinstalls++;
        LastReinstallReason = reason;
        LastReinstallTime = DateTime.Now;
        Trace?.Invoke($"hooks reinstalled: {reason} (ok={_kbHook != IntPtr.Zero})");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        _kbHook = _mouseHook = _fgHook = IntPtr.Zero;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watchdog?.Dispose();
        _witness?.Dispose();
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
        _rules = RuleParser.Parse(_s.Rules);
        _detector.Level = (Aggressiveness)Math.Clamp(_s.Aggressiveness, 0, 2);
    }

    /// <summary>
    /// Versions before 0.2.3 added a word to the exceptions after a single Backspace undo, so the list filled up
    /// with everyday words typed in the wrong layout ("ghbdtn" = привет). Remove, once, the entries whose
    /// conversion is a real word while they themselves are not — keeping a copy in the settings.
    /// </summary>
    public void CleanupLegacyExceptions()
    {
        if (_s.LegacyExceptionsCleaned) return;
        var removed = new List<string>();
        foreach (var w in _s.ExceptionWords.ToList())
        {
            var script = LayoutMap.DetectScript(w, PreferredCyrillic());
            if (script == null) continue;
            var from = script.Value;
            var to = LayoutMap.IsCyrillicLang(from) ? Lang.En : PreferredCyrillic();
            var alt = LayoutMap.ConvertText(w, from, to);
            if (!_detector.IsValid(w, from) && _detector.IsValid(LanguageDetector.Core(alt, to), to))
                removed.Add(w);
        }
        _s.LegacyExceptionsCleaned = true;
        if (removed.Count > 0)
        {
            _s.ExceptionWords.RemoveAll(w => removed.Contains(w));
            _s.RemovedLegacyExceptions = removed;
            StartupNotice = $"Убраны ошибочно выученные исключения ({removed.Count}): {string.Join(", ", removed.Take(8))}" +
                            (removed.Count > 8 ? "…" : "") + ". Если какое-то нужно, добавьте его снова в настройках.";
            Trace?.Invoke($"legacy exceptions removed: {removed.Count}");
        }
        try { _s.Save(); } catch { }
        ReloadSettings();
    }

    public void RemoveException(string word)
    {
        word = word.Trim().ToLowerInvariant();
        if (!_exceptions.Remove(word)) return;
        _s.ExceptionWords.RemoveAll(w => w.Equals(word, StringComparison.OrdinalIgnoreCase));
        SaveSoon();
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
                if (down)
                {
                    _witnessAtLastKey = _witness?.KeyDowns ?? 0;
                    LastHookKeyTick = Environment.TickCount64;
                }
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
        _lastLangSeen = null; // another window has its own layout: not a manual switch
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
            if (_s.CapsLockSwitchesLayout && !ShiftHeld && !CtrlHeld && !AltHeld && !IsSuspended)
            {
                if (first) ToggleLayout();
                _swallowCapsUp = true;
                return true;
            }
            return false;
        }

        if (IsSuspended) return false;
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
            if (_lastAuto != null && _s.UndoWithBackspace && Environment.TickCount64 - _lastAuto.Tick <= UndoWindowMs)
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
                _wordStartTick = Environment.TickCount64;
                _midWordTried = false;
                _midWordFixed = false;
                // The layout changed since the last word and it wasn't us: the user switched it by hand.
                if (_lastLangSeen != null && _wordStartLang != null && _wordStartLang != _lastLangSeen &&
                    Environment.TickCount64 - _layouts.LastOwnSwitchTick > 1500)
                    _userSwitchTick = Environment.TickCount64;
                if (_wordStartLang != null) _lastLangSeen = _wordStartLang;
                if (_wordStartLang is { } l && LayoutMap.IsCyrillicLang(l)) _lastCyrillic = l;
            }
            _line.Add(new KeyStroke(vk, ShiftHeld, CapsOn));
            if (_line.Count > 400) _line.RemoveRange(0, 200);
            return TryFixWhileTyping();
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
        if (!_shiftAlone || !_s.DoubleShiftConverts || IsSuspended || IsFullscreenPaused) { _shiftAlone = false; return; }
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
        _prevWordLang = null;
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
            // QUNS_RUNNING_D3D_FULL_SCREEN (3) and QUNS_PRESENTATION_MODE (4) are unambiguous. QUNS_BUSY (2) also
            // stays set while *any* window is full-screen, e.g. a video on the second monitor, so for it we
            // pause only if the window being typed into is itself full-screen.
            int st = SHQueryUserNotificationState(out var s) == 0 ? s : 0;
            _fullscreenCached = st is 3 or 4 || (st == 2 && ForegroundCoversMonitor());
        }
        return _fullscreenCached;
    }

    private static bool ForegroundCoversMonitor()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // the desktop itself
        // Maximized windows also cover the whole monitor when the taskbar auto-hides: they are not "full-screen".
        if (((long)GetWindowLongPtr(fg, -16 /* GWL_STYLE */) & 0x01000000 /* WS_MAXIMIZE */) != 0) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2 /* NEAREST */), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Human-readable state for the "Diagnostics" window. Contains no typed text.</summary>
    public string Diagnostics()
    {
        var fgProc = ForegroundProcess() ?? "?";
        long sinceKey = LastHookKeyTick == 0 ? -1 : (Environment.TickCount64 - LastHookKeyTick) / 1000;
        var lines = new List<string>
        {
            $"Перехват клавиатуры: {(_kbHook != IntPtr.Zero ? "установлен" : "НЕТ")}, последнее нажатие {(sinceKey < 0 ? "ещё не было" : $"{sinceKey} с назад")}",
            $"Сторож перехвата: {(WitnessActive ? "работает" : "недоступен")}; переустановок перехвата: {HookReinstalls}"
                + (LastReinstallReason != null ? $" (последняя {LastReinstallTime:HH:mm:ss}: {LastReinstallReason})" : ""),
            $"Автопереключение: {(_s.AutoSwitch ? "включено" : "ВЫКЛЮЧЕНО (меню трея → «Автопереключение раскладки»)")}",
            $"Исключений (слов, которые не исправляются): {_exceptions.Count}",
            $"Не беспокоить: {(IsDnd ? $"ВКЛЮЧЕНО до {DndUntil:HH:mm}" : "нет")}",
            $"Пауза в полноэкранном режиме: {(IsFullscreenPaused ? "ДА (сейчас пауза)" : "нет")}",
            $"Приостановлено своим окном: {(IsSuspended ? "ДА" : "нет")}",
            $"Выполняется операция с буфером: {(_busy ? "ДА" : "нет")}",
            $"Активное приложение: {fgProc}{(IsExcludedApp(fgProc) ? " (В ИСКЛЮЧЕНИЯХ)" : "")}",
            $"Удерживаемые модификаторы (по данным программы): {(_held.Count == 0 ? "нет" : string.Join(", ", _held.Select(v => $"0x{v:X2}")))}",
            $"Раскладка сейчас: {CurrentLang?.ToString() ?? "не распознана"}; установлены: {string.Join(", ", _layouts.Installed)}",
            $"Словари: {string.Join(", ", new[] { Lang.En, Lang.Ru, Lang.Uk }.Where(_layouts.Has).Select(l => $"{l}={(_detector.HasDictionary(l) ? "есть" : "нет")}"))}",
            $"Проверка словарей Windows: {(SpellCheckers.Count == 0 ? "—" : string.Join(", ", SpellCheckers.Select(sc => $"{sc.Tag} {(sc.LastSelfTestOk ? "ok" : "СБОЙ")} (ошибок {sc.Failures}, пересоздан {sc.Recreated})")))}",
            $"Правил: {_rules.Count}",
            "Решения за последний час:",
        };
        lines.AddRange(DecisionSummary(TimeSpan.FromHours(1)).Select(l => "  " + l));
        return string.Join(Environment.NewLine, lines);
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
        var why = count == 0 ? null
            : singleTokenSubmit && _detector.Level == Aggressiveness.Careful ? "одно слово перед Enter/Tab"
            : _midWordFixed ? "исправлено во время набора"
            : WhyNotAuto() is { } w ? w
            : _layouts.Current() is not { } c0 ? "раскладка не распознана"
            : _wordStartLang != null && _wordStartLang != c0 ? "раскладку сменили посреди слова"
            : _s.TrustManualSwitch && _wordStartTick - _userSwitchTick < 5000 && _userSwitchTick > _layouts.LastOwnSwitchTick
                ? "раскладку только что переключили вручную"
            : null;
        if (count > 0 && why != null) NoteDecision(why, false);
        if (count > 0 && why == null && _layouts.Current() is { } cur)
        {
            var word = _line.GetRange(start, count);
            var context = _prevWordLang is { } pl && pl != cur ? pl : (Lang?)null;
            var d = ApplyRules(word, cur) ?? _detector.AnalyzeBest(word, cur, _layouts.Installed, PreferredCyrillic(), context);
            // A lone word followed by Enter/Tab may be a password: convert it only into a real dictionary/common word.
            if (singleTokenSubmit && d.Convert && d.Strength < 2)
                d = d with { Convert = false, Reason = "одно слово перед Enter/Tab, нет уверенности" };
            NoteDecision(d.Reason, d.Convert);
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
            // "z ndjq" → "я твой": a short word just before the corrected one was typed in the same wrong layout
            var prev = d.Convert ? ShortWordBefore(start, cur, renderLang) : null;
            if (prev is { } pw)
            {
                word = [.. _line.GetRange(pw.start, start - pw.start), .. word];
                newKeys = [.. _line.GetRange(pw.start, start - pw.start), .. newKeys];
                count += start - pw.start;
                start = pw.start;
                newText = LayoutMap.Render(newKeys, renderLang);
                oldText = LayoutMap.Render(word, cur);
                kinds.Add("короткое слово перед ним");
            }
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
        if (count > 0) _prevWordLang = _layouts.Current();
        _wordStartLang = null;
        if (vk == VK_SPACE) _line.Add(new KeyStroke(VK_SPACE, false));
        else { var keep = _lastAuto; ResetLine(); _lastAuto = keep; }
        return swallow;
    }

    /// <summary>
    /// Caramba-style: once the first 4–7 letters can't start a word of this layout but start a common word of the
    /// other one, retype them in the other layout and switch, so the rest of the word is typed right.
    /// </summary>
    /// <returns>true to swallow the current key (it is retyped together with the converted start).</returns>
    private bool TryFixWhileTyping()
    {
        if (_midWordTried || !_s.SwitchWhileTyping || _detector.Level != Aggressiveness.Active) return false;
        var (start, count) = WordRange();
        if (count < 4) return false;
        if (count > 7) { _midWordTried = true; return false; }
        if (_layouts.Current() is not { } cur || (_wordStartLang != null && _wordStartLang != cur)) return false;
        if (WhyNotAuto() != null || IsSuspended) return false;
        if (_s.TrustManualSwitch && _wordStartTick - _userSwitchTick < 5000 && _userSwitchTick > _layouts.LastOwnSwitchTick) return false;
        var keys = _line.GetRange(start, count);
        var rule = ApplyRules(keys, cur);
        if (rule is { Convert: false }) { _midWordTried = true; return false; }
        var d = _detector.AnalyzePrefix(keys, cur, _layouts.Installed, PreferredCyrillic());
        if (!d.Convert) return false;
        _midWordTried = true;
        _midWordFixed = true;
        var newText = LayoutMap.Render(keys, d.Target);
        InputSender.ReleaseModifiers(_held.Where(v => v is 0xA0 or 0xA1));
        InputSender.Backspace(count - 1);   // the current key hasn't reached the app yet: we swallow it
        InputSender.Text(newText);
        _layouts.Switch(d.Target);
        _wordStartLang = d.Target;
        _lastLangSeen = d.Target;
        if (LayoutMap.IsCyrillicLang(d.Target)) _lastCyrillic = d.Target;
        NoteDecision("во время набора: " + d.Reason, true);
        AddLog(d.Current + "…", newText + "…", "во время набора");
        if (_s.SoundOnAutoSwitch) Task.Run(() => Console.Beep(1400, 25));
        return true;
    }

    /// <summary>Why auto-correction is off right now (null = it is on).</summary>
    private string? WhyNotAuto() =>
        !_s.AutoSwitch ? "автопереключение выключено"
        : IsDnd ? "режим «Не беспокоить»"
        : IsExcludedApp(ForegroundProcess()) ? "приложение в исключениях"
        : InPasswordField() ? "поле пароля"
        : null;

    /// <summary>User rules: "нет" wins over "да"; both win over the dictionary and the exceptions.</summary>
    private Decision? ApplyRules(List<KeyStroke> word, Lang cur)
    {
        if (_rules.Count == 0) return null;
        var app = ForegroundProcess();
        var curText = LayoutMap.Render(word, cur);
        var renders = _layouts.Installed.Select(l => (lang: l, text: LayoutMap.Render(word, l))).ToList();
        bool Hit(SwitchRule r, string t) => r.Matches(t) || r.Matches(LanguageDetector.Core(t, LayoutMap.DetectScript(t) ?? cur));
        var applicable = _rules.Where(r => r.AppliesTo(app)).ToList();
        if (applicable.Any(r => r.Action == RuleAction.Keep && renders.Any(x => Hit(r, x.text))))
            return new Decision(false, "правило: не трогать", curText, "", cur);
        foreach (var r in applicable.Where(r => r.Action == RuleAction.Switch))
        {
            foreach (var (l, text) in renders)
            {
                if (l == cur || (r.Target is { } t && t != l)) continue;
                if (Hit(r, text) || Hit(r, curText))
                    return new Decision(true, "правило: исправлять", curText, text, l, 9);
            }
        }
        return null;
    }

    /// <summary>
    /// A 1–3 letter word right before position <paramref name="start"/> (one space between) that isn't a word
    /// in the current layout but is a common short word in the target one ("z" → "я", "b" → "и").
    /// </summary>
    private (int start, int count)? ShortWordBefore(int start, Lang cur, Lang target)
    {
        if (!_s.FixShortWordBefore || LayoutMap.IsCyrillicLang(cur) == LayoutMap.IsCyrillicLang(target)) return null;
        if (start < 2 || !_line[start - 1].IsSpace || _line[start - 2].IsSpace) return null;
        int end = start - 1, s = end;
        while (s > 0 && !_line[s - 1].IsSpace) s--;
        int n = end - s;
        if (n is < 1 or > 3) return null;
        var keys = _line.GetRange(s, n);
        var curText = LanguageDetector.Core(LayoutMap.Render(keys, cur), cur);
        var tText = LanguageDetector.Core(LayoutMap.Render(keys, target), target);
        if (curText.Length == 0 || tText.Length == 0 || _detector.IsValid(curText, cur) || IsExceptionWord(curText)) return null;
        return _detector.IsShortCommon(tText, target) ? (s, n) : null;
    }

    private bool IsExceptionWord(string w) => _exceptions.Contains(w.ToLowerInvariant());

    private void NoteDecision(string reason, bool converted)
    {
        _decisions.Enqueue((DateTime.Now, reason, converted));
        while (_decisions.Count > 500) _decisions.Dequeue();
    }

    /// <summary>Last hour of decisions as "reason: count" lines (no typed text, only why).</summary>
    private List<string> DecisionSummary(TimeSpan window)
    {
        var since = DateTime.Now - window;
        var recent = _decisions.Where(x => x.time >= since).ToList();
        var lines = new List<string> { $"слов: {recent.Count}, исправлено: {recent.Count(x => x.converted)}" };
        lines.AddRange(recent.GroupBy(x => x.reason).OrderByDescending(g => g.Count()).Take(8).Select(g => $"  {g.Key}: {g.Count()}"));
        return lines;
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
        if (a.Switched && _s.LearnFromUndo && LearnFromUndo(LanguageDetector.Core(a.Original, a.From)))
            AddLog(a.Converted, a.Original, "отмена → в исключения");
        else AddLog(a.Converted, a.Original, "отмена");
    }

    /// <summary>
    /// The user fixed a word by hand (Pause) that we left alone. If it is in the exceptions, that exception was
    /// wrong: drop it now. After the second manual fix of the same word, add a rule to always correct it.
    /// </summary>
    private void LearnFromManual(string word, Lang target)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0) return;
        if (IsExceptionWord(word))
        {
            RemoveException(word);
            Notify?.Invoke($"«{word}» убрано из исключений: вы исправили его вручную");
            return;
        }
        if (word.Length < 2) return;
        int n = _manualCounts.GetValueOrDefault(word) + 1;
        _manualCounts[word] = n;
        if (n < 2) return;
        _manualCounts.Remove(word);
        var line = $"{word} = {target.ToString().ToLowerInvariant()}";
        if (_s.Rules.Any(r => r.Trim().Equals(line, StringComparison.OrdinalIgnoreCase))) return;
        _s.Rules.Add(line);
        _rules = RuleParser.Parse(_s.Rules);
        SaveSoon();
        Notify?.Invoke($"Новое правило «{line}»: вы дважды исправили это слово вручную. Правила — в настройках.");
    }

    /// <summary>
    /// Backspace right after a correction is also how people delete the space or fix a typo, so one undo is not
    /// proof that the detector was wrong. Learning from every single undo used to fill the exception list with
    /// everyday words (привет, как, надо...) until auto-correction quietly stopped working. Now a word becomes
    /// an exception only when its correction is undone twice.
    /// </summary>
    /// <returns>True if the word was added to the exceptions.</returns>
    private bool LearnFromUndo(string word)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0) return false;
        int n = _undoCounts.GetValueOrDefault(word) + 1;
        _undoCounts[word] = n;
        if (n < UndoesToLearn) return false;
        _undoCounts.Remove(word);
        AddException(word);
        Notify?.Invoke($"«{word}» больше не исправляется (исправление отменено дважды). Убрать можно в настройках, вкладка «Исключения».");
        return true;
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
        // A previous clipboard operation may still be restoring the user's clipboard (clipboard managers make
        // that take ~0.5 s). Wait for it instead of silently dropping the key press.
        for (int i = 0; i < 40 && _busy; i++) await Task.Delay(50);
        if (_busy) { Trace?.Invoke($"hotkey {action} dropped: busy"); return; }
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
        bool wasAuto = _lastAuto is { Switched: true, IsSnippet: false };
        if (!phrase && _lastAuto is { Switched: true, IsSnippet: false } a && _s.LearnFromUndo)
            LearnFromUndo(LanguageDetector.Core(a.Original, a.From));
        else if (!phrase && !wasAuto && _s.LearnFromManual && _cycle is { Index: 0 })
            LearnFromManual(LanguageDetector.Core(before, cur), target);
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
        bool changed = false; // only then is the line buffer out of date; otherwise the user may already be typing
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
            changed = true;
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
            if (changed) ResetLine();
        }
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ");
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}
