using System;
using System.Windows;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Which auxiliary window — drives small differences in snap inference/placement.
/// </summary>
public enum AuxSnapWindowKind
{
    Playlist,
    Options,
    Lyrics,
}

/// <summary>
/// Unified geometry for persisted snap-to-main (edge + one meaningful offset axis).
/// </summary>
public readonly struct AuxSnapPersistResult
{
    public bool Snapped { get; init; }
    /// <summary>None | Left | Right | Top | Bottom (case-insensitive when parsed).</summary>
    public string Edge { get; init; }
    public double DockXOffset { get; init; }
    public double DockYOffset { get; init; }
}

/// <summary>
/// Static snap inference / placement shared by Playlist, Options, and Lyrics windows.
/// </summary>
public static class AuxWindowSnapHelper
{
    private static double Overlap1D(double a0, double a1, double b0, double b1)
        => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    /// <summary>
    /// Infer snap edge and dock offsets from current main vs aux outer bounds (persist / latch restore).
    /// </summary>
    public static AuxSnapPersistResult InferPersistedSnap(
        Rect mainOuter,
        Rect auxOuter,
        double snapGapPx,
        double persistAdjacencyPx,
        double minOverlapPx,
        AuxSnapWindowKind kind)
    {
        var mainRight = mainOuter.Left + mainOuter.Width;
        var mainBottom = mainOuter.Top + mainOuter.Height;
        var auxRight = auxOuter.Left + auxOuter.Width;
        var auxBottom = auxOuter.Top + auxOuter.Height;

        var vOverlap = Overlap1D(mainOuter.Top, mainBottom, auxOuter.Top, auxBottom);
        var hOverlap = Overlap1D(mainOuter.Left, mainRight, auxOuter.Left, auxRight);

        if (Math.Abs(auxOuter.Left - (mainRight + snapGapPx)) <= persistAdjacencyPx && vOverlap >= minOverlapPx)
        {
            return new AuxSnapPersistResult
            {
                Snapped = true,
                Edge = SnapEdges.Right,
                DockXOffset = 0,
                DockYOffset = auxOuter.Top - mainOuter.Top,
            };
        }

        if (Math.Abs(auxRight - (mainOuter.Left - snapGapPx)) <= persistAdjacencyPx && vOverlap >= minOverlapPx)
        {
            return new AuxSnapPersistResult
            {
                Snapped = true,
                Edge = SnapEdges.Left,
                DockXOffset = 0,
                DockYOffset = auxOuter.Top - mainOuter.Top,
            };
        }

        if (Math.Abs(auxOuter.Top - (mainBottom + snapGapPx)) <= persistAdjacencyPx && hOverlap >= minOverlapPx)
        {
            return new AuxSnapPersistResult
            {
                Snapped = true,
                Edge = SnapEdges.Bottom,
                DockXOffset = auxOuter.Left - mainOuter.Left,
                DockYOffset = 0,
            };
        }

        if (Math.Abs(auxBottom - (mainOuter.Top - snapGapPx)) <= persistAdjacencyPx && hOverlap >= minOverlapPx)
        {
            return new AuxSnapPersistResult
            {
                Snapped = true,
                Edge = SnapEdges.Top,
                DockXOffset = auxOuter.Left - mainOuter.Left,
                DockYOffset = 0,
            };
        }

        return new AuxSnapPersistResult
        {
            Snapped = false,
            Edge = SnapEdges.None,
            DockXOffset = 0,
            DockYOffset = 0,
        };
    }

    /// <summary>
    /// Bottom-snapped Options: keep horizontal span over main chrome only when narrower than main.
    /// </summary>
    public static double ClampOptionsBottomSnapLeft(double mainLeft, double mainRight, double optionsOuterWidth, double dockX)
    {
        var mainW = Math.Max(0, mainRight - mainLeft);
        var inner = mainLeft + dockX;
        if (optionsOuterWidth <= mainW + 1e-6)
            return Math.Clamp(inner, mainLeft, mainRight - optionsOuterWidth);
        return mainRight - optionsOuterWidth;
    }

