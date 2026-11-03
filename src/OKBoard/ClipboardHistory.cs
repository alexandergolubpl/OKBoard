using System.Runtime.InteropServices;

namespace OKBoard;

/// <summary>
/// In-memory history of copied text. Never written to disk. Respects the standard "don't record me" clipboard
/// formats that password managers set (same rules as Windows' own Win+V history).
/// </summary>
internal sealed class ClipboardHistory : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    public const int MaxChars = 100_000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    public sealed record Item(string Text, DateTime Time, string? App);

    private readonly uint _fmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _fmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore"); // KeePass & co.
    private readonly uint _fmtCanInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly Func<string?, bool> _isExcludedApp;
    private readonly Func<bool> _isSuppressed;

    public List<Item> Items { get; } = [];
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 30;
    public event Action? Changed;

    /// <param name="isExcludedApp">Don't record copies made in these apps (password managers etc.).</param>
    /// <param name="isSuppressed">True while OKBoard itself is copying (selection conversion).</param>
    public ClipboardHistory(Func<string?, bool> isExcludedApp, Func<bool> isSuppressed)
    {
        _isExcludedApp = isExcludedApp;
        _isSuppressed = isSuppressed;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // message-only window
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            try { Capture(0); } catch { }
            return;
        }
        base.WndProc(ref m);
    }

    private void Capture(int attempt)
    {
        if (!Enabled || _isSuppressed()) return;
        if (IsClipboardFormatAvailable(_fmtExclude) || IsClipboardFormatAvailable(_fmtViewerIgnore)) return;
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return;

        var owner = GetClipboardOwner();
        var app = Native.GetProcessName(owner != IntPtr.Zero ? owner : Native.GetForegroundWindow());
        if (_isExcludedApp(app)) return;

        if (!OpenClipboard(Handle))
        {
            // Someone else holds the clipboard; try again shortly.
            if (attempt < 3) Task.Delay(60).ContinueWith(_ => BeginRetry(attempt + 1), TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        string? text = null;
        try
        {
            if (IsClipboardFormatAvailable(_fmtCanInclude) && ReadDword(_fmtCanInclude) == 0) return;
            text = ReadText();
        }
        finally { CloseClipboard(); }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return;
        Add(text, app);
    }

    private void BeginRetry(int attempt)
    {
        try { Capture(attempt); } catch { }
    }

    private static uint? ReadDword(uint format)
    {
        var h = GetClipboardData(format);
        if (h == IntPtr.Zero || (ulong)GlobalSize(h) < 4) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try { return (uint)Marshal.ReadInt32(p); }
        finally { GlobalUnlock(h); }
    }

    /// <summary>Read CF_UNICODETEXT, bounded by the block size (never trusts the data to be NUL-terminated).</summary>
    private static string? ReadText()
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == IntPtr.Zero) return null;
        long bytes = (long)(ulong)GlobalSize(h);
        if (bytes < 2) return null;
        int chars = (int)Math.Min(bytes / 2, MaxChars + 1);
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var s = Marshal.PtrToStringUni(p, chars);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        finally { GlobalUnlock(h); }
    }

    public void Add(string text, string? app)
    {
        Items.RemoveAll(i => i.Text == text);
        Items.Insert(0, new Item(text, DateTime.Now, app));
        while (Items.Count > Math.Max(1, MaxItems)) Items.RemoveAt(Items.Count - 1);
        Changed?.Invoke();
    }

    public void Remove(Item item)
    {
        Items.Remove(item);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
