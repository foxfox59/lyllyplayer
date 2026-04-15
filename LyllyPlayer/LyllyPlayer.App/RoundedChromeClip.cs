using System.Windows;
using System.Windows.Media;

namespace LyllyPlayer;

/// <summary>
/// Applies a rounded-rectangle <see cref="UIElement.Clip"/> that matches the window chrome outer radius.
/// Prevents square-corner pixels (often black) from ImageBrush / child layers at thick borders, and
/// stabilizes edge anti-aliasing at thin borders vs relying on nested CornerRadius alone.
/// </summary>
public static class RoundedChromeClip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(RoundedChromeClip),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        if ((bool)e.NewValue)
        {
            fe.Loaded += OnLoaded;
            fe.SizeChanged += OnSizeChanged;
            ApplyClip(fe);
        }
        else
        {
            fe.Loaded -= OnLoaded;
            fe.SizeChanged -= OnSizeChanged;
            fe.Clip = null;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) =>
        ApplyClip((FrameworkElement)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyClip((FrameworkElement)sender);

    /// <summary>
    /// Re-applies clip after <see cref="MainWindow.ApplyWindowBorderFromSettings"/> changes snap/clip resources
    /// (no layout change required).
    /// </summary>
    public static void RefreshAll()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        foreach (Window window in app.Windows)
        {
            if (window?.Content is not DependencyObject root)
                continue;
            WalkVisualTree(root, fe =>
            {
                if (GetIsEnabled(fe))
                    ApplyClip(fe);
            });
        }
    }

    private static void WalkVisualTree(DependencyObject d, Action<FrameworkElement> visit)
    {
        if (d is FrameworkElement fe)
            visit(fe);

        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++)
            WalkVisualTree(VisualTreeHelper.GetChild(d, i), visit);
    }

    private static void ApplyClip(FrameworkElement fe)
    {
        if (System.Windows.Application.Current?.Resources["App.Theme.WindowChromeRoundedClipEnabled"] is bool enabled
            && !enabled)
        {
            fe.Clip = null;
            return;
        }

        var w = fe.RenderSize.Width > 0 ? fe.RenderSize.Width : fe.ActualWidth;
        var h = fe.RenderSize.Height > 0 ? fe.RenderSize.Height : fe.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var (rx, ry) = GetOuterRadii();

        // Clamp so radii never exceed half the shorter side (avoids degenerate geometry).
        var maxR = Math.Min(w, h) / 2.0;
        rx = Math.Min(rx, maxR);
        ry = Math.Min(ry, maxR);

        fe.Clip = new RectangleGeometry(new Rect(0, 0, w, h), rx, ry);
    }

    private static (double rx, double ry) GetOuterRadii()
    {
        if (System.Windows.Application.Current?.Resources["App.Theme.WindowCornerRadiusOuter"] is CornerRadius cr)
            return (cr.TopLeft, cr.TopLeft);

        return (8, 8);
    }
}