    /// <summary>
    /// Apply snapped placement to <paramref name="w"/>; updates dock offsets in-memory to match placed position.
    /// Returns false if not snapped or edge is None.
    /// </summary>
    public static bool TryApplySnapPlacement(
        AuxSnapWindowKind kind,
        bool snapped,
        string? edgeRaw,
        double dockX,
        double dockY,
        Rect mainOuter,
        Window w,
        double snapGapPx,
        out double outDockX,
        out double outDockY)
    {
        outDockX = dockX;
        outDockY = dockY;

        if (!snapped || string.IsNullOrWhiteSpace(edgeRaw))
            return false;

        var edge = (edgeRaw ?? "").Trim();
        if (string.Equals(edge, SnapEdges.None, StringComparison.OrdinalIgnoreCase))
            return false;

        var mainLeft = mainOuter.Left;
        var mainTop = mainOuter.Top;
        var mainRight = mainLeft + mainOuter.Width;
        var mainBottom = mainTop + mainOuter.Height;

        double desiredLeft;
        double desiredTop;

        if (string.Equals(edge, SnapEdges.Right, StringComparison.OrdinalIgnoreCase))
        {
            desiredLeft = mainRight + snapGapPx;
            desiredTop = mainTop + dockY;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = desiredLeft;
            w.Top = desiredTop;
            outDockX = 0;
            outDockY = desiredTop - mainTop;
            return true;
        }

        if (string.Equals(edge, SnapEdges.Left, StringComparison.OrdinalIgnoreCase))
        {
            desiredLeft = mainLeft - w.Width - snapGapPx;
            desiredTop = mainTop + dockY;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = desiredLeft;
            w.Top = desiredTop;
            outDockX = 0;
            outDockY = desiredTop - mainTop;
            return true;
        }

        if (string.Equals(edge, SnapEdges.Bottom, StringComparison.OrdinalIgnoreCase))
        {
            desiredTop = mainBottom + snapGapPx;
            desiredLeft = kind == AuxSnapWindowKind.Options
                ? ClampOptionsBottomSnapLeft(mainLeft, mainRight, w.Width, dockX)
                : mainLeft + dockX;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = desiredLeft;
            w.Top = desiredTop;
            outDockX = desiredLeft - mainLeft;
            outDockY = 0;
            return true;
        }

        if (string.Equals(edge, SnapEdges.Top, StringComparison.OrdinalIgnoreCase))
        {
            desiredTop = mainTop - w.Height - snapGapPx;
            desiredLeft = mainLeft + dockX;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = desiredLeft;
            w.Top = desiredTop;
            outDockX = desiredLeft - mainLeft;
            outDockY = 0;
            return true;
        }

        return false;
    }

    private static class SnapEdges
    {
        public const string None = "None";
        public const string Left = "Left";
        public const string Right = "Right";
        public const string Top = "Top";
        public const string Bottom = "Bottom";
    }
}

/// <summary>
/// Warm-reuse helper for a single auxiliary <see cref="Window"/> (Hide/Show instead of Close).
/// Snap geometry stays on <see cref="MainWindow"/> until fully migrated; this type centralizes visibility.
/// </summary>
public sealed class AuxWindowController<TWindow> where TWindow : Window
{
    private TWindow? _window;

    public TWindow? Window => _window;

    public bool HasWindow => _window is not null;

    public bool IsOpen => _window is { IsVisible: true };

    public void Register(TWindow window) => _window = window;

    public void Clear() => _window = null;

    public void ShowWarm(Action? afterShow = null)
    {
        if (_window is null)
            return;
        if (!_window.IsVisible)
            _window.Show();
        try { _window.Activate(); } catch { /* ignore */ }
        try { afterShow?.Invoke(); } catch { /* ignore */ }
    }

    public void HideWarm()
    {
        try { _window?.Hide(); } catch { /* ignore */ }
    }
}
