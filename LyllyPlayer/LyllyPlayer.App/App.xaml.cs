using System.Windows;
using System.Windows.Threading;
using System.Threading;
using System;
using LyllyPlayer.Player;
using LyllyPlayer.Utils;

namespace LyllyPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before any HWND: stable shell identity (taskbar / Task Manager "Apps" grouping).
        ShellProcessIdentity.TrySetExplicitAppUserModelId();

        // LibVLC native runtime (VideoLAN.LibVLC.Windows) + LibVLCSharp must initialize before Media/MediaPlayer.
        try { LibVlcHost.EnsureInitialized(); } catch { /* logged on first real use */ }

        base.OnStartup(e);
    }

    public App()
    {
        // Single-instance guard: do this as early as possible so the second instance cannot partially
        // initialize WPF and accidentally touch persisted state.
        try
        {
            // Use Local\ so it works per-user session; include a GUID-like suffix to avoid collisions.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\LyllyPlayer_9B8C4C2B0B984C1C8AB9D4B9E3B6A1C1", createdNew: out var createdNew);
            if (!createdNew)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        "LyllyPlayer is already running.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch { /* ignore */ }

                // Hard exit: avoid running any WPF shutdown paths in the second instance.
                Environment.Exit(0);
                return;
            }
        }
        catch
        {
            // If mutex creation fails for any reason, don't block startup.
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    AppLog.Exception(ex, "AppDomain unhandled exception");
                else
                    AppLog.Error($"AppDomain unhandled exception: {e.ExceptionObject}");
            }
            catch { /* ignore */ }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { AppLog.Exception(e.Exception, "Dispatcher unhandled exception"); } catch { /* ignore */ }
        try
        {
            var title = System.Windows.Application.Current?.MainWindow?.Title;
            if (string.IsNullOrWhiteSpace(title))
                title = "LyllyPlayer";
            System.Windows.MessageBox.Show(
                $"Unhandled error:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
        catch { /* ignore */ }

        // Don't swallow; failing fast is safer, but now we have the log + popup.
        e.Handled = false;
    }
}


