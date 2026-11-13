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

    private ISpellChecker? _checker;
    private readonly Dictionary<string, bool> _cache = new();

    public bool Available => _checker != null;
    public string Tag { get; }
    /// <summary>Calls that failed (COM error) and how often the checker was re-created.</summary>
    public int Failures { get; private set; }
    public int Recreated { get; private set; }
    public bool LastSelfTestOk { get; private set; } = true;

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
        _checker = Create(languageTag);
    }

    private static ISpellChecker? Create(string tag)
    {
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            if (factory.IsSupported(tag, out var ok) == 0 && ok != 0 && factory.CreateSpellChecker(tag, out var checker) == 0)
                return checker;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Drop the COM object and make a new one. After many hours, sleep/resume or a restart of the Windows text
    /// services the old object can start failing every call; every word that needs the dictionary then looks
    /// "unknown" and only the built-in common words still get corrected.
    /// </summary>
    public bool Recreate()
    {
        try { if (_checker != null) Marshal.ReleaseComObject(_checker); } catch { }
        _checker = Create(Tag);
        Recreated++;
        Engine.Trace?.Invoke($"spell checker {Tag} re-created (ok={_checker != null})");
        return _checker != null;
    }

    /// <summary>Check a word that must be in the dictionary; re-create the checker if it says otherwise.</summary>
    public bool SelfTest()
    {
        var probe = Tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "солнце"
                  : Tag.StartsWith("uk", StringComparison.OrdinalIgnoreCase) ? "сонце" : "house";
        _cache.Remove(probe);
        bool ok = IsCorrect(probe) == true;
        if (!ok)
        {
            Recreate();
            _cache.Remove(probe);
            ok = IsCorrect(probe) == true;
            Engine.Trace?.Invoke($"spell checker {Tag} self-test failed; after re-create ok={ok}");
        }
        LastSelfTestOk = ok;
        return ok;
    }

    private bool? TryCheck(string word)
    {
        try
        {
            if (_checker!.Check(word, out var errors) != 0 || errors == null) return null;
            var hr = errors.Next(out var err);
            if (err != IntPtr.Zero) Marshal.Release(err);
            Marshal.ReleaseComObject(errors);
            return hr != 0; // S_FALSE => no errors
        }
        catch { return null; }
    }

    /// <summary>True if the word is spelled correctly; null if the checker is unavailable.</summary>
    public bool? IsCorrect(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        if (_cache.TryGetValue(word, out var cached)) return cached;
        if (_checker == null) return null;
        bool? r = TryCheck(word);
        if (r == null)
        {
            // The COM object went bad: re-create it once and retry.
            Failures++;
            if (Failures % 50 == 1) Engine.Trace?.Invoke($"spell checker {Tag} call failed ({Failures} so far)");
            if (!Recreate()) return null;
            r = TryCheck(word);
            if (r == null) return null;
        }
        bool result = r.Value;
        if (_cache.Count > 20000) _cache.Clear();
        _cache[word] = result;
        return result;
    }
}
