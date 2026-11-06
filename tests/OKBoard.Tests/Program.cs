using OKBoard;

if (args.Length == 2 && args[0] == "--ui")
{
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    int rcUi = 1;
    var tu = new Thread(() => rcUi = UiShots.Run(args[1]));
    tu.SetApartmentState(ApartmentState.STA);
    tu.Start();
    tu.Join();
    return rcUi;
}

if (args.Contains("--update-live"))
{
    // Real GitHub: latest release → pick the installer → download → SHA-256 check. Installs nothing.
    using var http = new System.Net.Http.HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("OKBoard-Updater/test");
    var json = await http.GetStringAsync($"https://api.github.com/repos/{Updater.Repo}/releases/latest");
    var info = Updater.Pick(json, new Version(0, 0, 1));
    Console.WriteLine(info == null ? "no usable release" : $"picked {info.AssetName} {info.Size} bytes sha256 {info.Sha256}");
    if (info == null) return 1;
    using var up = new Updater(new Settings());
    var path = await up.DownloadAsync(info);
    Console.WriteLine($"downloaded and verified: {path} ({new FileInfo(path).Length} bytes)");
    File.Delete(path);
    return 0;
}

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

// ── Ukrainian ──
Check("uk render", LayoutMap.Render(Keys("ghbdsn"), Lang.Uk) == "привіт", LayoutMap.Render(Keys("ghbdsn"), Lang.Uk));
Check("uk ї є", LayoutMap.Render(Keys("]'"), Lang.Uk) == "їє");
Check("convert en->uk", LayoutMap.ConvertText("ghbdsn", Lang.Uk) == "привіт");
Check("convert uk->en", LayoutMap.ConvertText("привіт") == "ghbdsn", LayoutMap.ConvertText("привіт"));
Check("detect uk script", LayoutMap.DetectScript("привіт") == Lang.Uk && LayoutMap.DetectScript("съешь", Lang.Uk) == Lang.Ru
                          && LayoutMap.DetectScript("привет", Lang.Uk) == Lang.Uk);
Lang[] all3 = [Lang.En, Lang.Ru, Lang.Uk];
var dUk = det.AnalyzeBest(Keys("ghbdsn"), Lang.En, all3, Lang.Ru);
Check("best: привіт → UK", dUk.Convert && dUk.Target == Lang.Uk, $"{dUk.Convert} {dUk.Target} {dUk.Reason}");
var dRu = det.AnalyzeBest(Keys("ghbdtn"), Lang.En, all3, Lang.Uk);
Check("best: привет → RU even if UK preferred", dRu.Convert && dRu.Target == Lang.Ru, $"{dRu.Convert} {dRu.Target} {dRu.Reason}");
var dHerb = det.AnalyzeBest(Keys("herb"), Lang.Uk, all3);   // "руки" typed correctly in UK (no UK dictionary installed)
Check("uk plausible word not turned into English", !dHerb.Convert, $"{dHerb.Reason} '{dHerb.Current}'→'{dHerb.Alternative}'");
var dHello = det.AnalyzeBest(Keys("hello"), Lang.Uk, all3); // "руддщ" — garbage in Ukrainian
Check("garbage in UK → English", dHello.Convert && dHello.Target == Lang.En, $"{dHello.Convert} {dHello.Reason}");
var dCyr = det.AnalyzeBest(Keys("ghbdsn"), Lang.Ru, [Lang.Ru, Lang.Uk]);
Check("never auto RU↔UK", !dCyr.Convert, dCyr.Reason);
Check("uk translit", Transliterator.ToLatin("Щастя Юрій Згорани Їжак", Lang.Uk) == "Shchastia Yurii Zghorany Yizhak",
    Transliterator.ToLatin("Щастя Юрій Згорани Їжак", Lang.Uk));
Check("uk translit back", Transliterator.ToCyrillic("pryvit", Lang.Uk) == "привіт", Transliterator.ToCyrillic("pryvit", Lang.Uk));
Check("translit auto uk", Transliterator.Transliterate("Київ") == "Kyiv", Transliterator.Transliterate("Київ"));

