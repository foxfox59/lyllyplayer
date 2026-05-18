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
    /// <summary>First supported media path from cold-start args (set before <see cref="MainWindow"/> load).</summary>
    internal static string? ColdStartOpenFilePath { get; private set; }

    internal static void ConsumeColdStartOpenFilePath()
    {
        ColdStartOpenFilePath = null;
    }

    internal static void SetColdStartOpenFilePathIfUnset(string? path)
    {
        if (!string.IsNullOrWhiteSpace(ColdStartOpenFilePath))
            return;
        var normalized = PlaylistDragDropHelper.TryNormalizeLocalAudioPath(path);
        if (string.IsNullOrWhiteSpace(normalized) || !FileOpenIpc.LooksLikeSupportedFileOpenArg(normalized))
            return;
        ColdStartOpenFilePath = normalized;
        BeginColdStartOpenSettlement();
    }

    /// <summary>
    /// Explorer passes the opened file as a shell "drop" when the playlist window appears. That second pass
    /// was invoking YouTube import and showing "Added 1 items" while wiping the real append. Block automated
    /// drop/url imports until cold-start settlement completes (and briefly after).
    /// </summary>
    internal static DateTime SuppressAutomatedPlaylistImportsUntilUtc { get; private set; } =
        DateTime.UtcNow.AddSeconds(20);

    /// <summary>True while a cold-start Explorer file must be merged into the playlist without interference.</summary>
    internal static bool ColdStartOpenSettlementPending { get; private set; }

    internal static void BeginColdStartOpenSettlement()
    {
        ColdStartOpenSettlementPending = true;
        SuppressAutomatedPlaylistImportsUntilUtc = DateTime.UtcNow.AddSeconds(60);
    }

    internal static void CompleteColdStartOpenSettlement()
    {
        ColdStartOpenSettlementPending = false;
        SuppressAutomatedPlaylistImportsUntilUtc = DateTime.UtcNow.AddSeconds(8);
    }

    internal static bool ShouldSuppressAutomatedPlaylistImports()
        => ColdStartOpenSettlementPending
           || !string.IsNullOrWhiteSpace(ColdStartOpenFilePath)
           || DateTime.UtcNow < SuppressAutomatedPlaylistImportsUntilUtc;

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _openIpcCts;
    private IDisposable? _openIpcServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before any HWND: stable shell identity (taskbar / Task Manager "Apps" grouping).
        ShellProcessIdentity.TrySetExplicitAppUserModelId();

        base.OnStartup(e);

        try { AppVersion.LogRunningBuildIdentity(); } catch { /* ignore */ }

        // LibVLC native runtime (VideoLAN.LibVLC.Windows) + LibVLCSharp must initialize before Media/MediaPlayer.
        // Do not block the WPF dispatcher here — init runs on the dedicated LibVLC STA thread.
        _ = Task.Run(() =>
        {
            try { LibVlcHost.EnsureInitialized(); } catch { /* logged on first real use */ }
        });

        // Primary instance only: start IPC server for Explorer "open with" / file associations.
        try
        {
            _openIpcCts = new CancellationTokenSource();
            _openIpcServer = FileOpenIpc.StartServerBestEffort(path =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                                mw.HandleExternalOpenFileRequestBestEffort(path);
                        }
                        catch { /* ignore */ }
                    });
                }
                catch { /* ignore */ }
            }, _openIpcCts.Token);
        }
        catch { /* ignore */ }

        // Cold-start: stash path so MainWindow can merge it during startup playlist load.
        try
        {
            // MainWindow captures this on Loaded and applies it after the saved playlist finishes loading.
            // Do not queue HandleExternalOpen here — a second dispatch was racing startup and could show
            // "Added 1 items" while the playlist UI was rebuilt from a stale snapshot.
            ColdStartOpenFilePath =
                FileOpenIpc.TryGetFirstSupportedPathFromArgs(e.Args)
                ?? FileOpenIpc.TryGetFirstSupportedPathFromArgs(Environment.GetCommandLineArgs())
                ?? FileOpenIpc.TryGetFirstSupportedPathFromCommandLine();

            if (!string.IsNullOrWhiteSpace(ColdStartOpenFilePath))
            {
                ColdStartOpenFilePath = PlaylistDragDropHelper.TryNormalizeLocalAudioPath(ColdStartOpenFilePath)
                                        ?? ColdStartOpenFilePath;
                BeginColdStartOpenSettlement();
                try
                {
                    AppLog.Warn($"Open-with argv path: {ColdStartOpenFilePath}");
                }
                catch { /* ignore */ }
            }
            else
            {
                SuppressAutomatedPlaylistImportsUntilUtc = DateTime.UtcNow.AddSeconds(20);
                try
                {
                    var argc = Environment.GetCommandLineArgs().Length;
                    AppLog.Warn(
                        $"Open-with: no media path in argv ({argc} args). " +
                        "Explorer may deliver the file via shell drop when the playlist window opens.");
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
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
                // If we were launched with a supported file path (Explorer association), forward it to the primary instance.
                // Do this before showing the "already running" popup.
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    var p = FileOpenIpc.TryGetFirstSupportedPathFromArgs(args);
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        try
                        {
                            // Best-effort synchronous wait (very short): this is the second instance, so we want to exit ASAP.
                            var ok = FileOpenIpc.TrySendOpenFileRequestAsync(p!, timeoutMs: 400).GetAwaiter().GetResult();
                            if (ok)
                            {
                                Environment.Exit(0);
                                return;
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }

                try
                {
                    TopmostMessageBox.Show(
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
        try { _openIpcCts?.Cancel(); } catch { /* ignore */ }
        try { _openIpcServer?.Dispose(); } catch { /* ignore */ }
        _openIpcServer = null;
        try { _openIpcCts?.Dispose(); } catch { /* ignore */ }
        _openIpcCts = null;

        try
        {
            if (Current?.MainWindow is MainWindow mw)
            {
                try { mw.EnsurePlaybackShutdownBestEffort(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
        _singleInstanceMutex = null;
        try { Player.VlcDispatcherHost.ShutdownBestEffort(); } catch { /* ignore */ }
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
            TopmostMessageBox.Show(
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


