namespace OKBoard;

public sealed record Decision(bool Convert, string Reason, string Current, string Alternative);

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

    public (double avg, int unseen) Score(string word)
    {
        var s = "^" + word.ToLowerInvariant() + "$";
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

/// <summary>Decides whether a just-typed word was entered in the wrong keyboard layout.</summary>
public sealed class LanguageDetector
{
    private readonly Func<string, bool?> _spellRu, _spellEn;
    private readonly HashSet<string> _commonRu, _commonEn;
    private readonly BigramModel _modelRu, _modelEn;

    /// <summary>Words (lower-case, either form) that must never be auto-converted.</summary>
    public Func<string, bool> IsException { get; set; } = _ => false;

    public LanguageDetector(Func<string, bool?> spellRu, Func<string, bool?> spellEn)
    {
        _spellRu = spellRu;
        _spellEn = spellEn;
        _commonRu = Split(WordData.Ru);
        _commonEn = Split(WordData.En);
        _commonRu.UnionWith(WordData.Ru2);
        _commonEn.UnionWith(WordData.En2);
        _modelRu = new BigramModel(_commonRu);
        _modelEn = new BigramModel(_commonEn);
    }

    private static HashSet<string> Split(string s) =>
        s.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToHashSet();

    private static bool IsLetterOf(char c, Lang l) => l == Lang.Ru ? LayoutMap.IsCyrillic(c) : LayoutMap.IsLatin(c);

    /// <summary>Strip non-letters at both ends.</summary>
    internal static string Core(string s, Lang l)
    {
        int a = 0, b = s.Length;
        while (a < b && !IsLetterOf(s[a], l)) a++;
        while (b > a && !IsLetterOf(s[b - 1], l)) b--;
        return s[a..b];
    }

    /// <summary>Core contains something other than letters, hyphen, or (English) apostrophe.</summary>
    private static bool IsBroken(string core, Lang l) =>
        core.Any(c => !IsLetterOf(c, l) && c != '-' && !(l == Lang.En && c == '\''));

    public bool IsValid(string word, Lang l)
    {
        if (word.Length == 0 || IsBroken(word, l)) return false;
        var lower = word.ToLowerInvariant();
        var common = l == Lang.Ru ? _commonRu : _commonEn;
        if (common.Contains(lower)) return true;
        if (word.Length <= 2) return false; // only the curated list decides for very short words
        var spell = l == Lang.Ru ? _spellRu : _spellEn;
        // Spell checkers tend to ignore ALL-CAPS words, so always check the lower-case form too.
        if (spell(lower) == true) return true;
        if (word != lower && word.Any(char.IsLower) && spell(word) == true) return true; // proper names
        if (word.All(c => !char.IsLower(c)))
        {
            var title = char.ToUpperInvariant(lower[0]) + lower[1..];
            if (spell(title) == true) return true;
        }
        return false;
    }

    public Decision Analyze(IReadOnlyList<KeyStroke> keys, Lang current)
    {
        var other = LayoutMap.Other(current);
        var cur = LayoutMap.Render(keys, current);
        var alt = LayoutMap.Render(keys, other);
        Decision No(string why) => new(false, why, cur, alt);

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
        if (!curBroken)
        {
            // camelCase / PascalCase identifiers in the current layout: leave alone
            for (int i = 1; i < curCore.Length; i++)
                if (char.IsUpper(curCore[i]) && char.IsLower(curCore[i - 1])) return No("mixed case");
            // short ALL-CAPS: probably an acronym
            if (curCore.Length <= 4 && curCore.All(c => !char.IsLower(c))) return No("acronym");
            if (IsValid(curCore, current)) return No("current word is valid");
        }

        if (IsBroken(altCore, other)) return No("alternative is not a word");

        if (altCore.Length == 2)
        {
            var list = other == Lang.Ru ? WordData.Ru2 : WordData.En2;
            return list.Contains(altCore.ToLowerInvariant()) ? new(true, "short common word", cur, alt) : No("short");
        }

        if (altCore.Length == 3 && !curBroken)
        {
            // Spell checkers accept lots of 3-letter abbreviations (щас → "ofc"): trust only the curated list.
            var common = other == Lang.Ru ? _commonRu : _commonEn;
            return common.Contains(altCore.ToLowerInvariant()) ? new(true, "common word", cur, alt) : No("short, not common");
        }

        if (IsValid(altCore, other)) return new(true, "dictionary", cur, alt);

        // Neither form is in a dictionary: fall back to letter statistics, very conservatively.
        if (altCore.Length >= 4)
        {
            var (aAvg, aUnseen) = (other == Lang.Ru ? _modelRu : _modelEn).Score(altCore);
            if (aUnseen == 0)
            {
                if (curBroken) return new(true, "statistics (current has punctuation inside)", cur, alt);
                var (cAvg, cUnseen) = (current == Lang.Ru ? _modelRu : _modelEn).Score(curCore);
                if (cUnseen >= 2 && aAvg - cAvg > 1.5) return new(true, "statistics", cur, alt);
            }
        }
        return No("not sure");
    }
}
