namespace OKBoard;

/// <param name="Target">Layout the word should be converted to (meaningful when <paramref name="Convert"/> is true).</param>
/// <param name="Strength">How sure we are: 3 = OS dictionary, 2 = built-in common words, 1 = statistics.</param>
public sealed record Decision(bool Convert, string Reason, string Current, string Alternative, Lang Target = Lang.En, int Strength = 0);

/// <summary>Character-bigram language model trained on the built-in word list.</summary>
internal sealed class BigramModel
{
    private readonly Dictionary<(char, char), int> _pairs = new();
    private readonly Dictionary<char, int> _singles = new();
    private const double K = 0.1;
    private const int Vocab = 40;

    public BigramModel(IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            var s = "^" + w.ToLowerInvariant() + "$";
            for (int i = 0; i + 1 < s.Length; i++)
            {
                _pairs[(s[i], s[i + 1])] = _pairs.GetValueOrDefault((s[i], s[i + 1])) + 1;
                _singles[s[i]] = _singles.GetValueOrDefault(s[i]) + 1;
            }
        }
    }

    /// <param name="prefix">Score the start of a word being typed: no end-of-word marker.</param>
    public (double avg, int unseen) Score(string word, bool prefix = false)
    {
        var s = "^" + word.ToLowerInvariant() + (prefix ? "" : "$");
        double sum = 0;
        int unseen = 0, n = 0;
        for (int i = 0; i + 1 < s.Length; i++, n++)
        {
            var c = _pairs.GetValueOrDefault((s[i], s[i + 1]));
            if (c == 0) unseen++;
            sum += Math.Log((c + K) / (_singles.GetValueOrDefault(s[i]) + K * Vocab));
        }
        return (n == 0 ? 0 : sum / n, unseen);
    }
}

/// <summary>How eagerly words are corrected.</summary>
public enum Aggressiveness { Careful = 0, Normal = 1, Active = 2 }

/// <summary>Decides whether a just-typed word was entered in the wrong keyboard layout.</summary>
public sealed class LanguageDetector
{
    private sealed record LangData(Func<string, bool?>? Spell, HashSet<string> Common, HashSet<string> Short, BigramModel Model,
        HashSet<string> Prefixes);

    /// <summary>Active (default): 3-letter words by dictionary, softer statistics, prefixes while typing.</summary>
    public Aggressiveness Level { get; set; } = Aggressiveness.Active;

    private readonly Dictionary<Lang, LangData> _data = new();

    /// <summary>Words (lower-case, either form) that must never be auto-converted.</summary>
    public Func<string, bool> IsException { get; set; } = _ => false;

    /// <param name="spell">OS spell checkers per language; a missing entry means "no dictionary for that language".</param>
    public LanguageDetector(IReadOnlyDictionary<Lang, Func<string, bool?>> spell)
    {
        Add(Lang.En, spell, WordData.En + "\n" + WordData.EnMore, WordData.En2);
        Add(Lang.Ru, spell, WordData.Ru + "\n" + WordData.RuMore, WordData.Ru2);
        Add(Lang.Uk, spell, WordData.Uk, WordData.Uk2);
    }

    /// <summary>Back-compat convenience: RU + EN dictionaries only.</summary>
    public LanguageDetector(Func<string, bool?> spellRu, Func<string, bool?> spellEn)
        : this(new Dictionary<Lang, Func<string, bool?>> { [Lang.Ru] = spellRu, [Lang.En] = spellEn }) { }

    private void Add(Lang l, IReadOnlyDictionary<Lang, Func<string, bool?>> spell, string words, HashSet<string> shortWords)
    {
        var common = Split(words);
        common.UnionWith(shortWords);
        // Word starts (4..7 letters) of the common words: "прив", "приве"... for correcting while typing.
        var prefixes = new HashSet<string>();
        foreach (var w in common)
            for (int n = 4; n <= Math.Min(7, w.Length); n++) prefixes.Add(w[..n]);
        _data[l] = new LangData(spell.GetValueOrDefault(l), common, shortWords, new BigramModel(common), prefixes);
    }

    private static HashSet<string> Split(string s) =>
        s.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToHashSet();

    public bool HasDictionary(Lang l) => _data[l].Spell != null;

    /// <summary>A short everyday word of the language (curated list), e.g. "я", "и", "the".</summary>
    public bool IsShortCommon(string word, Lang l) =>
        word.Length > 0 && (_data[l].Short.Contains(word.ToLowerInvariant()) || (word.Length <= 3 && _data[l].Common.Contains(word.ToLowerInvariant())));

    /// <summary>Strip non-letters at both ends.</summary>
    internal static string Core(string s, Lang l)
    {
        int a = 0, b = s.Length;
        while (a < b && !LayoutMap.IsLetterOf(s[a], l)) a++;
        while (b > a && !LayoutMap.IsLetterOf(s[b - 1], l)) b--;
        return s[a..b];
    }

