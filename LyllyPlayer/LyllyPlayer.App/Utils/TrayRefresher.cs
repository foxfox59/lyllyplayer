using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LyllyPlayer.Utils;

/// <summary>
/// Best-effort "nudge" to make Explorer re-evaluate notification area layout.
/// This targets the phantom right-edge tray gap that sometimes appears until the user interacts with the tray.
/// </summary>
internal static class TrayRefresher
{
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void ClickToolbarBestEffort(IntPtr toolbar)
    {
        if (toolbar == IntPtr.Zero)
            return;

        // lParam=0 clicks at (0,0) in the toolbar client coords; we only care about triggering a relayout.
        _ = SendMessageW(toolbar, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
        _ = SendMessageW(toolbar, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        _ = SendMessageW(toolbar, WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
        _ = SendMessageW(toolbar, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr TryGetMainTrayToolbar()
    {
        // Shell_TrayWnd -> TrayNotifyWnd -> (optional SysPager) -> ToolbarWindow32
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return IntPtr.Zero;

        var notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return IntPtr.Zero;

        // Some builds wrap the toolbar in SysPager.
        var pager = FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
        if (pager != IntPtr.Zero)
        {
            var tbInPager = FindWindowExW(pager, IntPtr.Zero, "ToolbarWindow32", null);
            if (tbInPager != IntPtr.Zero) return tbInPager;
        }

        return FindWindowExW(notify, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private static IntPtr TryGetOverflowTrayToolbar()
    {
        // Overflow window (hidden icons): NotifyIconOverflowWindow -> ToolbarWindow32
        var overflow = FindWindowW("NotifyIconOverflowWindow", null);
        if (overflow == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null);
    }

    public static void RefreshTrayLayoutBestEffort()
    {
        try { ClickToolbarBestEffort(TryGetMainTrayToolbar()); } catch { /* ignore */ }
        try { ClickToolbarBestEffort(TryGetOverflowTrayToolbar()); } catch { /* ignore */ }
    }

    public static async Task RefreshTrayLayoutBestEffortAsync()
    {
        RefreshTrayLayoutBestEffort();
        await Task.Delay(60).ConfigureAwait(false);
        RefreshTrayLayoutBestEffort();
    }
}

