using System.Runtime.InteropServices;

namespace OKBoard;

/// <summary>
/// Puts text on the clipboard with *delayed rendering*: the data is produced only when an application actually
/// asks for it (WM_RENDERFORMAT). This tells us exactly when the paste happened, so the user's original
/// clipboard is restored only after the target app has read our text — never before (which would make the app
/// paste the user's previous clipboard contents, e.g. a copied password).
/// </summary>
internal sealed class DelayedClipboard : NativeWindow, IDisposable
{
    private const int WM_RENDERFORMAT = 0x0305, WM_RENDERALLFORMATS = 0x0306, WM_DESTROYCLIPBOARD = 0x0307;
    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    private static readonly string[] PrivacyFormats =
        ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

    private string? _text;
    private TaskCompletionSource? _requested;

    public DelayedClipboard() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

    /// <summary>True while the clipboard still holds what we offered (nobody copied anything since).</summary>
    public bool IsOwner => GetClipboardOwner() == Handle;

    /// <summary>
    /// Offer text. The returned task completes when some application reads it. Null if the clipboard stayed
    /// locked by another program. Waits asynchronously: this runs on the thread that also serves the keyboard hook.
    /// </summary>
    public async Task<Task?> OfferAsync(string text)
    {
        for (int i = 0; i < 20 && !OpenClipboard(Handle); i++)
        {
            if (i == 19) return null;
            await Task.Delay(25);
        }
        return OfferOpened(text);
    }

    /// <summary>
    /// Wait (asynchronously) until no other program holds the clipboard, so that the synchronous WinForms
    /// clipboard calls that follow don't block the hook thread with their own sleep-and-retry loops.
    /// </summary>
    public async Task WaitAvailableAsync(int timeoutMs = 500)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            if (OpenClipboard(Handle)) { CloseClipboard(); return; }
            await Task.Delay(25);
        }
    }

    private Task? OfferOpened(string text)
    {
        try
        {
            if (!EmptyClipboard()) return null;
            _text = text;
            _requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetClipboardData(CF_UNICODETEXT, IntPtr.Zero); // NULL handle = "render on request"
            foreach (var name in PrivacyFormats) // DWORD 0: keep it out of Win+V history and cloud sync
                SetOwned(RegisterClipboardFormat(name), Alloc(new byte[4]));
            return _requested.Task;
        }
        finally { CloseClipboard(); }
    }

    private static IntPtr Alloc(byte[] bytes)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return IntPtr.Zero; }
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        return h;
    }

    private static IntPtr AllocText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2]; // UTF-16 + terminating NUL
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return Alloc(bytes);
    }

    /// <summary>SetClipboardData takes ownership only on success; free the memory otherwise.</summary>
    private static void SetOwned(uint format, IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_RENDERFORMAT:
                // The requesting app already has the clipboard open; just hand over the data.
                if ((uint)(long)m.WParam == CF_UNICODETEXT && _text != null)
                {
                    SetOwned(CF_UNICODETEXT, AllocText(_text));
                    _requested?.TrySetResult();
                }
                return;
            case WM_RENDERALLFORMATS:
                // We are going away while still owning unrendered data.
                if (_text != null && OpenClipboard(Handle))
                {
                    if (GetClipboardOwner() == Handle) SetOwned(CF_UNICODETEXT, AllocText(_text));
                    CloseClipboard();
                }
                return;
            case WM_DESTROYCLIPBOARD:
                _text = null;
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
