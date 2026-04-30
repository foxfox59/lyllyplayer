using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LyllyPlayer.Settings;

namespace LyllyPlayer.Windows;

public partial class BackgroundDesignerWindow : Window
{
    public sealed record Result(
        RectN MainNormal,
        RectN MainCompact,
        RectN MainUltra,
        RectN Playlist,
        RectN OptionsLog,
        RectN Lyrics);

    private readonly BitmapSource _src;
    private readonly Action<Result>? _apply;
    private readonly double _mainDefaultAspect;
    private readonly double _mainCompactAspect;
    private readonly double _mainUltraAspect;
    private readonly double _playlistAspect;
    private readonly double _optionsLogAspect;
    private readonly double _lyricsAspect;

    private RectN _mainNormal;
    private RectN _mainCompact;
    private RectN _mainUltra;
    private RectN _playlist;
    private RectN _optionsLog;
    private RectN _lyrics;

    private string _target = "Main"; // Main | Playlist | OptionsLog | Lyrics
    private string _mainMode = "Default"; // Default | Compact | Ultra

    // Render mapping (image displayed rect inside host)
    private Rect _imageRectPx;
    private bool _suppressZoomEvent;
    // Minimum selection size in normalized image coordinates.
    // Keep small enough to allow fine crops; runtime will still clamp to visible bounds.
    private const double MinRectNSize = 0.01;
    // Per-target/per-mode aspect lock state (designer-local; not persisted to app settings).
    private bool _lockAspectMainDefault = true;
    private bool _lockAspectMainCompact = true;
    private bool _lockAspectMainUltra = true;
    private bool _lockAspectPlaylist = true;
    private bool _lockAspectOptionsLog = true;
    private bool _lockAspectLyrics = true;
    private bool _resizing;
    private RectN _resizeStartRect;
    private double _resizeAnchorX;
    private double _resizeAnchorY;
    /// <summary>Avoid re-entrancy when syncing radio buttons from <see cref="_mainMode"/> (Checked would mutate state).</summary>
    private bool _suppressMainModeRadioEvents;

    public BackgroundDesignerWindow(
        BitmapSource src,
        RectN mainNormal,
        RectN mainCompact,
        RectN mainUltra,
        RectN playlist,
        RectN optionsLog,
        RectN lyrics,
        double mainDefaultAspect,
        double mainCompactAspect,
        double mainUltraAspect,
        double playlistAspect,
        double optionsLogAspect,
        double lyricsAspect,
        Action<Result>? apply)
    {
        _src = src;
        // Fallback aspects tuned to app's fixed 600px width and typical heights.
        _mainDefaultAspect = mainDefaultAspect > 0 ? mainDefaultAspect : 600.0 / 260.0;
        _mainCompactAspect = mainCompactAspect > 0 ? mainCompactAspect : 600.0 / 140.0;
        _mainUltraAspect = mainUltraAspect > 0 ? mainUltraAspect : 600.0 / 110.0;
        _playlistAspect = playlistAspect > 0 ? playlistAspect : 560.0 / 720.0;
        _optionsLogAspect = optionsLogAspect > 0 ? optionsLogAspect : 640.0 / 480.0;
        _lyricsAspect = lyricsAspect > 0 ? lyricsAspect : 700.0 / 500.0;
        _apply = apply;
        _mainNormal = SettingsStore.NormalizeRectN(mainNormal);
        _mainCompact = SettingsStore.NormalizeRectN(mainCompact);
        _mainUltra = SettingsStore.NormalizeRectN(mainUltra);
        _playlist = SettingsStore.NormalizeRectN(playlist);
        _optionsLog = SettingsStore.NormalizeRectN(optionsLog);
        _lyrics = SettingsStore.NormalizeRectN(lyrics);
        CoalesceInitialMainSubRects();

        InitializeComponent();

        PreviewImage.Source = _src;
        _suppressZoomEvent = true;
        try
        {
            // Initialize zoom UI from the provided rects (don't trigger zoom math during startup).
            RefreshUiFromState();
            SyncZoomToActiveRect();
        }
        finally
        {
            _suppressZoomEvent = false;
        }
    }

