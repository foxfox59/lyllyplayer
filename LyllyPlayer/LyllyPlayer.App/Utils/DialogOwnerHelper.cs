using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyllyPlayer.Utils;

public static class DialogOwnerHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_HWNDNEXT = 2;

    public static Window? GetBestOwnerWindow()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return null;

            var visible = new List<Window>();
            Window? active = null;
            foreach (Window w in app.Windows)
            {
                if (w is null || !w.IsVisible)
                    continue;

                if (w.IsActive)
                    active ??= w;
                visible.Add(w);
            }

            // Best-effort: pick the top-most window in *actual Win32 z-order* among our visible windows.
            // This prevents common dialogs from appearing under another aux window even when Topmost/Owner chains differ.
            try
            {
                var byHwnd = new Dictionary<IntPtr, Window>();
                foreach (var w in visible)
                {
                    try
                    {
                        var hwnd = new WindowInteropHelper(w).Handle;
                        if (hwnd != IntPtr.Zero && !byHwnd.ContainsKey(hwnd))
                            byHwnd.Add(hwnd, w);
                    }
                    catch { /* ignore */ }
                }

                var h = GetTopWindow(IntPtr.Zero);
                while (h != IntPtr.Zero)
                {
                    if (byHwnd.TryGetValue(h, out var win))
                        return win;
                    h = GetWindow(h, GW_HWNDNEXT);
                }
            }
            catch { /* ignore */ }

            if (active is not null)
                return active;
            return app.MainWindow;
        }
        catch
        {
            return System.Windows.Application.Current?.MainWindow;
        }
    }
}

