using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

/// <summary>
/// Low-level hook: media keys are handled here and not passed on (<see cref="CallNextHookEx"/> skipped),
/// so other apps’ hooks lower in the chain do not receive them. Hooks run last-installed-first; restart
/// LyllyPlayer after other media apps if you need this app to win consistently.
/// </summary>
public sealed class GlobalMediaHotkeys : IDisposable
{
    private IntPtr _hook = IntPtr.Zero;
    private HookProc? _proc;

    public event EventHandler? PlayPausePressed;
    public event EventHandler? NextPressed;
    public event EventHandler? PrevPressed;

    // Media key virtual-key codes
    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    public GlobalMediaHotkeys(IntPtr hwnd) { }

    public bool TryRegister()
    {
        if (_hook != IntPtr.Zero)
            return true;

        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_hook == IntPtr.Zero)
            return;
        try { UnhookWindowsHookEx(_hook); } catch { /* ignore */ }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    public void Dispose()
    {
        try { Unregister(); } catch { /* ignore */ }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var vk = (uint)kb.vkCode;
                    if (vk == VK_MEDIA_PLAY_PAUSE)
                    {
                        PlayPausePressed?.Invoke(this, EventArgs.Empty);
                        return (IntPtr)1;
                    }
                    if (vk == VK_MEDIA_NEXT_TRACK)
                    {
                        NextPressed?.Invoke(this, EventArgs.Empty);
                        return (IntPtr)1;
                    }
                    if (vk == VK_MEDIA_PREV_TRACK)
                    {
                        PrevPressed?.Invoke(this, EventArgs.Empty);
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}