    private void SyncZoomToActiveRect()
    {
        try
        {
            var cur = GetActiveRect();
            var outer = GetOuterConstraintIfAny() ?? RectN.Full;
            outer = SettingsStore.NormalizeRectN(outer);
            cur = SettingsStore.NormalizeRectN(cur);
            var aspect = GetTargetAspectForRectN();

            double z;
            if (aspect >= 1.0)
            {
                z = cur.W / Math.Max(MinRectNSize, outer.W);
            }
            else
            {
                z = cur.H / Math.Max(MinRectNSize, outer.H);
            }

            z = Math.Clamp(z, MinRectNSize, 1.0);
            _suppressZoomEvent = true;
            try
            {
                ZoomSlider.Value = z;
                ZoomValueText.Text = $"{(int)Math.Round(z * 100)}%";
            }
            finally
            {
                _suppressZoomEvent = false;
            }
        }
        catch { /* ignore */ }
    }

    private void RefreshUiFromState()
    {
        try
        {
            MainModePanel.IsEnabled = string.Equals(_target, "Main", StringComparison.OrdinalIgnoreCase);
            MainModePanel.Opacity = MainModePanel.IsEnabled ? 1.0 : 0.45;

            // Never clobber _mainMode when switching Main <-> Playlist/OptionsLog: GetActiveRect() already keys off
            // _target, and forcing Default desyncs radio visuals from _mainMode so Compact/Ultra "break" after a few tab changes.
            if (MainModePanel.IsEnabled)
                SyncMainModeRadiosFromState();

            var boundsVis = (string.Equals(_target, "Main", StringComparison.OrdinalIgnoreCase) &&
                             (_mainMode is "Compact" or "Ultra"))
                ? Visibility.Visible
                : Visibility.Collapsed;
            MainOutlineRect.Visibility = boundsVis;
            MainOutlineRectShadow.Visibility = boundsVis;

            // Sync aspect lock checkbox to the current target/mode.
            try
            {
                LockAspectRatioCheckBox.IsChecked = GetLockAspectForCurrentTargetMode();
            }
            catch { /* ignore */ }

            UpdateDisplayedRects();
            SyncZoomToActiveRect();
        }
        catch { /* ignore */ }
    }

    private void SyncMainModeRadiosFromState()
    {
        _suppressMainModeRadioEvents = true;
        try
        {
            if (_mainMode == "Compact") MainModeCompactRadio.IsChecked = true;
            else if (_mainMode == "Ultra") MainModeUltraRadio.IsChecked = true;
            else MainModeDefaultRadio.IsChecked = true;
        }
        finally
        {
            _suppressMainModeRadioEvents = false;
        }
    }

