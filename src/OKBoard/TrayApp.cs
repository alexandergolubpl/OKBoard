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

internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly Engine _engine;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _autoItem, _dndMenu, _excludeItem, _autostartItem, _statusItem;
    private string _iconKey = "";
    private Icon? _icon;
    private string? _lastApp;
    private IntPtr _lastAppHwnd;
    private SettingsForm? _settingsForm;
    private readonly ClipboardHistory _history;
    private ClipboardPopup? _popup;

    public TrayApp(Settings settings, Engine engine)
    {
        _settings = settings;
        _engine = engine;
        _history = new ClipboardHistory(_engine.IsExcludedApp, () => _engine.Busy);
        ApplyHistorySettings();

        _statusItem = new ToolStripMenuItem("OKBoard") { Enabled = false };
        _autoItem = new ToolStripMenuItem("Автопереключение раскладки", null, (_, _) =>
        {
            _settings.AutoSwitch = !_settings.AutoSwitch;
            _settings.Save();
            UpdateIcon(true);
        });
        _dndMenu = new ToolStripMenuItem("Не беспокоить");
        _dndMenu.DropDownItems.Add("Выключить", null, (_, _) => _engine.SetDnd(null));
        _dndMenu.DropDownItems.Add("На 15 минут", null, (_, _) => _engine.SetDnd(TimeSpan.FromMinutes(15)));
        _dndMenu.DropDownItems.Add("На 1 час", null, (_, _) => _engine.SetDnd(TimeSpan.FromHours(1)));
        _dndMenu.DropDownItems.Add("На 3 часа", null, (_, _) => _engine.SetDnd(TimeSpan.FromHours(3)));
        _dndMenu.DropDownItems.Add("Пока не выключу", null, (_, _) => _engine.SetDnd(TimeSpan.MaxValue));
        _excludeItem = new ToolStripMenuItem("Исключить приложение", null, (_, _) => ToggleExcludeLastApp());
        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) =>
        {
            try { Autostart.Enabled = !Autostart.Enabled; } catch (Exception ex) { ShowTip(ex.Message); }
        });

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _autoItem,
            _dndMenu,
            _excludeItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("История буфера обмена…", null, (_, _) => ShowClipboardPopup(_lastAppHwnd, atCaret: false)),
            new ToolStripMenuItem("Очистить историю буфера", null, (_, _) => { _history.Clear(); ShowTip("История буфера обмена очищена"); }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Настройки…", null, (_, _) => ShowSettings(0)),
            new ToolStripMenuItem("Журнал исправлений…", null, (_, _) => ShowSettings(SettingsForm.LogTab)),
            _autostartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "OKBoard" };
        _tray.DoubleClick += (_, _) => ShowSettings(0);
        _engine.Notify += ShowTip;
        _engine.StateChanged += () => UpdateIcon(false);
        _engine.ClipboardHistoryRequested += fg => ShowClipboardPopup(fg, atCaret: true);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => { TrackForegroundApp(); UpdateIcon(false); _engine.CheckHookHealth(); };
        _timer.Start();
        UpdateIcon(true);
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
            _engine.Suspended = false;
            if (!popup.WasPicked && target != IntPtr.Zero) Native.SetForegroundWindow(target);
            if (_popup == popup) _popup = null;
        };
        _popup = popup;
        _engine.Suspended = true; // don't auto-correct what is typed into the search box
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
    }

    private string StatusText()
    {
        if (!_settings.AutoSwitch) return "автопереключение выключено";
        if (_engine.IsDnd) return "не беспокоить";
        if (_engine.IsFullscreenPaused) return "пауза (полноэкранное приложение)";
        return "работает";
    }

    private void UpdateIcon(bool force)
    {
        var lang = _engine.CurrentLang;
        bool paused = !_settings.AutoSwitch || _engine.IsDnd || _engine.IsFullscreenPaused;
        var key = $"{lang}|{paused}";
        if (!force && key == _iconKey) return;
        _iconKey = key;
        var old = _icon;
        _icon = DrawIcon(lang switch { Lang.En => "EN", Lang.Ru => "RU", Lang.Uk => "UA", _ => "··" }, lang, paused);
        _tray.Icon = _icon;
        _tray.Text = $"OKBoard — {lang switch { Lang.En => "EN", Lang.Ru => "RU", Lang.Uk => "UA", _ => "?" }}, {StatusText()}";
        if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
    }

    private static Icon DrawIcon(string text, Lang? lang, bool paused)
    {
        int size = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var bg = paused ? Color.FromArgb(120, 120, 120)
                : lang == Lang.Ru ? Color.FromArgb(200, 45, 45)
                : lang == Lang.Uk ? Color.FromArgb(205, 145, 0)
                : lang == Lang.En ? Color.FromArgb(30, 100, 200)
                : Color.FromArgb(90, 90, 90);
            using var path = RoundRect(new RectangleF(0, size * 0.12f, size, size * 0.76f), size * 0.18f);
            using var brush = new SolidBrush(bg);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size + 1), fmt);
            if (paused)
            {
                using var pen = new Pen(Color.FromArgb(255, 210, 60), size / 10f);
                g.DrawLine(pen, size * 0.08f, size * 0.85f, size * 0.92f, size * 0.15f);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
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
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
