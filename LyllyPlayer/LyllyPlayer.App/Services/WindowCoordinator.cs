using System.Windows;
using LyllyPlayer.Utils;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// WPF-specific helpers for auxiliary windows: snapping registration and bounds capture for persistence.
/// </summary>
public static class WindowCoordinator
{
    public static void RegisterSnapping(Window window)
    {
        try { WindowSnapService.Register(window); } catch { /* ignore */ }
    }

    /// <summary>
    /// Rebuilds latch relations from current window positions (best-effort).
    /// Call this after showing/restoring aux windows so snapping clusters work immediately.
    /// </summary>
    public static void RestoreLatchRelationsFromCurrentPositionsBestEffort()
    {
        try { WindowSnapService.RestoreLatchedRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
    }

    public static void CaptureWindowBounds(Window w, out Rect? bounds, out WindowState? state)
    {
        bounds = null;
        state = null;
        try
        {
            var ws = w.WindowState;
            var b = ws == WindowState.Normal
                ? new Rect(w.Left, w.Top, w.Width, w.Height)
                : w.RestoreBounds;
            bounds = b;
            state = ws;
        }
        catch
        {
            // ignore
        }
    }
}