    private bool GetLockAspectForCurrentTargetMode()
    {
        if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) return _lockAspectPlaylist;
        if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) return _lockAspectOptionsLog;
        if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) return _lockAspectLyrics;
        return _mainMode switch
        {
            "Compact" => _lockAspectMainCompact,
            "Ultra" => _lockAspectMainUltra,
            _ => _lockAspectMainDefault
        };
    }

    private void SetLockAspectForCurrentTargetMode(bool value)
    {
        if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) { _lockAspectPlaylist = value; return; }
        if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) { _lockAspectOptionsLog = value; return; }
        if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) { _lockAspectLyrics = value; return; }
        if (_mainMode == "Compact") _lockAspectMainCompact = value;
        else if (_mainMode == "Ultra") _lockAspectMainUltra = value;
        else _lockAspectMainDefault = value;
    }

    private RectN? GetOuterConstraintIfAny()
    {
        if (string.Equals(_target, "Main", StringComparison.OrdinalIgnoreCase) && (_mainMode is "Compact" or "Ultra"))
            return _mainNormal;
        return null;
    }

    private double GetTargetAspect()
    {
        if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase))
            return _playlistAspect;
        if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase))
            return _optionsLogAspect;
        if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase))
            return _lyricsAspect;

        return _mainMode switch
        {
            "Compact" => _mainCompactAspect,
            "Ultra" => _mainUltraAspect,
            _ => _mainDefaultAspect
        };
    }

    private double GetSourceImageAspect()
    {
        var iw = Math.Max(1.0, (double)_src.PixelWidth);
        var ih = Math.Max(1.0, (double)_src.PixelHeight);
        return iw / ih;
    }

    /// <summary>
    /// <see cref="RectN"/> W is a fraction of bitmap width and H a fraction of bitmap height, so the physical
    /// crop aspect is (W/H)×(image width/height). Window aspect Aw needs W/H = Aw / imageAspect for the on-screen
    /// selection to match the real window proportions in the designer preview.
    /// </summary>
    private double WindowAspectToRectNAspect(double windowWidthOverHeight)
    {
        if (windowWidthOverHeight <= 0) return 0;
        var imgA = GetSourceImageAspect();
        if (imgA <= 0.0001) return windowWidthOverHeight;
        return windowWidthOverHeight / imgA;
    }

    private double GetTargetAspectForRectN() => WindowAspectToRectNAspect(GetTargetAspect());

    private static RectN CoerceToAspect(RectN r, double aspect, RectN? outerConstraint)
    {
        r = SettingsStore.NormalizeRectN(r);
        if (aspect <= 0) return r;

        // aspect = W/H in RectN space (W = fraction of image width, H = fraction of image height).
        // Keep center, adjust size to match aspect.
        var cx = r.X + r.W / 2.0;
        var cy = r.Y + r.H / 2.0;

        double w, h;
        if (outerConstraint is { } oc)
        {
            // When constrained (Main Compact/Ultra), maximize the rect inside Default bounds.
            // This keeps Compact/Ultra widths consistent (prefer full width, shrink height as needed).
            oc = SettingsStore.NormalizeRectN(oc);
            w = oc.W;
            h = w / aspect;
            if (h > oc.H)
            {
                h = oc.H;
                w = h * aspect;
            }
        }
        else
        {
            // Unconstrained: fit inside current rect.
            w = r.W;
            h = r.H;
            var curAspect = w / Math.Max(0.0001, h);
            if (curAspect > aspect)
            {
                // Too wide: reduce width.
                w = h * aspect;
            }
            else
            {
                // Too tall: reduce height.
                h = w / aspect;
            }
        }

        w = Math.Max(MinRectNSize, Math.Min(1.0, w));
        h = Math.Max(MinRectNSize, Math.Min(1.0, h));

        var x = cx - w / 2.0;
        var y = cy - h / 2.0;
        var next = SettingsStore.NormalizeRectN(new RectN(x, y, w, h));

        if (outerConstraint is { } oc2)
            return ConstrainInside(oc2, next);
        return next;
    }

    private RectN GetActiveRect()
    {
        if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) return _playlist;
        if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) return _optionsLog;
        if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) return _lyrics;
        return _mainMode switch
        {
            "Compact" => _mainCompact,
            "Ultra" => _mainUltra,
            _ => _mainNormal
        };
    }

    private void SetActiveRect(RectN r)
    {
        r = SettingsStore.NormalizeRectN(r);
        if (GetLockAspectForCurrentTargetMode())
            r = CoerceToAspect(r, GetTargetAspectForRectN(), GetOuterConstraintIfAny());
        if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) { _playlist = r; return; }
        if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) { _optionsLog = r; return; }
        if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) { _lyrics = r; return; }

        if (_mainMode == "Compact") _mainCompact = ConstrainInside(_mainNormal, r);
        else if (_mainMode == "Ultra") _mainUltra = ConstrainInside(_mainNormal, r);
        else
        {
            var prev = _mainNormal;
            _mainNormal = r;
            // Keep sub-rects moving consistently with Default changes (not just clamped).
            RemapMainSubSelectionsAfterDefaultChanged(prev, _mainNormal);
        }
    }

    private static RectN ConstrainInside(RectN outer, RectN inner)
    {
        outer = SettingsStore.NormalizeRectN(outer);
        inner = SettingsStore.NormalizeRectN(inner);
        if (inner.W > outer.W) inner = inner with { W = outer.W };
        if (inner.H > outer.H) inner = inner with { H = outer.H };

        var x = Math.Clamp(inner.X, outer.X, outer.X + outer.W - inner.W);
        var y = Math.Clamp(inner.Y, outer.Y, outer.Y + outer.H - inner.H);
        return SettingsStore.NormalizeRectN(new RectN(x, y, inner.W, inner.H));
    }

    private static RectN MapRectRelativeToOuterChange(RectN prevOuter, RectN nextOuter, RectN inner)
    {
        prevOuter = SettingsStore.NormalizeRectN(prevOuter);
        nextOuter = SettingsStore.NormalizeRectN(nextOuter);
        inner = SettingsStore.NormalizeRectN(inner);

        // If prev outer is degenerate, fall back to clamp-only behavior.
        if (prevOuter.W <= 1e-9 || prevOuter.H <= 1e-9)
            return ConstrainInside(nextOuter, inner);

        // Express inner in normalized coordinates relative to prevOuter.
        var rx = (inner.X - prevOuter.X) / prevOuter.W;
        var ry = (inner.Y - prevOuter.Y) / prevOuter.H;
        var rw = inner.W / prevOuter.W;
        var rh = inner.H / prevOuter.H;

        // Map into nextOuter space.
        var nx = nextOuter.X + rx * nextOuter.W;
        var ny = nextOuter.Y + ry * nextOuter.H;
        var nw = rw * nextOuter.W;
        var nh = rh * nextOuter.H;

        return SettingsStore.NormalizeRectN(new RectN(nx, ny, nw, nh));
    }

    private void RemapMainSubSelectionsAfterDefaultChanged(RectN prevDefault, RectN newDefault)
    {
        try
        {
            prevDefault = SettingsStore.NormalizeRectN(prevDefault);
            newDefault = SettingsStore.NormalizeRectN(newDefault);

            // Only meaningful if sub-rects are actually constrained to Default.
            _mainCompact = ConstrainInside(newDefault, MapRectRelativeToOuterChange(prevDefault, newDefault, _mainCompact));
            _mainUltra = ConstrainInside(newDefault, MapRectRelativeToOuterChange(prevDefault, newDefault, _mainUltra));

            // If aspect lock is on for those modes, re-coerce to the intended footprint while staying inside Default.
            var savedTarget = _target;
            var savedMode = _mainMode;
            try
            {
                _target = "Main";

                _mainMode = "Compact";
                if (GetLockAspectForCurrentTargetMode())
                    _mainCompact = CoerceToAspect(_mainCompact, GetTargetAspectForRectN(), newDefault);

                _mainMode = "Ultra";
                if (GetLockAspectForCurrentTargetMode())
                    _mainUltra = CoerceToAspect(_mainUltra, GetTargetAspectForRectN(), newDefault);
            }
            finally
            {
                _target = savedTarget;
                _mainMode = savedMode;
            }

            _mainCompact = ConstrainInside(newDefault, _mainCompact);
            _mainUltra = ConstrainInside(newDefault, _mainUltra);
        }
        catch { /* ignore */ }
    }

    private void UpdateDisplayedRects()
    {
        UpdateImageRectPx();
        var active = GetActiveRect();
        var activePx = RectNToPx(active);

        Canvas.SetLeft(SelectionAdorner, activePx.Left);
        Canvas.SetTop(SelectionAdorner, activePx.Top);
        SelectionAdorner.Width = activePx.Width;
        SelectionAdorner.Height = activePx.Height;

        // Resize handle should never impose a minimum crop size.
        // Scale it down when the selection gets tiny so the user can still make small crops.
        try
        {
            var maxHandle = 18.0;
            var minHandle = 8.0;
            var scaled = Math.Min(maxHandle, Math.Max(minHandle, Math.Min(activePx.Width, activePx.Height) * 0.30));
            ResizeThumb.Width = scaled;
            ResizeThumb.Height = scaled;
        }
        catch { /* ignore */ }

        if (MainOutlineRect.Visibility == Visibility.Visible)
        {
            var outerPx = RectNToPx(_mainNormal);
            Canvas.SetLeft(MainOutlineRectShadow, outerPx.Left);
            Canvas.SetTop(MainOutlineRectShadow, outerPx.Top);
            MainOutlineRectShadow.Width = outerPx.Width;
            MainOutlineRectShadow.Height = outerPx.Height;

            Canvas.SetLeft(MainOutlineRect, outerPx.Left);
            Canvas.SetTop(MainOutlineRect, outerPx.Top);
            MainOutlineRect.Width = outerPx.Width;
            MainOutlineRect.Height = outerPx.Height;
        }

        // Sync zoom slider to the active rect size relative to its constraint.
        try
        {
            _suppressZoomEvent = true;
            var aspect = GetTargetAspectForRectN();
            var outer = GetOuterConstraintIfAny() ?? RectN.Full;
            var outerDim = aspect >= 1.0 ? outer.W : outer.H;
            outerDim = Math.Max(MinRectNSize, Math.Min(1.0, outerDim));
            var curDim = aspect >= 1.0 ? active.W : active.H;
            var z = curDim / outerDim;
            z = Math.Clamp(z, MinRectNSize, 1.0);
            ZoomSlider.Value = z;
            ZoomValueText.Text = $"{(int)Math.Round(z * 100)}%";
        }
        catch { /* ignore */ }
        finally { _suppressZoomEvent = false; }
    }

    private void UpdateImageRectPx()
    {
        // PreviewImage is Stretch=Uniform; compute actual displayed rect within PreviewHostGrid.
        var hostW = Math.Max(1.0, PreviewHostGrid.ActualWidth);
        var hostH = Math.Max(1.0, PreviewHostGrid.ActualHeight);
        var imgW = Math.Max(1.0, _src.PixelWidth);
        var imgH = Math.Max(1.0, _src.PixelHeight);
        var scale = Math.Min(hostW / imgW, hostH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        var left = (hostW - dispW) / 2.0;
        var top = (hostH - dispH) / 2.0;
        _imageRectPx = new Rect(left, top, dispW, dispH);
    }

    private Rect RectNToPx(RectN r)
    {
        r = SettingsStore.NormalizeRectN(r);
        return new Rect(
            _imageRectPx.Left + r.X * _imageRectPx.Width,
            _imageRectPx.Top + r.Y * _imageRectPx.Height,
            r.W * _imageRectPx.Width,
            r.H * _imageRectPx.Height);
    }

    private RectN PxToRectN(Rect rPx)
    {
        var x = (rPx.Left - _imageRectPx.Left) / _imageRectPx.Width;
        var y = (rPx.Top - _imageRectPx.Top) / _imageRectPx.Height;
        var w = rPx.Width / _imageRectPx.Width;
        var h = rPx.Height / _imageRectPx.Height;
        return SettingsStore.NormalizeRectN(new RectN(x, y, w, h));
    }

    private void ApplyCurrentToApp()
    {
        try
        {
            _apply?.Invoke(new Result(_mainNormal, _mainCompact, _mainUltra, _playlist, _optionsLog, _lyrics));
        }
        catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { if (e.ClickCount == 2) return; DragMove(); } catch { /* ignore */ }
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TargetComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (TargetComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _target = tag;
                RefreshUiFromState();
            }
        }
        catch { /* ignore */ }
    }

    private void MainModeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_suppressMainModeRadioEvents)
                return;
            if (MainModeCompactRadio.IsChecked == true) _mainMode = "Compact";
            else if (MainModeUltraRadio.IsChecked == true) _mainMode = "Ultra";
            else _mainMode = "Default";

            // First-time defaults: when switching into Compact/Ultra, ensure the selection starts inside Default.
            if (string.Equals(_target, "Main", StringComparison.OrdinalIgnoreCase) && (_mainMode is "Compact" or "Ultra"))
            {
                var candidate = _mainMode == "Compact" ? _mainCompact : _mainUltra;
                // Treat full-image as "unset" for sub-selections.
                var looksUnset = candidate.X == 0 && candidate.Y == 0 && candidate.W == 1 && candidate.H == 1;
                if (looksUnset || !IsRectInside(_mainNormal, candidate))
                {
                    // Initialize as the largest aspect-correct rect within Default (so it matches the window footprint immediately).
                    var aspect = GetTargetAspectForRectN();
                    candidate = CoerceToAspect(_mainNormal, aspect, _mainNormal);
                    candidate = ConstrainInside(_mainNormal, candidate);
                    if (_mainMode == "Compact") _mainCompact = candidate;
                    else _mainUltra = candidate;
                }
            }
            RefreshUiFromState();
        }
        catch { /* ignore */ }
    }

    private static bool IsRectInside(RectN outer, RectN inner)
    {
        outer = SettingsStore.NormalizeRectN(outer);
        inner = SettingsStore.NormalizeRectN(inner);
        return inner.X >= outer.X
               && inner.Y >= outer.Y
               && inner.X + inner.W <= outer.X + outer.W
               && inner.Y + inner.H <= outer.Y + outer.H;
    }

    private static bool RectNNearlyEqual(RectN a, RectN b, double eps = 1e-4)
    {
        a = SettingsStore.NormalizeRectN(a);
        b = SettingsStore.NormalizeRectN(b);
        return Math.Abs(a.X - b.X) < eps
               && Math.Abs(a.Y - b.Y) < eps
               && Math.Abs(a.W - b.W) < eps
               && Math.Abs(a.H - b.H) < eps;
    }

    /// <summary>
    /// MainWindow often seeds Compact/Ultra from Main until the user edits them — that makes both modes show the
    /// full Default crop and wrong aspect until a radio click. Replace copies of Default / full image with the
    /// largest valid aspect rect inside Default.
    /// </summary>
    private void CoalesceInitialMainSubRects()
    {
        try
        {
            _mainNormal = SettingsStore.NormalizeRectN(_mainNormal);

            void EnsureSub(ref RectN sub, double windowAspect)
            {
                sub = SettingsStore.NormalizeRectN(sub);
                var unsetFull = sub.X == 0 && sub.Y == 0 && Math.Abs(sub.W - 1.0) < 1e-9 && Math.Abs(sub.H - 1.0) < 1e-9;
                if (unsetFull
                    || !IsRectInside(_mainNormal, sub)
                    || RectNNearlyEqual(sub, _mainNormal))
                {
                    var ar = WindowAspectToRectNAspect(windowAspect);
                    sub = CoerceToAspect(_mainNormal, ar, _mainNormal);
                    sub = ConstrainInside(_mainNormal, sub);
                }
            }

            EnsureSub(ref _mainCompact, _mainCompactAspect);
            EnsureSub(ref _mainUltra, _mainUltraAspect);
        }
        catch { /* ignore */ }
    }

    private void PreviewHostGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { UpdateDisplayedRects(); } catch { /* ignore */ }
    }

    private void SelectionThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        try
        {
            UpdateImageRectPx();
            var cur = GetActiveRect();
            // Convert pixel delta into normalized delta, preserving rect size (move only).
            var dxN = e.HorizontalChange / Math.Max(1.0, _imageRectPx.Width);
            var dyN = e.VerticalChange / Math.Max(1.0, _imageRectPx.Height);
            var next = new RectN(cur.X + dxN, cur.Y + dyN, cur.W, cur.H);

            // Clamp to image bounds.
            var x = Math.Clamp(next.X, 0.0, 1.0 - next.W);
            var y = Math.Clamp(next.Y, 0.0, 1.0 - next.H);
            next = new RectN(x, y, next.W, next.H);

            // If constrained (Main Compact/Ultra), clamp inside the outer selection.
            if (GetOuterConstraintIfAny() is { } oc)
                next = ConstrainInside(oc, next);

            // Store without re-coercing aspect (it already matches; we only moved).
            if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) _playlist = next;
            else if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) _optionsLog = next;
            else if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) _lyrics = next;
            else
            {
                if (_mainMode == "Compact") _mainCompact = next;
                else if (_mainMode == "Ultra") _mainUltra = next;
                else
                {
                    var prevMain = _mainNormal;
                    _mainNormal = next;
                    RemapMainSubSelectionsAfterDefaultChanged(prevMain, _mainNormal);
                }
            }

            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void ZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressZoomEvent)
            return;
        try
        {
            var z = Math.Clamp(ZoomSlider.Value, MinRectNSize, 1.0);
            ZoomValueText.Text = $"{(int)Math.Round(z * 100)}%";

            var cur = GetActiveRect();
            var cx = cur.X + cur.W / 2.0;
            var cy = cur.Y + cur.H / 2.0;
            var aspect = GetTargetAspectForRectN();
            var outer = GetOuterConstraintIfAny() ?? RectN.Full;
            var outerW = Math.Max(MinRectNSize, Math.Min(1.0, outer.W));
            var outerH = Math.Max(MinRectNSize, Math.Min(1.0, outer.H));

            // z is relative to outer along the primary axis.
            double w, h;
            if (aspect >= 1.0)
            {
                w = Math.Clamp(outerW * z, MinRectNSize, outerW);
                h = Math.Clamp(w / aspect, MinRectNSize, outerH);
            }
            else
            {
                h = Math.Clamp(outerH * z, MinRectNSize, outerH);
                w = Math.Clamp(h * aspect, MinRectNSize, outerW);
            }

            // Centered zoom: keep center, then clamp to outer bounds (minimal movement).
            var x = cx - w / 2.0;
            var y = cy - h / 2.0;
            var next = SettingsStore.NormalizeRectN(new RectN(x, y, w, h));
            next = ConstrainInside(outer, next);
            SetActiveRect(next);
            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void ResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        try
        {
            UpdateImageRectPx();
            if (!_resizing)
                return;

            var outer = GetOuterConstraintIfAny() ?? RectN.Full;

            // Drive resize by absolute pointer position (stable), not per-tick deltas (which jitter when layout updates).
            var p = Mouse.GetPosition(OverlayCanvas);
            var px = (p.X - _imageRectPx.Left) / Math.Max(1.0, _imageRectPx.Width);
            var py = (p.Y - _imageRectPx.Top) / Math.Max(1.0, _imageRectPx.Height);
            px = Math.Clamp(px, 0.0, 1.0);
            py = Math.Clamp(py, 0.0, 1.0);

            var x = _resizeAnchorX;
            var y = _resizeAnchorY;

            // Maximum size available from the fixed anchor (top-left) within the constraint.
            // Min size in normalized coords, but also allow going smaller than the handle by keeping this low.
            var maxW = Math.Max(MinRectNSize, (outer.X + outer.W) - x);
            var maxH = Math.Max(MinRectNSize, (outer.Y + outer.H) - y);

            var desiredW = px - x;
            var desiredH = py - y;

            double w, h;
            if (GetLockAspectForCurrentTargetMode())
            {
                var aspect = GetTargetAspectForRectN();
                if (aspect <= 0)
                {
                    w = desiredW;
                    h = desiredH;
                }
                else
                {
                    // Ensure a minimum size in BOTH dimensions even when aspect is wide/tall.
                    // This prevents the selection from collapsing into a "horizontal-only" shape at small sizes.
                    var minW = aspect >= 1.0 ? (MinRectNSize * aspect) : MinRectNSize;
                    var minH = aspect >= 1.0 ? MinRectNSize : (MinRectNSize / Math.Max(0.0001, aspect));

                    // Stable aspect-locked resize: keep top-left anchored and choose the largest rect that
                    // fits under the pointer in both axes while maintaining aspect.
                    //
                    // Constraints:
                    // - w <= desiredW
                    // - h <= desiredH
                    // - h = w / aspect
                    // => w <= desiredH * aspect
                    var wLimit = Math.Min(desiredW, desiredH * aspect);
                    w = Math.Clamp(wLimit, minW, maxW);
                    h = w / aspect;
                    if (h > maxH)
                    {
                        h = maxH;
                        w = h * aspect;
                    }

                    // If we were clamped by maxH and that pushed us below minH, bump within available bounds.
                    if (h < minH)
                    {
                        h = Math.Clamp(minH, MinRectNSize, maxH);
                        w = h * aspect;
                        if (w > maxW)
                        {
                            w = maxW;
                            h = w / aspect;
                        }
                    }
                }
            }
            else
            {
                w = Math.Clamp(desiredW, MinRectNSize, maxW);
                h = Math.Clamp(desiredH, MinRectNSize, maxH);
            }

            var next = SettingsStore.NormalizeRectN(new RectN(x, y, w, h));

            // Store directly (don't re-coerce aspect during drag; we already handled it above).
            if (string.Equals(_target, "Playlist", StringComparison.OrdinalIgnoreCase)) _playlist = next;
            else if (string.Equals(_target, "OptionsLog", StringComparison.OrdinalIgnoreCase)) _optionsLog = next;
            else if (string.Equals(_target, "Lyrics", StringComparison.OrdinalIgnoreCase)) _lyrics = next;
            else
            {
                if (_mainMode == "Compact") _mainCompact = next;
                else if (_mainMode == "Ultra") _mainUltra = next;
                else
                {
                    var prevMain = _mainNormal;
                    _mainNormal = next;
                    RemapMainSubSelectionsAfterDefaultChanged(prevMain, _mainNormal);
                }
            }

            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void EnsureMainSubSelectionsInsideDefault()
    {
        try
        {
            // Keep Compact/Ultra inside Default after Default is moved/resized.
            _mainCompact = ConstrainInside(_mainNormal, _mainCompact);
            _mainUltra = ConstrainInside(_mainNormal, _mainUltra);
        }
        catch { /* ignore */ }
    }

    private void ResizeThumb_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        try
        {
            _resizing = true;
            _resizeStartRect = GetActiveRect();
            _resizeAnchorX = _resizeStartRect.X;
            _resizeAnchorY = _resizeStartRect.Y;
        }
        catch { /* ignore */ }
    }

    private void ResizeThumb_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
    }

    private void LockAspectRatioCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        SetLockAspectForCurrentTargetMode(true);
        try
        {
            // When turning lock on, coerce only the currently active selection.
            var r = GetActiveRect();
            SetActiveRect(CoerceToAspect(r, GetTargetAspectForRectN(), GetOuterConstraintIfAny()));
            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void LockAspectRatioCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        SetLockAspectForCurrentTargetMode(false);
        try
        {
            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetActiveRect(RectN.Full);
            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void CopyFromDefaultButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.Equals(_target, "Main", StringComparison.OrdinalIgnoreCase))
                return;
            _mainCompact = ConstrainInside(_mainNormal, _mainNormal);
            _mainUltra = ConstrainInside(_mainNormal, _mainNormal);
            UpdateDisplayedRects();
            ApplyCurrentToApp();
        }
        catch { /* ignore */ }
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e) => ApplyCurrentToApp();

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentToApp();
        try { DialogResult = true; } catch { /* ignore */ }
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { /* ignore */ }
        Close();
    }
}

