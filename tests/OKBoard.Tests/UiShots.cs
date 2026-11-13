using OKBoard;

/// <summary>Renders the new windows to PNG files for a visual check (no input needed).</summary>
internal static class UiShots
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Settings.FilePath = Path.Combine(Path.GetTempPath(), "okboard-ui-settings.json");
        var settings = new Settings
        {
            Snippets =
            [
                new Snippet { Abbr = "мк", Text = "Москва, Кремль" },
                new Snippet { Abbr = "подп", Text = "С уважением,\nАлександр\n+48 000 000 000" },
                new Snippet { Abbr = "сег", Text = "{date}" },
            ],
        };
        var det = new LanguageDetector(_ => null, _ => null);
        using var engine = new Engine(settings, det, new LayoutManager());
        using var history = new ClipboardHistory(_ => false, () => true);
        history.Add("git status && git log --oneline -5", "WindowsTerminal.exe");
        history.Add("https://github.com/alexandergolubpl/OKBoard/releases", "chrome.exe");
        history.Add("Привет! Созвонимся завтра в 11:00?\nСсылка будет в календаре.", "Telegram.exe");
        history.Add("SELECT * FROM users WHERE id = 42;", "Code.exe");

        foreach (int sz in new[] { 16, 20, 24, 32 })
        {
            using var sheet = new Bitmap(sz * 5 + 40, sz + 8);
            using (var sg = Graphics.FromImage(sheet))
            {
                sg.Clear(Color.FromArgb(32, 32, 32));
                int x = 4;
                foreach (var (t, l, p) in new (string, Lang?, IconState)[] { ("EN", Lang.En, IconState.Active), ("RU", Lang.Ru, IconState.Active), ("UA", Lang.Uk, IconState.Active), ("EN", Lang.En, IconState.Off), ("RU", Lang.Ru, IconState.Excluded) })
                {
                    using var ic = TrayApp.RenderIcon(t, l, p, sz);
                    sg.DrawImageUnscaled(ic, x, 4);
                    x += sz + 8;
                }
            }
            sheet.Save(Path.Combine(dir, $"icons-{sz}.png"));
        }

        var tray = new TrayApp(settings, engine);
        foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
        {
            Theme.Set(mode);
            var tag = mode.ToString().ToLowerInvariant();

            var popup = new ClipboardPopup(history, _ => { });
            popup.ShowAt(new Point(200, 200));
            Settle();
            Capture(popup.Bounds, Path.Combine(dir, $"popup-{tag}.png"));
            popup.Close();

            var form = new SettingsForm(settings, engine, () => { }) { StartPosition = FormStartPosition.Manual, Location = new Point(100, 80), TopMost = true };
            form.Show();
            foreach (var (page, name) in new[] { (0, "general"), (SettingsForm.RulesTab, "rules"), (SettingsForm.LogTab, "log") })
            {
                form.SelectTab(page);
                Settle();
                Capture(form.Bounds, Path.Combine(dir, $"settings-{name}-{tag}.png"));
            }
            form.Close();

            tray.Menu.Show(new Point(300, 150));
            Settle();
            Capture(new Rectangle(tray.Menu.Left - 10, tray.Menu.Top - 10, tray.Menu.Width + 20, tray.Menu.Height + 20), Path.Combine(dir, $"menu-{tag}.png"));
            tray.Menu.Close();
        }
        tray.ExitThread();
        File.Delete(Settings.FilePath);
        Console.WriteLine("shots saved to " + dir);
        return 0;
    }

    private static void Settle()
    {
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(15); }
    }

    /// <summary>Real screen capture (includes the DWM title bar, rounded corners and menus).</summary>
    private static void Capture(Rectangle r, string path)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        bmp.Save(path);
    }

    private static void Save(Control c, string path)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        bmp.Save(path);
    }
}
