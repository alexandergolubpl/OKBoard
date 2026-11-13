using System.Text.RegularExpressions;

namespace OKBoard;

public enum RuleAction { Keep, Switch }
public enum RuleKind { Word, Prefix, Suffix, Contains, Regex }

/// <summary>
/// One user rule, written as a line:  [программа.exe:] шаблон = действие
/// шаблон: слово, маска с * в начале/конце (t:*, *.cs, *abc*) или /регулярное выражение/;
/// действие: да / нет (не исправлять) / en / ru / uk (исправлять именно в эту раскладку).
/// </summary>
public sealed record SwitchRule(string? App, string Pattern, RuleKind Kind, RuleAction Action, Lang? Target, string Source)
{
    internal Regex? Re { get; init; }

    public bool AppliesTo(string? app) => App == null || (app != null && app.Equals(App, StringComparison.OrdinalIgnoreCase));

    public bool Matches(string text)
    {
        if (text.Length == 0) return false;
        var t = text.ToLowerInvariant();
        try
        {
            return Kind switch
            {
                RuleKind.Word => t == Pattern,
                RuleKind.Prefix => t.StartsWith(Pattern, StringComparison.Ordinal),
                RuleKind.Suffix => t.EndsWith(Pattern, StringComparison.Ordinal),
                RuleKind.Contains => t.Contains(Pattern, StringComparison.Ordinal),
                RuleKind.Regex => Re!.IsMatch(text),
                _ => false,
            };
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

public static class RuleParser
{
    public const string Help =
        "Одно правило на строку:  [программа.exe:] шаблон = действие\n" +
        "Шаблон — слово (в любой раскладке), маска со звёздочкой (t:*  *.cs  *abc*) или /регулярное выражение/.\n" +
        "Действие: да — исправлять раскладку, нет — никогда не трогать, en / ru / uk — исправлять именно в эту раскладку.\n" +
        "Строки, начинающиеся с #, — комментарии. «Нет» важнее «да»; правила важнее словаря и исключений.";

    public static List<string> Examples =>
    [
        "# Примеры (уберите # в начале строки, чтобы включить):",
        "# ofc = ru                 «ofc» всегда превращать в «щас»",
        "# t:* = нет                поиск Unity (t:Prefab) никогда не трогать",
        "# *.cs = нет               имена файлов",
        "# /^[A-Z]{2,}-\\d+$/ = нет  номера задач (JIRA-123)",
        "# Code.exe: lerp = нет     только в VS Code",
    ];

    public static List<SwitchRule> Parse(IEnumerable<string> lines, List<string>? errors = null)
    {
        var result = new List<SwitchRule>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.LastIndexOf('=');
            if (line.StartsWith('#') && eq < 0) continue;  // comment
            if (line.StartsWith("# ")) continue;           // commented-out example
            var rule = ParseLine(line, out var error);
            if (rule != null) result.Add(rule);
            else errors?.Add($"{raw.Trim()}  — {error}");
        }
        return result;
    }

    public static SwitchRule? ParseLine(string line, out string error)
    {
        error = "";
        int eq = line.LastIndexOf('=');
        if (eq <= 0) { error = "нет «= действие»"; return null; }
        var left = line[..eq].Trim();
        var right = line[(eq + 1)..].Trim();
        int hash = right.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) right = right[..hash].Trim();

        string? app = null;
        int colon = left.IndexOf(':');
        if (colon > 0 && left[..colon].Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            app = left[..colon].Trim();
            left = left[(colon + 1)..].Trim();
        }
        if (left.Length == 0) { error = "пустой шаблон"; return null; }

        RuleAction action;
        Lang? target = null;
        switch (right.ToLowerInvariant())
        {
            case "да" or "yes" or "switch" or "исправлять" or "переключать": action = RuleAction.Switch; break;
            case "нет" or "no" or "keep" or "никогда" or "не исправлять" or "не трогать": action = RuleAction.Keep; break;
            default:
                if (Settings.ParseLang(right) is { } l) { action = RuleAction.Switch; target = l; break; }
                error = "действие должно быть: да, нет, en, ru или uk";
                return null;
        }

        if (left.Length >= 2 && left.StartsWith('/') && left.EndsWith('/'))
        {
            try
            {
                var re = new Regex(left[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
                return new SwitchRule(app, left, RuleKind.Regex, action, target, line) { Re = re };
            }
            catch (ArgumentException ex) { error = "ошибка в регулярном выражении: " + ex.Message; return null; }
        }

        var p = left.ToLowerInvariant();
        bool starts = p.StartsWith('*'), ends = p.EndsWith('*');
        var core = p.Trim('*');
        if (core.Length == 0) { error = "шаблон из одних звёздочек"; return null; }
        var kind = starts && ends ? RuleKind.Contains : ends ? RuleKind.Prefix : starts ? RuleKind.Suffix : RuleKind.Word;
        return new SwitchRule(app, core, kind, action, target, line);
    }
}
