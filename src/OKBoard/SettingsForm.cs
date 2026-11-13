using System.Diagnostics;

namespace OKBoard;

internal sealed class SettingsForm : Form
{
    public const int LogTab = 6;
    public const int RulesTab = 2;
    private const int ExceptionsTabIndex = 4;

    private readonly Settings _s;
    private readonly Engine _engine;
    private readonly List<Page> _pages = [];
    private readonly List<NavButton> _nav = [];
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 28, 0) };
    private NavButton? _themeButton;
    private readonly List<(CheckBox box, Action<bool> set)> _checks = [];
    private readonly Dictionary<HotkeyAction, HotkeyBox> _hotkeyBoxes = new();
    private readonly FluentCheckBox _autostart = new() { Text = "Запускать вместе с Windows", AutoSize = true };
    private readonly TextBox _apps = MultiLine(), _words = MultiLine(), _appLayouts = MultiLine(), _rulesBox = MultiLine();
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
        [HotkeyAction.ClipboardHistory] = "История буфера обмена",
    };

    private readonly Action _applyHistory;
    private readonly List<Snippet> _snips;
    private readonly NumericUpDown _historySize = new() { Minimum = 5, Maximum = 200, Width = 70 };
    private readonly RoundCombo _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };

    public SettingsForm(Settings settings, Engine engine, Action applyHistory)
    {
        _s = settings;
        _engine = engine;
        _applyHistory = applyHistory;
        _snips = settings.Snippets.Select(x => new Snippet { Abbr = x.Abbr, Text = x.Text }).ToList();
        // Everything below is laid out in 96-DPI units; WinForms scales it to the monitor on ResumeLayout.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "OKBoard — настройки";
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(1000, 680);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        foreach (var page in new[] { GeneralTab(), HotkeysTab(), RulesTabPage(), SnippetsTab(), ExceptionsTab(), AppLayoutsTab(), LogTabPage() })
        {
            _pages.Add(page);
            page.Visible = false;
            _content.Controls.Add(page);
        }

        // ── navigation pane (ShareX / Windows 11 style) ──
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 236, Padding = new Padding(8, 10, 8, 10), Tag = "sidebar" };
        var brand = new BrandHeader { Dock = DockStyle.Top, Height = 56 };
        var navHost = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        for (int i = 0; i < _pages.Count; i++)
        {
            int idx = i;
            var nb = new NavButton(_pages[i].Glyph, _pages[i].Title) { Width = 218 };
            nb.Click += (_, _) => SelectTab(idx);
            _nav.Add(nb);
            navHost.Controls.Add(nb);
        }
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        bottom.Controls.Add(new Divider { Width = 218 });
        _themeButton = new NavButton(Theme.GMoon, "") { Width = 218 };
        _themeButton.Click += (_, _) => ToggleTheme();
        bottom.Controls.Add(_themeButton);
        var folder = new NavButton(Theme.GFolder, "Папка настроек") { Width = 218 };
        folder.Click += (_, _) => { Directory.CreateDirectory(Settings.Dir); Process.Start("explorer.exe", Settings.Dir); };
        bottom.Controls.Add(folder);
        sidebar.Controls.Add(navHost);
        sidebar.Controls.Add(bottom);
        sidebar.Controls.Add(brand);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(20, 10, 20, 14) };
        var ok = new RoundButton { Text = "Сохранить", AutoSize = true, MinimumSize = new Size(120, 34), Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new RoundButton { Text = "Отмена", AutoSize = true, MinimumSize = new Size(110, 34) };
        ok.Click += (_, _) => { Apply(); Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_content);
        Controls.Add(buttons);
        Controls.Add(new Divider { Dock = DockStyle.Left, Width = 1, Vertical = true });
        Controls.Add(sidebar);

        SelectTab(0);
        Theme.WrapInputs(this);
        ResumeLayout(true);
        ApplyTheme();
        Theme.Changed += ApplyTheme;
        HandleCreated += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        if (_themeButton != null)
        {
            _themeButton.Glyph = Theme.P.Dark ? Theme.GSun : Theme.GMoon;
            _themeButton.Text = Theme.P.Dark ? "Светлая тема" : "Тёмная тема";
        }
        Theme.Apply(this);
        Invalidate(true);
    }

    private void ToggleTheme()
    {
        var mode = Theme.P.Dark ? ThemeMode.Light : ThemeMode.Dark;
        _s.Theme = mode.ToString().ToLowerInvariant();
        try { _s.Save(); } catch { }
        Theme.Set(mode);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Theme.Changed -= ApplyTheme;
        _engine.Resume();
        base.OnFormClosed(e);
    }

    public void SelectTab(int i)
    {
        i = Math.Clamp(i, 0, _pages.Count - 1);
        for (int k = 0; k < _pages.Count; k++)
        {
            _pages[k].Visible = k == i;
            _nav[k].Selected = k == i;
        }
        if (IsHandleCreated) Theme.Apply(_pages[i]); // pages get their window handles (dark scroll bars) when first shown
    }

    private static TextBox MultiLine() => new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true,
        Font = new Font("Consolas", 10f),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, Tag = "hint", MaximumSize = new Size(640, 0), Margin = new Padding(26, 0, 3, 10),
    };

    private void AddCheck(FlowLayoutPanel panel, string text, string? hint, bool value, Action<bool> set)
    {
        var cb = new FluentCheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
        _checks.Add((cb, set));
        panel.Controls.Add(cb);
        if (hint != null) panel.Controls.Add(Hint(hint));
    }

    private Page GeneralTab()
    {
        var page = new Page("Основные", Theme.GSettings);
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 0, 12) };
        AddCheck(p, "Автоматически исправлять слова, набранные не в той раскладке", "ghbdtn → привет, руддщ → hello. Срабатывает на пробеле/Enter и только когда уверен: слово должно быть в словаре Windows.", _s.AutoSwitch, v => _s.AutoSwitch = v);
        AddCheck(p, "Отменять автоисправление клавишей Backspace", "В течение 3 секунд после исправления Backspace вернёт исходное слово и раскладку.", _s.UndoWithBackspace, v => _s.UndoWithBackspace = v);
        AddCheck(p, "Запоминать отменённые исправления как исключения", "Если исправление одного и того же слова отменить дважды (Backspace сразу после исправления или Pause), " +
            "слово больше не будет исправляться. Список и удаление — на вкладке «Исключения».", _s.LearnFromUndo, v => _s.LearnFromUndo = v);
        var levelRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 3, 2) };
        levelRow.Controls.Add(new Label { Text = "Активность:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
        _level.Items.AddRange(["Осторожно — только словарные слова", "Обычно", "Активно — как Caramba и Ninja (рекомендуется)"]);
        _level.SelectedIndex = Math.Clamp(_s.Aggressiveness, 0, 2);
        levelRow.Controls.Add(_level);
        p.Controls.Add(levelRow);
        p.Controls.Add(Hint("Активно: исправляются и трёхбуквенные слова, и одиночное слово перед Enter (если получается " +
                            "настоящее слово), учитывается язык предыдущего слова фразы."));
        AddCheck(p, "Исправлять уже во время набора (активный режим)",
            "Как только по первым 4–7 буквам ясно, что это начало частого слова в другой раскладке (ghbd → прив…), " +
            "набранное переводится и раскладка переключается — остальное слово вы печатаете уже правильно.",
            _s.SwitchWhileTyping, v => _s.SwitchWhileTyping = v);
        AddCheck(p, "Не исправлять слово, если раскладку только что переключили вручную",
            "Переключили сами (Alt+Shift, Win+Пробел) — значит, знаете, что печатаете: следующее слово не трогается.",
            _s.TrustManualSwitch, v => _s.TrustManualSwitch = v);
        AddCheck(p, "Исправлять и короткое слово перед исправленным (z ndjq → я твой)",
            "Одно-трёхбуквенные слова сами по себе не исправляются (слишком мало букв), а вместе со следующим словом — да.",
            _s.FixShortWordBefore, v => _s.FixShortWordBefore = v);
        AddCheck(p, "Учиться на исправлениях вручную (Pause)",
            "Исправили вручную слово из исключений — оно убирается из исключений. Исправили одно и то же слово дважды — " +
            "появляется правило всегда его исправлять.",
            _s.LearnFromManual, v => _s.LearnFromManual = v);
        AddCheck(p, "Исправлять случайно нажатый Caps Lock (пРИВЕТ → Привет)", null, _s.FixCapsLockAccident, v => _s.FixCapsLockAccident = v);
        AddCheck(p, "Исправлять ДВе заглавные буквы в начале слова", null, _s.FixDoubleCapital, v => _s.FixDoubleCapital = v);
        AddCheck(p, "Двойное нажатие Shift исправляет последнее слово", "Удобно на ноутбуках без клавиши Pause/Break.", _s.DoubleShiftConverts, v => _s.DoubleShiftConverts = v);
        AddCheck(p, "Caps Lock переключает раскладку (Shift+Caps Lock — обычный Caps Lock)", null, _s.CapsLockSwitchesLayout, v => _s.CapsLockSwitchesLayout = v);
        AddCheck(p, "Пауза в полноэкранных приложениях (игры, презентации)", "В этом режиме отключаются и автоисправление, и горячие клавиши.", _s.PauseInFullscreen, v => _s.PauseInFullscreen = v);
        AddCheck(p, "Не работать в полях ввода пароля", null, _s.SkipPasswordFields, v => _s.SkipPasswordFields = v);
        AddCheck(p, "Не исправлять одиночное слово перед Enter/Tab", "Так вводят пароли и логины в браузерах, где поле пароля не распознать. Исправить вручную можно горячей клавишей.", _s.SkipSingleWordBeforeEnter, v => _s.SkipSingleWordBeforeEnter = v);
        AddCheck(p, "Оставлять текст выделенным после преобразования", "Позволяет нажимать горячую клавишу регистра несколько раз подряд.", _s.ReselectAfterConvert, v => _s.ReselectAfterConvert = v);
        AddCheck(p, "Тихий звук при автоисправлении", null, _s.SoundOnAutoSwitch, v => _s.SoundOnAutoSwitch = v);
        AddCheck(p, "Вести историю буфера обмена (Ctrl+Alt+V)",
            "Только текст и только в памяти — после выхода из программы история исчезает. Не записывается то, что " +
            "менеджеры паролей помечают как приватное, и то, что скопировано в приложениях-исключениях.",
            _s.ClipboardHistoryEnabled, v => _s.ClipboardHistoryEnabled = v);
        var sizeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(20, 0, 3, 6) };
        sizeRow.Controls.Add(new Label { Text = "Хранить записей:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _historySize.Value = Math.Clamp(_s.ClipboardHistorySize, 5, 200);
        sizeRow.Controls.Add(_historySize);
        p.Controls.Add(sizeRow);
        AddCheck(p, "Обновляться автоматически с GitHub",
            "Раз в 12 часов программа проверяет релизы на GitHub, скачивает установщик, сверяет его контрольную сумму " +
            "и устанавливает, когда вы минуту не пользуетесь клавиатурой. Портативная копия только сообщает о новой версии.",
            _s.AutoUpdate, v => _s.AutoUpdate = v);
        try { _autostart.Checked = Autostart.Enabled; } catch { }
        _autostart.Margin = new Padding(3, 12, 3, 3);
        p.Controls.Add(_autostart);
        p.Resize += (_, _) =>
        {
            int w = Math.Max(200, p.ClientSize.Width - p.LogicalToDeviceUnits(60));
            foreach (Control c in p.Controls) if (c is Label { Tag: "hint" }) c.MaximumSize = new Size(w, 0);
        };
        page.Controls.Add(p);
        return page;
    }

    private Page HotkeysTab()
    {
        var page = new Page("Горячие клавиши", Theme.GKeyboard);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (action, name) in HotkeyNames)
        {
            var box = new HotkeyBox(_engine) { Width = 180, Text = _s.Hotkeys.GetValueOrDefault(action, "") };
            var clear = new RoundButton { Text = "✕", Width = 34, Height = box.Height + 2 };
            clear.Click += (_, _) => box.Text = "";
            _hotkeyBoxes[action] = box;
            t.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            t.Controls.Add(box);
            t.Controls.Add(clear);
        }
        var reset = new RoundButton { Text = "По умолчанию", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
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

    private Page SnippetsTab()
    {
        var page = new Page("Автозамена", Theme.GEdit);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 0, 0, 8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var enabled = new FluentCheckBox { Text = "Заменять сокращения после пробела или Enter", Checked = _s.SnippetsEnabled, AutoSize = true };
        _checks.Add((enabled, v => _s.SnippetsEnabled = v));
        var head = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        head.Controls.Add(enabled);
        head.Controls.Add(Hint("Сокращение срабатывает в любой раскладке («мк» сработает, даже если набрано как «vr»). " +
                               "Backspace сразу после замены вернёт сокращение. Можно использовать {date}, {time}, {datetime}. " +
                               "Многострочный текст вставляется через буфер обмена (ваш буфер сохраняется)."));
        t.Controls.Add(head, 0, 0);
        t.SetColumnSpan(head, 2);

        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        var abbr = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Сокращение, например: мк" };
        var text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "Полный текст. Например:\r\nС уважением,\r\nАлександр",
        };
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(text);
        right.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        right.Controls.Add(abbr);
        t.Controls.Add(list, 0, 1);
        t.Controls.Add(right, 1, 1);

        bool loading = false;
        string Display(Snippet x)
        {
            var first = x.Text.Replace("\r\n", "\n").Split('\n')[0];
            if (first.Length > 30) first = first[..30] + "…";
            return $"{(x.Abbr.Length > 0 ? x.Abbr : "(новое)")} → {first}";
        }
        void Rebuild(int select)
        {
            loading = true;
            list.Items.Clear();
            foreach (var x in _snips) list.Items.Add(Display(x));
            list.SelectedIndex = Math.Min(select, list.Items.Count - 1);
            loading = false;
            LoadCurrent();
        }
        void LoadCurrent()
        {
            loading = true;
            var cur = list.SelectedIndex >= 0 ? _snips[list.SelectedIndex] : null;
            abbr.Text = cur?.Abbr ?? "";
            text.Text = cur?.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) ?? "";
            abbr.Enabled = text.Enabled = cur != null;
            loading = false;
        }
        void Store()
        {
            if (loading || list.SelectedIndex < 0) return;
            var cur = _snips[list.SelectedIndex];
            cur.Abbr = abbr.Text.Trim();
            cur.Text = text.Text.Replace("\r\n", "\n");
            loading = true;
            list.Items[list.SelectedIndex] = Display(cur);
            loading = false;
        }
        list.SelectedIndexChanged += (_, _) => { if (!loading) LoadCurrent(); };
        abbr.TextChanged += (_, _) => Store();
        text.TextChanged += (_, _) => Store();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var add = new RoundButton { Text = "Добавить", AutoSize = true };
        var del = new RoundButton { Text = "Удалить", AutoSize = true };
        add.Click += (_, _) => { _snips.Add(new Snippet()); Rebuild(_snips.Count - 1); abbr.Focus(); };
        del.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            int i = list.SelectedIndex;
            _snips.RemoveAt(i);
            Rebuild(Math.Max(0, i - 1));
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(del);
        t.Controls.Add(buttons, 0, 2);
        t.SetColumnSpan(buttons, 2);

        Rebuild(0);
        page.Controls.Add(t);
        return page;
    }

    private Page ExceptionsTab()
    {
        var page = new Page("Исключения", Theme.GBlock);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 0, 0, 8) };
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
        var running = new RoundCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        running.Items.AddRange(RunningApps());
        var add = new RoundButton { Text = "Добавить", AutoSize = true };
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

    private Page RulesTabPage()
    {
        var page = new Page("Правила", Theme.GRules);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(0, 0, 0, 8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var help = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(3, 0, 3, 10), Text = RuleParser.Help };
        t.Controls.Add(help);
        _rulesBox.Text = string.Join(Environment.NewLine, _s.Rules);
        t.Controls.Add(_rulesBox);
        var status = new Label { AutoSize = true, Tag = "hint", MaximumSize = new Size(760, 0), Margin = new Padding(3, 8, 3, 3) };
        void Validate()
        {
            var errors = new List<string>();
            var n = RuleParser.Parse(_rulesBox.Lines, errors).Count;
            status.Text = errors.Count == 0
                ? $"Правил: {n}. Порядок проверки: правила «нет» → правила «да» → исключения → словарь."
                : $"Правил: {n}. Не понятно ({errors.Count}): " + string.Join("; ", errors.Take(3));
        }
        _rulesBox.TextChanged += (_, _) => Validate();
        Validate();
        t.Controls.Add(status);
        t.Resize += (_, _) =>
        {
            var w = new Size(Math.Max(200, t.ClientSize.Width - t.LogicalToDeviceUnits(12)), 0);
            help.MaximumSize = w;
            status.MaximumSize = w;
        };
        page.Controls.Add(t);
        return page;
    }

    private Page AppLayoutsTab()
    {
        var page = new Page("Раскладка приложений", Theme.GApps);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 0, 0, 8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0),
            Text = "При переключении на приложение сразу включать нужную раскладку. Формат: программа.exe = en или ru\n" +
                   "Например:  Telegram.exe = ru,  Viber.exe = uk,  Code.exe = en,  WindowsTerminal.exe = en",
        });
        _appLayouts.Text = string.Join(Environment.NewLine, _s.AppLayouts.Select(kv => $"{kv.Key} = {kv.Value}"));
        t.Controls.Add(_appLayouts);
        page.Controls.Add(t);
        return page;
    }

    private Page LogTabPage()
    {
        var page = new Page("Журнал", Theme.GHistory);
        _log.Columns.Add("Время", 90);
        _log.Columns.Add("Приложение", 140);
        _log.Columns.Add("Было", 150);
        _log.Columns.Add("Стало", 150);
        _log.Columns.Add("Что", 170);
        foreach (var e in _engine.Log)
            _log.Items.Add(new ListViewItem([e.Time.ToString("HH:mm:ss"), e.App, e.From, e.To, e.Kind]) { Tag = e });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var never = new RoundButton { Text = "Это было ошибкой — не исправлять такое слово", AutoSize = true };
        never.Click += (_, _) =>
        {
            foreach (ListViewItem item in _log.SelectedItems)
                if (item.Tag is CorrectionEntry e)
                {
                    var w = e.From.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '\'').ToLowerInvariant();
                    if (w.Length > 0 && !_words.Lines.Contains(w)) _words.AppendText((_words.Text.Length > 0 ? Environment.NewLine : "") + w);
                }
            SelectTab(ExceptionsTabIndex);
        };
        bar.Controls.Add(never);
        bar.Controls.Add(new Label { Text = "Журнал хранится только в памяти до выхода из программы.", AutoSize = true, Tag = "hint", Margin = new Padding(8, 8, 3, 3) });
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
            if (parts.Length == 2 && Settings.ParseLang(parts[1]) is { } lang) _s.AppLayouts[parts[0]] = lang.ToString().ToLowerInvariant();
        }
        _s.ClipboardHistorySize = (int)_historySize.Value;
        _s.Aggressiveness = _level.SelectedIndex;
        _s.Rules = _rulesBox.Lines.Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        _s.Snippets = _snips
            .Where(x => !string.IsNullOrWhiteSpace(x.Abbr) && x.Text.Length > 0)
            .GroupBy(x => x.Abbr.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new Snippet { Abbr = g.Key, Text = g.First().Text.Replace("\r\n", "\n") })
            .ToList();
        try { Autostart.Enabled = _autostart.Checked; } catch { }
        _s.Save();
        _engine.ReloadSettings();
        _applyHistory();
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
        ShortcutsEnabled = false;
    }

    protected override void OnEnter(EventArgs e) { _engine.SuspendFor(Handle); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { _engine.Resume(); base.OnLeave(e); }

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

/// <summary>One section of the settings window: a big title and its content.</summary>
internal sealed class Page : Panel
{
    public string Title { get; }
    public char Glyph { get; }

    public Page(string title, char glyph)
    {
        Title = title;
        Glyph = glyph;
        Dock = DockStyle.Fill;
        Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Top, AutoSize = false, Height = 50,
            Font = new Font("Segoe UI Semibold", 17f), TextAlign = ContentAlignment.TopLeft,
        });
    }

    // Fill controls must dock last (be at the front of the z-order), whatever order they were added in.
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control!.Dock == DockStyle.Fill) e.Control.BringToFront();
    }
}

