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

    private ISpellChecker? _checker;                  // touched only on the worker thread
    private readonly Dictionary<string, bool> _cache = new();
    private readonly object _cacheLock = new();
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private long _lastCreateAttempt;
    private const int CallTimeoutMs = 250;

    public bool Available { get; private set; }
    public string Tag { get; }
    /// <summary>Calls that failed (COM error / timeout) and how often the checker was re-created.</summary>
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

    /// <remarks>
    /// The Windows spell checker is an out-of-process COM service. The keyboard hook runs inside an
    /// input-synchronous call, where Windows forbids outgoing cross-process COM calls
    /// (RPC_E_CANTCALLOUT_ININPUTSYNCCALL): calls made from the hook worked while the dictionary was warm and
    /// then failed — and so did re-creating the checker — so after a while only the built-in word lists still
    /// corrected anything. All COM work therefore happens on this dedicated STA thread; the hook only waits
    /// for the answer (a plain wait, not a COM call), at most <see cref="CallTimeoutMs"/>.
    /// </remarks>
    public SpellChecker(string languageTag)
    {
        Tag = languageTag;
        _worker = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true, Name = "OKBoard spell " + languageTag,
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
        Available = Invoke(() => { _checker = Create(languageTag); _lastCreateAttempt = Environment.TickCount64; return _checker != null; }, 5000);
    }

    /// <summary>Run on the worker thread and wait for the result (default if it takes too long).</summary>
    private T? Invoke<T>(Func<T> f, int timeoutMs = CallTimeoutMs)
    {
        if (Thread.CurrentThread == _worker) return f();
        T? result = default;
        using var done = new ManualResetEventSlim();
        try { _work.Add(() => { try { result = f(); } catch { } finally { try { done.Set(); } catch (ObjectDisposedException) { } } }); }
        catch (InvalidOperationException) { return default; }
        return done.Wait(timeoutMs) ? result : default;
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

    /// <summary>Drop the COM object and make a new one (on the worker thread).</summary>
    public bool Recreate() => Invoke(RecreateOnWorker, 5000);

    private bool RecreateOnWorker()
    {
        try { if (_checker != null) Marshal.ReleaseComObject(_checker); } catch { }
        _checker = Create(Tag);
        _lastCreateAttempt = Environment.TickCount64;
        Recreated++;
        Engine.Trace?.Invoke($"spell checker {Tag} re-created (ok={_checker != null})");
        return _checker != null;
    }

    /// <summary>Check a word that must be in the dictionary; re-create the checker if it says otherwise.</summary>
    public bool SelfTest()
    {
        var probe = Tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "солнце"
                  : Tag.StartsWith("uk", StringComparison.OrdinalIgnoreCase) ? "сонце" : "house";
        bool ok = Invoke(() => CheckOnWorker(probe) == true, 5000);
        if (!ok)
        {
            Recreate();
            ok = Invoke(() => CheckOnWorker(probe) == true, 5000);
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

    /// <summary>Worker thread: check, re-creating the COM object once if the call fails.</summary>
    private bool? CheckOnWorker(string word)
    {
        if (_checker == null)
        {
            // a failed re-create is retried at most every 5 s (the service may be restarting)
            if (Environment.TickCount64 - _lastCreateAttempt < 5000 || !RecreateOnWorker()) return null;
        }
        bool? r = TryCheck(word);
        if (r == null)
        {
            Failures++;
            if (Failures % 50 == 1) Engine.Trace?.Invoke($"spell checker {Tag} call failed ({Failures} so far)");
            if (!RecreateOnWorker()) return null;
            r = TryCheck(word);
        }
        return r;
    }

    /// <summary>True if the word is spelled correctly; null if the checker is unavailable. Any thread.</summary>
    public bool? IsCorrect(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        lock (_cacheLock) if (_cache.TryGetValue(word, out var cached)) return cached;
        var r = Invoke<bool?>(() => CheckOnWorker(word));
        if (r is not { } result) return null;
        lock (_cacheLock)
        {
            if (_cache.Count > 20000) _cache.Clear();
            _cache[word] = result;
        }
        return result;
    }
}
