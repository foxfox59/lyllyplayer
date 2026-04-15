using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

/// <summary>
/// Borderless WPF (<see cref="System.Windows.WindowStyle.None"/>) windows can pick up <c>WS_EX_TOOLWINDOW</c>,
/// which removes normal shell / taskbar integration and can make the process disappear from Task Manager's "Apps" group.
/// </summary>
internal static class ShellWindowStyle
{
    private const int GwlExstyle = -20;
    private const uint WsExToolwindow = 0x00000080;
    private const uint WsExAppwindow = 0x00040000;

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpNoactivate = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    public static void EnsureAppearsAsForegroundApp(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // GWL_EXSTYLE is a 32-bit mask; avoid accidental sign-extension when reading LONG_PTR.
        var before = unchecked((uint)GetWindowLongPtr(hwnd, GwlExstyle).ToInt64());
        var after = (before & ~WsExToolwindow) | WsExAppwindow;
        if (after == before)
            return;

        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)(unchecked((int)after)));
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNozorder | SwpFramechanged | SwpNoactivate);
    }
}
