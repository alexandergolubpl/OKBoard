using System.Diagnostics;

namespace OKBoard;

internal sealed class SettingsForm : Form
{
    public const int LogTab = 4;

    private readonly Settings _s;
    private readonly Engine _engine;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<(CheckBox box, Action<bool> set)> _checks = [];
    private readonly Dictionary<HotkeyAction, HotkeyBox> _hotkeyBoxes = new();
    private readonly CheckBox _autostart = new() { Text = "Запускать вместе с Windows", AutoSize = true };
    private readonly TextBox _apps = MultiLine(), _words = MultiLine(), _appLayouts = MultiLine();
    private readonly ListView _log = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };

    private static readonly Dictionary<HotkeyAction, string> HotkeyNames = new()
    {
        [HotkeyAction.ConvertLastWord] = "Исправить раскладку последнего слова (или выделения)",
        [HotkeyAction.ConvertPhrase] = "Исправить раскладку всей набранной фразы",
        [HotkeyAction.ConvertSelection] = "Исправить раскладку выделенного текста",
        [HotkeyAction.CycleCase] = "Регистр: строчные → ПРОПИСНЫЕ → Как В Заголовке",
        [HotkeyAction.Transliterate] = "Транслитерация (привет ⇄ privet)",
        [HotkeyAction.ToggleDoNotDisturb] = "Режим «Не беспокоить» вкл/выкл",
        [HotkeyAction.AddException] = "Добавить последнее слово в исключения",
    };

    public SettingsForm(Settings settings, Engine engine)
    {
        _s = settings;
        _engine = engine;
        Text = "OKBoard — настройки";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(760, 560);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _tabs.TabPages.Add(GeneralTab());
        _tabs.TabPages.Add(HotkeysTab());
        _tabs.TabPages.Add(ExceptionsTab());
        _tabs.TabPages.Add(AppLayoutsTab());
        _tabs.TabPages.Add(LogTabPage());

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        var ok = new Button { Text = "Сохранить", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        ok.Click += (_, _) => { Apply(); Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var folder = new LinkLabel { Text = "Папка настроек", AutoSize = true, Margin = new Padding(3, 8, 20, 3) };
        folder.LinkClicked += (_, _) => { Directory.CreateDirectory(Settings.Dir); Process.Start("explorer.exe", Settings.Dir); };
        buttons.Controls.Add(folder);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_tabs);
        Controls.Add(buttons);
    }

    public void SelectTab(int i) => _tabs.SelectedIndex = Math.Clamp(i, 0, _tabs.TabCount - 1);

    private static TextBox MultiLine() => new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true,
        Font = new Font("Consolas", 10f),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(680, 0), Margin = new Padding(24, 0, 3, 8),
    };

    private void AddCheck(FlowLayoutPanel panel, string text, string? hint, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
        _checks.Add((cb, set));
        panel.Controls.Add(cb);
        if (hint != null) panel.Controls.Add(Hint(hint));
    }

    private TabPage GeneralTab()
    {
        var page = new TabPage("Основные");
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        AddCheck(p, "Автоматически исправлять слова, набранные не в той раскладке", "ghbdtn → привет, руддщ → hello. Срабатывает на пробеле/Enter и только когда уверен: слово должно быть в словаре Windows.", _s.AutoSwitch, v => _s.AutoSwitch = v);
        AddCheck(p, "Отменять автоисправление клавишей Backspace", "Сразу после исправления Backspace вернёт исходное слово и раскладку.", _s.UndoWithBackspace, v => _s.UndoWithBackspace = v);
        AddCheck(p, "Запоминать отменённые исправления как исключения", "Если отменить исправление, это слово больше не будет исправляться.", _s.LearnFromUndo, v => _s.LearnFromUndo = v);
        AddCheck(p, "Исправлять случайно нажатый Caps Lock (пРИВЕТ → Привет)", null, _s.FixCapsLockAccident, v => _s.FixCapsLockAccident = v);
        AddCheck(p, "Исправлять ДВе заглавные буквы в начале слова", null, _s.FixDoubleCapital, v => _s.FixDoubleCapital = v);
        AddCheck(p, "Двойное нажатие Shift исправляет последнее слово", "Удобно на ноутбуках без клавиши Pause/Break.", _s.DoubleShiftConverts, v => _s.DoubleShiftConverts = v);
        AddCheck(p, "Caps Lock переключает раскладку (Shift+Caps Lock — обычный Caps Lock)", null, _s.CapsLockSwitchesLayout, v => _s.CapsLockSwitchesLayout = v);
        AddCheck(p, "Пауза в полноэкранных приложениях (игры, презентации)", "В этом режиме отключаются и автоисправление, и горячие клавиши.", _s.PauseInFullscreen, v => _s.PauseInFullscreen = v);
        AddCheck(p, "Не работать в полях ввода пароля", null, _s.SkipPasswordFields, v => _s.SkipPasswordFields = v);
        AddCheck(p, "Не исправлять одиночное слово перед Enter/Tab", "Так вводят пароли и логины в браузерах, где поле пароля не распознать. Исправить вручную можно горячей клавишей.", _s.SkipSingleWordBeforeEnter, v => _s.SkipSingleWordBeforeEnter = v);
        AddCheck(p, "Оставлять текст выделенным после преобразования", "Позволяет нажимать горячую клавишу регистра несколько раз подряд.", _s.ReselectAfterConvert, v => _s.ReselectAfterConvert = v);
        AddCheck(p, "Тихий звук при автоисправлении", null, _s.SoundOnAutoSwitch, v => _s.SoundOnAutoSwitch = v);
        try { _autostart.Checked = Autostart.Enabled; } catch { }
        _autostart.Margin = new Padding(3, 12, 3, 3);
        p.Controls.Add(_autostart);
        page.Controls.Add(p);
        return page;
    }

    private TabPage HotkeysTab()
    {
        var page = new TabPage("Горячие клавиши");
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(12) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (action, name) in HotkeyNames)
        {
            var box = new HotkeyBox(_engine) { Width = 180, Text = _s.Hotkeys.GetValueOrDefault(action, "") };
            var clear = new Button { Text = "✕", Width = 32, Height = box.Height + 2 };
            clear.Click += (_, _) => box.Text = "";
            _hotkeyBoxes[action] = box;
            t.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            t.Controls.Add(box);
            t.Controls.Add(clear);
        }
        var reset = new Button { Text = "По умолчанию", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        reset.Click += (_, _) =>
        {
            foreach (var (a, v) in Settings.DefaultHotkeys())
                if (_hotkeyBoxes.TryGetValue(a, out var b)) b.Text = v;
        };
        t.Controls.Add(reset);
        t.SetColumnSpan(reset, 3);
        var hint = Hint("Щёлкните в поле и нажмите сочетание. Backspace/Delete — очистить. " +
                        "Если ничего не набрано (например, после щелчка мышью), действия «последнее слово», регистр и транслитерация " +
                        "применяются к выделенному тексту.");
        hint.Margin = new Padding(3, 8, 3, 3);
        t.Controls.Add(hint);
        t.SetColumnSpan(hint, 3);
        page.Controls.Add(t);
        return page;
    }

    private TabPage ExceptionsTab()
    {
        var page = new TabPage("Исключения");
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        split.Controls.Add(new Label { Text = "Приложения без автопереключения (по одному на строку).\nГорячие клавиши в них продолжают работать.", AutoSize = true }, 0, 0);
        split.Controls.Add(new Label { Text = "Слова, которые никогда не исправлять\n(в любой раскладке, по одному на строку).", AutoSize = true }, 1, 0);
        _apps.Text = string.Join(Environment.NewLine, _s.ExcludedApps);
        _words.Text = string.Join(Environment.NewLine, _s.ExceptionWords);
        split.Controls.Add(_apps, 0, 1);
        split.Controls.Add(_words, 1, 1);

        var addRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var running = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        running.Items.AddRange(RunningApps());
        var add = new Button { Text = "Добавить", AutoSize = true };
        add.Click += (_, _) =>
        {
            if (running.SelectedItem is string s && !_apps.Lines.Contains(s, StringComparer.OrdinalIgnoreCase))
                _apps.AppendText((_apps.Text.Length > 0 && !_apps.Text.EndsWith('\n') ? Environment.NewLine : "") + s);
        };
        addRow.Controls.Add(running);
        addRow.Controls.Add(add);
        split.Controls.Add(addRow, 0, 2);
        page.Controls.Add(split);
        return page;
    }

    private TabPage AppLayoutsTab()
    {
        var page = new TabPage("Раскладка приложений");
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0),
            Text = "При переключении на приложение сразу включать нужную раскладку. Формат: программа.exe = en или ru\n" +
                   "Например:  Telegram.exe = ru,  Code.exe = en,  WindowsTerminal.exe = en",
        });
        _appLayouts.Text = string.Join(Environment.NewLine, _s.AppLayouts.Select(kv => $"{kv.Key} = {kv.Value}"));
        t.Controls.Add(_appLayouts);
        page.Controls.Add(t);
        return page;
    }

    private TabPage LogTabPage()
    {
        var page = new TabPage("Журнал");
        _log.Columns.Add("Время", 70);
        _log.Columns.Add("Приложение", 140);
        _log.Columns.Add("Было", 150);
        _log.Columns.Add("Стало", 150);
        _log.Columns.Add("Что", 170);
        foreach (var e in _engine.Log)
            _log.Items.Add(new ListViewItem([e.Time.ToString("HH:mm:ss"), e.App, e.From, e.To, e.Kind]) { Tag = e });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var never = new Button { Text = "Это было ошибкой — не исправлять такое слово", AutoSize = true };
        never.Click += (_, _) =>
        {
            foreach (ListViewItem item in _log.SelectedItems)
                if (item.Tag is CorrectionEntry e)
                {
                    var w = e.From.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '\'').ToLowerInvariant();
                    if (w.Length > 0 && !_words.Lines.Contains(w)) _words.AppendText((_words.Text.Length > 0 ? Environment.NewLine : "") + w);
                }
            _tabs.SelectedIndex = 2;
        };
        bar.Controls.Add(never);
        bar.Controls.Add(new Label { Text = "Журнал хранится только в памяти до выхода из программы.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 8, 3, 3) });
        page.Controls.Add(_log);
        page.Controls.Add(bar);
        return page;
    }

    private static object[] RunningApps()
    {
        var self = Environment.ProcessId;
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    names.Add(Native.GetProcessName(p.MainWindowHandle) ?? p.ProcessName + ".exe");
                }
                catch { }
            }
        }
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
    }

    private static List<string> Lines(TextBox tb) =>
        tb.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Apply()
    {
        foreach (var (box, set) in _checks) set(box.Checked);
        foreach (var (action, box) in _hotkeyBoxes) _s.Hotkeys[action] = box.Text;
        _s.ExcludedApps = Lines(_apps);
        _s.ExceptionWords = Lines(_words).Select(w => w.ToLowerInvariant()).Distinct().ToList();
        _s.AppLayouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(_appLayouts))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].ToLowerInvariant() is "en" or "ru") _s.AppLayouts[parts[0]] = parts[1].ToLowerInvariant();
        }
        try { Autostart.Enabled = _autostart.Checked; } catch { }
        _s.Save();
        _engine.ReloadSettings();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.Suspended = false;
        base.OnFormClosed(e);
    }
}

/// <summary>Text box that records a key combination. Suspends the global hook while focused.</summary>
internal sealed class HotkeyBox : TextBox
{
    private readonly Engine _engine;

    public HotkeyBox(Engine engine)
    {
        _engine = engine;
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
    }

    protected override void OnEnter(EventArgs e) { _engine.Suspended = true; base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { _engine.Suspended = false; base.OnLeave(e); }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var key = e.KeyCode == Keys.Cancel ? Keys.Pause : e.KeyCode;
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        bool win = Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C);
        if (!e.Control && !e.Alt && !e.Shift && !win && key is Keys.Back or Keys.Delete or Keys.Escape)
        {
            Text = "";
            return;
        }
        if (key == Keys.Tab && !e.Control && !e.Alt) { Parent?.SelectNextControl(this, !e.Shift, true, true, true); return; }
        var hk = new Hotkey(key, e.Control, e.Alt, e.Shift, win);
        if (!hk.IsSafe)
        {
            // e.g. a bare letter: it would be swallowed everywhere and become impossible to type
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        Text = hk.ToString();
    }

    // Alt combinations arrive as system keys
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Alt) != 0)
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
