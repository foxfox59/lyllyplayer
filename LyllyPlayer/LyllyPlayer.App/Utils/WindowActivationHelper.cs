using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LyllyPlayer.Utils;

public static class WindowActivationHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void ActivateWindowBestEffort(Window? w)
    {
        try
        {
            if (w is null || !w.IsVisible)
                return;

            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;

            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var fg = GetForegroundWindow();
            var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            var thisThread = GetWindowThreadProcessId(hwnd, out _);

            var attached = false;
            try
            {
                if (fgThread != 0 && thisThread != 0 && fgThread != thisThread)
                    attached = AttachThreadInput(fgThread, thisThread, true);
            }
            catch { /* ignore */ }

            try
            {
                _ = ShowWindow(hwnd, SW_RESTORE);
                _ = BringWindowToTop(hwnd);
                _ = SetForegroundWindow(hwnd);
                try { w.Activate(); } catch { /* ignore */ }
                try { w.Focus(); } catch { /* ignore */ }
            }
            finally
            {
                try
                {
                    if (attached)
                        _ = AttachThreadInput(fgThread, thisThread, false);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    public static void ActivateWithRetriesBestEffort(Window? w)
    {
        if (w is null)
            return;

        // "Duck behind" often happens slightly after Closed; retry a few times.
        var delaysMs = new[] { 0, 50, 150, 350 };
        foreach (var d in delaysMs)
        {
            try
            {
                var t = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(d),
                };
                t.Tick += (_, _) =>
                {
                    try { t.Stop(); } catch { /* ignore */ }
                    ActivateWindowBestEffort(w);
                };
                t.Start();
            }
            catch { /* ignore */ }
        }
    }
}

