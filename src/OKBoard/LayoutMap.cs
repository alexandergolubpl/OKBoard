using System.Text;

namespace OKBoard;

public enum Lang { En, Ru, Uk }

/// <summary>A physical key press: virtual key (US positions), Shift state and Caps Lock state at the time of the press.</summary>
public readonly record struct KeyStroke(ushort Vk, bool Shift, bool Caps = false)
{
    public const ushort Space = 0x20;
    public bool IsSpace => Vk == Space;
}

/// <summary>
/// Map between physical keys and characters for the supported layouts (US English, Russian ЙЦУКЕН, Ukrainian).
/// Built-in tables are defaults; <see cref="Override"/> replaces them with what the installed Windows layout
/// actually produces (e.g. "Ukrainian" vs "Ukrainian (Enhanced)" differ on the ` key).
/// </summary>
public static class LayoutMap
{
    // vk, en, EN, ru, RU, uk, UK   (uk = Windows "Ukrainian (Enhanced)", the default for the Ukrainian language)
    private static readonly (ushort vk, string en, string ru, string uk)[] Defaults =
    [
        ('Q', "qQ", "йЙ", "йЙ"), ('W', "wW", "цЦ", "цЦ"), ('E', "eE", "уУ", "уУ"), ('R', "rR", "кК", "кК"),
        ('T', "tT", "еЕ", "еЕ"), ('Y', "yY", "нН", "нН"), ('U', "uU", "гГ", "гГ"), ('I', "iI", "шШ", "шШ"),
        ('O', "oO", "щЩ", "щЩ"), ('P', "pP", "зЗ", "зЗ"), (0xDB, "[{", "хХ", "хХ"), (0xDD, "]}", "ъЪ", "їЇ"),
        ('A', "aA", "фФ", "фФ"), ('S', "sS", "ыЫ", "іІ"), ('D', "dD", "вВ", "вВ"), ('F', "fF", "аА", "аА"),
        ('G', "gG", "пП", "пП"), ('H', "hH", "рР", "рР"), ('J', "jJ", "оО", "оО"), ('K', "kK", "лЛ", "лЛ"),
        ('L', "lL", "дД", "дД"), (0xBA, ";:", "жЖ", "жЖ"), (0xDE, "'\"", "эЭ", "єЄ"),
        ('Z', "zZ", "яЯ", "яЯ"), ('X', "xX", "чЧ", "чЧ"), ('C', "cC", "сС", "сС"), ('V', "vV", "мМ", "мМ"),
        ('B', "bB", "иИ", "иИ"), ('N', "nN", "тТ", "тТ"), ('M', "mM", "ьЬ", "ьЬ"), (0xBC, ",<", "бБ", "бБ"),
        (0xBE, ".>", "юЮ", "юЮ"), (0xBF, "/?", ".,", ".,"), (0xC0, "`~", "ёЁ", "'₴"),
        ('1', "1!", "1!", "1!"), ('2', "2@", "2\"", "2\""), ('3', "3#", "3№", "3№"), ('4', "4$", "4;", "4;"),
        ('5', "5%", "5%", "5%"), ('6', "6^", "6:", "6:"), ('7', "7&", "7?", "7?"), ('8', "8*", "8*", "8*"),
        ('9', "9(", "9(", "9("), ('0', "0)", "0)", "0)"), (0xBD, "-_", "-_", "-_"), (0xBB, "=+", "=+", "=+"),
        (0xDC, "\\|", "\\/", "\\/"), (0xE2, "\\|", "\\/", "ґҐ"), (0x20, "  ", "  ", "  "),
    ];

    public static readonly Lang[] All = [Lang.En, Lang.Ru, Lang.Uk];

    private static readonly Dictionary<Lang, Dictionary<ushort, (char n, char s)>> ByVk = new();
    private static readonly Dictionary<Lang, Dictionary<char, KeyStroke>> ByChar = new();

    static LayoutMap()
    {
        foreach (var l in All) ByVk[l] = new();
        foreach (var (vk, en, ru, uk) in Defaults)
        {
            ByVk[Lang.En][vk] = (en[0], en[1]);
            ByVk[Lang.Ru][vk] = (ru[0], ru[1]);
            ByVk[Lang.Uk][vk] = (uk[0], uk[1]);
        }
        foreach (var l in All) RebuildReverse(l);
    }

