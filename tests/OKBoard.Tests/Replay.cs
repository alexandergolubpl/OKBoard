using System.Text;
using System.Text.Json;
using OKBoard;

/// <summary>
/// Replays the user's real prompts from Claude Code transcripts (~/.claude/projects, last N days, other projects)
/// through the real Engine with a virtual keyboard, document and clock. The corpus is read at run time and never
/// stored: the repository is public and the prompts are private. Only statistics and the problem words are printed.
/// </summary>
internal static class Replay
{
    private const ushort VK_SHIFT = 0xA0, VK_SPACE = 0x20, VK_RETURN = 0x0D, VK_END = 0x23;
    private const long KeyMs = 130; // a brisk human typist

    /// <summary>Virtual text field + layouts: receives both the user's keys and the engine's corrections.</summary>
    private sealed class Doc(VirtualLayouts layouts) : IOutput
    {
        public readonly StringBuilder Text = new();
        public void Backspace(int count) => Text.Length = Math.Max(0, Text.Length - count);
        void IOutput.Text(string text) => Text.Append(text.Replace("\r\n", "\n"));
        public void Tap(ushort vk)
        {
            if (vk == VK_SPACE) Text.Append(' ');
            else if (vk == VK_RETURN) Text.Append('\n');
            else if (vk == 0x09) Text.Append('\t');
        }
        public void ReleaseModifiers(IEnumerable<ushort> held) { }
        public VirtualLayouts Layouts { get; } = layouts;
    }

    private sealed class VirtualLayouts(Func<long> clock) : LayoutManager
    {
        private Lang _cur = Lang.En;
        private long _own;
        public override Lang? Current() => _cur;
        public override void Switch(Lang lang, IntPtr? target = null) { _cur = lang; _own = clock(); }
        public override long LastOwnSwitchTick { get => _own; protected set => _own = value; }
        /// <summary>The user presses Alt+Shift: not one of OKBoard's own switches.</summary>
        public void UserSwitch(Lang lang) => _cur = lang;
    }

