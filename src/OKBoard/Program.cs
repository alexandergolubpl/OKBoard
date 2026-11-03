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
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var settings = Settings.Load();
        var layouts = new LayoutManager();
        if (!layouts.HasBoth)
        {
            MessageBox.Show("Для работы нужны установленные раскладки English и Русская.\n" +
                            "Добавьте их в Параметры → Время и язык → Язык и регион.",
                "OKBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var spellRu = SpellChecker.ForLanguage("ru", "ru-RU");
        var spellEn = SpellChecker.ForLanguage("en", "en-US", "en-GB", "en-AU", "en-IE");
        // Warm up: the first ISpellChecker call loads the dictionary, which must not happen inside the hook.
        spellRu.IsCorrect("проверка");
        spellEn.IsCorrect("check");
        var detector = new LanguageDetector(spellRu.IsCorrect, spellEn.IsCorrect);
        using var engine = new Engine(settings, detector, layouts);
        engine.Start();

        if (!File.Exists(Settings.FilePath)) settings.Save();
        Application.Run(new TrayApp(settings, engine));
    }
}