    private static void RebuildReverse(Lang l)
    {
        var map = new Dictionary<char, KeyStroke>();
        // Unshifted first, and the main backslash key before the ISO 102nd key.
        foreach (var (vk, (n, s)) in ByVk[l].OrderBy(kv => kv.Key == 0xE2 ? 1 : 0))
        {
            map.TryAdd(n, new KeyStroke(vk, false));
            map.TryAdd(s, new KeyStroke(vk, true));
        }
        ByChar[l] = map;
    }

    /// <summary>Replace the default for one key with what the installed layout produces.</summary>
    public static void Override(Lang l, ushort vk, char normal, char shifted)
    {
        if (!ByVk[l].ContainsKey(vk) || vk == KeyStroke.Space) return;
        ByVk[l][vk] = (normal, shifted);
        RebuildReverse(l);
    }

    public static IEnumerable<ushort> Keys => ByVk[Lang.En].Keys;

    public static bool IsCyrillicLang(Lang l) => l != Lang.En;

    /// <summary>Keys that produce printable characters (space excluded).</summary>
    public static bool IsCharKey(ushort vk) => vk != KeyStroke.Space && ByVk[Lang.En].ContainsKey(vk);

    /// <summary>Re-encode a key so that it renders as upper/lower case in any layout, ignoring Caps Lock.</summary>
    public static KeyStroke WithCase(KeyStroke k, bool upper) => new(k.Vk, upper, false);

    public static char? CharFor(KeyStroke k, Lang lang)
    {
        if (!ByVk[lang].TryGetValue(k.Vk, out var m)) return null;
        bool shift = k.Shift ^ (k.Caps && char.IsLetter(m.n)); // Caps Lock affects letters only
        return shift ? m.s : m.n;
    }

    public static string Render(IEnumerable<KeyStroke> keys, Lang lang)
    {
        var sb = new StringBuilder();
        foreach (var k in keys)
            if (CharFor(k, lang) is { } c) sb.Append(c);
        return sb.ToString();
    }

    public static bool IsCyrillic(char c) =>
        c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё' or 'і' or 'І' or 'ї' or 'Ї' or 'є' or 'Є' or 'ґ' or 'Ґ';

    public static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static bool IsLetterOf(char c, Lang l) => l switch
    {
        Lang.En => IsLatin(c),
        Lang.Ru => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё',
        _ => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'і' or 'І' or 'ї' or 'Ї' or 'є' or 'Є' or 'ґ' or 'Ґ'
                 && c is not ('ы' or 'Ы' or 'э' or 'Э' or 'ъ' or 'Ъ'),
    };

    /// <summary>Which Cyrillic language the text is definitely in (by letters unique to it), or null.</summary>
    public static Lang? DetectCyrillic(string text)
    {
        foreach (var c in text)
        {
            if (c is 'і' or 'І' or 'ї' or 'Ї' or 'є' or 'Є' or 'ґ' or 'Ґ') return Lang.Uk;
            if (c is 'ы' or 'Ы' or 'э' or 'Э' or 'ъ' or 'Ъ' or 'ё' or 'Ё') return Lang.Ru;
        }
        return null;
    }

    /// <summary>Latin vs Cyrillic by letter majority; Cyrillic resolved via <see cref="DetectCyrillic"/> or the preference.</summary>
    public static Lang? DetectScript(string text, Lang preferredCyrillic = Lang.Ru)
    {
        int cyr = 0, lat = 0;
        foreach (var c in text)
        {
            if (IsCyrillic(c)) cyr++;
            else if (IsLatin(c)) lat++;
        }
        if (cyr == 0 && lat == 0) return null;
        return lat > cyr ? Lang.En : DetectCyrillic(text) ?? preferredCyrillic;
    }

    /// <summary>Re-type text as if it had been typed in <paramref name="to"/> instead of <paramref name="from"/>.</summary>
    public static string ConvertText(string text, Lang from, Lang to)
    {
        if (from == to) return text;
        var src = ByChar[from];
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(src.TryGetValue(c, out var k) && CharFor(k, to) is { } r ? r : c);
        return sb.ToString();
    }

    /// <summary>
    /// Fix text typed in the wrong layout, word by word: Latin words become <paramref name="cyrillic"/>,
    /// Cyrillic words become English. Words without letters follow the majority.
    /// </summary>
    public static string ConvertText(string text, Lang cyrillic = Lang.Ru)
    {
        var overall = DetectScript(text, cyrillic);
        if (overall == null) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i++]); continue; }
            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
            var word = text[i..j];
            var src = DetectScript(word, overall == Lang.En ? cyrillic : overall.Value) ?? overall.Value;
            sb.Append(ConvertText(word, src, src == Lang.En ? cyrillic : Lang.En));
            i = j;
        }
        return sb.ToString();
    }
}