    public static int Run(int days, string? onlyProject)
    {
        var prompts = LoadPrompts(days, onlyProject, out var projects);
        Console.WriteLine($"prompts: {prompts.Count} from {projects} projects (last {days} days), " +
                          $"{prompts.Sum(p => p.Length)} characters");
        if (prompts.Count == 0) return 1;

        var spell = new Dictionary<Lang, Func<string, bool?>>();
        foreach (var (lang, primary, tags) in new[] { (Lang.En, "en", new[] { "en-US", "en-GB" }), (Lang.Ru, "ru", new[] { "ru-RU" }) })
        {
            var ch = SpellChecker.ForLanguage(primary, tags);
            if (ch.Available) spell[lang] = ch.IsCorrect;
        }

        int rc = 0;
        foreach (var careful in new[] { true, false })
        {
            Settings.FilePath = Path.Combine(Path.GetTempPath(), "okboard-replay-settings.json");
            var settings = new Settings { SoundOnAutoSwitch = false, PauseInFullscreen = false };
            long clock = 1_000_000;
            var layouts = new VirtualLayouts(() => clock);
            var doc = new Doc(layouts);
            var det = new LanguageDetector(spell);
            using var engine = new Engine(settings, det, layouts) { Out = doc, Clock = () => clock, Simulated = true };

            var keyMap = BuildKeyMap();
            var wrong = new Dictionary<string, (string got, int n)>();
            var missed = new Dictionary<string, int>();
            int words = 0, wrongWords = 0, missedWords = 0, fixedWords = 0;

            foreach (var prompt in prompts)
            {
                doc.Text.Clear();
                Feed(engine, VK_END, ref clock); // new field: the engine forgets the previous line
                var typed = new StringBuilder();  // what the user meant (only typeable characters)
                Lang? home = null;                // language of the current word: its keys are pressed
                for (int ci = 0; ci < prompt.Length; ci++)
                {
                    var ch = prompt[ci];
                    if (ci == 0 || prompt[ci - 1] is ' ' or '\n')
                    {
                        int e = ci;
                        while (e < prompt.Length && prompt[e] is not (' ' or '\n')) e++;
                        home = Script(prompt[ci..e]);
                        // a careful user switches to the word's language before typing it
                        if (careful && home is { } hl && layouts.Current() != hl) layouts.UserSwitch(hl);
                    }
                    if (ch == ' ' || ch == '\n')
                    {
                        if (typed.Length > 0 && typed[^1] is ' ' or '\n') continue;
                        typed.Append(ch);
                        ushort vk = ch == ' ' ? VK_SPACE : VK_RETURN;
                        if (!Feed(engine, vk, ref clock)) doc.Tap(vk);
                        continue;
                    }
                    if (!keyMap.TryGetValue(ch, out var opts)) continue; // emoji, «», — ... not on these keyboards
                    var cur = layouts.Current()!.Value;
                    (ushort vk, bool shift) key;
                    if (!careful && home is { } h && opts.TryGetValue(h, out var hk)) key = hk; // keys of the word's language
                    else if (opts.TryGetValue(cur, out var k)) key = k;                          // exists in this layout
                    else if (careful) { var l = opts.Keys.First(); layouts.UserSwitch(l); key = opts[l]; } // switches by hand
                    else key = opts.Values.First();
                    typed.Append(ch);
                    if (key.shift) Feed(engine, VK_SHIFT, ref clock, down: true);
                    bool swallowed = Feed(engine, key.vk, ref clock);
                    if (key.shift) Feed(engine, VK_SHIFT, ref clock, down: false, upOnly: true);
                    if (!swallowed)
                    {
                        var out1 = LayoutMap.CharFor(new KeyStroke(key.vk, key.shift), layouts.Current()!.Value);
                        if (out1 is { } c) doc.Text.Append(c);
                    }
                }
                if (!Feed(engine, VK_RETURN, ref clock)) doc.Tap(VK_RETURN); // send the prompt

                var exp = Words(typed.ToString());
                var got = Words(doc.Text.ToString());
                words += exp.Count;
                if (exp.Count != got.Count)
                {
                    // word count changed (a space was eaten or added): count every differing position once
                    wrongWords++;
                    Count(wrong, $"[строка] {Short(typed.ToString())}", Short(doc.Text.ToString()));
                    continue;
                }
                for (int i = 0; i < exp.Count; i++)
                {
                    // words mixing both alphabets (paths with Russian folders, "unity-проект", typos like "Tеперь")
                    // can't be typed "forgetfully" in a meaningful way: score them in the careful pass only
                    if (!careful && MixedScript(exp[i])) continue;
                    if (exp[i] == got[i]) { if (!careful && Script(exp[i]) != null) fixedWords++; continue; }
                    if (careful) { wrongWords++; Count(wrong, exp[i], got[i]); continue; }
                    // forgetful user: still in the wrong layout = a miss; changed into something else = wrong
                    var asTypedWrong = LayoutMap.ConvertText(exp[i], Script(exp[i]) == Lang.Ru ? Lang.Ru : Lang.En,
                                                             Script(exp[i]) == Lang.Ru ? Lang.En : Lang.Ru);
                    if (got[i] == asTypedWrong) { missedWords++; missed[exp[i]] = missed.GetValueOrDefault(exp[i]) + 1; }
                    else { wrongWords++; Count(wrong, exp[i], got[i]); }
                }
            }

            Console.WriteLine();
            if (careful)
            {
                Console.WriteLine($"== Аккуратный набор (раскладку переключаете сами): {words} слов, испорчено {wrongWords} " +
                                  $"({100.0 * wrongWords / Math.Max(1, words):0.00}%)");
                Top(wrong, 40);
                if (wrongWords > 0) rc = 1;
            }
            else
            {
                Console.WriteLine($"== Забывчивый набор (раскладку не переключаете): {words} слов, " +
                                  $"неверно исправлено {wrongWords}, не исправлено {missedWords}");
                Console.WriteLine("  неверно исправлено:");
                Top(wrong, 25);
                Console.WriteLine("  не исправлено (чаще всего):");
                foreach (var (w, n) in missed.OrderByDescending(x => x.Value).Take(40))
                    Console.WriteLine($"    {w} ×{n}");
            }
        }
        File.Delete(Settings.FilePath);
        return rc;
    }

    private static void Count(Dictionary<string, (string got, int n)> d, string exp, string got)
    {
        var key = exp + "\u0001" + got;
        d[key] = (got, d.GetValueOrDefault(key).n + 1);
    }