    /// <summary>Core contains something other than letters, hyphen, or (EN/UK) apostrophe.</summary>
    private static bool IsBroken(string core, Lang l) =>
        core.Any(c => !LayoutMap.IsLetterOf(c, l) && c != '-' && !(l != Lang.Ru && c is '\'' or 'ʼ' or '’'));

    public bool IsValid(string word, Lang l)
    {
        if (word.Length == 0 || IsBroken(word, l)) return false;
        var d = _data[l];
        var lower = word.ToLowerInvariant();
        if (d.Common.Contains(lower)) return true;
        if (word.Length <= 2 || d.Spell == null) return false; // only the curated list decides for very short words
        // Spell checkers tend to ignore ALL-CAPS words, so always check the lower-case form too.
        if (d.Spell(lower) == true) return true;
        if (word != lower && word.Any(char.IsLower) && d.Spell(word) == true) return true; // proper names
        if (word.All(c => !char.IsLower(c)))
        {
            var title = char.ToUpperInvariant(lower[0]) + lower[1..];
            if (d.Spell(title) == true) return true;
        }
        return false;
    }

    /// <summary>
    /// Pick the best conversion among <paramref name="candidates"/> (layouts installed besides the current one).
    /// Only Latin↔Cyrillic pairs are auto-corrected: Russian and Ukrainian share too many letters for that to be safe.
    /// When several Cyrillic layouts qualify, the strongest evidence wins, ties go to <paramref name="preferred"/>.
    /// </summary>
    /// <param name="context">Language of the previous word in this line: the next word more likely continues in it.</param>
    public Decision AnalyzeBest(IReadOnlyList<KeyStroke> keys, Lang current, IEnumerable<Lang> candidates, Lang? preferred = null,
        Lang? context = null)
    {
        Decision? best = null;
        foreach (var target in candidates)
        {
            if (target == current || LayoutMap.IsCyrillicLang(target) == LayoutMap.IsCyrillicLang(current)) continue;
            var d = Analyze(keys, current, target, context);
            if (!d.Convert) { best ??= d; continue; }
            if (best is not { Convert: true } || d.Strength > best.Strength || (d.Strength == best.Strength && target == preferred))
                best = d;
        }
        return best ?? new Decision(false, "no candidates", LayoutMap.Render(keys, current), "");
    }

    public Decision Analyze(IReadOnlyList<KeyStroke> keys, Lang current) =>
        Analyze(keys, current, current == Lang.En ? Lang.Ru : Lang.En);

    /// <summary>
    /// Correct while typing (as Caramba does): the first 4–7 letters can't start a word of the current language
    /// but start a common word of the other one ("ghbd" → "прив" of привет). Active level only.
    /// </summary>
    public Decision AnalyzePrefix(IReadOnlyList<KeyStroke> keys, Lang current, IEnumerable<Lang> candidates, Lang? preferred = null)
    {
        var none = new Decision(false, "prefix: no", LayoutMap.Render(keys, current), "");
        if (Level != Aggressiveness.Active || keys.Count is < 4 or > 7) return none;
        if (keys.Any(k => k.IsSpace || k.Vk is >= '0' and <= '9')) return none;
        var cur = LayoutMap.Render(keys, current);
        if (IsException(cur.ToLowerInvariant())) return none;
        var cd = _data[current];
        var curLower = cur.ToLowerInvariant();
        if (cd.Prefixes.Contains(curLower) || cd.Common.Contains(curLower)) return none; // plausible start here
        bool curBroken = IsBroken(cur, current);
        // camelCase / acronyms in the current layout: an identifier, leave it
        if (!curBroken && cur.Skip(1).Any(char.IsUpper)) return none;
        if (!curBroken && cd.Spell != null && cd.Spell(curLower) == true) return none;
        var (_, cUnseen) = cd.Model.Score(curLower, prefix: true);
        if (!curBroken && cUnseen < 2) return none;
        Decision? best = null;
        foreach (var target in candidates)
        {
            if (target == current || LayoutMap.IsCyrillicLang(target) == LayoutMap.IsCyrillicLang(current)) continue;
            var alt = LayoutMap.Render(keys, target);
            var altLower = alt.ToLowerInvariant();
            if (IsBroken(alt, target) || IsException(altLower)) continue;
            if (alt.Skip(1).Any(char.IsUpper) && !alt.Skip(1).All(char.IsUpper)) continue;
            var td = _data[target];
            if (!td.Prefixes.Contains(altLower)) continue;
            if (td.Model.Score(altLower, prefix: true).unseen > 0) continue;
            var d = new Decision(true, "начало частого слова", cur, alt, target, 2);
            if (best == null || target == preferred) best = d;
        }
        return best ?? none;
    }

