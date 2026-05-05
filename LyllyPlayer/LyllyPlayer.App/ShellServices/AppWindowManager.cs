using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Centralizes app window activation/z-order heuristics so we don't scatter fragile per-window hacks.
/// Best-effort only: Windows focus-stealing rules still apply.
/// </summary>
public sealed class AppWindowManager
{
    private readonly object _gate = new();
    private readonly HashSet<Window> _windows = new();
    private Window? _lastActive;
    private int _raiseRequestId;

    public void Register(Window w)
    {
        if (w is null)
            return;

        lock (_gate)
            _windows.Add(w);

        try
        {
            w.Closing += (_, e) =>
            {
                try { OnWindowClosingBestEffort(w, e); } catch { /* ignore */ }
            };

            w.Activated += (_, _) =>
            {
                try
                {
                    lock (_gate) _lastActive = w;
                }
                catch { /* ignore */ }

                TryRaiseAppStackBestEffort(w);
            };

            // Closing/Closed are where the "duck behind other apps" bug tends to show up.
            w.Closed += (_, _) =>
            {
                try
                {
                    lock (_gate)
                        _windows.Remove(w);
                }
                catch { /* ignore */ }

                TryRaiseBestKeepWindowAfterCloseBestEffort();
            };
        }
        catch { /* ignore */ }
    }

    private void OnWindowClosingBestEffort(Window closing, CancelEventArgs e)
    {
        try
        {
            if (e.Cancel)
                return;

            // Key insight: if Main is already active when an aux closes, focus is fine.
            // Closing via the aux window's own [X] means the aux is the active window.
            // Proactively activate Main *during Closing* while the user focus token still belongs to this app.
            if (!closing.IsActive)
                return;

            var main = System.Windows.Application.Current?.MainWindow;
            if (main is null || ReferenceEquals(main, closing) || !main.IsVisible)
                return;

            WindowActivationHelper.ActivateWindowBestEffort(main);
        }
        catch { /* ignore */ }
    }

    private void TryRaiseAppStackBestEffort(Window keep)
    {
        try
        {
            var req = System.Threading.Interlocked.Increment(ref _raiseRequestId);

            // Defer and retry: the "duck behind other app" often happens slightly after activation/close.
            var delaysMs = new[] { 0, 60, 180, 420 };
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
                        try
                        {
                            if (req != _raiseRequestId)
                                return;

                            WindowActivationHelper.ActivateWindowBestEffort(keep);
                        }
                        catch { /* ignore */ }
                    };
                    t.Start();
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private void TryRaiseBestKeepWindowAfterCloseBestEffort()
    {
        try
        {
            Window? keep;
            lock (_gate)
            {
                keep = _lastActive;
                if (keep is null || !_windows.Contains(keep) || !keep.IsVisible)
                    keep = _windows.FirstOrDefault(x => x.IsVisible && x.IsActive)
                           ?? _windows.FirstOrDefault(x => x.IsVisible)
                           ?? System.Windows.Application.Current?.MainWindow;
            }

            if (keep is null)
                return;

            TryRaiseAppStackBestEffort(keep);
        }
        catch { /* ignore */ }
    }
}

