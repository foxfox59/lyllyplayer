using System;
using System.Windows;
using LyllyPlayer.Settings;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Centralizes consistent "aux window" behavior relative to Main:
/// - single instance per window type
/// - warm reuse (Hide/Show)
/// - re-apply snap placement from settings on reopen
/// - register snapping + restore latch relations after show
/// </summary>
public sealed class AuxWindowHost<TWindow> where TWindow : Window
{
    private readonly Func<TWindow?> _getWindow;
    private readonly Action<TWindow?> _setWindow;
    private readonly AuxWindowController<TWindow> _warm;
    private readonly Func<AppSettings> _loadLatestSettings;
    private readonly Func<AppSettings, TWindow> _createAndConfigure;
    private readonly Action<TWindow, AppSettings, bool> _applyPlacementFromSettings;
    /// <summary>
    /// Invoked after the window is shown (or re-shown warm). <paramref name="warmReopen"/> is true when an existing hidden window was shown again.
    /// </summary>
    private readonly Action<TWindow, bool>? _afterShow;

    public AuxWindowHost(
        Func<TWindow?> getWindow,
        Action<TWindow?> setWindow,
        AuxWindowController<TWindow> warmController,
        Func<AppSettings> loadLatestSettings,
        Func<AppSettings, TWindow> createAndConfigure,
        Action<TWindow, AppSettings, bool> applyPlacementFromSettings,
        Action<TWindow, bool>? afterShow = null)
    {
        _getWindow = getWindow;
        _setWindow = setWindow;
        _warm = warmController;
        _loadLatestSettings = loadLatestSettings;
        _createAndConfigure = createAndConfigure;
        _applyPlacementFromSettings = applyPlacementFromSettings;
        _afterShow = afterShow;
    }

    public void EnsureOpen()
    {
        var existing = _getWindow();
        if (existing is not null)
        {
            if (!existing.IsVisible)
            {
                var latest = _loadLatestSettings();
                _warm.ShowWarm(() =>
                {
                    try { _applyPlacementFromSettings(existing, latest, true); } catch { /* ignore */ }
                    try { _afterShow?.Invoke(existing, true); } catch { /* ignore */ }
                });
            }
            return;
        }

        var settings = _loadLatestSettings();
        var w = _createAndConfigure(settings);
        _setWindow(w);
        try { _warm.Register(w); } catch { /* ignore */ }
        try { _applyPlacementFromSettings(w, settings, false); } catch { /* ignore */ }
        try { if (!w.IsVisible) w.Show(); } catch { /* ignore */ }
        try { _afterShow?.Invoke(w, false); } catch { /* ignore */ }
    }

    public void Hide()
    {
        try { _warm.HideWarm(); } catch { /* ignore */ }
    }

    public void Toggle()
    {
        var w = _getWindow();
        if (w is not null && w.IsVisible)
        {
            Hide();
            return;
        }
        EnsureOpen();
    }
}