/// <summary>Navigation entry: icon glyph + text, rounded highlight, accent bar when selected.</summary>
internal sealed class NavButton : Control
{
    private bool _hover, _selected;
    private char _glyph;

    public NavButton(char glyph, string text)
    {
        _glyph = glyph;
        Text = text;
        Height = 40;
        Margin = new Padding(0, 1, 0, 1);
        Cursor = Cursors.Hand;
        Tag = "keep";
        Font = new Font("Segoe UI", 10.5f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public char Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(p.Sidebar);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_selected || _hover)
        {
            using var path = Theme.RoundRect(new RectangleF(2, 2, Width - 4, Height - 4), 5);
            using var b = new SolidBrush(_selected ? p.Selected : p.Hover);
            g.FillPath(b, path);
        }
        if (_selected)
        {
            using var bar = Theme.RoundRect(new RectangleF(3, Height / 2f - 9, 3, 18), 1.5f);
            using var ab = new SolidBrush(p.Accent);
            g.FillPath(ab, bar);
        }
        int icon = LogicalToDeviceUnits(36);
        Theme.DrawGlyph(g, _glyph, new Rectangle(LogicalToDeviceUnits(6), 0, icon, Height), p.Accent, LogicalToDeviceUnits(17));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(icon + LogicalToDeviceUnits(10), 0, Width - icon, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>App icon + name + version at the top of the navigation pane.</summary>
internal sealed class BrandHeader : Control
{
    public BrandHeader()
    {
        Tag = "keep";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        e.Graphics.Clear(p.Sidebar);
        int s = LogicalToDeviceUnits(28);
        using (var icon = TrayApp.RenderIcon("OK", Lang.En, IconState.Active, s))
            e.Graphics.DrawImage(icon, LogicalToDeviceUnits(10), (Height - s) / 2);
        var v = Application.ProductVersion.Split('+')[0];
        using var f = new Font("Segoe UI Semibold", 13f);
        TextRenderer.DrawText(e.Graphics, $"OKBoard {v}", f, new Rectangle(s + LogicalToDeviceUnits(20), 0, Width, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>1-px line in the border colour.</summary>
internal sealed class Divider : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Vertical { get; set; }

    public Divider()
    {
        Tag = "keep";
        Height = 9;
        Margin = new Padding(0, 4, 0, 4);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        e.Graphics.Clear(Vertical ? p.Border : p.Sidebar);
        if (Vertical) return;
        using var pen = new Pen(p.Border);
        e.Graphics.DrawLine(pen, 8, Height / 2, Width - 8, Height / 2);
    }
}

/// <summary>Check box drawn in the Windows 11 style: rounded square, accent fill and white tick when checked.</summary>
internal sealed class FluentCheckBox : CheckBox
{
    private bool _hover;

    public FluentCheckBox()
    {
        Tag = "keep";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(6, 0, 0, 0);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        int box = LogicalToDeviceUnits(20);
        return new Size(box + LogicalToDeviceUnits(10) + t.Width + 2, Math.Max(box, t.Height) + LogicalToDeviceUnits(6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor is { A: 255 } c ? c : p.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int box = LogicalToDeviceUnits(20);
        var r = new RectangleF(1, (Height - box) / 2f, box - 2, box - 2);
        using var path = Theme.RoundRect(r, LogicalToDeviceUnits(4));
        if (Checked)
        {
            using var b = new SolidBrush(_hover ? ControlPaint.Light(p.Accent, 0.1f) : p.Accent);
            g.FillPath(b, path);
            Theme.DrawGlyph(g, Theme.GCheck, Rectangle.Round(r), p.AccentText, LogicalToDeviceUnits(13));
        }
        else
        {
            using var b = new SolidBrush(_hover ? p.Hover : p.Surface);
            g.FillPath(b, path);
            using var pen = new Pen(p.SubText, LogicalToDeviceUnits(1));
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(box + LogicalToDeviceUnits(10), 0, Width - box, Height),
            Enabled ? p.Text : p.Disabled, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(p.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(fp, box + LogicalToDeviceUnits(8), 1, Width - box - LogicalToDeviceUnits(9), Height - 3);
        }
    }
}
