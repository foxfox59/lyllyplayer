using System.Windows;
using System.Windows.Media;

namespace LyllyPlayer;

/// <summary>
/// Draws the window frame as a filled ring (outer rounded rect minus inner rounded rect).
/// Avoids WPF <see cref="System.Windows.Controls.Border"/> stroke/fill corner mismatch on thick and 1px strokes.
/// </summary>
public sealed class WindowChromeFrameRing : FrameworkElement
{
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush),
        typeof(System.Windows.Media.Brush),
        typeof(WindowChromeFrameRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth),
        typeof(double),
        typeof(WindowChromeFrameRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OuterCornerRadiusProperty = DependencyProperty.Register(
        nameof(OuterCornerRadius),
        typeof(double),
        typeof(WindowChromeFrameRing),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Brush? RingBrush
    {
        get => (System.Windows.Media.Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public double OuterCornerRadius
    {
        get => (double)GetValue(OuterCornerRadiusProperty);
        set => SetValue(OuterCornerRadiusProperty, value);
    }

    static WindowChromeFrameRing()
    {
        IsHitTestVisibleProperty.OverrideMetadata(
            typeof(WindowChromeFrameRing),
            new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var brush = RingBrush;
        if (brush is null)
            return;

        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 1 || h <= 1)
            return;

        var raw = StrokeWidth;
        if (raw <= 0)
            return;

        // Snap stroke to whole device pixels so thin rings (1px + UI scale) don't sit on fractional
        // coordinates — that shows as dark gaps or wrong tints at rounded corners.
        var dpi = VisualTreeHelper.GetDpi(this);
        var ppd = dpi.PixelsPerDip;
        var strokePx = raw * ppd;
        var snappedPx = Math.Max(1, Math.Round(strokePx));
        var v = snappedPx / ppd;

        var ro = Math.Min(OuterCornerRadius, Math.Min(w, h) / 2.0);
        var outer = new RectangleGeometry(new Rect(0, 0, w, h), ro, ro);

        // Inset the exclude hole slightly more than v so the ring band overlaps its own anti-aliased edge
        // (stops thin strokes from showing as a dark notch along the top / title seam).
        var innerLead = v;
        if (snappedPx <= 3)
        {
            var bump = Math.Min(1.0 / ppd, v * 0.35);
            innerLead = Math.Min(v + bump, (Math.Min(w, h) - 2.0) / 4.0);
            innerLead = Math.Max(innerLead, v);
        }

        var innerW = w - 2 * innerLead;
        var innerH = h - 2 * innerLead;
        if (innerW <= 0 || innerH <= 0)
            return;

        var ri = Math.Max(0, ro - innerLead);
        ri = Math.Min(ri, Math.Min(innerW, innerH) / 2.0);
        var inner = new RectangleGeometry(new Rect(innerLead, innerLead, innerW, innerH), ri, ri);

        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        ring.Freeze();
        dc.DrawGeometry(brush, null, ring);
    }
}
