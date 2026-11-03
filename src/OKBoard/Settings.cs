using System.Text.Json;
using System.Text.Json.Serialization;

namespace OKBoard;

public enum HotkeyAction
{
    ConvertLastWord,
    ConvertSelection,
    ConvertPhrase,
    CycleCase,
    Transliterate,
    ToggleDoNotDisturb,
    AddException,
    ClipboardHistory,
}

/// <summary>Text expansion: typing <see cref="Abbr"/> followed by Space/Enter replaces it with <see cref="Text"/>.</summary>
public sealed class Snippet
{
    public string Abbr { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class Snippets
{
    /// <summary>Substitute {date}, {time}, {datetime}.</summary>
    public static string Expand(string text, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        return text
            .Replace("{datetime}", t.ToString("dd.MM.yyyy HH:mm"))
            .Replace("{date}", t.ToString("dd.MM.yyyy"))
            .Replace("{time}", t.ToString("HH:mm"));
    }
}

public sealed class Settings
{
    public bool AutoSwitch { get; set; } = true;
    public bool FixCapsLockAccident { get; set; } = true;
    public bool FixDoubleCapital { get; set; } = true;
    public bool DoubleShiftConverts { get; set; } = true;
    public bool CapsLockSwitchesLayout { get; set; } = false;
    public bool PauseInFullscreen { get; set; } = true;
    public bool UndoWithBackspace { get; set; } = true;
    public bool LearnFromUndo { get; set; } = true;
    public bool SoundOnAutoSwitch { get; set; } = false;
    public bool SkipPasswordFields { get; set; } = true;
    /// <summary>Don't auto-correct a lone word followed by Enter/Tab (that's how passwords are typed in browsers).</summary>
    public bool SkipSingleWordBeforeEnter { get; set; } = true;
    public bool ReselectAfterConvert { get; set; } = true;
    public bool SnippetsEnabled { get; set; } = true;
    public bool ClipboardHistoryEnabled { get; set; } = true;
    public int ClipboardHistorySize { get; set; } = 30;

    public List<Snippet> Snippets { get; set; } = [];

    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Process names (e.g. "mstsc.exe") where auto-switching is disabled. Hotkeys still work.</summary>
    public List<string> ExcludedApps { get; set; } =
        ["mstsc.exe", "vmconnect.exe", "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];

    /// <summary>Words that are never auto-converted (lower case, any layout).</summary>
    public List<string> ExceptionWords { get; set; } = [];

    /// <summary>Process name → layout ("en"/"ru"/"uk") applied when the app comes to the foreground.</summary>
    public Dictionary<string, string> AppLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<HotkeyAction, string> DefaultHotkeys() => new()
    {
        [HotkeyAction.ConvertLastWord] = "Pause",
        [HotkeyAction.ConvertSelection] = "Shift+Pause",
        [HotkeyAction.ConvertPhrase] = "Ctrl+Shift+Pause",
        [HotkeyAction.CycleCase] = "Alt+Pause",
        [HotkeyAction.Transliterate] = "Ctrl+Alt+T",
        [HotkeyAction.ToggleDoNotDisturb] = "Ctrl+Alt+F12",
        [HotkeyAction.AddException] = "Ctrl+Alt+X",
        [HotkeyAction.ClipboardHistory] = "Ctrl+Alt+V",
    };

    /// <summary>"en" / "ru" / "uk" (also "ua") → layout.</summary>
    public static Lang? ParseLang(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "en" => Lang.En,
        "ru" => Lang.Ru,
        "uk" or "ua" => Lang.Uk,
        _ => null,
    };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OKBoard");
    public static string FilePath { get; set; } = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
                foreach (var (k, v) in DefaultHotkeys()) s.Hotkeys.TryAdd(k, v);
                s.AppLayouts = new Dictionary<string, string>(s.AppLayouts, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch
        {
            // Corrupted file: keep a copy so the user's lists aren't lost when defaults get saved over it.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write to a temp file and swap, so a crash or full disk never leaves a half-written settings file.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>A key combination such as "Ctrl+Shift+Pause".</summary>
public readonly record struct Hotkey(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool c = false, a = false, s = false, w = false;
        Keys key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": c = true; break;
                case "alt": a = true; break;
                case "shift": s = true; break;
                case "win": w = true; break;
                case "break": key = Keys.Pause; break;
                default:
                    if (!Enum.TryParse(raw, true, out key)) return null;
                    break;
            }
        }
        return key == Keys.None ? null : new Hotkey(key, c, a, s, w);
    }

    /// <summary>
    /// A hotkey is swallowed system-wide, so a key that types something (letters, digits, space, Enter...) must
    /// have Ctrl, Alt or Win — otherwise the user could no longer type that character anywhere.
    /// </summary>
    public bool IsSafe
    {
        get
        {
            if (Key is Keys.None or Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin
                or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
                return false;
            if (Ctrl || Alt || Win) return true;
            return Key is Keys.Pause or Keys.Scroll or Keys.Insert or Keys.Apps or >= Keys.F1 and <= Keys.F24;
        }
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
