using System.Globalization;
using System.Text;

namespace OKBoard;

public static class Transliterator
{
    private static readonly Dictionary<char, string> RuToLat = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };

    // Official Ukrainian romanization (Cabinet of Ministers resolution No. 55, 2010).
    private static readonly Dictionary<char, string> UkToLat = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['є'] = "ie",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l",
        ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu",
        ['я'] = "ia", ['\''] = "", ['ʼ'] = "", ['’'] = "",
    };

    // At the start of a word these letters are romanized differently.
    private static readonly Dictionary<char, string> UkWordStart = new()
    {
        ['є'] = "ye", ['ї'] = "yi", ['й'] = "y", ['ю'] = "yu", ['я'] = "ya",
    };

    // Longest sequences first for greedy matching.
    private static readonly (string lat, string cyr)[] LatToRu =
    [
        ("shch", "щ"), ("sch", "щ"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"),
        ("yo", "ё"), ("yu", "ю"), ("ya", "я"), ("ye", "е"), ("jo", "ё"), ("ju", "ю"), ("ja", "я"),
        ("a", "а"), ("b", "б"), ("c", "ц"), ("d", "д"), ("e", "е"), ("f", "ф"), ("g", "г"), ("h", "х"), ("i", "и"),
        ("j", "й"), ("k", "к"), ("l", "л"), ("m", "м"), ("n", "н"), ("o", "о"), ("p", "п"), ("q", "к"), ("r", "р"),
        ("s", "с"), ("t", "т"), ("u", "у"), ("v", "в"), ("w", "в"), ("x", "кс"), ("y", "ы"), ("z", "з"),
    ];

    private static readonly (string lat, string cyr)[] LatToUk =
    [
        ("shch", "щ"), ("zgh", "зг"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"),
        ("yu", "ю"), ("ya", "я"), ("ye", "є"), ("yi", "ї"), ("iu", "ю"), ("ia", "я"), ("ie", "є"),
        ("a", "а"), ("b", "б"), ("c", "ц"), ("d", "д"), ("e", "е"), ("f", "ф"), ("g", "ґ"), ("h", "г"), ("i", "і"),
        ("j", "й"), ("k", "к"), ("l", "л"), ("m", "м"), ("n", "н"), ("o", "о"), ("p", "п"), ("q", "к"), ("r", "р"),
        ("s", "с"), ("t", "т"), ("u", "у"), ("v", "в"), ("w", "в"), ("x", "кс"), ("y", "и"), ("z", "з"),
    ];

    /// <summary>Cyrillic → Latin if the text is mostly Cyrillic, otherwise Latin → Cyrillic (Russian or Ukrainian).</summary>
    public static string Transliterate(string text, Lang preferredCyrillic = Lang.Ru)
    {
        var script = LayoutMap.DetectScript(text, preferredCyrillic);
        return script is Lang.Ru or Lang.Uk ? ToLatin(text, script.Value) : ToCyrillic(text, preferredCyrillic);
    }

    public static string ToLatin(string text, Lang from = Lang.Ru)
    {
        var table = from == Lang.Uk ? UkToLat : RuToLat;
        var sb = new StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lower = char.ToLowerInvariant(c);
            bool wordStart = i == 0 || !char.IsLetter(text[i - 1]) && text[i - 1] is not ('\'' or 'ʼ' or '’');
            string? t;
            if (from == Lang.Uk && wordStart && UkWordStart.TryGetValue(lower, out var ws)) t = ws;
            else if (from == Lang.Uk && lower == 'г' && i > 0 && char.ToLowerInvariant(text[i - 1]) == 'з') t = "gh"; // зг → zgh
            else if (!table.TryGetValue(lower, out t)) { sb.Append(c); continue; }
            if (char.IsUpper(c) && t.Length > 0)
            {
                // "ЩИ" → "SHCHI", "Щи" → "Shchi"
                bool nextUpper = i + 1 < text.Length && char.IsUpper(text[i + 1]);
                bool prevUpper = i > 0 && char.IsUpper(text[i - 1]);
                t = nextUpper || (prevUpper && (i + 1 >= text.Length || !char.IsLetter(text[i + 1])))
                    ? t.ToUpperInvariant()
                    : char.ToUpperInvariant(t[0]) + t[1..];
            }
            sb.Append(t);
        }
        return sb.ToString();
    }

    public static string ToCyrillic(string text, Lang to = Lang.Ru)
    {
        var table = to == Lang.Uk ? LatToUk : LatToRu;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            foreach (var (lat, cyr) in table)
            {
                if (i + lat.Length > text.Length) continue;
                var chunk = text.Substring(i, lat.Length);
                if (!chunk.Equals(lat, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(char.IsUpper(chunk[0]) ? char.ToUpperInvariant(cyr[0]) + cyr[1..] : cyr);
                i += lat.Length;
                matched = true;
                break;
            }
            if (!matched) sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}

public static class CaseTools
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Cycle: lower → UPPER → Title Case → lower. Mixed text starts with lower.</summary>
    public static string Cycle(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return text;
        if (letters.All(char.IsLower)) return text.ToUpper(Ru);
        if (letters.All(char.IsUpper)) return Ru.TextInfo.ToTitleCase(text.ToLower(Ru));
        return text.ToLower(Ru);
    }

    public static string Invert(string text) =>
        new(text.Select(c => char.IsUpper(c) ? char.ToLower(c, Ru) : char.IsLower(c) ? char.ToUpper(c, Ru) : c).ToArray());

    /// <summary>"hELLO" (typed with Caps Lock on) → true.</summary>
    public static bool LooksLikeCapsAccident(string word)
    {
        var letters = word.Where(char.IsLetter).ToArray();
        return letters.Length >= 3 && char.IsLower(letters[0]) && letters.Skip(1).All(char.IsUpper);
    }

    /// <summary>"ПРивет" (two capitals) → true.</summary>
    public static bool LooksLikeDoubleCapital(string word)
    {
        if (word.Length < 4 || !word.All(char.IsLetter)) return false;
        return char.IsUpper(word[0]) && char.IsUpper(word[1]) && word.Skip(2).All(char.IsLower);
    }
}