    public Decision Analyze(IReadOnlyList<KeyStroke> keys, Lang current, Lang other, Lang? context = null)
    {
        bool active = Level == Aggressiveness.Active, careful = Level == Aggressiveness.Careful;
        bool inContext = context == other; // the previous word of the line is already in the other language
        var cur = LayoutMap.Render(keys, current);
        var alt = LayoutMap.Render(keys, other);
        Decision No(string why) => new(false, why, cur, alt, other);
        Decision Yes(string why, int strength) => new(true, why, cur, alt, other, strength);

        if (cur == alt) return No("same text");
        if (keys.Any(k => k.Vk is >= '0' and <= '9')) return No("contains digits");
        if (cur.Contains('@') || cur.Contains('_') || cur.Contains("://") || cur.Contains('\\') || cur.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return No("looks like code/url/email");

        var curCore = Core(cur, current);
        var altCore = Core(alt, other);
        if (altCore.Length < 2) return No("too short");
        if (IsException(cur.ToLowerInvariant()) || IsException(alt.ToLowerInvariant()) ||
            IsException(curCore.ToLowerInvariant()) || IsException(altCore.ToLowerInvariant()))
            return No("exception");

        bool curBroken = curCore.Length == 0 || IsBroken(curCore, current);

        // "yyyy" → "нннн": the same letter three times in a row is a placeholder or a key held down, not a word.
        for (int i = 2; i < altCore.Length; i++)
            if (char.ToLowerInvariant(altCore[i]) == char.ToLowerInvariant(altCore[i - 1]) &&
                char.ToLowerInvariant(altCore[i]) == char.ToLowerInvariant(altCore[i - 2])) return No("repeated letter");

        // Phrase context: after a Russian word, "vs" is "мы" even though "vs" is also an English abbreviation.
        if (inContext && Level != Aggressiveness.Careful && altCore.Length <= 3 && !IsBroken(altCore, other) &&
            _data[other].Short.Contains(altCore.ToLowerInvariant()) && !_data[current].Short.Contains(curCore.ToLowerInvariant()))
            return Yes("short word, phrase context", 2);
        if (!curBroken)
        {
            // camelCase / PascalCase identifiers in the current layout: leave alone
            for (int i = 1; i < curCore.Length; i++)
                if (char.IsUpper(curCore[i]) && char.IsLower(curCore[i - 1])) return No("mixed case");
            // short ALL-CAPS: probably an acronym
            if (curCore.Length <= 4 && curCore.All(c => !char.IsLower(c))) return No("acronym");
            if (IsValid(curCore, current)) return No("current word is valid");
            // No OS dictionary for the current language: we can't tell an unknown real word from garbage,
            // so require the current word to also look statistically implausible.
            if (!HasDictionary(current))
            {
                var (_, cUnseen) = _data[current].Model.Score(curCore);
                if (cUnseen < 2) return No("current language has no dictionary and the word looks plausible");
            }
        }

        if (IsBroken(altCore, other)) return No("alternative is not a word");
        // An upper-case letter inside the converted word comes from Shift+punctuation in code ("t:Prefab" → "еЖЗкуаф",
        // "a<b>" ...), never from a real word typed in the wrong layout.
        for (int i = 1; i < altCore.Length; i++)
            if (char.IsUpper(altCore[i]) && char.IsLower(altCore[i - 1])) return No("alternative has mixed case");

        var od = _data[other];
        if (altCore.Length == 2)
        {
            if (od.Short.Contains(altCore.ToLowerInvariant())) return Yes("short common word", 2);
            if (inContext && !careful && od.Common.Contains(altCore.ToLowerInvariant())) return Yes("short word, phrase context", 2);
            return No("short");
        }

        if (altCore.Length == 3 && !curBroken)
        {
            if (od.Common.Contains(altCore.ToLowerInvariant())) return Yes("common word", 2);
            // Spell checkers accept lots of 3-letter abbreviations (щас → "ofc"), so the dictionary decides only when
            // the typed letters don't even look like the current language (or the phrase is already in the other one).
            if (!careful && (active || inContext) && IsValid(altCore, other))
            {
                var (_, u) = _data[current].Model.Score(curCore);
                if (u >= 2 || inContext) return Yes("dictionary (3 letters)", 2);
            }
            return No("short, not common");
        }

        if (od.Common.Contains(altCore.ToLowerInvariant())) return Yes("common word", 2);
        if (IsValid(altCore, other)) return Yes("dictionary", 3);

        // Neither form is in a dictionary: fall back to letter statistics.
        if (altCore.Length >= 4 && !careful)
        {
            var (aAvg, aUnseen) = od.Model.Score(altCore);
            if (aUnseen == 0)
            {
                if (curBroken) return Yes("statistics (current has punctuation inside)", 1);
                var (cAvg, cUnseen) = _data[current].Model.Score(curCore);
                if (cUnseen >= 2 && aAvg - cAvg > 1.5) return Yes("statistics", 1);
                if ((active || inContext) && cUnseen >= 1 && aAvg - cAvg > 0.8) return Yes("statistics (active)", 1);
            }
        }
        return No("not sure");
    }

    /// <summary>How many words of the text are valid in the language (used to pick RU vs UK for a selection).</summary>
    public int CountValidWords(string text, Lang l) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => IsValid(Core(w, l), l));
}
