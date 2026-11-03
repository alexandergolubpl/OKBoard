using System.Runtime.InteropServices;

namespace OKBoard;

/// <summary>Thin wrapper over the Windows Spell Checking API (ISpellChecker, available since Windows 8).</summary>
internal sealed class SpellChecker
{
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [PreserveSig] int get_SupportedLanguages(out IntPtr value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out IntPtr value);
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass;

    private readonly ISpellChecker? _checker;
    private readonly Dictionary<string, bool> _cache = new();

    public bool Available => _checker != null;
    public string Tag { get; }

    /// <summary>Language tags the OS spell checker supports (e.g. "en-US", "ru-RU").</summary>
    public static List<string> SupportedLanguages()
    {
        var result = new List<string>();
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            if (factory.get_SupportedLanguages(out var ptr) != 0 || ptr == IntPtr.Zero) return result;
            var e = (System.Runtime.InteropServices.ComTypes.IEnumString)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            var buf = new string[1];
            while (e.Next(1, buf, IntPtr.Zero) == 0) result.Add(buf[0]);
        }
        catch { }
        return result;
    }

    /// <summary>Pick the best supported tag for a language ("en" → "en-US", else "en-GB", ...).</summary>
    public static SpellChecker ForLanguage(string primary, params string[] preferred)
    {
        var all = SupportedLanguages();
        var tag = preferred.FirstOrDefault(p => all.Contains(p, StringComparer.OrdinalIgnoreCase))
                  ?? all.FirstOrDefault(t => t.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase) || t.Equals(primary, StringComparison.OrdinalIgnoreCase))
                  ?? preferred[0];
        return new SpellChecker(tag);
    }

    public SpellChecker(string languageTag)
    {
        Tag = languageTag;
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            if (factory.IsSupported(languageTag, out var ok) == 0 && ok != 0 &&
                factory.CreateSpellChecker(languageTag, out var checker) == 0)
                _checker = checker;
        }
        catch
        {
            _checker = null;
        }
    }

    /// <summary>True if the word is spelled correctly; null if the checker is unavailable.</summary>
    public bool? IsCorrect(string word)
    {
        if (_checker == null || string.IsNullOrWhiteSpace(word)) return null;
        if (_cache.TryGetValue(word, out var cached)) return cached;
        bool result;
        try
        {
            if (_checker.Check(word, out var errors) != 0 || errors == null) return null;
            var hr = errors.Next(out var err);
            if (err != IntPtr.Zero) Marshal.Release(err);
            Marshal.ReleaseComObject(errors);
            result = hr != 0; // S_FALSE => no errors
        }
        catch
        {
            return null;
        }
        if (_cache.Count > 20000) _cache.Clear();
        _cache[word] = result;
        return result;
    }
}