// ── Snippets ──
var fixedNow = new DateTime(2026, 11, 3, 9, 5, 0);
Check("snippet expand", Snippets.Expand("Сегодня {date}, {time}", fixedNow) == "Сегодня 03.11.2026, 09:05");
Check("snippet datetime", Snippets.Expand("{datetime}", fixedNow) == "03.11.2026 09:05");

// ── Editor / IDE / Unity tokens typed in the right layout must never be "corrected" ──
{
    Lang[] enRu = [Lang.En, Lang.Ru];
    // What the user typed with the EN layout active (US key positions).
    string[] enTokens =
    [
        // Unity search filters & paths
        "t:Prefab", "t:Material", "t:Scene", "t:Texture2D", "t:prefab", "l:Environment", "ref:Player", "t:AnimationClip",
        "Assets/Prefabs", "Packages/", "Editor/", "h:Player",
        // Unity / C# API
        "GameObject", "MonoBehaviour", "OnTriggerEnter", "deltaTime", "transform.position", "rb.velocity", "Debug.Log",
        "[SerializeField]", "Vector3.zero", "Quaternion.identity", "Mathf.Clamp", "GetComponent<Rigidbody>()", "Time.deltaTime",
        "Awake", "Update", "FixedUpdate", "Rigidbody", "Collider", "prefab", "prefabs", "shader", "mesh", "navmesh", "lerp",
        "slerp", "raycast", "gizmo", "gizmos", "asmdef", "csproj", "playmode", "inspector", "hierarchy", "spritesheet",
        // code keywords & common identifiers
        "var", "int", "bool", "float", "void", "enum", "struct", "foreach", "namespace", "using", "typeof", "nameof",
        "async", "await", "ctx", "cfg", "args", "impl", "init", "src", "dst", "tmp", "idx", "len", "str", "obj", "btn", "img",
        "std::vector", "i++", "x=5;", "a.b", "#if", "#endif", "e.g.", "i.e.", "etc.", "obj.name", "this.gameObject",
        // shell & tools
        "git", "npm", "cd", "ls", "mkdir", "sudo", "chmod", "grep", "json", "yaml", "http", "localhost", "dotnet", "nuget",
        "github", "okboard", "todo", "fixme", "wip", "lol", "btw", "ok", "pls", "thx",
        // hotkey-ish sequences that land in the buffer when an editor uses bare letters (Unity scene view: Q W E R T Y)
        "qwerty", "wasd", "qwe", "asdf", "zxc",
    ];
    foreach (var t in enTokens)
    {
        var d = det.AnalyzeBest(Keys(t), Lang.En, enRu);
        Check($"EN token kept: {t}", !d.Convert, $"→ '{d.Alternative}' ({d.Reason})");
    }
    // Russian words (including IT slang) typed correctly with the RU layout active. Given as US key positions.
    (string keys, string word)[] ruTokens =
    [
        ("rjvvbn", "коммит"), ("gei", "пуш"), (",bkl", "билд"), ("ghtaf,", "префаб"), ("lt,fuftv", "дебагаем"), ("rjvgjytyn", "компонент"),
        ("he,bv", "рубим"), ("bltv", "идем"), ("cltkfnm", "сделать"), ("ckjdj", "слово"), ("ytn", "нет"), ("lf", "да"),
    ];
    foreach (var (k, w) in ruTokens)
    {
        var d = det.AnalyzeBest(Keys(k), Lang.Ru, enRu);
        Check($"RU word kept: {w}", !d.Convert, $"'{d.Current}' → '{d.Alternative}' ({d.Reason})");
    }
    // ...while real wrong-layout typing is still fixed, including words with б ю ж х э ъ (punctuation keys)
    (string keys, Lang cur, string expect)[] fix =
    [
        ("ghbdtn", Lang.En, "привет"), ("j,sxyj", Lang.En, "обычно"), ("k.,jdm", Lang.En, "любовь"), (";bpym", Lang.En, "жизнь"),
        ("[jhjij", Lang.En, "хорошо"), ("'nj", Lang.En, "это"), ("cgfcb,j", Lang.En, "спасибо"), ("Ghbdtn", Lang.En, "Привет"),
        ("ntcn", Lang.En, "тест"), ("ghjtrn", Lang.En, "проект"),
        ("hello", Lang.Ru, "hello"), ("world", Lang.Ru, "world"), ("prefab", Lang.Ru, "prefab"), ("update", Lang.Ru, "update"),
    ];
    foreach (var (k, cur, expect) in fix)
    {
        var d = det.AnalyzeBest(Keys(k), cur, enRu);
        Check($"still fixed: {expect}", d.Convert && d.Alternative == expect, $"{d.Convert} '{d.Alternative}' ({d.Reason})");
    }
}

