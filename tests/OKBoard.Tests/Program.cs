using OKBoard;

if (args.Contains("--e2e"))
{
    int rc = 1;
    var t = new Thread(() => rc = E2E.Run());
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    return rc;
}

// Self-contained test runner (no NuGet needed): dotnet run --project tests/OKBoard.Tests
int failed = 0, passed = 0;
void Check(string name, bool ok, string details = "")
{
    if (ok) passed++; else { failed++; Console.WriteLine($"FAIL {name} {details}"); }
}

static List<KeyStroke> Keys(string typedEn)
{
    // Build keystrokes from what would appear with the US layout active.
    const string lower = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`1234567890-= ";
    const string upper = "QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?~!@#$%^&*()_+ ";
    ushort[] vks = [.. "QWERTYUIOP".Select(c => (ushort)c), 0xDB, 0xDD, .. "ASDFGHJKL".Select(c => (ushort)c), 0xBA, 0xDE,
        .. "ZXCVBNM".Select(c => (ushort)c), 0xBC, 0xBE, 0xBF, 0xC0, .. "1234567890".Select(c => (ushort)c), 0xBD, 0xBB, 0x20];
    var list = new List<KeyStroke>();
    foreach (var ch in typedEn)
    {
        int i = lower.IndexOf(ch);
        if (i >= 0) { list.Add(new KeyStroke(vks[i], false)); continue; }
        i = upper.IndexOf(ch);
        if (i < 0) throw new ArgumentException($"char {ch}");
        list.Add(new KeyStroke(vks[i], true));
    }
    return list;
}

// ── LayoutMap ──
Check("render ru", LayoutMap.Render(Keys("ghbdtn"), Lang.Ru) == "привет");
Check("render ru punct", LayoutMap.Render(Keys("ghbdtn?"), Lang.Ru) == "привет,");
Check("convert en->ru", LayoutMap.ConvertText("Ghbdtn? rfr ltkf&") == "Привет, как дела?", LayoutMap.ConvertText("Ghbdtn? rfr ltkf&"));
Check("convert ru->en", LayoutMap.ConvertText("руддщ цщкдв") == "hello world");
Check("convert roundtrip", LayoutMap.ConvertText(LayoutMap.ConvertText("Съешь же ещё этих мягких булок.")) == "Съешь же ещё этих мягких булок.",
    LayoutMap.ConvertText("Съешь же ещё этих мягких булок."));
Check("caps flag", LayoutMap.Render([new KeyStroke('G', false, true), new KeyStroke(0xBA, false, true)], Lang.Ru) == "ПЖ");
Check("caps flag en", LayoutMap.Render([new KeyStroke('G', false, true), new KeyStroke(0xBA, false, true)], Lang.En) == "G;");

// ── Detector ──
Console.WriteLine("Supported: " + string.Join(", ", SpellChecker.SupportedLanguages()));
var spellRu = SpellChecker.ForLanguage("ru", "ru-RU");
var spellEn = SpellChecker.ForLanguage("en", "en-US", "en-GB", "en-AU", "en-IE");
Console.WriteLine($"Spell checker ru: {spellRu.Available} ({spellRu.Tag}), en: {spellEn.Available} ({spellEn.Tag})");
var det = new LanguageDetector(spellRu.IsCorrect, spellEn.IsCorrect);

// typed (as seen in EN layout), current layout, expected conversion
(string typed, Lang cur, bool expect)[] cases =
[
    ("ghbdtn", Lang.En, true),          // привет
    ("Ghbdtn", Lang.En, true),
    ("ghbdtn?", Lang.En, true),         // привет,
    ("k.,k.", Lang.En, true),           // люблю
    ("yt", Lang.En, true),              // не
    ("rjvgm.nth", Lang.En, true),       // компьютер
    ("ckjdj", Lang.En, true),           // слово
    ("ntrcn", Lang.En, true),           // текст
    ("hello", Lang.En, false),
    ("world", Lang.En, false),
    ("git", Lang.En, false),
    ("status", Lang.En, false),
    ("Kubernetes", Lang.En, false),
    ("getValue", Lang.En, false),
    ("NFR", Lang.En, false),            // acronym
    ("a", Lang.En, false),
    ("is", Lang.En, false),
    ("user@mail", Lang.En, false),
    ("abc123", Lang.En, false),
    ("rfr", Lang.En, true),             // как
    ("lf", Lang.En, true),              // да
    ("ckfdyj", Lang.En, true),          // славно (dictionary)
    // typed in RU layout (string = keys as on US keyboard)
    ("hello", Lang.Ru, true),           // руддщ -> hello
    ("world", Lang.Ru, true),
    ("project", Lang.Ru, true),
    ("ghbdtn", Lang.Ru, false),         // привет — valid Russian
    ("yjhv", Lang.Ru, false),           // норм — slang; must not become "yjhv"
    ("vjcrdf", Lang.Ru, false),         // москва
    ("ntcn", Lang.Ru, false),           // тест
    ("lf", Lang.Ru, false),             // да
    ("cfqn", Lang.Ru, false),           // сайт
];
foreach (var (typed, cur, expect) in cases)
{
    var d = det.Analyze(Keys(typed), cur);
    Check($"detect {typed}/{cur}", d.Convert == expect, $"→ got {d.Convert} ({d.Reason}) cur='{d.Current}' alt='{d.Alternative}'");
}

// ── Broad sample: correct words must stay, wrong-layout words should be fixed ──
string[] ruWords = ("обработка исключение пожалуйста завтрак холодильник отпуск бухгалтерия электричка воскресенье " +
    "здорово чувствую неважно презентация созвонимся уточнить релиз дедлайн фича баг коммит мерж " +
    "Москва Петербург Иван Наталья хорошего ладно щас норм ок спс кек").Split(' ');
string[] enWords = ("breakfast refrigerator vacation accounting electricity sunday feeling presentation release " +
    "deadline feature commit merge branch pipeline kubectl npm localhost json yaml docker async await " +
    "London Microsoft Google gonna wanna lol btw asap thx").Split(' ');
int fpRu = 0, fpEn = 0, hitRu = 0, hitEn = 0;
foreach (var w in ruWords)
{
    var keys = Keys(LayoutMap.ConvertText(w, Lang.Ru));
    var ok = det.Analyze(keys, Lang.Ru); if (ok.Convert) { fpRu++; Console.WriteLine($"  false positive RU: {w} → {ok.Alternative} ({ok.Reason})"); }
    var bad = det.Analyze(keys, Lang.En); if (bad.Convert) hitRu++; else Console.WriteLine($"  missed (typed in EN): {bad.Current} → {w} ({bad.Reason})");
}
foreach (var w in enWords)
{
    var keys = Keys(w);
    var ok = det.Analyze(keys, Lang.En); if (ok.Convert) { fpEn++; Console.WriteLine($"  false positive EN: {w} → {ok.Alternative} ({ok.Reason})"); }
    var bad = det.Analyze(keys, Lang.Ru); if (bad.Convert) hitEn++; else Console.WriteLine($"  missed (typed in RU): {bad.Current} → {w} ({bad.Reason})");
}
Console.WriteLine($"RU: {hitRu}/{ruWords.Length} fixed, {fpRu} false positives; EN: {hitEn}/{enWords.Length} fixed, {fpEn} false positives");
Check("no false positives", fpRu + fpEn == 0);

// ── Text tools ──
Check("translit to lat", Transliterator.ToLatin("Щука и ёжик") == "Shchuka i yozhik", Transliterator.ToLatin("Щука и ёжик"));
Check("translit to cyr", Transliterator.ToCyrillic("Privet, shchuka i zhuk") == "Привет, щука и жук", Transliterator.ToCyrillic("Privet, shchuka i zhuk"));
Check("translit auto", Transliterator.Transliterate("ЖУК") == "ZHUK", Transliterator.Transliterate("ЖУК"));
Check("case cycle 1", CaseTools.Cycle("привет мир") == "ПРИВЕТ МИР");
Check("case cycle 2", CaseTools.Cycle("ПРИВЕТ МИР") == "Привет Мир");
Check("case cycle 3", CaseTools.Cycle("Привет Мир") == "привет мир");
Check("caps accident", CaseTools.LooksLikeCapsAccident("пРИВЕТ") && !CaseTools.LooksLikeCapsAccident("Привет"));
Check("double cap", CaseTools.LooksLikeDoubleCapital("ПРивет") && !CaseTools.LooksLikeDoubleCapital("ПРИВЕТ") && !CaseTools.LooksLikeDoubleCapital("USB"));

// ── Hotkey parsing ──
Check("hotkey parse", Hotkey.Parse("Ctrl+Shift+Pause") == new Hotkey(System.Windows.Forms.Keys.Pause, true, false, true, false));
Check("hotkey roundtrip", Hotkey.Parse("Ctrl+Alt+T")!.Value.ToString() == "Ctrl+Alt+T");

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
