using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyllyPlayer.Utils;

public static class WindowZOrderHelper
{
    // BringWindowToTop changes z-order but can also activate; we explicitly avoid activation with SetWindowPos.
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOP = new IntPtr(0);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private static IntPtr _lastKeepHwnd = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    public static void PulseRaiseAboveOtherAppsNoActivateBestEffort(Window keep)
    {
        try
        {
            if (!keep.IsVisible)
                return;

            var keepHwnd = new WindowInteropHelper(keep).Handle;
            if (keepHwnd == IntPtr.Zero || !IsWindow(keepHwnd))
                return;

            // If user already has Topmost=true, do not toggle it off.
            var alreadyTopmost = false;
            try { alreadyTopmost = keep.Topmost; } catch { /* ignore */ }

            _ = SetWindowPos(keepHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (!alreadyTopmost)
                _ = SetWindowPos(keepHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { /* ignore */ }
    }

    public static void ForceForegroundBestEffort(Window keep)
    {
        try
        {
            if (!keep.IsVisible)
                return;

            var keepHwnd = new WindowInteropHelper(keep).Handle;
            if (keepHwnd == IntPtr.Zero || !IsWindow(keepHwnd))
                return;

            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                _ = SetForegroundWindow(keepHwnd);
                _ = BringWindowToTop(keepHwnd);
                return;
            }

            var fgThread = GetWindowThreadProcessId(fg, out _);
            var keepThread = GetWindowThreadProcessId(keepHwnd, out _);

            // If Windows switched foreground already, we may need to temporarily attach input
            // to legally take foreground back.
            var attached = false;
            try
            {
                // Important: attach the *foreground thread* to the keep thread (not the other way around).
                // This matches the known workaround used in production WPF apps.
                if (fgThread != 0 && keepThread != 0 && fgThread != keepThread)
                    attached = AttachThreadInput(fgThread, keepThread, true);
            }
            catch { /* ignore */ }

            try
            {
                _ = SetForegroundWindow(keepHwnd);
                _ = BringWindowToTop(keepHwnd);
            }
            finally
            {
                try
                {
                    if (attached)
                        _ = AttachThreadInput(fgThread, keepThread, false);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    public static void BringAppWindowsToFrontNoActivate(Window? keepActive)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return;

            var keepHwnd = IntPtr.Zero;
            try
            {
                if (keepActive is not null && keepActive.IsVisible)
                    keepHwnd = new WindowInteropHelper(keepActive).Handle;
            }
            catch { /* ignore */ }

            if (keepHwnd != IntPtr.Zero)
            {
                _lastKeepHwnd = keepHwnd;
            }
            else if (_lastKeepHwnd != IntPtr.Zero)
            {
                // If the last "keep" window was closed, its HWND is no longer valid.
                if (IsWindow(_lastKeepHwnd))
                    keepHwnd = _lastKeepHwnd;
                else
                    _lastKeepHwnd = IntPtr.Zero;
            }

            // First, raise the keep window itself to the top (without activation). This prevents the situation
            // where the aux gets activated but the app stack is still under another app in z-order.
            if (keepHwnd != IntPtr.Zero && IsWindow(keepHwnd))
                _ = SetWindowPos(keepHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            foreach (Window w in app.Windows)
            {
                try
                {
                    if (w is null)
                        continue;
                    if (!w.IsVisible)
                        continue;
                    if (ReferenceEquals(w, keepActive))
                        continue;

                    var hwnd = new WindowInteropHelper(w).Handle;
                    if (hwnd == IntPtr.Zero)
                        continue;

                    // Keep the actively interacted aux window on top: move other app windows just behind it.
                    // If keepActive is unknown, fall back to a simple "bring to top" without activation.
                    var insertAfter = (keepHwnd != IntPtr.Zero && IsWindow(keepHwnd)) ? keepHwnd : HWND_TOP;
                    _ = SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    public static void TransferForegroundFromClosingWindowBestEffort(Window closing, Window? keep)
    {
        try
        {
            if (keep is null || !keep.IsVisible)
                return;

            var closingHwnd = new WindowInteropHelper(closing).Handle;
            if (closingHwnd == IntPtr.Zero || !IsWindow(closingHwnd))
                return;

            var keepHwnd = new WindowInteropHelper(keep).Handle;
            if (keepHwnd == IntPtr.Zero || !IsWindow(keepHwnd))
                return;

            // Only attempt to transfer when the closing window is actually the current foreground window.
            if (GetForegroundWindow() != closingHwnd)
                return;

            _lastKeepHwnd = keepHwnd;

            // Best-effort: put keep on top and request foreground.
            _ = SetWindowPos(keepHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _ = SetForegroundWindow(keepHwnd);
        }
        catch { /* ignore */ }
    }

    public static Window? GetActiveAppWindowBestEffort()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return null;
            foreach (Window w in app.Windows)
            {
                try
                {
                    if (w is not null && w.IsVisible && w.IsActive)
                        return w;
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static Window? GetBestKeepWindowBestEffort()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return null;

            // Prefer currently active app window.
            var active = GetActiveAppWindowBestEffort();
            if (active is not null)
                return active;

            // Otherwise, prefer the window matching the last keep hwnd.
            if (_lastKeepHwnd != IntPtr.Zero)
            {
                if (!IsWindow(_lastKeepHwnd))
                {
                    _lastKeepHwnd = IntPtr.Zero;
                }
                else
                {
                foreach (Window w in app.Windows)
                {
                    try
                    {
                        if (w is null || !w.IsVisible)
                            continue;
                        var hwnd = new WindowInteropHelper(w).Handle;
                        if (hwnd != IntPtr.Zero && hwnd == _lastKeepHwnd)
                            return w;
                    }
                    catch { /* ignore */ }
                }
                }
            }

            // Fallback: main window if visible.
            if (app.MainWindow is { IsVisible: true } mw)
                return mw;
        }
        catch { /* ignore */ }
        return null;
    }
}

