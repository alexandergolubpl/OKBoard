using System.Runtime.InteropServices;

namespace OKBoard;

/// <summary>
/// Independent witness of keyboard input. Windows silently removes a low-level hook whose callback ever takes
/// longer than LowLevelHooksTimeout (our UI thread stalled on a slow clipboard owner, a COM call...), and
/// nothing tells the app. This second hook lives on its own idle thread and only counts key presses, so it
/// never times out: "the witness saw keys the main hook did not" means the main hook is gone.
/// </summary>
/// <remarks>
/// Raw input (RIDEV_INPUTSINK) was tried first: registering it anywhere in the process stopped Windows from
/// calling our low-level hook at all (caught by the E2E tests).
/// </remarks>
internal sealed class KeyboardWitness : IDisposable
{
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly IntPtr _ignoreExtraInfo;
    private readonly Thread _thread;
    private readonly Native.LowLevelProc _proc;
    private IntPtr _hook;
    private uint _threadId;
    private long _keyDowns;

    /// <summary>Key presses seen so far (our own injected keys excluded).</summary>
    public long KeyDowns => Interlocked.Read(ref _keyDowns);
    public bool Active => _hook != IntPtr.Zero;

    /// <remarks>Create it before the main hook: hooks are called newest first, so the main hook (and anything it
    /// swallows) comes before the witness, and the witness can never count ahead of a healthy main hook.</remarks>
    public KeyboardWitness(IntPtr ignoreExtraInfo)
    {
        _ignoreExtraInfo = ignoreExtraInfo;
        _proc = Proc;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "OKBoard key witness", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(3000);
    }

    private void Loop(ManualResetEventSlim ready)
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        }
        catch { }
        finally { ready.Set(); }
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != _ignoreExtraInfo) Interlocked.Increment(ref _keyDowns);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}

/// <summary>Small rotating diagnostics log. Never receives typed or copied text.</summary>
internal static class DiagLog
{
    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OKBoard", "okboard.log");

    // Writes happen on a background thread: callers include the keyboard hook, where disk I/O could exceed
    // LowLevelHooksTimeout and get the hook removed.
    private static readonly System.Collections.Concurrent.BlockingCollection<string> Queue = new(boundedCapacity: 1000);
    private static readonly Thread Writer = StartWriter();

    private static Thread StartWriter()
    {
        var t = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Move(FilePath, FilePath + ".1", overwrite: true);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch { }
            }
        }) { IsBackground = true, Name = "OKBoard log", Priority = ThreadPriority.BelowNormal };
        t.Start();
        return t;
    }

    public static void Write(string message) =>
        Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
}
