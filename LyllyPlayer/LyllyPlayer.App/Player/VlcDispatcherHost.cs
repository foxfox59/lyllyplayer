using System.Threading;
using System.Windows.Threading;

namespace LyllyPlayer.Player;

/// <summary>
/// Dedicated STA thread for all LibVLC / LibVLCSharp work so playback never marshals onto the WPF UI dispatcher.
/// </summary>
internal static class VlcDispatcherHost
{
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;

    public static Dispatcher Dispatcher
    {
        get
        {
            EnsureStarted();
            return _dispatcher!;
        }
    }

    public static bool CheckAccess()
    {
        EnsureStarted();
        return _dispatcher!.CheckAccess();
    }

    public static void EnsureStarted()
    {
        if (_dispatcher is not null)
            return;

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LyllyPlayer.LibVlc",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Ready.Wait();
    }

    public static void ShutdownBestEffort()
    {
        try
        {
            var d = _dispatcher;
            if (d is null)
                return;
            d.BeginInvokeShutdown(DispatcherPriority.Normal);
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }
        finally
        {
            _dispatcher = null;
            _thread = null;
        }
    }
}
