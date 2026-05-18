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

    private static readonly TimeSpan DefaultMarshalTimeout = TimeSpan.FromSeconds(8);

    private static Dispatcher VlcDispatcher
    {
        get
        {
            VlcDispatcherHost.EnsureStarted();
            return VlcDispatcherHost.Dispatcher;
        }
    }

    /// <summary>Runs on the LibVLC STA thread (blocks the caller until done or timeout).</summary>
    public static void RunOnUiThread(Action action, TimeSpan? timeout = null)
    {
        var disp = VlcDispatcher;
        if (disp.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            disp.InvokeAsync(action, DispatcherPriority.Normal).Task.Wait(timeout ?? DefaultMarshalTimeout);
        }
        catch (Exception ex)
        {
            try { AppLog.Warn($"LibVlcHost.RunOnUiThread failed: {ex.Message}"); } catch { /* ignore */ }
        }
    }

    /// <summary>Runs on the LibVLC STA thread and returns the func result.</summary>
    public static T RunOnUiThread<T>(Func<T> func, TimeSpan? timeout = null)
    {
        T? result = default;
        RunOnUiThread(() => result = func(), timeout);
        return result!;
    }

    public static async Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken = default)
    {
        var disp = VlcDispatcher;
        if (disp.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return;
        }

        await disp.InvokeAsync(action, DispatcherPriority.Normal).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> RunOnUiThreadAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        var disp = VlcDispatcher;
        if (disp.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return func();
        }

        return await disp.InvokeAsync(func, DispatcherPriority.Normal).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Must be called from the LibVLC thread before constructing <see cref="MediaPlayer"/> / <see cref="Media"/>.</summary>
    public static void EnsureInitialized()
        => RunOnUiThread(EnsureInitializedInner);

    private static void EnsureInitializedInner()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            Core.Initialize();
            _libVlc ??= new LibVLC("--no-video", "--intf=dummy");
            _initialized = true;
            try { AppLog.Info("LibVlcHost: initialized", AppLogInfoTier.Diagnostic); } catch { /* ignore */ }
        }
    }

    public static LibVLC LibVLC
    {
        get
        {
            // Never hold Gate while marshaling onto the LibVLC STA thread — another thread may already
            // be inside RunOnUiThread waiting for that thread, which then needs Gate (classic deadlock).
            EnsureInitialized();
            lock (Gate)
            {
                return _libVlc!;
            }
        }
    }
}