// ── Updater: parsing GitHub's "latest release" ──
{
    const string sha = "2225818078a436018df28adf342844d2cc18dc439f3e58ecaf38793b616e5b6b";
    string Rel(string tag, string asset, string url, string? digest = "sha256:" + sha, bool pre = false, long size = 46649249)
    {
        string Q(string x) => "\"" + x + "\"";
        var other = "{" + $"{Q("name")}:{Q("OKBoard-0.9.0-portable.exe")},{Q("browser_download_url")}:{Q($"https://github.com/{Updater.Repo}/releases/download/{tag}/x.exe")},{Q("size")}:1,{Q("digest")}:{Q("sha256:" + sha)}" + "}";
        var main = "{" + $"{Q("name")}:{Q(asset)},{Q("browser_download_url")}:{Q(url)},{Q("size")}:{size}" + (digest == null ? "" : $",{Q("digest")}:{Q(digest)}") + "}";
        return "{" + $"{Q("tag_name")}:{Q(tag)},{Q("draft")}:false,{Q("prerelease")}:{(pre ? "true" : "false")},{Q("html_url")}:{Q("https://github.com/" + Updater.Repo + "/releases/tag/" + tag)},{Q("assets")}:[{other},{main}]" + "}";
    }
    var good = $"https://github.com/{Updater.Repo}/releases/download/v0.9.0/OKBoard-Setup-0.9.0.exe";
    var cur = new Version(0, 2, 3);
    var u = Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", good), cur);
    Check("update: newer release found", u != null && u.Version == new Version(0, 9, 0) && u.Sha256 == sha && u.Download.ToString() == good, u?.ToString() ?? "null");
    Check("update: same version ignored", Updater.Pick(Rel("v0.2.3", "OKBoard-Setup-0.2.3.exe", good), cur) == null);
    Check("update: older ignored", Updater.Pick(Rel("v0.1.0", "OKBoard-Setup-0.1.0.exe", good), cur) == null);
    Check("update: prerelease ignored", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", good, pre: true), cur) == null);
    Check("update: no digest → not installed", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", good, digest: null), cur) == null);
    Check("update: foreign host rejected", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", "https://evil.example/OKBoard-Setup-0.9.0.exe"), cur) == null);
    Check("update: other repo rejected", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", "https://github.com/someone/else/releases/download/v0.9.0/OKBoard-Setup-0.9.0.exe"), cur) == null);
    Check("update: http rejected", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", good.Replace("https:", "http:")), cur) == null);
    Check("update: oversized rejected", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.9.0.exe", good, size: 500L * 1024 * 1024), cur) == null);
    Check("update: wrong asset name ignored", Updater.Pick(Rel("v0.9.0", "OKBoard-Setup-0.8.0.exe", good), cur) == null);
    Check("update: garbage tag ignored", Updater.Pick(Rel("latest", "OKBoard-Setup-0.9.0.exe", good), cur) == null);
}

// ── Hotkey parsing ──
Check("hotkey parse", Hotkey.Parse("Ctrl+Shift+Pause") == new Hotkey(System.Windows.Forms.Keys.Pause, true, false, true, false));
Check("hotkey roundtrip", Hotkey.Parse("Ctrl+Alt+T")!.Value.ToString() == "Ctrl+Alt+T");
Check("unsafe hotkeys", Hotkey.Parse("Q")!.Value.IsSafe == false && Hotkey.Parse("Shift+A")!.Value.IsSafe == false
                        && Hotkey.Parse("Pause")!.Value.IsSafe && Hotkey.Parse("Ctrl+Alt+V")!.Value.IsSafe);

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
