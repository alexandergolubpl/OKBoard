namespace OKBoard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\OKBoard.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("OKBoard уже запущен — значок в области уведомлений.", "OKBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Engine.Trace = DiagLog.Write;
        Application.ThreadException += (_, e) => DiagLog.Write("unhandled: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagLog.Write("fatal: " + e.ExceptionObject);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var settings = Settings.Load();
        Theme.Set(Theme.Parse(settings.Theme));
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
                Theme.Refresh();
        };
        var layouts = new LayoutManager();
        if (!layouts.HasLatinAndCyrillic)
        {
            MessageBox.Show("Для работы нужны раскладка English и хотя бы одна из: Русская, Украинская.\n" +
                            "Добавьте их в Параметры → Время и язык → Язык и регион.",
                "OKBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // OS spell checkers for the installed languages (Ukrainian may be missing — the detector is stricter then).
        var spell = new Dictionary<Lang, Func<string, bool?>>();
        foreach (var (lang, primary, tags) in new[]
                 {
                     (Lang.En, "en", new[] { "en-US", "en-GB", "en-AU", "en-IE" }),
                     (Lang.Ru, "ru", new[] { "ru-RU" }),
                     (Lang.Uk, "uk", new[] { "uk-UA" }),
                 })
        {
            var checker = SpellChecker.ForLanguage(primary, tags);
            if (!checker.Available) continue;
            checker.IsCorrect(lang == Lang.En ? "check" : "привіт"); // warm up: first call loads the dictionary
            spell[lang] = checker.IsCorrect;
        }
        var detector = new LanguageDetector(spell);
        using var engine = new Engine(settings, detector, layouts);
        engine.Start();

        if (!File.Exists(Settings.FilePath)) settings.Save();
        Application.Run(new TrayApp(settings, engine));
    }
}