    private static void Top(Dictionary<string, (string got, int n)> d, int max)
    {
        foreach (var (k, v) in d.OrderByDescending(x => x.Value.n).Take(max))
            Console.WriteLine($"    {k.Split('\u0001')[0]}  →  {v.got}   ×{v.n}");
    }

    private static string Short(string s) => s.Length > 70 ? s[..70] + "…" : s;

    private static List<string> Words(string s) =>
        s.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool MixedScript(string w) =>
        w.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') && w.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    private static Lang? Script(string w) =>
        w.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') ? Lang.Ru
        : w.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') ? Lang.En : null;

    private static bool Feed(Engine e, ushort vk, ref long clock, bool down = true, bool upOnly = false)
    {
        clock += KeyMs;
        bool sw = false;
        if (!upOnly) sw = e.Feed(vk, true);
        if (!down || vk != VK_SHIFT) e.Feed(vk, false);
        return sw;
    }

    /// <summary>char → (layout → key) for the EN and RU layouts (unshifted preferred).</summary>
    private static Dictionary<char, Dictionary<Lang, (ushort vk, bool shift)>> BuildKeyMap()
    {
        var map = new Dictionary<char, Dictionary<Lang, (ushort, bool)>>();
        foreach (var lang in new[] { Lang.En, Lang.Ru })
            foreach (var shift in new[] { false, true })
                for (ushort vk = 0x20; vk < 0xE0; vk++)
                {
                    if (!LayoutMap.IsCharKey(vk) || vk == VK_SPACE) continue;
                    if (LayoutMap.CharFor(new KeyStroke(vk, shift), lang) is not { } c) continue;
                    if (!map.TryGetValue(c, out var m)) map[c] = m = new();
                    m.TryAdd(lang, (vk, shift));
                }
        return map;
    }

    /// <summary>What the user typed into Claude Code in other projects (not tool output, not pasted logs).</summary>
    private static List<string> LoadPrompts(int days, string? onlyProject, out int projects)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var since = DateTime.UtcNow.AddDays(-days);
        var result = new List<string>();
        var seen = new HashSet<string>();
        var projs = new HashSet<string>();
        if (!Directory.Exists(root)) { projects = 0; return result; }
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Contains("OKBoard", StringComparison.OrdinalIgnoreCase)) continue; // other projects only
            if (onlyProject != null && !name.Contains(onlyProject, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) < since) continue;
                foreach (var line in File.ReadLines(file))
                {
                    if (!line.Contains("\"type\":\"user\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                        if (r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) continue;
                        if (r.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var t) &&
                            t.ToUniversalTime() < since) continue;
                        if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) continue;
                        string? text = content.ValueKind == JsonValueKind.String ? content.GetString()
                            : content.ValueKind == JsonValueKind.Array
                                ? string.Join("\n", content.EnumerateArray()
                                    .Where(x => x.TryGetProperty("type", out var ty) && ty.GetString() == "text")
                                    .Select(x => x.GetProperty("text").GetString()))
                                : null;
                        text = Clean(text);
                        if (text == null || !seen.Add(text)) continue;
                        result.Add(text);
                        projs.Add(name);
                    }
                    catch (JsonException) { }
                }
            }
        }
        projects = projs.Count;
        return result;
    }

    /// <summary>Typed prompt text only: no command/system tags, no pasted blocks, no huge logs.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('<') || t.StartsWith("Caveat:") || t.StartsWith("[Request interrupted") ||
            t.StartsWith("This session is being continued")) return null;
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[a-z_-]+[^>]*>[\\s\\S]*?</[a-z_-]+>", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, "\\[(Image|Pasted text|File) #\\d+[^\\]]*\\]", " "); // placeholders, not typed
        if (t.Contains("```")) t = System.Text.RegularExpressions.Regex.Replace(t, "```[\\s\\S]*?```", " ");
        t = t.Replace("\r\n", "\n");
        if (t.Length > 1500 || t.Count(c => c == '\n') > 15) return null; // pasted logs/code, not typing
        t = System.Text.RegularExpressions.Regex.Replace(t, "[ \\t]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, "\\n+", "\n").Trim();
        return t.Length < 2 ? null : t;
    }
}
