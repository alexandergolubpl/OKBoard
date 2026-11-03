using System.Runtime.InteropServices;
using OKBoard;

/// <summary>End-to-end: real hook + real TextBox + synthetic (non-marked) keystrokes.</summary>
internal static class E2E
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, Native.INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private static void Key(ushort vk, bool up, bool ext = false) =>
        SendInput(1, [new Native.INPUT { type = 1, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0) | (ext ? 1u : 0) } } }], Marshal.SizeOf<Native.INPUT>());

    private static void Tap(ushort vk, params ushort[] mods)
    {
        foreach (var m in mods) Key(m, false);
        Key(vk, false, vk == 0x13 ? false : false); Key(vk, true);
        foreach (var m in mods.Reverse()) Key(m, true);
        Pump(60);
    }

    private static void TypeKeys(string usKeys)
    {
        foreach (var ch in usKeys)
        {
            ushort vk = ch switch { ' ' => 0x20, ',' => 0xBC, '.' => 0xBE, ';' => 0xBA, '\'' => 0xDE, '[' => 0xDB, ']' => 0xDD, _ => char.ToUpperInvariant(ch) };
            if (char.IsUpper(ch)) Tap(vk, 0xA0); else Tap(vk);
        }
        Pump(250);
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
        void Reset() { tb.Clear(); Tap(0x23); Pump(100); } // End key resets the engine's buffer

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
        Expect("learned exception", settings.ExceptionWords.Contains("руддщ").ToString(), "True");

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

        form.Close();
        File.Delete(Settings.FilePath);
        Console.WriteLine(failed == 0 ? "E2E: all passed" : $"E2E: {failed} failed");
        return failed;
    }
}
