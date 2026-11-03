namespace OKBoard;

public enum Lang { En, Ru }

/// <summary>A physical key press: virtual key (US positions), Shift state and Caps Lock state at the time of the press.</summary>
public readonly record struct KeyStroke(ushort Vk, bool Shift, bool Caps = false)
{
    public const ushort Space = 0x20;
    public bool IsSpace => Vk == Space;
}

/// <summary>
/// Static map between physical keys and characters for the standard US QWERTY and Russian ЙЦУКЕН layouts.
/// Used both for interpreting keystroke buffers and for converting already-typed text.
/// </summary>
public static class LayoutMap
{
    // vk, en, EN(shift), ru, RU(shift)
    private static readonly (ushort vk, char en, char enS, char ru, char ruS)[] Table =
    [
        ('Q', 'q', 'Q', 'й', 'Й'), ('W', 'w', 'W', 'ц', 'Ц'), ('E', 'e', 'E', 'у', 'У'), ('R', 'r', 'R', 'к', 'К'),
        ('T', 't', 'T', 'е', 'Е'), ('Y', 'y', 'Y', 'н', 'Н'), ('U', 'u', 'U', 'г', 'Г'), ('I', 'i', 'I', 'ш', 'Ш'),
        ('O', 'o', 'O', 'щ', 'Щ'), ('P', 'p', 'P', 'з', 'З'), (0xDB, '[', '{', 'х', 'Х'), (0xDD, ']', '}', 'ъ', 'Ъ'),
        ('A', 'a', 'A', 'ф', 'Ф'), ('S', 's', 'S', 'ы', 'Ы'), ('D', 'd', 'D', 'в', 'В'), ('F', 'f', 'F', 'а', 'А'),
        ('G', 'g', 'G', 'п', 'П'), ('H', 'h', 'H', 'р', 'Р'), ('J', 'j', 'J', 'о', 'О'), ('K', 'k', 'K', 'л', 'Л'),
        ('L', 'l', 'L', 'д', 'Д'), (0xBA, ';', ':', 'ж', 'Ж'), (0xDE, '\'', '"', 'э', 'Э'),
        ('Z', 'z', 'Z', 'я', 'Я'), ('X', 'x', 'X', 'ч', 'Ч'), ('C', 'c', 'C', 'с', 'С'), ('V', 'v', 'V', 'м', 'М'),
        ('B', 'b', 'B', 'и', 'И'), ('N', 'n', 'N', 'т', 'Т'), ('M', 'm', 'M', 'ь', 'Ь'), (0xBC, ',', '<', 'б', 'Б'),
        (0xBE, '.', '>', 'ю', 'Ю'), (0xBF, '/', '?', '.', ','), (0xC0, '`', '~', 'ё', 'Ё'),
        ('1', '1', '!', '1', '!'), ('2', '2', '@', '2', '"'), ('3', '3', '#', '3', '№'), ('4', '4', '$', '4', ';'),
        ('5', '5', '%', '5', '%'), ('6', '6', '^', '6', ':'), ('7', '7', '&', '7', '?'), ('8', '8', '*', '8', '*'),
        ('9', '9', '(', '9', '('), ('0', '0', ')', '0', ')'), (0xBD, '-', '_', '-', '_'), (0xBB, '=', '+', '=', '+'),
        (0xDC, '\\', '|', '\\', '/'), (0xE2, '\\', '|', '\\', '/'), (0x20, ' ', ' ', ' ', ' '),
    ];

    private static readonly Dictionary<ushort, (char en, char enS, char ru, char ruS)> ByVk = new();
    private static readonly Dictionary<char, char> EnToRu = new();
    private static readonly Dictionary<char, char> RuToEn = new();

    static LayoutMap()
    {
        foreach (var t in Table)
        {
            ByVk[t.vk] = (t.en, t.enS, t.ru, t.ruS);
            if (t.vk == 0xE2) continue; // duplicate of backslash key
            EnToRu.TryAdd(t.en, t.ru);
            EnToRu.TryAdd(t.enS, t.ruS);
            RuToEn.TryAdd(t.ru, t.en);
            RuToEn.TryAdd(t.ruS, t.enS);
        }
    }

    public static bool IsLetterKey(ushort vk) => vk is >= 'A' and <= 'Z' or 0xDB or 0xDD or 0xBA or 0xDE or 0xBC or 0xBE or 0xC0;

    /// <summary>Keys that produce printable characters (space excluded).</summary>
    public static bool IsCharKey(ushort vk) => vk != KeyStroke.Space && ByVk.ContainsKey(vk);

    /// <summary>Re-encode a key so that it renders as <paramref name="upper"/>-case in any layout, ignoring Caps Lock.</summary>
    public static KeyStroke WithCase(KeyStroke k, bool upper) => new(k.Vk, upper, false);

    public static char? CharFor(KeyStroke k, Lang lang)
    {
        if (!ByVk.TryGetValue(k.Vk, out var m)) return null;
        bool letter = lang == Lang.En ? k.Vk is >= 'A' and <= 'Z' : IsLetterKey(k.Vk);
        bool shift = k.Shift ^ (k.Caps && letter);
        return lang == Lang.En ? (shift ? m.enS : m.en) : (shift ? m.ruS : m.ru);
    }

    public static string Render(IEnumerable<KeyStroke> keys, Lang lang)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var k in keys)
            if (CharFor(k, lang) is { } c) sb.Append(c);
        return sb.ToString();
    }

    public static Lang Other(Lang l) => l == Lang.En ? Lang.Ru : Lang.En;

    public static bool IsCyrillic(char c) => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
    public static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>Guess which layout the text was typed in (by letter majority). Null if no letters.</summary>
    public static Lang? DetectScript(string text)
    {
        int cyr = 0, lat = 0;
        foreach (var c in text)
        {
            if (IsCyrillic(c)) cyr++;
            else if (IsLatin(c)) lat++;
        }
        if (cyr == 0 && lat == 0) return null;
        return cyr >= lat ? Lang.Ru : Lang.En;
    }

    /// <summary>Re-type text as if it had been typed with the other layout. Direction is detected automatically.</summary>
    /// Each whitespace-separated word is converted according to its own script, so mixed text
    /// ("Hello руддщ") becomes "Руддщ hello"-style swaps per word; words without letters follow the majority.
    public static string ConvertText(string text, Lang? from = null)
    {
        var overall = from ?? DetectScript(text);
        if (overall == null) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i++]); continue; }
            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
            var word = text[i..j];
            var src = from ?? DetectScript(word) ?? overall.Value;
            var map = src == Lang.En ? EnToRu : RuToEn;
            foreach (var c in word) sb.Append(map.TryGetValue(c, out var r) ? r : c);
            i = j;
        }
        return sb.ToString();
    }
}
