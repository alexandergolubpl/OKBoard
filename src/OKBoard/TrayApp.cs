using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace OKBoard;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "OKBoard";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
    }
}

internal enum IconState { Active, Off, Excluded }

internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly Engine _engine;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _autoItem, _dndMenu, _excludeItem, _autostartItem, _statusItem, _themeMenu;
    private readonly List<(ToolStripMenuItem item, char glyph)> _glyphItems = [];
    internal ContextMenuStrip Menu { get; }
    private string _iconKey = "";
    private Icon? _icon;
    private string? _lastApp;
    private IntPtr _lastAppHwnd;
    private SettingsForm? _settingsForm;
    private readonly ClipboardHistory _history;
    private readonly Updater _updater;
    private readonly ToolStripMenuItem _updateItem;
    private ClipboardPopup? _popup;

    public TrayApp(Settings settings, Engine engine)
    {
        _settings = settings;
        _engine = engine;
        _history = new ClipboardHistory(_engine.IsExcludedApp, () => _engine.Busy);
        ApplyHistorySettings();

        _statusItem = new ToolStripMenuItem("OKBoard") { Enabled = false };
        _autoItem = Item("Автопереключение раскладки", Theme.GSwitch, () =>
        {
            _settings.AutoSwitch = !_settings.AutoSwitch;
            _settings.Save();
            UpdateIcon(true);
        });
        _dndMenu = Item("Не беспокоить", Theme.GBellOff, null);
        _dndMenu.DropDownItems.AddRange([
            Item("Выключить", null, () => _engine.SetDnd(null)),
            Item("На 15 минут", null, () => _engine.SetDnd(TimeSpan.FromMinutes(15))),
            Item("На 1 час", null, () => _engine.SetDnd(TimeSpan.FromHours(1))),
            Item("На 3 часа", null, () => _engine.SetDnd(TimeSpan.FromHours(3))),
            Item("Пока не выключу", null, () => _engine.SetDnd(TimeSpan.MaxValue)),
        ]);
        _excludeItem = Item("Исключить приложение", Theme.GBlock, ToggleExcludeLastApp);
        _autostartItem = Item("Запускать вместе с Windows", Theme.GPlay, () =>
        {
            try { Autostart.Enabled = !Autostart.Enabled; } catch (Exception ex) { ShowTip(ex.Message); }
        });
        _updater = new Updater(_settings);
        _updateItem = Item("Проверить обновления", Theme.GSync, () =>
        {
            if (_updater.DownloadedInstaller != null) _updater.Install(silent: true);
            else _ = _updater.CheckAsync(manual: true);
        });
        _themeMenu = Item("Тема", Theme.GContrast, null);
        foreach (var (mode, label) in new[] { (ThemeMode.System, "Как в Windows"), (ThemeMode.Light, "Светлая"), (ThemeMode.Dark, "Тёмная") })
        {
            var m = mode;
            _themeMenu.DropDownItems.Add(Item(label, null, () => SetTheme(m)));
        }

        var menu = new ContextMenuStrip
        {
            Renderer = new ModernMenuRenderer(),
            Font = new Font("Segoe UI", 10f),
            ShowCheckMargin = false,
            ShowImageMargin = true,
            Padding = new Padding(0, 4, 0, 4),
        };
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _autoItem,
            _dndMenu,
            _excludeItem,
            new ToolStripSeparator(),
            Item("История буфера обмена…", Theme.GClipboard, () => ShowClipboardPopup(_lastAppHwnd, atCaret: false)),
            Item("Очистить историю буфера", Theme.GDelete, () => { _history.Clear(); ShowTip("История буфера обмена очищена"); }),
            new ToolStripSeparator(),
            Item("Настройки…", Theme.GSettings, () => ShowSettings(0)),
            Item("Журнал исправлений…", Theme.GHistory, () => ShowSettings(SettingsForm.LogTab)),
            Item("Диагностика…", Theme.GPulse, ShowDiagnostics),
            _themeMenu,
            _autostartItem,
            _updateItem,
            new ToolStripSeparator(),
            Item("Выход", Theme.GPower, ExitThread),
        ]);
        menu.Opening += (_, _) => RefreshMenu();
        menu.Opened += (_, _) => Theme.ApplyChrome(menu.Handle);
        foreach (var sub in new[] { _dndMenu, _themeMenu })
        {
            sub.DropDown.Renderer = menu.Renderer;
            sub.DropDown.Font = menu.Font;
            sub.DropDown.Padding = menu.Padding;
            sub.DropDown.Opened += (_, _) => Theme.ApplyChrome(sub.DropDown.Handle);
        }
        Menu = menu;
        _updater.Notify += ShowTip;
        _updater.Start();
        Theme.Changed += RefreshGlyphs;
        RefreshGlyphs();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "OKBoard" };
        _tray.DoubleClick += (_, _) => ShowSettings(0);
        _engine.Notify += ShowTip;
        _engine.StateChanged += () => UpdateIcon(false);
        _engine.ClipboardHistoryRequested += fg => ShowClipboardPopup(fg, atCaret: true);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => { TrackForegroundApp(); UpdateIcon(false); };
        _timer.Start();
        UpdateIcon(true);

        if (_engine.StartupNotice is { } notice) ShowTip(notice);
        var current = Updater.Current.ToString(3);
        if (_settings.LastRunVersion != current)
        {
            if (_settings.LastRunVersion.Length > 0) ShowTip($"OKBoard обновлён до версии {current}");
            _settings.LastRunVersion = current;
            try { _settings.Save(); } catch { }
        }
    }

    private ToolStripMenuItem Item(string text, char? glyph, Action? onClick)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(4, 6, 4, 6) };
        if (onClick != null) item.Click += (_, _) => onClick();
        if (glyph is { } g) _glyphItems.Add((item, g));
        return item;
    }

    /// <summary>Icons are glyphs tinted with the accent colour, re-rendered when the theme changes.</summary>
    private void RefreshGlyphs()
    {
        int size = Menu.LogicalToDeviceUnits(18);
        Menu.ImageScalingSize = new Size(size, size);
        foreach (var (item, g) in _glyphItems)
        {
            var old = item.Image;
            item.Image = Theme.Glyph(g, size, Theme.P.Accent);
            old?.Dispose();
        }
        _statusItem.Image?.Dispose();
        _statusItem.Image = RenderIcon("OK", Lang.En, IconState.Active, size);
        Menu.Invalidate();
    }

    private void SetTheme(ThemeMode mode)
    {
        _settings.Theme = mode.ToString().ToLowerInvariant();
        _settings.Save();
        Theme.Set(mode);
    }

    private void ShowDiagnostics()
    {
        var text = _engine.Diagnostics() + Environment.NewLine + Environment.NewLine +
                   $"Версия: {Application.ProductVersion}" + Environment.NewLine +
                   $"Журнал: {DiagLog.FilePath}" + Environment.NewLine + Environment.NewLine +
                   "«Да» переустановит перехват клавиатуры, «Нет» откроет папку с журналом.";
        var r = MessageBox.Show(text, "OKBoard: диагностика", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (r == DialogResult.Yes) { _engine.ReinstallHooks("manual (menu)"); ShowTip("Перехват клавиатуры переустановлен"); }
        else if (r == DialogResult.No)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagLog.FilePath)!);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(DiagLog.FilePath)}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void ShowTip(string text) => _tray.ShowBalloonTip(2000, "OKBoard", text, ToolTipIcon.None);

    private void TrackForegroundApp()
    {
        var fg = Native.GetForegroundWindow();
        var name = Native.GetProcessName(fg);
        if (name != null && !name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)
            && !name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            _lastApp = name;
            _lastAppHwnd = fg;
        }
    }

    public void ApplyHistorySettings()
    {
        _history.Enabled = _settings.ClipboardHistoryEnabled;
        _history.MaxItems = Math.Clamp(_settings.ClipboardHistorySize, 5, 200);
        if (!_history.Enabled) _history.Clear();
    }

    /// <summary>Show recent clipboard items next to the caret of <paramref name="target"/>; the pick is pasted there.</summary>
    private void ShowClipboardPopup(IntPtr target, bool atCaret)
    {
        if (!_settings.ClipboardHistoryEnabled)
        {
            ShowTip("История буфера обмена выключена в настройках");
            return;
        }
        _popup?.Close();
        var pos = Cursor.Position;
        if (atCaret && CaretPosition(target) is { } caret) pos = caret;

        var popup = new ClipboardPopup(_history, text => _ = _engine.PasteFromHistory(text, target));
        popup.FormClosed += (_, _) =>
        {
            _engine.Resume();
            if (!popup.WasPicked && target != IntPtr.Zero) Native.SetForegroundWindow(target);
            if (_popup == popup) _popup = null;
        };
        _popup = popup;
        _engine.SuspendFor(popup.Handle); // don't auto-correct what is typed into the search box
        InputSender.Tap(0xE8);    // counts as "last input", which lets us take the foreground
        popup.ShowAt(pos);
    }

    private static Point? CaretPosition(IntPtr hwnd)
    {
        var tid = Native.GetWindowThreadProcessId(hwnd, out _);
        var gti = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (tid == 0 || !Native.GetGUIThreadInfo(tid, ref gti) || gti.hwndCaret == IntPtr.Zero) return null;
        var p = new Point(gti.rcCaret.Left, gti.rcCaret.Bottom + 4);
        return Native.ClientToScreen(gti.hwndCaret, ref p) ? p : null;
    }

    private void ToggleExcludeLastApp()
    {
        if (_lastApp == null) return;
        var existing = _settings.ExcludedApps.FirstOrDefault(a => a.Equals(_lastApp, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _settings.ExcludedApps.Remove(existing);
        else _settings.ExcludedApps.Add(_lastApp);
        _settings.Save();
        ShowTip(existing != null ? $"{_lastApp}: автопереключение включено" : $"{_lastApp}: автопереключение отключено");
    }

    private void RefreshMenu()
    {
        _statusItem.Text = $"OKBoard — {StatusText()}";
        _autoItem.Checked = _settings.AutoSwitch;
        _dndMenu.Checked = _engine.IsDnd;
        _dndMenu.Text = _engine.IsDnd
            ? (_engine.DndUntil == DateTime.MaxValue ? "Не беспокоить (включено)" : $"Не беспокоить (до {_engine.DndUntil:HH:mm})")
            : "Не беспокоить";
        _excludeItem.Visible = _lastApp != null;
        bool excluded = _engine.IsExcludedApp(_lastApp);
        _excludeItem.Text = excluded ? $"Включить автопереключение в {_lastApp}" : $"Не переключать в {_lastApp}";
        try { _autostartItem.Checked = Autostart.Enabled; } catch { }
        _updateItem.Text = _updater.DownloadedInstaller != null && _updater.Available is { } up
            ? $"Установить версию {up.Version.ToString(3)}"
            : "Проверить обновления";
        for (int i = 0; i < _themeMenu.DropDownItems.Count; i++)
            ((ToolStripMenuItem)_themeMenu.DropDownItems[i]).Checked = (int)Theme.Mode == i;
        foreach (ToolStripItem it in _dndMenu.DropDownItems) if (it is ToolStripMenuItem mi) mi.Checked = false;
    }

    private string StatusText()
    {
        if (!_settings.AutoSwitch) return "автопереключение выключено";
        if (_engine.IsDnd) return "не беспокоить";
        if (_engine.IsFullscreenPaused) return "пауза (полноэкранное приложение)";
        if (_engine.IsExcludedApp(_lastApp)) return $"{_lastApp} в исключениях";
        return "работает";
    }

    private void UpdateIcon(bool force)
    {
        var lang = _engine.CurrentLang;
        var state = !_settings.AutoSwitch || _engine.IsDnd ? IconState.Off
            : _engine.IsFullscreenPaused || _engine.IsExcludedApp(_lastApp) ? IconState.Excluded
            : IconState.Active;
        var key = $"{lang}|{state}";
        if (!force && key == _iconKey) return;
        _iconKey = key;
        var old = _icon;
        _icon = DrawIcon(lang switch { Lang.En => "EN", Lang.Ru => "RU", Lang.Uk => "UA", _ => "?" }, lang, state);
        _tray.Icon = _icon;
        _tray.Text = $"OKBoard — {lang switch { Lang.En => "EN", Lang.Ru => "RU", Lang.Uk => "UA", _ => "?" }}, {StatusText()}";
        if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
    }

    private static Icon DrawIcon(string text, Lang? lang, IconState state)
    {
        // Render at the exact tray size (16/20/24/32 px depending on DPI) so the letters stay crisp.
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        using var bmp = RenderIcon(text, lang, state, size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static readonly string IconFont = PickIconFont();

    private static string PickIconFont()
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        foreach (var name in new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift SemiBold Condensed", "Bahnschrift", "Segoe UI Black" })
            if (fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        return "Segoe UI";
    }

    /// <summary>
    /// Full-size rounded square with the layout code as large as fits inside it. Colour: the layout when working,
    /// black when switched off (or "do not disturb"), grey in an excluded app or a full-screen pause.
    /// </summary>
    internal static Bitmap RenderIcon(string text, Lang? lang, IconState state, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var bg = state == IconState.Off ? Color.FromArgb(14, 14, 14)
            : state == IconState.Excluded ? Color.FromArgb(110, 110, 110)
            : lang == Lang.Ru ? Color.FromArgb(200, 40, 40)
            : lang == Lang.Uk ? Color.FromArgb(200, 140, 0)
            : lang == Lang.En ? Color.FromArgb(25, 95, 200)
            : Color.FromArgb(90, 90, 90);
        using var shape = RoundRect(new RectangleF(0, 0, size, size), size * 0.22f);
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, shape);
        g.SetClip(shape);

        // Letters as a path, scaled to fill the box (glyph bounds, not font line height).
        using var glyphs = new GraphicsPath();
        using (var family = new FontFamily(IconFont))
            glyphs.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        var b = glyphs.GetBounds();
        float boxW = size * 0.88f, boxH = size * 0.60f;
        float scale = Math.Min(boxW / b.Width, boxH / b.Height);
        using var m = new Matrix();
        m.Translate(size / 2f, size / 2f);
        m.Scale(scale, scale);
        m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
        glyphs.Transform(m);
        g.FillPath(Brushes.White, glyphs);

        if (state == IconState.Off)
        {
            // a thin light outline keeps the black icon visible on a dark taskbar
            g.ResetClip();
            using var outline = RoundRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * 0.22f);
            using var pen = new Pen(Color.FromArgb(150, 150, 150), Math.Max(1f, size / 24f));
            g.DrawPath(pen, outline);
        }
        return bmp;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ShowSettings(int tab)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.SelectTab(tab);
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _engine, ApplyHistorySettings);
        _settingsForm.SelectTab(tab);
        _settingsForm.FormClosed += (_, _) => { _settingsForm = null; UpdateIcon(true); };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _engine.Dispose();
        _history.Dispose();
        _updater.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
