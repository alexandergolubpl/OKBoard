using System.Runtime.InteropServices;
using OKBoard;

/// <summary>End-to-end: real hook + real TextBox + synthetic (non-marked) keystrokes.</summary>
internal static class E2E
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, Native.INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private sealed class FocusLostException(string where) : Exception(where);

    /// <summary>
    /// Windows our synthetic input may go to. Input is injected globally, so if the user clicks another app
    /// while the test runs, keys would land there (a chat, an editor...). Abort instead.
    /// </summary>
    private static readonly HashSet<IntPtr> AllowedForeground = [];

    private static void EnsureForeground()
    {
        if (AllowedForeground.Count == 0) return;
        for (int i = 0; i < 30 && !AllowedForeground.Contains(Native.GetForegroundWindow()); i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        var fg = Native.GetForegroundWindow();
        if (!AllowedForeground.Contains(fg)) throw new FocusLostException(Native.GetProcessName(fg) ?? "?");
    }

    private static void SendKey(ushort vk, bool up, bool ext = false, bool marked = false) =>
        SendInput(1, [new Native.INPUT { type = 1, U = new Native.InputUnion { ki = new Native.KEYBDINPUT
        {
            wVk = vk, dwFlags = (up ? 2u : 0) | (ext ? 1u : 0), dwExtraInfo = marked ? (IntPtr)0x4F4B4244 : IntPtr.Zero,
        } } }], Marshal.SizeOf<Native.INPUT>());

    private static void Key(ushort vk, bool up, bool ext = false)
    {
        if (!up) EnsureForeground();
        SendKey(vk, up, ext);
    }

    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5;

    private static void Tap(ushort vk, params ushort[] mods)
    {
        foreach (var m in mods) Key(m, false, IsExtended(m));
        Key(vk, false, IsExtended(vk)); Key(vk, true, IsExtended(vk));
        foreach (var m in mods.Reverse()) Key(m, true, IsExtended(m));
        Pump(60);
    }

    private const string UsLower = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`1234567890-=\\ ";
    private const string UsUpper = "QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?~!@#$%^&*()_+| ";
    private static readonly ushort[] UsVk =
    [
        .. "QWERTYUIOP".Select(c => (ushort)c), 0xDB, 0xDD, .. "ASDFGHJKL".Select(c => (ushort)c), 0xBA, 0xDE,
        .. "ZXCVBNM".Select(c => (ushort)c), 0xBC, 0xBE, 0xBF, 0xC0, .. "1234567890".Select(c => (ushort)c), 0xBD, 0xBB, 0xDC, 0x20,
    ];

    /// <summary>Type characters by their US key positions (what you'd see with the English layout).</summary>
    private static void TypeKeys(string usKeys, int settle = 250)
    {
        foreach (var ch in usKeys)
        {
            if (ch == '\n') { Tap(0x0D); continue; }
            int i = UsLower.IndexOf(ch);
            if (i >= 0) { Tap(UsVk[i]); continue; }
            i = UsUpper.IndexOf(ch);
            if (i < 0) throw new ArgumentException($"no US key for '{ch}'");
            Tap(UsVk[i], 0xA0);
        }
        Pump(settle);
    }

    private static void Click(Control c)
    {
        EnsureForeground();
        var p = c.PointToScreen(new Point(c.Width / 2, c.Height / 2));
        var vs = SystemInformation.VirtualScreen;
        int dx = (int)((p.X - vs.Left) * 65535.0 / (vs.Width - 1)), dy = (int)((p.Y - vs.Top) * 65535.0 / (vs.Height - 1));
        const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, ABS = 0x8000, VDESK = 0x4000;
        Native.INPUT M(uint f) => new() { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = f | ABS | VDESK | MOVE } } };
        SendInput(3, [M(0), M(LDOWN), M(LUP)], Marshal.SizeOf<Native.INPUT>());
        Pump(80);
    }

    /// <summary>Pump messages until the condition holds (or the timeout passes).</summary>
    private static void PumpUntil(Func<bool> done, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end && !done()) { Application.DoEvents(); Thread.Sleep(5); }
        Pump(150);
    }

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    public static int Run()
    {
        int failed = 0;
        Form? formRef = null;
        void Expect(string name, string actual, string expected)
        {
            if (actual == expected) Console.WriteLine($"ok   {name}: \"{actual}\"");
            else
            {
                failed++;
                var fgw = Native.GetForegroundWindow();
                Console.WriteLine($"FAIL {name}: got \"{actual}\", expected \"{expected}\" (foreground: {(fgw == formRef?.Handle ? "test window" : Native.GetProcessName(fgw))})");
            }
        }

        Engine.Trace = m => Console.WriteLine("  trace: " + m);
        Settings.FilePath = Path.Combine(Path.GetTempPath(), "okboard-e2e-settings.json");
        var settings = new Settings { PauseInFullscreen = false, SoundOnAutoSwitch = false };
        var layouts = new LayoutManager();
        var ru = SpellChecker.ForLanguage("ru", "ru-RU");
        var en = SpellChecker.ForLanguage("en", "en-US", "en-GB");
        var det = new LanguageDetector(ru.IsCorrect, en.IsCorrect);

        var form = new Form { Text = "OKBoard E2E", Width = 600, Height = 200, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        var tb = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 14) };
        form.Controls.Add(tb);
        formRef = form;
        form.Show();
        SetForegroundWindow(form.Handle);
        form.Activate();
        tb.Focus();
        Pump(300);

        if (Native.GetForegroundWindow() != form.Handle)
        {
            // Windows' foreground lock: simulate an Alt tap so SetForegroundWindow is allowed, then retry.
            Key(0xA4, false); Key(0xA4, true);
            SetForegroundWindow(form.Handle); form.Activate(); tb.Focus(); Pump(300);
        }
        Console.WriteLine($"foreground ok: {Native.GetForegroundWindow() == form.Handle}");
        using var engine = new Engine(settings, det, layouts);
        engine.Start();

        void SetLang(Lang l) { layouts.Switch(l, tb.Handle); Pump(200); }
        void Reset() { PumpUntil(() => !engine.Busy, 5000); tb.Clear(); Tap(0x23); Pump(100); } // wait for clipboard work; End resets the buffer

        AllowedForeground.Add(form.Handle);
        try
        {

        // 1. wrong layout EN → RU
        Reset(); SetLang(Lang.En);
        TypeKeys("ghbdtn ");
        Expect("auto EN→RU", tb.Text, "привет ");
        Expect("layout switched", layouts.Current()?.ToString() ?? "?", "Ru");

        // 2. wrong layout RU → EN, then undo with Backspace
        Reset(); SetLang(Lang.Ru);
        TypeKeys("hello ");
        Expect("auto RU→EN", tb.Text, "hello ");
        Tap(0x08); Pump(200);
        Expect("undo by Backspace", tb.Text, "руддщ ");
        Expect("one undo doesn't learn", settings.ExceptionWords.Contains("руддщ").ToString(), "False");
        Reset(); SetLang(Lang.Ru);
        TypeKeys("hello ");
        Tap(0x08); Pump(200);
        Expect("second undo learns", settings.ExceptionWords.Contains("руддщ").ToString(), "True");
        Reset(); SetLang(Lang.Ru);
        TypeKeys("hello ");
        Expect("learned word stays", tb.Text, "руддщ ");
        settings.ExceptionWords.Remove("руддщ"); engine.ReloadSettings();
        // Backspace long after the correction is an ordinary Backspace
        Reset(); SetLang(Lang.En);
        TypeKeys("ghbdtn ");
        Pump(3200);
        Tap(0x08); Pump(200);
        Expect("late Backspace just deletes", tb.Text, "привет");

        // 3. correct words untouched, sentence
        Reset(); SetLang(Lang.Ru);
        TypeKeys("ghbdtn vbh ");
        Expect("correct RU untouched", tb.Text, "привет мир ");

        // 4. mixed: wrong-layout word in the middle of a sentence
        Reset(); SetLang(Lang.En);
        TypeKeys("hello ghbdtn ");
        Expect("sentence mid-switch", tb.Text, "hello привет ");

        // 5. manual: Pause converts last word
        Reset(); SetLang(Lang.En);
        TypeKeys("z");
        Tap(0x13); Pump(200);
        Expect("Pause last word", tb.Text, "я");

        // 6. Shift+Ctrl+Pause converts whole phrase
        Reset(); SetLang(Lang.En);
        TypeKeys("z ndjq");
        Tap(0x13, 0xA2, 0xA0); Pump(250);
        Expect("phrase convert", tb.Text, "я твой");

        // 7. case cycle on last word (Alt+Pause)
        Reset(); SetLang(Lang.En);
        TypeKeys("hello");
        Tap(0x13, 0xA4); Pump(200);
        Expect("case cycle 1", tb.Text, "HELLO");
        Tap(0x13, 0xA4); Pump(200);
        Expect("case cycle 2", tb.Text, "Hello");

        // 8. transliteration of the selection (Ctrl+Alt+T)
        Reset(); tb.Text = "щука"; tb.SelectAll(); Pump(100);
        Tap(0x54, 0xA2, 0xA4); PumpUntil(() => tb.Text != "щука");
        Expect("translit selection", tb.Text, "shchuka");

        // 9. selection layout conversion (Shift+Pause)
        Reset(); tb.Text = "Ghbdtn? rfr ltkf&"; tb.SelectAll(); Pump(100);
        Tap(0x13, 0xA0); PumpUntil(() => tb.Text != "Ghbdtn? rfr ltkf&");
        Expect("convert selection", tb.Text, "Привет, как дела?");

        // 9b. the user's clipboard survives a selection operation
        Clipboard.SetText("SECRET-CLIPBOARD");
        Reset(); tb.Text = "ntrcn"; tb.SelectAll(); Pump(100);
        Tap(0x13, 0xA0); PumpUntil(() => tb.Text != "ntrcn");
        PumpUntil(() => Clipboard.GetText() == "SECRET-CLIPBOARD");
        Expect("selection with clipboard", tb.Text, "текст");
        Expect("clipboard restored", Clipboard.GetText(), "SECRET-CLIPBOARD");

        // 9c. lone word + Enter (password pattern) is not rewritten; with a space before it — it is
        Reset(); SetLang(Lang.En);
        TypeKeys("ghbdtn"); Tap(0x0D); Pump(200);
        Expect("single word + Enter untouched", tb.Text, "ghbdtn\r\n");
        Reset(); SetLang(Lang.En);
        TypeKeys("ok ghbdtn"); Tap(0x0D); Pump(200);
        Expect("sentence + Enter corrected", tb.Text, "ok привет\r\n");

        // 9d. an unsafe hotkey (bare letter) must be ignored, not swallow typing
        settings.Hotkeys[HotkeyAction.AddException] = "Q";
        engine.ReloadSettings();
        Reset(); SetLang(Lang.En);
        TypeKeys("q");
        Expect("bare-letter hotkey ignored", tb.Text, "q");
        settings.Hotkeys[HotkeyAction.AddException] = "Ctrl+Alt+X";
        engine.ReloadSettings();

        // 9e. Pause twice on the same word cycles back to the original layout
        Reset(); SetLang(Lang.En);
        TypeKeys("z");
        Tap(0x13); Pump(200);
        Tap(0x13); Pump(200);
        Expect("Pause cycle returns", tb.Text, "z");

        // 9f. snippets: abbreviation works in any layout, Backspace undoes it
        settings.Snippets = [new Snippet { Abbr = "мк", Text = "Москва, Кремль" },
                             new Snippet { Abbr = "подп", Text = "С уважением,\nАлекс" }];
        engine.ReloadSettings();
        Reset(); SetLang(Lang.Ru);
        TypeKeys("vr ");
        Expect("snippet (RU layout)", tb.Text, "Москва, Кремль ");
        Reset(); SetLang(Lang.En);
        TypeKeys("vr ");
        Expect("snippet typed in EN layout", tb.Text, "Москва, Кремль ");
        Tap(0x08); Pump(200);
        Expect("snippet undo", tb.Text, "vr ");
        Clipboard.SetText("KEEP-ME");
        Reset(); SetLang(Lang.Ru);
        TypeKeys("gjlg "); PumpUntil(() => tb.Text.EndsWith("Алекс "));
        PumpUntil(() => Clipboard.GetText() == "KEEP-ME");
        Expect("multiline snippet via paste", tb.Text.Replace("\r\n", "\n"), "С уважением,\nАлекс ");
        Expect("clipboard kept after snippet", Clipboard.GetText(), "KEEP-ME");
        settings.Snippets = [];
        engine.ReloadSettings();

        // 9g. clipboard history: records normal copies, skips private ones and our own copy operations
        using (var history = new ClipboardHistory(engine.IsExcludedApp, () => engine.Busy))
        {
            Clipboard.SetText("first copy"); Pump(200);
            Clipboard.SetText("second copy"); Pump(200);
            var priv = new DataObject();
            priv.SetText("my-password-123");
            priv.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            Clipboard.SetDataObject(priv, true); Pump(200);
            var noHist = new DataObject();
            noHist.SetText("no-history-please");
            noHist.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            Clipboard.SetDataObject(noHist, true); Pump(200);
            Reset(); tb.Text = "ntrcn"; tb.SelectAll(); Pump(100);
            Tap(0x13, 0xA0); PumpUntil(() => tb.Text != "ntrcn"); Pump(700); // selection conversion copies "ntrcn" internally
            Clipboard.SetText("first copy"); Pump(200);
            var texts = history.Items.Select(i => i.Text).ToList();
            Expect("history order & dedupe", string.Join("|", texts), "first copy|second copy");
        }

        // 9h. a Shift release the engine never saw (stuck modifier) must not turn the next word into capitals
        Reset(); SetLang(Lang.En);
        Key(0xA0, false);
        SendInput(1, [new Native.INPUT { type = 1, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0xA0, dwFlags = 2, dwExtraInfo = (IntPtr)0x4F4B4244 } } }], Marshal.SizeOf<Native.INPUT>());
        Pump(60);
        TypeKeys("ghbdtn ");
        Expect("stuck Shift ignored", tb.Text, "привет ");
        Reset(); SetLang(Lang.En);
        TypeKeys("Ghbdtn ");
        Expect("capitalised word keeps its case", tb.Text, "Привет ");

        // 10. Caps Lock accident
        Reset(); SetLang(Lang.En);
        Tap(0x14);
        TypeKeys("Hello ");
        Expect("caps accident", tb.Text, "Hello ");
        Expect("caps turned off", Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock).ToString(), "False");
        if (Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock)) Tap(0x14);

        // 11. Do-not-disturb disables auto-switch
        Reset(); SetLang(Lang.En);
        engine.SetDnd(TimeSpan.FromMinutes(1));
        TypeKeys("ghbdtn ");
        Expect("DND keeps text", tb.Text, "ghbdtn ");
        engine.SetDnd(null);

        Robustness(engine, settings, form, tb, SetLang, Reset, Expect);
        }
        catch (FocusLostException ex)
        {
            failed++;
            Console.WriteLine($"ABORTED: another window took the focus ({ex.Message}); stopped typing to keep input out of it");
        }
        finally
        {
            AllowedForeground.Clear();
            foreach (ushort m in new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) SendKey(m, true, IsExtended(m));
            if (Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock)) { SendKey(0x14, false); SendKey(0x14, true); }
        }

        form.Close();
        File.Delete(Settings.FilePath);
        Console.WriteLine(failed == 0 ? "E2E: all passed" : $"E2E: {failed} failed");
        return failed;
    }

    private static IEnumerable<Control> Descendants(Control c)
    {
        foreach (Control x in c.Controls)
        {
            yield return x;
            foreach (var y in Descendants(x)) yield return y;
        }
    }

    /// <summary>
    /// Long, messy editor sessions. The engine must survive every one of them (checked by typing a sentinel
    /// word in the wrong layout) and must never touch editor tokens typed in the right layout.
    /// </summary>
    private static void Robustness(Engine engine, Settings settings, Form form, TextBox tb,
        Action<Lang> SetLang, Action Reset, Action<string, string, string> Expect)
    {
        string? savedClipboard = null;
        try { savedClipboard = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
        var recent = new Queue<string>();
        void Note(string a) { recent.Enqueue(a); while (recent.Count > 15) recent.Dequeue(); }

        bool Alive(string label)
        {
            foreach (ushort m in new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3 }) SendKey(m, true, IsExtended(m));
            if (Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock)) Tap(0x14);
            Reset(); SetLang(Lang.En);
            TypeKeys("cjkywt ");
            bool ok = tb.Text == "солнце ";
            Expect(label, tb.Text, "солнце ");
            if (!ok)
            {
                Console.WriteLine("  last actions: " + string.Join(" | ", recent));
                foreach (var line in engine.Diagnostics().Split(Environment.NewLine)) Console.WriteLine("  state: " + line);
                Console.WriteLine("  state: exceptions = [" + string.Join(", ", settings.ExceptionWords) + "]");
            }
            // Forget the checkpoint's own correction: a Backspace in the next random step would otherwise "undo" it
            // and, after two such undos, teach the engine to never correct the checkpoint word.
            Reset();
            return ok;
        }

        // 12. Unity / IDE / shell tokens typed in the right layout stay exactly as typed
        string[] tokens =
        [
            "t:Prefab", "t:Material", "t:Scene", "l:Environment", "ref:Player", "Assets/Prefabs", "Vector3.zero",
            "transform.position", "GetComponent<Rigidbody>()", "[SerializeField]", "Debug.Log", "OnTriggerEnter",
            "deltaTime", "m_Speed", "#if", "std::vector", "x=5;", "i++", "e.g.", "git", "npm", "ctx", "lerp", "navmesh",
            "asmdef", "wasd", "qwe",
        ];
        foreach (var t in tokens)
        {
            Reset(); SetLang(Lang.En);
            TypeKeys(t + " ", 120);
            Expect($"editor token kept: {t}", tb.Text, t + " ");
        }
        foreach (var (k, w) in new[] { ("rjvvbn", "коммит"), (",bkl", "билд"), ("ghtaf,", "префаб"), ("lt,fu", "дебаг"), ("rjvgjytyn", "компонент") })
        {
            Reset(); SetLang(Lang.Ru);
            TypeKeys(k + " ", 120);
            Expect($"RU slang kept: {w}", tb.Text, w + " ");
        }

        // 13. Windows silently drops the keyboard hook → the witness hook notices and re-installs it
        int before = engine.HookReinstalls;
        engine.DropKeyboardHookForTest();
        Reset(); SetLang(Lang.En);
        TypeKeys("zzzz", 100);
        PumpUntil(() => engine.HookReinstalls > before, 3000);
        Expect("watchdog re-installed a dropped hook", (engine.HookReinstalls > before).ToString(), "True");
        Alive("alive after the hook was dropped");

        // 14. our thread stalls longer than LowLevelHooksTimeout while the user keeps typing
        before = engine.HookReinstalls;
        Reset(); SetLang(Lang.En);
        var typist = new Thread(() =>
        {
            for (int i = 0; i < 8; i++) { Thread.Sleep(300); SendKey(0x5A, false); SendKey(0x5A, true); }
        });
        typist.Start();
        Thread.Sleep(3500); // not pumping messages: the hook cannot run
        typist.Join();
        Pump(300);
        TypeKeys("zzzz", 100);
        Pump(1200);
        Console.WriteLine($"  info: hook re-installs after the stall: {engine.HookReinstalls - before}");
        Alive("alive after a 3.5 s stall");

        // 15. settings window left open with a hotkey box focused, the user goes back to work
        var sf = new SettingsForm(settings, engine, () => { });
        sf.Show();
        sf.SelectTab(1);
        var sfHandle = sf.Handle;
        AllowedForeground.Add(sfHandle);
        Native.SetForegroundWindow(sf.Handle); sf.Activate(); Pump(200);
        var hb = Descendants(sf).OfType<HotkeyBox>().First();
        hb.Focus(); Pump(200);
        Console.WriteLine($"  info: hotkey box focused, suspended there: {engine.IsSuspended}");
        Native.SetForegroundWindow(form.Handle); form.Activate(); tb.Focus(); Pump(300);
        Alive("alive while settings are left open in the background");
        sf.Close(); Pump(100);
        AllowedForeground.Remove(sfHandle);

        // 16. a long editing session: shortcuts, navigation, mouse, Caps Lock, stuck modifiers, layout hotkeys...
        int seed = int.TryParse(Environment.GetEnvironmentVariable("OKB_SEED"), out var sd) ? sd : 20261106;
        Console.WriteLine($"  info: editing session seed {seed}");
        var rnd = new Random(seed);
        string[] enOk = ["hello", "world", "update", "build", "prefab", "shader", "the", "project", "player", "t:Prefab", "Vector3.zero", "ctx.Pos"];
        string[] ruOk = ["ghbdtn", "vbh", "ntcn", "ghjtrn", "rjvvbn", "cgfcb,j", "lf", "ytn"]; // correct Russian, typed in RU layout
        ushort[][] combos =
        [
            [0x41, 0xA2], [0x43, 0xA2], [0x56, 0xA2], [0x58, 0xA2], [0x5A, 0xA2], [0x59, 0xA2], [0x53, 0xA2], [0x44, 0xA2],
            [0x46, 0xA2], [0x5A, 0xA2, 0xA0], [0x46, 0xA2, 0xA0], [0x20, 0xA2], [0x20, 0xA0], [0x08, 0xA2], [0x25, 0xA2],
            [0x27, 0xA2], [0x24, 0xA2], [0x23, 0xA2], [0x25, 0xA0], [0x27, 0xA0], [0x24, 0xA0], [0x23, 0xA0], [0x2E, 0xA2],
            [0x4B, 0xA2], [0x50, 0xA2, 0xA0], [0x70, 0xA0], [0x09, 0xA2], [0x1B, 0xA0],
        ];
        ushort[] nav = [0x25, 0x26, 0x27, 0x28, 0x24, 0x23, 0x21, 0x22, 0x2E, 0x1B, 0x71, 0x72, 0x74, 0x7B];
        int checkpoints = 0, alive = 0;
        Reset();
        for (int step = 1; step <= 400; step++)
        {
            if (tb.TextLength > 4000) { tb.Clear(); Note("clear"); }
            int r = rnd.Next(100);
            if (r < 30)
            {
                bool ru = rnd.Next(2) == 0;
                bool wrong = rnd.Next(4) == 0;
                var lang = ru ^ wrong ? Lang.Ru : Lang.En;
                if (layoutsCurrent() != lang) SetLang(lang);
                var w = ru ? ruOk[rnd.Next(ruOk.Length)] : enOk[rnd.Next(enOk.Length)];
                TypeKeys(w + (rnd.Next(5) == 0 ? "\n" : " "), 30);
                Note($"type[{lang}] {w}");
            }
            else if (r < 50) { var c = combos[rnd.Next(combos.Length)]; Tap(c[0], c[1..]); Note($"combo {string.Join("+", c.Select(x => x.ToString("X2")))}"); }
            else if (r < 62) { var k = nav[rnd.Next(nav.Length)]; Tap(k); Note($"nav {k:X2}"); }
            else if (r < 67) { Click(tb); Note("click"); }
            else if (r < 70) { Tap(0x14); Note("caps"); }
            else if (r < 74)
            {
                // a modifier whose release the engine never saw (Win+Shift+S, UAC, lock screen...)
                ushort m = rnd.Next(2) == 0 ? (ushort)0xA2 : (ushort)0xA0;
                Key(m, false); SendKey(m, true, marked: true); Pump(30);
                Note($"stuck {m:X2}");
            }
            else if (r < 78) { Tap(0x13, rnd.Next(3) switch { 0 => new ushort[0], 1 => new ushort[] { 0xA4 }, _ => new ushort[] { 0xA0 } }); Pump(300); PumpUntil(() => !engine.Busy, 3000); Note("pause-hotkey"); }
            else if (r < 82) { Tap(0xA0, 0xA4); Note("alt+shift"); }
            else if (r < 88) { for (int i = rnd.Next(1, 6); i > 0; i--) Tap(0x08); Note("backspaces"); }
            else if (r < 94) { Tap(rnd.Next(2) == 0 ? (ushort)0x0D : (ushort)0x09); Note("enter/tab"); }
            else { TypeKeys(new string(Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => "qwertasdfzxc"[rnd.Next(12)]).ToArray()), 20); Note("bare letters"); }

            if (step % 40 == 0)
            {
                checkpoints++;
                if (Alive($"alive at step {step}")) alive++;
            }
        }
        Console.WriteLine($"  info: editing session: {checkpoints} checkpoints, {alive} alive, {engine.HookReinstalls} hook re-installs total, " +
                          $"exceptions learned: [{string.Join(", ", settings.ExceptionWords)}]");

        Lang? layoutsCurrent() => engine.CurrentLang;
        try { if (savedClipboard != null) Clipboard.SetText(savedClipboard); } catch { }
    }
}
