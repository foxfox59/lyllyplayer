using LibVLCSharp.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>Single LibVLC instance + one-time <see cref="Core.Initialize"/> for the process.</summary>
public static class LibVlcHost
{
    private static readonly object Gate = new();
    private static LibVLC? _libVlc;
    private static bool _initialized;

    /// <summary>Must be called from the UI thread before constructing <see cref="MediaPlayer"/> / <see cref="Media"/>.</summary>
    public static void EnsureInitialized()
    {
        // Fast path: if already initialized, return immediately (do NOT marshal/log).
        lock (Gate)
        {
            if (_initialized)
                return;
        }

        // IMPORTANT:
        // - Some systems crash when initializing LibVLC off the UI thread
        // - But synchronously Dispatching to UI can deadlock/hang if the UI thread is busy
        // We do a bounded BeginInvoke to UI; if it doesn't complete quickly, we fall back to best-effort init.
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null && !disp.CheckAccess())
            {
                try { AppLog.Warn("LibVlcHost.EnsureInitialized: marshaling to UI thread"); } catch { /* ignore */ }

                var done = new ManualResetEventSlim(false);
                disp.BeginInvoke(new Action(() =>
                {
                    try { EnsureInitializedInner(); }
                    finally { try { done.Set(); } catch { /* ignore */ } }
                }), DispatcherPriority.Send);

                if (done.Wait(millisecondsTimeout: 2500))
                    return;

                try { AppLog.Warn("LibVlcHost.EnsureInitialized: UI marshal timed out; falling back to best-effort init"); } catch { /* ignore */ }
                // fall through to best-effort init
            }
        }
        catch { /* ignore */ }

        EnsureInitializedInner();
    }

    private static void EnsureInitializedInner()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            Core.Initialize();
            _libVlc ??= new LibVLC("--no-video", "--intf=dummy");
            _initialized = true;
            try { AppLog.Warn("LibVlcHost.EnsureInitialized: initialized"); } catch { /* ignore */ }
        }
    }

    public static LibVLC LibVLC
    {
        get
        {
            lock (Gate)
            {
                EnsureInitialized();
                return _libVlc!;
            }
        }
    }
}
