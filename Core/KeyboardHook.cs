using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WordLinkSolver.Core;

/// <summary>
/// Low-level keyboard hook so the Space/B/R/F/Backspace shortcuts work while
/// the user is interacting with the game window under the overlay (our app is
/// not the foreground window then).
///
/// The hook never swallows keys — every event is passed on — and the consumer
/// decides whether a key press is relevant (e.g. only when the foreground
/// window is the app itself or the game under the overlay).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Keep a strong reference to the delegate so the GC never collects it while hooked.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    /// <summary>Raised on the hooking (UI) thread for every key-down, with the virtual key code.</summary>
    public event Action<System.Windows.Forms.Keys>? KeyDown;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>Installs the hook. Call from the UI thread (its message loop dispatches the callback).</summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero)
            return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Trace.WriteLine($"Keyboard hook install failed (error {Marshal.GetLastWin32Error()}); falling back to in-window keys.");
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam); // first field of KBDLLHOOKSTRUCT
                try
                {
                    KeyDown?.Invoke((System.Windows.Forms.Keys)vk);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Hotkey handler failed: {ex}");
                }
            }
        }
        // Never block the key — the game should still receive it.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
