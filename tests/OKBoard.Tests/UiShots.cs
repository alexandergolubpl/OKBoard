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

        var popup = new ClipboardPopup(history, _ => { });
        popup.Show();
        Application.DoEvents();
        Save(popup, Path.Combine(dir, "popup.png"));
        popup.Close();

        var form = new SettingsForm(settings, engine, () => { });
        form.Show();
        foreach (var (tab, name) in new[] { (0, "general"), (2, "snippets") })
        {
            form.SelectTab(tab);
            Application.DoEvents();
            Save(form, Path.Combine(dir, $"settings-{name}.png"));
        }
        form.Close();
        File.Delete(Settings.FilePath);
        Console.WriteLine("shots saved to " + dir);
        return 0;
    }

    private static void Save(Control c, string path)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        bmp.Save(path);
    }
}
