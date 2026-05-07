using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using LyllyPlayer.Models;
using LyllyPlayer.Services;
using LyllyPlayer.ShellServices;
using LyllyPlayer.Shell;
using LyllyPlayer.Utils;
using LyllyPlayer.Player;
using LyllyPlayer.Settings;
using LyllyPlayer.Windows;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace LyllyPlayer;

public partial class MainWindow : Window
{
    private int _externalOpenRequestId;

    // Prefer topmost/active window as dialog owner.

    public void HandleExternalOpenFileRequestBestEffort(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var p = path.Trim().Trim('"');
            if (!FileOpenIpc.LooksLikeSupportedFileOpenArg(p))
                return;

            var requestId = Interlocked.Increment(ref _externalOpenRequestId);
            Dispatcher.BeginInvoke(async () =>
            {
                if (requestId != _externalOpenRequestId)
                    return;

                try { await HandleExternalOpenFileRequestCoreAsync(p).ConfigureAwait(true); }
                catch { /* ignore */ }
            }, DispatcherPriority.Loaded);
        }
        catch { /* ignore */ }
    }

    private async Task HandleExternalOpenFileRequestCoreAsync(string path)
    {
        var p = (path ?? "").Trim().Trim('"');
        if (!FileOpenIpc.LooksLikeSupportedFileOpenArg(p))
            return;

        var ext = "";
        try { ext = (System.IO.Path.GetExtension(p) ?? "").Trim().ToLowerInvariant(); } catch { ext = ""; }

        if (ext == ".lyllytheme")
        {
            await ApplyThemeFromFileBestEffortAsync(p).ConfigureAwait(true);
            return;
        }

        if (ext == ".lyllylist")
        {
            await PromptAndOpenPlaylistFromFileAsync(p).ConfigureAwait(true);
            return;
        }

        if (LocalPlaylistLoader.IsSupportedAudioExtension(ext))
        {
            // Explorer double-click on an audio file: append to current playlist by default (portable associations / Open with).
            var localId = "";
            try { localId = LocalPlaylistLoader.CreateLocalIdFromPath(p); } catch { localId = ""; }
            await AddFilesAsync(
                new[] { p },
                append: true,
                removeDuplicates: _localImportRemoveDuplicates,
                cancellationToken: CancellationToken.None,
                progress: null).ConfigureAwait(true);
            try { _playlistWindow?.FocusVideoIdBestEffort(localId); } catch { /* ignore */ }
            return;
        }
    }

    private Task ApplyThemeFromFileBestEffortAsync(string path)
    {
        try
        {
            var json = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxThemeImportFileBytes);
            var theme = System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(json, SafeJson.CreateDeserializerOptions());
            if (theme is null)
                return Task.CompletedTask;

            ApplyThemeSettings(theme);
        }
        catch { /* ignore */ }

        return Task.CompletedTask;
    }

    private async Task PromptAndOpenPlaylistFromFileAsync(string path)
    {
        try
        {
            var owner = DialogOwnerHelper.GetBestOwnerWindow() ?? this;
            var dlg = new OpenPlaylistFileDialog(path) { Owner = owner };
            dlg.ShowActivated = true;
            _ = dlg.ShowDialog();
            var mode = dlg.Mode;
            if (mode == PlaylistOpenMode.Cancel)
                return;

            var result = _playlistFiles.LoadSavedPlaylist(path);
            if (!result.Success || result.Playlist is null)
            {
                try
                {
                    TopmostMessageBox.Show(
                        result.ErrorMessage ?? "Could not load the playlist file.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { /* ignore */ }
                return;
            }

            var entries = result.Entries;
            var pl = result.Playlist;
            var title = pl.Name;

            if (mode == PlaylistOpenMode.Replace)
            {
                _lastPlaylistSourceType = ParsePlaylistSourceType(pl.SourceType);
                _lastLocalPlaylistPath = path;
                _playlistSourceText = path;
                _playlistWindow?.SetSourceText(path);

                await LoadPlaylistFromEntriesAsync(entries, title: title, sourceKey: path, isStartupAutoLoad: false).ConfigureAwait(true);
                ApplySavedPlaylistOriginsIfAny(pl, entries);
                UpdateRefreshEnabled();
                UpdatePlaylistTitleDisplayForNowPlaying();
                return;
            }

            _playlistIsCompound = true;
            var removeDupes = _localImportRemoveDuplicates;
            var (added, removedDupes) = AppendEntriesPreserveCurrent(
                entries,
                originLabel: string.IsNullOrWhiteSpace(title) ? "Playlist" : title,
                originSource: path,
                removeDuplicates: removeDupes,
                cancellationToken: CancellationToken.None);
            TryShowAppendSummaryDialog("Playlist", added, removedDupes);

            try { ApplySavedPlaylistOriginsIfAny(pl, entries); } catch { /* ignore */ }

            UpdateRefreshEnabled();
            UpdatePlaylistTitleDisplayForNowPlaying();
        }
        catch { /* ignore */ }
    }

    public static readonly DependencyProperty IsAlwaysOnTopUiProperty =
        DependencyProperty.Register(nameof(IsAlwaysOnTopUi), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsAlwaysOnTopUi
    {
        get => (bool)GetValue(IsAlwaysOnTopUiProperty);
        private set => SetValue(IsAlwaysOnTopUiProperty, value);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    private static readonly IntPtr HWND_TOP = new(0);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_MINIMIZE = 6;

    private const int GWL_STYLE = -16;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    // Prefer ITaskbarList DeleteTab/AddTab to hide the taskbar button without using WS_EX_TOOLWINDOW.
    // WS_EX_TOOLWINDOW hides the taskbar button but also removes the app from Task Manager's "Apps" group.
    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList { }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const int WM_COMMAND = 0x0111;
    private const int WM_NULL = 0x0000;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_TOPALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;

    private const uint TrayCmdOpen = 1001;
    private const uint TrayCmdPrev = 1002;
    private const uint TrayCmdPlayPause = 1003;
    private const uint TrayCmdNext = 1004;
    private const uint TrayCmdStop = 1005;
    private const uint TrayCmdExit = 1006;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private IntPtr TrayMessageWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg != WM_TRAYICON)
                return IntPtr.Zero;

            var evtMsg = unchecked((int)lParam.ToInt64());
            if (evtMsg == WM_LBUTTONDBLCLK)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ShowMainWindowFromTray(); } catch { /* ignore */ }
                }), DispatcherPriority.Normal);
                return IntPtr.Zero;
            }

            // Right-click tray menu handled by Hardcodet TaskbarIcon.
        }
        catch { /* ignore */ }
        return IntPtr.Zero;
    }

    private void ShowMainWindowFromTray()
    {
        try
        {
            if (!IsVisible)
                Show();
        }
        catch { /* ignore */ }

        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
        }
        catch { /* ignore */ }

        try { Activate(); } catch { /* ignore */ }
        try { Focus(); } catch { /* ignore */ }
    }

    // Native tray menu implementation removed; Hardcodet.NotifyIcon.Wpf provides a reliable tray ContextMenu.

    private IntPtr EnsureTrayMessageHwnd()
    {
        if (_trayMessageHwnd != IntPtr.Zero)
            return _trayMessageHwnd;

        try
        {
            var p = new HwndSourceParameters("LyllyPlayer.TrayMessageWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            };
            _trayMessageSource = new HwndSource(p);
            _trayMessageSource.AddHook(TrayMessageWndProc);
            _trayMessageHwnd = _trayMessageSource.Handle;
        }
        catch
        {
            _trayMessageSource = null;
            _trayMessageHwnd = IntPtr.Zero;
        }

        return _trayMessageHwnd;
    }

    private void LogShellState(string tag, IntPtr mainHwnd, IntPtr trayHwnd)
    {
        try
        {
            if (mainHwnd == IntPtr.Zero)
                return;
            var ex = unchecked((uint)GetWindowLongPtr(mainHwnd, GWL_EXSTYLE).ToInt64());
            var style = unchecked((uint)GetWindowLongPtr(mainHwnd, GWL_STYLE).ToInt64());
            var showsTaskbar = (ex & WS_EX_TOOLWINDOW) == 0;
            AppLog.Info(
                $"TrayGapDiag {tag}: mainHwnd=0x{mainHwnd.ToInt64():X} trayHwnd=0x{trayHwnd.ToInt64():X} " +
                $"ex=0x{ex:X} style=0x{style:X} showsTaskbar={showsTaskbar} " +
                $"trayAdded={_trayIconAdded} lastTray={_lastAppliedShowTray} lastTaskbar={_lastAppliedShowInTaskbar} rendered={_hasRenderedOnce}",
                AppLogInfoTier.Diagnostic);
        }
        catch { /* ignore */ }
    }

    /// <summary>True when settings.json did not exist at startup (first install / reset).</summary>
    private readonly bool _isFreshSettingsInstall;
    private readonly SettingsStartupLoadInfo _settingsStartupLoadInfo;

    private bool _isShuttingDown;
    private enum PlaylistSourceType
    {
        YouTube,
        M3U,
        Folder,
        SearchYoutubeMusic
    }
    private enum VisualizerMode
    {
        Vu,
        Spectrum,
        Off,
    }

    /// <summary>Default theme and low-saturation &quot;From image&quot; accent — steel slate (not #3B82F6 / Windows blue).</summary>
    private static readonly System.Windows.Media.Color DefaultThemeMutedAccentRgb =
        System.Windows.Media.Color.FromRgb(0x4A, 0x6B, 0x8C);

    /// <summary>Captured playlist UI/engine state for cancel/rollback (folder/M3U/refresh).</summary>
    private sealed class PlaylistRestoreSnapshot
    {
        public List<PlaylistEntry> Original = new();
        public List<PlaylistEntry> Current = new();
        public string PlaylistSourceText = "";
        public string? LastLocalPlaylistPath;
        public PlaylistSourceType LastSourceType;
        public string? LoadedPlaylistId;
        public string? PlaylistTitle;
        public bool HasLoadedPlaylist;
        public string? CurrentVideoId;

        public static PlaylistRestoreSnapshot Capture(MainWindow w)
        {
            return new PlaylistRestoreSnapshot
            {
                Original = w._playlistCore.Entries.ToList(),
                // Current = w._currentEntries.ToList(),
                PlaylistSourceText = w._playlistSourceText ?? "",
                LastLocalPlaylistPath = w._lastLocalPlaylistPath,
                LastSourceType = w._lastPlaylistSourceType,
                LoadedPlaylistId = w._loadedPlaylistId,
                PlaylistTitle = w._playlistTitle,
                HasLoadedPlaylist = w._hasLoadedPlaylist,
                CurrentVideoId = w._engine.GetCurrent()?.VideoId
            };
        }

        public void Restore(MainWindow w)
        {
            w._playlistCore.ReplaceEntries(Original);
            // w._currentEntries = Current.ToList();
            w._playlistSourceText = PlaylistSourceText;
            w._lastLocalPlaylistPath = LastLocalPlaylistPath;
            w._lastPlaylistSourceType = LastSourceType;
            w._loadedPlaylistId = LoadedPlaylistId;
            w._playlistTitle = PlaylistTitle;
            w._hasLoadedPlaylist = HasLoadedPlaylist;

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(CurrentVideoId))
            {
                var idx = FindIndexByVideoId(w._playlistCore.Entries, CurrentVideoId);
                if (idx >= 0 && idx < w._playlistCore.Entries.Count)
                    startIndex = idx;
            }

            var displayIndex = w.GetOriginalIndexByVideoId(CurrentVideoId) ?? 0;

            w.SetPlaylistTitle(PlaylistTitle);
            w.SetQueueList(w._playlistCore.Entries, w._playlistCore.Entries.Count == 0 ? -1 : displayIndex);
            // w._engine.SetQueue(w._currentEntries, w._currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            w.UpdateRefreshEnabled();
            w.SyncNowPlayingFromEngine();
            try { w._playlistWindow?.SetSourceText(w._playlistSourceText ?? ""); } catch { /* ignore */ }
            try { w.SetStatusMessage("INFO", "Cancelled."); } catch { /* ignore */ }
            try { w.FocusPlaylistOnNowPlaying(); } catch { /* ignore */ }
        }
    }

    private readonly YtDlpClient _ytDlp;
    private readonly PlaybackEngine _engine;
    /// <summary>Explicit path from settings, or null to resolve yt-dlp from PATH.</summary>
    private string? _savedYtDlpPath;
    /// <summary>Legacy settings field (ignored for playback; LibVLC replaces FFmpeg).</summary>
    private string? _savedFfmpegPath;
    /// <summary>Optional explicit node.exe path; Advanced features need a resolved Node.</summary>
    private string? _savedNodePath;
    private bool _internalYtDlpUpdateCheckEnabled;
    private string _ytdlpEjsComponentSource = "github";
    private bool _youtubeCookiesFromBrowserEnabled;
    private string _youtubeCookiesFromBrowser = "";
    private bool _youtubeImportAppend;
    private bool _exportM3uIncludeYoutube = true;
    private bool _exportM3uPreferRelativePaths;
    private bool _exportM3uIncludeLyllyMetadata = true;
    private bool _localImportAppend;
    private bool _localImportRemoveDuplicates;
    private bool _playlistDragDropAppend = true;
    private bool _playlistDragDropRemoveDuplicates = true;
    private readonly AppSettings _startupSettings;
    private readonly AppShell _shell = new();
    private PlaylistService _playlistCore => _shell.Playlist;
    private PlayOrderService _playOrder => _shell.PlayOrder;
    private SettingsService _settingsService => _shell.Settings;
    private PlaylistFileService _playlistFiles => _shell.PlaylistFiles;

    private LyricsService _lyricsService => _shell.Lyrics;
    /// <summary>Stack of previously played track VideoIds for "Previous" button navigation.</summary>
    private readonly Stack<string> _previousTrackHistory = new();
    /// <summary>Maximum number of entries in _previousTrackHistory to avoid unbounded growth.</summary>
    private const int MaxPreviousTrackHistory = 100;
    private bool _suppressPreviousTrackHistoryPushOnce;

    private void PlayTargetEntryNow(PlaylistEntry entry, bool suppressHistoryPushOnce)
    {
        try
        {
            _pendingResumeSeconds = 0;
            _pendingResumeVideoId = null;
            _suppressAutoScrollVideoId = null;

            var idx = _playlistCore.Entries.FindIndex(e => string.Equals(e.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                return;

            _suppressPreviousTrackHistoryPushOnce = suppressHistoryPushOnce;
            _engine.SetQueue(_playlistCore.Entries, startIndex: idx, raiseNowPlayingChanged: true);
            _ = _engine.PlayCurrentAsync();
        }
        catch { /* ignore */ }
    }

    private void NavigateNextFromResolverBestEffort()
    {
        try
        {
            // Keep the old behavior: if current is a queued item, consume it on manual Next.
            if (_nowPlayingEntry is not null && _queuedNext.Count > 0 &&
                string.Equals(_queuedNext[0].Entry.VideoId, _nowPlayingEntry.VideoId, StringComparison.OrdinalIgnoreCase))
            {
                _queuedNext.RemoveAt(0);
                if (_queueItems.Count > 0 && _queueItems[0].IsQueued)
                    _queueItems.RemoveAt(0);
                UpdateQueueOrdinals();
                _playlistWindow?.RefreshQueueView();
                RequestPersistSnapshot();
                if (_queuedNext.Count == 0)
                    try { FocusPlaylistOnNowPlaying(); } catch { /* ignore */ }
            }

            var next = ResolveNextTrack();
            if (next is null)
            {
                // End of list (repeat none) etc. Fall back to engine behavior.
                _ = _engine.NextAsync();
                return;
            }

            PlayTargetEntryNow(next, suppressHistoryPushOnce: false);
        }
        catch
        {
            try { _ = _engine.NextAsync(); } catch { /* ignore */ }
        }
    }
    ObservableCollection<QueueItem> _queueItems = new();
    ObservableCollection<QueueItem> _playlistItems = new ObservableCollection<QueueItem>();
    private readonly HashSet<string> _unavailableVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ageRestrictedVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _premiumVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _playlistTitle;
    private string _playlistSourceText = "";
    private int? _autoRefreshMinutes;
    private PlaylistWindow? _playlistWindow;
    private OptionsWindow? _optionsWindow;
    private LogWindow? _logWindow;
    private LyricsWindow? _lyricsWindow;
    private readonly AuxWindowController<PlaylistWindow> _playlistAuxCtl = new();
    private readonly AuxWindowController<OptionsWindow> _optionsAuxCtl = new();
    private readonly AuxWindowController<LyricsWindow> _lyricsAuxCtl = new();
    private string _nowPlayingStatus = "STOPPED";
    private PlaylistEntry? _nowPlayingEntry;
    private string? _suppressAutoScrollVideoId;
    private DateTime _suppressAutoScrollUntilUtc;
    private bool _shuffleEnabled;
    private readonly Random _shuffleRandom = new();
    private Queue<PlaylistEntry> _shuffleNextBuffer = new();

    private bool _startupResumeAttempted;
    private bool _hasLoadedPlaylist;
    private bool _playlistIsCompound;
    private sealed record QueuedInstance(Guid Id, PlaylistEntry Entry);
    private readonly List<QueuedInstance> _queuedNext = new();
    private Guid? _manualQueuedPlayInstanceId;
    private Guid? _playingQueuedInstanceId;
    private PlaylistRestoreSnapshot? _cancelPlaylistSnapshot;
    private string? _loadedPlaylistId;
    private bool _globalMediaKeysEnabled;
    private GlobalMediaHotkeys? _mediaHotkeys;
    private readonly Random _rng = new();
    private bool _ignoreSeekBar;
    private bool _isSeeking;
    /// <summary>VideoId active when seek drag started; discard mouse-up seek if the track changed (e.g. Next while dragging).</summary>
    private string? _seekMouseDownVideoId;
    private readonly DispatcherTimer _uiTimer;
    private VisualizerMode _visualizerMode = VisualizerMode.Vu;
    private System.Windows.Shapes.Path? _spectrumCurvePath;
    private System.Windows.Shapes.Rectangle[]? _spectrumGridLines;
    /// <summary>Horizontal rules in the spectrum view (fixed count; spacing follows canvas height).</summary>
    private const int SpectrumGridLineCount = 5;
    private DateTime _lastNonZeroVuUtc = DateTime.UtcNow;

    private DispatcherTimer? _refreshTimer;
    private int _uiTimerTickCounter;
    private long _lastSeekBufRefreshMs;

    // If we closed while paused, restore the timeline but only start playback when user hits Play.
    private string? _pendingResumeVideoId;
    private double _pendingResumeSeconds;
    private readonly DispatcherTimer _persistTimer;
    private bool _snapshotDirty;
    private bool _snapshotPersistInFlight;
    private int _youtubeDurationRequestId;
    private readonly Dictionary<string, int> _youtubeDurationByVideoId = new(StringComparer.OrdinalIgnoreCase);
    private int _lyricsResolveRequestId;
    private CancellationTokenSource? _lyricsResolveCts;
    private string? _lyricsResolveInFlightVideoId;

    // Persist playlist window bounds even when it's closed.
    private Rect? _lastPlaylistBounds;
    private WindowState? _lastPlaylistWindowState;
    // Persist options window bounds even when it's closed.
    private Rect? _lastOptionsBounds;
    private WindowState? _lastOptionsWindowState;
    private Rect? _lastLyricsBounds;
    private WindowState? _lastLyricsWindowState;
    private enum PlaylistSnapEdge { None, Left, Right, Bottom, Top }
    private bool _syncingWindowMove;
    private bool _playlistSnapped;
    private PlaylistSnapEdge _playlistSnapEdge = PlaylistSnapEdge.None;
    private double _playlistDockYOffset;
    private double _playlistDockXOffset;
    /// <summary>UI scale % that <see cref="_playlistWindow"/> outer Width/Height currently match (for proportional resize when scale changes).</summary>
    private int? _playlistWindowOuterAtUiScalePercent;
    private enum OptionsSnapEdge { None, Left, Right, Bottom, Top }
    private bool _optionsSnapped;
    private OptionsSnapEdge _optionsSnapEdge = OptionsSnapEdge.None;
    private double _optionsDockYOffset;
    private double _optionsDockXOffset;
    /// <summary>Persisted Options tab (header id: Tools, System, …).</summary>
    private string _optionsSelectedTab = "Tools";
    private enum LyricsSnapEdge { None, Left, Right, Bottom, Top }
    private bool _lyricsSnapped;
    private LyricsSnapEdge _lyricsSnapEdge = LyricsSnapEdge.None;
    private double _lyricsDockYOffset;
    private double _lyricsDockXOffset;
    private const double BaseSnapThresholdPx = 18;
    private const double BaseSnapUnsnapPx = 40;
    private const double SnapGapPx = 0;
    private const double SnapPersistAdjacencyPx = 2;
    private const double SnapPersistMinOverlapPx = 24;
    // Options should snap similarly to Playlist; too-large thresholds feel "magnetic" and accidental.
    private const double BaseOptionsSnapThresholdPx = 18;
    private const double BaseOptionsSnapUnsnapPx = 40;

    private double SnapThresholdPx => BaseSnapThresholdPx * UiScale;
    private double SnapUnsnapPx => BaseSnapUnsnapPx * UiScale;
    private double OptionsSnapThresholdPx => BaseOptionsSnapThresholdPx * UiScale;
    private double OptionsSnapUnsnapPx => BaseOptionsSnapUnsnapPx * UiScale;

    /// <summary>
    /// Left/right snap requires vertical overlap between main and aux. A fixed 64px band matches tall default chrome;
    /// Ultra-compact main height is often under 64 DIP, so overlap can never exceed the threshold and side snap never triggers.
    /// </summary>
    private static double SideSnapVerticalOverlapThresholdPx(double mainOuterHeightDip)
    {
        if (mainOuterHeightDip <= 0 || double.IsNaN(mainOuterHeightDip) || double.IsInfinity(mainOuterHeightDip))
            return 64;
        return Math.Min(64.0, Math.Max(8.0, mainOuterHeightDip - 2.0));
    }

    /// <summary>
    /// Vertical overlap for left/right snap tests only (not for positioning). Ultra-compact main is a short strip;
    /// a tall playlist is often aligned to a side with its top <i>below</i> the strip (near <see cref="mainBottom"/>),
    /// so raw intersection is empty. Inflate the test band, with more reach <i>downward</i> than upward.
    /// </summary>
    private static double ComputeSideSnapVerticalOverlapDip(
        double mainTop, double mainBottom, double auxTop, double auxBottom)
    {
        var mainH = mainBottom - mainTop;
        if (mainH <= 0 || double.IsNaN(mainH) || double.IsInfinity(mainH))
            return Math.Min(mainBottom, auxBottom) - Math.Max(mainTop, auxTop);

        if (mainH >= 96)
            return Math.Min(mainBottom, auxBottom) - Math.Max(mainTop, auxTop);

        var inflateUp = Math.Clamp(80 - mainH * 0.5, 24, 72);
        // Playlist body usually hangs below the control strip; allow a generous band under mainBottom.
        var inflateDown = Math.Clamp(320 - mainH, 140, 420);
        var effTop = mainTop - inflateUp;
        var effBottom = mainBottom + inflateDown;
        return Math.Min(effBottom, auxBottom) - Math.Max(effTop, auxTop);
    }

    private const double ChromeDragPendingMoveThresholdDip = 4.0;
    /// <summary>Hold-still fallback so a drag can start without movement (double-click uses ClickCount, not this timer).</summary>
    private static readonly TimeSpan ChromeDragPendingHoldDelay = TimeSpan.FromMilliseconds(120);

    private bool _chromeDragging;
    private DispatcherTimer? _chromeDragStartDelayTimer;
    private bool _chromeDragPendingMoveListener;
    private System.Windows.Point _chromePendingDragWindowPoint;
    // Legacy manual drag state removed (DragMove is used instead).

    private DispatcherTimer? _snapRestoreDebounceTimer;
    private DispatcherTimer? _auxSnapSyncDebounceTimer;
    private int _auxSnapSyncRequestId;
    private int _auxSnapSyncFrameQueued;

    /// <summary>Minimal main layout: no options/playlist row; hide shuffle/repeat and playlist title in the card.</summary>
    private bool _mainWindowCompact;
    private bool _compactModeHidesAuxWindows = true;
    private string _compactModeLayout = "Normal";

    /// <summary>Restore <see cref="PlaylistWindow"/> when leaving compact (seeded at startup from settings or when windows were open).</summary>
    private bool _playlistWindowWasOpenBeforeCompact;

    /// <summary>Restore <see cref="OptionsWindow"/> when leaving compact.</summary>
    private bool _optionsWindowWasOpenBeforeCompact;

    /// <summary>Restore <see cref="LyricsWindow"/> when leaving compact.</summary>
    private bool _lyricsWindowWasOpenBeforeCompact;

    private bool _suppressShuffleToggle;
    private bool _suppressCompactShuffleToggle;
    private Rect? _mainWindowCompactBoundsBeforeExpand;
    private Rect? _mainWindowExpandedBoundsAfterExpand;
    private bool _mainWindowExpandedMovedSinceExpand;
    private bool _compactUserOpenedPlaylistWindow;
    private bool _compactUserOpenedLyricsWindow;

    private enum RepeatMode { None, Single, Playlist }
    private RepeatMode _repeatMode = RepeatMode.None;
    private bool _startupBoundsApplied;
    private bool _shellStyleHookAttached;
    private HwndSource? _shellStyleHookSource;
    private readonly HwndSourceHook _shellStyleHook;
    private bool _includeSubfoldersOnFolderLoad;
    private int _cacheMaxMb = 512;
    private PlaylistSourceType _lastPlaylistSourceType = PlaylistSourceType.YouTube;
    private string? _lastLocalPlaylistPath;
    private string _lastYoutubeUrl = "";
    private string _themeMode = "Auto";
    private string _backgroundMode = "Default (Lylly)";
    private string _customBackgroundImagePath = "";
    private string _backgroundColorMode = "From image";
    private string _customBackgroundColor = "";
    private int _backgroundAlpha = SettingsStore.DefaultBackgroundAlpha;
    private int _backgroundScrimPercent = SettingsStore.DefaultBackgroundScrimPercent;
    private string _backgroundImageStretch = "BestFit";
    private RectN? _backgroundUserDefinedMainNormal;
    private RectN? _backgroundUserDefinedMainCompact;
    private RectN? _backgroundUserDefinedMainUltra;
    private RectN? _backgroundUserDefinedPlaylist;
    private RectN? _backgroundUserDefinedOptionsLog;
    private RectN? _backgroundUserDefinedLyrics;
    private LyllyPlayer.Windows.BackgroundDesignerWindow? _backgroundDesignerWindow;
    // Cached, measured window aspects (width/height) for background designer locking.
    // These are updated after layout settles so UserDefined crops match the real window footprint.
    private double _measuredMainDefaultAspect;
    private double _measuredMainCompactAspect;
    private double _measuredMainUltraAspect;

    // Coalesce deferred UserDefined crop refresh (avoid updating Viewbox before SizeToContent commits).
    private int _userDefinedMainCropRefreshGen;
    private bool? _lastAppliedMainCompactForUserDefined;
    private bool _userDefinedMainCropRefreshAfterSizePending;
    private int _userDefinedMainCropForceRenderFrames;
    private bool _userDefinedMainCropRenderHooked;

    /// <summary>
    /// When Playlist/Options are open, main-window resize + snap sync can fire many layout passes in quick succession.
    /// Each pass used to queue another Render-priority crop refresh, repeatedly mutating shared App brush resources and
    /// making the jerk scale with how many windows are bound to those resources. Debounce to a single refresh.
    /// </summary>
    private DispatcherTimer? _userDefinedMainBgDebounceTimer;

    /// <summary>
    /// When leaving compact with UserDefined wallpaper, skip <see cref="TryRestoreAuxiliaryWindowsAfterCompact"/>
    /// in <see cref="ApplyCompactAuxiliaryWindowState"/> and run it after <see cref="ApplyBackgroundFromSettings"/>
    /// on ContextIdle (whether aux stayed open during compact or were closed by compact-hide policy).
    /// </summary>
    private bool _pendingTryRestoreAfterLeaveCompactUserDefinedBg;

    /// <summary>
    /// Fingerprint of the bitmap backing UserDefined crops (path/uri + last-write/size when available).
    /// Used to detect stale <see cref="ImageBrush.ImageSource"/> after exports/replacements on disk.
    /// </summary>
    private string? _appliedUserDefinedBackgroundFingerprint;

    private string _appTitleMode = "Default";
    private string _customAppTitle = "";
    private string _appIconVisibility = "TaskbarOnly";
    private bool _lyricsEnabled;
    private bool _lyricsLocalFilesEnabled;
    private DispatcherTimer? _lyricsTimer;
    private System.Drawing.Icon? _trayIcon;
    private bool? _lastAppliedShowInTaskbar;
    private bool? _lastAppliedShowTray;
    private bool _queuedApplyAppIconVisibility;
    private bool _trayIconAdded;
    private bool _hasRenderedOnce;
    private bool _pendingShowTrayAfterRender;
    private bool _queueingStartupTrayShow;
    private HwndSource? _trayMessageSource;
    private IntPtr _trayMessageHwnd;
    private bool _userActivatedTrayAllowed;
    private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _hardcodetTrayIcon;
    private bool _nativeTrayCleanedUp;
    private ITaskbarList? _taskbarList;
    /// <summary>Last value passed to <see cref="ITaskbarList"/> AddTab/DeleteTab. Re-applying the same state every UI tick spams the shell and breaks focus with some window managers (e.g. DisplayFusion).</summary>
    private bool? _lastNativeTaskbarListShowInTaskbar;

    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 0x1B;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_STATE = 0x00000008;
    private const uint NIF_GUID = 0x00000020;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIS_HIDDEN = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    /// <summary>Binary Fortress DisplayFusion hooks shell Z-order; repeated <c>SetWindowPos</c> on our HWNDs can confuse it.</summary>
    private static bool? _cachedDisplayFusionRunning;
    private static DateTime _nextDisplayFusionProbeUtc;

    private static bool IsDisplayFusionRunning()
    {
        var now = DateTime.UtcNow;
        if (_cachedDisplayFusionRunning is bool b && now < _nextDisplayFusionProbeUtc)
            return b;
        try
        {
            _cachedDisplayFusionRunning = Process.GetProcessesByName("DisplayFusion").Length > 0;
        }
        catch
        {
            _cachedDisplayFusionRunning = false;
        }
        _nextDisplayFusionProbeUtc = now.AddSeconds(45);
        return _cachedDisplayFusionRunning.Value;
    }

    private const uint TrayUid = 1;
    private static readonly Guid TrayGuid = new("d2e9f5c8-40c3-4a4f-a9bf-0f9a6a5f3c2d"); // legacy (no longer used)
    private int _searchDefaultCount = 50;
    private int _searchMinLengthSeconds;
    private bool _readMetadataOnLoad;
    private bool _alwaysOnTop;
    private bool _alwaysOnTopPlaylistWindow;
    private bool _alwaysOnTopOptionsWindow;
    private bool _alwaysOnTopLyricsWindow;
    private bool _suppressAlwaysOnTopToggleEvents;
    private bool _raisingAuxWindows;
    private bool _playlistMinimizedByMain;
    private bool _optionsMinimizedByMain;
    private bool _playlistMinimizedByTopTaskbar;
    private bool _optionsMinimizedByTopTaskbar;
    private Rect? _playlistBoundsBeforeTopTaskbarMinimize;
    private Rect? _optionsBoundsBeforeTopTaskbarMinimize;
    private bool _restoringAuxFromMinimize;
    private bool _keepIncompletePlaylistOnCancel;
    private string? _lameEncoderPath;
    private string _mp3ExportEncodingMode = "Vbr";
    private int _mp3ExportCbrQualityIndex = Mp3QualityMaps.DefaultCbrSliderIndex;
    private int _mp3ExportVbrQualityIndex = Mp3QualityMaps.DefaultVbrSliderIndex;
    private bool _mp3ExportReplacePlaylistEntryAfterExport;
    private string _audioQuality = "Auto";
    private string? _audioOutputDevice;
    private bool _audioNormalizeEnabled;
    private string _appLogLevel = "ErrorsAndWarnings";
    private int _appLogMaxMb = 2;
    private int _uiScalePercent = 100;
    private string _windowBorderMode = "1px";
    private double _windowBorderCustomPx = 2;
    private double UiScale => Math.Clamp(_uiScalePercent / 100.0, 0.5, 2.0);

    private int _statusToastRequestId;
    private bool _bringingAuxToFrontOnActivate;

    public MainWindow()
    {
        _shellStyleHook = MainWindowShellStyleHwndHook;
        _isFreshSettingsInstall = !File.Exists(_settingsService.GetSettingsPath());
        _startupSettings = _settingsService.LoadStartup(out _settingsStartupLoadInfo);
        _mainWindowCompact = _startupSettings.MainWindowCompact ?? false;
        _compactModeHidesAuxWindows = _startupSettings.CompactModeHidesAuxWindows ?? true;
        _compactModeLayout = SettingsStore.NormalizeCompactModeLayout(_startupSettings.CompactModeLayout);
        if (_mainWindowCompact)
        {
            _playlistWindowWasOpenBeforeCompact = _startupSettings.PlaylistWindowOpen ?? false;
            _optionsWindowWasOpenBeforeCompact = _startupSettings.OptionsWindowOpen ?? false;
            _lyricsWindowWasOpenBeforeCompact = _startupSettings.LyricsWindowOpen ?? false;
        }

        _savedYtDlpPath = NormalizeToolSave(_startupSettings.YtDlpPath);
        _savedFfmpegPath = NormalizeToolSave(_startupSettings.FfmpegPath);
        _savedNodePath = NormalizeToolSave(_startupSettings.NodeJsPath);
        _internalYtDlpUpdateCheckEnabled = _startupSettings.InternalYtDlpUpdateCheckEnabled ?? false;
        _ytdlpEjsComponentSource = string.IsNullOrWhiteSpace(_startupSettings.YtdlpEjsComponentSource)
            ? "github"
            : _startupSettings.YtdlpEjsComponentSource.Trim();
        _youtubeCookiesFromBrowserEnabled = _startupSettings.YoutubeCookiesFromBrowserEnabled ?? false;
        _youtubeCookiesFromBrowser = _startupSettings.YoutubeCookiesFromBrowser ?? "";
        _youtubeImportAppend = _startupSettings.YoutubeImportAppend ?? false;
        _exportM3uIncludeYoutube = _startupSettings.ExportM3uIncludeYoutube ?? true;
        _exportM3uPreferRelativePaths = _startupSettings.ExportM3uPreferRelativePaths ?? false;
        _exportM3uIncludeLyllyMetadata = _startupSettings.ExportM3uIncludeLyllyMetadata ?? true;
        _localImportAppend = _startupSettings.LocalImportAppend ?? false;
        _localImportRemoveDuplicates = _startupSettings.LocalImportRemoveDuplicates ?? true;
        _playlistDragDropAppend = _startupSettings.PlaylistDragDropAppend ?? true;
        _playlistDragDropRemoveDuplicates = _startupSettings.PlaylistDragDropRemoveDuplicates ?? true;

        var yInit = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
        _ytDlp = new YtDlpClient(yInit.EffectiveFileName);

        InitializeComponent();

        // Universal gapless snapping between LyllyPlayer windows.
        try { WindowCoordinator.RegisterSnapping(this); } catch { /* ignore */ }
        try { Activated += MainWindow_Activated; } catch { /* ignore */ }

        _appLogLevel = AppLog.NormalizeLevelString(_startupSettings.AppLogLevel);
        try { AppLog.SetLevel(_appLogLevel); } catch { /* ignore */ }
        _appLogMaxMb = Math.Clamp(_startupSettings.AppLogMaxMb ?? SettingsStore.DefaultAppLogMaxMb, 1, 200);
        try { AppLog.SetMaxDiskMegabytes(_appLogMaxMb); } catch { /* ignore */ }

        var startupInfo = _settingsStartupLoadInfo;
        if (startupInfo.SettingsFileExisted)
        {
            var settingsPath = _settingsService.GetSettingsPath();
            if (startupInfo.RecoveryKind == SettingsStartupRecoveryKind.CorruptUsedDefaults)
            {
                TopmostMessageBox.Show(
                    "settings.json could not be read. Default settings are in use.\n\n"
                    + "You can delete or repair the file:\n"
                    + settingsPath,
                    "LyllyPlayer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (startupInfo.RecoveryKind == SettingsStartupRecoveryKind.PartialRecovery)
            {
                var tag = string.IsNullOrWhiteSpace(startupInfo.LastSavedByAppVersionReadFromFile)
                    ? ""
                    : $"\n\n(Version tag read from file: {startupInfo.LastSavedByAppVersionReadFromFile.Trim()})";
                TopmostMessageBox.Show(
                    "settings.json was damaged, but some values could still be read from it."
                    + tag
                    + "\n\nConsider fixing or backing up the file:\n"
                    + settingsPath,
                    "LyllyPlayer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        ApplyWindowSettings(_startupSettings);
        _lastPlaylistSourceType = ParsePlaylistSourceType(_startupSettings.LastPlaylistSourceType);
        _lastLocalPlaylistPath = _startupSettings.LastLocalPlaylistPath;
        _lastYoutubeUrl = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(_startupSettings.LastPlaylistUrl);

        _playlistSourceText = _lastPlaylistSourceType == PlaylistSourceType.YouTube
            ? (_startupSettings.LastPlaylistUrl ?? "")
            : (_startupSettings.LastLocalPlaylistPath ?? "");
        _autoRefreshMinutes = _startupSettings.PlaylistAutoRefreshMinutes;
        _globalMediaKeysEnabled = _startupSettings.GlobalMediaKeysEnabled ?? true;
        _includeSubfoldersOnFolderLoad = _startupSettings.IncludeSubfoldersOnFolderLoad ?? false;
        _themeMode = SettingsStore.NormalizeThemeMode(_startupSettings.ThemeMode);
        _backgroundMode = SettingsStore.NormalizeBackgroundMode(_startupSettings.BackgroundMode);
        _customBackgroundImagePath = _startupSettings.CustomBackgroundImagePath ?? "";
        _backgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(_startupSettings.BackgroundColorMode);
        _customBackgroundColor = _startupSettings.CustomBackgroundColor ?? "";
        _backgroundAlpha = _startupSettings.BackgroundAlpha is >= 0 and <= 255 ? _startupSettings.BackgroundAlpha.Value : SettingsStore.DefaultBackgroundAlpha;
        _backgroundScrimPercent = _startupSettings.BackgroundScrimPercent is >= 0 and <= 80 ? _startupSettings.BackgroundScrimPercent.Value : SettingsStore.DefaultBackgroundScrimPercent;
        _backgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(_startupSettings.BackgroundImageStretch);
        _appTitleMode = SettingsStore.NormalizeAppTitleMode(_startupSettings.AppTitleMode);
        _customAppTitle = _startupSettings.CustomAppTitle ?? "";
        _appIconVisibility = SettingsStore.NormalizeAppIconVisibility(_startupSettings.AppIconVisibility);
        _lyricsEnabled = _startupSettings.LyricsEnabled ?? false;
        _lyricsLocalFilesEnabled = _startupSettings.LyricsLocalFilesEnabled ?? false;
        _searchDefaultCount = _startupSettings.SearchDefaultCount is >= 1 and <= 200 ? _startupSettings.SearchDefaultCount.Value : 50;
        _searchMinLengthSeconds = _startupSettings.SearchMinLengthSeconds is >= 0 and <= 3600 ? _startupSettings.SearchMinLengthSeconds.Value : 0;
        _readMetadataOnLoad = _startupSettings.ReadMetadataOnLoad ?? false;
        _alwaysOnTop = _startupSettings.AlwaysOnTop ?? false;
        _alwaysOnTopPlaylistWindow = _startupSettings.AlwaysOnTopPlaylistWindow ?? false;
        _alwaysOnTopOptionsWindow = _startupSettings.AlwaysOnTopOptionsWindow ?? false;
        _alwaysOnTopLyricsWindow = _startupSettings.AlwaysOnTopLyricsWindow ?? false;
        _keepIncompletePlaylistOnCancel = _startupSettings.KeepIncompletePlaylistOnCancel ?? false;
        _lameEncoderPath = string.IsNullOrWhiteSpace(_startupSettings.LameEncoderPath)
            ? null
            : _startupSettings.LameEncoderPath.Trim();
        _mp3ExportEncodingMode = SettingsStore.NormalizeMp3ExportEncodingMode(_startupSettings.Mp3ExportEncodingMode);
        _mp3ExportCbrQualityIndex = SettingsStore.ClampMp3SliderIndex(
            _startupSettings.Mp3ExportCbrQualityIndex,
            Mp3QualityMaps.DefaultCbrSliderIndex);
        _mp3ExportVbrQualityIndex = SettingsStore.ClampMp3SliderIndex(
            _startupSettings.Mp3ExportVbrQualityIndex,
            Mp3QualityMaps.DefaultVbrSliderIndex);
        _mp3ExportReplacePlaylistEntryAfterExport = _startupSettings.Mp3ExportReplacePlaylistEntryAfterExport ?? false;
        _audioQuality = _startupSettings.AudioQuality ?? "Auto";
        _audioOutputDevice = string.IsNullOrWhiteSpace(_startupSettings.AudioOutputDevice) ? null : _startupSettings.AudioOutputDevice;
        _audioNormalizeEnabled = _startupSettings.AudioNormalize ?? false;
        _uiScalePercent = _startupSettings.UiScalePercent is >= 50 and <= 200 ? _startupSettings.UiScalePercent.Value : 100;
        _windowBorderMode = NormalizeWindowBorderMode(_startupSettings.WindowBorderMode);
        _windowBorderCustomPx = Math.Clamp(_startupSettings.WindowBorderCustomPx ?? 2, 1, 24);
        _optionsSelectedTab = SettingsStore.NormalizeOptionsWindowSelectedTab(_startupSettings.OptionsWindowSelectedTab);
        _cacheMaxMb = Math.Clamp(_startupSettings.CacheMaxMb ?? 512, 16, 102400);
        _backgroundUserDefinedMainNormal = _startupSettings.BackgroundUserDefinedMainNormal;
        _backgroundUserDefinedMainCompact = _startupSettings.BackgroundUserDefinedMainCompact;
        _backgroundUserDefinedMainUltra = _startupSettings.BackgroundUserDefinedMainUltra;
        _backgroundUserDefinedPlaylist = _startupSettings.BackgroundUserDefinedPlaylist;
        _backgroundUserDefinedOptionsLog = _startupSettings.BackgroundUserDefinedOptionsLog;
        _backgroundUserDefinedLyrics = _startupSettings.BackgroundUserDefinedLyrics;
        ApplyBackgroundFromSettings();
        ApplyBackgroundColorsFromSettings();
        ApplyAlwaysOnTopFromSettings();
        ApplyUiScale();
        ApplyAppTitleFromSettings();
        ApplyAppIconVisibilityFromSettings();
        ApplyVisualizerMode(ParseVisualizerMode(_startupSettings.VisualizerMode));

        // Initialize lyrics cache and timer
        var settingsDir = Path.GetDirectoryName(_settingsService.GetSettingsPath());
        if (!string.IsNullOrWhiteSpace(settingsDir))
            LyricsCache.Initialize(settingsDir);
        // Startup: hydrate cached lyrics immediately using the persisted CurrentVideoId.
        // This prevents the Lyrics window from staying empty while playlist/engine restore work is still running.
        try { TryHydrateLyricsFromCacheForStartupVideoIdBestEffort(); } catch { /* ignore */ }
        if (_lyricsEnabled)
            _startLyricsTimer();
        // Secondary windows may not exist yet (compact startup). Without this, snap/dock/bounds stay at
        // defaults and the next SaveSettingsSnapshot overwrites good persisted layout.
        ApplyStoredAuxiliaryWindowLayoutFromSettings(_startupSettings);
        ApplyMainWindowCompactMode();
        // playlist list is hosted in PlaylistWindow

        _engine = new PlaybackEngine(_ytDlp);
        _engine.SetNextTrackResolver(ResolveNextTrack);
        _engine.SetNextTrackPeekResolver(PeekNextTrackForPreheatOrPrefetch);
        ApplyYtdlpPlaybackOptions();
        try { _ytDlp.SetAudioQuality(_audioQuality); } catch { /* ignore */ }
        try { _engine.NotifyYoutubeAudioQualityChanged(); } catch { /* ignore */ }
        try { _engine.SetAudioOutputDevice(ResolveAudioDeviceNumber(_audioOutputDevice)); } catch { /* ignore */ }
        try { _engine.SetAudioNormalizeEnabled(_audioNormalizeEnabled); } catch { /* ignore */ }
        try
        {
            _engine.SetCacheMaxBytes(Math.Max(0, (long)_cacheMaxMb) * 1024L * 1024L);
        }
        catch { /* ignore */ }
        try
        {
            var vol = _startupSettings.Volume ?? 0.85;
            _engine.SetVolume(vol);
            VolumeSlider.Value = Math.Clamp(vol, 0, 1);
        }
        catch { /* ignore */ }

        // Restore duration early so seek UI can initialize immediately on startup.
        try
        {
            if (_startupSettings.CurrentDurationSeconds is int ds && ds > 0)
                _engine.OverrideCurrentDurationSeconds(ds);
        }
        catch { /* ignore */ }
        _engine.NowPlayingChanged += (_, entry) =>
        {
            try { AppLog.Warn($"NowPlayingChanged: enter videoId={(entry?.VideoId ?? "(null)")}"); } catch { /* ignore */ }
            try
            {
                // Async UI update: avoid synchronous Dispatcher.Invoke from the playback thread (hard-crash prone on some systems).
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { AppLog.Warn($"NowPlayingChanged(UI): begin videoId={(entry?.VideoId ?? "(null)")}"); } catch { /* ignore */ }
                _isSeeking = false;
                _seekMouseDownVideoId = null;
                try { SeekSlider.ReleaseMouseCapture(); } catch { /* ignore */ }

                if (entry is not null)
                {
                    try { AppLog.Warn("NowPlayingChanged(UI): entry_not_null"); } catch { /* ignore */ }
                    var isSameTrack = _nowPlayingEntry is not null &&
                        string.Equals(entry.VideoId, _nowPlayingEntry.VideoId, StringComparison.OrdinalIgnoreCase);

                    // Record previous track in history before updating _nowPlayingEntry.
                    // This enables "Previous" to navigate back to the actual previously playing track.
                    if (_nowPlayingEntry is not null && !isSameTrack && !_suppressPreviousTrackHistoryPushOnce && !_shuffleEnabled)
                    {
                        try { AppLog.Warn("NowPlayingChanged(UI): history_push_check"); } catch { /* ignore */ }
                        var prevId = _nowPlayingEntry.VideoId;
                        if (!string.IsNullOrWhiteSpace(prevId))
                        {
                            var topOfHistory = _previousTrackHistory.Count > 0 ? _previousTrackHistory.Peek() : null;
                            if (_previousTrackHistory.Count == 0 || topOfHistory != prevId)
                            {
                                _previousTrackHistory.Push(prevId);
                                while (_previousTrackHistory.Count > MaxPreviousTrackHistory)
                                {
                                    _previousTrackHistory.Pop();
                                }
                            }
                        }
                    }
                    _suppressPreviousTrackHistoryPushOnce = false;

                    _nowPlayingEntry = entry;
                    try { AppLog.Warn("NowPlayingChanged(UI): set_nowPlayingEntry"); } catch { /* ignore */ }

                    // Shuffle tape + "recently played" window for shuffle candidate selection.
                    if (_shuffleEnabled)
                        _playOrder.RecordNowPlayingForShuffleTape(entry.VideoId, _playlistCore.Entries.Count);
                    _playOrder.RecordRecentlyPlayedVideoId(entry.VideoId, _playlistCore.Entries.Count);

                    // Only clear lyrics when the track actually changes.
                    // Resuming stopped playback on the same track should preserve lyrics.
                    if (!isSameTrack)
                    {
                        try { AppLog.Warn("NowPlayingChanged(UI): clear_lyrics"); } catch { /* ignore */ }
                        // Clear lyrics BEFORE Refresh() so the window doesn't show stale lyrics
                        // from the previous track. TryResolveLyricsAsync (called async below) will
                        // call Refresh() again once new lyrics are resolved.
                        _lyricsService.ClearParsedLyricsState();
                        _lyricsWindow?.Refresh();

                        // Immediately hydrate from cache (no network) so the lyrics window is populated
                        // on startup/track change without waiting for async resolution.
                        try { TryLoadLyricsFromCacheForEntryBestEffort(entry); } catch { /* ignore */ }
                        try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
                    }

                    _nowPlayingStatus = "FETCHING";
                    try { AppLog.Warn("NowPlayingChanged(UI): set_status_fetching"); } catch { /* ignore */ }
                }
                else
                {
                    // When stopping (entry is null), keep _nowPlayingEntry and lyrics intact.
                    // This preserves the lyrics display and avoids unnecessary re-resolution
                    // when the user resumes playback on the same track.
                    _nowPlayingStatus = "STOPPED";
                }

                try
                {
                    if (_manualQueuedPlayInstanceId is Guid qid &&
                         (entry is null || !_queuedNext.Any(q => q.Id == qid && q.Entry.VideoId.Equals(entry.VideoId))))
                    {
                        _manualQueuedPlayInstanceId = null;
                    }
                }
                catch { /* ignore */ }

                try { AppLog.Warn("NowPlayingChanged(UI): updating_text"); } catch { /* ignore */ }
                UpdateNowPlayingText();
                UpdatePlaylistTitleDisplayForNowPlaying();
                UpdateNowPlayingFlag(entry);
                if (!ShouldSuppressAutoScroll(entry))
                    SelectAndScrollToNowPlaying(entry);
                UpdateDurationUi(entry?.DurationSeconds ?? _engine.CurrentDurationSeconds);

                // ← MOVE THIS INSIDE Dispatcher.Invoke:
                try { FocusPlaylistOnNowPlaying(); } catch { /* ignore */ }
                try { AppLog.Warn("NowPlayingChanged(UI): end"); } catch { /* ignore */ }
                }), DispatcherPriority.Render);
            }
            catch { /* ignore */ }

            _ = EnrichLocalNowPlayingAsync(entry);
            _ = EnrichYoutubeDurationNowPlayingAsync(entry);
            _ = TryResolveLyricsAsync();

            // Pre-heat lyrics for the next track (runs during current track's full duration)
            PreheatNextLyricsAsync();
        };
        _engine.PlaybackStateChanged += (_, isPlaying) =>
            Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = isPlaying ? "||" : ">";
                _nowPlayingStatus = isPlaying ? "BUFFERING" : (_engine.CanResume ? "PAUSED" : "STOPPED");
                UpdateNowPlayingText();
                UpdatePlaylistTitleDisplayForNowPlaying();

                // Lyrics update cadence: run a lightweight timer only while playing.
                // When paused/stopped, refresh once to keep the correct line highlighted.
                try
                {
                    if (_lyricsEnabled && isPlaying)
                        _startLyricsTimer();
                    else
                    {
                        _stopLyricsTimer();
                        UpdateLyricsDisplay(force: true);
                    }
                }
                catch { /* ignore */ }
            });
        _engine.PlaybackFailed += (_, payload) =>
            Dispatcher.Invoke(() => HandlePlaybackFailed(payload.entry, payload.message));
        _engine.PrefetchTagged += (_, tag) =>
            Dispatcher.Invoke(() => HandlePrefetchTagged(tag));
        _engine.YoutubeDiskCacheReady += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { UpdateExportMp3ControlsEnabled(); } catch { /* ignore */ }
            }), DispatcherPriority.Render);
        _engine.StatusChanged += (_, payload) =>
            Dispatcher.Invoke(() =>
            {
                // Only show fine-grained loading state for the currently selected/playing entry.
                if (_nowPlayingEntry is null || !string.Equals(_nowPlayingEntry.VideoId, payload.entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    return;
                _nowPlayingStatus = payload.status;
                UpdateNowPlayingText(extraDetail: payload.detail);
                UpdatePlaylistTitleDisplayForNowPlaying();
            });
        _engine.Error += (_, msg) => Dispatcher.Invoke(() =>
        {
            if (!LooksLikeCancelled(msg))
            {
                _nowPlayingStatus = "ERROR";
                UpdateNowPlayingText(extraDetail: msg);
                UpdatePlaylistTitleDisplayForNowPlaying();
            }
        });
        _engine.TrackEnded += (_, payload) => Dispatcher.Invoke(async () =>
        {
            // 1. Consume the ended track from queue (if it was a queued track)
            //    AND track its ID so ResolveNextTrack() skips it
            if (_nowPlayingEntry is not null && _queuedNext.Count > 0 &&
                string.Equals(_queuedNext[0].Entry.VideoId, _nowPlayingEntry.VideoId, StringComparison.OrdinalIgnoreCase))
            {
                // CONSUME — remove from queue now that playback ended
                _queuedNext.RemoveAt(0);
                if (_queueItems.Count > 0 && _queueItems[0].IsQueued)
                    _queueItems.RemoveAt(0);
                UpdateQueueOrdinals();
                _playlistWindow?.RefreshQueueView();
                RequestPersistSnapshot();
                if (_queuedNext.Count == 0)
                    try { FocusPlaylistOnNowPlaying(); } catch { /* ignore */ }

                // Track so ResolveNextTrack() skips it
                _playingQueuedInstanceId = _queuedNext.Count > 0 ? _queuedNext[0].Id : (Guid?)null;
            }
            else
            {
                // Track isn't queued — reset
                _playingQueuedInstanceId = null;
            }

                // 2. Repeat:Single
            if (_repeatMode == RepeatMode.Single)
            {
                    _ = Task.Run(async () =>
                    {
                        try { await _engine.PlayCurrentAsync().ConfigureAwait(false); }
                        catch (Exception ex) { try { AppLog.Exception(ex, "Repeat single PlayCurrentAsync failed"); } catch { /* ignore */ } }
                    });
                return;
            }

            // 3. End of play order checks
            if (_engine.PlayOrder.Count == 0)
                return;
            if (_engine.CurrentIndex >= _engine.PlayOrder.Count - 1)
            {
                if (_repeatMode == RepeatMode.Playlist)
                {
                    _engine.SetQueue(_engine.PlayOrder, startIndex: 0, raiseNowPlayingChanged: true);
                        _ = Task.Run(async () =>
                        {
                            try { await _engine.PlayCurrentAsync().ConfigureAwait(false); }
                            catch (Exception ex) { try { AppLog.Exception(ex, "Repeat playlist PlayCurrentAsync failed"); } catch { /* ignore */ } }
                        });
                }
                return;
            }

            // 4. Advance to next track
            await _engine.NextAsync();
        });

        _shuffleEnabled = _startupSettings.ShuffleEnabled ?? false;
        _suppressShuffleToggle = true;
        ShuffleToggle.IsChecked = _shuffleEnabled;
        _suppressShuffleToggle = false;
        UpdateShuffleToggleContent();

        _repeatMode = ParseRepeatMode(_startupSettings.RepeatMode);
        UpdateRepeatButtonContent();
        UpdateRefreshEnabled();

        // Background priority can be starved during startup (layout, restore, IO), which makes the
        // timeline/lyrics appear "stuck" for several seconds even while audio is playing.
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
            IsEnabled = true,
        };
        _uiTimer.Tick += (_, _) =>
        {
            UpdateTimelineUi();
            UpdateVisualizerUi();
            // WPF/Chrome can flip WS_EX_TOOLWINDOW without WM_STYLECHANGED; periodic re-assert keeps Task Manager "Apps" stable.
            if ((++_uiTimerTickCounter % 32) == 0)
            {
                try { ApplyMainWindowShellIntegration(); } catch { /* ignore */ }
            }
        };

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            IsEnabled = false,
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            // Prevent overlapping refreshes if yt-dlp is slow.
            _refreshTimer.Stop();
            try { await RefreshPlaylistAsync(preserveCurrentIfPossible: true); }
            finally { ApplyAutoRefreshSelection(); }
        };

        // Ensure the selected refresh interval is applied on startup.
        ApplyAutoRefreshSelection();

        _persistTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600),
            IsEnabled = false,
        };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            if (_isShuttingDown)
                return;
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }

            // Persist last playlist snapshot off the UI thread (can be large).
            try { _ = PersistLastPlaylistSnapshotIfNeededAsync(); } catch { /* ignore */ }
        };

        UpdateDurationUi(durationSeconds: null);

        Loaded += (_, _) =>
        {
            try
            {
                if (SeekSliderHostGrid is not null)
                    SeekSliderHostGrid.SizeChanged += (_, _) => { try { UpdateSeekBufferedVisuals(); } catch { /* ignore */ } };
            }
            catch { /* ignore */ }

            // Borderless chrome can leave WS_EX_TOOLWINDOW set, which drops the process from Task Manager "Apps".
            ApplyMainWindowShellIntegration();
            // Restore last resolved playlist snapshot (no refresh / no yt-dlp).
            Dispatcher.BeginInvoke(new Action(() => _ = LoadLastPlaylistFromSettingsAsync()), DispatcherPriority.Background);

            // Restore secondary window visibility after the main window is shown.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_isFreshSettingsInstall)
                    {
                        ApplyFirstRunMainWindowPlacement();
                        _ = ShowFirstRunExternalToolsNoticeAsync();
                    }
                }
                catch { /* ignore */ }
                try
                {
                    if (_startupSettings.PlaylistWindowOpen ?? false)
                    {
                        EnsurePlaylistWindowOpen();
                    }
                }
                catch { /* ignore */ }
                try
                {
                    if (_startupSettings.OptionsWindowOpen ?? false)
                    {
                        EnsureOptionsWindowOpen();
                    }
                }
                catch { /* ignore */ }
                try
                {
                    if (_startupSettings.LyricsWindowOpen ?? false)
                    {
                        EnsureLyricsWindowOpen();
                    }
                }
                catch { /* ignore */ }
                try
                {
                    if (_isFreshSettingsInstall)
                    {
                        // After layout, re-sync so playlist/options use final main bounds and scale.
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                // SyncOptionsWindowToMain removed; WindowSnapService handles interactive latching + cluster move.
                                RequestPersistSnapshot();
                            }
                            catch { /* ignore */ }
                        }), DispatcherPriority.ContextIdle);
                    }
                }
                catch { /* ignore */ }

                // After all windows have opened and applied their persisted bounds, restore latch relations
                // for the snap service so clusters behave correctly immediately on startup.
                try
                {
                    // Startup can trigger a flurry of Location/Size changes while windows are still settling.
                    // Delay the snap-state restore so we only scan once after things are stable (reduces jitter/CPU spikes).
                    _snapRestoreDebounceTimer?.Stop();
                    _snapRestoreDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(650)
                    };
                    _snapRestoreDebounceTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            if (WindowSnapService.AnyWindowDragging)
                                return; // keep waiting until the user isn't dragging
                        }
                        catch { /* ignore */ }

                        try { _snapRestoreDebounceTimer?.Stop(); } catch { /* ignore */ }
                        try { WindowSnapService.RestoreLatchedRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
                    };
                    _snapRestoreDebounceTimer.Start();
                }
                catch { /* ignore */ }

                // Ensure the "snap to window edge" sync runs after startup layout settles.
                // Without this, the persisted edge-based offsets can be computed against pre-render bounds
                // and only correct themselves after the user nudges the main window.
                try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
            }), DispatcherPriority.ContextIdle);
        };

        SourceInitialized += (_, _) =>
        {
            ApplyMainWindowShellIntegration();
            // Apply once at the latest safe point (after HWND exists).
            if (_startupBoundsApplied) return;
            _startupBoundsApplied = true;
            try
            {
                var s = _settingsService.LoadLatest();
                AppLog.Info($"Startup bounds (sourceinit) settings: L={s.WindowLeft} T={s.WindowTop} W={s.WindowWidth} H={s.WindowHeight} State={s.WindowState}");
                ApplyWindowSettings(s);
                ApplyMainWindowCompactMode();
                AppLog.Info($"Startup bounds (sourceinit) applied: L={Left} T={Top} W={Width} H={Height} State={WindowState}");
            }
            catch (Exception ex)
            {
                AppLog.Exception(ex, "Startup bounds apply (sourceinit) failed");
            }
        };

        SourceInitialized += (_, _) => UpdateGlobalMediaKeysRegistration();
        LocationChanged += (_, _) => OnMainWindowMovedOrSized();
        SizeChanged += (_, _) => OnMainWindowMovedOrSized();
        StateChanged += (_, _) =>
        {
            ApplyMainWindowShellIntegration();
            SyncAuxWindowsMinimizeStateWithMain();
            OnMainWindowMovedOrSized();
        };
        Activated += (_, _) =>
        {
            ApplyMainWindowShellIntegration();
            // Restore minimized auxiliaries even when activation came from a click (RaiseAux is mouse-gated).
            try { TryRestoreAuxAfterTopTaskbarMinimize(); } catch { /* ignore */ }
            QueueRaiseAuxWindowsOnce();
            // (Startup tray gap workaround runs in ContentRendered.)
        };
        ContentRendered += (_, _) =>
        {
            _hasRenderedOnce = true;
            // Allow tray icon initialization once the window has actually rendered.
            // This keeps the "tray refresher" fix, but removes the need for the user to focus/click first.
            if (!_userActivatedTrayAllowed)
            {
                _userActivatedTrayAllowed = true;
                _lastAppliedShowTray = null;
                try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }
            }
            // After first render, apply any deferred tray visibility.
            if (_pendingShowTrayAfterRender)
            {
                _pendingShowTrayAfterRender = false;
                if (_queueingStartupTrayShow)
                    return;
                _queueingStartupTrayShow = true;

                // Let WPF finish its initial non-client/style work (it can briefly flip WS_EX_TOOLWINDOW
                // during first render). Then re-assert shell integration and only then show the tray icon.
                _ = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await Task.Delay(200);
                        try { ApplyMainWindowShellIntegration(); } catch { /* ignore */ }
                        await Task.Delay(50);
                        // Force a re-apply so the tray icon is actually added/unhidden now.
                        _lastAppliedShowTray = null;
                        ApplyAppIconVisibilityFromSettings();
                    }
                    catch { /* ignore */ }
                    finally { _queueingStartupTrayShow = false; }
                }), DispatcherPriority.Background);
                return;
            }
            try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }

            // Kick one more layout-aware sync after the first render so edge snapping is correct immediately.
            try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
        };
        // If main is already active, Activated won't fire again. Also, chrome interactions often mark events
        // as handled. Use AddHandler(..., handledEventsToo:true) so clicks anywhere (including title bar)
        // can restore auxiliaries.
        AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) =>
            {
                // Back-compat: if anything prevents ContentRendered from running, allow tray on first click.
                if (!_userActivatedTrayAllowed)
                {
                    _userActivatedTrayAllowed = true;
                    _lastAppliedShowTray = null;
                    try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }
                }
                try { TryRestoreAuxAfterTopTaskbarMinimize(); } catch { /* ignore */ }
            }),
            handledEventsToo: true);
        // If auxiliaries are merely behind other windows (not minimized), bring them forward when the user
        // clicks the main window. Do this on mouse-up so we don't interfere with title bar drags/clicks.
        AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) =>
            {
                try { QueueRaiseAuxWindowsOnce(); } catch { /* ignore */ }
            }),
            handledEventsToo: true);

        AddHandler(
            UIElement.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler((_, _) =>
            {
                // Back-compat: if anything prevents ContentRendered from running, allow tray on first key press.
                if (!_userActivatedTrayAllowed)
                {
                    _userActivatedTrayAllowed = true;
                    _lastAppliedShowTray = null;
                    try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }
                }
            }),
            handledEventsToo: true);
        // NOTE: Avoid work on Deactivated (shell styles / HWND_TOPMOST resync fought the shell: taskbar restore
        // and foreground for other apps could fail; borderless minimize behavior also suffered).
        Deactivated += (_, _) => { };
        Closing += (_, _) =>
        {
            try
            {
                // no-op (legacy: used to un-hide UserDefined wallpaper)
            }
            catch { /* ignore */ }
            // Clear Topmost before teardown so we never rely on Win32 topmost cleanup after HWND destruction.
            try { Topmost = false; } catch { /* ignore */ }
            try { if (_playlistWindow is not null) _playlistWindow.Topmost = false; } catch { /* ignore */ }
            try { if (_optionsWindow is not null) _optionsWindow.Topmost = false; } catch { /* ignore */ }
            DetachMainWindowShellStyleHook();
            _isShuttingDown = true;
            try { _persistTimer.Stop(); } catch { /* ignore */ }
            try { _refreshTimer?.Stop(); } catch { /* ignore */ }
            try { _mediaHotkeys?.Dispose(); } catch { /* ignore */ }
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        var trayHwnd = _trayMessageHwnd != IntPtr.Zero ? _trayMessageHwnd : hwnd;
                        RemoveTrayIconNative(trayHwnd);
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
            try { _trayIcon?.Dispose(); } catch { /* ignore */ }
            _trayIcon = null;
            try { _trayMessageSource?.Dispose(); } catch { /* ignore */ }
            _trayMessageSource = null;
            _trayMessageHwnd = IntPtr.Zero;
            try { _hardcodetTrayIcon?.Dispose(); } catch { /* ignore */ }
            _hardcodetTrayIcon = null;
            CapturePlaylistWindowBounds();
            CaptureOptionsWindowBounds();
            // Capture these early in case playback stops during shutdown.
            var pos = 0.0;
            var wasPlaying = false;
            try { pos = _engine.CurrentPositionSeconds; } catch { /* ignore */ }
            try { wasPlaying = _engine.IsPlaying; } catch { /* ignore */ }
            SaveSettingsSnapshot(overridePositionSeconds: pos, overrideWasPlaying: wasPlaying);
            // Persist timer is stopped above — write the last playlist snapshot synchronously so it matches
            // the session we just saved in AppSettings (YouTube vs local).
            try { SaveLastPlaylistSnapshotBestEffort(); } catch { /* ignore */ }
            _snapshotDirty = false;
        };

        // Tray icon needs a real HWND; apply icon visibility once it exists.
        SourceInitialized += (_, _) =>
        {
            try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }
        };

    }
    private async Task EnrichLocalNowPlayingAsync(PlaylistEntry? entry)
    {
        try
        {
            if (entry is null)
                return;

            if (!LocalPlaylistLoader.TryGetLocalPath(entry.WebpageUrl, out var localPath))
                return;

            var info = await LocalMetadataService.TryGetInfoAsync(localPath, CancellationToken.None);
            if (info is null)
                return;

            // If the user already moved to another track, ignore.
            if (_nowPlayingEntry is null || !string.Equals(_nowPlayingEntry.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            var title = string.IsNullOrWhiteSpace(info.Title) ? null : info.Title!.Trim();
            var artist = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist!.Trim();
            var dur = info.DurationSeconds is > 0 ? info.DurationSeconds : null;

            // Make metadata "stick" by updating the underlying entries + queue item.
            Dispatcher.Invoke(() =>
            {
                ApplyMetadataToEntries(entry.VideoId, title, artist, dur);
                UpdateDurationUi(dur ?? _engine.CurrentDurationSeconds);
                UpdateNowPlayingText();

            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Bound in-memory duration probe cache so long sessions do not grow without limit.</summary>
    private void TrimYoutubeDurationCacheIfNeeded()
    {
        const int maxEntries = 256;
        if (_youtubeDurationByVideoId.Count <= maxEntries)
            return;
        try
        {
            var remove = _youtubeDurationByVideoId.Count - maxEntries / 2;
            foreach (var key in _youtubeDurationByVideoId.Keys.Take(remove).ToList())
                _youtubeDurationByVideoId.Remove(key);
        }
        catch { /* ignore */ }
    }

    private async Task EnrichYoutubeDurationNowPlayingAsync(PlaylistEntry? entry)
    {
        try
        {
            if (entry is null)
                return;
            if (entry.VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                return;
            if (entry.VideoId.StartsWith("stream:", StringComparison.OrdinalIgnoreCase))
                return;

            // If we already have duration, nothing to do.
            if (entry.DurationSeconds is int d0 && d0 > 0)
                return;

            if (_youtubeDurationByVideoId.TryGetValue(entry.VideoId, out var cached) && cached > 0)
            {
                ApplyMetadataToEntries(entry.VideoId, title: null, artist: null, durationSeconds: cached);
                try { _engine.OverrideCurrentDurationSeconds(cached); } catch { /* ignore */ }
                Dispatcher.Invoke(() =>
                {
                    try { UpdateDurationUi(cached); } catch { /* ignore */ }
                });
                return;
            }

            var req = Interlocked.Increment(ref _youtubeDurationRequestId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var dur = await _ytDlp.TryGetDurationSecondsAsync(entry.WebpageUrl, cts.Token);
            if (dur is not int d || d <= 0)
            {
                // Cache negative result to avoid repeated probing on the same video during this run.
                _youtubeDurationByVideoId[entry.VideoId] = -1;
                TrimYoutubeDurationCacheIfNeeded();
                AppLog.Info($"Duration probe: missing for {entry.VideoId}", AppLogInfoTier.Crucial);
                return;
            }

            // If now-playing changed while we awaited, drop result.
            if (req != _youtubeDurationRequestId)
                return;
            if (_engine.GetCurrent() is not PlaylistEntry cur || !string.Equals(cur.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            _youtubeDurationByVideoId[entry.VideoId] = d;
            TrimYoutubeDurationCacheIfNeeded();
            ApplyMetadataToEntries(entry.VideoId, title: null, artist: null, durationSeconds: d);
            try { _engine.OverrideCurrentDurationSeconds(d); } catch { /* ignore */ }

            Dispatcher.Invoke(() =>
            {
                try { UpdateDurationUi(d); } catch { /* ignore */ }
            });
        }
        catch
        {
            // ignore (best-effort enrichment)
        }
    }

    private void SetNowPlayingTitleText(string title)
    {
        try
        {
            if (NowPlayingTitleRun is not null)
                NowPlayingTitleRun.Text = title;
            else
                NowPlayingTextBlock.Text = $"[{_nowPlayingStatus.Trim().ToUpperInvariant()}] {title}";
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyMetadataToEntries(string videoId, string? title, string? artist, int? durationSeconds)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            PlaylistEntry? updatedEntry = null;

            for (var i = 0; i < _playlistCore.Entries.Count; i++)
            {
                if (!string.Equals(_playlistCore.Entries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var e = _playlistCore.Entries[i];
                var newE = e with
                {
                    Title = string.IsNullOrWhiteSpace(title) ? e.Title : title!,
                    Channel = string.IsNullOrWhiteSpace(artist) ? e.Channel : artist,
                    DurationSeconds = durationSeconds ?? e.DurationSeconds
                };
                _playlistCore.Entries[i] = newE;
                updatedEntry = newE;
                break;
            }

            if (_playlistCore.Entries is List<PlaylistEntry> curList)
            {
                for (var i = 0; i < curList.Count; i++)
                {
                    if (!string.Equals(curList[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var e = curList[i];
                    var newE = e with
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? e.Title : title!,
                        Channel = string.IsNullOrWhiteSpace(artist) ? e.Channel : artist,
                        DurationSeconds = durationSeconds ?? e.DurationSeconds
                    };
                    curList[i] = newE;
                    updatedEntry ??= newE;
                    break;
                }
            }

            if (updatedEntry is null)
                return;

            if (_engine.GetCurrent() is { } cur && string.Equals(cur.VideoId, videoId, StringComparison.OrdinalIgnoreCase))
            {
                if (updatedEntry.DurationSeconds is int d && d > 0)
                {
                    try { _engine.OverrideCurrentDurationSeconds(d); } catch { /* ignore */ }
                }
                _nowPlayingEntry = updatedEntry;

                try
                {
                    // If the title/artist was enriched, ensure the "Current song" window title updates immediately.
                    ApplyMainWindowTitleFromSettings(GetAppTitleBase());
                }
                catch { /* ignore */ }
            }

            // Update playlist items by VideoId
            for (var i = 0; i < _playlistItems.Count; i++)
            {
                if (_playlistItems[i].IsQueued) continue;
                if (!string.Equals(_playlistItems[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var orig = _playlistItems[i];
                var updatedEntry2 = orig.Entry! with
                {
                    Title = string.IsNullOrWhiteSpace(title) ?
                    orig.Entry.Title : title!,
                    Channel = string.IsNullOrWhiteSpace(artist) ? orig.Entry.Channel : artist,
                    DurationSeconds = durationSeconds
                };
                orig.UpdateEntry(updatedEntry2);
            }

            // Update queue items by VideoId
            for (var i = 0; i < _queueItems.Count; i++)
            {
                if (!_queueItems[i].IsQueued) continue;
                if (!string.Equals(_queueItems[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var orig = _queueItems[i];
                var updatedEntry2 = orig.Entry! with
                {
                    Title = string.IsNullOrWhiteSpace(title) ?
                    orig.Entry.Title : title!,
                    Channel = string.IsNullOrWhiteSpace(artist) ? orig.Entry.Channel : artist,
                    DurationSeconds = durationSeconds
                };
                orig.UpdateEntry(updatedEntry2);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SetGlobalMediaKeysEnabled(bool enabled)
    {
        _globalMediaKeysEnabled = enabled;
        UpdateGlobalMediaKeysRegistration();
        RequestPersistSnapshot();
    }

    private void UpdateGlobalMediaKeysRegistration()
    {
        try
        {
            if (!_globalMediaKeysEnabled)
            {
                try { _mediaHotkeys?.Dispose(); } catch { /* ignore */ }
                _mediaHotkeys = null;
                return;
            }

            if (_mediaHotkeys is not null)
                return;

            InitializeGlobalMediaKeys();
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyMainWindowCompactMode()
    {
        var leavingCompact = (_lastAppliedMainCompactForUserDefined ?? _mainWindowCompact) && !_mainWindowCompact;
        var deferFullLeaveCompactUserDefinedBg =
            leavingCompact && ShouldApplyUserDefinedBackgroundForMain();

        try { _userDefinedMainBgDebounceTimer?.Stop(); } catch { /* ignore */ }

        if (_mainWindowCompact && _pendingTryRestoreAfterLeaveCompactUserDefinedBg)
            _pendingTryRestoreAfterLeaveCompactUserDefinedBg = false;

        try
        {
            // Apply Default UserDefined brushes before expanding chrome / SizeToContent so the first expanded
            // layout pass does not paint Compact/Ultra Viewbox at Default aspect (zoom / wrong crop flash).
            if (deferFullLeaveCompactUserDefinedBg)
            {
                try
                {
                    ApplyBackgroundFromSettings();
                    ApplyBackgroundColorsFromSettings();
                }
                catch { /* ignore */ }
                try
                {
                    if (MainWindowImageBackgroundBorder is not null
                        && System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] is System.Windows.Media.Brush b)
                        MainWindowImageBackgroundBorder.Background = b;
                }
                catch { /* ignore */ }
            }

            var ultra = _mainWindowCompact && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase);
            var fullChromeVis = _mainWindowCompact ? Visibility.Collapsed : Visibility.Visible;
            MainToolsRowGrid.Visibility = fullChromeVis;
            PlaylistTitleTextBlock.Visibility = fullChromeVis;
            ShuffleToggle.Visibility = fullChromeVis;
            RepeatButton.Visibility = fullChromeVis;
            try { CompactShuffleToggleButton.Visibility = (_mainWindowCompact && !ultra) ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactRepeatButton.Visibility = (_mainWindowCompact && !ultra) ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactLyricsButton.Visibility = (_mainWindowCompact && !ultra) ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactPlaylistButton.Visibility = (_mainWindowCompact && !ultra) ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactExportMp3Button.Visibility = (_mainWindowCompact && !ultra) ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactPlaylistButtonInline.Visibility = ultra ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { LyricsButton.Visibility = ultra ? Visibility.Collapsed : Visibility.Visible; } catch { /* ignore */ }
            try { LyricsUltraButtonInline.Visibility = ultra ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { ExportMp3UltraButtonInline.Visibility = ultra ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { ExportMp3Button.Visibility = _mainWindowCompact ? Visibility.Collapsed : Visibility.Visible; } catch { /* ignore */ }

            try { UpdateExportMp3ControlsEnabled(); } catch { /* ignore */ }

            ChromeCompactLayoutButton.Content = _mainWindowCompact ? "[+]" : "[-]";

            ApplyMainWindowCompactLayoutDensity();
            _pendingTryRestoreAfterLeaveCompactUserDefinedBg = deferFullLeaveCompactUserDefinedBg;
            ApplyCompactAuxiliaryWindowState();

            // Saved window bounds may set an explicit Height; clear so SizeToContent can follow the card.
            SizeToContent = SizeToContent.Height;
            ClearValue(FrameworkElement.HeightProperty);

            _lastAppliedMainCompactForUserDefined = _mainWindowCompact;
        }
        catch
        {
            // ignore
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_visualizerMode == VisualizerMode.Spectrum)
                    UpdateVisualizerUi();
            }
            catch
            {
                // ignore
            }

            try { UpdateMeasuredBackgroundAspects(); } catch { /* ignore */ }

            if (_pendingTryRestoreAfterLeaveCompactUserDefinedBg)
            {
                _pendingTryRestoreAfterLeaveCompactUserDefinedBg = false;
                try { TryRestoreAuxiliaryWindowsAfterCompact(); } catch { /* ignore */ }
            }

            try
            {
                // SyncOptionsWindowToMain removed; WindowSnapService handles interactive latching + cluster move.
            }
            catch
            {
                // ignore
            }

            if (deferFullLeaveCompactUserDefinedBg)
            {
                try { _userDefinedMainBgDebounceTimer?.Stop(); } catch { /* ignore */ }
                try { UpdateLayout(); } catch { /* ignore */ }
                try { RefreshBackgroundIfUserDefinedCrop(); } catch { /* ignore */ }
                try
                {
                    if (MainWindowImageBackgroundBorder is not null)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                MainWindowImageBackgroundBorder.SetResourceReference(
                                    System.Windows.Controls.Border.BackgroundProperty,
                                    "App.Brush.WindowBgImage.Main");
                                MainWindowImageBackgroundBorder.InvalidateVisual();
                            }
                            catch { /* ignore */ }
                        }), DispatcherPriority.Render);
                    }
                }
                catch { /* ignore */ }
            }

            // After aux-window sync + layout, re-apply caption rules (Ultra uses current track; expanded respects App title mode).
            try { ApplyMainWindowTitleFromSettings(GetAppTitleBase()); } catch { /* ignore */ }
        }), DispatcherPriority.ContextIdle);

        // Compact/Ultra changes height via SizeToContent; ensure bottom-snapped aux windows follow the *final* height.
        try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }

        // UserDefined wallpaper uses different crops for Default/Compact/Ultra; updating Viewbox immediately
        // can happen before SizeToContent commits the new height, causing a visible 2-step. Defer to render.
        if (!deferFullLeaveCompactUserDefinedBg)
        {
            try
            {
                // Also refresh after the next SizeChanged settles. When snapped aux windows are opening/syncing,
                // the main SizeChanged handler can run after our Render callback; this ensures we always apply the
                // final crop once the resize + aux sync have completed.
                _userDefinedMainCropRefreshAfterSizePending = true;
                ScheduleUserDefinedMainCropRefreshAfterLayout();
            }
            catch { /* ignore */ }

            // Final safety net: update crop right before rendering a couple frames.
            // When aux windows are opening/syncing, dispatcher ordering can still allow one frame of the "old crop"
            // to paint at the new size. Rendering runs once per frame immediately before draw.
            try
            {
                if (ShouldApplyUserDefinedBackgroundForMain())
                {
                    var anyAux = (_playlistWindow is not null) || (_optionsWindow is not null) || (_logWindow is not null);
                    // Fewer forced frames when aux windows are open: each frame mutates shared App brushes and can amplify jerk.
                    _userDefinedMainCropForceRenderFrames = anyAux ? 1 : 3;
                    HookUserDefinedMainCropRenderLoopIfNeeded();
                }
            }
            catch { /* ignore */ }
        }
        else
        {
            _userDefinedMainCropForceRenderFrames = 0;
            try { UnhookUserDefinedMainCropRenderLoop(); } catch { /* ignore */ }
        }

        // Main window caption/taskbar text depends on compact + Ultra vs Options "App title" mode.
        // This must run on compact toggles (not only when the compact layout radio changes), otherwise leaving
        // Ultra/compact can leave Title stuck on the last track string even when AppTitleMode is Default/Custom.
        try { ApplyMainWindowTitleFromSettings(GetAppTitleBase()); } catch { /* ignore */ }
    }

    private void HookUserDefinedMainCropRenderLoopIfNeeded()
    {
        if (_userDefinedMainCropRenderHooked)
            return;
        _userDefinedMainCropRenderHooked = true;
        System.Windows.Media.CompositionTarget.Rendering += CompositionTarget_Rendering_UserDefinedMainCrop;
    }

    private void UnhookUserDefinedMainCropRenderLoop()
    {
        if (!_userDefinedMainCropRenderHooked)
            return;
        _userDefinedMainCropRenderHooked = false;
        try { System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering_UserDefinedMainCrop; } catch { /* ignore */ }
    }

    private void CompositionTarget_Rendering_UserDefinedMainCrop(object? sender, EventArgs e)
    {
        try
        {
            if (_userDefinedMainCropForceRenderFrames <= 0)
            {
                UnhookUserDefinedMainCropRenderLoop();
                return;
            }

            if (!ShouldApplyUserDefinedBackgroundForMain())
            {
                _userDefinedMainCropForceRenderFrames = 0;
                UnhookUserDefinedMainCropRenderLoop();
                return;
            }

            // Apply the in-place update (best effort). If it cannot run in-place, fall back once and stop.
            if (!TryUpdateUserDefinedMainCropInPlaceFast())
            {
                TryUpdateUserDefinedMainCropInPlace();
                _userDefinedMainCropForceRenderFrames = 0;
                UnhookUserDefinedMainCropRenderLoop();
                return;
            }

            _userDefinedMainCropForceRenderFrames--;
            if (_userDefinedMainCropForceRenderFrames <= 0)
                UnhookUserDefinedMainCropRenderLoop();
        }
        catch
        {
            _userDefinedMainCropForceRenderFrames = 0;
            UnhookUserDefinedMainCropRenderLoop();
        }
    }

    private void ScheduleUserDefinedMainCropRefreshAfterLayout()
    {
        if (!ShouldApplyUserDefinedBackgroundForMain())
            return;

        var anyAux = (_playlistWindow is not null) || (_optionsWindow is not null) || (_logWindow is not null);
        if (anyAux)
        {
            RequestDebouncedUserDefinedMainBackgroundCropRefresh();
            return;
        }

        _userDefinedMainCropRefreshGen++;
        var gen = _userDefinedMainCropRefreshGen;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (gen != _userDefinedMainCropRefreshGen)
                    return;
                if (!ShouldApplyUserDefinedBackgroundForMain())
                    return;

                // Commit the new SizeToContent pass, then update Viewbox in-place once.
                try { UpdateLayout(); } catch { /* ignore */ }
                try { RefreshBackgroundIfUserDefinedCrop(); } catch { /* ignore */ }
            }
            catch { /* ignore */ }
        }), DispatcherPriority.Render);
    }

    private void RequestDebouncedUserDefinedMainBackgroundCropRefresh()
    {
        try
        {
            if (!ShouldApplyUserDefinedBackgroundForMain())
                return;

            if (_userDefinedMainBgDebounceTimer is null)
            {
                _userDefinedMainBgDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _userDefinedMainBgDebounceTimer.Tick += (_, _) =>
                {
                    try { _userDefinedMainBgDebounceTimer!.Stop(); } catch { /* ignore */ }
                    if (!ShouldApplyUserDefinedBackgroundForMain())
                        return;
                    try { UpdateLayout(); } catch { /* ignore */ }
                    try { RefreshBackgroundIfUserDefinedCrop(); } catch { /* ignore */ }
                };
            }

            // Restart the debounce window on every burst event (resize/sync storms).
            try { _userDefinedMainBgDebounceTimer.Stop(); } catch { /* ignore */ }
            try { _userDefinedMainBgDebounceTimer.Start(); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Keeps Default/Compact/Ultra aspect hints in sync. Historically only the <i>current</i> layout was measured,
    /// leaving Compact/Ultra at 0 in Default mode — the designer then fell back to hard-coded 600×140 / 600×110,
    /// which mismatched the real window and produced vertically squashed crops. Unknown aspects are derived from
    /// the live client size using typical height ratios between the three layouts (same fixed width as Main).
    /// </summary>
    private void FillDerivedMainAspectHints(double w, double h)
    {
        static double SafeAspect(double ww, double hh)
        {
            if (ww <= 1 || hh <= 1) return 0;
            return ww / hh;
        }

        // Prefer the DIP size of the element that actually displays the image (not always identical to Window bounds).
        try
        {
            if (MainWindowImageBackgroundBorder is { } host && host.ActualWidth > 1 && host.ActualHeight > 1)
            {
                w = host.ActualWidth;
                h = host.ActualHeight;
            }
        }
        catch { /* ignore */ }

        // Reference height ratios for 600px-wide SizeToContent layouts (see Background designer fallbacks).
        const double refHDefault = 260.0;
        const double refHCompact = 140.0;
        const double refHUltra = 110.0;

        var cur = SafeAspect(w, h);
        if (cur <= 0)
            return;

        if (!_mainWindowCompact)
        {
            _measuredMainDefaultAspect = cur;
            _measuredMainCompactAspect = SafeAspect(w, h * (refHCompact / refHDefault));
            _measuredMainUltraAspect = SafeAspect(w, h * (refHUltra / refHDefault));
            return;
        }

        if (string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase))
        {
            _measuredMainUltraAspect = cur;
            _measuredMainDefaultAspect = SafeAspect(w, h * (refHDefault / refHUltra));
            _measuredMainCompactAspect = SafeAspect(w, h * (refHCompact / refHUltra));
        }
        else
        {
            _measuredMainCompactAspect = cur;
            _measuredMainDefaultAspect = SafeAspect(w, h * (refHDefault / refHCompact));
            _measuredMainUltraAspect = SafeAspect(w, h * (refHUltra / refHCompact));
        }
    }

    private void UpdateMeasuredBackgroundAspects() =>
        FillDerivedMainAspectHints(ActualWidth, ActualHeight);

    private void ApplyCompactAuxiliaryWindowState()
    {
        if (_mainWindowCompact)
        {
            if (_compactModeHidesAuxWindows)
            {
                _playlistWindowWasOpenBeforeCompact = (_playlistWindow is not null && _playlistWindow.IsVisible) || _playlistWindowWasOpenBeforeCompact;
                _optionsWindowWasOpenBeforeCompact = (_optionsWindow is not null && _optionsWindow.IsVisible) || _optionsWindowWasOpenBeforeCompact;
                _lyricsWindowWasOpenBeforeCompact = (_lyricsWindow is not null && _lyricsWindow.IsVisible) || _lyricsWindowWasOpenBeforeCompact;
                CloseAuxiliaryWindowsForCompact();
            }
        }
        else
        {
            if (!_pendingTryRestoreAfterLeaveCompactUserDefinedBg)
                TryRestoreAuxiliaryWindowsAfterCompact();
        }
    }

    private void CloseAuxiliaryWindowsForCompact()
    {
        try
        {
            if (_optionsWindow is not null)
            {
                try { WindowCoordinator.CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState); } catch { /* ignore */ }
                try { _optionsWindow.Hide(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_playlistWindow is not null && !_compactUserOpenedPlaylistWindow)
            {
                try { WindowCoordinator.CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState); } catch { /* ignore */ }
                try { _playlistWindow.Hide(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_lyricsWindow is not null && !_compactUserOpenedLyricsWindow)
            {
                try { WindowCoordinator.CaptureWindowBounds(_lyricsWindow, out var _unusedLyricsBounds, out var _unusedLyricsState); } catch { /* ignore */ }
                try { _lyricsWindow.Hide(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private void ApplyMainWindowCompactModeLayout()
    {
        // Layout variants affect both density and visibility (PL button, info stack), so re-apply the compact mode.
        ApplyMainWindowCompactMode();
    }

    private void TryRestoreAuxiliaryWindowsAfterCompact()
    {
        try
        {
            if (_playlistWindowWasOpenBeforeCompact)
            {
                EnsurePlaylistWindowOpen();
                _playlistWindowWasOpenBeforeCompact = false;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_optionsWindowWasOpenBeforeCompact)
            {
                EnsureOptionsWindowOpen();
                _optionsWindowWasOpenBeforeCompact = false;
            }

            if (_lyricsWindowWasOpenBeforeCompact)
            {
                EnsureLyricsWindowOpen();
                _lyricsWindowWasOpenBeforeCompact = false;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// In compact mode, park <see cref="VisualizerBorder"/> in the middle column of <see cref="TransportVolumeRowGrid"/>
    /// (between transport buttons and volume). Expanded mode keeps it in row 0 above now-playing.
    /// </summary>
    private void ApplyVisualizerPlacementForCompact(bool compact)
    {
        try
        {
            if (PlaybackCardInnerGrid is null || TransportVolumeRowGrid is null || VisualizerBorder is null)
                return;

            if (compact)
            {
                // Compact: transport uses natural width; middle column (*); volume Auto — avoids starving the visualizer.
                TransportVolumeRowGrid.ColumnDefinitions[0].Width = GridLength.Auto;

                var needReparent = VisualizerBorder.Parent != TransportVolumeRowGrid || Grid.GetColumn(VisualizerBorder) != 1;
                if (needReparent)
                {
                    if (VisualizerBorder.Parent is System.Windows.Controls.Panel p)
                        p.Children.Remove(VisualizerBorder);
                    Grid.SetRow(VisualizerBorder, 0);
                    Grid.SetColumn(VisualizerBorder, 1);
                    Grid.SetColumnSpan(VisualizerBorder, 1);
                    TransportVolumeRowGrid.Children.Add(VisualizerBorder);
                }

                // Stretch so the spectrum bottom edge aligns with adjacent transport controls.
                VisualizerBorder.VerticalAlignment = VerticalAlignment.Stretch;
                PlaybackCardInnerGrid.RowDefinitions[0].Height = new GridLength(0);
                PlaybackCardInnerGrid.RowDefinitions[0].MinHeight = 0;
                TransportVolumeRowGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                // Expanded: keep Auto + spacer + Auto (same as XAML) to avoid subtle clipping of the last button.
                TransportVolumeRowGrid.ColumnDefinitions[0].Width = GridLength.Auto;
                TransportVolumeRowGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                var needReparent = VisualizerBorder.Parent != PlaybackCardInnerGrid || Grid.GetRow(VisualizerBorder) != 0;
                if (needReparent)
                {
                    if (VisualizerBorder.Parent is System.Windows.Controls.Panel p)
                        p.Children.Remove(VisualizerBorder);
                    Grid.SetRow(VisualizerBorder, 0);
                    Grid.SetColumn(VisualizerBorder, 0);
                    Grid.SetColumnSpan(VisualizerBorder, 1);
                    PlaybackCardInnerGrid.Children.Insert(0, VisualizerBorder);
                }

                try { VisualizerBorder.ClearValue(FrameworkElement.VerticalAlignmentProperty); } catch { /* ignore */ }
                PlaybackCardInnerGrid.RowDefinitions[0].Height = GridLength.Auto;
                PlaybackCardInnerGrid.RowDefinitions[0].MinHeight = 0;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Compact mode: VU/spectrum sits between transport and volume; tight margins; hide hairline dividers; modestly smaller type.
    /// Avoids non-uniform scale transforms so text is not vertically squashed.
    /// </summary>
    private void ApplyMainWindowCompactLayoutDensity()
    {
        var c = _mainWindowCompact;
        var ultra = c && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase);

        try
        {
            MainPlaybackCardOuterGrid.Margin = c ? new Thickness(0) : new Thickness(12, 0, 12, 8);
        }
        catch
        {
            // ignore
        }

        try
        {
            if (MainChromeBodyWrapGrid is not null)
            {
                if (c)
                    MainChromeBodyWrapGrid.Margin = new Thickness(0);
                else
                    MainChromeBodyWrapGrid.SetResourceReference(FrameworkElement.MarginProperty, "App.Theme.WindowChromeBodyInsetMargin");
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (MainPlaybackCardBorder is not null)
                MainPlaybackCardBorder.CornerRadius = c ? new CornerRadius(0) : new CornerRadius(6);
        }
        catch
        {
            // ignore
        }

        try
        {
            ApplyVisualizerPlacementForCompact(c);
            if (c)
            {
                if (ultra)
                {
                    // Ultra compact is a single-row control strip; keep the visualizer tight.
                    VisualizerBorder.Padding = new Thickness(4, 0, 4, 0);
                    VisualizerHostGrid.Height = 24.0;
                    if (SpectrumPanelChrome is not null)
                        SpectrumPanelChrome.Height = 24.0;
                }
                else
                {
                    VisualizerBorder.Padding = new Thickness(4, 0, 4, 0);
                    VisualizerHostGrid.Height = 24.0;
                    if (SpectrumPanelChrome is not null)
                        SpectrumPanelChrome.Height = 24.0;
                }
            }
            else
            {
                VisualizerBorder.Padding = new Thickness(10, 8, 10, 0);
                VisualizerHostGrid.Height = 26.0;
                if (SpectrumPanelChrome is not null)
                    SpectrumPanelChrome.Height = 26.0;
                VuLeftBar.ClearValue(FrameworkElement.HeightProperty);
                VuRightBar.ClearValue(FrameworkElement.HeightProperty);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var divVis = c ? Visibility.Collapsed : Visibility.Visible;
            CardDividerAboveInfoBorder.Visibility = divVis;
            CardDividerAboveTransportBorder.Visibility = divVis;
        }
        catch
        {
            // ignore
        }

        try
        {
            if (PlaybackCardInnerGrid is not null)
            {
                if (ultra)
                {
                    MainPlaybackInfoStack.Visibility = Visibility.Collapsed;
                    PlaybackCardInnerGrid.RowDefinitions[2].Height = new GridLength(0);
                    PlaybackCardInnerGrid.RowDefinitions[3].Height = new GridLength(0);
                }
                else
                {
                    MainPlaybackInfoStack.Visibility = Visibility.Visible;
                    PlaybackCardInnerGrid.RowDefinitions[2].Height = GridLength.Auto;
                    PlaybackCardInnerGrid.RowDefinitions[3].Height = GridLength.Auto;
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            MainPlaybackInfoStack.Margin = c ? new Thickness(4, 3, 4, 2) : new Thickness(10, 8, 10, 8);
            SeekTimeRowGrid.Margin = c ? new Thickness(0, 2, 0, 0) : new Thickness(0, 8, 0, 0);
            TransportVolumeRowGrid.Margin = c ? new Thickness(2, 2, 2, 2) : new Thickness(10, 8, 10, 10);
            VolumeLabelTextBlock.Margin = c ? new Thickness(4, 0, 3, 0) : new Thickness(8, 0, 6, 0);
        }
        catch
        {
            // ignore
        }

        try
        {
            NowPlayingTextBlock.FontSize = c ? 12.0 : 14.0;
            if (c)
            {
                ElapsedTextBlock.FontSize = 10.0;
                DurationTextBlock.FontSize = 10.0;
                VolumeLabelTextBlock.FontSize = 10.0;
            }
            else
            {
                ElapsedTextBlock.ClearValue(TextBlock.FontSizeProperty);
                DurationTextBlock.ClearValue(TextBlock.FontSizeProperty);
                VolumeLabelTextBlock.ClearValue(TextBlock.FontSizeProperty);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            // Use style default (App.Slider.Seek MinHeight 18) so the scrub bar is not vertically squashed.
            SeekSlider.ClearValue(FrameworkElement.MinHeightProperty);
        }
        catch
        {
            // ignore
        }

        try
        {
            VolumeSlider.Width = c ? 64.0 : 120.0;
        }
        catch
        {
            // ignore
        }

        try
        {
            var mw = c ? 30.0 : 44.0;
            PrevButton.MinWidth = mw;
            PlayPauseButton.MinWidth = mw;
            NextButton.MinWidth = mw;
        }
        catch
        {
            // ignore
        }
    }

    private void ChromeCompactLayoutButton_OnClick(object sender, RoutedEventArgs e) => ToggleMainWindowCompactMode();

    private void ChromeAlwaysOnTopToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressAlwaysOnTopToggleEvents)
            return;
        SetAlwaysOnTop(true);
    }

    private void ChromeAlwaysOnTopToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressAlwaysOnTopToggleEvents)
            return;
        SetAlwaysOnTop(false);
    }

    private void SetAlwaysOnTop(bool enabled)
    {
        if (_alwaysOnTop == enabled)
            return;
        _alwaysOnTop = enabled;
        ApplyAlwaysOnTopFromSettings();
        RequestPersistSnapshot();
    }

    private void SetAlwaysOnTopPlaylistWindow(bool enabled)
    {
        _alwaysOnTopPlaylistWindow = enabled;
        ApplyAlwaysOnTopFromSettings();
        RequestPersistSnapshot();
    }

    private void SetAlwaysOnTopOptionsWindow(bool enabled)
    {
        _alwaysOnTopOptionsWindow = enabled;
        ApplyAlwaysOnTopFromSettings();
        RequestPersistSnapshot();
    }

    private void SetAlwaysOnTopLyricsWindow(bool enabled)
    {
        _alwaysOnTopLyricsWindow = enabled;
        ApplyAlwaysOnTopFromSettings();
        RequestPersistSnapshot();
    }

    private void ToggleMainWindowCompactMode()
    {
        var wasCompact = _mainWindowCompact;
        Rect pre;
        try { pre = GetOuterBounds(this); }
        catch { pre = new Rect(Left, Top, Width, Height); }

        _mainWindowCompact = !_mainWindowCompact;
        ApplyMainWindowCompactMode();

        // Window height is SizeToContent="Height" so it changes after layout; adjust position after the
        // new height is realized.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                UpdateLayout();

                var works = GetAllWorkAreasDips(this);
                Rect PickWorkArea(Rect bounds)
                {
                    var cx = bounds.Left + bounds.Width / 2.0;
                    var cy = bounds.Top + bounds.Height / 2.0;
                    foreach (var w in works)
                        if (w.Contains(new System.Windows.Point(cx, cy)))
                            return w;
                    // Fallback: nearest by center distance.
                    return works
                        .OrderBy(w =>
                        {
                            var wx = w.Left + w.Width / 2.0;
                            var wy = w.Top + w.Height / 2.0;
                            var dx = wx - cx;
                            var dy = wy - cy;
                            return dx * dx + dy * dy;
                        })
                        .FirstOrDefault();
                }

                var postW = ActualWidth > 0 ? ActualWidth : Width;
                var postH = ActualHeight > 0 ? ActualHeight : Height;
                if (postW <= 1) postW = Width;
                if (postH <= 1) postH = Height;

                if (wasCompact && !_mainWindowCompact)
                {
                    // Leaving compact: if the expanded height would push the window off-screen, expand upward
                    // (keep the bottom edge stable as much as possible). Default behavior is to expand downward.
                    _mainWindowCompactBoundsBeforeExpand = pre;
                    _mainWindowExpandedBoundsAfterExpand = null;
                    _mainWindowExpandedMovedSinceExpand = false;

                    var work = PickWorkArea(pre);
                    // Default: keep the same Top, so the window grows downward.
                    var desiredTopDown = pre.Top;
                    var desiredBottomDown = desiredTopDown + postH;

                    double newTop;
                    if (desiredBottomDown <= work.Bottom + 1e-6)
                    {
                        // Fits: expand downwards.
                        newTop = Math.Clamp(desiredTopDown, work.Top, work.Bottom - postH);
                    }
                    else
                    {
                        // Would go off-screen: expand upward by anchoring the bottom edge.
                        var anchorBottom = Math.Min(pre.Bottom, work.Bottom);
                        var desiredTopUp = anchorBottom - postH;
                        newTop = Math.Clamp(desiredTopUp, work.Top, work.Bottom - postH);
                    }
                    var newLeft = Math.Clamp(pre.Left, work.Left, work.Right - postW);
                    Top = SnapRound(newTop);
                    Left = SnapRound(newLeft);

                    try { _mainWindowExpandedBoundsAfterExpand = GetOuterBounds(this); } catch { /* ignore */ }
                }
                else if (!wasCompact && _mainWindowCompact)
                {
                    // Returning to compact: restore the original compact position only if the user did not move
                    // the expanded window since the last expand.
                    var canRestore = !_mainWindowExpandedMovedSinceExpand && _mainWindowCompactBoundsBeforeExpand is { } compactBounds;
                    if (canRestore)
                    {
                        var work = PickWorkArea(compactBounds);
                        var newTop = Math.Clamp(compactBounds.Top, work.Top, work.Bottom - postH);
                        var newLeft = Math.Clamp(compactBounds.Left, work.Left, work.Right - postW);
                        Top = SnapRound(newTop);
                        Left = SnapRound(newLeft);
                    }

                    _mainWindowExpandedBoundsAfterExpand = null;
                    _mainWindowExpandedMovedSinceExpand = false;
                }
            }
            catch { /* ignore */ }
        }), DispatcherPriority.Loaded);

        RequestPersistSnapshot();
    }

    private void SetCompactModeHidesAuxWindows(bool enabled)
    {
        if (_compactModeHidesAuxWindows == enabled)
            return;
        _compactModeHidesAuxWindows = enabled;

        // If compact is currently active, apply the new policy immediately (hide = close).
        try
        {
            if (_mainWindowCompact && _compactModeHidesAuxWindows)
                CloseAuxiliaryWindowsForCompact();
        }
        catch { /* ignore */ }

        RequestPersistSnapshot();
    }

    private void SetCompactModeLayout(string layout)
    {
        var norm = SettingsStore.NormalizeCompactModeLayout(layout);
        if (string.Equals(_compactModeLayout, norm, StringComparison.OrdinalIgnoreCase))
            return;
        _compactModeLayout = norm;

        // If compact is active, apply immediately (layout changes are handled later in the compact UI work).
        try { ApplyMainWindowCompactModeLayout(); } catch { /* ignore */ }

        RequestPersistSnapshot();
    }

    private bool ShouldApplyUserDefinedBackgroundForMain()
    {
        try
        {
            if (!string.Equals(SettingsStore.NormalizeBackgroundImageStretch(_backgroundImageStretch), "UserDefined", StringComparison.OrdinalIgnoreCase))
                return false;
            var mode = (_backgroundMode ?? "Default").Trim();
            if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshBackgroundIfUserDefinedCrop()
    {
        try
        {
            if (!ShouldApplyUserDefinedBackgroundForMain())
                return;

            // Update the existing Main brushes in-place (no resource swap, no opacity tricks).
            if (TryUpdateUserDefinedMainCropInPlaceFast())
                return;

            // Fallback: rebuild if resources aren't in the expected shape (older states).
            TryUpdateUserDefinedMainCropInPlace();
        }
        catch { /* ignore */ }
    }

    private bool TryUpdateUserDefinedMainCropInPlaceFast()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
                return false;

            var rect = GetUserDefinedRectForTarget("Main") ?? RectN.Full;
            rect = SettingsStore.NormalizeRectN(rect);

            if (System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] is not System.Windows.Media.ImageBrush raw
                || raw.ImageSource is not System.Windows.Media.Imaging.BitmapSource rawSrc)
                return false;

            // If the designer / settings changed the underlying image, in-place Viewbox updates alone will keep
            // painting the old bitmap until something rebuilds the brushes. Detect mismatch and re-seed.
            var curFp = TryComputeLiveUserDefinedBackgroundFingerprintFromBitmap(rawSrc);
            var expectedFp = TryComputeExpectedUserDefinedBackgroundFingerprintWithoutDecode();
            if (!string.IsNullOrEmpty(expectedFp) &&
                !string.IsNullOrEmpty(curFp) &&
                !string.Equals(expectedFp, curFp, StringComparison.Ordinal) &&
                TryLoadUserDefinedBackgroundBitmapSourceFromSettings(out var expectedSrc) &&
                expectedSrc is not null)
            {
                var newRaw = new System.Windows.Media.ImageBrush(expectedSrc);
                ApplyBackgroundImageUserDefinedToBrush(newRaw, expectedSrc, rect);
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] = newRaw;
                raw = newRaw;
                rawSrc = expectedSrc;
                _appliedUserDefinedBackgroundFingerprint = TryComputeBackgroundBitmapFingerprint(expectedSrc);

                var newMain = new System.Windows.Media.ImageBrush(expectedSrc);
                ApplyBackgroundImageUserDefinedToBrush(newMain, expectedSrc, rect);
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] =
                    ApplyScrimIfNeeded(newMain, scrimPercent: _backgroundScrimPercent);
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] =
                    System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"];
                return true;
            }

            // If a previous non-UserDefined apply left these frozen, re-seed mutable brushes so compact toggles
            // can update Viewbox immediately (otherwise we fall back to a later rebuild and the crop looks "late").
            if (raw.IsFrozen)
            {
                var newRaw = new System.Windows.Media.ImageBrush(rawSrc);
                ApplyBackgroundImageUserDefinedToBrush(newRaw, rawSrc, rect);
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] = newRaw;
                raw = newRaw;
            }

            raw.ViewboxUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
            raw.Viewbox = new System.Windows.Rect(rect.X, rect.Y, rect.W, rect.H);

            if (System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] is System.Windows.Media.ImageBrush mainIb)
            {
                if (mainIb.ImageSource is not System.Windows.Media.Imaging.BitmapSource src)
                    return false;

                if (mainIb.IsFrozen)
                {
                    var next = new System.Windows.Media.ImageBrush(src);
                    // Preserve the current mapping (should already be UserDefined), then override Viewbox below.
                    try
                    {
                        next.TileMode = mainIb.TileMode;
                        next.Viewport = mainIb.Viewport;
                        next.ViewportUnits = mainIb.ViewportUnits;
                        next.Stretch = mainIb.Stretch;
                        next.AlignmentX = mainIb.AlignmentX;
                        next.AlignmentY = mainIb.AlignmentY;
                        next.ViewboxUnits = mainIb.ViewboxUnits;
                        next.Viewbox = mainIb.Viewbox;
                    }
                    catch { /* ignore */ }
                    System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] = mainIb = next;
                    System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] = mainIb;
                }

                mainIb.ViewboxUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
                mainIb.Viewbox = new System.Windows.Rect(rect.X, rect.Y, rect.W, rect.H);
            }
            else if (System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] is System.Windows.Media.DrawingBrush db)
            {
                if (db.IsFrozen)
                {
                    // Rebuild a mutable scrim wrapper around the current raw brush.
                    var newMain = new System.Windows.Media.ImageBrush(rawSrc);
                    ApplyBackgroundImageUserDefinedToBrush(newMain, rawSrc, rect);
                    var rebuilt = ApplyScrimIfNeeded(newMain, scrimPercent: _backgroundScrimPercent);
                    System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] = rebuilt;
                    System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] = rebuilt;
                    db = rebuilt as System.Windows.Media.DrawingBrush ?? db;
                    if (db.IsFrozen) return false;
                }
                if (db.Drawing is not System.Windows.Media.DrawingGroup dg) return false;
                if (dg.IsFrozen) return false;

                // Update any ImageBrush inside the geometry drawings, and resize the content rect to match the cropped bitmap aspect.
                var src = rawSrc;

                var vb = raw.Viewbox;
                var imgW = Math.Max(1.0, (double)src.PixelWidth);
                var imgH = Math.Max(1.0, (double)src.PixelHeight);
                var physW = vb.Width * imgW;
                var physH = vb.Height * imgH;
                var cropAspect = physW / Math.Max(1e-9, physH);
                var contentRect = cropAspect >= 1.0
                    ? new System.Windows.Rect(0, 0, cropAspect, 1.0)
                    : new System.Windows.Rect(0, 0, 1.0, 1.0 / cropAspect);

                foreach (var child in dg.Children)
                {
                    if (child is not System.Windows.Media.GeometryDrawing gd)
                        continue;

                    if (gd.Geometry is System.Windows.Media.RectangleGeometry rg && !rg.IsFrozen)
                        rg.Rect = contentRect;

                    if (gd.Brush is System.Windows.Media.ImageBrush ib && !ib.IsFrozen)
                    {
                        ib.ViewboxUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
                        ib.Viewbox = new System.Windows.Rect(rect.X, rect.Y, rect.W, rect.H);
                    }
                }
            }
            else
            {
                return false;
            }

            // Legacy key tracks Main.
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] =
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"];

            // Nudge the live visual to pick up the updated brush immediately. When other windows are opening/syncing,
            // DynamicResource propagation can lag a frame; re-binding the same key avoids the "late flip".
            try
            {
                if (MainWindowImageBackgroundBorder is not null)
                {
                    MainWindowImageBackgroundBorder.SetResourceReference(
                        System.Windows.Controls.Border.BackgroundProperty,
                        "App.Brush.WindowBgImage.Main");
                    MainWindowImageBackgroundBorder.InvalidateVisual();
                }
            }
            catch { /* ignore */ }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryComputeBackgroundBitmapFingerprint(System.Windows.Media.Imaging.BitmapSource? src)
    {
        try
        {
            if (src is null)
                return null;

            // Prefer the original URI (custom file paths + pack URIs).
            if (src is System.Windows.Media.Imaging.BitmapImage bi && bi.UriSource is { } uri)
            {
                if (uri.IsFile)
                {
                    try
                    {
                        var p = uri.LocalPath;
                        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        {
                            var fp = TryComputeFileFingerprint(p);
                            if (!string.IsNullOrEmpty(fp))
                                return $"{fp}|px:{src.PixelWidth}x{src.PixelHeight}";

                            var fi = new FileInfo(p);
                            return $"file:{p}|lw:{fi.LastWriteTimeUtc.Ticks}|len:{fi.Length}|px:{src.PixelWidth}x{src.PixelHeight}";
                        }
                    }
                    catch { /* ignore */ }

                    return $"file:{Path.GetFullPath(uri.LocalPath)}|px:{src.PixelWidth}x{src.PixelHeight}";
                }

                return $"uri:{uri}|px:{src.PixelWidth}x{src.PixelHeight}";
            }

            return $"mem:{src.PixelWidth}x{src.PixelHeight}|fmt:{src.Format}";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryComputeFileFingerprint(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var p = Path.GetFullPath(path);
            if (!File.Exists(p))
                return null;

            var fi = new FileInfo(p);
            return $"file:{p}|lw:{fi.LastWriteTimeUtc.Ticks}|len:{fi.Length}";
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource? TryLoadBitmapSourceFromFileFresh(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var p = Path.GetFullPath(path);
            if (!File.Exists(p))
                return null;

            // Decode from bytes to avoid WPF URI image caching when a file is replaced in-place.
            var bytes = File.ReadAllBytes(p);
            using var ms = new MemoryStream(bytes);
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private string? TryComputeLiveUserDefinedBackgroundFingerprintFromBitmap(System.Windows.Media.Imaging.BitmapSource src)
    {
        try
        {
            var mode = (_backgroundMode ?? "Default").Trim();

            // Custom file-backed backgrounds: compare on-disk stamp only (stable across decode/caching).
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_customBackgroundImagePath) &&
                File.Exists(_customBackgroundImagePath))
            {
                return TryComputeFileFingerprint(_customBackgroundImagePath);
            }

            return TryComputeBackgroundBitmapFingerprint(src);
        }
        catch
        {
            return TryComputeBackgroundBitmapFingerprint(src);
        }
    }

    private string? TryComputeExpectedUserDefinedBackgroundFingerprintWithoutDecode()
    {
        try
        {
            var mode = (_backgroundMode ?? "Default").Trim();
            if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_customBackgroundImagePath) &&
                File.Exists(_customBackgroundImagePath))
            {
                return TryComputeFileFingerprint(_customBackgroundImagePath);
            }

            if (System.Windows.Application.Current.Resources[GetDefaultBackgroundResourceKey(mode)] is System.Windows.Media.ImageBrush defBrush
                && defBrush.ImageSource is System.Windows.Media.Imaging.BitmapSource srcDef)
                return TryComputeBackgroundBitmapFingerprint(srcDef);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryLoadUserDefinedBackgroundBitmapSourceFromSettings(out System.Windows.Media.Imaging.BitmapSource? src)
    {
        src = null;
        try
        {
            var mode = (_backgroundMode ?? "Default").Trim();
            if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_customBackgroundImagePath) &&
                File.Exists(_customBackgroundImagePath))
            {
                src = TryLoadBitmapSourceFromFileFresh(_customBackgroundImagePath);
                return src is not null;
            }

            if (System.Windows.Application.Current.Resources[GetDefaultBackgroundResourceKey(mode)] is System.Windows.Media.ImageBrush defBrush
                && defBrush.ImageSource is System.Windows.Media.Imaging.BitmapSource srcDef)
            {
                src = srcDef;
                return true;
            }

            return false;
        }
        catch
        {
            src = null;
            return false;
        }
    }

    private static string GetDefaultBackgroundResourceKey(string? backgroundMode)
    {
        var m = string.IsNullOrWhiteSpace(backgroundMode) ? "Default (Lylly)" : backgroundMode.Trim();
        return m switch
        {
            "Default (Meow Cat)" => "App.Brush.DefaultWindowBgImage.MeowCat",
            "Default (Lylly)" => "App.Brush.DefaultWindowBgImage.Lylly",
            "Default" => "App.Brush.DefaultWindowBgImage.Lylly",
            _ => "App.Brush.DefaultWindowBgImage.Lylly",
        };
    }

    private void TryUpdateUserDefinedMainCropInPlace()
    {
        try
        {
            // Update raw + scrimmed main brushes by reusing their existing ImageSource.
            if (System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] is not System.Windows.Media.ImageBrush raw
                || raw.ImageSource is not System.Windows.Media.Imaging.BitmapSource src)
                return;

            // Build a new raw brush with updated viewbox but same source.
            var newRaw = new System.Windows.Media.ImageBrush(src);
            ApplyBackgroundImageUserDefinedToBrush(newRaw, src, GetUserDefinedRectForTarget("Main"));
            try { newRaw.Freeze(); } catch { /* ignore */ }
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] = newRaw;

            var newMain = new System.Windows.Media.ImageBrush(src);
            ApplyBackgroundImageUserDefinedToBrush(newMain, src, GetUserDefinedRectForTarget("Main"));
            try { newMain.Freeze(); } catch { /* ignore */ }
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] =
                ApplyScrimIfNeeded(newMain, scrimPercent: _backgroundScrimPercent);

            // Legacy key tracks Main.
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] =
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"];
        }
        catch { /* ignore */ }
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Tunnel on the chrome bar so drags register on the title <see cref="TextBlock"/> and on “empty” grid
    /// space (glyph-only hit testing would otherwise miss many clicks).
    /// </summary>
    private void ChromeBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (sender is not DependencyObject chromeBar)
            return;
        if (ChromeDragExcludesInteractiveChild(e.OriginalSource as DependencyObject, chromeBar))
            return;

        if (e.ClickCount >= 2)
        {
            CancelChromeDragStartTimer();
            ToggleMainWindowCompactMode();
            e.Handled = true;
            return;
        }

        CancelChromeDragStartTimer();
        _chromePendingDragWindowPoint = Mouse.GetPosition(this);
        _chromeDragStartDelayTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = ChromeDragPendingHoldDelay };
        _chromeDragStartDelayTimer.Tick += ChromeDragStartDelayTimer_OnTick;
        _chromeDragStartDelayTimer.Start();
        AttachChromePendingDragMoveListener();
        e.Handled = true;
    }

    private void ChromeBar_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (_chromeDragging)
            return;
        CancelChromeDragStartTimer();
    }

    private void ChromeDragStartDelayTimer_OnTick(object? sender, EventArgs e)
    {
        CancelChromeDragStartTimer();
        if (Mouse.LeftButton != MouseButtonState.Pressed)
            return;
        try
        {
            StartChromeDrag(PointToScreen(Mouse.GetPosition(this)));
        }
        catch
        {
            // ignore
        }
    }

    private void AttachChromePendingDragMoveListener()
    {
        if (_chromeDragPendingMoveListener)
            return;
        PreviewMouseMove += ChromePendingDrag_PreviewMouseMove;
        _chromeDragPendingMoveListener = true;
    }

    private void DetachChromePendingDragMoveListener()
    {
        if (!_chromeDragPendingMoveListener)
            return;
        try
        {
            PreviewMouseMove -= ChromePendingDrag_PreviewMouseMove;
        }
        catch
        {
            // ignore
        }
        _chromeDragPendingMoveListener = false;
    }

    private void ChromePendingDrag_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_chromeDragging || _chromeDragStartDelayTimer is null)
            return;
        if (Mouse.LeftButton != MouseButtonState.Pressed)
            return;

        var p = Mouse.GetPosition(this);
        var dx = p.X - _chromePendingDragWindowPoint.X;
        var dy = p.Y - _chromePendingDragWindowPoint.Y;
        var thr = ChromeDragPendingMoveThresholdDip * UiScale;
        if (dx * dx + dy * dy < thr * thr)
            return;

        CancelChromeDragStartTimer();
        try
        {
            StartChromeDrag(PointToScreen(p));
        }
        catch
        {
            // ignore
        }
        e.Handled = true;
    }

    private void CancelChromeDragStartTimer()
    {
        DetachChromePendingDragMoveListener();
        if (_chromeDragStartDelayTimer is null)
            return;
        try
        {
            _chromeDragStartDelayTimer.Stop();
            _chromeDragStartDelayTimer.Tick -= ChromeDragStartDelayTimer_OnTick;
        }
        catch
        {
            // ignore
        }
        _chromeDragStartDelayTimer = null;
    }

    /// <summary>Returns true if the hit target is a chrome button (or inside one) so we do not start a drag.</summary>
    private static bool ChromeDragExcludesInteractiveChild(DependencyObject? leaf, DependencyObject chromeBar)
    {
        for (var d = leaf; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, chromeBar))
                return false;
            if (d is System.Windows.Controls.Primitives.ToggleButton)
                return true;
            if (d is System.Windows.Controls.Button)
                return true;
        }

        return false;
    }

    private void StartChromeDrag(System.Windows.Point screenPoint)
    {
        CancelChromeDragStartTimer();
        try
        {
            // Use OS-driven window dragging so WM_MOVING-based snapping + cluster-follow can engage.
            _chromeDragging = true;
            DragMove();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _chromeDragging = false;
        }
    }

    // Legacy manual drag handlers removed (DragMove is used instead).

    private void InitializeGlobalMediaKeys()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            _mediaHotkeys = new GlobalMediaHotkeys(hwnd);
            _mediaHotkeys.PlayPausePressed += (_, _) => Dispatcher.Invoke(OnGlobalPlayPause);
            _mediaHotkeys.NextPressed += (_, _) => Dispatcher.Invoke(NavigateNextFromResolverBestEffort);
            _mediaHotkeys.PrevPressed += (_, _) => Dispatcher.Invoke(() => NavigatePreviousFromHistoryBestEffort());

            _mediaHotkeys.TryRegister();
        }
        catch
        {
            // ignore
        }
    }

    private void NavigatePreviousFromHistoryBestEffort()
    {
        try
        {
            _pendingResumeSeconds = 0;
            _pendingResumeVideoId = null;
            _suppressAutoScrollVideoId = null;

            // Shuffle "tape": if we are at the beginning and user clicks Prev, disallow.
            if (_shuffleEnabled)
            {
                if (_playOrder.ShuffleTapeCursor <= 0 || _playOrder.ShuffleTapeVideoIds.Count == 0)
                    return;

                // Walk back until we find an entry that still exists in the playlist.
                for (var i = _playOrder.ShuffleTapeCursor - 1; i >= 0; i--)
                {
                    var prevVideoId = _playOrder.ShuffleTapeVideoIds[i];
                    var prevEntry = _playlistCore.Entries.FirstOrDefault(e => string.Equals(e.VideoId, prevVideoId, StringComparison.OrdinalIgnoreCase));
                    if (prevEntry is not null)
                    {
                        PlayTargetEntryNow(prevEntry, suppressHistoryPushOnce: true);
                        return;
                    }
                }

                // Nothing valid before current.
                return;
            }

            // Try to navigate to the previous track from history first.
            while (_previousTrackHistory.Count > 0)
            {
                var prevVideoId = _previousTrackHistory.Pop();
                var prevEntry = _playlistCore.Entries.FirstOrDefault(e => string.Equals(e.VideoId, prevVideoId, StringComparison.OrdinalIgnoreCase));
                if (prevEntry is not null)
                {
                    // Prevent "ping-pong": when going back, don't immediately push the current track into history.
                    PlayTargetEntryNow(prevEntry, suppressHistoryPushOnce: true);
                    return;
                }
            }
        }
        catch { /* ignore */ }

        // Fallback to engine default.
        try { _ = _engine.PrevAsync(); } catch { /* ignore */ }
    }

    private void PlaylistToolsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_playlistWindow is not null)
        {
            try
            {
                if (_mainWindowCompact && _compactModeHidesAuxWindows)
                    _playlistWindowWasOpenBeforeCompact = false;

                // Capture before closing (the Closing event also captures, but don't rely on field state).
                WindowCoordinator.CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState);
                GetPlaylistHost().Toggle();
            }
            catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }
            return;
        }

        GetPlaylistHost().EnsureOpen();
    }

    private void EnsurePlaylistWindowOpen()
    {
        try
        {
            GetPlaylistHost().EnsureOpen();
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "Open playlist window failed");
            try { _playlistWindow = null; } catch { /* ignore */ }
            try { _playlistAuxCtl.Clear(); } catch { /* ignore */ }
            try
            {
                TopmostMessageBox.Show(
                    $"Failed to open Playlist window.\n\n{ex.Message}",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch { /* ignore */ }
        }
    }

    private Task<int> CleanInvalidPlaylistItemsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            static bool TryGetLocalPathLoose(string? webpageUrlOrPath, out string path)
            {
                path = "";
                if (string.IsNullOrWhiteSpace(webpageUrlOrPath))
                    return false;
                var s = webpageUrlOrPath.Trim();
                try
                {
                    if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                        uri.IsFile &&
                        !string.IsNullOrWhiteSpace(uri.LocalPath))
                    {
                        path = uri.LocalPath;
                        return true;
                    }
                }
                catch { /* ignore */ }

                try
                {
                    if (Path.IsPathRooted(s))
                    {
                        path = s;
                        return true;
                    }
                }
                catch { /* ignore */ }

                return false;
            }

            // Identify invalid items based on current UI flags + local file existence.
            var invalidIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var qi in _playlistItems.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (qi.Entry is null)
                    continue;

                var id = qi.Entry.VideoId;
                var urlOrPath = qi.Entry.WebpageUrl ?? "";

                // For cleaning: detect "local-ness" even if the file is missing.
                if (TryGetLocalPathLoose(urlOrPath, out var p))
                {
                    if (!File.Exists(p))
                        invalidIds.Add(id);
                    continue;
                }

                if (qi.IsUnavailable || qi.IsPremium || qi.IsAgeRestricted)
                    invalidIds.Add(id);
            }

            if (invalidIds.Count == 0)
                return Task.FromResult(0);

            // Remove queued instances that point at removed playlist entries.
            try
            {
                var ids = _queuedNext.Where(q => invalidIds.Contains(q.Entry.VideoId)).Select(q => q.Id).ToList();
                foreach (var id in ids)
                    RemoveQueuedInstance(id);
            }
            catch { /* ignore */ }

            var cur = _engine.GetCurrent();
            var curId = cur?.VideoId;
            var oldIndex = _engine.CurrentIndex;

            var removedIds = _playlistCore.RemoveInvalidEntries(invalidIds);
            if (removedIds.Count <= 0)
                return Task.FromResult(0);

            // Preserve current track if it still exists; otherwise pick the nearest surviving index.
            var newIndex = -1;
            if (!string.IsNullOrWhiteSpace(curId))
                newIndex = _playlistCore.Entries.FindIndex(e => string.Equals(e.VideoId, curId, StringComparison.OrdinalIgnoreCase));
            if (newIndex < 0)
                newIndex = _playlistCore.Entries.Count == 0 ? -1 : Math.Clamp(oldIndex, 0, _playlistCore.Entries.Count - 1);

            _engine.SetQueue(_playlistCore.Entries, startIndex: newIndex, raiseNowPlayingChanged: true);
            SetQueueList(_playlistCore.Entries, selectedIndex: -1);
            UpdatePlaylistTitleDisplayForNowPlaying();
            UpdateRefreshEnabled();
            RequestPersistSnapshot();

            return Task.FromResult(removedIds.Count);
        }
        catch
        {
            return Task.FromResult(0);
        }
    }

    private Task<int> RemoveDuplicatePlaylistItemsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Dedupe by VideoId (case-insensitive). Keep first occurrence; preserve ordering.
            var entries = _playlistCore.Entries.ToList();
            if (entries.Count <= 1)
                return Task.FromResult(0);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<PlaylistEntry>(entries.Count);
            foreach (var e in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (e is null)
                    continue;
                var id = (e.VideoId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!seen.Add(id))
                    continue;
                kept.Add(e);
            }

            var removed = Math.Max(0, entries.Count - kept.Count);
            if (removed == 0)
                return Task.FromResult(0);

            // Preserve current track if it still exists; otherwise keep nearest index.
            var curId = _engine.GetCurrent()?.VideoId;
            var oldIndex = _engine.CurrentIndex;

            _playlistCore.ReplaceEntries(kept);
            var newIndex = -1;
            if (!string.IsNullOrWhiteSpace(curId))
                newIndex = _playlistCore.Entries.FindIndex(e => string.Equals(e.VideoId, curId, StringComparison.OrdinalIgnoreCase));
            if (newIndex < 0)
                newIndex = _playlistCore.Entries.Count == 0 ? -1 : Math.Clamp(oldIndex, 0, _playlistCore.Entries.Count - 1);

            _engine.SetQueue(_playlistCore.Entries, startIndex: newIndex, raiseNowPlayingChanged: true);
            SetQueueList(_playlistCore.Entries, selectedIndex: -1);
            UpdatePlaylistTitleDisplayForNowPlaying();
            UpdateRefreshEnabled();
            RequestPersistSnapshot();

            return Task.FromResult(removed);
        }
        catch
        {
            return Task.FromResult(0);
        }
    }

    private void LyricsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lyricsWindow is not null)
            {
                if (_mainWindowCompact && _compactModeHidesAuxWindows)
                    _lyricsWindowWasOpenBeforeCompact = false;
                try
                {
                    WindowCoordinator.CaptureWindowBounds(_lyricsWindow, out _lastLyricsBounds, out _lastLyricsWindowState);
                    if (_lyricsWindow.IsVisible)
                    {
                        _compactUserOpenedLyricsWindow = false;
                        GetLyricsHost().Hide();
                    }
                    else
                    {
                        if (_mainWindowCompact && _compactModeHidesAuxWindows)
                            _lyricsWindowWasOpenBeforeCompact = true;
                        _compactUserOpenedLyricsWindow = true;
                        GetLyricsHost().EnsureOpen();
                    }
                }
                catch { /* ignore */ }
                try { SaveSettingsSnapshot(); } catch { /* ignore */ }
                return;
            }
            if (_mainWindowCompact && _compactModeHidesAuxWindows)
            {
                _compactUserOpenedLyricsWindow = true;
                _lyricsWindowWasOpenBeforeCompact = true;
            }
            GetLyricsHost().EnsureOpen();
        }
        catch { /* ignore */ }
    }

    private void LyricsUltraButtonInline_OnClick(object sender, RoutedEventArgs e)
    {
        LyricsButton_OnClick(sender, e);
    }

    private void EnsureLyricsWindowOpen()
    {
        try
        {
            GetLyricsHost().EnsureOpen();
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "Open lyrics window failed");
            try { _lyricsWindow = null; } catch { /* ignore */ }
            try { _lyricsAuxCtl.Clear(); } catch { /* ignore */ }
        }
    }

    private int FindPlayOrderIndexByVideoId(string videoId)
    {
        for (var i = 0; i < _engine.PlayOrder.Count; i++)
        {
            if (string.Equals(_engine.PlayOrder[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void FocusPlaylistOnNowPlaying()
    {
        try
        {
            if (_playlistWindow is null)
                return;
            var cur = _engine.GetCurrent();
            if (cur is null)
            {
                try { _playlistWindow.EndSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
                return;
            }
            if (_playlistCore.Entries.Count == 0)
            {
                try { _playlistWindow.EndSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
                return;
            }

            var idx = _playlistCore.Entries.FindIndex(e => string.Equals(e.VideoId, cur.VideoId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx >= _playlistCore.Entries.Count)
            {
                try { _playlistWindow.EndSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
                return;
            }

            //try { _playlistWindow.CenterNowPlaying(cur); } catch { /* ignore */ }
        }
        catch
        {
            try { _playlistWindow?.EndSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
        }
    }

    private static bool IsYoutubeLikeSource(PlaylistSourceType t)
        => t == PlaylistSourceType.YouTube || t == PlaylistSourceType.SearchYoutubeMusic;

    private Task OpenYoutubeModalAsync(Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dlg = new YoutubeModal(
            searchVideosAsync: async (query, count, minLen, append, dedupe, ct) => await SearchYoutubeVideosAsync(query, count, minLen, append, dedupe, ct).ConfigureAwait(true),
            searchPlaylistsAsync: async (query, count, ct) => await _ytDlp.ResolveYoutubePlaylistSearchAsync(query, count, ct).ConfigureAwait(true),
            listAccountPlaylistsAsync: async (count, ct) => await _ytDlp.ResolveAccountPlaylistsBestEffortAsync(count, ct).ConfigureAwait(true),
            importPlaylistAsync: async (urlOrId, append, dedupe, ct) => await ImportYoutubePlaylistAsync(urlOrId, append, dedupe, ct).ConfigureAwait(true),
            tryGetPlaylistItemCountAsync: async (urlOrId, ct) => await _ytDlp.TryGetPlaylistItemCountBestEffortAsync(urlOrId, ct).ConfigureAwait(true),
            getLastUrl: () => _lastYoutubeUrl,
            setLastUrl: url =>
            {
                var t = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(url);
                if (!string.IsNullOrEmpty(t))
                    _lastYoutubeUrl = t;
            },
            openUrlAsync: async (url, ct) => await LoadUrlAsync(url, ct).ConfigureAwait(true),
            searchDefaults: (_searchDefaultCount, _searchMinLengthSeconds),
            importAppendDefault: _youtubeImportAppend,
            setImportAppendDefault: v =>
            {
                try
                {
                    _youtubeImportAppend = v;
                    SaveSettingsSnapshot();
                }
                catch { /* ignore */ }
            }
        )
        {
            Owner = owner,
        };

        try { dlg.Title = $"{GetAppTitleBase()} — YouTube"; } catch { /* ignore */ }
        // Modeless: allow interacting with Playlist/Main while the YouTube modal is open.
        dlg.ShowActivated = true;
        dlg.Show();
        return Task.CompletedTask;
    }

    private async Task LoadUrlAsync(string src, CancellationToken ct)
    {
        var s = (src ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return;

        ct.ThrowIfCancellationRequested();

        _playlistSourceText = s;

        // Accept direct stream URLs (Icecast/Shoutcast/etc) as a single-item playlist.
        if (TryParseHttpUrl(s, out var uri) && !LooksLikeYoutube(uri))
        {
            _lastPlaylistSourceType = PlaylistSourceType.M3U;
            _lastLocalPlaylistPath = s;

            var entries = new List<PlaylistEntry>
            {
                new PlaylistEntry(
                    VideoId: StreamIdFromUrl(s),
                    Title: uri.Host,
                    Channel: null,
                    DurationSeconds: null,
                    WebpageUrl: s
                )
            };

            await LoadPlaylistFromEntriesAsync(entries, title: uri.Host, sourceKey: s, isStartupAutoLoad: false, cancellationToken: ct).ConfigureAwait(true);
            return;
        }

        await LoadPlaylistFromSourceAsync(forceFetch: false, isStartupAutoLoad: false).ConfigureAwait(true);
    }

    private Task OpenLocalFilesModalAsync(Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dlg = new LocalFilesModal(
            getAppendDefault: () => _localImportAppend,
            setAppendDefault: v =>
            {
                try
                {
                    _localImportAppend = v;
                    SaveSettingsSnapshot();
                }
                catch { /* ignore */ }
            },
            getRemoveDuplicatesDefault: () => _localImportRemoveDuplicates,
            setRemoveDuplicatesDefault: v =>
            {
                try
                {
                    _localImportRemoveDuplicates = v;
                    SaveSettingsSnapshot();
                }
                catch { /* ignore */ }
            },
            getReadMetadataOnLoad: () => _readMetadataOnLoad,
            getIncludeSubfoldersOnFolderLoad: () => _includeSubfoldersOnFolderLoad,
            addFolderAsync: async (folder, append, dedupe, forceNoMetadata, ct, progress) =>
                await AddFolderAsync(folder, append, dedupe, forceNoMetadata, ct, progress).ConfigureAwait(true),
            addFilesAsync: async (files, append, dedupe, ct, progress) => await AddFilesAsync(files, append, dedupe, ct, progress).ConfigureAwait(true)
        )
        {
            Owner = owner,
        };

        try { dlg.Title = $"{GetAppTitleBase()} — Local files"; } catch { /* ignore */ }
        dlg.ShowActivated = true;
        dlg.Show();
        return Task.CompletedTask;
    }

    private async Task SearchYoutubeVideosAsync(string query, int count, int minLenSeconds, bool append, bool removeDuplicates, CancellationToken cancellationToken)
    {
        var q = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return;

        count = Math.Clamp(count <= 0 ? 50 : count, 1, 200);
        minLenSeconds = Math.Clamp(minLenSeconds, 0, 3600);

        try
        {
            _lastPlaylistSourceType = PlaylistSourceType.SearchYoutubeMusic;
            _lastLocalPlaylistPath = null;
            _playlistSourceText = $"Search: {q}";

            cancellationToken.ThrowIfCancellationRequested();
            var fetch = Math.Clamp((int)Math.Round(count * 2.0), 20, 200);
            var res = await _ytDlp.ResolveYoutubeMusicSearchAsync(q, count, minLenSeconds, fetch, cancellationToken).ConfigureAwait(true);
            var entries = PlaylistDeduper.DedupeForSearch(res.Entries);

            var title = res.PlaylistTitle ?? $"Search: {q}";
            var sourceKey = _playlistSourceText ?? "";

            if (!append)
            {
                // Replace current playlist with search results.
                try { BeginCancelPlaylistSnapshot(); } catch { /* ignore */ }
                try
                {
                    await LoadPlaylistFromEntriesAsync(
                        entries,
                        title: title,
                        sourceKey: sourceKey,
                        isStartupAutoLoad: false,
                        cancellationToken: cancellationToken).ConfigureAwait(true);
                    CommitCancelPlaylistSnapshot();
                }
                catch
                {
                    try { RollbackCancelPlaylistSnapshot(); } catch { /* ignore */ }
                    throw;
                }
                return;
            }

            // Append: preserve current playback. Search doesn't have a stable source URL, so treat as compound origin label.
            var (added, removedDupes) = AppendEntriesPreserveCurrent(
                entries,
                originLabel: title,
                originSource: sourceKey,
                removeDuplicates: removeDuplicates,
                cancellationToken: cancellationToken);

            TryShowAppendSummaryDialog("Playlist", added, removedDupes);
        }
        catch { throw; }
    }

    private async Task AddFolderAsync(
    string folder,
    bool append,
    bool removeDuplicates,
    bool forceNoMetadata,
    CancellationToken cancellationToken,
    IProgress<(int done, int total)>? progress = null)  // <-- new parameter
    {
        var f = (folder ?? "").Trim();
        if (string.IsNullOrWhiteSpace(f))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var includeSub = _includeSubfoldersOnFolderLoad;
        var readMeta = _readMetadataOnLoad && !forceNoMetadata;

        var entries = await LocalPlaylistLoader.LoadFolderAsync(
            f,
            includeSub,
            readMetadataOnLoad: readMeta,
            cancellationToken,
            metadataProgress: progress).ConfigureAwait(true);

        var title = "";
        try { title = Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); } catch { title = ""; }
        if (string.IsNullOrWhiteSpace(title))
            title = "Folder";

        if (!append)
        {
            _lastPlaylistSourceType = PlaylistSourceType.Folder;
            _lastLocalPlaylistPath = f;
            _playlistSourceText = f;
            await LoadPlaylistFromEntriesAsync(entries, title: title, sourceKey: f, isStartupAutoLoad: false, cancellationToken: cancellationToken).ConfigureAwait(true);
            return;
        }

        var (added, removedDupes) = AppendEntriesPreserveCurrent(entries, originLabel: title, originSource: f, removeDuplicates, cancellationToken);
        TryShowAppendSummaryDialog("Playlist", added, removedDupes);
    }

    private async Task AddFilesAsync(IReadOnlyList<string> files, bool append, bool removeDuplicates, CancellationToken cancellationToken, IProgress<(int done, int total)>? progress = null)
    {
        var list = files?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Where(p => p.Length > 0).ToList()
                   ?? new List<string>();
        if (list.Count == 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var readMeta = _readMetadataOnLoad;
        var entries = await LoadLocalFilesEntriesAsync(list, readMeta, cancellationToken, progress).ConfigureAwait(true);

        var title = list.Count == 1 ? "File" : "Files";
        var sourceKey = list.Count == 1 ? list[0] : "Local files";

        if (!append)
        {
            _lastPlaylistSourceType = PlaylistSourceType.Folder;
            _lastLocalPlaylistPath = sourceKey;
            _playlistSourceText = sourceKey;
            await LoadPlaylistFromEntriesAsync(entries, title: title, sourceKey: sourceKey, isStartupAutoLoad: false, cancellationToken: cancellationToken).ConfigureAwait(true);
            return;
        }

        var (added, removedDupes) = AppendEntriesPreserveCurrent(entries, originLabel: title, originSource: sourceKey, removeDuplicates, cancellationToken);
        TryShowAppendSummaryDialog("Playlist", added, removedDupes);
    }

    private async Task<List<PlaylistEntry>> LoadLocalFilesEntriesAsync(IReadOnlyList<string> files, bool readMetadata, CancellationToken ct, IProgress<(int done, int total)>? progress = null)
    {
        ct.ThrowIfCancellationRequested();

        var done = 0;

        if (!readMetadata)
        {
            var acc = new List<PlaylistEntry>(files.Count);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var t = "";
                try { t = Path.GetFileNameWithoutExtension(file); } catch { t = file; }
                acc.Add(new PlaylistEntry(
                    VideoId: LocalPlaylistLoader.CreateLocalIdFromPath(file),
                    Title: t,
                    Channel: null,
                    DurationSeconds: null,
                    WebpageUrl: file));
                progress?.Report((Interlocked.Increment(ref done), files.Count));
            }
            return acc;
        }

        using var sem = new SemaphoreSlim(4, 4);
        async Task<PlaylistEntry> OneAsync(string file)
        {
            var title = "";
            try { title = Path.GetFileNameWithoutExtension(file); } catch { title = file; }
            string? artist = null;
            int? duration = null;
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var info = await LocalMetadataService.TryGetInfoAsync(file, ct).ConfigureAwait(false);
                if (info is not null)
                {
                    if (!string.IsNullOrWhiteSpace(info.Title))
                        title = info.Title!.Trim();
                    artist = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist.Trim();
                    duration = info.DurationSeconds is > 0 ? info.DurationSeconds : null;
                }
                progress?.Report((Interlocked.Increment(ref done), files.Count));
            }
            catch { /* ignore */ }
            finally { try { sem.Release(); } catch { /* ignore */ } }

            return new PlaylistEntry(
                VideoId: LocalPlaylistLoader.CreateLocalIdFromPath(file),
                Title: title,
                Channel: artist,
                DurationSeconds: duration,
                WebpageUrl: file);
        }

        var res = await Task.WhenAll(files.Select(OneAsync)).ConfigureAwait(false);
        return res.ToList();
    }

    private (int added, int removedDuplicates) AppendEntriesPreserveCurrent(
        IReadOnlyList<PlaylistEntry> toAppend,
        string originLabel,
        string originSource,
        bool removeDuplicates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cur = _engine.GetCurrent();

        var existingCountBefore = 0;
        try { existingCountBefore = _playlistCore.Entries?.Count ?? 0; } catch { existingCountBefore = 0; }

        var appended0 = toAppend?.ToList() ?? new List<PlaylistEntry>();
        var appended = appended0;
        var removedDupes = 0;
        if (removeDuplicates)
        {
            // Dedupe against existing playlist by VideoId, and (for local entries) also by normalized full path.
            var existingEntries = _playlistCore.Entries?.ToArray() ?? Array.Empty<PlaylistEntry>();
            var existingVideoIds = new HashSet<string>(existingEntries.Select(e => e.VideoId), StringComparer.OrdinalIgnoreCase);
            var existingLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingLocalFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in existingEntries)
            {
                if (TryExtractLocalPathBestEffort(e.WebpageUrl, out var p))
                {
                    existingLocalPaths.Add(NormalizeLocalPathKey(p));
                    try { existingVideoIds.Add(LocalPlaylistLoader.CreateLocalIdFromPath(p)); } catch { /* ignore */ }
                }

                var fn0 = NormalizeLocalFileNameKey(e.WebpageUrl ?? "");
                if (!string.IsNullOrWhiteSpace(fn0))
                    existingLocalFileNames.Add(fn0);
            }

            var acc = new List<PlaylistEntry>(appended0.Count);
            var rejectByVideoId = 0;
            var rejectByPath = 0;
            var rejectByDerivedId = 0;
            var rejectByFileName = 0;
            var accepted = 0;
            foreach (var e in appended0)
            {
                if (string.IsNullOrWhiteSpace(e.VideoId) || !existingVideoIds.Add(e.VideoId))
                {
                    rejectByVideoId++;
                    continue;
                }

                if (TryExtractLocalPathBestEffort(e.WebpageUrl, out var p))
                {
                    var k = NormalizeLocalPathKey(p);
                    if (!existingLocalPaths.Add(k))
                    {
                        rejectByPath++;
                        continue;
                    }

                    try
                    {
                        var derivedId = LocalPlaylistLoader.CreateLocalIdFromPath(p);
                        // Don't reject the entry just because the derived ID equals the entry's own VideoId.
                        if (!string.Equals(derivedId, e.VideoId, StringComparison.OrdinalIgnoreCase) &&
                            !existingVideoIds.Add(derivedId))
                        {
                            rejectByDerivedId++;
                            continue;
                        }
                    }
                    catch { /* ignore */ }
                }

                var fn = NormalizeLocalFileNameKey(e.WebpageUrl ?? "");
                // File-name dedupe should only compare against *existing* playlist items,
                // not collapse multiple distinct files in the newly appended folder that happen to share a name.
                if (!string.IsNullOrWhiteSpace(fn) && existingLocalFileNames.Contains(fn))
                {
                    rejectByFileName++;
                    continue;
                }

                acc.Add(e);
                accepted++;
            }

            appended = acc;
            removedDupes = Math.Max(0, appended0.Count - appended.Count);

            try
            {
                if (appended0.Count > 0 && appended.Count == 0)
                {
                    var sample = appended0
                        .Take(3)
                        .Select(x => $"[{x.VideoId}] {(x.WebpageUrl ?? "")}")
                        .ToList();
                    AppLog.Info(
                        "AppendEntriesPreserveCurrent: dedupe rejected all incoming " +
                        $"(existing={existingCountBefore}, incoming={appended0.Count}, " +
                        $"rejVid={rejectByVideoId}, rejPath={rejectByPath}, rejDerived={rejectByDerivedId}, rejName={rejectByFileName}, accepted={accepted}) " +
                        $"sample={string.Join(" | ", sample)}",
                        AppLogInfoTier.Diagnostic);
                }
            }
            catch { /* ignore */ }
        }

        if (appended.Count == 0)
        {
            try
            {
                if (appended0.Count > 0)
                    AppLog.Info($"AppendEntriesPreserveCurrent: nothing to append (existing={existingCountBefore}, incoming={appended0.Count}, removedDupes={removedDupes})", AppLogInfoTier.Diagnostic);
            }
            catch { /* ignore */ }
            return (added: 0, removedDuplicates: removedDupes);
        }

        _playlistIsCompound = true;
        UpdateRefreshEnabled();

        var core = _playlistCore;
        if (core is null)
            return (added: 0, removedDuplicates: removedDupes);

        var entries = core.Entries;
        var origins = core.OriginByVideoId;
        if (entries is null || origins is null)
            return (added: 0, removedDuplicates: removedDupes);

        foreach (var e in appended)
            entries.Add(e);

        // Per-item origins for appended entries.
        foreach (var e in appended)
            origins[e.VideoId] = new PlaylistOriginInfo(originLabel, originSource);

        // Rebuild play order around the current track; do not change playback.
        // RebuildEffectivePlayOrderPreserveCurrent(raiseNowPlayingChanged: false);
        var final = entries.ToArray();
        _engine.SetQueue(final, startIndex: _engine.CurrentIndex); // , raiseNowPlayingChanged: false);
        SetQueueList(final, selectedIndex: -1);
        UpdatePlaylistTitleDisplayForNowPlaying();
        RequestPersistSnapshot();

        return (added: appended.Count, removedDuplicates: removedDupes);
    }

    private List<PlaylistEntry> RemoveLocalDuplicatesByFullPath(List<PlaylistEntry> entries)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _playlistCore.Entries.ToArray())
        {
            if (LocalPlaylistLoader.TryGetLocalPath(e.WebpageUrl, out var p))
                existing.Add(NormalizeLocalPathKey(p));
        }

        var acc = new List<PlaylistEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (LocalPlaylistLoader.TryGetLocalPath(e.WebpageUrl, out var p))
            {
                var k = NormalizeLocalPathKey(p);
                if (existing.Contains(k))
                    continue;
                existing.Add(k);
            }
            acc.Add(e);
        }
        return acc;
    }

    private static string NormalizeLocalPathKey(string path)
    {
        try
        {
            var p = Path.GetFullPath(path).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Normalize Win32 long path prefixes (\\?\ and \\?\UNC\) so paths compare equal.
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                p = @"\\" + p.Substring(@"\\?\UNC\".Length);
            else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(@"\\?\".Length);

            return p;
        }
        catch { return (path ?? "").Trim(); }
    }

    private static string NormalizeLocalFileNameKey(string pathOrUrl)
    {
        try
        {
            if (LocalPlaylistLoader.TryGetLocalPath(pathOrUrl, out var p))
                return (Path.GetFileName(p) ?? "").Trim();
        }
        catch { /* ignore */ }

        try { return (Path.GetFileName(pathOrUrl) ?? "").Trim(); }
        catch { return ""; }
    }

    private static bool TryExtractLocalPathBestEffort(string? webpageUrlOrPath, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(webpageUrlOrPath))
            return false;

        var s = webpageUrlOrPath.Trim();

        try
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                uri.IsFile &&
                !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                path = uri.LocalPath;
                return true;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (Path.IsPathRooted(s))
            {
                path = s;
                return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static void TryShowAppendSummaryDialog(string title, int added, int removedDuplicates)
    {
        try
        {
            var msg = removedDuplicates > 0
                ? $"Added {added} items.\nSkipped {removedDuplicates} duplicates."
                : $"Added {added} items.";
            TopmostMessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { /* ignore */ }
    }

    private async Task ImportYoutubePlaylistAsync(string playlistUrlOrId, bool append, bool dedupe, CancellationToken cancellationToken)
    {
        var src = (playlistUrlOrId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(src))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var res = await _ytDlp.ResolvePlaylistEntriesAsync(src, cancellationToken).ConfigureAwait(true);
        var plName = string.IsNullOrWhiteSpace(res.PlaylistTitle) ? null : res.PlaylistTitle!.Trim();
        var imported = res.Entries?.ToList() ?? new List<PlaylistEntry>();
        if (imported.Count == 0)
        {
            SetStatusMessage("WARN", $"No playlist entries found{(string.IsNullOrWhiteSpace(plName) ? "" : $" for \"{plName}\"")}.");
            return;
        }

        var curId = _engine.GetCurrent()?.VideoId;

        List<PlaylistEntry> merged;
        if (!append)
        {
            merged = imported;
            _lastPlaylistSourceType = PlaylistSourceType.YouTube;
            _lastLocalPlaylistPath = null;
            _playlistSourceText = src;
            _playlistIsCompound = false;
            try { SetPlaylistTitle(plName ?? src); } catch { /* ignore */ }
            try { SetStatusMessage("INFO", $"Imported playlist: {(string.IsNullOrWhiteSpace(plName) ? src : $"\"{plName}\"")} ({imported.Count} items)."); } catch { /* ignore */ }
            RebuildPerItemPlaylistOriginsForCurrentPlaylist(plName ?? src, src);
        }
        else
        {
            _playlistIsCompound = true;
            merged = _playlistCore.Entries.ToList();
            var beforeCount = merged.Count;
            var appendLabel = string.IsNullOrWhiteSpace(plName) ? src : plName;
            if (dedupe)
            {
                var seen = new HashSet<string>(merged.Select(e => e.VideoId), StringComparer.OrdinalIgnoreCase);
                foreach (var e in imported)
                    if (seen.Add(e.VideoId))
                        merged.Add(e);
            }
            else
            {
                merged.AddRange(imported);
            }
            try
            {
                foreach (var e in imported)
                    _playlistCore.OriginByVideoId[e.VideoId] = new PlaylistOriginInfo(appendLabel, src);
            }
            catch { /* ignore */ }
            try
            {
                var added = Math.Max(0, merged.Count - beforeCount);
                var removedDupes = dedupe ? Math.Max(0, imported.Count - added) : 0;
                var namePart = string.IsNullOrWhiteSpace(plName) ? src : $"\"{plName}\"";
                SetStatusMessage("INFO", $"Appended playlist: {namePart} (+{added} items).");
                TryShowAppendSummaryDialog("Playlist", added, removedDupes);
            }
            catch { /* ignore */ }
        }

        // Apply list without stopping playback; preserve current track if possible.
        _playlistCore.ReplaceEntries(merged);
        //_currentEntries = BuildEffectivePlayOrder(_engine.GetCurrent(), _shuffleEnabled).ToList();
        var startIndex = FindIndexByVideoId(_playlistCore.Entries, curId);
        _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : (startIndex >= 0 ? startIndex : 0), raiseNowPlayingChanged: false);

        var displayIndex = GetOriginalIndexByVideoId(curId) ?? 0;
        SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex);
        UpdateRefreshEnabled();
        MarkLastPlaylistSnapshotDirty();
        RequestPersistSnapshot();
        UpdatePlaylistTitleDisplayForNowPlaying();
    }

    private async Task HandleDroppedLocalPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (paths is null || paths.Count == 0)
                return;

            // Drag/drop policy: folders are always recursive; unsupported files are ignored silently.
            var files = LocalPlaylistLoader.ExpandToSupportedAudioFilesRecursive(paths, ct);
            if (files.Count == 0)
                return;

            // Use the Playlist drag/drop defaults (append/replace + dedupe).
            // Dedupe affects append only, but AddFilesAsync applies it safely either way.
            await AddFilesAsync(
                files,
                append: _playlistDragDropAppend,
                removeDuplicates: _playlistDragDropRemoveDuplicates,
                ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore (drop is best-effort)
        }
    }

    private async Task HandleDroppedUrlsAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (urls is null || urls.Count == 0)
                return;

            var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
                return;

            // Use the Playlist drag/drop defaults. For multiple dropped URLs, never "replace" more than once.
            var appendForFirst = _playlistDragDropAppend;
            for (var i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var append = i == 0 ? appendForFirst : true;
                var dedupe = _playlistDragDropRemoveDuplicates;
                await ImportYoutubePlaylistAsync(list[i], append: append, dedupe: dedupe, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore (drop is best-effort)
        }
    }

    private Task ApplyPlaylistSortAsync(PlaylistSortSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = spec.Mode;
        if (mode == PlaylistSortMode.None || _playlistCore.Entries.Count <= 1)
            return Task.CompletedTask;

        var isYoutube = IsYoutubeLikeSource(_lastPlaylistSourceType);
        var curId = _engine.GetCurrent()?.VideoId;

        string LocalPathOrUrl(PlaylistEntry e)
        {
            var s = (e.WebpageUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "";
            try
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile)
                    return u.LocalPath ?? "";
            }
            catch { /* ignore */ }
            return s;
        }

        string LocalFileNameFallback(PlaylistEntry e)
        {
            try
            {
                var p = LocalPathOrUrl(e);
                if (string.IsNullOrWhiteSpace(p))
                    return "";
                return Path.GetFileName(p) ?? "";
            }
            catch { return ""; }
        }

        string NameKey(PlaylistEntry e)
        {
            if (isYoutube)
                return (e.Title ?? "").Trim();
            var t = (e.Title ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(t) && !string.Equals(t, "(untitled)", StringComparison.OrdinalIgnoreCase))
                return t;
            return LocalFileNameFallback(e);
        }

        string SourceKey(PlaylistEntry e)
        {
            try
            {
                if (_playlistCore.OriginByVideoId.TryGetValue(e.VideoId, out var origin))
                    return (origin.Label ?? "").Trim();
            }
            catch { /* ignore */ }

            if (isYoutube)
                return (e.Channel ?? "").Trim();

            try
            {
                if (LocalPlaylistLoader.TryGetLocalPath(e.WebpageUrl, out var p))
                {
                    var dir = Path.GetDirectoryName(p) ?? "";
                    var name = string.IsNullOrWhiteSpace(dir) ? "" : (Path.GetFileName(dir) ?? dir);
                    return name;
                }
            }
            catch { /* ignore */ }

            try
            {
                var u = (e.WebpageUrl ?? "").Trim();
                if (Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
                    (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                     uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                    return (uri.Host ?? "").Trim();
            }
            catch { /* ignore */ }

            return LocalPathOrUrl(e);
        }

        IOrderedEnumerable<PlaylistEntry> ordered;
        if (spec.Direction == PlaylistSortDirection.Desc)
        {
            ordered = mode switch
            {
                PlaylistSortMode.NameOrTitle => _playlistCore.Entries
                    .OrderByDescending(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                PlaylistSortMode.ChannelOrPath => _playlistCore.Entries
                    .OrderByDescending(SourceKey, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                PlaylistSortMode.Duration => _playlistCore.Entries
                    // Keep unknown durations last even in Desc.
                    .OrderBy(static e => e.DurationSeconds is null ? 1 : 0)
                    .ThenByDescending(static e => e.DurationSeconds ?? 0)
                    .ThenByDescending(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                _ => _playlistCore.Entries.OrderByDescending(NameKey, StringComparer.OrdinalIgnoreCase),
            };
        }
        else
        {
            ordered = mode switch
            {
                PlaylistSortMode.NameOrTitle => _playlistCore.Entries
                    .OrderBy(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                PlaylistSortMode.ChannelOrPath => _playlistCore.Entries
                    .OrderBy(SourceKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                PlaylistSortMode.Duration => _playlistCore.Entries
                    // Keep unknown durations last.
                    .OrderBy(static e => e.DurationSeconds is null ? 1 : 0)
                    .ThenBy(static e => e.DurationSeconds ?? 0)
                    .ThenBy(NameKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.VideoId, StringComparer.OrdinalIgnoreCase),
                _ => _playlistCore.Entries.OrderBy(NameKey, StringComparer.OrdinalIgnoreCase),
            };
        }

        var sorted = ordered.ToList();

        _playlistCore.ReplaceEntries(sorted);
        //_currentEntries = BuildEffectivePlayOrder(_engine.GetCurrent(), _shuffleEnabled).ToList();
        var startIndex = FindIndexByVideoId(_playlistCore.Entries, curId);
        _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : (startIndex >= 0 ? startIndex : 0), raiseNowPlayingChanged: false);

        var displayIndex = GetOriginalIndexByVideoId(curId) ?? 0;
        SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex);
        MarkLastPlaylistSnapshotDirty();
        RequestPersistSnapshot();
        return Task.CompletedTask;
    }

    private void OptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_optionsWindow is not null)
        {
            try
            {
                if (_mainWindowCompact && _compactModeHidesAuxWindows)
                    _optionsWindowWasOpenBeforeCompact = false;

                WindowCoordinator.CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState);
                GetOptionsHost().Toggle();
            }
            catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }
            return;
        }

        if (_mainWindowCompact && _compactModeHidesAuxWindows)
            _optionsWindowWasOpenBeforeCompact = true;
        GetOptionsHost().EnsureOpen();
    }

    private void EnsureOptionsWindowOpen()
    {
        GetOptionsHost().EnsureOpen();
    }

    private void SyncPlaylistWindowToMain()
    {
        if (_playlistWindow is null) return;
        if (!_playlistSnapped && _playlistSnapEdge == PlaylistSnapEdge.None) return;
        if (_playlistSnapEdge == PlaylistSnapEdge.None)
        {
            _playlistSnapped = false;
            return;
        }
        try
        {
            _syncingWindowMove = true;
            var main = GetOuterBounds(this);
            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;

            var pl = GetOuterBounds(_playlistWindow);

            var desiredLeft = _playlistSnapEdge switch
            {
                PlaylistSnapEdge.Right => mainRight + SnapGapPx,
                PlaylistSnapEdge.Left => mainLeft - pl.Width - SnapGapPx,
                _ => pl.Left
            };
            var desiredTop = _playlistSnapEdge switch
            {
                PlaylistSnapEdge.Bottom => mainBottom + SnapGapPx,
                PlaylistSnapEdge.Top => mainTop - pl.Height - SnapGapPx,
                _ => mainTop + _playlistDockYOffset
            };

            // For bottom snap we preserve horizontal offset.
            if (_playlistSnapEdge == PlaylistSnapEdge.Bottom || _playlistSnapEdge == PlaylistSnapEdge.Top)
                desiredLeft = mainLeft + _playlistDockXOffset;

            // If the computed snapped position would be mostly off-screen (monitor changes / DPI changes),
            // clamp to the nearest screen's work area so the user can still reach the window.
            try
            {
                var works = GetAllWorkAreasDips(this);
                (desiredLeft, desiredTop) = ClampSnappedToWorkAreasAvoidOverlap(
                    snap: _playlistSnapEdge,
                    desiredLeft,
                    desiredTop,
                    plW: pl.Width,
                    plH: pl.Height,
                    main,
                    works);
            }
            catch { /* ignore */ }

            _playlistWindow.Left = SnapRound(desiredLeft);
            _playlistWindow.Top = SnapRound(desiredTop);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _syncingWindowMove = false;
        }
    }

    private static List<Rect> GetAllWorkAreasDips(Window w)
    {
        // Convert monitor work areas (device pixels) -> DIPs in this window's coordinate space.
        var src = PresentationSource.FromVisual(w);
        var toDevice = src?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var fromDevice = src?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        _ = toDevice; // kept for symmetry / future use

        var works = new List<Rect>();
        foreach (var s in Forms.Screen.AllScreens)
        {
            var waPx = s.WorkingArea;
            var tlDip = fromDevice.Transform(new System.Windows.Point(waPx.Left, waPx.Top));
            var brDip = fromDevice.Transform(new System.Windows.Point(waPx.Right, waPx.Bottom));
            works.Add(new Rect(tlDip.X, tlDip.Y, Math.Max(0, brDip.X - tlDip.X), Math.Max(0, brDip.Y - tlDip.Y)));
        }

        if (works.Count == 0)
        {
            // Fallback: primary work area in DIPs
            var wa = SystemParameters.WorkArea;
            works.Add(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
        }

        return works;
    }

    private static (double left, double top) ClampSnappedToWorkAreasAvoidOverlap(
        PlaylistSnapEdge snap,
        double left,
        double top,
        double plW,
        double plH,
        Rect main,
        IReadOnlyList<Rect> works)
    {
        return ClampSnappedToWorkAreasAvoidOverlapCore(snap switch
        {
            PlaylistSnapEdge.Right => SnapSide.Right,
            PlaylistSnapEdge.Left => SnapSide.Left,
            PlaylistSnapEdge.Bottom => SnapSide.Bottom,
            PlaylistSnapEdge.Top => SnapSide.Top,
            _ => SnapSide.Right
        }, left, top, plW, plH, main, works);
    }

    private enum SnapSide { Right, Left, Bottom, Top }

    private static (double left, double top) ClampSnappedToWorkAreasAvoidOverlap(
        LyricsSnapEdge snap,
        double left,
        double top,
        double lwW,
        double lwH,
        Rect main,
        IReadOnlyList<Rect> works)
    {
        return ClampSnappedToWorkAreasAvoidOverlapCore(snap switch
        {
            LyricsSnapEdge.Right => SnapSide.Right,
            LyricsSnapEdge.Left => SnapSide.Left,
            LyricsSnapEdge.Bottom => SnapSide.Bottom,
            LyricsSnapEdge.Top => SnapSide.Top,
            _ => SnapSide.Right
        }, left, top, lwW, lwH, main, works);
    }

    private static (double left, double top) ClampSnappedToWorkAreasAvoidOverlap(
        OptionsSnapEdge snap,
        double left,
        double top,
        double owW,
        double owH,
        Rect main,
        IReadOnlyList<Rect> works)
    {
        return ClampSnappedToWorkAreasAvoidOverlapCore(snap switch
        {
            OptionsSnapEdge.Right => SnapSide.Right,
            OptionsSnapEdge.Left => SnapSide.Left,
            OptionsSnapEdge.Bottom => SnapSide.Bottom,
            OptionsSnapEdge.Top => SnapSide.Top,
            _ => SnapSide.Right
        }, left, top, owW, owH, main, works);
    }

    private static (double left, double top) ClampSnappedToWorkAreasAvoidOverlapCore(
        SnapSide snap,
        double left,
        double top,
        double winW,
        double winH,
        Rect main,
        IReadOnlyList<Rect> works)
    {
        const double minVisible = 80.0;

        if (winW <= 1 || winH <= 1)
            return (left, top);

        // Candidate placements. Prefer the snapped intent; if it would overlap main due to lack of space,
        // try the opposite side (still adjacent), then above/below within the work area.
        var gap = SnapGapPx;

        (double l, double t) ClampToWork(Rect work, double l0, double t0)
        {
            var l1 = Math.Clamp(l0, work.Left, work.Right - winW);
            var t1 = Math.Clamp(t0, work.Top, work.Bottom - winH);
            return (l1, t1);
        }

        double OverlapArea(Rect a, Rect b)
        {
            var i = Rect.Intersect(a, b);
            return i.IsEmpty ? 0 : Math.Max(0, i.Width) * Math.Max(0, i.Height);
        }

        (double l, double t, double visible, double overlap) Evaluate(IEnumerable<Rect> workAreas)
        {
            var bestLocal = (l: left, t: top, visible: 0.0, overlap: double.MaxValue);
            foreach (var work in workAreas)
            {
                var candidates = new List<(double l, double t)>(8);

                // 0) current desired (clamped to this work area)
                candidates.Add(ClampToWork(work, left, top));

                if (snap == SnapSide.Right || snap == SnapSide.Left)
                {
                    var intendedLeft = snap == SnapSide.Right ? (main.Right + gap) : (main.Left - winW - gap);
                    candidates.Add(ClampToWork(work, intendedLeft, top));

                    var oppositeLeft = snap == SnapSide.Right ? (main.Left - winW - gap) : (main.Right + gap);
                    candidates.Add(ClampToWork(work, oppositeLeft, top));

                    candidates.Add(ClampToWork(work, intendedLeft, main.Top - winH - gap));
                    candidates.Add(ClampToWork(work, intendedLeft, main.Bottom + gap));
                }
                else if (snap == SnapSide.Bottom)
                {
                    var intendedTop = main.Bottom + gap;
                    candidates.Add(ClampToWork(work, left, intendedTop));
                    candidates.Add(ClampToWork(work, left, main.Top - winH - gap));
                    candidates.Add(ClampToWork(work, main.Right + gap, intendedTop));
                    candidates.Add(ClampToWork(work, main.Left - winW - gap, intendedTop));
                }
                else if (snap == SnapSide.Top)
                {
                    var intendedTop = main.Top - winH - gap;
                    candidates.Add(ClampToWork(work, left, intendedTop));
                    candidates.Add(ClampToWork(work, left, main.Bottom + gap));
                    candidates.Add(ClampToWork(work, main.Right + gap, intendedTop));
                    candidates.Add(ClampToWork(work, main.Left - winW - gap, intendedTop));
                }

                foreach (var c in candidates.Distinct())
                {
                    var r = new Rect(c.l, c.t, winW, winH);
                    var inter = Rect.Intersect(r, work);
                    var visible = inter.IsEmpty ? 0 : Math.Max(0, inter.Width) * Math.Max(0, inter.Height);
                    if (visible <= 0)
                        continue;
                    if (inter.Height < Math.Min(winH, minVisible) || inter.Width < Math.Min(winW, minVisible))
                        continue;

                    var overlap = OverlapArea(r, main);
                    if (visible > bestLocal.visible + 1e-6 || (Math.Abs(visible - bestLocal.visible) <= 1e-6 && overlap < bestLocal.overlap))
                        bestLocal = (c.l, c.t, visible, overlap);
                }
            }
            return bestLocal;
        }

        // Strong preference: keep snapped windows on the same work area as the main window.
        Rect? preferred = null;
        try
        {
            var mc = new System.Windows.Point(main.Left + main.Width / 2.0, main.Top + main.Height / 2.0);
            preferred = works.FirstOrDefault(w => w.Contains(mc));
            if (preferred is null)
            {
                // Fallback: choose the work area with the largest overlap with main.
                preferred = works
                    .OrderByDescending(w => OverlapArea(w, main))
                    .FirstOrDefault();
            }
        }
        catch { preferred = null; }

        if (preferred is Rect pw && works.Count > 1)
        {
            var bestPreferred = Evaluate(new[] { pw });
            if (bestPreferred.visible > 0)
                return (bestPreferred.l, bestPreferred.t);
        }

        var best = Evaluate(works);

        // As a last resort (no good candidates), clamp to the first work area to keep it reachable.
        if (best.visible <= 0 && works.Count > 0)
        {
            var c0 = ClampToWork(works[0], left, top);
            return c0;
        }

        return (best.l, best.t);
    }

    private void OnMainWindowMovedOrSized()
    {
        try
        {
            // If the user moves the expanded (non-compact) window after we expanded it, do not "snap back"
            // to the pre-expand compact position on the next collapse.
            if (!_mainWindowCompact && _mainWindowExpandedBoundsAfterExpand is { } b0 && !_mainWindowExpandedMovedSinceExpand)
            {
                var b1 = GetOuterBounds(this);
                var moved =
                    Math.Abs(b1.Left - b0.Left) > 1.0 ||
                    Math.Abs(b1.Top - b0.Top) > 1.0;
                if (moved)
                    _mainWindowExpandedMovedSinceExpand = true;
            }
        }
        catch { /* ignore */ }

        // Legacy "dock to main" snapping disabled (new snap service is interactive-only).

        // Persisted snap relations (aux snapped to Main) still require syncing on Main move/resize,
        // especially when SizeToContent changes height (Compact/Ultra).
        try
        {
            if (_playlistWindow is not null || _optionsWindow is not null || _lyricsWindow is not null)
            {
                try
                {
                    if (WindowSnapService.AnyWindowDragging)
                        return;
                }
                catch { /* ignore */ }

                // Phase 1 (smooth): sync on the next render tick so aux moves "with" the main resize/move.
                // Coalesce bursts (dragging/resizing) into at most one render callback per frame.
                var reqFrame = Interlocked.Increment(ref _auxSnapSyncRequestId);
                if (Interlocked.Exchange(ref _auxSnapSyncFrameQueued, 1) == 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (reqFrame != _auxSnapSyncRequestId)
                                return;
                            try
                            {
                                if (WindowSnapService.AnyWindowDragging)
                                    return;
                            }
                            catch { /* ignore */ }

                            // Programmatic Main moves/resizes (Compact/Ultra SizeToContent, restore, etc.) still need
                            // edge-relative re-application for already-snapped aux windows.
                            try { SyncPlaylistWindowToMain(); } catch { /* ignore */ }
                            try { SyncOptionsWindowToMain(); } catch { /* ignore */ }
                            try { SyncLyricsWindowToMain(); } catch { /* ignore */ }
                        }
                        catch { /* ignore */ }
                        finally
                        {
                            try { Interlocked.Exchange(ref _auxSnapSyncFrameQueued, 0); } catch { /* ignore */ }
                        }
                    }), DispatcherPriority.Render);
                }

                _auxSnapSyncDebounceTimer?.Stop();
                var req = Interlocked.Increment(ref _auxSnapSyncRequestId);
                _auxSnapSyncDebounceTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher)
                {
                    // Wait for SizeToContent/layout to settle (Compact/Ultra height changes).
                    Interval = TimeSpan.FromMilliseconds(160),
                };
                _auxSnapSyncDebounceTimer.Tick += (_, _) =>
                {
                    try { _auxSnapSyncDebounceTimer?.Stop(); } catch { /* ignore */ }
                    if (req != _auxSnapSyncRequestId)
                        return;
                    try
                    {
                        if (WindowSnapService.AnyWindowDragging)
                            return;
                    }
                    catch { /* ignore */ }

                    // Apply once more after layout settles (SizeToContent two-pass).
                    try { SyncPlaylistWindowToMain(); } catch { /* ignore */ }
                    try { SyncOptionsWindowToMain(); } catch { /* ignore */ }
                    try { SyncLyricsWindowToMain(); } catch { /* ignore */ }
                    try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
                };
                _auxSnapSyncDebounceTimer.Start();
            }
        }
        catch { /* ignore */ }

        // If a compact-mode toggle is in-flight, apply the UserDefined crop after the resize + aux-window sync.
        if (_userDefinedMainCropRefreshAfterSizePending && ShouldApplyUserDefinedBackgroundForMain())
        {
            _userDefinedMainCropRefreshAfterSizePending = false;
            try { ScheduleUserDefinedMainCropRefreshAfterLayout(); } catch { /* ignore */ }
        }

        // If not snapped, do nothing. Snapping is only initiated by moving the secondary window near the main window.
    }

    private void QueueAuxSnapSyncAfterLayout()
    {
        // Fire a couple of times to catch WPF's two-pass SizeToContent + layout settling.
        var req = Interlocked.Increment(ref _auxSnapSyncRequestId);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (req != _auxSnapSyncRequestId) return;
            try { OnMainWindowMovedOrSized(); } catch { /* ignore */ }
        }), DispatcherPriority.Render);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (req != _auxSnapSyncRequestId) return;
            try { OnMainWindowMovedOrSized(); } catch { /* ignore */ }
        }), DispatcherPriority.ContextIdle);
    }

    private void SyncLyricsWindowToMain()
    {
        if (_lyricsWindow is null) return;
        if (!_lyricsSnapped && _lyricsSnapEdge == LyricsSnapEdge.None) return;
        if (_lyricsSnapEdge == LyricsSnapEdge.None)
        {
            _lyricsSnapped = false;
            return;
        }
        try
        {
            _syncingWindowMove = true;
            var main = GetOuterBounds(this);
            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;

            var lw = GetOuterBounds(_lyricsWindow);

            var desiredLeft = _lyricsSnapEdge switch
            {
                LyricsSnapEdge.Right => mainRight + SnapGapPx,
                LyricsSnapEdge.Left => mainLeft - lw.Width - SnapGapPx,
                _ => mainLeft + _lyricsDockXOffset
            };
            var desiredTop = _lyricsSnapEdge switch
            {
                LyricsSnapEdge.Bottom => mainBottom + SnapGapPx,
                LyricsSnapEdge.Top => mainTop - lw.Height - SnapGapPx,
                _ => mainTop + _lyricsDockYOffset
            };

            _lyricsWindow.Left = SnapRound(desiredLeft);
            _lyricsWindow.Top = SnapRound(desiredTop);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _syncingWindowMove = false;
        }
    }

    private void UpdateSnapStateFromLyricsPosition()
    {
        if (_lyricsWindow is null)
            return;

        try
        {
            if (WindowState != WindowState.Normal || _lyricsWindow.WindowState != WindowState.Normal)
            {
                _lyricsSnapped = false;
                _lyricsSnapEdge = LyricsSnapEdge.None;
                return;
            }

            if (_lyricsSnapped && _lyricsSnapEdge == LyricsSnapEdge.None)
            {
                _lyricsSnapped = false;
                return;
            }

            var main = GetOuterBounds(this);
            var ly = GetOuterBounds(_lyricsWindow);

            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;
            var mainOuterH = mainBottom - mainTop;

            var lyLeft = ly.Left;
            var lyTop = ly.Top;
            var lyRight = ly.Right;
            var lyBottom = ly.Bottom;

            var verticalOverlap = ComputeSideSnapVerticalOverlapDip(mainTop, mainBottom, lyTop, lyBottom);
            var hasOverlap = verticalOverlap > SideSnapVerticalOverlapThresholdPx(mainOuterH);

            var horizontalOverlap = Math.Min(mainRight, lyRight) - Math.Max(mainLeft, lyLeft);
            var hasHOverlap = horizontalOverlap > 64;

            var desiredRightLeft = mainRight + SnapGapPx;
            var desiredLeftLeft = mainLeft - ly.Width - SnapGapPx;
            var desiredBottomTop = mainBottom + SnapGapPx;
            var desiredTopTop = mainTop - ly.Height - SnapGapPx;

            var distToRight = Math.Abs(lyLeft - desiredRightLeft);
            var distToLeft = Math.Abs(lyLeft - desiredLeftLeft);
            var distToBottom = Math.Abs(lyTop - desiredBottomTop);
            var distToTop = Math.Abs(lyTop - desiredTopTop);

            if (_lyricsSnapped)
            {
                if (_lyricsSnapEdge == LyricsSnapEdge.None)
                {
                    _lyricsSnapped = false;
                    return;
                }

                var desired = _lyricsSnapEdge switch
                {
                    LyricsSnapEdge.Right => desiredRightLeft,
                    LyricsSnapEdge.Left => desiredLeftLeft,
                    LyricsSnapEdge.Bottom => desiredBottomTop,
                    LyricsSnapEdge.Top => desiredTopTop,
                    _ => double.NaN
                };

                var movedFar = _lyricsSnapEdge switch
                {
                    LyricsSnapEdge.Bottom => Math.Abs(lyTop - desired) > BaseSnapUnsnapPx * UiScale || !hasHOverlap,
                    LyricsSnapEdge.Top => Math.Abs(lyTop - desired) > BaseSnapUnsnapPx * UiScale || !hasHOverlap,
                    _ => Math.Abs(lyLeft - desired) > BaseSnapUnsnapPx * UiScale || !hasOverlap
                };

                if (movedFar)
                {
                    _lyricsSnapped = false;
                    _lyricsSnapEdge = LyricsSnapEdge.None;
                    return;
                }

                if (_lyricsSnapEdge is LyricsSnapEdge.Bottom or LyricsSnapEdge.Top)
                    _lyricsDockXOffset = lyLeft - mainLeft;
                else
                    _lyricsDockYOffset = lyTop - mainTop;
                return;
            }

            if (hasOverlap && distToRight <= SnapThresholdPx)
            {
                _lyricsSnapped = true;
                _lyricsSnapEdge = LyricsSnapEdge.Right;
                _lyricsDockYOffset = lyTop - mainTop;
                SnapLyricsToEdge(desiredRightLeft, mainTop + _lyricsDockYOffset);
                return;
            }

            if (hasOverlap && distToLeft <= SnapThresholdPx)
            {
                _lyricsSnapped = true;
                _lyricsSnapEdge = LyricsSnapEdge.Left;
                _lyricsDockYOffset = lyTop - mainTop;
                SnapLyricsToEdge(desiredLeftLeft, mainTop + _lyricsDockYOffset);
                return;
            }

            if (hasHOverlap && distToBottom <= SnapThresholdPx)
            {
                _lyricsSnapped = true;
                _lyricsSnapEdge = LyricsSnapEdge.Bottom;
                _lyricsDockXOffset = lyLeft - mainLeft;
                SnapLyricsToEdge(lyLeft, desiredBottomTop);
                return;
            }

            if (hasHOverlap && distToTop <= SnapThresholdPx)
            {
                _lyricsSnapped = true;
                _lyricsSnapEdge = LyricsSnapEdge.Top;
                _lyricsDockXOffset = lyLeft - mainLeft;
                SnapLyricsToEdge(lyLeft, desiredTopTop);
                return;
            }

            _lyricsSnapped = false;
            _lyricsSnapEdge = LyricsSnapEdge.None;
        }
        catch
        {
            _lyricsSnapped = false;
            _lyricsSnapEdge = LyricsSnapEdge.None;
        }
    }

    private void SnapLyricsToEdge(double left, double top)
    {
        if (_lyricsWindow is null)
            return;

        try
        {
            _syncingWindowMove = true;
            var clamped = ClampSnappedToWorkAreasAvoidOverlap(_lyricsSnapEdge, left, top, _lyricsWindow.Width, _lyricsWindow.Height, GetOuterBounds(this), GetAllWorkAreasDips(this));
            _lyricsWindow.Left = SnapRound(clamped.left);
            _lyricsWindow.Top = SnapRound(clamped.top);
        }
        catch
        {
            _syncingWindowMove = false;
        }
        finally
        {
            _syncingWindowMove = false;
        }
    }

    private void SyncOptionsWindowToMain()
    {
        if (_optionsWindow is null) return;
        if (!_optionsSnapped && _optionsSnapEdge == OptionsSnapEdge.None) return;
        if (_optionsSnapEdge == OptionsSnapEdge.None)
        {
            _optionsSnapped = false;
            return;
        }
        try
        {
            _syncingWindowMove = true;
            var main = GetOuterBounds(this);
            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;

            var ow = GetOuterBounds(_optionsWindow);

            var desiredLeft = _optionsSnapEdge switch
            {
                OptionsSnapEdge.Right => mainRight + SnapGapPx,
                OptionsSnapEdge.Left => mainLeft - ow.Width - SnapGapPx,
                _ => ow.Left
            };
            var desiredTop = _optionsSnapEdge switch
            {
                OptionsSnapEdge.Bottom => mainBottom + SnapGapPx,
                OptionsSnapEdge.Top => mainTop - ow.Height - SnapGapPx,
                _ => mainTop + _optionsDockYOffset
            };

            if (_optionsSnapEdge == OptionsSnapEdge.Bottom || _optionsSnapEdge == OptionsSnapEdge.Top)
                desiredLeft = AuxWindowSnapHelper.ClampOptionsBottomSnapLeft(mainLeft, mainRight, ow.Width, _optionsDockXOffset);

            var clamped = ClampSnappedToWorkAreasAvoidOverlap(
                _optionsSnapEdge,
                desiredLeft,
                desiredTop,
                ow.Width,
                ow.Height,
                GetOuterBounds(this),
                GetAllWorkAreasDips(this));
            _optionsWindow.Left = SnapRound(clamped.left);
            _optionsWindow.Top = SnapRound(clamped.top);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _syncingWindowMove = false;
        }
    }

    private void UpdateSnapStateFromOptionsPosition()
    {
        if (_optionsWindow is null)
            return;

        try
        {
            if (WindowState != WindowState.Normal || _optionsWindow.WindowState != WindowState.Normal)
            {
                _optionsSnapped = false;
                _optionsSnapEdge = OptionsSnapEdge.None;
                return;
            }

            if (_optionsSnapped && _optionsSnapEdge == OptionsSnapEdge.None)
            {
                _optionsSnapped = false;
                return;
            }

            var main = GetOuterBounds(this);
            var ow = GetOuterBounds(_optionsWindow);

            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;
            var mainOuterH = mainBottom - mainTop;

            var owLeft = ow.Left;
            var owTop = ow.Top;
            var owRight = ow.Right;
            var owBottom = ow.Bottom;

            var verticalOverlap = ComputeSideSnapVerticalOverlapDip(mainTop, mainBottom, owTop, owBottom);
            var hasOverlap = verticalOverlap > SideSnapVerticalOverlapThresholdPx(mainOuterH);

            var horizontalOverlap = Math.Min(mainRight, owRight) - Math.Max(mainLeft, owLeft);
            var hasHOverlap = horizontalOverlap > 64;

            var desiredRightLeft = mainRight + SnapGapPx;
            var desiredLeftLeft = mainLeft - ow.Width - SnapGapPx;
            var desiredBottomTop = mainBottom + SnapGapPx;
            var desiredTopTop = mainTop - ow.Height - SnapGapPx;

            var distToRight = Math.Abs(owLeft - desiredRightLeft);
            var distToLeft = Math.Abs(owLeft - desiredLeftLeft);
            var distToBottom = Math.Abs(owTop - desiredBottomTop);
            var distToTop = Math.Abs(owTop - desiredTopTop);

            if (_optionsSnapped)
            {
                if (_optionsSnapEdge == OptionsSnapEdge.None)
                {
                    _optionsSnapped = false;
                    return;
                }

                var desired = _optionsSnapEdge switch
                {
                    OptionsSnapEdge.Right => desiredRightLeft,
                    OptionsSnapEdge.Left => desiredLeftLeft,
                    OptionsSnapEdge.Bottom => desiredBottomTop,
                    OptionsSnapEdge.Top => desiredTopTop,
                    _ => double.NaN
                };

                var movedFar = _optionsSnapEdge switch
                {
                    OptionsSnapEdge.Bottom => Math.Abs(owTop - desired) > OptionsSnapUnsnapPx || !hasHOverlap,
                    OptionsSnapEdge.Top => Math.Abs(owTop - desired) > OptionsSnapUnsnapPx || !hasHOverlap,
                    _ => Math.Abs(owLeft - desired) > OptionsSnapUnsnapPx || !hasOverlap
                };

                if (movedFar)
                {
                    _optionsSnapped = false;
                    _optionsSnapEdge = OptionsSnapEdge.None;
                    return;
                }

                if (_optionsSnapEdge == OptionsSnapEdge.Bottom || _optionsSnapEdge == OptionsSnapEdge.Top)
                    _optionsDockXOffset = owLeft - mainLeft;
                else
                    _optionsDockYOffset = owTop - mainTop;
                return;
            }

            if (hasOverlap && distToRight <= OptionsSnapThresholdPx)
            {
                _optionsSnapped = true;
                _optionsSnapEdge = OptionsSnapEdge.Right;
                _optionsDockYOffset = owTop - mainTop;
                SnapOptionsToEdge(desiredRightLeft, mainTop + _optionsDockYOffset);
                return;
            }

            if (hasOverlap && distToLeft <= OptionsSnapThresholdPx)
            {
                _optionsSnapped = true;
                _optionsSnapEdge = OptionsSnapEdge.Left;
                _optionsDockYOffset = owTop - mainTop;
                SnapOptionsToEdge(desiredLeftLeft, mainTop + _optionsDockYOffset);
                return;
            }

            if (hasHOverlap && distToBottom <= OptionsSnapThresholdPx)
            {
                _optionsSnapped = true;
                _optionsSnapEdge = OptionsSnapEdge.Bottom;
                _optionsDockXOffset = owLeft - mainLeft;
                var bottomLeft = AuxWindowSnapHelper.ClampOptionsBottomSnapLeft(mainLeft, mainRight, ow.Width, _optionsDockXOffset);
                _optionsDockXOffset = bottomLeft - mainLeft;
                SnapOptionsToEdge(bottomLeft, desiredBottomTop);
                return;
            }

            if (hasHOverlap && distToTop <= OptionsSnapThresholdPx)
            {
                _optionsSnapped = true;
                _optionsSnapEdge = OptionsSnapEdge.Top;
                _optionsDockXOffset = owLeft - mainLeft;
                var topLeft = AuxWindowSnapHelper.ClampOptionsBottomSnapLeft(mainLeft, mainRight, ow.Width, _optionsDockXOffset);
                _optionsDockXOffset = topLeft - mainLeft;
                SnapOptionsToEdge(topLeft, desiredTopTop);
                return;
            }

            _optionsSnapped = false;
            _optionsSnapEdge = OptionsSnapEdge.None;
        }
        catch
        {
            _optionsSnapped = false;
            _optionsSnapEdge = OptionsSnapEdge.None;
        }
    }

    private void SnapOptionsToEdge(double left, double top)
    {
        if (_optionsWindow is null)
            return;

        try
        {
            _syncingWindowMove = true;
            var ob = GetOuterBounds(_optionsWindow);
            var clamped = ClampSnappedToWorkAreasAvoidOverlap(_optionsSnapEdge, left, top, ob.Width, ob.Height, GetOuterBounds(this), GetAllWorkAreasDips(this));
            _optionsWindow.Left = SnapRound(clamped.left);
            _optionsWindow.Top = SnapRound(clamped.top);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _syncingWindowMove = false;
        }
    }

    private void UpdateSnapStateFromPlaylistPosition()
    {
        if (_playlistWindow is null)
            return;

        try
        {
            // Only snap/follow when windows are both in normal state.
            if (WindowState != WindowState.Normal || _playlistWindow.WindowState != WindowState.Normal)
            {
                _playlistSnapped = false;
                _playlistSnapEdge = PlaylistSnapEdge.None;
                return;
            }

            // Invariant: never consider "snapped" without a specific edge.
            if (_playlistSnapped && _playlistSnapEdge == PlaylistSnapEdge.None)
            {
                _playlistSnapped = false;
                return;
            }

            var main = GetOuterBounds(this);
            var pl = GetOuterBounds(_playlistWindow);

            var mainLeft = main.Left;
            var mainTop = main.Top;
            var mainRight = main.Right;
            var mainBottom = main.Bottom;
            var mainOuterH = mainBottom - mainTop;

            var plLeft = pl.Left;
            var plTop = pl.Top;
            var plRight = pl.Right;
            var plBottom = pl.Bottom;

            // Consider snap only if windows overlap vertically a bit (prevents accidental snap).
            var verticalOverlap = ComputeSideSnapVerticalOverlapDip(mainTop, mainBottom, plTop, plBottom);
            var hasOverlap = verticalOverlap > SideSnapVerticalOverlapThresholdPx(mainOuterH);

            // For bottom snap require horizontal overlap.
            var horizontalOverlap = Math.Min(mainRight, plRight) - Math.Max(mainLeft, plLeft);
            var hasHOverlap = horizontalOverlap > 64;

            var desiredRightLeft = mainRight + SnapGapPx;
            var desiredLeftLeft = mainLeft - pl.Width - SnapGapPx;
            var desiredBottomTop = mainBottom + SnapGapPx;
            var desiredTopTop = mainTop - pl.Height - SnapGapPx;

            var distToRight = Math.Abs(plLeft - desiredRightLeft);
            var distToLeft = Math.Abs(plLeft - desiredLeftLeft);
            var distToBottom = Math.Abs(plTop - desiredBottomTop);
            var distToTop = Math.Abs(plTop - desiredTopTop);

            // If already snapped, keep snapped unless user drags far away.
            if (_playlistSnapped)
            {
                // Defensive: if edge is missing, unsnap.
                if (_playlistSnapEdge == PlaylistSnapEdge.None)
                {
                    _playlistSnapped = false;
                    return;
                }

                var desired = _playlistSnapEdge switch
                {
                    PlaylistSnapEdge.Right => desiredRightLeft,
                    PlaylistSnapEdge.Left => desiredLeftLeft,
                    PlaylistSnapEdge.Bottom => desiredBottomTop,
                    PlaylistSnapEdge.Top => desiredTopTop,
                    _ => double.NaN
                };

                // If the target snapped position is not feasible within the current monitor work area
                // (monitor/DPI/work-area changed), allow the clamped-on-screen position without unsnapping.
                // Otherwise, a safety clamp would immediately be interpreted as "user dragged away".
                var desiredFeasible = true;
                try
                {
                    var works = GetAllWorkAreasDips(this);
                    desiredFeasible = works.Any(work => _playlistSnapEdge switch
                    {
                        PlaylistSnapEdge.Bottom => desired >= work.Top - 1 && desired <= work.Bottom - pl.Height + 1,
                        PlaylistSnapEdge.Top => desired >= work.Top - 1 && desired <= work.Bottom - pl.Height + 1,
                        PlaylistSnapEdge.Right or PlaylistSnapEdge.Left => desired >= work.Left - 1 && desired <= work.Right - pl.Width + 1,
                        _ => true
                    });
                }
                catch { /* ignore */ }

                var movedFar = _playlistSnapEdge switch
                {
                    PlaylistSnapEdge.Bottom or PlaylistSnapEdge.Top => (desiredFeasible && Math.Abs(plTop - desired) > SnapUnsnapPx) || !hasHOverlap,
                    _ => (desiredFeasible && Math.Abs(plLeft - desired) > SnapUnsnapPx) || !hasOverlap
                };

                if (movedFar)
                {
                    _playlistSnapped = false;
                    _playlistSnapEdge = PlaylistSnapEdge.None;
                    return;
                }

                // Update dock offsets only when we are actually at the intended snapped coordinates.
                // If we had to clamp to keep the window visible (monitor/work-area changed), do NOT overwrite
                // the user's chosen offset — otherwise a successful safety placement would "forget" it.
                if (_playlistSnapEdge == PlaylistSnapEdge.Bottom || _playlistSnapEdge == PlaylistSnapEdge.Top)
                {
                    var expectedLeft = mainLeft + _playlistDockXOffset;
                    if (Math.Abs(plLeft - expectedLeft) <= Math.Max(2.0, SnapThresholdPx))
                        _playlistDockXOffset = plLeft - mainLeft;
                }
                else
                {
                    var expectedTop = mainTop + _playlistDockYOffset;
                    if (Math.Abs(plTop - expectedTop) <= Math.Max(2.0, SnapThresholdPx))
                        _playlistDockYOffset = plTop - mainTop;
                }
                return;
            }

            if (hasOverlap && distToRight <= SnapThresholdPx)
            {
                _playlistSnapped = true;
                _playlistSnapEdge = PlaylistSnapEdge.Right;
                _playlistDockYOffset = plTop - mainTop;
                SnapPlaylistToEdge(desiredRightLeft, mainTop + _playlistDockYOffset);
                return;
            }

            if (hasOverlap && distToLeft <= SnapThresholdPx)
            {
                _playlistSnapped = true;
                _playlistSnapEdge = PlaylistSnapEdge.Left;
                _playlistDockYOffset = plTop - mainTop;
                SnapPlaylistToEdge(desiredLeftLeft, mainTop + _playlistDockYOffset);
                return;
            }

            if (hasHOverlap && distToBottom <= SnapThresholdPx)
            {
                _playlistSnapped = true;
                _playlistSnapEdge = PlaylistSnapEdge.Bottom;
                _playlistDockXOffset = plLeft - mainLeft;
                SnapPlaylistToEdge(mainLeft + _playlistDockXOffset, desiredBottomTop);
                return;
            }

            if (hasHOverlap && distToTop <= SnapThresholdPx)
            {
                _playlistSnapped = true;
                _playlistSnapEdge = PlaylistSnapEdge.Top;
                _playlistDockXOffset = plLeft - mainLeft;
                SnapPlaylistToEdge(mainLeft + _playlistDockXOffset, desiredTopTop);
                return;
            }

            _playlistSnapped = false;
            _playlistSnapEdge = PlaylistSnapEdge.None;
        }
        catch
        {
            _playlistSnapped = false;
            _playlistSnapEdge = PlaylistSnapEdge.None;
        }
    }

    private void SnapPlaylistToEdge(double left, double top)
    {
        if (_playlistWindow is null)
            return;

        try
        {
            _syncingWindowMove = true;
            _playlistWindow.Left = left;
            _playlistWindow.Top = top;
            _syncingWindowMove = false;
        }
        catch
        {
            _syncingWindowMove = false;
        }
    }

    /// <summary>
    /// On first launch, offer to download yt-dlp if missing. Users can always choose their own binary instead.
    /// </summary>
    private async Task ShowFirstRunExternalToolsNoticeAsync()
    {
        if (!_isFreshSettingsInstall)
            return;
        try
        {
            // Only prompt if yt-dlp isn't already available (PATH/custom/managed).
            _ = await EnsureYtDlpReadyAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyMainWindowShellIntegration()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Keep shell integration consistent with the current "App icon visibility" mode.
            // In TrayOnly mode we must not re-assert WS_EX_APPWINDOW, otherwise the window reappears on the taskbar.
            try
            {
                var m = SettingsStore.NormalizeAppIconVisibility(_appIconVisibility);
                var showTaskbar = m is "TaskbarOnly" or "TaskbarAndTray";
                // Apply the native taskbar style each time; the helper no-ops when already correct.
                ApplyTaskbarVisibilityNative(showTaskbar);
                if (showTaskbar)
                    ShellWindowStyle.EnsureAppearsAsForegroundApp(hwnd);
            }
            catch { /* ignore */ }
            EnsureMinimizeBoxStyle(hwnd);
            if (!_shellStyleHookAttached && hwnd != IntPtr.Zero)
            {
                var src = HwndSource.FromHwnd(hwnd);
                if (src is not null)
                {
                    src.AddHook(_shellStyleHook);
                    _shellStyleHookSource = src;
                    _shellStyleHookAttached = true;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void EnsureMinimizeBoxStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            var before = unchecked((uint)GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64());
            var after = before | WS_MINIMIZEBOX;
            if (after == before)
                return;

            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)unchecked((int)after));
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
        }
        catch { /* ignore */ }
    }

    private void DetachMainWindowShellStyleHook()
    {
        if (!_shellStyleHookAttached || _shellStyleHookSource is null)
            return;
        try
        {
            _shellStyleHookSource.RemoveHook(_shellStyleHook);
        }
        catch
        {
            // ignore
        }

        _shellStyleHookAttached = false;
        _shellStyleHookSource = null;
    }

    private IntPtr MainWindowShellStyleHwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_STYLECHANGED = 0x007D;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020;
        const int SC_RESTORE = 0xF120;

        // Tray icon messages are handled by the dedicated tray message HWND.

        // Taskbar click on an already-active window should minimize it (normal Windows behavior).
        // However, when TOP is enabled we keep the app visible (treat TOP as "stay up").
        if (msg == WM_SYSCOMMAND)
        {
            try
            {
                var cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MINIMIZE)
                {
                    // Only block minimize when TOP is enabled; otherwise allow Windows to handle it normally.
                    if (_alwaysOnTop)
                    {
                        handled = true;
                        // If TOP blocks minimizing the main window, still hide auxiliaries that are NOT configured
                        // to follow TOP (so taskbar-click behaves like "minimize the app" as much as possible).
                        try { MinimizeNonTopmostAuxWindows(); } catch { /* ignore */ }
                        return IntPtr.Zero;
                    }
                    return IntPtr.Zero;
                }
                if (cmd == SC_RESTORE)
                {
                    // Allow restore normally.
                    return IntPtr.Zero;
                }
            }
            catch { /* ignore */ }
        }

        if (msg != WM_STYLECHANGED)
            return IntPtr.Zero;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_isShuttingDown)
                        return;
                    var h = new WindowInteropHelper(this).Handle;
                    // Respect "Notification area only" mode: do not re-assert WS_EX_APPWINDOW when the user
                    // explicitly chose to hide the taskbar button.
                    var m = SettingsStore.NormalizeAppIconVisibility(_appIconVisibility);
                    var showTaskbar = m is "TaskbarOnly" or "TaskbarAndTray";
                    if (showTaskbar)
                        ShellWindowStyle.EnsureAppearsAsForegroundApp(h);
                }
                catch
                {
                    // ignore
                }
            }), DispatcherPriority.Normal);
        }
        catch
        {
            // ignore
        }

        return IntPtr.Zero;
    }

    private void MinimizeNonTopmostAuxWindows()
    {
        // Only minimize the windows that are NOT following TOP.
        try
        {
            if (_playlistWindow is not null && _playlistWindow.IsVisible && !_alwaysOnTopPlaylistWindow)
            {
                WindowCoordinator.CaptureWindowBounds(_playlistWindow, out var b, out var s);
                _playlistBoundsBeforeTopTaskbarMinimize = b;
                _playlistMinimizedByTopTaskbar = true;
                _playlistWindow.WindowState = WindowState.Minimized;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_optionsWindow is not null && _optionsWindow.IsVisible && !_alwaysOnTopOptionsWindow)
            {
                WindowCoordinator.CaptureWindowBounds(_optionsWindow, out var b, out var s);
                _optionsBoundsBeforeTopTaskbarMinimize = b;
                _optionsMinimizedByTopTaskbar = true;
                _optionsWindow.WindowState = WindowState.Minimized;
            }
        }
        catch { /* ignore */ }
    }

    // (Intentionally no "taskbar click detection" fallback here; it proved unreliable and caused focus bugs.)

    /// <summary>Centers the main window on the primary work area when no prior settings exist.</summary>
    private void ApplyFirstRunMainWindowPlacement()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        var w = ActualWidth > 0 && !double.IsNaN(ActualWidth) ? ActualWidth : Width;
        var h = ActualHeight > 0 && !double.IsNaN(ActualHeight) ? ActualHeight : Height;
        if (w <= 0 || double.IsNaN(w))
            w = Width;
        if (h <= 0 || double.IsNaN(h))
            h = Height;
        Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - h) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private static Rect GetOuterBounds(Window w)
    {
        // ActualWidth/Height are more accurate once shown; fallback to Width/Height.
        var width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        var height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        return new Rect(w.Left, w.Top, width, height);
    }

    private void UpdatePlaylistSnapStateFromCurrentPositionsBestEffort()
    {
        try
        {
            if (_playlistWindow is null)
                return;

            var main = GetOuterBounds(this);
            var aux = GetOuterBounds(_playlistWindow);
            var r = AuxWindowSnapHelper.InferPersistedSnap(
                main, aux,
                SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx,
                AuxSnapWindowKind.Playlist);

            if (r.Snapped && Enum.TryParse<PlaylistSnapEdge>(r.Edge, ignoreCase: true, out var edge) && edge != PlaylistSnapEdge.None)
            {
                _playlistSnapped = true;
                _playlistSnapEdge = edge;
                _playlistDockXOffset = r.DockXOffset;
                _playlistDockYOffset = r.DockYOffset;
                return;
            }

            _playlistSnapped = false;
            _playlistSnapEdge = PlaylistSnapEdge.None;
        }
        catch { /* ignore */ }
    }

    private void UpdateLyricsSnapStateFromCurrentPositionsBestEffort()
    {
        try
        {
            if (_lyricsWindow is null)
                return;

            var main = GetOuterBounds(this);
            var aux = GetOuterBounds(_lyricsWindow);
            var r = AuxWindowSnapHelper.InferPersistedSnap(
                main, aux,
                SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx,
                AuxSnapWindowKind.Lyrics);

            if (r.Snapped && Enum.TryParse<LyricsSnapEdge>(r.Edge, ignoreCase: true, out var edge) && edge != LyricsSnapEdge.None)
            {
                _lyricsSnapped = true;
                _lyricsSnapEdge = edge;
                _lyricsDockXOffset = r.DockXOffset;
                _lyricsDockYOffset = r.DockYOffset;
                return;
            }

            _lyricsSnapped = false;
            _lyricsSnapEdge = LyricsSnapEdge.None;
        }
        catch { /* ignore */ }
    }

    private void UpdateOptionsSnapStateFromCurrentPositionsBestEffort()
    {
        try
        {
            if (_optionsWindow is null)
                return;

            var main = GetOuterBounds(this);
            var aux = GetOuterBounds(_optionsWindow);
            var r = AuxWindowSnapHelper.InferPersistedSnap(
                main, aux,
                SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx,
                AuxSnapWindowKind.Options);

            if (r.Snapped && Enum.TryParse<OptionsSnapEdge>(r.Edge, ignoreCase: true, out var edge) && edge != OptionsSnapEdge.None)
            {
                _optionsSnapped = true;
                _optionsSnapEdge = edge;
                _optionsDockXOffset = r.DockXOffset;
                _optionsDockYOffset = r.DockYOffset;
                return;
            }

            _optionsSnapped = false;
            _optionsSnapEdge = OptionsSnapEdge.None;
        }
        catch { /* ignore */ }
    }

    private double SnapRound(double value)
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget is null)
                return Math.Round(value);
            var m = src.CompositionTarget.TransformToDevice;
            var scale = m.M11; // assume uniform
            return Math.Round(value * scale) / scale;
        }
        catch
        {
            return Math.Round(value);
        }
    }

    private void OnGlobalPlayPause()
    {
        if (_engine.GetCurrent() is null)
            return;

        if (!_engine.IsPlaying)
        {
            if (_engine.CanResume)
                _engine.TogglePlayPause();
            else
                _ = _engine.PlayCurrentAsync();
            return;
        }

        _engine.TogglePlayPause();
    }

    private void SetStatusMessage(string status, string message)
    {
        _nowPlayingStatus = status;
        _nowPlayingEntry = null;
        if (NowPlayingStatusRun is not null)
            NowPlayingStatusRun.Text = $"[{status.Trim().ToUpperInvariant()}] ";
        if (NowPlayingTitleRun is not null)
            NowPlayingTitleRun.Text = message;
        else
            NowPlayingTextBlock.Text = $"[{status.Trim().ToUpperInvariant()}] {message}";
    }

    private void ShowInfoToast(string message, int ms = 2500)
    {
        try
        {
            var req = Interlocked.Increment(ref _statusToastRequestId);
            SetStatusMessage("INFO", message);

            // Revert to current song/status after a short delay (unless something else updated status).
            DispatcherTimer? t = null;
            t = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 250, 15000)),
            };
            t.Tick += (_, _) =>
            {
                try { t.Stop(); } catch { /* ignore */ }
                try
                {
                    if (req != _statusToastRequestId)
                        return;
                    SyncNowPlayingFromEngine();
                    UpdatePlaylistTitleDisplayForNowPlaying();
                }
                catch { /* ignore */ }
            };
            t.Start();
        }
        catch { /* ignore */ }
    }

    private void SyncNowPlayingFromEngine()
    {
        try
        {
            var cur = _engine.GetCurrent();
            if (cur is null)
                return;

            _nowPlayingEntry = cur;
            try
            {
                // Startup restore path can bypass NowPlayingChanged; make sure shuffle tape / recently-played
                // state is seeded so the current track is reachable via Prev after the next shuffle advance.
                if (_shuffleEnabled)
                {
                    if (_playOrder.ShuffleTapeVideoIds.Count == 0 || _playOrder.ShuffleTapeCursor < 0)
                        _playOrder.RecordNowPlayingForShuffleTape(cur.VideoId, _playlistCore.Entries.Count);
                }
                _playOrder.RecordRecentlyPlayedVideoId(cur.VideoId, _playlistCore.Entries.Count);
            }
            catch { /* ignore */ }

            // Cached lyrics hydration (no network) so the Lyrics window isn't blank on startup restore.
            try { TryLoadLyricsFromCacheForEntryBestEffort(cur); } catch { /* ignore */ }
            try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
            try { UpdateLyricsDisplay(force: true); } catch { /* ignore */ }

            _nowPlayingStatus = _engine.IsPlaying
                ? "PLAYING"
                : (_engine.CanResume ? "PAUSED" : "STOPPED");
            try
            {
                if (cur.DurationSeconds is int d && d > 0)
                    _engine.OverrideCurrentDurationSeconds(d);
                else if (_startupSettings.CurrentDurationSeconds is int ds && ds > 0)
                    _engine.OverrideCurrentDurationSeconds(ds);
                UpdateDurationUi(cur.DurationSeconds ?? _engine.CurrentDurationSeconds);
            }
            catch { /* ignore */ }
            UpdateNowPlayingText();
            UpdatePlaylistTitleDisplayForNowPlaying();
            _ = TryResolveLyricsAsync();
            // Startup restore path can bypass NowPlayingChanged; kick enrichment so duration/seek overlay appears.
            _ = EnrichLocalNowPlayingAsync(cur);
            _ = EnrichYoutubeDurationNowPlayingAsync(cur);
        }
        catch
        {
            // ignore
        }
    }

    private void BeginCancelPlaylistSnapshot()
        => _cancelPlaylistSnapshot = PlaylistRestoreSnapshot.Capture(this);

    private void CommitCancelPlaylistSnapshot()
        => _cancelPlaylistSnapshot = null;

    private void RollbackCancelPlaylistSnapshot()
    {
        try
        {
            if (_cancelPlaylistSnapshot is { } s)
            {
                s.Restore(this);
                _cancelPlaylistSnapshot = null;
            }
        }
        catch { /* ignore */ }
    }

    // Legacy handler (controls are in secondary windows now).
    private async void LoadButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadPlaylistFromSourceAsync();
    }

    private async Task LoadPlaylistFromSourceAsync()
        => await LoadPlaylistFromSourceAsync(forceFetch: false, isStartupAutoLoad: false);

    private async Task LoadPlaylistFromSourceAsync(bool forceFetch, bool isStartupAutoLoad)
    {
        try
        {
            _playlistWindow?.SetLoadEnabled(false);
            SetStatusMessage("INFO", "Loading playlist...");
            SetQueueList(Array.Empty<PlaylistEntry>(), selectedIndex: -1);
            SetPlaylistTitle(null);

            var input = _playlistSourceText?.Trim() ?? string.Empty;
            var playlistId = PlaylistIdParser.TryExtractPlaylistId(input) ?? input;
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                SetStatusMessage("INFO", "Paste a playlist URL or ID.");
                return;
            }

            // Changing playlist source should stop playback.
            var sourceChanged = _hasLoadedPlaylist &&
                                !string.IsNullOrWhiteSpace(_loadedPlaylistId) &&
                                !string.Equals(_loadedPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase);
            if (sourceChanged)
            {
                try { _engine.Stop(); } catch { /* ignore */ }
                _pendingResumeSeconds = 0;
                _pendingResumeVideoId = null;
                ResetTimelineUiToStart();
            }

            _lastPlaylistSourceType = PlaylistSourceType.YouTube;
            _lastLocalPlaylistPath = null;
            if (PlaylistSourcePathHeuristics.LooksLikeLocalFilesystemSource(input))
            {
                SetStatusMessage("INFO", "That looks like a local file or folder. Use Open or Load saved playlist instead.");
                return;
            }

            if (PlaylistSourcePathHeuristics.IsStorableLastLoadedYoutubeUrl(input))
                _lastYoutubeUrl = input;

            // If we already loaded this playlist during this run, don't hit yt-dlp again unless forced.
            if (!forceFetch &&
                _hasLoadedPlaylist &&
                !string.IsNullOrWhiteSpace(_loadedPlaylistId) &&
                string.Equals(_loadedPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase) &&
                _playlistCore.Entries.Count > 0)
            {
                SetStatusMessage("INFO", $"Loaded {_playlistCore.Entries.Count} items (cached in memory).");
                SyncNowPlayingFromEngine();
                MarkLastPlaylistSnapshotDirty();
                return;
            }

            // Use the on-disk cache when the playlist hasn't changed (startup / normal load).
            if (!forceFetch)
            {
                var cacheDiskPath = PlaylistCacheStore.GetCachePath();
                var cacheFileOnDisk = File.Exists(cacheDiskPath);
                var cached = PlaylistCacheStore.Load();
                if (cacheFileOnDisk && cached is null)
                {
                    try
                    {
                        SetStatusMessage(
                            "WARN",
                            "Saved playlist cache file could not be read; loading from the network instead.");
                    }
                    catch { /* ignore */ }
                }

                if (cached is not null &&
                    !string.IsNullOrWhiteSpace(cached.PlaylistId) &&
                    string.Equals(cached.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase) &&
                    cached.Entries is not { Count: > 0 })
                {
                    try
                    {
                        SetStatusMessage(
                            "WARN",
                            "Saved playlist cache was empty for this playlist; loading fresh.");
                    }
                    catch { /* ignore */ }
                }

                if (cached is not null &&
                    !string.IsNullOrWhiteSpace(cached.PlaylistId) &&
                    string.Equals(cached.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase) &&
                    cached.Entries is { Count: > 0 })
                {
                    _loadedPlaylistId = playlistId;
                    SetPlaylistTitle(cached.PlaylistTitle);
                    _playlistCore.ReplaceEntries(cached.Entries);
                    // Only apply saved play-order from startup settings on initial app startup.
                    var applyIds = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
                    // _currentEntries = ApplySavedOrderIfAny(_playlistCore.Entries, applyIds, shuffle: _shuffleEnabled).ToList();

                    var cacheStartIndex = 0;
                    var cacheDesiredId = isStartupAutoLoad ? _startupSettings.CurrentVideoId : (_playlistCore.Entries.Count > 0 ? _playlistCore.Entries[0].VideoId : null);
                    if (!string.IsNullOrWhiteSpace(cacheDesiredId))
                    {
                        var idx = FindIndexByVideoId(_playlistCore.Entries, cacheDesiredId);
                        if (idx >= 0 && idx < _playlistCore.Entries.Count)
                            cacheStartIndex = idx;
                    }

                    var cacheDisplayIndex = GetOriginalIndexByVideoId(cacheDesiredId) ?? 0;
                    _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : cacheStartIndex, raiseNowPlayingChanged: true);
                    SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : cacheDisplayIndex);
                    _hasLoadedPlaylist = true;
                    _previousTrackHistory.Clear();
                    UpdateRefreshEnabled();

                    SetStatusMessage("INFO", $"Loaded {cached.Entries.Count} items (cached).");
                    SyncNowPlayingFromEngine();
                    FocusPlaylistOnNowPlaying();

                    if (!_startupResumeAttempted)
                    {
                        _startupResumeAttempted = true;
                        await TryResumePlaybackFromSettingsAsync();
                    }

                    MarkLastPlaylistSnapshotDirty();
                    if (!isStartupAutoLoad)
                        ClearPlaylistSearchFilterAfterNewPlaylist();
                    return;
                }
            }

            if (!await EnsureYtDlpReadyAsync().ConfigureAwait(true))
                return;

            YtDlpClient.PlaylistResolveResult resolved;
            try
            {
                resolved = await _ytDlp.ResolvePlaylistEntriesAsync(playlistId, CancellationToken.None);
            }
            catch (Win32Exception wex) when (IsYtDlpNotFound(wex))
            {
                AppLog.Exception(wex, "yt-dlp not found");
                if (!PromptForYtDlpPath())
                    throw;
                resolved = await _ytDlp.ResolvePlaylistEntriesAsync(playlistId, CancellationToken.None);
            }
            _loadedPlaylistId = playlistId;
            try { _playlistWindow?.SetSourceText(_playlistSourceText ?? ""); } catch { /* ignore */ }
            SetPlaylistTitle(resolved.PlaylistTitle);
            var entries = resolved.Entries;
            _playlistCore.ReplaceEntries(entries);
            // Only apply saved play-order from startup settings on initial app startup.
            var applyIds2 = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
            // _currentEntries = ApplySavedOrderIfAny(_playlistCore.Entries, applyIds2, shuffle: _shuffleEnabled).ToList();

            var startIndex = 0;
            var desiredId = isStartupAutoLoad
                ? _startupSettings.CurrentVideoId
                : (_playlistCore.Entries.Count > 0 ? _playlistCore.Entries[0].VideoId : null);
            if (!string.IsNullOrWhiteSpace(desiredId))
            {
                var idx = FindIndexByVideoId(_playlistCore.Entries, desiredId);
                if (idx >= 0 && idx < _playlistCore.Entries.Count)
                    startIndex = idx;
            }

            SetStatusMessage("INFO", entries.Count == 0 ? "Playlist is empty." : $"Loaded {entries.Count} items.");
            SyncNowPlayingFromEngine();

            _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            // Playlist window always shows original playlist order.
            var displayIndex = GetOriginalIndexByVideoId(desiredId) ?? 0;
            SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex);
            _hasLoadedPlaylist = true;
            _previousTrackHistory.Clear();
            UpdateRefreshEnabled();
            FocusPlaylistOnNowPlaying();
            MarkLastPlaylistSnapshotDirty();

            // Persist resolved entries so next startup doesn't need yt-dlp.
            try
            {
                PlaylistCacheStore.Save(new PlaylistCacheStore.PlaylistCache(
                    PlaylistId: playlistId,
                    PlaylistTitle: resolved.PlaylistTitle,
                    SavedAtUtc: DateTimeOffset.UtcNow,
                    Entries: entries.ToList()
                ));
            }
            catch { /* ignore */ }

            if (!isStartupAutoLoad)
                ClearPlaylistSearchFilterAfterNewPlaylist();

            if (!_startupResumeAttempted)
            {
                _startupResumeAttempted = true;
                await TryResumePlaybackFromSettingsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _nowPlayingStatus = "ERROR";
            _nowPlayingEntry = null;
            UpdateNowPlayingText(extraDetail: ex.Message);
            AppLog.Exception(ex, "Load playlist failed");
        }
        finally
        {
            _playlistWindow?.SetLoadEnabled(true);
        }
    }

    private async Task LoadPlaylistFromEntriesAsync(IReadOnlyList<PlaylistEntry> entries, string? title, string sourceKey, bool isStartupAutoLoad, CancellationToken cancellationToken = default, bool deferNowPlayingChanged = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _playlistWindow?.SetLoadEnabled(false);
            SetStatusMessage("INFO", deferNowPlayingChanged ? "Searching…" : "Loading playlist...");

            // Changing playlist source should stop playback.
            var sourceChanged = _hasLoadedPlaylist &&
                                !string.IsNullOrWhiteSpace(_loadedPlaylistId) &&
                                !string.Equals(_loadedPlaylistId, sourceKey, StringComparison.OrdinalIgnoreCase);
            //if (sourceChanged)
            //{
            try { _engine.Stop(); } catch { /* ignore */ }
            _pendingResumeSeconds = 0;
            _pendingResumeVideoId = null;
            ResetTimelineUiToStart();
            //}

            _loadedPlaylistId = sourceKey;
            _playlistSourceText = sourceKey;
            _lastLocalPlaylistPath = sourceKey;
            try { _playlistWindow?.SetSourceText(_playlistSourceText ?? ""); } catch { /* ignore */ }
            _playlistIsCompound = false;
            RebuildPerItemPlaylistOriginsForCurrentPlaylist(title ?? sourceKey, sourceKey);

            // Replacing the playlist clears the transient queue.
            ClearQueue();

            var list = entries?.ToList() ?? new List<PlaylistEntry>();
            _playlistCore.ReplaceEntries(list);

            // Populate shuffle buffer so the first "Next" click has tracks ready
            if (_shuffleEnabled)
            {
                _EnsureShuffleBufferHasItems();
            }

            // Restore queue only for startup auto-load (internal state), not when user explicitly loads a playlist file.
            if (isStartupAutoLoad && !_hasLoadedPlaylist && _startupSettings.QueuedVideoIds is { Count: > 0 } qids)
            {
                foreach (var id in qids)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var match = _playlistCore.Entries.FirstOrDefault(e => string.Equals(e.VideoId, id, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        AddToQueue(match);
                }
            }

            // Only apply saved play-order from startup settings on initial app startup.
            var applyIds3 = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
            // var baseOrder = ApplySavedOrderIfAny(_playlistCore.Entries, applyIds3, shuffle: _shuffleEnabled).ToList();
            // _currentEntries = (_queuedNext.Count == 0 ? baseOrder : _queuedNext.Select(q => q.Entry).Concat(baseOrder)).ToList();

            var startIndex = 0;
            var desiredId = isStartupAutoLoad
                ? _startupSettings.CurrentVideoId
                : (_playlistCore.Entries.Count > 0 ? _playlistCore.Entries[0].VideoId : null);
            if (!string.IsNullOrWhiteSpace(desiredId))
            {
                var idx = FindIndexByVideoId(_playlistCore.Entries, desiredId);
                if (idx >= 0 && idx < _playlistCore.Entries.Count)
                    startIndex = idx;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetPlaylistTitle(title);
            _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: !deferNowPlayingChanged);
            // Startup restore: Apply saved duration after SetQueue (Stop/SetQueue paths can clear engine duration).
            try
            {
                if (isStartupAutoLoad && _startupSettings.CurrentDurationSeconds is int ds && ds > 0)
                    _engine.OverrideCurrentDurationSeconds(ds);
            }
            catch { /* ignore */ }
            var displayIndex = GetOriginalIndexByVideoId(desiredId) ?? 0;
            if (deferNowPlayingChanged && isStartupAutoLoad)
            {
                // Startup restore: building the full playlist UI can take noticeable time for large lists and
                // blocks the dispatcher, which makes the seek bar / lyrics look "stuck" even while audio plays.
                // Defer the heavy UI rebuild until after the first render pass.
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _ = SetQueueListAsync(
                            _playlistCore.Entries,
                            selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex,
                            forceFullRebuild: true,
                            cancellationToken: CancellationToken.None);
                    }
                    catch { /* ignore */ }
                }), DispatcherPriority.ContextIdle);
            }
            else
            {
                await SetQueueListAsync(
                    _playlistCore.Entries,
                    selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex,
                    cancellationToken: cancellationToken);
            }
            _hasLoadedPlaylist = true;
            _previousTrackHistory.Clear();
            UpdateRefreshEnabled();
            if (!deferNowPlayingChanged)
                FocusPlaylistOnNowPlaying();
            UpdatePlaylistTitleDisplayForNowPlaying();

            if (!deferNowPlayingChanged)
            {
                SetStatusMessage("INFO", _playlistCore.Entries.Count == 0 ? "Playlist is empty." : $"Loaded {_playlistCore.Entries.Count} items.");
                SyncNowPlayingFromEngine();
            }
            else
                SetStatusMessage("INFO", _playlistCore.Entries.Count == 0 ? "Searching…" : $"Searching… ({_playlistCore.Entries.Count} found)");
            MarkLastPlaylistSnapshotDirty();

            if (!deferNowPlayingChanged && !isStartupAutoLoad)
                ClearPlaylistSearchFilterAfterNewPlaylist();

            // Local playlists do not support refresh; stop any auto-refresh timer.
            try
            {
                _autoRefreshMinutes = null;
                ApplyAutoRefreshSelection();
            }
            catch { /* ignore */ }

            if (!_startupResumeAttempted)
            {
                _startupResumeAttempted = true;
                cancellationToken.ThrowIfCancellationRequested();
                await TryResumePlaybackFromSettingsAsync(cancellationToken);
            }
        }
        finally
        {
            _playlistWindow?.SetLoadEnabled(true);
        }
    }

    private static PlaylistSourceType ParsePlaylistSourceType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return PlaylistSourceType.YouTube;
        return Enum.TryParse<PlaylistSourceType>(s.Trim(), ignoreCase: true, out var v) ? v : PlaylistSourceType.YouTube;
    }

    private async Task LoadLastPlaylistFromSettingsAsync()
    {
        try
        {
            // Snapshot can lag (e.g. YouTube session never marked dirty). If its SourceType disagrees with
            // AppSettings.LastPlaylistSourceType, trust settings and load from URL/path instead.
            var settingsType = _lastPlaylistSourceType;
            SavedPlaylist? snap = null;
            var snapshotUnreadable = false;
            try { snap = LastPlaylistSnapshotStore.TryLoad(out snapshotUnreadable); } catch { /* ignore */ }

            if (snapshotUnreadable)
            {
                try
                {
                    TopmostMessageBox.Show(
                        "The saved last-playlist file (last-playlist.json) could not be read. It will be ignored.\n\n"
                        + "If the file is damaged, you can delete it from your LyllyPlayer app data folder.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { /* ignore */ }
            }

            if (snap is not null &&
                ParsePlaylistSourceType(snap.SourceType) == settingsType)
            {
                try { await TryRestoreLastPlaylistSnapshotAsync(snap); } catch { /* ignore */ }
                return;
            }

            try { await LoadStartupPlaylistFromPersistedLocationAsync(); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// When the on-disk <see cref="LastPlaylistSnapshotStore"/> does not match the last saved source kind,
    /// rebuild the queue from <see cref="_playlistSourceText"/> / local path (no yt-dlp for YouTube until here).
    /// </summary>
    private async Task LoadStartupPlaylistFromPersistedLocationAsync()
    {
        if (_lastPlaylistSourceType == PlaylistSourceType.YouTube)
        {
            if (!string.IsNullOrWhiteSpace(_playlistSourceText))
                await LoadPlaylistFromSourceAsync(forceFetch: false, isStartupAutoLoad: true).ConfigureAwait(true);
            return;
        }

        if (_lastPlaylistSourceType == PlaylistSourceType.SearchYoutubeMusic)
            return;

        var source = _lastLocalPlaylistPath ?? _playlistSourceText;
        if (string.IsNullOrWhiteSpace(source))
            return;

        if (_lastPlaylistSourceType == PlaylistSourceType.Folder)
        {
            if (!Directory.Exists(source))
                return;
            var entries = await LocalPlaylistLoader.LoadFolderAsync(
                source,
                _includeSubfoldersOnFolderLoad,
                readMetadataOnLoad: _readMetadataOnLoad,
                CancellationToken.None).ConfigureAwait(true);
            await LoadPlaylistFromEntriesAsync(entries,
                Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                source,
                isStartupAutoLoad: true).ConfigureAwait(true);
            return;
        }

        // M3U / M3U8
        if (!File.Exists(source))
            return;
        var loaded = await LocalPlaylistLoader.LoadM3uAsync(
            source,
            readMetadataOnLoad: _readMetadataOnLoad,
            CancellationToken.None).ConfigureAwait(true);
        await LoadPlaylistFromEntriesAsync(loaded.entries, loaded.title, source, isStartupAutoLoad: true).ConfigureAwait(true);
    }

    private void SaveLastPlaylistSnapshotBestEffort()
    {
        try
        {
            var snap = CaptureLastPlaylistSnapshotForWrite();
            if (snap is not null)
                LastPlaylistSnapshotStore.Save(snap);
        }
        catch
        {
            // ignore
        }
    }

    private void MarkLastPlaylistSnapshotDirty()
    {
        try
        {
            _snapshotDirty = true;
            RequestPersistSnapshot();
        }
        catch { /* ignore */ }
    }

    private SavedPlaylist? CaptureLastPlaylistSnapshotForWrite()
    {
        try
        {
            return _playlistCore.BuildSavedPlaylistSnapshot(
                name: _playlistTitle ?? "Autosave",
                sourceType: _lastPlaylistSourceType.ToString(),
                source: _playlistSourceText ?? "");
        }
        catch
        {
            return null;
        }
    }

    private async Task PersistLastPlaylistSnapshotIfNeededAsync()
    {
        if (!_snapshotDirty)
            return;
        if (_snapshotPersistInFlight)
            return;

        SavedPlaylist? snap;
        try { snap = CaptureLastPlaylistSnapshotForWrite(); }
        catch { snap = null; }

        // If we have nothing to write, clear dirty and exit.
        if (snap is null)
        {
            _snapshotDirty = false;
            return;
        }

        _snapshotPersistInFlight = true;
        try
        {
            _snapshotDirty = false;
            await Task.Run(() =>
            {
                try { LastPlaylistSnapshotStore.Save(snap); } catch { /* ignore */ }
            });
        }
        finally
        {
            _snapshotPersistInFlight = false;
        }

        // If something changed while we were writing, schedule another pass.
        if (_snapshotDirty)
        {
            try { RequestPersistSnapshot(); } catch { /* ignore */ }
        }
    }

    private async Task<bool> TryRestoreLastPlaylistSnapshotAsync(SavedPlaylist snap)
    {
        try
        {
            if (snap.Entries is null || snap.Entries.Count == 0)
            {
                // Persisted "empty playlist" snapshot.
                _hasLoadedPlaylist = false;
                _loadedPlaylistId = null;
                _lastPlaylistSourceType = ParsePlaylistSourceType(snap.SourceType);
                _playlistSourceText = snap.Source ?? "";
                _playlistWindow?.SetSourceText(_playlistSourceText);
                try { SetPlaylistTitle(null); } catch { /* ignore */ }
                try { _playlistCore.Clear(); } catch { /* ignore */ }
                _engine.SetQueue(_playlistCore.Entries, startIndex: -1, raiseNowPlayingChanged: false);
                SetQueueList(Array.Empty<PlaylistEntry>(), selectedIndex: -1);
                UpdateRefreshEnabled();
                RequestPersistSnapshot();
                return true;
            }

            _lastPlaylistSourceType = ParsePlaylistSourceType(snap.SourceType);
            _playlistSourceText = snap.Source ?? "";
            if (_lastPlaylistSourceType == PlaylistSourceType.YouTube &&
            PlaylistSourcePathHeuristics.IsStorableLastLoadedYoutubeUrl(_playlistSourceText))
                _lastYoutubeUrl = _playlistSourceText;
            _playlistWindow?.SetSourceText(_playlistSourceText);

            await LoadPlaylistFromEntriesAsync(
            entries: SavedPlaylistFile.ToEntries(snap),
            title: snap.Name,
            sourceKey: string.IsNullOrWhiteSpace(_playlistSourceText) ? "snapshot" : _playlistSourceText,
            isStartupAutoLoad: true,
            deferNowPlayingChanged: true
            );

            // Apply per-item origins — same path as "Load playlist" in EnsurePlaylistWindowOpen
            ApplySavedPlaylistOriginsIfAny(snap, SavedPlaylistFile.ToEntries(snap));

            // Full UI sync now that origins are correct
            SyncNowPlayingFromEngine();
            UpdatePlaylistTitleDisplayForNowPlaying();
            try { _ = TryResolveLyricsAsync(); } catch { /* ignore */ }
            // SetStatusMessage("INFO", _playlistCore.Entries.Count == 0 ? "Playlist is empty." : $"Loaded {_playlistCore.Entries.Count} items.");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        // Aux windows are deliberately not owned (Owner=null) so they can appear separately in Alt+Tab.
        // That means Windows won't automatically raise them when the main window is activated.
        // Bring any visible aux windows forward so "focus main" also surfaces the currently open auxes.
        try { BringOpenAuxWindowsToFrontBestEffort(); } catch { /* ignore */ }
    }

    private void BringOpenAuxWindowsToFrontBestEffort()
    {
        if (_bringingAuxToFrontOnActivate)
            return;

        _bringingAuxToFrontOnActivate = true;
        try
        {
            // Defer slightly to let the activation settle; avoids flicker/races with restore/minimize flows.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    BringWindowForwardBestEffort(_playlistWindow);
                    BringWindowForwardBestEffort(_optionsWindow);
                    BringWindowForwardBestEffort(_lyricsWindow);
                    BringWindowForwardBestEffort(_logWindow);
                }
                finally
                {
                    _bringingAuxToFrontOnActivate = false;
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            _bringingAuxToFrontOnActivate = false;
        }
    }

    private static void BringWindowForwardBestEffort(Window? w)
    {
        if (w is null)
            return;
        if (!w.IsVisible)
            return;

        try
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
        }
        catch { /* ignore */ }

        // "Topmost toggle" is the least invasive way to raise a window without stealing keyboard focus
        // from the main window (calling Activate() would move focus to that window).
        try
        {
            var wasTopmost = w.Topmost;
            w.Topmost = true;
            w.Topmost = wasTopmost;
        }
        catch { /* ignore */ }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshPlaylistAsync(preserveCurrentIfPossible: true);
    }

    // yt-dlp (and optional Node) are configured in OptionsWindow now.

    private void LogButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_logWindow is not null)
            {
                try { _optionsWindow?.SetLogPopoutOpen(true); } catch { /* ignore */ }
                _logWindow.Activate();
                if (_logWindow.WindowState == WindowState.Minimized)
                    _logWindow.WindowState = WindowState.Normal;
                return;
            }
        }
        catch
        {
            _logWindow = null;
        }

        var w = new LogWindow { Owner = this };
        _logWindow = w;
        try { WindowCoordinator.RegisterSnapping(w); } catch { /* ignore */ }
        try { _optionsWindow?.SetLogPopoutOpen(true); } catch { /* ignore */ }
        w.Closed += (_, _) =>
        {
            _logWindow = null;
            try { _optionsWindow?.SetLogPopoutOpen(false); } catch { /* ignore */ }
        };
        try { w.Title = $"{GetAppTitleBase()} — Log"; } catch { /* ignore */ }
        w.Show();
    }


    private void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigatePreviousFromHistoryBestEffort();
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _engine.Stop();
            _pendingResumeSeconds = 0;
            _pendingResumeVideoId = null;
            ResetTimelineUiToStart();
        }
        catch { /* ignore */ }
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_engine.GetCurrent() is null)
            return;

        if (!_engine.IsPlaying)
        {
            // If paused, resume; otherwise start playback.
            if (_engine.CanResume)
                _engine.TogglePlayPause();
            else
            {
                var cur = _engine.GetCurrent();
                if (cur is not null &&
                    _pendingResumeSeconds > 1 &&
                    !string.IsNullOrWhiteSpace(_pendingResumeVideoId) &&
                    string.Equals(cur.VideoId, _pendingResumeVideoId, StringComparison.OrdinalIgnoreCase))
                {
                    var resumeAt = _pendingResumeSeconds;
                    _pendingResumeSeconds = 0;
                    _pendingResumeVideoId = null;
                    _ = _engine.SeekAsync(resumeAt);
                }
                else
                {
                    _pendingResumeSeconds = 0;
                    _pendingResumeVideoId = null;
                    _ = _engine.PlayCurrentAsync();
                }
            }
            return;
        }

        _engine.TogglePlayPause();
    }

    private void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigateNextFromResolverBestEffort();
    }

    private void SetShuffleEnabled(bool enabled)
    {
        _shuffleEnabled = enabled;
        UpdateShuffleToggleContent();

        try
        {
            // Seed/reset shuffle tape when enabling shuffle so the current track becomes the first tape item.
            if (_shuffleEnabled)
            {
                _playOrder.ClearShuffleTape();
                var curId = _engine.GetCurrent()?.VideoId;
                if (!string.IsNullOrWhiteSpace(curId))
                    _playOrder.RecordNowPlayingForShuffleTape(curId, _playlistCore.Entries.Count);
            }
            else
            {
                _playOrder.ClearShuffleTape();
            }
        }
        catch { /* ignore */ }

        ApplyShuffleAndPreserveCurrent();
    }

    private void UpdateShuffleToggleContent()
    {
        try
        {
            ShuffleToggle.Content = _shuffleEnabled ? "Shuffle ON" : "Shuffle OFF";
        }
        catch { /* ignore */ }

        // Make Shuffle ON visually obvious even in low-contrast themes.
        try
        {
            if (_shuffleEnabled)
            {
                ShuffleToggle.SetResourceReference(BackgroundProperty, System.Windows.SystemColors.HighlightBrushKey);
                ShuffleToggle.SetResourceReference(ForegroundProperty, System.Windows.SystemColors.HighlightTextBrushKey);
                ShuffleToggle.SetResourceReference(Border.BorderBrushProperty, System.Windows.SystemColors.HighlightBrushKey);
                ShuffleToggle.BorderThickness = new Thickness(1);
                ShuffleToggle.FontWeight = FontWeights.Bold;
            }
            else
            {
                ShuffleToggle.ClearValue(BackgroundProperty);
                ShuffleToggle.ClearValue(ForegroundProperty);
                ShuffleToggle.ClearValue(Border.BorderBrushProperty);
                ShuffleToggle.ClearValue(Border.BorderThicknessProperty);
                ShuffleToggle.FontWeight = FontWeights.Normal;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (CompactShuffleToggleButton is not null)
            {
                _suppressCompactShuffleToggle = true;
                CompactShuffleToggleButton.IsChecked = _shuffleEnabled;
            }
        }
        catch { /* ignore */ }
        finally
        {
            _suppressCompactShuffleToggle = false;
        }
    }

    private void ShuffleToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressShuffleToggle) return;
        SetShuffleEnabled(true);
        SaveSettingsSnapshot();
    }

    private void ShuffleToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressShuffleToggle) return;
        SetShuffleEnabled(false);
        SaveSettingsSnapshot();
    }

    private void CompactShuffleToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressCompactShuffleToggle) return;
        SetShuffleEnabled(true);
        SaveSettingsSnapshot();
    }

    private void CompactShuffleToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressCompactShuffleToggle) return;
        SetShuffleEnabled(false);
        SaveSettingsSnapshot();
    }

    private static RepeatMode ParseRepeatMode(string? value)
        => Enum.TryParse<RepeatMode>(value, ignoreCase: true, out var m) ? m : RepeatMode.None;

    private void RepeatButton_OnClick(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.None => RepeatMode.Single,
            RepeatMode.Single => RepeatMode.Playlist,
            _ => RepeatMode.None,
        };
        UpdateRepeatButtonContent();
        SaveSettingsSnapshot();
    }

    private void CompactRepeatButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { RepeatButton_OnClick(sender, e); } catch { /* ignore */ }
    }

    private void CompactPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_playlistWindow is not null)
            {
                if (_playlistWindow.IsVisible)
                {
                    _compactUserOpenedPlaylistWindow = false;
                    if (_mainWindowCompact && _compactModeHidesAuxWindows)
                        _playlistWindowWasOpenBeforeCompact = false;
                    try { _playlistWindow.Hide(); } catch { /* ignore */ }
                }
                else
                {
                    if (_mainWindowCompact && _compactModeHidesAuxWindows)
                    {
                        _compactUserOpenedPlaylistWindow = true;
                        _playlistWindowWasOpenBeforeCompact = true;
                    }
                    EnsurePlaylistWindowOpen();
                }
                return;
            }
            if (_mainWindowCompact && _compactModeHidesAuxWindows)
            {
                _compactUserOpenedPlaylistWindow = true;
                _playlistWindowWasOpenBeforeCompact = true;
            }
            EnsurePlaylistWindowOpen();
        }
        catch { /* ignore */ }
    }

    private void UpdateRepeatButtonContent()
    {
        if (RepeatButton is null)
            return;
        var t = _repeatMode switch
        {
            RepeatMode.Single => "Repeat: Single",
            RepeatMode.Playlist => "Repeat: All",
            _ => "Repeat: None",
        };
        RepeatButton.Content = t;

        // Make Repeat active visually obvious even in low-contrast themes.
        try
        {
            if (_repeatMode == RepeatMode.None)
            {
                RepeatButton.ClearValue(BackgroundProperty);
                RepeatButton.ClearValue(ForegroundProperty);
                RepeatButton.ClearValue(Border.BorderBrushProperty);
                RepeatButton.ClearValue(Border.BorderThicknessProperty);
                RepeatButton.FontWeight = FontWeights.Normal;
            }
            else
            {
                RepeatButton.SetResourceReference(BackgroundProperty, System.Windows.SystemColors.HighlightBrushKey);
                RepeatButton.SetResourceReference(ForegroundProperty, System.Windows.SystemColors.HighlightTextBrushKey);
                RepeatButton.SetResourceReference(Border.BorderBrushProperty, System.Windows.SystemColors.HighlightBrushKey);
                RepeatButton.BorderThickness = new Thickness(1);
                RepeatButton.FontWeight = FontWeights.Bold;
            }
        }
        catch { /* ignore */ }

        // Compact repeat: make active state very obvious.
        try
        {
            if (CompactRepeatButton is null)
                return;
            if (_repeatMode == RepeatMode.None)
            {
                CompactRepeatButton.Background = System.Windows.Media.Brushes.Transparent;
                CompactRepeatButton.ClearValue(ForegroundProperty);
                CompactRepeatButton.ClearValue(FrameworkElement.ToolTipProperty);
                CompactRepeatButton.Content = "r";
            }
            else
            {
                CompactRepeatButton.SetResourceReference(BackgroundProperty, System.Windows.SystemColors.HighlightBrushKey);
                CompactRepeatButton.SetResourceReference(ForegroundProperty, System.Windows.SystemColors.HighlightTextBrushKey);
                CompactRepeatButton.ClearValue(FrameworkElement.ToolTipProperty);
                CompactRepeatButton.Content = _repeatMode switch
                {
                    RepeatMode.Single => "Ro",
                    _ => "Ra",
                };
            }
        }
        catch { /* ignore */ }
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        try { _engine.SetVolume(e.NewValue); } catch { /* ignore */ }
        try { SaveSettingsSnapshot(); } catch { /* ignore */ }
    }

    private void AutoRefreshComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Legacy handler (auto-refresh is configured in OptionsWindow).
        ApplyAutoRefreshSelection();
    }

    // Playlist interactions are handled by PlaylistWindow now.

    private bool ShouldSuppressAutoScroll(PlaylistEntry? entry)
    {
        if (entry is null)
            return false;
        if (string.IsNullOrWhiteSpace(_suppressAutoScrollVideoId))
            return false;
        if (DateTime.UtcNow > _suppressAutoScrollUntilUtc)
        {
            _suppressAutoScrollVideoId = null;
            return false;
        }
        return string.Equals(entry.VideoId, _suppressAutoScrollVideoId, StringComparison.OrdinalIgnoreCase);
    }

    private void SourceTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Legacy handler (textbox is hosted in PlaylistWindow).
        if (e.Key == Key.Enter)
        {
            _ = LoadPlaylistFromSourceAsync();
            e.Handled = true;
        }
    }

    private async Task RefreshPlaylistAsync(bool preserveCurrentIfPossible, CancellationToken cancellationToken = default)
    {
        if (!_hasLoadedPlaylist)
        {
            SetStatusMessage("INFO", "Load a playlist first.");
            return;
        }

        var input = _playlistSourceText?.Trim() ?? string.Empty;
        var playlistId = PlaylistIdParser.TryExtractPlaylistId(input) ?? input;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            SetStatusMessage("INFO", "Paste a playlist URL or ID.");
            return;
        }

        string? currentVideoId = null;
        if (preserveCurrentIfPossible)
            currentVideoId = _engine.GetCurrent()?.VideoId;

        _playlistWindow?.SetRefreshEnabled(false);
        SetStatusMessage("INFO", "Refreshing playlist...");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await EnsureYtDlpReadyAsync(cancellationToken).ConfigureAwait(true))
                return;

            YtDlpClient.PlaylistResolveResult resolved;
            try
            {
                resolved = await _ytDlp.ResolvePlaylistEntriesAsync(playlistId, cancellationToken);
            }
            catch (Win32Exception wex) when (IsYtDlpNotFound(wex))
            {
                AppLog.Exception(wex, "yt-dlp not found");
                if (!PromptForYtDlpPath())
                    throw;
                resolved = await _ytDlp.ResolvePlaylistEntriesAsync(playlistId, cancellationToken);
            }
            SetPlaylistTitle(resolved.PlaylistTitle);
            var entries = resolved.Entries;
            _playlistCore.ReplaceEntries(entries);
            // _currentEntries = BuildEffectivePlayOrder(_engine.GetCurrent(), _shuffleEnabled).ToList();
            _loadedPlaylistId = playlistId;

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentVideoId))
            {
                var idx = FindIndexByVideoId(_playlistCore.Entries, currentVideoId);
                if (idx >= 0 && idx < _playlistCore.Entries.Count)
                    startIndex = idx;
            }

            _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            var displayIndex = GetOriginalIndexByVideoId(currentVideoId) ?? 0;
            SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex);

            SetStatusMessage("INFO", _playlistCore.Entries.Count == 0 ? "Playlist is empty." : $"Refreshed {_playlistCore.Entries.Count} items.");
            SyncNowPlayingFromEngine();

            // Update cache so future launches avoid yt-dlp unless refreshed/changed.
            try
            {
                PlaylistCacheStore.Save(new PlaylistCacheStore.PlaylistCache(
                    PlaylistId: playlistId,
                    PlaylistTitle: resolved.PlaylistTitle,
                    SavedAtUtc: DateTimeOffset.UtcNow,
                    Entries: entries.ToList()
                ));
            }
            catch { /* ignore */ }

            ClearPlaylistSearchFilterAfterNewPlaylist();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _nowPlayingStatus = "ERROR";
            _nowPlayingEntry = null;
            UpdateNowPlayingText(extraDetail: ex.Message);
            AppLog.Exception(ex, "Refresh playlist failed");
        }
        finally
        {
            _playlistWindow?.SetRefreshEnabled(true);
        }
    }

    private static async Task<bool> TryDownloadYtDlpAsync(string destExePath, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(destExePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = destExePath + ".tmp";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }

            // Official latest download URL (no GitHub API required).
            const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // Best-effort replace (keep old as .bak).
            try
            {
                var bak = destExePath + ".bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { /* ignore */ }
                if (File.Exists(destExePath))
                {
                    try { File.Move(destExePath, bak, overwrite: true); } catch { /* ignore */ }
                }
                File.Move(tmp, destExePath, overwrite: true);
            }
            catch
            {
                // If replace fails (e.g. file locked), keep the downloaded file around for manual recovery.
                try
                {
                    var alt = Path.Combine(dir ?? "", "yt-dlp.new.exe");
                    File.Move(tmp, alt, overwrite: true);
                }
                catch { /* ignore */ }
                return false;
            }

            return File.Exists(destExePath);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureYtDlpReadyAsync(CancellationToken cancellationToken = default)
    {
        ApplyResolvedToolPaths();
        var r = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
        if (r.IsFound)
            return true;

        // If not on PATH, try our managed per-user tool location.
        var managed = ToolPaths.GetManagedYtDlpPath();
        try
        {
            if (File.Exists(managed))
            {
                _savedYtDlpPath = managed;
                ApplyResolvedToolPaths();
                ApplyYtdlpPlaybackOptions();
                SaveSettingsSnapshot();
                return ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp").IsFound;
            }
        }
        catch { /* ignore */ }

        try
        {
            var msg =
                "yt-dlp was not found on PATH and no custom path is configured.\n\n" +
                "You can download yt-dlp now (recommended), or use your own yt-dlp binary by selecting it (even if it's not on PATH).\n\n" +
                "Download yt-dlp now?";

            var choice = TopmostMessageBox.Show(
                msg,
                GetAppTitleBase(),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.No)
                return PromptForYtDlpPath();
            if (choice != MessageBoxResult.Yes)
                return false;

            SetStatusMessage("INFO", "Downloading yt-dlp…");
            var ok = await TryDownloadYtDlpAsync(managed, cancellationToken).ConfigureAwait(true);
            if (!ok)
            {
                SetStatusMessage("ERROR", "yt-dlp download failed. Set it under Options → Tools.");
                return false;
            }

            _savedYtDlpPath = managed;
            ApplyResolvedToolPaths();
            ApplyYtdlpPlaybackOptions();
            SaveSettingsSnapshot();
            SetStatusMessage("INFO", "yt-dlp downloaded.");
            return ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp").IsFound;
        }
        catch
        {
            SetStatusMessage("ERROR", "yt-dlp not found. Set it under Options → Tools.");
            AppLog.Error("yt-dlp not found on PATH and no valid configured path.");
            return false;
        }
    }

    private static bool TryParseYtDlpVersion(string? s, out (int y, int m, int d) v)
    {
        v = default;
        var t = (s ?? "").Trim();
        // yt-dlp versions look like 2026.03.17
        var parts = t.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;
        if (!int.TryParse(parts[0], out var yy)) return false;
        if (!int.TryParse(parts[1], out var mm)) return false;
        if (!int.TryParse(parts[2], out var dd)) return false;
        v = (yy, mm, dd);
        return true;
    }

    private static async Task<string?> TryGetYtDlpVersionAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }
            return (stdout ?? "").Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task CheckInternalYtDlpNowAsync()
    {
        try
        {
            var managed = ToolPaths.GetManagedYtDlpPath();
            if (!File.Exists(managed))
            {
                TopmostMessageBox.Show(
                    "Internal yt-dlp is not downloaded yet.",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowInfoToast("Checking yt-dlp updates…", ms: 1200);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LyllyPlayer", "1.0"));

            // Installed version
            var installedStr = await TryGetYtDlpVersionAsync(managed, CancellationToken.None).ConfigureAwait(true);
            _ = TryParseYtDlpVersion(installedStr, out var installed);

            // Latest release tag
            using var resp = await http.GetAsync("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest").ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tn) ? (tn.GetString() ?? "") : "";
            if (!TryParseYtDlpVersion(tag, out var latest))
            {
                SetStatusMessage("ERROR", "yt-dlp update check failed.");
                return;
            }

            var isNewer = latest.CompareTo(installed) > 0;
            if (!isNewer)
            {
                ShowInfoToast($"yt-dlp is up to date ({installedStr}).", ms: 1600);
                TopmostMessageBox.Show(
                    $"Internal yt-dlp is up to date.\n\nInstalled: {installedStr}\nLatest: {tag}",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var choice = TopmostMessageBox.Show(
                $"An internal yt-dlp update is available.\n\nInstalled: {installedStr}\nLatest: {tag}\n\nUpdate now?",
                GetAppTitleBase(),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes)
            {
                ShowInfoToast("yt-dlp update available.", ms: 2500);
                return;
            }

            var ok = await TryDownloadYtDlpAsync(managed, CancellationToken.None).ConfigureAwait(true);
            if (!ok)
            {
                SetStatusMessage("ERROR", "yt-dlp update failed.");
                return;
            }

            var newVer = await TryGetYtDlpVersionAsync(managed, CancellationToken.None).ConfigureAwait(true);
            ShowInfoToast($"yt-dlp updated ({newVer}).", ms: 2500);
        }
        catch
        {
            SetStatusMessage("ERROR", "yt-dlp update check failed.");
        }
    }

    private bool PromptForYtDlpPath()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select yt-dlp executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            try
            {
                var cur = _savedYtDlpPath ?? _ytDlp.YtDlpPath;
                if (!string.IsNullOrWhiteSpace(cur) && (cur.Contains('\\') || cur.Contains('/')) && File.Exists(cur))
                    dlg.InitialDirectory = Path.GetDirectoryName(cur);
            }
            catch { /* ignore */ }

            using var top = new TopmostDialogOwner(this);
            if (dlg.ShowDialog(top.OwnerWindow) != true)
                return false;

            _savedYtDlpPath = dlg.FileName;
            ApplyResolvedToolPaths();
            ApplyYtdlpPlaybackOptions();
            SaveSettingsSnapshot();
            SetStatusMessage("INFO", $"yt-dlp set to: {Path.GetFileName(dlg.FileName)}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsYtDlpNotFound(Win32Exception ex)
        => ex.NativeErrorCode == 2;

    private static bool TryParseHttpUrl(string? s, out Uri uri)
    {
        uri = null!;
        try
        {
            if (!Uri.TryCreate((s ?? "").Trim(), UriKind.Absolute, out var u))
                return false;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;
            uri = u;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeYoutube(Uri uri)
    {
        try
        {
            var host = (uri.Host ?? "").ToLowerInvariant();
            return host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("music.youtube.com");
        }
        catch
        {
            return false;
        }
    }

    private static string StreamIdFromUrl(string url)
    {
        try
        {
            // Keep stable IDs for stable play-order and persistence.
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes((url ?? "").Trim().ToLowerInvariant());
            var hash = sha.ComputeHash(bytes);
            var hex = Convert.ToHexString(hash);
            var shortHex = hex.Length >= 12 ? hex[..12] : hex;
            return $"stream:{shortHex}";
        }
        catch
        {
            return $"stream:{Guid.NewGuid():N}";
        }
    }

    private void UpdateRefreshEnabled()
    {
        try
        {
            // Safety: compound can also be inferred from origins (e.g. loaded from saved playlist file).
            // If multiple distinct origins exist, treat as compound even if a flag wasn't set.
            if (!_playlistIsCompound && _playlistCore.OriginByVideoId.Count > 0)
            {
                var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in _playlistCore.OriginByVideoId.Values)
                    distinct.Add($"{o.Label}||{o.Source}");
                if (distinct.Count > 1)
                    _playlistIsCompound = true;
            }
        }
        catch { /* ignore */ }

        // Refresh is supported for both YouTube and local sources.
        var canRefresh = _hasLoadedPlaylist && _lastPlaylistSourceType != PlaylistSourceType.SearchYoutubeMusic;
        // For direct stream URLs loaded via Load URL, refresh doesn't make sense.
        if (TryParseHttpUrl(_lastLocalPlaylistPath ?? _playlistSourceText, out var u) && !LooksLikeYoutube(u))
            canRefresh = false;
        // Compound playlists (appended / merged) can't reliably refresh all sources.
        if (_playlistIsCompound)
            canRefresh = false;
        _playlistWindow?.SetRefreshEnabled(canRefresh);
    }

    private async Task RefreshCurrentSourceAsync(
        bool preserveCurrentIfPossible,
        CancellationToken cancellationToken = default,
        bool forceLocalNoMetadata = false)
    {
        if (!_hasLoadedPlaylist)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        if (_lastPlaylistSourceType == PlaylistSourceType.YouTube)
        {
            await RefreshPlaylistAsync(preserveCurrentIfPossible, cancellationToken);
            return;
        }

        // Local refresh: rescan folder or re-parse M3U.
        var currentVideoId = preserveCurrentIfPossible ? _engine.GetCurrent()?.VideoId : null;
        var source = _lastLocalPlaylistPath ?? _playlistSourceText;
        if (string.IsNullOrWhiteSpace(source))
            return;

        var readMetaForLocal = _readMetadataOnLoad && !forceLocalNoMetadata;

        IProgress<(int done, int total)>? metaProgress = null;
        var playlistWin = _playlistWindow;
        if (readMetaForLocal && playlistWin is not null)
        {
            metaProgress = new Progress<(int done, int total)>(v =>
            {
                if (v.total <= 0)
                    return;
                try { playlistWin.ReportBusyOverlayDeterminate((double)v.done / v.total); } catch { /* ignore */ }
            });
        }

        List<PlaylistEntry> entries;
        string? title;

        if (_lastPlaylistSourceType == PlaylistSourceType.Folder)
        {
            if (!Directory.Exists(source))
                return;
            entries = await LocalPlaylistLoader.LoadFolderAsync(
                source,
                _includeSubfoldersOnFolderLoad,
                readMetadataOnLoad: readMetaForLocal,
                cancellationToken,
                metadataProgress: metaProgress).ConfigureAwait(true);
            title = Path.GetFileName(source);
        }
        else
        {
            if (!File.Exists(source))
                return;
            var loaded = await LocalPlaylistLoader.LoadM3uAsync(
                source,
                readMetadataOnLoad: readMetaForLocal,
                cancellationToken,
                metadataProgress: metaProgress).ConfigureAwait(true);
            entries = loaded.entries;
            title = loaded.title;
        }

        // Load and try to keep the same current track if possible.
        await LoadPlaylistFromEntriesRuntimeAsync(entries, title, source, currentVideoId, cancellationToken);
    }

    private async Task LoadPlaylistFromEntriesRuntimeAsync(
        IReadOnlyList<PlaylistEntry> entries,
        string? title,
        string sourceKey,
        string? preserveCurrentVideoId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _playlistWindow?.SetLoadEnabled(false);
            SetStatusMessage("INFO", "Loading playlist...");

            // Changing playlist source should stop playback.
            if (_hasLoadedPlaylist &&
                !string.IsNullOrWhiteSpace(_loadedPlaylistId) &&
                !string.Equals(_loadedPlaylistId, sourceKey, StringComparison.OrdinalIgnoreCase))
            {
                try { _engine.Stop(); } catch { /* ignore */ }
                _pendingResumeSeconds = 0;
                _pendingResumeVideoId = null;
                ResetTimelineUiToStart();
            }

            _loadedPlaylistId = sourceKey;
            _playlistSourceText = sourceKey;
            _lastLocalPlaylistPath = sourceKey;
            _playlistIsCompound = false;
            RebuildPerItemPlaylistOriginsForCurrentPlaylist(title ?? sourceKey, sourceKey);

            _playlistCore.ReplaceEntries(entries ?? Array.Empty<PlaylistEntry>());
            // _currentEntries = BuildEffectivePlayOrder(_engine.GetCurrent(), _shuffleEnabled).ToList();

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(preserveCurrentVideoId))
            {
                var idx = FindIndexByVideoId(_playlistCore.Entries, preserveCurrentVideoId);
                if (idx >= 0 && idx < _playlistCore.Entries.Count)
                    startIndex = idx;
            }

            SetPlaylistTitle(title);
            var displayIndex = GetOriginalIndexByVideoId(preserveCurrentVideoId) ?? 0;
            await SetQueueListAsync(
                _playlistCore.Entries,
                selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex,
                cancellationToken: cancellationToken);
            // _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            _engine.SetBasePlayOrder(_playlistCore.Entries, startIndex: 0);
            _hasLoadedPlaylist = true;
            _playOrder.ClearRecentlyPlayed();
            _previousTrackHistory.Clear();
            _playOrder.ClearShuffleTape();
            _shuffleNextBuffer.Clear();
            UpdateRefreshEnabled();
            FocusPlaylistOnNowPlaying();
            UpdatePlaylistTitleDisplayForNowPlaying();

            SetStatusMessage("INFO", _playlistCore.Entries.Count == 0 ? "Playlist is empty." : $"Loaded {_playlistCore.Entries.Count} items.");
            SyncNowPlayingFromEngine();

            try
            {
                _playlistWindow?.SetSourceText(_playlistSourceText);
            }
            catch { /* ignore */ }

            ClearPlaylistSearchFilterAfterNewPlaylist();
        }
        finally
        {
            _playlistWindow?.SetLoadEnabled(true);
        }
    }

    private void ClearPlaylistSearchFilterAfterNewPlaylist()
    {
        try
        {
            _playlistWindow?.ClearPlaylistViewFilter();
            RequestPersistSnapshot();
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyAutoRefreshSelection()
    {
        if (_refreshTimer is null)
            return;

        var minutes = _autoRefreshMinutes;
        if (minutes is null)
        {
            _refreshTimer.Stop();
            return;
        }

        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes.Value);
        _refreshTimer.Start();
    }

    private int? GetSelectedAutoRefreshMinutes() => _autoRefreshMinutes;

    private void ApplyShuffleAndPreserveCurrent()
    {
        var currentVideoId = _engine.GetCurrent()?.VideoId;

        // _currentEntries = BuildEffectivePlayOrder(_engine.GetCurrent(), _shuffleEnabled).ToList();

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(currentVideoId))
        {
            var idx = FindIndexByVideoId(_playlistCore.Entries, currentVideoId);
            if (idx >= 0 && idx < _playlistCore.Entries.Count)
                startIndex = idx;
        }

        // Shuffle is only changing play order; avoid raising NowPlayingChanged (it can auto-center the playlist and
        // look like the list "jerks" even when the current track didn't change).
        _engine.SetQueue(_playlistCore.Entries, startIndex: _playlistCore.Entries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: false);

        // Playlist window always shows the original playlist order. Shuffle only changes engine play order — do not
        // clear/rebind the list when rows are already the same (that flashes the Playlist window).
        var displayIndex = GetOriginalIndexByVideoId(currentVideoId) ?? 0;
        SetQueueList(_playlistCore.Entries, selectedIndex: _playlistCore.Entries.Count == 0 ? -1 : displayIndex, forceFullRebuild: false);
    }

    private int? GetOriginalIndexByVideoId(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;
        var idx = _playlistCore.Entries.FindIndex(e => string.Equals(e.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx < _playlistCore.Entries.Count ? idx : null;
    }

    private static int FindIndexByVideoId(IReadOnlyList<PlaylistEntry> entries, string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void SetQueueList(IReadOnlyList<PlaylistEntry> entries, int selectedIndex, bool forceFullRebuild = true)
    {
        if (!forceFullRebuild && PlaylistDisplaysSameEntryOrder(entries))
        {
            UpdateNowPlayingFlag(_engine.GetCurrent());
            return;
        }

        // Clear playlist items (queue is cleared by caller when loading new playlist)
        _playlistItems.Clear();

        var entriesSnapshot = CopyEntriesSnapshot(entries);
        var isLocal = !IsYoutubeLikeSource(_lastPlaylistSourceType);
        var pad = Math.Max(1, entriesSnapshot.Length.ToString().Length);
        var current = _engine.GetCurrent();

        var baseIndex = 0;
        foreach (var e in entriesSnapshot)
        {
            baseIndex++;
            //var prefix = isLocal ? $"{baseIndex.ToString().PadLeft(pad, '0')}. " : null;
            var prefix = $"{baseIndex.ToString().PadLeft(pad, '0')}. ";
            var qi = new QueueItem(e, prefix)
            {
                BaseIndex = baseIndex - 1,
                //IsInQueue = _queuedNext.Any(q => string.Equals(q.Entry.VideoId, e.VideoId, StringComparison.OrdinalIgnoreCase)),
                IsInQueue = false,  // Always false — IsQueued is the authoritative flag
                IsQueued = false,
            };
            if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
                qi.IsUnavailable = true;
            if (_ageRestrictedVideoIds.Contains(qi.VideoId))
                qi.IsAgeRestricted = true;
            if (_premiumVideoIds.Contains(qi.VideoId))
                qi.IsPremium = true;
            if (current is { } cur && string.Equals(cur.VideoId, qi.VideoId, StringComparison.OrdinalIgnoreCase))
                qi.IsNowPlaying = true;
            _playlistItems.Add(qi);
        }

        _playlistWindow?.SetItemsSource(_queueItems, _playlistItems);
        try { _playlistWindow?.RefreshSortChoices(); } catch { /* ignore */ }
        if (selectedIndex >= 0)
            _playlistWindow?.ScrollToIndex(selectedIndex);
    }

    private async Task SetQueueListAsync(
        IReadOnlyList<PlaylistEntry> entries,
        int selectedIndex,
        bool forceFullRebuild = true,
        CancellationToken cancellationToken = default)
    {
        // Some refresh paths can call into this from non-UI threads; marshal to the window dispatcher.
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(
                async () => await SetQueueListAsync(entries, selectedIndex, forceFullRebuild, cancellationToken),
                DispatcherPriority.Normal).Task.Unwrap();
            return;
        }

        if (!forceFullRebuild && PlaylistDisplaysSameEntryOrder(entries))
        {
            UpdateNowPlayingFlag(_engine.GetCurrent());
            return;
        }

        _playlistItems.Clear(); // fox
        _queueItems.Clear();

        // Snapshot by index: List<T> enumerators are invalidated even by item assignment (metadata enrichment),
        // so never foreach over the live list here.
        var entriesSnapshot = CopyEntriesSnapshot(entries);
        var isLocal = _lastPlaylistSourceType != PlaylistSourceType.YouTube;
        var pad = Math.Max(1, entriesSnapshot.Length.ToString().Length);
        var current = _engine.GetCurrent();

        // Yield periodically so building huge playlists doesn't freeze the main window (e.g. when skipping metadata).
        const int batch = 200;
        var i = 0;
        var baseIndex = 0;
        for (var idx = 0; idx < entriesSnapshot.Length; idx++)
        {
            var e = entriesSnapshot[idx];
            cancellationToken.ThrowIfCancellationRequested();

            baseIndex++;
            //var prefix = isLocal ? $"{baseIndex.ToString().PadLeft(pad, '0')}. " : null;
            var prefix = $"{baseIndex.ToString().PadLeft(pad, '0')}. ";
            var qi = new QueueItem(e, prefix)
            {
                BaseIndex = baseIndex - 1,
                // IsInQueue = _queuedNext.Any(q => string.Equals(q.Entry.VideoId, e.VideoId, StringComparison.OrdinalIgnoreCase)),
                IsInQueue = false  // Always false — IsQueued is the authoritative flag
            };
            if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
                qi.IsUnavailable = true;
            if (_ageRestrictedVideoIds.Contains(qi.VideoId))
                qi.IsAgeRestricted = true;
            if (_premiumVideoIds.Contains(qi.VideoId))
                qi.IsPremium = true;
            if (current is { } cur && string.Equals(cur.VideoId, qi.VideoId, StringComparison.OrdinalIgnoreCase))
                qi.IsNowPlaying = true;

            _playlistItems.Add(qi); // fox

            i++;
            if (i % batch == 0)
                await Dispatcher.Yield(DispatcherPriority.Background);
        }

        _playlistWindow?.SetItemsSource(_queueItems, _playlistItems);
        if (selectedIndex >= 0)
        {
            _playlistWindow?.ScrollToIndex(selectedIndex);
        }
    }

    private bool PlaylistDisplaysSameEntryOrder(IReadOnlyList<PlaylistEntry> entries)
    {
        // Queue items are in a separate collection now — only compare playlist items
        if (_playlistItems.Count != entries.Count)
            return false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(_playlistItems[i].VideoId, entries[i].VideoId, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static PlaylistEntry[] CopyEntriesSnapshot(IReadOnlyList<PlaylistEntry> entries)
    {
        try
        {
            var n = entries.Count;
            var arr = new PlaylistEntry[n];
            for (var i = 0; i < n; i++)
                arr[i] = entries[i];
            return arr;
        }
        catch
        {
            // Fallback: best effort empty snapshot.
            return Array.Empty<PlaylistEntry>();
        }
    }

    private static PlaylistEntry CloneEntry(PlaylistEntry e) => e with { };

    private PlaylistEntry? ResolveNextTrack()
    {
        // DEBUG: log entry to ResolveNextTrack
        var dbgCurrentEntry = _engine.GetCurrent();
        var dbgCurrentIndex = _engine.CurrentIndex;
        var dbgCurrentVid = dbgCurrentEntry?.VideoId ?? "<null>";
        AppLog.Info($"ResolveNextTrack: CurrentIndex={dbgCurrentIndex} CurrentVideoId={dbgCurrentVid[..Math.Min(40, dbgCurrentVid.Length)]}... Shuffle={_shuffleEnabled} QueueItems={_queuedNext.Count}");

        // 1. Queue Priority: If queue has items, return first one that isn't currently playing
        if (_queuedNext.Count > 0)
        {
            // Collect bad IDs before removing, so we can sync the UI
            var badIds = _queuedNext
                .Where(q => q.Id != _playingQueuedInstanceId &&
                            (_unavailableVideoIds.Contains(q.Entry.VideoId) ||
                             _ageRestrictedVideoIds.Contains(q.Entry.VideoId) ||
                             _premiumVideoIds.Contains(q.Entry.VideoId)))
                .Select(q => q.Entry.VideoId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Purge known-bad items from the internal queue
            _queuedNext.RemoveAll(q => badIds.Contains(q.Entry.VideoId));

            // Sync the visual queue list box — remove the same items from _queueItems
            foreach (var qi in _queueItems.Where(qi => badIds.Contains(qi.VideoId)).ToList())
            {
                _queueItems.Remove(qi);
            }

            // Return first remaining playable item (skip the one currently playing)
            foreach (var q in _queuedNext)
            {
                if (q.Id != _playingQueuedInstanceId)
                {
                    var nextTrack = q.Entry;
                    return nextTrack;
                }
            }
        }
        else
        {
            // --- Shuffle Mode ---
            if (_shuffleEnabled)
            {
                var currentId = _engine.GetCurrent()?.VideoId;

                // If the user previously navigated back in shuffle history, move forward within the tape first.
                if (_playOrder.ShuffleTapeCursor >= 0 && _playOrder.ShuffleTapeCursor < _playOrder.ShuffleTapeVideoIds.Count - 1)
                {
                    // Skip over any tape entries that no longer exist in the playlist.
                    for (var i = _playOrder.ShuffleTapeCursor + 1; i < _playOrder.ShuffleTapeVideoIds.Count; i++)
                    {
                        var vid = _playOrder.ShuffleTapeVideoIds[i];
                        var e = _playlistCore.Entries.FirstOrDefault(x => string.Equals(x.VideoId, vid, StringComparison.OrdinalIgnoreCase));
                        if (e is not null)
                            return e;
                    }
                }

                // Build candidate list: exclude current track, recently played, and unavailable tracks
                var candidates = _playlistCore.Entries
                    .Where(e => !string.Equals(e.VideoId, currentId, StringComparison.OrdinalIgnoreCase)
                             && !_playOrder.RecentlyPlayedContains(e.VideoId)
                             && !_unavailableVideoIds.Contains(e.VideoId)
                             && !_ageRestrictedVideoIds.Contains(e.VideoId)
                             && !_premiumVideoIds.Contains(e.VideoId))
                    .ToList();

                AppLog.Info($"ResolveNextTrack[SHUFFLE]: currentId={currentId?[..Math.Min(40, currentId.Length)] ?? "<null>"} candidates={candidates.Count} recentlyPlayed={_playOrder.RecentlyPlayedCount}");

                if (candidates.Count == 0)
                {
                    // All tracks excluded — reset recently played and try again
                    _playOrder.ClearRecentlyPlayed();
                    candidates = _playlistCore.Entries
                        .Where(e => !string.Equals(e.VideoId, currentId, StringComparison.OrdinalIgnoreCase)
                                 && !_unavailableVideoIds.Contains(e.VideoId)
                                 && !_ageRestrictedVideoIds.Contains(e.VideoId)
                                 && !_premiumVideoIds.Contains(e.VideoId))
                        .ToList();
                    AppLog.Info($"ResolveNextTrack[SHUFFLE]: reset recentlyPlayed, new candidates={candidates.Count}");
                }

                if (candidates.Count == 0) return null; // No playable tracks

                // Pop the next track from the shuffle buffer (pre-populated with upcoming tracks)
                PlaylistEntry? nextTrack = null;
                if (_shuffleNextBuffer.Count > 0)
                {
                    nextTrack = _shuffleNextBuffer.Dequeue();
                }

                // Refill buffer if needed (keep it at 3 items)
                while (_shuffleNextBuffer.Count < 3 && candidates.Count > 0)
                {
                    var rndIndex = _shuffleRandom.Next(candidates.Count);
                    var entry = candidates[rndIndex];
                    _shuffleNextBuffer.Enqueue(entry);
                    candidates.RemoveAt(rndIndex); // don't pick the same track again
                }

                AppLog.Info($"ResolveNextTrack[SHUFFLE]: popped={nextTrack?.VideoId[..Math.Min(40, nextTrack.VideoId.Length)] ?? "<empty>"}... buffer={_shuffleNextBuffer.Count} remaining");
                return nextTrack;
            }

            // --- Sequential Mode ---
            int nextIndex = _engine.CurrentIndex + 1;

            if (_repeatMode == RepeatMode.Playlist && nextIndex >= _playlistCore.Entries.Count)
            {
                nextIndex = 0; // Loop back to start
            }

            if (nextIndex >= _playlistCore.Entries.Count)
            {
                AppLog.Info($"ResolveNextTrack[SEQ]: nextIndex={nextIndex} >= entries={_playlistCore.Entries.Count} -> returning null");
                return null; // End of list (Repeat:None)
            }

            var sequentialNextTrack = _playlistCore.Entries[nextIndex];
            AppLog.Info($"ResolveNextTrack[SEQ]: nextIndex={nextIndex} nextVideoId={sequentialNextTrack.VideoId[..Math.Min(40, sequentialNextTrack.VideoId.Length)]}... (CURRENT index={dbgCurrentIndex})");
            return sequentialNextTrack;
        }

        AppLog.Info("ResolveNextTrack: returning null");
        return null; // something went wrong
    }

    /// <summary>
    /// Non-mutating "peek next" used for background operations (prefetch/warm and lyrics preheating).
    /// Must NOT dequeue or otherwise advance shuffle/queue state.
    /// </summary>
    private PlaylistEntry? PeekNextTrackForPreheatOrPrefetch()
    {
        try
        {
            // Queue takes precedence over shuffle/sequential.
            if (_queuedNext.Count > 0)
            {
                foreach (var q in _queuedNext)
                {
                    if (q.Id != _playingQueuedInstanceId)
                        return q.Entry;
                }
            }

            if (_shuffleEnabled)
            {
                // If we're in the middle of the shuffle tape, Next will be a history-forward move.
                // Suppress prefetch/preheat (lyrics + audio) for history traversal.
                if (_playOrder.ShuffleTapeCursor >= 0 && _playOrder.ShuffleTapeCursor < _playOrder.ShuffleTapeVideoIds.Count - 1)
                    return null;

                if (_shuffleNextBuffer.Count > 0)
                    return _shuffleNextBuffer.Peek();
                return null;
            }

            // Sequential.
            var nextIndex = _engine.CurrentIndex + 1;
            if (_repeatMode == RepeatMode.Playlist && nextIndex >= _playlistCore.Entries.Count)
                nextIndex = 0;
            if (nextIndex < 0 || nextIndex >= _playlistCore.Entries.Count)
                return null;
            return _playlistCore.Entries[nextIndex];
        }
        catch
        {
            return null;
        }
    }

    private QueueItem CreateQueueItemForQueuedInstance(QueuedInstance qe, int ordinal)
    {
        var qi = new QueueItem(qe.Entry)
        {
            IsQueued = true,
            QueueOrdinal = ordinal,
            QueueInstanceId = qe.Id,
        };
        if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
            qi.IsUnavailable = true;
        if (_ageRestrictedVideoIds.Contains(qi.VideoId))
            qi.IsAgeRestricted = true;
        if (_premiumVideoIds.Contains(qi.VideoId))
            qi.IsPremium = true;
        if (_engine.GetCurrent() is { } cur && cur.VideoId.Equals(qi.VideoId))
            qi.IsNowPlaying = true;
        return qi;
    }

    private void UpdateQueueOrdinals()
    {
        for (var i = 0; i < _queueItems.Count; i++)
        {
            if (_queueItems[i].IsQueued)
                _queueItems[i].QueueOrdinal = i + 1;
        }
    }

    private void RebuildQueueUiFromBackingList()
    {
        _queueItems.Clear();
        var n = 0;
        foreach (var qe in _queuedNext)
        {
            n++;
            var qi = new QueueItem(qe.Entry)
            {
                IsQueued = true,
                QueueOrdinal = n,
                QueueInstanceId = qe.Id,
            };
            if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
                qi.IsUnavailable = true;
            if (_ageRestrictedVideoIds.Contains(qi.VideoId))
                qi.IsAgeRestricted = true;
            if (_premiumVideoIds.Contains(qi.VideoId))
                qi.IsPremium = true;
            if (_engine.GetCurrent() is { } cur && cur.VideoId.Equals(qi.VideoId))
                qi.IsNowPlaying = true;
            _queueItems.Add(qi);
        }
        _playlistWindow?.RefreshQueueView();
    }

    private void ClearQueue()
    {
        if (_queuedNext.Count == 0) return;
        _queuedNext.Clear();
        _queueItems.Clear();
        _playlistWindow?.RefreshQueueView();
    }

    private void AddToQueue(PlaylistEntry entry)
    {
        var cloned = CloneEntry(entry);
        var instance = new QueuedInstance(Guid.NewGuid(), cloned);
        _queuedNext.Add(instance);

        // Append to queue collection — O(1), no shift of playlist items
        var qi = new QueueItem(cloned)
        {
            IsQueued = true,
            QueueOrdinal = _queuedNext.Count,
            QueueInstanceId = instance.Id,
        };
        if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
            qi.IsUnavailable = true;
        if (_ageRestrictedVideoIds.Contains(qi.VideoId))
            qi.IsAgeRestricted = true;
        if (_premiumVideoIds.Contains(qi.VideoId))
            qi.IsPremium = true;
        if (_engine.GetCurrent() is { } cur && cur.VideoId.Equals(qi.VideoId))
            qi.IsNowPlaying = true;

        _queueItems.Add(qi);
        _playlistWindow?.RefreshQueueView();
    }

    private void QueueNextByVideoId(string videoId)
    {
        var entry = _playlistCore.Entries.FirstOrDefault(e => string.Equals(e.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        // Check if already in queue
        if (_queuedNext.Any(q => q.Entry.VideoId == entry.VideoId)) return;

        var id = Guid.NewGuid();
        var queuedInstance = new QueuedInstance(id, entry);

        _queuedNext.Insert(0, queuedInstance);
        _queueItems.Insert(0, new QueueItem(queuedInstance.Entry));

        UpdateQueueOrdinals();
        _playlistWindow?.RefreshQueueView();
    }

    private void QueueLastByVideoId(string videoId)
    {
        var entry = _playlistCore.Entries.FirstOrDefault(e => string.Equals(e.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        // Check if already in queue
        if (_queuedNext.Any(q => q.Entry.VideoId == entry.VideoId)) return;

        var id = Guid.NewGuid();
        var queuedInstance = new QueuedInstance(id, entry);

        _queuedNext.Add(queuedInstance);
        _queueItems.Add(new QueueItem(queuedInstance.Entry));

        UpdateQueueOrdinals();
        _playlistWindow?.RefreshQueueView();
    }

    private bool RemoveQueuedInstance(Guid id)
    {
        // Remove from backing list
        var listIndex = -1;
        for (var i = 0; i < _queuedNext.Count; i++)
        {
            if (_queuedNext[i].Id == id)
            {
                _queuedNext.RemoveAt(i);
                listIndex = i;
                break;
            }
        }
        if (listIndex < 0) return false;

        // Remove from queue collection only (small collection, fast)
        var uiIndex = -1;
        for (var i = 0; i < _queueItems.Count; i++)
        {
            if (_queueItems[i].IsQueued && _queueItems[i].QueueInstanceId == id)
            {
                uiIndex = i;
                break;
            }
        }
        if (uiIndex >= 0)
            _queueItems.RemoveAt(uiIndex);

        UpdateQueueOrdinals();
        _playlistWindow?.RefreshQueueView();
        return true;
    }

    private void UpdateNowPlayingFlag(PlaylistEntry? now)
    {
        // Highlight in the queue (if the track is queued)
        foreach (var qi in _queueItems)
        {
            qi.IsNowPlaying = now is not null && qi.Entry is not null && qi.Entry.VideoId.Equals(now.VideoId);
        }

        // Highlight in the playlist only if the track is NOT currently in the queue
        var isQueued = now is not null && _queuedNext.Any(q => string.Equals(q.Entry.VideoId, now.VideoId, StringComparison.OrdinalIgnoreCase));

        foreach (var qi in _playlistItems)
        {
            qi.IsNowPlaying = !isQueued && now is not null && string.Equals(qi.VideoId, now.VideoId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool LooksLikeUnavailableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        return title.Contains("private video", StringComparison.OrdinalIgnoreCase)
               || title.Contains("deleted video", StringComparison.OrdinalIgnoreCase)
               || title.Contains("[private video]", StringComparison.OrdinalIgnoreCase)
               || title.Contains("[deleted video]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAgeRestricted(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age restricted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age verification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCancelled(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("operation canceled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("operation cancelled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("task was cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateNowPlayingText(string? extraDetail = null)
    {
        var status = string.IsNullOrWhiteSpace(_nowPlayingStatus) ? "STOPPED" : _nowPlayingStatus.Trim().ToUpperInvariant();
        // AppLog.Info($"UpdateNowPlayingText: status={status}, entry={_nowPlayingEntry?.VideoId ?? "(null)"}, lyricsEnabled={_lyricsEnabled}, hasLyrics={_lyricsService.Manager.HasLyrics}, lineCount={_lyricsService.Manager.LineCount}, isPlaying={_engine.IsPlaying}, position={_engine.CurrentPositionSeconds:F2}s");
        if (NowPlayingStatusRun is not null)
        {
            // Don't show lyric line/status when lyrics window is open — it already displays lyrics.
            if (_lyricsEnabled && _lyricsService.Manager.HasLyrics && _engine.IsPlaying && (_lyricsWindow is null || !_lyricsWindow.IsVisible))
            {
                NowPlayingStatusRun.Text = $"[>]";
            }
            else
            {
                NowPlayingStatusRun.Text = $"[{status}] ";
            }
        }

        string title;
        if (_nowPlayingEntry is null)
        {
            title = "Not playing";
        }
        else
        {
            // Check for lyrics override (Normal/Compact modes show current lyric line).
            // Only show lyric lines in main window when lyrics window is NOT open — it already displays them.
            if (_lyricsEnabled && _lyricsService.Manager.HasLyrics && _engine.IsPlaying && (_lyricsWindow is null || !_lyricsWindow.IsVisible))
            {
                var lyricLine = _lyricsService.Manager.GetCurrentLine(_engine.CurrentPositionSeconds);
                if (!string.IsNullOrEmpty(lyricLine))
                    title = lyricLine;
                else
                    title = $"{_nowPlayingEntry.Title}{(string.IsNullOrWhiteSpace(_nowPlayingEntry.Channel) ? "" : $" \u2014 {_nowPlayingEntry.Channel}")}";
            }
            else
            {
                // Lyrics window is open — highlight the current line there instead of the title.
                if (_lyricsEnabled && _lyricsService.Manager.HasLyrics && _engine.IsPlaying && _lyricsWindow is { IsVisible: true })
                    _lyricsWindow.RefreshCurrentLine();

                title = $"{_nowPlayingEntry.Title}{(string.IsNullOrWhiteSpace(_nowPlayingEntry.Channel) ? "" : $" \u2014 {_nowPlayingEntry.Channel}")}";
            }

            if (!string.IsNullOrWhiteSpace(extraDetail) && (status is "ERROR" or "AGE" or "UNAVAILABLE" or "PREMIUM" or "COOKIE" or "FETCHING"))
            {
                var shortMsg = extraDetail.Trim();
                if (shortMsg.Length > 80)
                    shortMsg = shortMsg[..80] + "\u2026";
                title = $"{title} ({shortMsg})";
            }
        }

        if (NowPlayingTitleRun is not null)
            NowPlayingTitleRun.Text = title;
        else
            NowPlayingTextBlock.Text = $"[{status}] {title}";

        try
        {
            // Keep the main window title synced when using "Current song" title mode.
            ApplyMainWindowTitleFromSettings(GetAppTitleBase());
        }
        catch { /* ignore */ }

        try { UpdateExportMp3ControlsEnabled(); } catch { /* ignore */ }
    }

    private static bool CanExportCurrentTrackToMp3(PlaybackEngine engine, string? lameEncoderPath)
    {
        try
        {
            if (engine.GetCurrent() is not PlaylistEntry cur)
                return false;
            if (!engine.TryGetYoutubeDiskCachePath(cur, out _))
                return false;
            return LameEncoderLocator.TryResolve(lameEncoderPath, out _);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateExportMp3ControlsEnabled()
    {
        var can = CanExportCurrentTrackToMp3(_engine, _lameEncoderPath);
        try { MainExportToMp3MenuItem.IsEnabled = can; } catch { /* ignore */ }
        try { ExportMp3Button.IsEnabled = can; } catch { /* ignore */ }
        try { CompactExportMp3Button.IsEnabled = can; } catch { /* ignore */ }
        try { ExportMp3UltraButtonInline.IsEnabled = can; } catch { /* ignore */ }
    }

    private void SelectAndScrollToNowPlaying(PlaylistEntry? entry)
    {
        if (entry is null || _playlistWindow is null) return;

        try
        {
            _playlistWindow.CenterNowPlaying(entry);
        }
        catch { /* ignore */ }
    }

    private void CapturePlaylistWindowBounds()
    {
        try
        {
            if (_playlistWindow is null)
                return;

            var state = _playlistWindow.WindowState;
            var bounds = state == WindowState.Normal
                ? new Rect(_playlistWindow.Left, _playlistWindow.Top, _playlistWindow.Width, _playlistWindow.Height)
                : _playlistWindow.RestoreBounds;

            _lastPlaylistBounds = bounds;
            _lastPlaylistWindowState = state;
        }
        catch
        {
            // ignore
        }
    }

    private void SetPlaylistTitle(string? title)
    {
        _playlistTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (PlaylistTitleTextBlock is not null)
            PlaylistTitleTextBlock.Text = _playlistTitle ?? "(no playlist)";
    }

    private void SetBasePlaylistOrigin(string? label, string? source)
    {
        var l = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        var s = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        _playlistCore.BaseOrigin = (string.IsNullOrWhiteSpace(l) && string.IsNullOrWhiteSpace(s))
            ? null
            : new PlaylistOriginInfo(l ?? "", s ?? "");
    }

    private void RebuildPerItemPlaylistOriginsForCurrentPlaylist(string? baseLabel, string? baseSource)
    {
        try
        {
            SetBasePlaylistOrigin(baseLabel, baseSource);
            _playlistCore.OriginByVideoId.Clear();
            var b = _playlistCore.BaseOrigin;
            if (b is null)
                return;
            foreach (var e in _playlistCore.Entries)
                _playlistCore.OriginByVideoId[e.VideoId] = b;
        }
        catch { /* ignore */ }
    }

    private void UpdatePlaylistTitleDisplayForNowPlaying()
    {
        try
        {
            if (PlaylistTitleTextBlock is null)
                return;

            var cur = _nowPlayingEntry;

            // Check if lyrics are active (enabled + loaded + playing).
            bool lyricsActive = _lyricsEnabled && _lyricsService.Manager.HasLyrics && _engine.IsPlaying && (_lyricsWindow is null || !_lyricsWindow.IsVisible);

            // Resolve the origin label: per-video origin > base origin > playlist title.
            string? origin = cur is not null &&
                _playlistCore.OriginByVideoId.TryGetValue(cur.VideoId, out var info) &&
                !string.IsNullOrWhiteSpace(info?.Label)
                ? info.Label
                : (_playlistCore.BaseOrigin?.Label ?? _playlistTitle ?? "");

            // Show "<origin> - <title>" on the origin line when lyrics are active; otherwise normal display.
            PlaylistTitleTextBlock.Text = (lyricsActive && cur is not null)
                ? $"{origin} : {cur.Channel} - {cur.Title}"
                : (origin ?? _playlistTitle ?? "(no playlist)");
        }
        catch { /* ignore */ }
    }

    private bool TryGetNowPlayingOrigin(out PlaylistOriginInfo origin)
    {
        origin = new PlaylistOriginInfo(Label: _playlistTitle ?? "", Source: _playlistSourceText ?? "");
        try
        {
            var cur = _nowPlayingEntry;
            if (cur is null)
                return false;
            if (_playlistCore.OriginByVideoId.TryGetValue(cur.VideoId, out var o))
            {
                origin = o;
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static string? NormalizeYoutubePlaylistUrlOrNull(string? src)
    {
        var t = (src ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            return null;
        try
        {
            if (Uri.TryCreate(t, UriKind.Absolute, out var u) &&
                (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                return u.ToString();
        }
        catch { /* ignore */ }

        // Treat as playlist ID.
        if (t.Length >= 10 && !t.Contains(' ') && !t.Contains('/') && !t.Contains('\\'))
            return $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(t)}";
        return null;
    }

    private static void TryOpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static void TryOpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }

    private void MainPlaybackInfoStack_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try { UpdateExportMp3ControlsEnabled(); } catch { /* ignore */ }
    }

    private static string BuildComparableNameKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        try
        {
            Span<char> buf = stackalloc char[Math.Min(256, s.Length)];
            var n = 0;
            foreach (var ch in s.Trim())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (n >= buf.Length)
                        break;
                    buf[n++] = char.ToLowerInvariant(ch);
                }
            }
            return n == 0 ? "" : new string(buf[..n]);
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksLikeUploaderHandle(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length < 3)
            return false;
        if (t.Contains(' '))
            return false;
        var hasLetter = false;
        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        foreach (var ch in t)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                if (char.IsUpper(ch)) hasUpper = true;
                if (char.IsLower(ch)) hasLower = true;
            }
            if (char.IsDigit(ch)) hasDigit = true;
        }
        return hasLetter && (hasDigit || (hasUpper && hasLower)) && t.Length >= 6;
    }

    private static string NormalizeTopicChannel(string? channel)
    {
        var c = (channel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(c))
            return "";
        try
        {
            c = System.Text.RegularExpressions.Regex.Replace(
                c,
                @"\s*-\s*topic\s*$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }
        catch { /* ignore */ }
        return c;
    }

    private static (string exportTitle, string? exportArtist) InferExportTagsFromNowPlaying(PlaylistEntry cur)
    {
        // Goal: don't poison exported MP3 tags by blindly using the YouTube channel/uploader as Artist.
        // Prefer parsing "Artist - Title" from the visible title when possible; otherwise fall back to a cleaned Topic channel.
        var rawTitle = (cur.Title ?? "").Trim();
        var rawChannel = (cur.Channel ?? "").Trim();
        var topicChannel = NormalizeTopicChannel(rawChannel);

        // Try structural split on common separators.
        try
        {
            var s = rawTitle;
            // Normalize mixed dash spacing for splitting.
            if (!string.IsNullOrWhiteSpace(s))
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[-–—]\s*", " - ");

            var parts = s.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                // "Artist - Track"
                var a = parts[0].Trim();
                var t = parts[1].Trim();

                // "Artist - Track - Topic" or "Artist - Track - Uploader"
                if (parts.Length >= 3)
                {
                    var third = parts[2].Trim();
                    var kThird = BuildComparableNameKey(third);
                    var kChan = BuildComparableNameKey(topicChannel);
                    if (string.Equals(third, "Topic", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(kThird) && !string.IsNullOrWhiteSpace(kChan) && kThird == kChan) ||
                        LooksLikeUploaderHandle(third))
                    {
                        // ignore third chunk
                    }
                    else
                    {
                        // Sometimes title legitimately has 3 chunks; keep as part of title.
                        t = $"{t} {third}".Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(t))
                    return (t, a);
            }
        }
        catch { /* ignore */ }

        // If channel is a Topic channel, it's usually a good artist hint.
        if (!string.IsNullOrWhiteSpace(topicChannel) && !LooksLikeUploaderHandle(topicChannel))
            return (rawTitle, topicChannel);

        // Otherwise, keep title and omit artist rather than saving a likely-uploader handle.
        return (rawTitle, null);
    }

    private async void ExportCurrentTrackToMp3_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_engine.GetCurrent() is not PlaylistEntry cur)
                return;

            if (!_engine.TryGetYoutubeDiskCachePath(cur, out var cachePath) || string.IsNullOrWhiteSpace(cachePath))
            {
                TopmostMessageBox.Show(
                    "There is no finished on-disk cache for this track yet. Let playback finish caching, then try again.",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!LameEncoderLocator.TryResolve(_lameEncoderPath, out _))
            {
                TopmostMessageBox.Show(
                    "LAME (libmp3lame) was not found.\n\nPlace libmp3lame.64.dll next to the app or set a custom DLL under Options → Export.",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var defaultName = FileNameSanitizer.MakeSafeFileName(cur.Title, "track") + ".mp3";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export to MP3",
                Filter = "MP3 (*.mp3)|*.mp3|All files (*.*)|*.*",
                DefaultExt = ".mp3",
                AddExtension = true,
                FileName = defaultName,
                OverwritePrompt = true,
            };

            using var top = new TopmostDialogOwner(this);
            if (dlg.ShowDialog(top.OwnerWindow) != true)
                return;

            var destPath = dlg.FileName;
            var modeNorm = SettingsStore.NormalizeMp3ExportEncodingMode(_mp3ExportEncodingMode);
            var req = new Mp3ExportRequest(
                string.Equals(modeNorm, "Cbr", StringComparison.OrdinalIgnoreCase) ? Mp3ExportEncodingMode.Cbr : Mp3ExportEncodingMode.Vbr,
                _mp3ExportCbrQualityIndex,
                _mp3ExportVbrQualityIndex);

            var (title, channel) = InferExportTagsFromNowPlaying(cur);
            var videoId = cur.VideoId;
            var lamePath = _lameEncoderPath;

            ShowInfoToast("Exporting MP3…", ms: 2000);
            await Task.Run(() =>
                    Mp3ExportService.ExportFileToMp3(cachePath, destPath, title, channel, req, lamePath))
                .ConfigureAwait(true);

            ShowInfoToast($"Exported: {Path.GetFileName(destPath)}");

            if (_mp3ExportReplacePlaylistEntryAfterExport)
                ReplaceYoutubePlaylistRowsWithLocalMp3(videoId, destPath, cur, inferredTitle: title, inferredArtist: channel);
        }
        catch (Exception ex)
        {
            try
            {
                TopmostMessageBox.Show(
                    ex.Message,
                    "Export to MP3",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
    }

    private void ReplaceYoutubePlaylistRowsWithLocalMp3(string oldVideoId, string mp3Path, PlaylistEntry sourceEntry, string? inferredTitle = null, string? inferredArtist = null)
    {
        if (string.IsNullOrWhiteSpace(oldVideoId))
            return;

        string full;
        try { full = Path.GetFullPath(mp3Path); }
        catch { return; }

        var newEntry = new PlaylistEntry(
            VideoId: LocalPlaylistLoader.CreateLocalIdFromPath(full),
            Title: string.IsNullOrWhiteSpace(inferredTitle) ? sourceEntry.Title : inferredTitle,
            Channel: string.IsNullOrWhiteSpace(inferredArtist) ? sourceEntry.Channel : inferredArtist,
            DurationSeconds: sourceEntry.DurationSeconds,
            WebpageUrl: full,
            RequiresCookies: false);

        LyricsCache.TryMigrateYoutubeLyricsToLocalEntry(oldVideoId, newEntry.VideoId);

        if (_playlistCore.OriginByVideoId.TryGetValue(oldVideoId, out var origin))
        {
            _playlistCore.OriginByVideoId.Remove(oldVideoId);
            _playlistCore.OriginByVideoId[newEntry.VideoId] = origin;
        }

        for (var i = 0; i < _playlistCore.Entries.Count; i++)
        {
            if (string.Equals(_playlistCore.Entries[i].VideoId, oldVideoId, StringComparison.OrdinalIgnoreCase))
                _playlistCore.Entries[i] = newEntry;
        }

        for (var i = 0; i < _queuedNext.Count; i++)
        {
            var q = _queuedNext[i];
            if (string.Equals(q.Entry.VideoId, oldVideoId, StringComparison.OrdinalIgnoreCase))
                _queuedNext[i] = new QueuedInstance(q.Id, newEntry);
        }

        var curIdx = _engine.CurrentIndex;
        _engine.SetQueue(_playlistCore.Entries, startIndex: curIdx >= 0 ? curIdx : 0, raiseNowPlayingChanged: true);
        try { _nowPlayingEntry = _engine.GetCurrent(); } catch { /* ignore */ }
        SetQueueList(_playlistCore.Entries, selectedIndex: -1, forceFullRebuild: true);
        UpdatePlaylistTitleDisplayForNowPlaying();
        RequestPersistSnapshot();

        RefreshLyricsAfterExportReplace(newEntry);
    }

    private void PlaylistTitle_OpenOriginMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetNowPlayingOrigin(out var origin))
                return;
            var src = (origin.Source ?? "").Trim();
            var yt = NormalizeYoutubePlaylistUrlOrNull(src);
            if (!string.IsNullOrWhiteSpace(yt))
            {
                TryOpenInBrowser(yt);
                return;
            }
            if (!string.IsNullOrWhiteSpace(src))
                TryOpenInExplorer(src);
        }
        catch { /* ignore */ }
    }

    private void ApplySavedPlaylistOriginsIfAny(SavedPlaylist pl, IReadOnlyList<PlaylistEntry> entries)
    {
        try
        {
            var baseLabel = string.IsNullOrWhiteSpace(pl?.Name) ? null : pl!.Name.Trim();
            var baseSource = string.IsNullOrWhiteSpace(pl?.Source) ? null : pl!.Source.Trim();
            SetBasePlaylistOrigin(baseLabel, baseSource);

            _playlistCore.OriginByVideoId.Clear();

            var legacy = pl?.OriginByVideoId;
            var origins = pl?.OriginInfoByVideoId;
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(baseLabel))
                distinct.Add(baseLabel);

            foreach (var e in entries ?? Array.Empty<PlaylistEntry>())
            {
                if (e is null || string.IsNullOrWhiteSpace(e.VideoId))
                    continue;

                string label = baseLabel ?? "";
                string src = baseSource ?? "";

                if (origins is not null && origins.TryGetValue(e.VideoId, out var oi) && oi is not null)
                {
                    if (!string.IsNullOrWhiteSpace(oi.Label)) label = oi.Label.Trim();
                    if (!string.IsNullOrWhiteSpace(oi.Source)) src = oi.Source.Trim();
                }
                else if (legacy is not null && legacy.TryGetValue(e.VideoId, out var l0))
                {
                    if (!string.IsNullOrWhiteSpace(l0)) label = l0.Trim();
                }

                if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(src))
                {
                    _playlistCore.OriginByVideoId[e.VideoId] = new PlaylistOriginInfo(label, src);
                    if (!string.IsNullOrWhiteSpace(label))
                        distinct.Add(label);
                }
            }

            _playlistIsCompound = distinct.Count > 1;
        }
        catch { /* ignore */ }
    }

    private static ScrollViewer? FindListBoxScrollViewer(System.Windows.Controls.ListBox listBox)
    {
        // Prefer the ListBox template's scrollviewer if present.
        if (listBox.Template?.FindName("PART_ScrollViewer", listBox) is ScrollViewer templated)
            return templated;

        return FindDescendant<ScrollViewer>(listBox);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t)
            return t;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void HandlePrefetchTagged(PlaybackPrefetchTag tag)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tag.VideoId))
                return;

            if (string.Equals(tag.Category, "Premium", StringComparison.OrdinalIgnoreCase))
            {
                _premiumVideoIds.Add(tag.VideoId);
                foreach (var qi in _queueItems)
                {
                    if (qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsPremium = true;
                }
                foreach (var qi in _playlistItems)
                {
                    if (!qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsPremium = true;
                }
            }
            else if (string.Equals(tag.Category, "AgeRestricted", StringComparison.OrdinalIgnoreCase))
            {
                _ageRestrictedVideoIds.Add(tag.VideoId);
                foreach (var qi in _queueItems)
                {
                    if (qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsAgeRestricted = true;
                }
                foreach (var qi in _playlistItems)
                {
                    if (!qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsAgeRestricted = true;
                }
            }
            else
            {
                _unavailableVideoIds.Add(tag.VideoId);
                foreach (var qi in _queueItems)
                {
                    if (qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsUnavailable = true;
                }
                foreach (var qi in _playlistItems)
                {
                    if (!qi.IsQueued && string.Equals(qi.VideoId, tag.VideoId, StringComparison.OrdinalIgnoreCase))
                        qi.IsUnavailable = true;
                }
            }

            try
            {
                AppLog.Info($"Prefetch: tagged skip-before-play ({tag.Category}) VideoId={tag.VideoId}", AppLogInfoTier.Crucial);
            }
            catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    private void HandlePlaybackFailed(PlaylistEntry entry, string message)
    {
        try
        {
            var t = string.IsNullOrWhiteSpace(entry.Title) ? "" : $" \"{entry.Title}\"";
            AppLog.Warn($"Track skipped (unavailable/age/premium/DRM); loading next. VideoId={entry.VideoId}{t}.");
        }
        catch { /* ignore */ }

        var isPremium = LooksLikePremiumRequired(message);
        var isAge = !isPremium && LooksLikeAgeRestricted(message);

        if (isPremium)
        {
            _premiumVideoIds.Add(entry.VideoId);
            foreach (var qi in _queueItems)
            {
                if (qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsPremium = true;
            }
            foreach (var qi in _playlistItems)
            {
                if (!qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsPremium = true;
            }
            _nowPlayingStatus = "PREMIUM";
        }
        else if (isAge)
        {
            _ageRestrictedVideoIds.Add(entry.VideoId);
            foreach (var qi in _queueItems)
            {
                if (qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsAgeRestricted = true;
            }
            foreach (var qi in _playlistItems)
            {
                if (!qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsAgeRestricted = true;
            }
            _nowPlayingStatus = "AGE";
        }
        else
        {
            _unavailableVideoIds.Add(entry.VideoId);
            foreach (var qi in _queueItems)
            {
                if (qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsPremium = true;
            }
            foreach (var qi in _playlistItems)
            {
                if (!qi.IsQueued && string.Equals(qi.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    qi.IsPremium = true;
            }
            _nowPlayingStatus = "UNAVAILABLE";
        }

        _nowPlayingEntry = entry;
        _playingQueuedInstanceId = null;
        UpdateNowPlayingText(extraDetail: message);

        if (_engine.PlayOrder.Count == 0)
            return;

        // If we're already at the end, avoid looping on the last item.
        if (_engine.CurrentIndex >= _engine.PlayOrder.Count - 1)
        {
            _nowPlayingStatus = isPremium ? "PREMIUM" : (isAge ? "AGE" : "UNAVAILABLE");
            _nowPlayingEntry = entry;
            UpdateNowPlayingText(extraDetail: "End of playlist");
            return;
        }

        _ = _engine.NextAsync();
    }

    private static bool LooksLikePremiumRequired(string? message)
        => YtDlpClient.LooksLikePremiumRequired(message);

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private void UpdateDurationUi(int? durationSeconds)
    {
        if (durationSeconds is not int dur || dur <= 0)
        {
            DurationTextBlock.Text = "--:--";
            SeekSlider.IsEnabled = false;
            SeekSlider.Minimum = 0;
            SeekSlider.Maximum = 1;
            SeekSlider.Value = 0;
            try { UpdateSeekBufferedVisuals(); } catch { /* ignore */ }
            try { UpdateUltraSeekOverlay(_engine.CurrentPositionSeconds, durationSeconds); } catch { /* ignore */ }
            return;
        }

        DurationTextBlock.Text = FormatTime(dur);
        SeekSlider.IsEnabled = true;
        SeekSlider.Minimum = 0;
        SeekSlider.Maximum = dur;

        // If we have a pending resume position (paused-on-close), reflect it in the UI once duration becomes known.
        // Otherwise reset the thumb — leaving the previous track's value makes the next track look like it "starts mid-way".
        try
        {
            var cur = _engine.GetCurrent();
            if (cur is not null &&
                _pendingResumeSeconds > 1 &&
                !string.IsNullOrWhiteSpace(_pendingResumeVideoId) &&
                string.Equals(cur.VideoId, _pendingResumeVideoId, StringComparison.OrdinalIgnoreCase) &&
                !_engine.IsPlaying &&
                !_isSeeking)
            {
                var clamped = Math.Max(0, Math.Min(_pendingResumeSeconds, dur));
                _ignoreSeekBar = true;
                try { SeekSlider.Value = clamped; }
                finally { _ignoreSeekBar = false; }
                ElapsedTextBlock.Text = FormatTime(clamped);
            }
            else
            {
                _ignoreSeekBar = true;
                try { SeekSlider.Value = 0; }
                finally { _ignoreSeekBar = false; }
                if (!_isSeeking)
                    ElapsedTextBlock.Text = FormatTime(0);
            }
        }
        catch { /* ignore */ }

        try { UpdateSeekBufferedVisuals(); } catch { /* ignore */ }
        try { UpdateUltraSeekOverlay(_engine.CurrentPositionSeconds, durationSeconds); } catch { /* ignore */ }
    }

    private void UpdateTimelineUi()
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastSeekBufRefreshMs >= 450)
            {
                _lastSeekBufRefreshMs = now;
                try { _engine.RefreshSeekableBufferedFromCache(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        if (_isSeeking)
            return;

        var dur = _engine.CurrentDurationSeconds;
        var pos = _engine.CurrentPositionSeconds;
        try { UpdateSeekBufferedVisuals(); } catch { /* ignore */ }
        try { UpdateUltraSeekOverlay(pos, dur); } catch { /* ignore */ }

        // If we were paused when closed, keep showing the saved position until playback starts.
        try
        {
            var cur = _engine.GetCurrent();
            if (cur is not null &&
                !_engine.IsPlaying &&
                _pendingResumeSeconds > 1 &&
                !string.IsNullOrWhiteSpace(_pendingResumeVideoId) &&
                string.Equals(cur.VideoId, _pendingResumeVideoId, StringComparison.OrdinalIgnoreCase) &&
                dur is int pdur && pdur > 0)
            {
                var clampedPending = Math.Max(0, Math.Min(_pendingResumeSeconds, pdur));

                ElapsedTextBlock.Text = FormatTime(clampedPending);

                _ignoreSeekBar = true;
                try { SeekSlider.Value = clampedPending; }
                finally { _ignoreSeekBar = false; }

                try { UpdateUltraSeekOverlay(clampedPending, pdur); } catch { /* ignore */ }
                return;
            }
        }
        catch { /* ignore */ }

        ElapsedTextBlock.Text = FormatTime(pos);

        if (dur is null or <= 0)
            return;

        var clamped = Math.Max(0, Math.Min(pos, dur.Value));
        var cap = _engine.MaxSeekSecondsForUi;
        var posClamped = cap > 0.05 ? Math.Min(clamped, cap) : clamped;

        _ignoreSeekBar = true;
        try
        {
            SeekSlider.Value = posClamped;
        }
        finally
        {
            _ignoreSeekBar = false;
        }
    }

    private void UpdateSeekBufferedVisuals()
    {
        try
        {
            if (SeekCacheTrackBorder is null || SeekCacheFillBorder is null)
                return;

            var dur = _engine.CurrentDurationSeconds;
            var buf = _engine.SeekableBufferedSeconds;
            if (dur is not int dd || dd <= 0 || buf <= 0.05)
            {
                SeekCacheTrackBorder.Visibility = Visibility.Collapsed;
                return;
            }

            // Only show when buffered extent is meaningfully short of full duration (typical: cookie-pipe + growing cache).
            if (buf + 0.35 >= dd)
            {
                SeekCacheTrackBorder.Visibility = Visibility.Collapsed;
                return;
            }

            SeekCacheTrackBorder.Visibility = Visibility.Visible;
            var tw = SeekCacheTrackBorder.ActualWidth;
            if (tw > 1)
                SeekCacheFillBorder.Width = tw * Math.Max(0, Math.Min(1, buf / dd));
        }
        catch
        {
            // ignore
        }
    }

    private void ClampSeekSliderToBufferedCap()
    {
        try
        {
            if (!SeekSlider.IsEnabled)
                return;
            var cap = _engine.MaxSeekSecondsForUi;
            if (double.IsNaN(cap) || double.IsInfinity(cap))
                return;
            _ignoreSeekBar = true;
            try
            {
                SeekSlider.Value = Math.Max(SeekSlider.Minimum, Math.Min(SeekSlider.Value, Math.Min(SeekSlider.Maximum, cap)));
            }
            finally
            {
                _ignoreSeekBar = false;
            }
        }
        catch
        {
            // ignore
        }
    }

    private void UpdateUltraSeekOverlay(double posSeconds, int? durationSeconds)
    {
        if (UltraSeekOverlayCanvas is null || UltraSeekFillRect is null || UltraSeekThumbRect is null || VisualizerHostGrid is null)
            return;

        var ultra = _mainWindowCompact && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase);
        if (!ultra || durationSeconds is not int dur || dur <= 0)
        {
            UltraSeekOverlayCanvas.Visibility = Visibility.Collapsed;
            try { if (UltraSeekCacheRect is not null) UltraSeekCacheRect.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
            return;
        }

        // On startup, ActualWidth may be 0 until layout completes; defer one refresh rather than staying collapsed forever.
        if (VisualizerHostGrid.ActualWidth <= 1)
        {
            UltraSeekOverlayCanvas.Visibility = Visibility.Collapsed;
            try { if (UltraSeekCacheRect is not null) UltraSeekCacheRect.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
            try
            {
                Dispatcher.BeginInvoke(
                    new Action(() => { try { UpdateUltraSeekOverlay(posSeconds, durationSeconds); } catch { /* ignore */ } }),
                    DispatcherPriority.Loaded);
            }
            catch { /* ignore */ }
            return;
        }

        UltraSeekOverlayCanvas.Visibility = Visibility.Visible;

        var w = VisualizerHostGrid.ActualWidth;
        try
        {
            var buf = _engine.SeekableBufferedSeconds;
            if (UltraSeekCacheRect is not null && buf > 0.05 && buf + 0.35 < dur)
            {
                UltraSeekCacheRect.Visibility = Visibility.Visible;
                var cacheW = Math.Max(0.0, Math.Min(w, w * (buf / dur)));
                UltraSeekCacheRect.Width = cacheW;
                UltraSeekCacheRect.Height = UltraSeekFillRect.Height > 0 ? UltraSeekFillRect.Height : 26;
            }
            else if (UltraSeekCacheRect is not null)
                UltraSeekCacheRect.Visibility = Visibility.Collapsed;
        }
        catch
        {
            try { if (UltraSeekCacheRect is not null) UltraSeekCacheRect.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
        }

        var ratio = Math.Max(0.0, Math.Min(1.0, posSeconds / dur));
        var fillW = Math.Max(0.0, Math.Min(w, w * ratio));

        UltraSeekFillRect.Width = fillW;

        // Thumb sits at the right edge of the fill.
        var thumbW = UltraSeekThumbRect.Width > 0 ? UltraSeekThumbRect.Width : 2.0;
        var thumbLeft = Math.Max(0.0, Math.Min(w - thumbW, fillW - (thumbW / 2.0)));
        System.Windows.Controls.Canvas.SetLeft(UltraSeekThumbRect, thumbLeft);
    }

    private void VisualizerHostGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            UpdateUltraSeekOverlay(_engine.CurrentPositionSeconds, _engine.CurrentDurationSeconds);
        }
        catch { /* ignore */ }
    }

    private void ResetTimelineUiToStart()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                try { _isSeeking = false; } catch { /* ignore */ }

                ElapsedTextBlock.Text = "0:00";
                DurationTextBlock.Text = "--:--";

                // Reset meters so playlist changes don't leave stale visuals.
                try { VuLeftBar.Value = 0; } catch { /* ignore */ }
                try { VuRightBar.Value = 0; } catch { /* ignore */ }
                try
                {
                    if (SpectrumCanvas is not null)
                    {
                        try { if (_spectrumCurvePath is not null) _spectrumCurvePath.Data = null; } catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }

                _ignoreSeekBar = true;
                try
                {
                    SeekSlider.IsEnabled = false;
                    SeekSlider.Minimum = 0;
                    SeekSlider.Maximum = 1;
                    SeekSlider.Value = 0;
                }
                finally
                {
                    _ignoreSeekBar = false;
                }
            });
        }
        catch
        {
            // ignore
        }
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
            return;

        _isSeeking = true;
        _seekMouseDownVideoId = _engine.GetCurrent()?.VideoId;
        SeekSlider.CaptureMouse();

        // Click-to-seek: jump thumb to the clicked position.
        var p = e.GetPosition(SeekSlider);
        if (SeekSlider.ActualWidth > 0)
        {
            var ratio = Math.Max(0, Math.Min(1, p.X / SeekSlider.ActualWidth));
            SeekSlider.Value = SeekSlider.Minimum + ratio * (SeekSlider.Maximum - SeekSlider.Minimum);
        }

        ClampSeekSliderToBufferedCap();
        e.Handled = true;
    }

    private void SeekSlider_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Update thumb position while the user drags. The actual SeekAsync fires on mouse-up so we
        // don't spam seeks during a drag — this is purely a visual update.
        if (!_isSeeking || !SeekSlider.IsMouseCaptured || SeekSlider.ActualWidth <= 0)
            return;

        var p = e.GetPosition(SeekSlider);
        var ratio = Math.Max(0, Math.Min(1, p.X / SeekSlider.ActualWidth));
        SeekSlider.Value = SeekSlider.Minimum + ratio * (SeekSlider.Maximum - SeekSlider.Minimum);
        ClampSeekSliderToBufferedCap();
    }

    private async void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
            return;

        var expectedId = _seekMouseDownVideoId;
        _seekMouseDownVideoId = null;
        _isSeeking = false;
        try { SeekSlider.ReleaseMouseCapture(); } catch { /* ignore */ }

        // If the user changed tracks while the slider still had capture, do not seek with a stale thumb position.
        var cur = _engine.GetCurrent();
        if (cur is null || string.IsNullOrWhiteSpace(expectedId) ||
            !string.Equals(expectedId, cur.VideoId, StringComparison.OrdinalIgnoreCase))
            return;

        ClampSeekSliderToBufferedCap();
        await _engine.SeekAsync(SeekSlider.Value);
    }

    private void SeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ignoreSeekBar)
            return;

        if (_isSeeking)
            ElapsedTextBlock.Text = FormatTime(SeekSlider.Value);
    }

    private static string? NormalizeToolSave(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    private void ApplyResolvedToolPaths()
    {
        var y = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
        _ytDlp.SetPath(y.EffectiveFileName);
    }

    private void ApplyYtdlpPlaybackOptions()
    {
        var nodeRes = ToolPathResolver.Resolve(_savedNodePath, "node");
        string? nodeFull = null;
        if (nodeRes.IsFound)
        {
            try
            {
                var p = Path.GetFullPath(nodeRes.EffectiveFileName);
                if (File.Exists(p))
                    nodeFull = p;
            }
            catch { /* ignore */ }
        }

        var ejsGithub = string.IsNullOrWhiteSpace(_ytdlpEjsComponentSource)
                        || string.Equals(_ytdlpEjsComponentSource, "github", StringComparison.OrdinalIgnoreCase);
        _ytDlp.SetYoutubePlaybackOptions(
            ejsGithub,
            nodeFull,
            _youtubeCookiesFromBrowserEnabled,
            _youtubeCookiesFromBrowserEnabled ? _youtubeCookiesFromBrowser : null);
    }

    /// <summary>Returns NAudio WaveOut device number for the given product name, or -1 (WAVE_MAPPER default) if not found/null.</summary>
    private static int ResolveAudioDeviceNumber(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1;
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
            {
                if (string.Equals(NAudio.Wave.WaveOut.GetCapabilities(i).ProductName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        catch { /* ignore */ }
        return -1;
    }

    private void ApplyBackgroundFromSettings()
    {
        try
        {
            var mode = (_backgroundMode ?? "Default").Trim();
            System.Windows.Media.Brush mainBrush;
            System.Windows.Media.Brush playlistBrush;
            System.Windows.Media.Brush optionsLogBrush;
            System.Windows.Media.Brush lyricsBrush;
            System.Windows.Media.ImageBrush? rawMain = null;
            var isUserDefined = string.Equals(
                SettingsStore.NormalizeBackgroundImageStretch(_backgroundImageStretch),
                "UserDefined",
                StringComparison.OrdinalIgnoreCase);

            if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
            {
                // Flat background: follow current theme palette.
                var surface = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["App.Theme.Surface"];
                // If the user sets Background opacity to 0, App.Theme.Surface can become fully transparent.
                // With BackgroundMode=None that reveals the default window black, which can make Light theme
                // look like "black on black". Force an opaque fill while still honoring the theme RGB.
                if (_backgroundAlpha <= 0 && surface is System.Windows.Media.SolidColorBrush sb)
                {
                    var c = System.Windows.Media.Color.FromRgb(sb.Color.R, sb.Color.G, sb.Color.B);
                    var b = new System.Windows.Media.SolidColorBrush(c);
                    try { b.Freeze(); } catch { /* ignore */ }
                    surface = b;
                }
                mainBrush = surface;
                playlistBrush = surface;
                optionsLogBrush = surface;
                lyricsBrush = surface;
            }
            else if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(_customBackgroundImagePath) &&
                     File.Exists(_customBackgroundImagePath))
            {
                var bi = TryLoadBitmapSourceFromFileFresh(_customBackgroundImagePath);
                if (bi is null)
                    return;

                var img = new System.Windows.Media.ImageBrush(bi);
                ApplyBackgroundImageLayoutToBrushForTarget(img, bi, _backgroundImageStretch, target: "Main");
                if (!isUserDefined) { try { img.Freeze(); } catch { /* ignore */ } }
                rawMain = img;
                mainBrush = img;

                var imgPl = new System.Windows.Media.ImageBrush(bi);
                ApplyBackgroundImageLayoutToBrushForTarget(imgPl, bi, _backgroundImageStretch, target: "Playlist");
                try { imgPl.Freeze(); } catch { /* ignore */ }
                playlistBrush = imgPl;

                var imgOl = new System.Windows.Media.ImageBrush(bi);
                ApplyBackgroundImageLayoutToBrushForTarget(imgOl, bi, _backgroundImageStretch, target: "OptionsLog");
                try { imgOl.Freeze(); } catch { /* ignore */ }
                optionsLogBrush = imgOl;

                var imgLy = new System.Windows.Media.ImageBrush(bi);
                ApplyBackgroundImageLayoutToBrushForTarget(imgLy, bi, _backgroundImageStretch, target: "Lyrics");
                try { imgLy.Freeze(); } catch { /* ignore */ }
                lyricsBrush = imgLy;
            }
            else
            {
                // Bundled default pack image — clone so Stretch/Tile can follow settings without mutating the frozen resource.
                var defaultKey = GetDefaultBackgroundResourceKey(mode);

                if (System.Windows.Application.Current.Resources[defaultKey] is System.Windows.Media.ImageBrush defBrush
                    && defBrush.ImageSource is System.Windows.Media.Imaging.BitmapSource srcDef)
                {
                    var imgMain = new System.Windows.Media.ImageBrush(srcDef);
                    ApplyBackgroundImageLayoutToBrushForTarget(imgMain, srcDef, _backgroundImageStretch, target: "Main");
                    if (!isUserDefined) { try { imgMain.Freeze(); } catch { /* ignore */ } }
                    rawMain = imgMain;
                    mainBrush = imgMain;

                    var imgPl = new System.Windows.Media.ImageBrush(srcDef);
                    ApplyBackgroundImageLayoutToBrushForTarget(imgPl, srcDef, _backgroundImageStretch, target: "Playlist");
                    try { imgPl.Freeze(); } catch { /* ignore */ }
                    playlistBrush = imgPl;

                    var imgOl = new System.Windows.Media.ImageBrush(srcDef);
                    ApplyBackgroundImageLayoutToBrushForTarget(imgOl, srcDef, _backgroundImageStretch, target: "OptionsLog");
                    try { imgOl.Freeze(); } catch { /* ignore */ }
                    optionsLogBrush = imgOl;

                    var imgLy = new System.Windows.Media.ImageBrush(srcDef);
                    ApplyBackgroundImageLayoutToBrushForTarget(imgLy, srcDef, _backgroundImageStretch, target: "Lyrics");
                    try { imgLy.Freeze(); } catch { /* ignore */ }
                    lyricsBrush = imgLy;
                }
                else
                {
                    rawMain = System.Windows.Application.Current.Resources[defaultKey] as System.Windows.Media.ImageBrush;
                    mainBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[defaultKey];
                    playlistBrush = mainBrush;
                    optionsLogBrush = mainBrush;
                    lyricsBrush = mainBrush;
                }
            }

            // Keep a raw image brush around for theme color sampling.
            if (rawMain is not null)
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] = rawMain;

            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"] =
                ApplyScrimIfNeeded(mainBrush, scrimPercent: _backgroundScrimPercent);
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Playlist"] =
                ApplyScrimIfNeeded(playlistBrush, scrimPercent: _backgroundScrimPercent);
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.OptionsLog"] =
                ApplyScrimIfNeeded(optionsLogBrush, scrimPercent: _backgroundScrimPercent);
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Lyrics"] =
                ApplyScrimIfNeeded(lyricsBrush, scrimPercent: _backgroundScrimPercent);

            // Legacy key (fallback).
            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] =
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImage.Main"];

            // Track what bitmap UserDefined in-place updates should be tied to. If the backing file changes without
            // a full settings apply (rare), compact toggles can otherwise keep painting an older decoded BitmapSource.
            try
            {
                if (isUserDefined &&
                    !string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase) &&
                    rawMain?.ImageSource is System.Windows.Media.Imaging.BitmapSource appliedSrc)
                {
                    _appliedUserDefinedBackgroundFingerprint = TryComputeLiveUserDefinedBackgroundFingerprintFromBitmap(appliedSrc);
                }
                else
                {
                    _appliedUserDefinedBackgroundFingerprint = null;
                }
            }
            catch
            {
                _appliedUserDefinedBackgroundFingerprint = null;
            }
        }
        catch
        {
            // ignore
        }
    }

    private RectN? GetUserDefinedRectForTarget(string target)
    {
        try
        {
            if (string.Equals(target, "Playlist", StringComparison.OrdinalIgnoreCase))
                return _backgroundUserDefinedPlaylist;
            if (string.Equals(target, "OptionsLog", StringComparison.OrdinalIgnoreCase))
                return _backgroundUserDefinedOptionsLog;
            if (string.Equals(target, "Lyrics", StringComparison.OrdinalIgnoreCase))
                return _backgroundUserDefinedLyrics;

            // Main target: choose sub-rect by current main layout state.
            if (_mainWindowCompact)
            {
                if (string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase))
                    return _backgroundUserDefinedMainUltra ?? _backgroundUserDefinedMainCompact ?? _backgroundUserDefinedMainNormal;
                return _backgroundUserDefinedMainCompact ?? _backgroundUserDefinedMainNormal;
            }
            return _backgroundUserDefinedMainNormal;
        }
        catch { return null; }
    }

    private void ApplyBackgroundImageLayoutToBrushForTarget(
        System.Windows.Media.ImageBrush ib,
        System.Windows.Media.Imaging.BitmapSource src,
        string stretchMode,
        string target)
    {
        var m = SettingsStore.NormalizeBackgroundImageStretch(stretchMode);
        if (string.Equals(m, "UserDefined", StringComparison.OrdinalIgnoreCase))
        {
            ApplyBackgroundImageUserDefinedToBrush(ib, src, GetUserDefinedRectForTarget(target));
            return;
        }

        ApplyBackgroundImageStretchToBrush(ib, src, m);
    }

    private void ApplyBackgroundImageUserDefinedToBrush(
        System.Windows.Media.ImageBrush ib,
        System.Windows.Media.Imaging.BitmapSource src,
        RectN? rect)
    {
        // Normalized source-image viewbox. When rect is missing, fall back to full image.
        var r = rect ?? RectN.Full;
        r = SettingsStore.NormalizeRectN(r);

        ib.TileMode = System.Windows.Media.TileMode.None;
        ib.ViewboxUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
        ib.Viewbox = new System.Windows.Rect(r.X, r.Y, r.W, r.H);
        ib.Viewport = new System.Windows.Rect(0, 0, 1, 1);
        ib.ViewportUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
        // Preserve the crop's aspect ratio on the window (same idea as BestFit / UniformToFill on the full bitmap).
        // Fill would vertically or horizontally squash the wallpaper whenever the user crop ≠ window aspect.
        ib.Stretch = System.Windows.Media.Stretch.UniformToFill;
        ib.AlignmentX = System.Windows.Media.AlignmentX.Center;
        ib.AlignmentY = System.Windows.Media.AlignmentY.Center;
    }

    private void OpenBackgroundDesigner()
    {
        try
        {
            static System.Windows.Media.Imaging.BitmapSource? CoerceBitmapSource(System.Windows.Media.ImageSource? img)
            {
                try
                {
                    if (img is null)
                        return null;
                    if (img is System.Windows.Media.Imaging.BitmapSource bs)
                        return bs;
                    // Some ImageSources can be BitmapImage without being a BitmapSource in unusual cases;
                    // attempt a reload from URI so we always have a decodable bitmap.
                    if (img is System.Windows.Media.Imaging.BitmapImage bi && bi.UriSource is { } uri)
                    {
                        var clone = new System.Windows.Media.Imaging.BitmapImage();
                        clone.BeginInit();
                        clone.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        clone.UriSource = uri;
                        clone.EndInit();
                        clone.Freeze();
                        return clone;
                    }
                }
                catch { /* ignore */ }
                return null;
            }

            // Best-effort source bitmap from current raw brush; fallback to default pack background.
            System.Windows.Media.Imaging.BitmapSource? src = null;
            try
            {
                if (System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] is System.Windows.Media.ImageBrush raw
                    && raw.ImageSource is System.Windows.Media.Imaging.BitmapSource bs)
                    src = bs;
            }
            catch { /* ignore */ }

            if (src is null)
            {
                try
                {
                    if (System.Windows.Application.Current.Resources["App.Brush.DefaultWindowBgImage"] is System.Windows.Media.ImageBrush def
                        && def.ImageSource is { } img2)
                        src = CoerceBitmapSource(img2);
                }
                catch { /* ignore */ }
            }

            if (src is null)
            {
                // Last resort: load the built-in pack image directly (ensures we always have a decoded BitmapSource).
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri("pack://application:,,,/LyllyPlayer;component/Assets/Background.png", UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    src = bi;
                }
                catch { /* ignore */ }
            }

            if (src is null)
            {
                try
                {
                    TopmostMessageBox.Show(
                        "Background designer couldn't load the default background image source.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { /* ignore */ }
                return;
            }

            // Keep MainWindow's stretch mode aligned with what the Options UI currently shows.
            // Otherwise the user can pick "User defined" in Options but MainWindow still applies BestFit/Stretch until Apply,
            // which makes the designer + crops appear to "do nothing" (especially with Background=Default).
            try
            {
                if (_optionsWindow is not null)
                {
                    var tag = _optionsWindow.TryGetBackgroundImageStretchUiTag();
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var next = SettingsStore.NormalizeBackgroundImageStretch(tag);
                        if (!string.Equals(SettingsStore.NormalizeBackgroundImageStretch(_backgroundImageStretch), next, StringComparison.OrdinalIgnoreCase))
                        {
                            _backgroundImageStretch = next;
                            ApplyBackgroundFromSettings();
                            ApplyBackgroundColorsFromSettings();
                            RequestPersistSnapshot();
                        }
                    }
                }
            }
            catch { /* ignore */ }

            if (_backgroundDesignerWindow is not null)
            {
                try
                {
                    _backgroundDesignerWindow.Activate();
                    return;
                }
                catch { _backgroundDesignerWindow = null; }
            }

            static double SafeAspect(double w, double h)
            {
                if (w <= 1 || h <= 1) return 0;
                return w / h;
            }

            static double SafeAspectFromWindow(Window w, Rect? lastBounds)
            {
                try
                {
                    // ActualWidth/Height can be 0 while minimized or before first render; prefer RestoreBounds.
                    var a = SafeAspect(w.ActualWidth, w.ActualHeight);
                    if (a > 0 && w.WindowState != WindowState.Minimized)
                        return a;

                    var rb = w.RestoreBounds;
                    var rba = SafeAspect(rb.Width, rb.Height);
                    if (rba > 0)
                        return rba;

                    if (lastBounds is { } lb)
                    {
                        var lba = SafeAspect(lb.Width, lb.Height);
                        if (lba > 0)
                            return lba;
                    }
                }
                catch { /* ignore */ }
                return 0;
            }

            // Ensure Default/Compact/Ultra hints are populated for this client size (avoids designer-only fallbacks
            // that don't match the real window and squashed crops).
            try { UpdateLayout(); } catch { /* ignore */ }
            try { FillDerivedMainAspectHints(ActualWidth, ActualHeight); } catch { /* ignore */ }

            // Aspect hints for the designer.
            // Prefer cached, measured aspects (more stable than sampling the current mode only).
            // Fall back to currently visible window sizes (playlist/options), else designer-internal defaults.
            var mainDefaultAspect = _measuredMainDefaultAspect;
            var mainCompactAspect = _measuredMainCompactAspect;
            var mainUltraAspect = _measuredMainUltraAspect;
            var playlistAspect = (_playlistWindow is not null) ? SafeAspectFromWindow(_playlistWindow, _lastPlaylistBounds) : 0;
            var optionsLogAspect = (_optionsWindow is not null) ? SafeAspectFromWindow(_optionsWindow, _lastOptionsBounds) : 0;
            var lyricsAspect = (_lyricsWindow is not null) ? SafeAspectFromWindow(_lyricsWindow, _lastLyricsBounds) : 0;

            var win = new LyllyPlayer.Windows.BackgroundDesignerWindow(
                src: src,
                mainNormal: (
                    _backgroundUserDefinedMainNormal
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().mainNormal : RectN.Full)
                ),
                mainCompact: (
                    _backgroundUserDefinedMainCompact
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().mainCompact : (_backgroundUserDefinedMainNormal ?? RectN.Full))
                ),
                mainUltra: (
                    _backgroundUserDefinedMainUltra
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().mainUltra : (_backgroundUserDefinedMainNormal ?? RectN.Full))
                ),
                playlist: (
                    _backgroundUserDefinedPlaylist
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().playlist : RectN.Full)
                ),
                optionsLog: (
                    _backgroundUserDefinedOptionsLog
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().optionsLog : RectN.Full)
                ),
                lyrics: (
                    _backgroundUserDefinedLyrics
                    ?? (_optionsWindow is not null ? _optionsWindow.GetBackgroundDesignerDraft().lyrics : RectN.Full)
                ),
                mainDefaultAspect: mainDefaultAspect,
                mainCompactAspect: mainCompactAspect,
                mainUltraAspect: mainUltraAspect,
                playlistAspect: playlistAspect,
                optionsLogAspect: optionsLogAspect,
                lyricsAspect: lyricsAspect,
                apply: (r) =>
                {
                    try
                    {
                        _backgroundUserDefinedMainNormal = r.MainNormal;
                        _backgroundUserDefinedMainCompact = r.MainCompact;
                        _backgroundUserDefinedMainUltra = r.MainUltra;
                        _backgroundUserDefinedPlaylist = r.Playlist;
                        _backgroundUserDefinedOptionsLog = r.OptionsLog;
                        _backgroundUserDefinedLyrics = r.Lyrics;

                        ApplyBackgroundFromSettings();
                        ApplyBackgroundColorsFromSettings();
                        RequestPersistSnapshot();
                        try
                        {
                            _optionsWindow?.UpdateBackgroundDesignerDraft(
                                mainNormal: r.MainNormal,
                                mainCompact: r.MainCompact,
                                mainUltra: r.MainUltra,
                                playlist: r.Playlist,
                                optionsLog: r.OptionsLog,
                                lyrics: r.Lyrics);
                        }
                        catch { /* ignore */ }
                    }
                    catch { /* ignore */ }
                });
            try { win.Owner = _optionsWindow; } catch { /* ignore */ }
            if (win.Owner is null)
                win.Owner = this;
            _backgroundDesignerWindow = win;
            win.Closed += (_, _) =>
            {
                try
                {
                    if (ReferenceEquals(_backgroundDesignerWindow, win))
                        _backgroundDesignerWindow = null;
                }
                catch { /* ignore */ }
            };

            // Modeless: allow interacting with other windows while the designer is open.
            win.ShowActivated = true;
            win.Show();
            try
            {
                // Ensure the window is actually brought to front (ShowInTaskbar=False + owned window can look like "nothing happened").
                win.Activate();
                win.Focus();
                win.Topmost = true;
                win.Topmost = false;
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            try
            {
                TopmostMessageBox.Show(
                    "Background designer failed to open.\n\n" + ex.Message,
                    "LyllyPlayer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// One wallpaper tile size in device-independent units (WPF <see cref="System.Windows.Media.BrushMappingMode.Absolute"/> viewport).
    /// </summary>
    private static (double W, double H) GetBackgroundImageTileSizeDips(System.Windows.Media.Imaging.BitmapSource src)
    {
        var dpiX = src.DpiX > 0 ? src.DpiX : 96.0;
        var dpiY = src.DpiY > 0 ? src.DpiY : 96.0;
        var w = Math.Max(1.0, src.PixelWidth * 96.0 / dpiX);
        var h = Math.Max(1.0, src.PixelHeight * 96.0 / dpiY);
        return (w, h);
    }

    private static void ApplyBackgroundImageStretchToBrush(
        System.Windows.Media.ImageBrush ib,
        System.Windows.Media.Imaging.BitmapSource src,
        string stretchMode)
    {
        var m = (stretchMode ?? "Stretch").Trim();
        if (string.Equals(m, "Tile", StringComparison.OrdinalIgnoreCase))
        {
            ib.Stretch = System.Windows.Media.Stretch.None;
            ib.TileMode = System.Windows.Media.TileMode.Tile;
            var (tw, th) = GetBackgroundImageTileSizeDips(src);
            ib.Viewport = new System.Windows.Rect(0, 0, tw, th);
            ib.ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute;
            ib.AlignmentX = System.Windows.Media.AlignmentX.Left;
            ib.AlignmentY = System.Windows.Media.AlignmentY.Top;
        }
        else if (string.Equals(m, "BestFit", StringComparison.OrdinalIgnoreCase))
        {
            // UniformToFill: same aspect ratio as the bitmap, but always cover the window (no letterboxing).
            // Narrow/tall images therefore fill the width; excess height is cropped instead of empty side bands.
            ib.TileMode = System.Windows.Media.TileMode.None;
            ib.Viewport = new System.Windows.Rect(0, 0, 1, 1);
            ib.ViewportUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
            ib.Stretch = System.Windows.Media.Stretch.UniformToFill;
            ib.AlignmentX = System.Windows.Media.AlignmentX.Center;
            ib.AlignmentY = System.Windows.Media.AlignmentY.Center;
        }
        else
        {
            ib.TileMode = System.Windows.Media.TileMode.None;
            ib.Viewport = new System.Windows.Rect(0, 0, 1, 1);
            ib.ViewportUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
            ib.Stretch = System.Windows.Media.Stretch.Fill;
            ib.AlignmentX = System.Windows.Media.AlignmentX.Center;
            ib.AlignmentY = System.Windows.Media.AlignmentY.Center;
        }
    }

    private bool IsEffectiveDarkThemeForBackground()
    {
        var mode = SettingsStore.NormalizeThemeMode(_themeMode);
        if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase))
            return false;

        // Auto: prefer image luminance if an image is present; otherwise fall back to Windows.
        var bgMode = (_backgroundMode ?? "Default").Trim();
        if (!string.Equals(bgMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            var avg = TryGetAverageColorFromCurrentBackgroundImage(dropBrightestPercent: 0.15);
            if (avg is { } c)
            {
                // Bright background => prefer Light theme (dark text); dark background => prefer Dark theme (light text).
                return RelativeLuminance(c) < 0.50;
            }
        }

        return !WindowsAppsUseLightTheme();
    }

    private static bool WindowsAppsUseLightTheme()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            if (v is int i)
                return i != 0;
            if (v is byte[] b && b.Length >= 4)
                return BitConverter.ToInt32(b, 0) != 0;
        }
        catch { /* ignore */ }
        return true; // default to Light if unknown
    }

    private static bool WindowsAccentOnTitleBarsEnabled()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "ColorPrevalence",
                null);
            if (v is int i)
                return i != 0;
            if (v is byte[] b && b.Length >= 4)
                return BitConverter.ToInt32(b, 0) != 0;
        }
        catch { /* ignore */ }
        return false;
    }

    private static System.Windows.Media.Color? WindowsAccentColorRgb()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "AccentColor",
                null);
            if (v is int i)
            {
                // DWORD is ABGR (0xAABBGGRR) for AccentColor.
                var r = (byte)(i & 0xFF);
                var g = (byte)((i >> 8) & 0xFF);
                var b = (byte)((i >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
            if (v is byte[] b4 && b4.Length >= 4)
            {
                var i2 = BitConverter.ToInt32(b4, 0);
                var r = (byte)(i2 & 0xFF);
                var g = (byte)((i2 >> 8) & 0xFF);
                var b = (byte)((i2 >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Optional DWM inactive title accent (same ABGR DWORD as <c>AccentColor</c>); may be absent until user/customization sets it.</summary>
    private static System.Windows.Media.Color? WindowsAccentColorInactiveRgb()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "AccentColorInactive",
                null);
            if (v is int i)
            {
                var r = (byte)(i & 0xFF);
                var g = (byte)((i >> 8) & 0xFF);
                var b = (byte)((i >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
            if (v is byte[] b4 && b4.Length >= 4)
            {
                var i2 = BitConverter.ToInt32(b4, 0);
                var r = (byte)(i2 & 0xFF);
                var g = (byte)((i2 >> 8) & 0xFF);
                var b = (byte)((i2 >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static (System.Windows.Media.Color active, System.Windows.Media.Color inactive, System.Windows.Media.Color textActive, System.Windows.Media.Color textInactive)
        GetWindowsTitleBarPalette(System.Windows.Media.Color surfaceRgb)
    {
        // Prefer DWM accent-on-titlebars behavior. This matches what most modern Windows apps visually do.
        var appsLight = WindowsAppsUseLightTheme();
        var accentOn = WindowsAccentOnTitleBarsEnabled();
        var accent = WindowsAccentColorRgb();

        System.Windows.Media.Color baseActive;
        System.Windows.Media.Color baseInactive;
        if (accentOn && accent is { } a)
        {
            baseActive = a;
            // Prefer explicit inactive accent from DWM when present; otherwise blend active toward client surface.
            var inactiveAccent = WindowsAccentColorInactiveRgb();
            baseInactive = inactiveAccent is { } ia
                ? Blend(System.Windows.Media.Color.FromRgb(ia.R, ia.G, ia.B), surfaceRgb, 0.12)
                : Blend(a, surfaceRgb, 0.45);
        }
        else
        {
            baseActive = appsLight
                ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
            baseInactive = Blend(baseActive, surfaceRgb, 0.45);
        }

        var textA = PickForegroundForBackground(baseActive, minRatio: 4.5);
        var textI = Blend(textA, PickForegroundForBackground(surfaceRgb, minRatio: 4.5), 0.55);
        return (baseActive, baseInactive, textA, textI);
    }

    private System.Windows.Media.Brush ApplyScrimIfNeeded(System.Windows.Media.Brush baseBrush, int scrimPercent)
    {
        try
        {
            var allowMutableForUserDefined = string.Equals(
                SettingsStore.NormalizeBackgroundImageStretch(_backgroundImageStretch),
                "UserDefined",
                StringComparison.OrdinalIgnoreCase);

            scrimPercent = Math.Clamp(scrimPercent, 0, 80);
            if (scrimPercent <= 0)
                return baseBrush;

            // Only apply scrim to image backgrounds (solid backgrounds don't need it).
            if (baseBrush is not System.Windows.Media.ImageBrush ib)
                return baseBrush;
            if (ib.ImageSource is not System.Windows.Media.Imaging.BitmapSource src)
                return baseBrush;

            // Need a decodable bitmap (same guard as color sampling) before wrapping in DrawingBrush.
            if (ComputeAverageColorDroppingBrightest(src, dropBrightestPercent: 0.15) is null)
                return baseBrush;

            // Direction depends on effective theme:
            // - Dark theme (light text): darken background with black scrim
            // - Light theme (dark text): lighten background with white scrim
            var darkTheme = IsEffectiveDarkThemeForBackground();
            var scrimRgb = darkTheme
                ? System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF);

            var a = (byte)Math.Clamp((int)Math.Round(255.0 * (scrimPercent / 100.0)), 0, 255);
            var scrim = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, scrimRgb.R, scrimRgb.G, scrimRgb.B));
            scrim.Freeze();

            var drawing = new System.Windows.Media.DrawingGroup();
            double tileW = 0, tileH = 0;
            // Tiled ImageBrush: inner ImageDrawing must match one tile in DIP space; 0,0,1,1 only works with Stretch≠None.
            if (ib.TileMode == System.Windows.Media.TileMode.Tile)
            {
                (tileW, tileH) = GetBackgroundImageTileSizeDips(src);
                var tileRect = new System.Windows.Rect(0, 0, tileW, tileH);
                drawing.Children.Add(new System.Windows.Media.ImageDrawing(ib.ImageSource, tileRect));
                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(scrim, null, new System.Windows.Media.RectangleGeometry(tileRect)));
            }
            else
            {
                // For non-tiled image brushes, keep drawing space normalized (0..1) and preserve the original
                // ImageBrush Viewbox/ViewboxUnits so user-defined crops don't get reset when scrim toggles.
                //
                // Do NOT use ImageDrawing(ImageSource, 0..1 rect): it stretches the bitmap into a unit square
                // (Fill-like), which crushes aspect for any non-square source — UserDefined / BestFit then look
                // vertically squashed on the window. GeometryDrawing + cloned ImageBrush keeps the same mapping.
                //
                // The inner rectangle must match the *physical* aspect of the cropped bitmap (Viewbox × pixel size).
                // If it is a square (0,0,1,1), the outer DrawingBrush applies a second UniformToFill (tile → window)
                // and over-crops / "zooms in" vs a plain ImageBrush on the window.
                var vb = ib.Viewbox;
                var imgW = Math.Max(1.0, (double)src.PixelWidth);
                var imgH = Math.Max(1.0, (double)src.PixelHeight);
                double physW;
                double physH;
                if (ib.ViewboxUnits == System.Windows.Media.BrushMappingMode.RelativeToBoundingBox)
                {
                    physW = vb.Width * imgW;
                    physH = vb.Height * imgH;
                }
                else
                {
                    physW = vb.Width;
                    physH = vb.Height;
                }

                var cropAspect = physW / Math.Max(1e-9, physH);
                double dw;
                double dh;
                if (cropAspect >= 1.0)
                {
                    dw = cropAspect;
                    dh = 1.0;
                }
                else
                {
                    dw = 1.0;
                    dh = 1.0 / cropAspect;
                }

                var contentRect = new System.Windows.Rect(0, 0, dw, dh);
                // For UserDefined we keep the *same* ImageBrush instance so we can change Viewbox in-place later.
                var ibForDrawing = allowMutableForUserDefined ? ib : ib.CloneCurrentValue();
                if (!allowMutableForUserDefined)
                {
                    try { ibForDrawing.Freeze(); } catch { /* ignore */ }
                }

                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(ibForDrawing, null, new System.Windows.Media.RectangleGeometry(contentRect)));
                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(scrim, null, new System.Windows.Media.RectangleGeometry(contentRect)));
            }

            if (!allowMutableForUserDefined)
                drawing.Freeze();

            System.Windows.Media.DrawingBrush db;
            if (ib.TileMode == System.Windows.Media.TileMode.Tile)
            {
                db = new System.Windows.Media.DrawingBrush(drawing)
                {
                    Stretch = System.Windows.Media.Stretch.None,
                    AlignmentX = ib.AlignmentX,
                    AlignmentY = ib.AlignmentY,
                    TileMode = System.Windows.Media.TileMode.Tile,
                    Viewport = new System.Windows.Rect(0, 0, tileW, tileH),
                    ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                };
            }
            else
            {
                // Cropping / stretch live on the cloned ImageBrush inside the drawing (0..1 content rect).
                // Copying ib.Viewbox onto this DrawingBrush would apply the crop twice (wrong zoom / wrong frame).
                db = new System.Windows.Media.DrawingBrush(drawing)
                {
                    Stretch = ib.Stretch,
                    AlignmentX = ib.AlignmentX,
                    AlignmentY = ib.AlignmentY,
                    TileMode = ib.TileMode,
                    Viewbox = new System.Windows.Rect(0, 0, 1, 1),
                    ViewboxUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox,
                    Viewport = ib.Viewport,
                    ViewportUnits = ib.ViewportUnits,
                };
            }

            if (!allowMutableForUserDefined)
                db.Freeze();
            return db;
        }
        catch
        {
            return baseBrush;
        }
    }

    private void ApplyUiScale()
    {
        try
        {
            // Replace the resource instance each time (Freezables can get frozen if shared).
            System.Windows.Application.Current.Resources["App.UiScaleTransform"] =
                new System.Windows.Media.ScaleTransform(UiScale, UiScale);
        }
        catch { /* ignore */ }

        try
        {
            ApplyScaledFixedWindowSizes();
        }
        catch { /* ignore */ }

        try
        {
            // Legacy "dock to main" snapping disabled.
        }
        catch { /* ignore */ }

        // Chrome BorderThickness lives outside LayoutTransform — scale it with UiScale here.
        try
        {
            ApplyWindowBorderFromSettings();
        }
        catch { /* ignore */ }
    }

    private void ApplyAlwaysOnTopFromSettings()
    {
        try
        {
            Topmost = _alwaysOnTop;
        }
        catch { /* ignore */ }

        try
        {
            // Keep main window chrome in "active" colors while TOP is enabled.
            IsAlwaysOnTopUi = _alwaysOnTop;
        }
        catch { /* ignore */ }

        // Aux windows: NEVER topmost unless TOP is enabled AND the corresponding "also keep on top" flag is enabled.
        try { if (_playlistWindow is not null) _playlistWindow.Topmost = _alwaysOnTop && _alwaysOnTopPlaylistWindow; } catch { /* ignore */ }
        try { if (_optionsWindow is not null) _optionsWindow.Topmost = _alwaysOnTop && _alwaysOnTopOptionsWindow; } catch { /* ignore */ }
        try { if (_lyricsWindow is not null) _lyricsWindow.Topmost = _alwaysOnTop && _alwaysOnTopLyricsWindow; } catch { /* ignore */ }

        try
        {
            if (ChromeAlwaysOnTopToggleButton is not null)
            {
                _suppressAlwaysOnTopToggleEvents = true;
                ChromeAlwaysOnTopToggleButton.IsChecked = _alwaysOnTop;
            }
        }
        catch { /* ignore */ }
        finally
        {
            _suppressAlwaysOnTopToggleEvents = false;
        }
    }

    private void SyncAuxWindowsMinimizeStateWithMain()
    {
        // When windows are not owned, minimizing Main won't minimize auxiliaries automatically.
        // For normal Windows taskbar behavior, minimize/restore auxiliaries together with Main,
        // but only when TOP is not enabled.
        try
        {
            if (_alwaysOnTop)
                return;

            if (WindowState == WindowState.Minimized)
            {
                try
                {
                    if (_playlistWindow is not null && _playlistWindow.IsVisible && _playlistWindow.WindowState != WindowState.Minimized)
                    {
                        _playlistMinimizedByMain = true;
                        _playlistWindow.WindowState = WindowState.Minimized;
                    }
                }
                catch { /* ignore */ }

                try
                {
                    if (_optionsWindow is not null && _optionsWindow.IsVisible && _optionsWindow.WindowState != WindowState.Minimized)
                    {
                        _optionsMinimizedByMain = true;
                        _optionsWindow.WindowState = WindowState.Minimized;
                    }
                }
                catch { /* ignore */ }

                return;
            }

            // Restore auxiliaries we minimized when Main comes back.
            if (WindowState == WindowState.Normal)
            {
                try
                {
                    if (_playlistMinimizedByMain && _playlistWindow is not null && _playlistWindow.IsVisible && _playlistWindow.WindowState == WindowState.Minimized)
                        _playlistWindow.WindowState = WindowState.Normal;
                }
                catch { /* ignore */ }
                finally { _playlistMinimizedByMain = false; }

                try
                {
                    if (_optionsMinimizedByMain && _optionsWindow is not null && _optionsWindow.IsVisible && _optionsWindow.WindowState == WindowState.Minimized)
                        _optionsWindow.WindowState = WindowState.Normal;
                }
                catch { /* ignore */ }
                finally { _optionsMinimizedByMain = false; }
            }
        }
        catch { /* ignore */ }
    }

    private void QueueRaiseAuxWindowsOnce()
    {
        try
        {
            // Defer so activation doesn't interfere with the mouse down that often caused it (title bar click/drag).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RaiseAuxWindowsOnceCore();
            }), DispatcherPriority.Background);
        }
        catch { /* ignore */ }
    }

    private void RaiseAuxWindowsOnceCore()
    {
        if (_raisingAuxWindows)
            return;
        if (_isShuttingDown)
            return;

        // Never steal focus while the user is clicking/dragging the chrome bar (would break button clicks / drag).
        try
        {
            if (_chromeDragging)
                return;
        }
        catch { /* ignore */ }
        try
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed)
                return;
        }
        catch { /* ignore */ }

        try
        {
            _raisingAuxWindows = true;

            // If we previously minimized auxiliaries due to TOP+taskbar click, restore them first to their
            // prior coordinates so the following "raise" doesn't resurrect them at weird positions.
            TryRestoreAuxAfterTopTaskbarMinimize();

            // Raise auxiliaries without stealing activation/focus (prevents taskbar flash loops).
            TryRaiseWindowNoActivate(_playlistWindow);
            TryRaiseWindowNoActivate(_optionsWindow);
        }
        finally
        {
            _raisingAuxWindows = false;
        }
    }

    private void TryRestoreAuxAfterTopTaskbarMinimize()
    {
        // Important: restoring from Minimized can apply OS restore placement *after* we set WindowState.
        // Apply the saved Left/Top on the dispatcher after the restore to make it deterministic.
        _restoringAuxFromMinimize = true;
        try
        {
            if (_playlistMinimizedByTopTaskbar && _playlistWindow is not null && _playlistWindow.WindowState == WindowState.Minimized)
            {
                var w = _playlistWindow;
                var b = _playlistBoundsBeforeTopTaskbarMinimize;
                try { w.Show(); } catch { /* ignore */ }
                w.WindowState = WindowState.Normal;
                if (b is { })
                {
                    // Use high priority to get coordinates applied before the first visible render, to reduce "flash"
                    // at a default location.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _syncingWindowMove = true;
                            w.Left = SnapRound(b.Value.Left);
                            w.Top = SnapRound(b.Value.Top);
                        }
                        catch { /* ignore */ }
                        finally
                        {
                            _syncingWindowMove = false;
                            _playlistMinimizedByTopTaskbar = false;
                            _playlistBoundsBeforeTopTaskbarMinimize = null;
                        }
                    }), DispatcherPriority.Send);
                }
                else
                {
                    _playlistMinimizedByTopTaskbar = false;
                    _playlistBoundsBeforeTopTaskbarMinimize = null;
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_optionsMinimizedByTopTaskbar && _optionsWindow is not null && _optionsWindow.WindowState == WindowState.Minimized)
            {
                var w = _optionsWindow;
                var b = _optionsBoundsBeforeTopTaskbarMinimize;
                try { w.Show(); } catch { /* ignore */ }
                w.WindowState = WindowState.Normal;
                if (b is { })
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _syncingWindowMove = true;
                            w.Left = SnapRound(b.Value.Left);
                            w.Top = SnapRound(b.Value.Top);
                        }
                        catch { /* ignore */ }
                        finally
                        {
                            _syncingWindowMove = false;
                            _optionsMinimizedByTopTaskbar = false;
                            _optionsBoundsBeforeTopTaskbarMinimize = null;
                        }
                    }), DispatcherPriority.Send);
                }
                else
                {
                    _optionsMinimizedByTopTaskbar = false;
                    _optionsBoundsBeforeTopTaskbarMinimize = null;
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            // Give any pending LocationChanged/SizeChanged (and our placement dispatches) time to run.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _restoringAuxFromMinimize = false; } catch { /* ignore */ }
            }), DispatcherPriority.ContextIdle);
        }
    }

    private void TryRaiseWindowNoActivate(Window? w)
    {
        if (w is null)
            return;
        try
        {
            if (!w.IsVisible)
                return;
        }
        catch { /* ignore */ }

        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var mainHwnd = new WindowInteropHelper(this).Handle;

            // DisplayFusion manages Z-order across monitors; stacking sibling HWNDs via SetWindowPos here fights its hooks
            // and can leave other apps unable to activate reliably. Rely on WPF visibility/Topmost only.
            if (IsDisplayFusionRunning())
                return;

            if (_alwaysOnTop)
            {
                // Main uses WPF Topmost (WS_EX_TOPMOST). Do not call SetWindowPos(HWND_TOPMOST) — it fights the shell
                // and can break Z-order/taskbar restore for other apps. Only order aux above main when they share TOP.
                var wantTop = ReferenceEquals(w, _playlistWindow) && _alwaysOnTopPlaylistWindow
                    || ReferenceEquals(w, _optionsWindow) && _alwaysOnTopOptionsWindow
                    || ReferenceEquals(w, _lyricsWindow) && _alwaysOnTopLyricsWindow;
                if (wantTop && mainHwnd != IntPtr.Zero)
                {
                    _ = SetWindowPos(hwnd, mainHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    return;
                }

                return;
            }

            // Not always-on-top: stack the auxiliary immediately above the main window (avoid HWND_TOP over whole desktop).
            var insertAfter = mainHwnd != IntPtr.Zero ? mainHwnd : HWND_TOP;
            _ = SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch { /* ignore */ }
    }

    private string GetAppTitleBase()
    {
        try
        {
            var mode = string.IsNullOrWhiteSpace(_appTitleMode) ? "Default" : _appTitleMode.Trim();
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                var t = (_customAppTitle ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    return t;
            }
        }
        catch { /* ignore */ }
        return "LyllyPlayer";
    }

    private string? TryGetCurrentSongTitleForWindowTitle()
    {
        try
        {
            if (_nowPlayingEntry is null)
                return null;
            var song = (_nowPlayingEntry.Title ?? "").Trim();
            var artist = (_nowPlayingEntry.Channel ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(song))
                return string.IsNullOrWhiteSpace(artist) ? song : $"{song} - {artist}";

            // yt-dlp can briefly yield an empty title during fetch; fall back so Ultra-compact caption isn't blank.
            if (!string.IsNullOrWhiteSpace(artist))
                return artist;

            var id = (_nowPlayingEntry.VideoId ?? "").Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyMainWindowTitleFromSettings(string baseTitle)
    {
        try
        {
            if (_mainWindowCompact && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase))
            {
                // Lyrics override for Ultra-Compact mode.
                // Only show lyric lines in main window when lyrics window is NOT open — it already displays them.
                if (_lyricsEnabled && _lyricsService.Manager.HasLyrics && _engine.IsPlaying && (_lyricsWindow is null || !_lyricsWindow.IsVisible))
                {
                    var lyricLine = _lyricsService.Manager.GetCurrentLine(_engine.CurrentPositionSeconds);
                    if (!string.IsNullOrEmpty(lyricLine))
                    {
                        Title = $"{lyricLine}";
                        return;
                    }
                }
                Title = TryGetCurrentSongTitleForWindowTitle() ?? baseTitle;
                return;
            }

            var mode = string.IsNullOrWhiteSpace(_appTitleMode) ? "Default" : _appTitleMode.Trim();
            if (string.Equals(mode, "Current song", StringComparison.OrdinalIgnoreCase))
            {
                Title = TryGetCurrentSongTitleForWindowTitle() ?? baseTitle;
                return;
            }

            Title = baseTitle;
        }
        catch { /* ignore */ }
    }

    private void ApplyAppTitleFromSettings()
    {
        try
        {
            var baseTitle = GetAppTitleBase();
            ApplyMainWindowTitleFromSettings(baseTitle);
            if (_playlistWindow is not null)
                _playlistWindow.Title = $"{baseTitle} — Playlist";
            if (_optionsWindow is not null)
                _optionsWindow.Title = $"{baseTitle} — Options";
            if (_lyricsWindow is not null)
                _lyricsWindow.Title = $"{baseTitle} — Lyrics";
        }
        catch { /* ignore */ }
    }

    private void EnsureTrayIconLoaded()
    {
        try
        {
            // Load once and keep the same icon for app lifetime.
            if (_trayIcon is not null)
                return;

            // Safe fallback: always valid handle.
            try { _trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(); } catch { /* ignore */ }

            try
            {
                var uri = new Uri("pack://application:,,,/LyllyPlayer;component/Assets/LyllyPlayer.ico", UriKind.Absolute);
                var sri = System.Windows.Application.GetResourceStream(uri);
                if (sri?.Stream is not null)
                {
                    // Force a tray-sized icon (16x16) so we always have a valid small icon handle.
                    using var ico = new System.Drawing.Icon(sri.Stream, new System.Drawing.Size(16, 16));
                    _trayIcon = (System.Drawing.Icon)ico.Clone();
                }
            }
            catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    private void EnsureHardcodetTrayIconCreated()
    {
        if (_hardcodetTrayIcon is not null)
            return;

        EnsureTrayIconLoaded();
        if (_trayIcon is null)
            return;

        var cm = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open" };
        openItem.Click += (_, _) => { try { ShowMainWindowFromTray(); } catch { /* ignore */ } };
        cm.Items.Add(openItem);
        cm.Items.Add(new System.Windows.Controls.Separator());

        var prevItem = new System.Windows.Controls.MenuItem { Header = "Previous" };
        prevItem.Click += (_, _) => { try { PrevButton_OnClick(this, new RoutedEventArgs()); } catch { /* ignore */ } };
        cm.Items.Add(prevItem);

        var stopItem = new System.Windows.Controls.MenuItem { Header = "Stop" };
        stopItem.Click += (_, _) =>
        {
            try
            {
                _engine.Stop();
                _pendingResumeSeconds = 0;
                _pendingResumeVideoId = null;
                ResetTimelineUiToStart();
            }
            catch { /* ignore */ }
        };
        cm.Items.Add(stopItem);

        var playPauseItem = new System.Windows.Controls.MenuItem { Header = "Play/Pause" };
        playPauseItem.Click += (_, _) => { try { PlayPauseButton_OnClick(this, new RoutedEventArgs()); } catch { /* ignore */ } };
        cm.Items.Add(playPauseItem);

        var nextItem = new System.Windows.Controls.MenuItem { Header = "Next" };
        nextItem.Click += (_, _) => { try { NextButton_OnClick(this, new RoutedEventArgs()); } catch { /* ignore */ } };
        cm.Items.Add(nextItem);

        cm.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => { try { Close(); } catch { /* ignore */ } };
        cm.Items.Add(exitItem);

        _hardcodetTrayIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
        {
            Icon = _trayIcon,
            // Disable tray hover tooltip (Explorer-owned, not themeable).
            ToolTipText = "",
            ContextMenu = cm,
            Visibility = Visibility.Collapsed,
        };
        _hardcodetTrayIcon.TrayMouseDoubleClick += (_, _) =>
        {
            try { ShowMainWindowFromTray(); } catch { /* ignore */ }
        };
    }

    // Lyrics resolution and display methods

    private void _startLyricsTimer()
    {
        if (_lyricsTimer is null)
        {
            _lyricsTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                // 250ms misses fast lyric transitions. Use a faster tick, but keep CPU low by only updating UI when the line changes.
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _lyricsTimer.Tick += (_, _) =>
            {
                try { UpdateLyricsDisplay(force: false); } catch { /* ignore */ }
            };
        }

        if (!_lyricsTimer.IsEnabled)
        {
            _lyricsService.LastDisplayedLyricLineIndex = int.MinValue;
            _lyricsTimer.Start();
        }
    }

    private void _stopLyricsTimer()
    {
        try { _lyricsTimer?.Stop(); } catch { /* ignore */ }
    }

    private void UpdateLyricsDisplay(bool force)
    {
        // AppLog.Info($"UpdateLyricsDisplay: lyricsEnabled={_lyricsEnabled}, hasLyrics={_lyricsService.Manager.HasLyrics}, lineCount={_lyricsService.Manager.LineCount}, position={_engine.CurrentPositionSeconds:F2}s, compact={_mainWindowCompact}, layout={_compactModeLayout}");
        if (!_lyricsEnabled || !_lyricsService.Manager.HasLyrics)
            return;

        var lineIdx = _lyricsService.Manager.GetCurrentLineIndex(_engine.CurrentPositionSeconds);
        if (lineIdx < 0)
            return;
        if (!force && lineIdx == _lyricsService.LastDisplayedLyricLineIndex)
            return;
        _lyricsService.LastDisplayedLyricLineIndex = lineIdx;

        var lyricLine = _lyricsService.Manager.GetCurrentLine(_engine.CurrentPositionSeconds);
        // AppLog.Info($"UpdateLyricsDisplay: GetCurrentLine result={lyricLine ?? "(null)"}");
        if (string.IsNullOrEmpty(lyricLine))
            return;

        // Ultra-Compact: update title bar when lyrics window is NOT open; otherwise only highlight in Lyrics window
        if (_mainWindowCompact && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase))
        {
            if (_lyricsWindow is null || !_lyricsWindow.IsVisible)
            {
                try { Title = $"{lyricLine}"; } catch { /* ignore */ }
            }
            else
            {
                try { _lyricsWindow.RefreshCurrentLine(); } catch { /* ignore */ }
                // Revert title to normal track info when lyrics window is open
                try { Title = $"{_nowPlayingEntry?.Title}{(string.IsNullOrWhiteSpace(_nowPlayingEntry?.Channel) ? "" : $" \u2014 {_nowPlayingEntry?.Channel}")}"; } catch { /* ignore */ }
            }
        }
        // Normal/Compact: refresh status line so the current lyric line updates in real-time
        else
        {
            try
            {
                UpdateNowPlayingText();
                UpdatePlaylistTitleDisplayForNowPlaying();
            }
            catch { /* ignore */ }
        }
    }

    private async Task TryResolveLyricsAsync()
    {
        var entry = _engine.GetCurrent();
        LogLyricsVerbose($"TryResolveLyricsAsync: entry={entry?.VideoId ?? "(null)"}, lyricsEnabled={_lyricsEnabled}, resolvedVideoId={_lyricsService.ResolvedVideoId ?? "(none)"}");
        if (entry is null)
        {
            // When the engine has no current entry (e.g., stopped), preserve lyrics so the
            // lyrics window keeps displaying them. Only clear when there's truly no track.
            if (_nowPlayingEntry is null)
            {
                _lyricsService.ClearParsedLyricsState();
                _lyricsWindow?.Refresh();
            }
            return;
        }

        // Single-flight: if a resolve is already in progress for this exact track id, do not start another request.
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.VideoId) &&
                string.Equals(_lyricsResolveInFlightVideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
            {
                LogLyricsVerbose($"TryResolveLyricsAsync: skip — another resolve for this track is still running (single-flight) id={entry.VideoId}");
                return;
            }
        }
        catch { /* ignore */ }

        // Already resolved for this track — no need to re-resolve
        if (!string.Equals(entry.VideoId, _lyricsService.ResolvedVideoId, StringComparison.OrdinalIgnoreCase))
        {
            var req = Interlocked.Increment(ref _lyricsResolveRequestId);
            var requestedVideoId = entry.VideoId;
            _lyricsService.Manager.Clear();
            _lyricsService.ResolvedVideoId = requestedVideoId;
            _lyricsWindow?.Refresh();

            // Cancel any prior in-flight resolve (track changed / user triggered re-resolve).
            try { _lyricsResolveCts?.Cancel(); } catch { /* ignore */ }
            try { _lyricsResolveCts?.Dispose(); } catch { /* ignore */ }
            _lyricsResolveCts = new CancellationTokenSource();
            var resolveCt = _lyricsResolveCts.Token;
            _lyricsResolveInFlightVideoId = requestedVideoId;

            try
            {
            if (_lyricsEnabled && !string.IsNullOrWhiteSpace(entry.VideoId))
            {
                var isLocalFile = entry.VideoId.StartsWith("local:");

                // Local files: only resolve if enabled in settings
                if (isLocalFile && !_lyricsLocalFilesEnabled)
                {
                    LogLyricsVerbose($"TryResolveLyricsAsync: skipped for {entry.VideoId} — local files lyrics disabled");
                    _lyricsWindow?.Refresh();
                }
                // Local files: resolve via LRCLIB (check cache first, cache results)
                else if (isLocalFile && _lyricsLocalFilesEnabled)
                {
                    var cacheKey = $"lyr_{entry.VideoId}";
                    if (LyricsCache.IsMiss(cacheKey))
                    {
                        LogLyricsVerbose(
                            $"TryResolveLyricsAsync: lyrics cache has a stored miss for {entry.VideoId} (key={cacheKey}) — not re-fetching for ~{LyricsCache.MissEntryTtlHours}h; clear lyrics cache to retry");
                        _lyricsWindow?.Refresh();
                        return;
                    }
                    var cached = LyricsCache.Get(cacheKey);
                    if (cached != null)
                    {
                        var metadata = LrcParser.TryExtractMetadata(cached);
                        var cacheArtist = metadata?.Artist ?? entry.Channel;
                        var cacheTitle = metadata?.Title ?? entry.Title;
                        LogLyricsVerbose($"TryResolveLyricsAsync: cache hit for {entry.VideoId}, lines={LrcParser.Parse(cached, CancellationToken.None).Count}, artist={cacheArtist ?? "(none)"}, title={cacheTitle ?? "(none)"}");
                        _lyricsService.Manager.Parse(cached, artist: cacheArtist, title: cacheTitle);
                        UpdateNowPlayingText();
                        UpdatePlaylistTitleDisplayForNowPlaying();
                        _lyricsWindow?.Refresh();
                        return;
                    }

                    LogLyricsVerbose($"TryResolveLyricsAsync: fetching lyrics for local file {entry.VideoId} via LRCLIB");
                    // WebpageUrl holds the actual file path for local entries (VideoId is local:{filename}:{hash}, not a path)
                    var localFilePath = entry.WebpageUrl;
                    if (string.IsNullOrWhiteSpace(localFilePath))
                    {
                        AppLog.Warn($"TryResolveLyricsAsync: local file has no WebpageUrl, skipping for {entry.VideoId}");
                        _lyricsWindow?.Refresh();
                        return;
                    }
                    // Extract local file path and try to get metadata (title/artist) from file tags
                    string? searchTitle = null;
                    string? searchArtist = null;
                    // Synchronous read — tags + duration, no async overhead.
                    var (syncTitle, syncArtist, syncDuration) = LocalMetadataService.ReadTagsSync(localFilePath);
                    if (!string.IsNullOrWhiteSpace(syncTitle) || !string.IsNullOrWhiteSpace(syncArtist))
                    {
                        searchTitle = syncTitle;
                        searchArtist = syncArtist;
                        LogLyricsVerbose($"TryResolveLyricsAsync: read metadata for {entry.VideoId}: title={searchTitle ?? "(none)"}, artist={searchArtist ?? "(none)"}");
                    }

                    // Use sync duration if available (entry.DurationSeconds may be null if enrichment hasn't finished)
                    var duration = syncDuration ?? entry.DurationSeconds;

                    // Fall back to entry.Title/Channel (which may be filename if enrichment hasn't completed yet)
                    var title = searchTitle ?? entry.Title ?? Path.GetFileNameWithoutExtension(localFilePath);
                    var artist = searchArtist ?? entry.Channel;
                    try
                    {
                        // Preserve a structural separator between the two fields so the LRCLIB query builder
                        // can split into parts and drop trailing junk chunks.
                        var combinedName = $"{title} - {artist}".Trim();
                        var (lrcLrclib, lrclibDuration, lrclibArtist, lrclibName, isPlainLyrics, isDefinitiveMiss) =
                            await LyricsResolver.FetchLyricsFromLrclibAsync(combinedName, artist: null, duration, resolveCt);
                        // Don't discard good results: always cache. Only apply to UI if this is still the active resolve.
                        if (!string.IsNullOrWhiteSpace(lrcLrclib))
                            LyricsCache.Set(cacheKey, lrcLrclib);

                        var stillLatestResolve = req == _lyricsResolveRequestId;
                        var stillCurrentTrack = string.Equals(_engine.GetCurrent()?.VideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase);
                        if (!stillLatestResolve || !stillCurrentTrack)
                        {
                            LogLyricsVerbose(!string.IsNullOrWhiteSpace(lrcLrclib)
                                ? $"TryResolveLyricsAsync: LRCLIB fetch completed but track changed, cached for {requestedVideoId}"
                                : $"TryResolveLyricsAsync: LRCLIB fetch completed but track changed, no lyrics for {requestedVideoId}");
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(lrcLrclib))
                        {
                            // Calculate sync offset: local file duration minus LRCLIB studio duration
                            double syncOffset = 0;
                            if (entry.DurationSeconds.HasValue && lrclibDuration.HasValue)
                            {
                                syncOffset = entry.DurationSeconds.Value - lrclibDuration.Value;
                                LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned lyrics for {entry.VideoId} ({title}), length={lrcLrclib.Length}, lines={LrcParser.Parse(lrcLrclib, CancellationToken.None).Count}, localDur={entry.DurationSeconds}s, lrclibDur={lrclibDuration}s, offset={syncOffset:+0.##;-0.##;0}s, artist={lrclibArtist ?? "(none)"}, name={lrclibName ?? "(none)"}");
                            }
                            else
                            {
                                LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned lyrics for {entry.VideoId} ({title}), length={lrcLrclib.Length}, lines={LrcParser.Parse(lrcLrclib, CancellationToken.None).Count}, no duration for offset calc, artist={lrclibArtist ?? "(none)"}, name={lrclibName ?? "(none)"}");
                            }
                            _lyricsService.Manager.Parse(lrcLrclib, syncOffset, artist: lrclibArtist, title: lrclibName, isPlainLyrics: isPlainLyrics);
                            Dispatcher.Invoke(() =>
                            {
                                try { UpdateNowPlayingText(); } catch { /* ignore */ }
                                try { UpdatePlaylistTitleDisplayForNowPlaying(); } catch { /* ignore */ }
                                try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
                            });
                            return;
                        }
                        else if (isDefinitiveMiss)
                        {
                            LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned no lyrics for {title} (localDur={duration?.ToString("F0") ?? "null"}s)");
                            LyricsCache.SetMiss(cacheKey);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when a newer resolve cancels the in-flight request (single-flight) or during shutdown.
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Exception(ex, $"TryResolveLyricsAsync: LRCLIB fetch failed for {title} (localDur={duration?.ToString("F0") ?? "null"}s)");
                        // Do not treat failures as "resolved" — allow retry later.
                        _lyricsService.ClearResolvedIfStillCurrent(requestedVideoId);
                    }
                    _lyricsWindow?.Refresh();
                }
                // YouTube videos: use yt-dlp with LRCLIB fallback
                else
                {
                    // Check cache first
                    var cacheKey = $"yt_{entry.VideoId}";
                    if (LyricsCache.IsMiss(cacheKey))
                    {
                        LogLyricsVerbose(
                            $"TryResolveLyricsAsync: lyrics cache has a stored miss for {entry.VideoId} (key={cacheKey}) — not re-fetching for ~{LyricsCache.MissEntryTtlHours}h; clear lyrics cache to retry");
                        _lyricsWindow?.Refresh();
                        return;
                    }
                    var cached = LyricsCache.Get(cacheKey);
                    if (cached != null)
                    {
                        var metadata = LrcParser.TryExtractMetadata(cached);
                        var cacheArtist = metadata?.Artist ?? entry.Channel;
                        var cacheTitle = metadata?.Title ?? entry.Title;
                        LogLyricsVerbose($"TryResolveLyricsAsync: cache hit for {entry.VideoId}, lines={LrcParser.Parse(cached, CancellationToken.None).Count}, artist={cacheArtist ?? "(none)"}, title={cacheTitle ?? "(none)"}");
                        _lyricsService.Manager.Parse(cached, artist: cacheArtist, title: cacheTitle);
                        UpdateNowPlayingText();
                        UpdatePlaylistTitleDisplayForNowPlaying();
                        _lyricsWindow?.Refresh();
                        return;
                    }

                    LogLyricsVerbose($"TryResolveLyricsAsync: fetching lyrics for {requestedVideoId} via yt-dlp at '{ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp").EffectiveFileName}'");
                    // Fetch from YouTube via yt-dlp
                    var resolvedYtDlp = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
                    try
                    {
                        var lrc = await LyricsResolver.FetchLyricsForYouTubeAsync(resolvedYtDlp.EffectiveFileName, entry.VideoId, resolveCt);
                        if (!string.IsNullOrWhiteSpace(lrc))
                            LyricsCache.Set(cacheKey, lrc);

                        var stillLatestResolve = req == _lyricsResolveRequestId;
                        var stillCurrentTrack = string.Equals(_engine.GetCurrent()?.VideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase);
                        if (!stillLatestResolve || !stillCurrentTrack)
                        {
                            LogLyricsVerbose(!string.IsNullOrWhiteSpace(lrc)
                                ? $"TryResolveLyricsAsync: yt-dlp fetch completed but track changed, cached for {requestedVideoId}"
                                : $"TryResolveLyricsAsync: yt-dlp fetch completed but track changed, no lyrics for {requestedVideoId}");
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(lrc))
                        {
                            var metadata = LrcParser.TryExtractMetadata(lrc);
                            var ytArtist = metadata?.Artist ?? entry.Channel;
                            var ytTitle = metadata?.Title ?? entry.Title;
                            LogLyricsVerbose($"TryResolveLyricsAsync: fetched lyrics for {requestedVideoId}, length={lrc.Length}, lines={LrcParser.Parse(lrc, CancellationToken.None).Count}, artist={ytArtist ?? "(none)"}, title={ytTitle ?? "(none)"}");
                            _lyricsService.Manager.Parse(lrc, artist: ytArtist, title: ytTitle);
                            Dispatcher.Invoke(() =>
                            {
                                try { UpdateNowPlayingText(); } catch { /* ignore */ }
                                try { UpdatePlaylistTitleDisplayForNowPlaying(); } catch { /* ignore */ }
                                try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
                            });
                        }
                        else
                        {
                            LogLyricsVerbose($"TryResolveLyricsAsync: yt-dlp returned no lyrics for {requestedVideoId}, trying LRCLIB fallback");
                            _lyricsWindow?.Refresh();
                            // Fallback: try LRCLIB using track title and channel (artist)
                            var title = entry.Title ?? "";
                            // Strip " - Topic" so we do not build "Askel - Topic" (right chunk is dropped as junk) when the real artist is "Viikate - Topic".
                            var channelForCombine = LyricsResolver.NormalizeYoutubeChannelForLrclib(entry.Channel);
                            var combinedName = $"{title} - {channelForCombine}".Trim();
                            try
                            {
                                var (lrcLrclib, lrclibDuration, lrclibArtist, lrclibName, isPlainLyrics, isDefinitiveMiss) =
                                    await LyricsResolver.FetchLyricsFromLrclibAsync(combinedName, entry.Channel, entry.DurationSeconds, resolveCt);
                                if (!string.IsNullOrWhiteSpace(lrcLrclib))
                                    LyricsCache.Set(cacheKey, lrcLrclib);

                                stillLatestResolve = req == _lyricsResolveRequestId;
                                stillCurrentTrack = string.Equals(_engine.GetCurrent()?.VideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase);
                                if (!stillLatestResolve || !stillCurrentTrack)
                                {
                                    LogLyricsVerbose(!string.IsNullOrWhiteSpace(lrcLrclib)
                                        ? $"TryResolveLyricsAsync: LRCLIB fetch completed but track changed, cached for {requestedVideoId}"
                                        : $"TryResolveLyricsAsync: LRCLIB fetch completed but track changed, no lyrics for {requestedVideoId}");
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace(lrcLrclib))
                                {
                                    // Calculate sync offset: YouTube duration minus LRCLIB studio duration
                                    // Positive offset means YouTube has a longer intro (lyrics start later)
                                    // Negative offset means YouTube has a shorter intro (lyrics start earlier)
                                    double syncOffset = 0;
                                    if (entry.DurationSeconds.HasValue && lrclibDuration.HasValue)
                                    {
                                        syncOffset = entry.DurationSeconds.Value - lrclibDuration.Value;
                                        LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned lyrics for {entry.VideoId} ({title}), length={lrcLrclib.Length}, lines={LrcParser.Parse(lrcLrclib, CancellationToken.None).Count}, ytDur={entry.DurationSeconds}s, lrclibDur={lrclibDuration}s, offset={syncOffset:+0.##;-0.##;0}s, artist={lrclibArtist ?? "(none)"}, name={lrclibName ?? "(none)"}");
                                    }
                                    else
                                    {
                                        LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned lyrics for {entry.VideoId} ({title}), length={lrcLrclib.Length}, lines={LrcParser.Parse(lrcLrclib, CancellationToken.None).Count}, no duration for offset calc, artist={lrclibArtist ?? "(none)"}, name={lrclibName ?? "(none)"}");
                                    }
                                    _lyricsService.Manager.Parse(lrcLrclib, syncOffset, artist: lrclibArtist, title: lrclibName, isPlainLyrics: isPlainLyrics);
                                    Dispatcher.Invoke(() =>
                                    {
                                        try { UpdateNowPlayingText(); } catch { /* ignore */ }
                                        try { UpdatePlaylistTitleDisplayForNowPlaying(); } catch { /* ignore */ }
                                        try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
                                    });
                                    return;
                                }
                                else if (isDefinitiveMiss)
                                {
                                    LogLyricsVerbose($"TryResolveLyricsAsync: LRCLIB returned no lyrics for {title} (ytDur={entry.DurationSeconds?.ToString("F0") ?? "null"}s)");
                                    LyricsCache.SetMiss(cacheKey);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected when a newer resolve cancels the in-flight request (single-flight) or during shutdown.
                                return;
                            }
                            catch (Exception ex)
                            {
                                AppLog.Exception(ex, $"TryResolveLyricsAsync: LRCLIB fetch failed for {title} (ytDur={entry.DurationSeconds?.ToString("F0") ?? "null"}s)");
                                // Do not treat failures as "resolved" — allow retry later.
                                _lyricsService.ClearResolvedIfStillCurrent(entry.VideoId);
                            }

                            _lyricsWindow?.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Exception(ex, $"TryResolveLyricsAsync: fetch failed for {entry.VideoId}");
                        // Do not treat failures as "resolved" — allow retry later.
                        _lyricsService.ClearResolvedIfStillCurrent(entry.VideoId);
                    }

                    _lyricsWindow?.Refresh();
                }
            }
            else
            {
                LogLyricsVerbose($"TryResolveLyricsAsync: skipped for {entry.VideoId} — enabled={_lyricsEnabled}, videoId={(string.IsNullOrWhiteSpace(entry.VideoId) ? "(empty)" : entry.VideoId)}");
                _lyricsWindow?.Refresh();
            }

            }
            finally
            {
                try { _lyricsResolveInFlightVideoId = null; } catch { /* ignore */ }
            }

        }
        else
        {
            LogLyricsVerbose($"TryResolveLyricsAsync: already resolved for {entry.VideoId}, skipping");
        }
    }

    /// <summary>
    /// Ensures the shuffle buffer has at least 3 upcoming tracks pre-populated.
    /// Must be called when shuffle is enabled and the playlist is loaded (including app startup).
    /// </summary>
    private void _EnsureShuffleBufferHasItems()
    {
        if (_shuffleNextBuffer.Count >= 3)
            return;

        var currentId = _engine.GetCurrent()?.VideoId;
        var candidates = _playlistCore.Entries
            .Where(e => !string.Equals(e.VideoId, currentId, StringComparison.OrdinalIgnoreCase)
                     && !_playOrder.RecentlyPlayedContains(e.VideoId)
                     && !_unavailableVideoIds.Contains(e.VideoId)
                     && !_ageRestrictedVideoIds.Contains(e.VideoId)
                     && !_premiumVideoIds.Contains(e.VideoId))
            .ToList();

        while (_shuffleNextBuffer.Count < 3 && candidates.Count > 0)
        {
            var rndIndex = _shuffleRandom.Next(candidates.Count);
            var entry = candidates[rndIndex];
            _shuffleNextBuffer.Enqueue(entry);
            candidates.RemoveAt(rndIndex); // don't pick the same track again
        }

        AppLog.Info($"Shuffle buffer populated: {_shuffleNextBuffer.Count} items ready");
    }

    /// <summary>
    /// Computes the next track based on the current engine state and preheats its lyrics.
    /// Called from NowPlayingChanged so preheat runs during the current track's full duration,
    /// ensuring lyrics are cached before the user navigates to the next track.
    /// </summary>
    private void PreheatNextLyricsAsync()
    {
        if (!_lyricsEnabled)
            return;

        var currentEntry = _engine.GetCurrent();
        if (currentEntry is null)
            return;
        var nextEntry = PeekNextTrackForPreheatOrPrefetch();
        if (nextEntry is not null)
            _ = PreheatLyricsAsync(nextEntry);
    }

    /// <summary>
    /// Pre-fetches and caches lyrics for a track without updating the UI.
    /// Called from PreheatNextLyricsAsync (via NowPlayingChanged) to preheat lyrics for the next track.
    /// </summary>
    private async Task PreheatLyricsAsync(PlaylistEntry entry)
    {
        try
        {
            if (!_lyricsEnabled || string.IsNullOrWhiteSpace(entry.VideoId))
                return;

            try
            {
                var kind = entry.VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? "local file" : "YouTube";
                LogLyricsVerbose($"Preheat: starting to preheat lyrics for {kind} {entry.VideoId}");
            }
            catch { /* ignore */ }

            var cacheKey = entry.VideoId.StartsWith("local:") ? $"lyr_{entry.VideoId}" : $"yt_{entry.VideoId}";
            if (LyricsCache.IsMiss(cacheKey))
            {
                LogLyricsVerbose(
                    $"Preheat: skip — lyrics cache has stored miss for {entry.VideoId} (key={cacheKey}), no fetch until ~{LyricsCache.MissEntryTtlHours}h TTL expires");
                return;
            }
            var cached = LyricsCache.Get(cacheKey);
            if (cached != null)
            {
                LogLyricsVerbose($"Preheat: skip — already cached for {entry.VideoId} (key={cacheKey})");
                return;
            }

            if (entry.VideoId.StartsWith("local:"))
            {
                // Local file: fetch from LRCLIB
                // WebpageUrl holds the actual file path (VideoId is local:{filename}:{hash}, not a path)
                var localFilePath = entry.WebpageUrl;
                if (string.IsNullOrWhiteSpace(localFilePath))
                    return;
                var (syncTitle, syncArtist, syncDuration) = LocalMetadataService.ReadTagsSync(localFilePath);
                var title = syncTitle ?? entry.Title ?? Path.GetFileNameWithoutExtension(localFilePath);
                var artist = syncArtist ?? entry.Channel;
                var duration = syncDuration ?? entry.DurationSeconds;

                var (lrcLrclib, _, _, _, _, isDefinitiveMiss) =
                    await LyricsResolver.FetchLyricsFromLrclibAsync(title, artist, duration, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(lrcLrclib))
                {
                    LyricsCache.Set(cacheKey, lrcLrclib);
                    LogLyricsVerbose($"Preheat: cached lyrics for local file {entry.VideoId}");
                }
                else if (isDefinitiveMiss)
                {
                    LyricsCache.SetMiss(cacheKey);
                    LogLyricsVerbose($"Preheat: stored lyrics miss (local) for {entry.VideoId} (key={cacheKey})");
                }
            }
            else
            {
                // YouTube: fetch from yt-dlp, fallback to LRCLIB
                var resolvedYtDlp = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
                var lrc = await LyricsResolver.FetchLyricsForYouTubeAsync(resolvedYtDlp.EffectiveFileName, entry.VideoId, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(lrc))
                {
                    LyricsCache.Set(cacheKey, lrc);
                    LogLyricsVerbose($"Preheat: cached lyrics for {entry.VideoId} via yt-dlp");
                }
                else
                {
                    // Fallback to LRCLIB
                    var title = entry.Title ?? "";
                    var (lrcLrclib, _, _, _, _, isDefinitiveMiss) =
                        await LyricsResolver.FetchLyricsFromLrclibAsync(title, entry.Channel, entry.DurationSeconds, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(lrcLrclib))
                    {
                        LyricsCache.Set(cacheKey, lrcLrclib);
                        LogLyricsVerbose($"Preheat: cached lyrics for {entry.VideoId} via LRCLIB");
                    }
                    else if (isDefinitiveMiss)
                    {
                        LyricsCache.SetMiss(cacheKey);
                        LogLyricsVerbose($"Preheat: stored lyrics miss (YouTube/LRCLIB) for {entry.VideoId} (key={cacheKey})");
                    }
                }
            }
        }
        catch
        {
            // ignore pre-fetch failures
        }
    }

    private void CleanupLegacyNativeTrayIconsBestEffort()
    {
        if (_nativeTrayCleanedUp)
            return;
        _nativeTrayCleanedUp = true;

        try
        {
            // Remove any old native tray icon entries (from previous implementations) so we don't show duplicates.
            var hwndMain = new WindowInteropHelper(this).Handle;
            if (hwndMain != IntPtr.Zero)
                RemoveTrayIconNative(hwndMain);
        }
        catch { /* ignore */ }

        try
        {
            var hwndTray = _trayMessageHwnd;
            if (hwndTray != IntPtr.Zero)
                RemoveTrayIconNative(hwndTray);
        }
        catch { /* ignore */ }

        try { _trayIconAdded = false; } catch { /* ignore */ }
    }

    private void EnsureTrayIconNativeAdded(IntPtr hwnd)
    {
        if (_trayIconAdded)
            return;

        EnsureTrayIconLoaded();
        if (_trayIcon is null)
            return;

        // Best-effort cleanup: Explorer can temporarily retain a stale icon entry if the app previously exited
        // unexpectedly. Delete by (hWnd,uID) before adding.
        try
        {
            var del = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = TrayUid,
                uFlags = 0,
            };
            _ = Shell_NotifyIcon(NIM_DELETE, ref del);
        }
        catch { /* ignore */ }

        try
        {
            var main = new WindowInteropHelper(this).Handle;
            LogShellState("BeforeNimAdd", main, hwnd);
        }
        catch { /* ignore */ }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayUid,
            // No NIF_TIP: disable Explorer hover tooltip (not themeable).
            uFlags = NIF_MESSAGE | NIF_ICON,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIcon.Handle,
            szTip = "",
        };

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
            return;

        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        _ = Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _trayIconAdded = true;

        try
        {
            var main = new WindowInteropHelper(this).Handle;
            LogShellState("AfterNimAdd", main, hwnd);
        }
        catch { /* ignore */ }
    }

    private void SetTrayIconHiddenNative(IntPtr hwnd, bool hidden)
    {
        if (!_trayIconAdded)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayUid,
            // No NIF_TIP: keep tooltip disabled even when toggling hidden/shown.
            uFlags = NIF_STATE,
            dwStateMask = NIS_HIDDEN,
            dwState = hidden ? NIS_HIDDEN : 0,
            szTip = "",
        };
        _ = Shell_NotifyIcon(NIM_MODIFY, ref data);
        try
        {
            var main = new WindowInteropHelper(this).Handle;
            LogShellState(hidden ? "NimModifyHidden" : "NimModifyShown", main, hwnd);
        }
        catch { /* ignore */ }
    }

    private void RemoveTrayIconNative(IntPtr hwnd)
    {
        if (!_trayIconAdded)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayUid,
            uFlags = 0,
        };
        _ = Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayIconAdded = false;
    }

    private void ApplyAppIconVisibilityFromSettings()
    {
        try
        {
            // Apply shell integration only once the HWND exists.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                if (_queuedApplyAppIconVisibility)
                    return;
                _queuedApplyAppIconVisibility = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _queuedApplyAppIconVisibility = false;
                    try { ApplyAppIconVisibilityFromSettings(); } catch { /* ignore */ }
                }), DispatcherPriority.Loaded);
                return;
            }

            var m = SettingsStore.NormalizeAppIconVisibility(_appIconVisibility);
            var showTaskbar = m is "TaskbarOnly" or "TaskbarAndTray";
            var showTray = m is "TrayOnly" or "TaskbarAndTray";

            try { CleanupLegacyNativeTrayIconsBestEffort(); } catch { /* ignore */ }

            // On first apply, detect the current taskbar visibility from native styles so we don't
            // rebuild the taskbar button unnecessarily at startup (which is the remaining gap repro).
            if (_lastAppliedShowInTaskbar is null)
            {
                try
                {
                    var ex0 = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                    var nativeShowsTaskbar = (ex0 & WS_EX_TOOLWINDOW) == 0;
                    _lastAppliedShowInTaskbar = nativeShowsTaskbar;
                }
                catch { /* ignore */ }
            }

            // Tray icon + context menu via Hardcodet.Wpf.NotifyIcon (previously best-behaving in this app).
            try
            {
                if (_lastAppliedShowTray != showTray)
                {
                    // Startup workaround: only create/show the tray icon after the user manually focuses/interacts
                    // with the main window (click or key). Toggle in Options happens after interaction, so it works.
                    if (showTray && !_userActivatedTrayAllowed)
                    {
                        // Do not add the tray icon yet.
                        return;
                    }

                    EnsureHardcodetTrayIconCreated();
                    if (_hardcodetTrayIcon is not null)
                    {
                        try { _hardcodetTrayIcon.Icon = _trayIcon; } catch { /* ignore */ }
                        try { _hardcodetTrayIcon.ToolTipText = ""; } catch { /* ignore */ }
                        _hardcodetTrayIcon.Visibility = showTray ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (showTray)
                    {
                        try { _ = TrayRefresher.RefreshTrayLayoutBestEffortAsync(); } catch { /* ignore */ }
                    }
                    _lastAppliedShowTray = showTray;
                }
            }
            catch { /* ignore */ }

            // Avoid constantly "jolting" Explorer with style rebuilds when the mode hasn't changed.
            // Rebuilding the taskbar button can cause a temporary right-edge gap in the notification area
            // on some Win10 configurations until the window is activated again.
            if (_lastAppliedShowInTaskbar != showTaskbar)
            {
                ApplyTaskbarVisibilityNative(showTaskbar);
                _lastAppliedShowInTaskbar = showTaskbar;
            }
            // ShowInTaskbar is set inside ApplyTaskbarVisibilityNative (which also uses ITaskbarList).
            // Taskbar rebuilds can leave a cosmetic tray gap on some Win10 builds until the user interacts
            // with the tray — do not steal/restore foreground to "fix" it (breaks focus for all apps).
        }
        catch { /* ignore */ }
    }

    private void ApplyTaskbarVisibilityNative(bool showInTaskbar)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ApplyTaskbarVisibilityNative(showInTaskbar); } catch { /* ignore */ }
                }), DispatcherPriority.Loaded);
                return;
            }

            // Do NOT use WS_EX_TOOLWINDOW to hide from taskbar; it hides from Task Manager "Apps" too.
            // Instead, use a combination of:
            // - WS_EX_APPWINDOW on when we want a taskbar button
            // - WS_EX_APPWINDOW off when we do not
            // - ITaskbarList DeleteTab/AddTab as a best-effort shell hint
            // - ShowInTaskbar for WPF consistency
            try
            {
                var before = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                var ex = before;
                ex &= ~WS_EX_TOOLWINDOW;
                if (showInTaskbar)
                    ex |= WS_EX_APPWINDOW;
                else
                    ex &= ~WS_EX_APPWINDOW;

                if (ex != before)
                {
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(unchecked((int)ex)));
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }
            catch { /* ignore */ }

            // Hide/show the taskbar button via ITaskbarList (shell hint). Only when the desired state changes:
            // ApplyMainWindowShellIntegration runs ~1s from the UI timer; repeating AddTab forever spams the shell
            // and breaks activation/focus with DisplayFusion and similar taskbar hooks.
            if (_lastNativeTaskbarListShowInTaskbar != showInTaskbar)
            {
                try
                {
                    _taskbarList ??= (ITaskbarList)new CTaskbarList();
                    _taskbarList.HrInit();
                    if (showInTaskbar)
                        _taskbarList.AddTab(hwnd);
                    else
                        _taskbarList.DeleteTab(hwnd);
                    _lastNativeTaskbarListShowInTaskbar = showInTaskbar;
                }
                catch { /* ignore */ }
            }

            // Keep WPF's property in sync (best-effort; it can be flaky with custom chrome).
            try
            {
                if (ShowInTaskbar != showInTaskbar)
                    ShowInTaskbar = showInTaskbar;
            }
            catch { /* ignore */ }
            try
            {
                var tray = _trayMessageHwnd != IntPtr.Zero ? _trayMessageHwnd : hwnd;
                // LogShellState(showInTaskbar ? "TaskbarNativeShow" : "TaskbarNativeHide", hwnd, tray);
            }
            catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Options is a fixed 640×480 design with <see cref="LayoutTransform"/>; outer chrome must be that size times <see cref="UiScale"/>.</summary>
    private void ApplyOptionsWindowScaledChromeSize(System.Windows.Window w)
    {
        var s = UiScale;
        const double optW = 640.0;
        const double optH = 480.0;
        var ow = optW * s;
        var oh = optH * s;
        w.Width = ow;
        w.Height = oh;
        w.MinWidth = ow;
        w.MaxWidth = ow;
        w.MinHeight = oh;
        w.MaxHeight = oh;
    }

    private void ApplyScaledFixedWindowSizes()
    {
        // Content uses LayoutTransform (App.UiScaleTransform), so layout grows by UiScale. Fixed window widths
        // must match that footprint or horizontal space is clamped while SizeToContent still grows height.
        var s = UiScale;
        try
        {
            const double mainW = 600.0;
            var w = mainW * s;
            Width = w;
            MinWidth = w;
            MaxWidth = w;

            // Height is content-driven (SizeToContent="Height").
            ClearValue(HeightProperty);
            ClearValue(MinHeightProperty);
            ClearValue(MaxHeightProperty);
            SizeToContent = System.Windows.SizeToContent.Height;
        }
        catch { /* ignore */ }

        try
        {
            if (_optionsWindow is not null)
                ApplyOptionsWindowScaledChromeSize(_optionsWindow);
        }
        catch { /* ignore */ }

        try
        {
            if (_playlistWindow is not null)
            {
                var minPlW = 420.0 * s;
                var minPlH = 320.0 * s;
                _playlistWindow.MinWidth = minPlW;
                _playlistWindow.MinHeight = minPlH;
                if (_playlistWindowOuterAtUiScalePercent is int prevPct)
                {
                    var oldS = Math.Clamp(prevPct / 100.0, 0.5, 2.0);
                    var ratio = s / oldS;
                    if (Math.Abs(ratio - 1.0) > 1e-6)
                    {
                        _playlistWindow.Width = Math.Max(minPlW, _playlistWindow.Width * ratio);
                        _playlistWindow.Height = Math.Max(minPlH, _playlistWindow.Height * ratio);
                    }
                }

                _playlistWindowOuterAtUiScalePercent = _uiScalePercent;
            }
        }
        catch { /* ignore */ }
    }

    private Models.ThemeSettings GetCurrentThemeSettings()
        => new(
            ThemeMode: _themeMode,
            BackgroundMode: _backgroundMode,
            CustomBackgroundImagePath: _customBackgroundImagePath,
            BackgroundColorMode: _backgroundColorMode,
            CustomBackgroundColor: _customBackgroundColor,
            BackgroundAlpha: _backgroundAlpha,
            BackgroundScrimPercent: _backgroundScrimPercent,
            BackgroundImageStretch: _backgroundImageStretch,
            BackgroundUserDefinedMainNormal: _backgroundUserDefinedMainNormal,
            BackgroundUserDefinedMainCompact: _backgroundUserDefinedMainCompact,
            BackgroundUserDefinedMainUltra: _backgroundUserDefinedMainUltra,
            BackgroundUserDefinedPlaylist: _backgroundUserDefinedPlaylist,
            BackgroundUserDefinedOptionsLog: _backgroundUserDefinedOptionsLog,
            BackgroundUserDefinedLyrics: _backgroundUserDefinedLyrics,
            AppTitleMode: _appTitleMode,
            CustomAppTitle: _customAppTitle,
            UiScalePercent: _uiScalePercent,
            WindowBorderMode: _windowBorderMode,
            WindowBorderCustomPx: _windowBorderCustomPx
        );

    private void ApplyThemeSettings(Models.ThemeSettings t)
    {
        try
        {
            _themeMode = SettingsStore.NormalizeThemeMode(t.ThemeMode);
            _backgroundMode = SettingsStore.NormalizeBackgroundMode(t.BackgroundMode);
            _customBackgroundImagePath = t.CustomBackgroundImagePath ?? "";
            _backgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(t.BackgroundColorMode);
            _customBackgroundColor = t.CustomBackgroundColor ?? "";
            _backgroundAlpha = t.BackgroundAlpha is >= 0 and <= 255 ? t.BackgroundAlpha.Value : _backgroundAlpha;
            _backgroundScrimPercent = t.BackgroundScrimPercent is >= 0 and <= 80 ? t.BackgroundScrimPercent.Value : _backgroundScrimPercent;
            _backgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(t.BackgroundImageStretch ?? _backgroundImageStretch);
            _backgroundUserDefinedMainNormal = t.BackgroundUserDefinedMainNormal;
            _backgroundUserDefinedMainCompact = t.BackgroundUserDefinedMainCompact;
            _backgroundUserDefinedMainUltra = t.BackgroundUserDefinedMainUltra;
            _backgroundUserDefinedPlaylist = t.BackgroundUserDefinedPlaylist;
            _backgroundUserDefinedOptionsLog = t.BackgroundUserDefinedOptionsLog;
            _backgroundUserDefinedLyrics = t.BackgroundUserDefinedLyrics;
            _appTitleMode = string.IsNullOrWhiteSpace(t.AppTitleMode) ? _appTitleMode : t.AppTitleMode.Trim();
            _customAppTitle = t.CustomAppTitle ?? _customAppTitle;
            _uiScalePercent = t.UiScalePercent is >= 50 and <= 200 ? t.UiScalePercent.Value : _uiScalePercent;
            if (!string.IsNullOrWhiteSpace(t.WindowBorderMode))
                _windowBorderMode = NormalizeWindowBorderMode(t.WindowBorderMode);
            if (t.WindowBorderCustomPx is not null)
                _windowBorderCustomPx = Math.Clamp(t.WindowBorderCustomPx.Value, 1, 24);

            ApplyBackgroundFromSettings();
            ApplyBackgroundColorsFromSettings();
            ApplyUiScale();
            ApplyAppTitleFromSettings();
            RequestPersistSnapshot();
        }
        catch { /* ignore */ }
    }

    private static string NormalizeWindowBorderMode(string? mode)
    {
        var m = string.IsNullOrWhiteSpace(mode) ? "1px" : mode.Trim();
        if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase))
            return "None";
        if (string.Equals(m, "1px", StringComparison.OrdinalIgnoreCase))
            return "1px";
        if (string.Equals(m, "Custom", StringComparison.OrdinalIgnoreCase))
            return "1px";
        return "1px";
    }

    private void ApplyWindowBorderFromSettings()
    {
        try
        {
            var vBase = ResolveWindowBorderThickness(_windowBorderMode, _windowBorderCustomPx);
            // Border chrome is outside LayoutTransform; scale thickness with UI scale so frame matches scaled content.
            var v = vBase * UiScale;
            System.Windows.Application.Current.Resources["App.Theme.WindowBorderThickness"] =
                new System.Windows.Thickness(v);
            // Uniform thickness: WPF joins inner/outer rounded outlines cleanly (asymmetric NoTop caused square inner arcs).
            // Top frame band is covered by title bar top Padding (see WindowChromeTitleBarPadding).
            System.Windows.Application.Current.Resources["App.Theme.WindowBorderThicknessNoTop"] =
                new System.Windows.Thickness(v);
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeContentMargin"] =
                new System.Windows.Thickness(0);
            // Horizontal bleed only: negative top was clipped by RoundedChromeClip (root geometry starts at y=0).
            System.Windows.Application.Current.Resources["App.Theme.WindowBorderThicknessNegativeSides"] =
                new System.Windows.Thickness(-v, 0, -v, 0);
            System.Windows.Application.Current.Resources["App.Theme.WindowBorderThicknessNoBottom"] =
                new System.Windows.Thickness(v, v, v, 0);

            // 1px mode (design thickness): tight rounded clip can trim anti-aliased joins; keep clip off for thin only.
            var thinStroke = vBase > 0 && vBase <= 1.0;
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeSnapsToDevicePixels"] = true;
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeRoundedClipEnabled"] = !thinStroke;

            var inner = Math.Max(0, 8 - v);
            System.Windows.Application.Current.Resources["App.Theme.WindowCornerRadiusOuter"] =
                new System.Windows.CornerRadius(8);
            System.Windows.Application.Current.Resources["App.Theme.WindowCornerRadiusOuterTopOnly"] =
                new System.Windows.CornerRadius(8, 8, 0, 0);
            System.Windows.Application.Current.Resources["App.Theme.WindowCornerRadiusInner"] =
                new System.Windows.CornerRadius(inner);
            System.Windows.Application.Current.Resources["App.Theme.WindowCornerRadiusInnerTopOnly"] =
                new System.Windows.CornerRadius(inner, inner, 0, 0);
            System.Windows.Application.Current.Resources["App.Theme.WindowCornerRadiusInnerBottomOnly"] =
                new System.Windows.CornerRadius(0, 0, inner, inner);

            System.Windows.Application.Current.Resources["App.Theme.WindowChromeStrokeWidth"] = v;

            var pad = v > 0 ? Math.Ceiling(v) : 0;
            // Body inset from frame ring; title row uses WindowChromeTitleBarContentMargin to line up with first body row (12px gutter).
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeBodyInsetMargin"] =
                pad > 0 ? new System.Windows.Thickness(pad, 0, pad, pad) : new System.Windows.Thickness(0);
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeInsetMargin"] = new System.Windows.Thickness(0);

            const double chromeContentGutterPx = 12.0;
            var titleContentHInset = pad + chromeContentGutterPx + v;
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeTitleBarContentMargin"] =
                new System.Windows.Thickness(titleContentHInset, 0, titleContentHInset, 0);
            System.Windows.Application.Current.Resources["App.Theme.WindowChromeTitleBarPadding"] =
                v > 0 ? new System.Windows.Thickness(0, v, 0, 0) : new System.Windows.Thickness(0);

            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(RoundedChromeClip.RefreshAll));

            try
            {
                ApplyMainWindowCompactLayoutDensity();
            }
            catch
            {
                // ignore
            }
        }
        catch { /* ignore */ }
    }

    private static double ResolveWindowBorderThickness(string mode, double customPx)
    {
        var m = string.IsNullOrWhiteSpace(mode) ? "None" : mode.Trim();
        if (string.Equals(m, "1px", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(m, "Custom", StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(customPx, 1, 24);
        return 0;
    }

    private void ApplyBackgroundColorsFromSettings()
    {
        try
        {
            var mode = (_backgroundColorMode ?? "Default").Trim();
            var alpha = (byte)Math.Clamp(_backgroundAlpha, 0, 255);

            var effectiveDark = IsEffectiveDarkThemeForBackground();

            if (string.Equals(mode, "Windows", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Windows theme", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "WindowsTheme", StringComparison.OrdinalIgnoreCase))
            {
                ApplyWindowsThemeBrushes(alpha);
                return;
            }

            if (string.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDefaultBrushesWithAlpha(alpha, effectiveDark);
                ApplyDefaultThemeBrushes(alpha, effectiveDark);
                return;
            }

            System.Windows.Media.Color? baseColor = null;

            if (string.Equals(mode, "From image", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "FromImage", StringComparison.OrdinalIgnoreCase))
            {
                baseColor = TryGetAverageColorFromCurrentBackgroundImage(dropBrightestPercent: 0.15);
            }
            else if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                baseColor = TryParseHexColor(_customBackgroundColor);
            }

            if (baseColor is null)
            {
                // RestoreDefaultBrushes alone leaves App.Theme.* stale; mirror Default path for coherent palette.
                ApplyDefaultBrushesWithAlpha(alpha, effectiveDark);
                ApplyDefaultThemeBrushes(alpha, effectiveDark);
                return;
            }

            var bc = baseColor.Value;
            var chromeA = MapThemeChromeAlpha(alpha);
            var light = System.Windows.Media.Color.FromArgb(chromeA, bc.R, bc.G, bc.B);
            var dark = System.Windows.Media.Color.FromArgb(chromeA,
                (byte)Math.Clamp((int)Math.Round(bc.R * 0.75), 0, 255),
                (byte)Math.Clamp((int)Math.Round(bc.G * 0.75), 0, 255),
                (byte)Math.Clamp((int)Math.Round(bc.B * 0.75), 0, 255));

            ApplyThemeBrushesFromBaseColor(bc, alpha, preferDarkTheme: effectiveDark);

            SetSystemBrush(System.Windows.SystemColors.ControlLightBrushKey, light);
            SetSystemBrush(System.Windows.SystemColors.ControlDarkBrushKey, dark);
            // GrayTextBrushKey is updated in ApplyThemeBrushesFromBaseColor (contrast-based).
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { RefreshVuAndSpectrumThemeBindings(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Re-bind VU/spectrum visuals to <see cref="Application"/> theme brushes after <c>App.Theme.*</c> objects are replaced
    /// (custom / from-image tint). Style-based <c>DynamicResource</c> on <see cref="ProgressBar"/> can otherwise keep stale brushes.
    /// </summary>
    private void RefreshVuAndSpectrumThemeBindings()
    {
        try
        {
            if (VuLeftBar is not null)
            {
                VuLeftBar.SetResourceReference(System.Windows.Controls.ProgressBar.BackgroundProperty, "App.Theme.Surface");
                VuLeftBar.SetResourceReference(System.Windows.Controls.ProgressBar.ForegroundProperty, "App.Theme.Foreground");
                TryBindVuProgressBarTemplateBrushes(VuLeftBar);
            }
            if (VuRightBar is not null)
            {
                VuRightBar.SetResourceReference(System.Windows.Controls.ProgressBar.BackgroundProperty, "App.Theme.Surface");
                VuRightBar.SetResourceReference(System.Windows.Controls.ProgressBar.ForegroundProperty, "App.Theme.Foreground");
                TryBindVuProgressBarTemplateBrushes(VuRightBar);
            }
            if (SpectrumPanelChrome is not null)
            {
                SpectrumPanelChrome.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "App.Theme.SpectrumPanelBackground");
                SpectrumPanelChrome.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "App.Theme.Border");
            }
            if (VuMeterLeftLabel is not null)
                VuMeterLeftLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "App.Theme.ForegroundSubtle");
            if (VuMeterRightLabel is not null)
                VuMeterRightLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "App.Theme.ForegroundSubtle");
        }
        catch { /* ignore */ }

        RefreshSpectrumGridLineBrushes();
    }

    /// <summary>
    /// Binds VU template <c>PART_*</c> borders to theme brushes. Needed because implicit <see cref="Control"/> styles can
    /// leave <see cref="ProgressBar.Foreground"/> ineffective for <see cref="TemplateBinding"/> until local/theme refresh.
    /// </summary>
    private static void TryBindVuProgressBarTemplateBrushes(System.Windows.Controls.ProgressBar? bar)
    {
        if (bar is null)
            return;
        try
        {
            bar.ApplyTemplate();
            if (bar.Template?.FindName("PART_Track", bar) is System.Windows.Controls.Border track)
            {
                track.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "App.Theme.Surface");
                track.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "App.Theme.Border");
            }
            if (bar.Template?.FindName("PART_Indicator", bar) is System.Windows.Controls.Border ind)
                ind.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "App.Theme.Foreground");
        }
        catch { /* ignore */ }
    }

    private void RefreshSpectrumGridLineBrushes()
    {
        ApplySpectrumGridLineFills();
        ApplySpectrumCurveFill();
    }

    /// <summary>Spectrum fill uses <c>App.Theme.Foreground</c> (same semantic as VU level fill).</summary>
    private void ApplySpectrumCurveFill()
    {
        if (_spectrumCurvePath is null)
            return;
        try
        {
            _spectrumCurvePath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Theme.Foreground");
        }
        catch { /* ignore */ }
    }

    /// <summary>Spectrum grid lines: <c>App.Theme.SpectrumGridLine</c> (subtle foreground darkened toward border).</summary>
    private void ApplySpectrumGridLineFills()
    {
        if (_spectrumGridLines is null || _spectrumGridLines.Length == 0)
            return;
        try
        {
            foreach (var line in _spectrumGridLines)
            {
                if (line is null)
                    continue;
                line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Theme.SpectrumGridLine");
                line.Opacity = 0.30;
            }
        }
        catch
        {
            foreach (var line in _spectrumGridLines)
            {
                if (line is null)
                    continue;
                try
                {
                    line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Theme.Border");
                    line.Opacity = 0.24;
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>Minimum alpha for panels, borders, selection, title bar, and window frame tint so 0% background opacity does not erase all chrome.</summary>
    private const byte ThemeChromeAlphaFloor = 28;

    /// <summary>Maps user background opacity [0,255] to alpha for theme UI chrome: low end stays visible; 255 stays fully opaque.</summary>
    private static byte MapThemeChromeAlpha(byte userAlpha)
    {
        if (userAlpha >= 255) return 255;
        var t = userAlpha / 255.0;
        return (byte)Math.Clamp(
            Math.Round(ThemeChromeAlphaFloor + (255.0 - ThemeChromeAlphaFloor) * t),
            ThemeChromeAlphaFloor,
            255);
    }

    /// <summary>
    /// Mutes inactive-vs-active caption contrast when global UI opacity is low so the inactive strip does not dominate
    /// the window (0 = keep full distinction, higher = pull inactive RGB toward active).
    /// </summary>
    private static double InactiveTitleBarMuteStrength(byte userAlpha)
    {
        if (userAlpha >= 255) return 0;
        var u = userAlpha / 255.0;
        return Math.Clamp(Math.Pow(1.0 - u, 1.2) * 0.82, 0.0, 0.82);
    }

    private static void ApplyDefaultThemeBrushes(byte alpha, bool darkTheme)
    {
        try
        {
            var a = MapThemeChromeAlpha(alpha);
            var inactiveMute = InactiveTitleBarMuteStrength(alpha);
            // Text/ink should be translucent (so it layers with the background like the spectrum curve).
            // Use a strong mapping so UI transparency changes are actually visible.
            var inkA = (byte)Math.Clamp((int)Math.Round(80 + a * 0.45), 110, 200);
            var inkSubtleA = (byte)Math.Clamp((int)Math.Round(64 + a * 0.30), 90, 170);
            System.Windows.Media.Color surface;
            System.Windows.Media.Color border;
            System.Windows.Media.Color subtle;
            System.Windows.Media.Color fg;
            System.Windows.Media.Color selection;
            System.Windows.Media.Color selectionBorder;
            if (darkTheme)
            {
                // Use baked defaults (not prior theme's App.Brush.*) so Default dark stays stable after switching schemes.
                surface = System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x1B);
                border = System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A);
                subtle = System.Windows.Media.Color.FromRgb(0xB8, 0xB8, 0xB8);
                fg = System.Windows.Media.Color.FromRgb(0xE6, 0xE6, 0xE6);
                selection = System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x2C);
                selectionBorder = System.Windows.Media.Color.FromRgb(0x5C, 0x5C, 0x5E);
            }
            else
            {
                surface = System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2);
                border = System.Windows.Media.Color.FromRgb(0xC9, 0xC9, 0xC9);
                subtle = System.Windows.Media.Color.FromRgb(0x4A, 0x4A, 0x4A);
                fg = System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18);
                selection = System.Windows.Media.Color.FromRgb(0xE2, 0xE2, 0xE4);
                selectionBorder = System.Windows.Media.Color.FromRgb(0xB6, 0xB6, 0xB8);
            }

            var surfaceRaisedRgbOnly = darkTheme
                ? System.Windows.Media.Color.FromRgb(0x23, 0x23, 0x23)
                : System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA);
            var hoverRgb = Blend(surfaceRaisedRgbOnly, System.Windows.Media.Color.FromRgb(fg.R, fg.G, fg.B), 0.08);
            var pressedRgb = Blend(surfaceRaisedRgbOnly, System.Windows.Media.Color.FromRgb(fg.R, fg.G, fg.B), 0.14);

            SetBrush("App.Theme.Surface", System.Windows.Media.Color.FromArgb(a, surface.R, surface.G, surface.B));
            SetBrush("App.Theme.SurfaceRaised", darkTheme
                ? System.Windows.Media.Color.FromArgb(a, 0x23, 0x23, 0x23)
                : System.Windows.Media.Color.FromArgb(a, 0xFA, 0xFA, 0xFA));
            // Popups/menus must remain opaque so they don't become click-through when background alpha is low.
            SetBrush("App.Theme.PopupSurface", System.Windows.Media.Color.FromArgb(0xFF, surface.R, surface.G, surface.B));
            SetBrush("App.Theme.PopupSurfaceRaised", darkTheme
                ? System.Windows.Media.Color.FromArgb(0xFF, 0x23, 0x23, 0x23)
                : System.Windows.Media.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));
            // Tabs: passive headers should be darker, and must honor global UI alpha.
            SetBrush("App.Theme.TabHeaderPassiveBackground",
                DeriveTabHeaderPassiveBackground(
                    baseSurfaceRgbOnly: System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B),
                    surfaceRaisedRgbOnly: surfaceRaisedRgbOnly,
                    preferDarkTheme: darkTheme,
                    chromeAlpha: a,
                    darkBlendToBlack: 0.62,
                    lightBlendToBlack: 0.14,
                    alphaMultiplier: 0.72));
            SetBrush("App.Theme.Border", System.Windows.Media.Color.FromArgb(a, border.R, border.G, border.B));
            // Window border should remain visible even when background alpha is low (border is chrome, not content).
            SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(0xFF, border.R, border.G, border.B));
            var winBorderInactive = Blend(
                System.Windows.Media.Color.FromRgb(border.R, border.G, border.B),
                System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B),
                0.55);
            SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(0xFF, winBorderInactive.R, winBorderInactive.G, winBorderInactive.B));
            SetBrush("App.Theme.Foreground", System.Windows.Media.Color.FromArgb(inkA, fg.R, fg.G, fg.B));
            SetBrush("App.Theme.ForegroundSubtle", System.Windows.Media.Color.FromArgb(inkSubtleA, subtle.R, subtle.G, subtle.B));
            var spectrumGrid = Blend(
                System.Windows.Media.Color.FromRgb(subtle.R, subtle.G, subtle.B),
                System.Windows.Media.Color.FromRgb(border.R, border.G, border.B),
                darkTheme ? 0.82 : 0.70);
            spectrumGrid = Blend(
                spectrumGrid,
                System.Windows.Media.Color.FromRgb(0, 0, 0),
                darkTheme ? 0.38 : 0.22);
            SetBrush("App.Theme.SpectrumGridLine",
                System.Windows.Media.Color.FromArgb(a, spectrumGrid.R, spectrumGrid.G, spectrumGrid.B));
            var spectrumPanelRgb = Blend(
                System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B),
                darkTheme ? System.Windows.Media.Color.FromRgb(0, 0, 0) : System.Windows.Media.Color.FromRgb(border.R, border.G, border.B),
                darkTheme ? 0.52 : 0.30);
            SetBrush("App.Theme.SpectrumPanelBackground",
                System.Windows.Media.Color.FromArgb(a, spectrumPanelRgb.R, spectrumPanelRgb.G, spectrumPanelRgb.B));

            // Muted steel accent (same family as SelectionBorder). Avoid #3B82F6 — it reads as Windows/chrome blue on Default.
            SetBrush("App.Theme.Accent", DefaultThemeMutedAccentRgb);
            SetBrush("App.Theme.AccentText", darkTheme ? System.Windows.Media.Color.FromRgb(0xE6, 0xE6, 0xE6) : System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18));
            SetColor("App.Theme.AccentColor", DefaultThemeMutedAccentRgb);
            // Neutral selection (Shuffle checked, tab selected, focus ring) — avoid #161F2B / #2A4B73 blue cast.
            SetBrush("App.Theme.Selection", System.Windows.Media.Color.FromArgb(a, selection.R, selection.G, selection.B));
            SetBrush("App.Theme.SelectionBorder", System.Windows.Media.Color.FromArgb(a, selectionBorder.R, selectionBorder.G, selectionBorder.B));
            SetBrush("App.Theme.SelectionText", PickForegroundForBackground(selection, minRatio: 4.5));

            var textFieldSel = DeriveTextFieldSelection(surfaceRaisedRgbOnly, DefaultThemeMutedAccentRgb);
            SetBrush("App.Theme.TextSelection", textFieldSel.fill);
            SetBrush("App.Theme.TextSelectionText", textFieldSel.text);

            SetBrush("App.Theme.Hover", System.Windows.Media.Color.FromArgb(a, hoverRgb.R, hoverRgb.G, hoverRgb.B));
            SetBrush("App.Theme.Pressed", System.Windows.Media.Color.FromArgb(a, pressedRgb.R, pressedRgb.G, pressedRgb.B));

            var selRgb = System.Windows.Media.Color.FromRgb(selection.R, selection.G, selection.B);
            var selTxtRgb = PickForegroundForBackground(selRgb, minRatio: 4.5);
            var hotRgb = System.Windows.Media.Color.FromRgb(hoverRgb.R, hoverRgb.G, hoverRgb.B);
            // ControlText must follow normal foreground — not selection text (was wrong for Default theme).
            SyncSystemChromeBrushesFromSelectionTheme(selRgb, selTxtRgb, hotRgb, fg);

            // Title chrome: match raised surface + readable inactive state (same idea as image-derived themes).
            var surfaceRgbOnly = System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B);
            SetBrush("App.Theme.TitleBarActive", System.Windows.Media.Color.FromArgb(a, surfaceRaisedRgbOnly.R, surfaceRaisedRgbOnly.G, surfaceRaisedRgbOnly.B));
            var titleInactiveRgb = Blend(surfaceRaisedRgbOnly, surfaceRgbOnly, 0.65);
            var lumA = RelativeLuminance(surfaceRaisedRgbOnly);
            var lumI = RelativeLuminance(titleInactiveRgb);
            var minSep = 0.18 * (1.0 - inactiveMute * 0.72);
            if (Math.Abs(lumA - lumI) < minSep)
            {
                var push = 1.0 - inactiveMute * 0.55;
                titleInactiveRgb = AdjustLuminance(titleInactiveRgb, (darkTheme ? -0.22 : -0.28) * push);
                lumI = RelativeLuminance(titleInactiveRgb);
                if (Math.Abs(lumA - lumI) < minSep)
                    titleInactiveRgb = AdjustLuminance(titleInactiveRgb, (darkTheme ? -0.14 : -0.18) * push);
            }

            titleInactiveRgb = Blend(titleInactiveRgb, surfaceRaisedRgbOnly, inactiveMute);
            SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, titleInactiveRgb.R, titleInactiveRgb.G, titleInactiveRgb.B));
            var titleTextActive = PickForegroundForBackground(surfaceRaisedRgbOnly, minRatio: 7.0);
            var titleTextInactive = PickForegroundForBackground(titleInactiveRgb, minRatio: 7.0);
            var dimCandidate = Blend(titleTextInactive, surfaceRgbOnly, 0.25);
            if (ContrastRatio(titleInactiveRgb, dimCandidate) >= 4.5)
                titleTextInactive = dimCandidate;
            SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromArgb(inkA, titleTextActive.R, titleTextActive.G, titleTextActive.B));
            SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromArgb(inkSubtleA, titleTextInactive.R, titleTextInactive.G, titleTextInactive.B));

            SyncLegacyWindowChromeFillBrush(System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B));
        }
        catch { /* ignore */ }
    }

    private static void ApplyWindowsThemeBrushes(byte alpha)
    {
        try
        {
            var a = MapThemeChromeAlpha(alpha);
            var inactiveMute = InactiveTitleBarMuteStrength(alpha);
            var inkA = (byte)Math.Clamp((int)Math.Round(80 + a * 0.45), 110, 200);
            var inkSubtleA = (byte)Math.Clamp((int)Math.Round(64 + a * 0.30), 90, 170);
            // Pull live Windows theme colors (not our baked defaults).
            var surfaceRgb = System.Windows.SystemColors.ControlColor;
            var surfaceRaisedRgb = System.Windows.SystemColors.ControlLightColor;
            var borderRgb = System.Windows.SystemColors.ControlDarkColor;
            // In Windows theme mode, normal foreground should be the system text color (typically black in light theme).
            var fgRgb = System.Windows.SystemColors.WindowTextColor;
            var subtleRgb = System.Windows.SystemColors.GrayTextColor;
            var selectionRgb = System.Windows.SystemColors.HighlightColor;
            var selectionTextRgb = System.Windows.SystemColors.HighlightTextColor;
            var hotTrackRgb = System.Windows.SystemColors.HotTrackColor;

            // Apply system brushes (some controls still bind these directly).
            SetSystemBrush(System.Windows.SystemColors.ControlLightBrushKey,
                System.Windows.Media.Color.FromArgb(a, surfaceRgb.R, surfaceRgb.G, surfaceRgb.B));
            SetSystemBrush(System.Windows.SystemColors.ControlDarkBrushKey,
                System.Windows.Media.Color.FromArgb(a, borderRgb.R, borderRgb.G, borderRgb.B));
            SetSystemBrush(System.Windows.SystemColors.GrayTextBrushKey,
                System.Windows.Media.Color.FromRgb(subtleRgb.R, subtleRgb.G, subtleRgb.B));

            // App palette.
            SetBrush("App.Theme.Surface", System.Windows.Media.Color.FromArgb(a, surfaceRgb.R, surfaceRgb.G, surfaceRgb.B));
            SetBrush("App.Theme.SurfaceRaised", System.Windows.Media.Color.FromArgb(a, surfaceRaisedRgb.R, surfaceRaisedRgb.G, surfaceRaisedRgb.B));
            // Popups/menus must remain opaque so they don't become click-through when background alpha is low.
            SetBrush("App.Theme.PopupSurface", System.Windows.Media.Color.FromArgb(0xFF, surfaceRgb.R, surfaceRgb.G, surfaceRgb.B));
            SetBrush("App.Theme.PopupSurfaceRaised", System.Windows.Media.Color.FromArgb(0xFF, surfaceRaisedRgb.R, surfaceRaisedRgb.G, surfaceRaisedRgb.B));
            // Tabs: passive headers slightly darker than surface in Windows theme mode.
            var surfaceRgbOnlyWin = System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B);
            var darkWinChrome = RelativeLuminance(surfaceRgbOnlyWin) < 0.42;
            SetBrush("App.Theme.TabHeaderPassiveBackground",
                DeriveTabHeaderPassiveBackground(
                    baseSurfaceRgbOnly: surfaceRgb,
                    surfaceRaisedRgbOnly: surfaceRaisedRgb,
                    preferDarkTheme: darkWinChrome,
                    chromeAlpha: a,
                    darkBlendToBlack: 0.12,
                    lightBlendToBlack: 0.12,
                    alphaMultiplier: 0.70));
            SetBrush("App.Theme.Border", System.Windows.Media.Color.FromArgb(a, borderRgb.R, borderRgb.G, borderRgb.B));
            // Window frame: WPF SystemColors.ActiveBorder/InactiveBorder are legacy and don't track Win10/11 DWM chrome.
            // Derive 1px borders from the same caption palette we use for title bars + ControlDark.
            var surfaceForTitleEarly = System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B);
            var (capActiveEarly, capInactiveEarly, capTxtAEarly, capTxtIEarly) = GetWindowsTitleBarPalette(surfaceForTitleEarly);
            var borderOnlyEarly = System.Windows.Media.Color.FromRgb(borderRgb.R, borderRgb.G, borderRgb.B);
            var winBorderAct = Blend(borderOnlyEarly, capActiveEarly, 0.26);
            var winBorderInact = Blend(borderOnlyEarly, capInactiveEarly, 0.38);
            // Keep window borders fully opaque regardless of background alpha.
            SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(0xFF, winBorderAct.R, winBorderAct.G, winBorderAct.B));
            SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(0xFF, winBorderInact.R, winBorderInact.G, winBorderInact.B));
            SetBrush("App.Theme.Foreground", System.Windows.Media.Color.FromArgb(inkA, fgRgb.R, fgRgb.G, fgRgb.B));
            SetBrush("App.Theme.ForegroundSubtle", System.Windows.Media.Color.FromArgb(inkSubtleA, subtleRgb.R, subtleRgb.G, subtleRgb.B));
            var spectrumWin = Blend(
                System.Windows.Media.Color.FromRgb(subtleRgb.R, subtleRgb.G, subtleRgb.B),
                System.Windows.Media.Color.FromRgb(borderRgb.R, borderRgb.G, borderRgb.B),
                darkWinChrome ? 0.82 : 0.70);
            spectrumWin = Blend(
                spectrumWin,
                System.Windows.Media.Color.FromRgb(0, 0, 0),
                darkWinChrome ? 0.36 : 0.20);
            SetBrush("App.Theme.SpectrumGridLine",
                System.Windows.Media.Color.FromArgb(a, spectrumWin.R, spectrumWin.G, spectrumWin.B));
            var spectrumPanelWin = Blend(
                System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B),
                System.Windows.Media.Color.FromRgb(borderRgb.R, borderRgb.G, borderRgb.B),
                darkWinChrome ? 0.42 : 0.32);
            spectrumPanelWin = Blend(
                spectrumPanelWin,
                System.Windows.Media.Color.FromRgb(0, 0, 0),
                darkWinChrome ? 0.14 : 0.10);
            SetBrush("App.Theme.SpectrumPanelBackground",
                System.Windows.Media.Color.FromArgb(a, spectrumPanelWin.R, spectrumPanelWin.G, spectrumPanelWin.B));

            // Accent/selection from Windows.
            SetBrush("App.Theme.Accent", System.Windows.Media.Color.FromRgb(hotTrackRgb.R, hotTrackRgb.G, hotTrackRgb.B));
            SetBrush("App.Theme.AccentText", PickForegroundForBackground(System.Windows.Media.Color.FromRgb(hotTrackRgb.R, hotTrackRgb.G, hotTrackRgb.B), minRatio: 4.5));
            SetColor("App.Theme.AccentColor", System.Windows.Media.Color.FromRgb(hotTrackRgb.R, hotTrackRgb.G, hotTrackRgb.B));

            // Windows highlight blue is often too aggressive for our flat UI. Soften it by blending toward the surface.
            var selSoft = Blend(
                System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B),
                System.Windows.Media.Color.FromRgb(selectionRgb.R, selectionRgb.G, selectionRgb.B),
                0.22);
            var selBorderSoft = Blend(
                System.Windows.Media.Color.FromRgb(selectionRgb.R, selectionRgb.G, selectionRgb.B),
                System.Windows.Media.Color.FromRgb(borderRgb.R, borderRgb.G, borderRgb.B),
                0.55);
            SetBrush("App.Theme.Selection", System.Windows.Media.Color.FromArgb(a, selSoft.R, selSoft.G, selSoft.B));
            SetBrush("App.Theme.SelectionBorder", System.Windows.Media.Color.FromArgb(a, selBorderSoft.R, selBorderSoft.G, selBorderSoft.B));
            // Prefer normal foreground color for selection text if it meets contrast; otherwise keep Windows highlight text.
            var selTextPrefer = System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B);
            var selTextWin = System.Windows.Media.Color.FromRgb(selectionTextRgb.R, selectionTextRgb.G, selectionTextRgb.B);
            var selTextFinal = ContrastRatio(selSoft, selTextPrefer) >= 4.5 ? selTextPrefer : selTextWin;
            SetBrush("App.Theme.SelectionText", System.Windows.Media.Color.FromRgb(selTextFinal.R, selTextFinal.G, selTextFinal.B));

            // Opaque caret band even when other chrome uses reduced alpha (semi-transparent selection looked faint on fields).
            SetBrush("App.Theme.TextSelection", System.Windows.Media.Color.FromArgb(255, selectionRgb.R, selectionRgb.G, selectionRgb.B));
            SetBrush("App.Theme.TextSelectionText", System.Windows.Media.Color.FromRgb(selectionTextRgb.R, selectionTextRgb.G, selectionTextRgb.B));

            // Hover/pressed: subtle overlays in the direction of foreground.
            var hover = Blend(
                System.Windows.Media.Color.FromRgb(surfaceRaisedRgb.R, surfaceRaisedRgb.G, surfaceRaisedRgb.B),
                System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B),
                0.06);
            var pressed = Blend(
                System.Windows.Media.Color.FromRgb(surfaceRaisedRgb.R, surfaceRaisedRgb.G, surfaceRaisedRgb.B),
                System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B),
                0.12);
            SetBrush("App.Theme.Hover", System.Windows.Media.Color.FromArgb(a, hover.R, hover.G, hover.B));
            SetBrush("App.Theme.Pressed", System.Windows.Media.Color.FromArgb(a, pressed.R, pressed.G, pressed.B));

            // Title bar: same DWM-informed caption palette as window borders (computed above).
            var capInactiveMuted = Blend(capInactiveEarly, capActiveEarly, inactiveMute);
            SetBrush("App.Theme.TitleBarActive", System.Windows.Media.Color.FromArgb(a, capActiveEarly.R, capActiveEarly.G, capActiveEarly.B));
            SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, capInactiveMuted.R, capInactiveMuted.G, capInactiveMuted.B));
            SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromArgb(inkA, capTxtAEarly.R, capTxtAEarly.G, capTxtAEarly.B));
            SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromArgb(inkSubtleA, capTxtIEarly.R, capTxtIEarly.G, capTxtIEarly.B));

            SyncSystemChromeBrushesFromSelectionTheme(
                System.Windows.Media.Color.FromRgb(selSoft.R, selSoft.G, selSoft.B),
                System.Windows.Media.Color.FromRgb(selTextFinal.R, selTextFinal.G, selTextFinal.B),
                System.Windows.Media.Color.FromRgb(hotTrackRgb.R, hotTrackRgb.G, hotTrackRgb.B),
                System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B));

            SyncLegacyWindowChromeFillBrush(surfaceRgb);
        }
        catch { /* ignore */ }
    }

    private static void ApplyThemeBrushesFromBaseColor(System.Windows.Media.Color baseColor, byte alpha, bool preferDarkTheme)
    {
        var a = MapThemeChromeAlpha(alpha);
        var inactiveMute = InactiveTitleBarMuteStrength(alpha);
        var inkA = (byte)Math.Clamp((int)Math.Round(80 + a * 0.45), 110, 200);
        var inkSubtleA = (byte)Math.Clamp((int)Math.Round(64 + a * 0.30), 90, 170);
        // Bias extremely bright/dark bases toward requested polarity so manual Light/Dark remains usable.
        var lum = RelativeLuminance(baseColor);
        var bc = baseColor;
        if (!preferDarkTheme && lum < 0.35)
            bc = AdjustLuminance(bc, 0.30);
        else if (preferDarkTheme && lum > 0.65)
            bc = AdjustLuminance(bc, -0.30);

        // Surfaces honor mapped chrome alpha, foreground stays opaque.
        var surface = System.Windows.Media.Color.FromArgb(a, bc.R, bc.G, bc.B);
        var surfaceRaised = System.Windows.Media.Color.FromArgb(a,
            (byte)Math.Clamp((int)Math.Round(bc.R * 0.92 + 0x23 * 0.08), 0, 255),
            (byte)Math.Clamp((int)Math.Round(bc.G * 0.92 + 0x23 * 0.08), 0, 255),
            (byte)Math.Clamp((int)Math.Round(bc.B * 0.92 + 0x23 * 0.08), 0, 255));

        // Use stronger defaults so light backgrounds remain readable.
        var fg = PickForegroundForBackground(bc, minRatio: 7.0);
        var fgSubtle = PickForegroundForBackground(bc, minRatio: 4.5);
        // Manual preference: if requested polarity's foreground meets ratio, use it.
        var preferFg = preferDarkTheme
            ? System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA)
            : System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10);
        if (ContrastRatio(bc, preferFg) >= 7.0)
            fg = preferFg;
        if (ContrastRatio(bc, preferFg) >= 4.5)
            fgSubtle = preferFg;

        var themeBorder = DeriveBorder(bc, a);
        var accent = DeriveAccentFromBase(bc);
        var accentText = PickForegroundForBackground(accent, minRatio: 4.5);

        var (sel, selBorder, selText) = DeriveSelection(bc, accent, themeBorder, a);

        SetBrush("App.Theme.Surface", surface);
        SetBrush("App.Theme.SurfaceRaised", surfaceRaised);
        // Popups/menus must remain opaque so they don't become click-through when background alpha is low.
        SetBrush("App.Theme.PopupSurface", System.Windows.Media.Color.FromArgb(0xFF, bc.R, bc.G, bc.B));
        SetBrush("App.Theme.PopupSurfaceRaised", System.Windows.Media.Color.FromArgb(0xFF, surfaceRaised.R, surfaceRaised.G, surfaceRaised.B));
        SetBrush("App.Theme.Border", themeBorder);
        // Tabs: passive headers should be darker, and must honor global UI alpha.
        SetBrush("App.Theme.TabHeaderPassiveBackground",
            DeriveTabHeaderPassiveBackground(
                baseSurfaceRgbOnly: System.Windows.Media.Color.FromRgb(bc.R, bc.G, bc.B),
                surfaceRaisedRgbOnly: System.Windows.Media.Color.FromRgb(surfaceRaised.R, surfaceRaised.G, surfaceRaised.B),
                preferDarkTheme: preferDarkTheme,
                chromeAlpha: a,
                // Custom dark themes can otherwise make passive tabs read nearly black (especially with saturated base colors).
                darkBlendToBlack: 0.48,
                lightBlendToBlack: 0.14,
                alphaMultiplier: preferDarkTheme ? 0.62 : 0.72));
        SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(0xFF, themeBorder.R, themeBorder.G, themeBorder.B));
        var themeBorderRgbOnly = System.Windows.Media.Color.FromRgb(themeBorder.R, themeBorder.G, themeBorder.B);
        var baseSurfaceRgb = System.Windows.Media.Color.FromRgb(bc.R, bc.G, bc.B);
        var themeBorderInactiveRgb = Blend(themeBorderRgbOnly, baseSurfaceRgb, 0.55);
        SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(0xFF, themeBorderInactiveRgb.R, themeBorderInactiveRgb.G, themeBorderInactiveRgb.B));
        SetBrush("App.Theme.Foreground", System.Windows.Media.Color.FromArgb(inkA, fg.R, fg.G, fg.B));
        SetBrush("App.Theme.ForegroundSubtle", System.Windows.Media.Color.FromArgb(inkSubtleA, fgSubtle.R, fgSubtle.G, fgSubtle.B));
        var spectrumBase = Blend(
            fgSubtle,
            System.Windows.Media.Color.FromRgb(themeBorder.R, themeBorder.G, themeBorder.B),
            preferDarkTheme ? 0.82 : 0.70);
        spectrumBase = Blend(
            spectrumBase,
            System.Windows.Media.Color.FromRgb(0, 0, 0),
            preferDarkTheme ? 0.38 : 0.22);
        SetBrush("App.Theme.SpectrumGridLine",
            System.Windows.Media.Color.FromArgb(a, spectrumBase.R, spectrumBase.G, spectrumBase.B));
        var spectrumPanelBase = Blend(
            System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B),
            preferDarkTheme ? System.Windows.Media.Color.FromRgb(0, 0, 0) : System.Windows.Media.Color.FromRgb(themeBorder.R, themeBorder.G, themeBorder.B),
            preferDarkTheme ? 0.52 : 0.30);
        SetBrush("App.Theme.SpectrumPanelBackground",
            System.Windows.Media.Color.FromArgb(a, spectrumPanelBase.R, spectrumPanelBase.G, spectrumPanelBase.B));
        SetBrush("App.Theme.Accent", accent);
        SetBrush("App.Theme.AccentText", accentText);
        SetColor("App.Theme.AccentColor", accent);
        SetBrush("App.Theme.Selection", sel);
        SetBrush("App.Theme.SelectionBorder", selBorder);
        SetBrush("App.Theme.SelectionText", selText);

        var srForTextSel = System.Windows.Media.Color.FromRgb(surfaceRaised.R, surfaceRaised.G, surfaceRaised.B);
        var textFieldSel = DeriveTextFieldSelection(srForTextSel, accent);
        SetBrush("App.Theme.TextSelection", textFieldSel.fill);
        SetBrush("App.Theme.TextSelectionText", textFieldSel.text);

        // Title bar: follow the same palette polarity; keep it close to surfaceRaised so it feels integrated.
        // Inactive must be clearly visible and keep readable text. Derive text colors from the actual title bar
        // backgrounds (not from the base surface), otherwise some palettes end up gray-on-gray.
        var titleActive = System.Windows.Media.Color.FromArgb(a, surfaceRaised.R, surfaceRaised.G, surfaceRaised.B);
        var titleActiveRgb = System.Windows.Media.Color.FromRgb(surfaceRaised.R, surfaceRaised.G, surfaceRaised.B);
        var surfaceRgb = System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B);

        // Start by blending toward the window surface, then enforce a minimum luminance separation.
        var titleInactiveRgb = Blend(titleActiveRgb, surfaceRgb, 0.65);
        var lumA = RelativeLuminance(titleActiveRgb);
        var lumI = RelativeLuminance(titleInactiveRgb);
        var minSep = 0.18 * (1.0 - inactiveMute * 0.72);
        if (Math.Abs(lumA - lumI) < minSep)
        {
            var push = 1.0 - inactiveMute * 0.55;
            // Push harder in a direction that maintains the chosen polarity.
            titleInactiveRgb = AdjustLuminance(titleInactiveRgb, (preferDarkTheme ? 0.28 : -0.28) * push);
            lumI = RelativeLuminance(titleInactiveRgb);
            if (Math.Abs(lumA - lumI) < minSep)
                titleInactiveRgb = AdjustLuminance(titleInactiveRgb, (preferDarkTheme ? 0.18 : -0.18) * push);
        }

        titleInactiveRgb = Blend(titleInactiveRgb, titleActiveRgb, inactiveMute);
        SetBrush("App.Theme.TitleBarActive", titleActive);
        SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, titleInactiveRgb.R, titleInactiveRgb.G, titleInactiveRgb.B));
        var titleTextActive = PickForegroundForBackground(titleActiveRgb, minRatio: 7.0);
        var titleTextInactive = PickForegroundForBackground(titleInactiveRgb, minRatio: 7.0);
        // Dim inactive slightly but keep contrast; fall back to the high-contrast pick if dimming breaks it.
        var dimCandidate = Blend(titleTextInactive, surfaceRgb, 0.25);
        if (ContrastRatio(titleInactiveRgb, dimCandidate) >= 4.5)
            titleTextInactive = dimCandidate;
        SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromArgb(inkA, titleTextActive.R, titleTextActive.G, titleTextActive.B));
        SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromArgb(inkSubtleA, titleTextInactive.R, titleTextInactive.G, titleTextInactive.B));

        // Hover/pressed should be visible against raised surface.
        var hover = Blend(surfaceRaised, fg, 0.08);
        var pressed = Blend(surfaceRaised, fg, 0.14);
        SetBrush("App.Theme.Hover", hover);
        SetBrush("App.Theme.Pressed", pressed);

        var hotRgb = System.Windows.Media.Color.FromRgb(hover.R, hover.G, hover.B);
        SyncSystemChromeBrushesFromSelectionTheme(
            System.Windows.Media.Color.FromRgb(sel.R, sel.G, sel.B),
            System.Windows.Media.Color.FromRgb(selText.R, selText.G, selText.B),
            hotRgb,
            System.Windows.Media.Color.FromRgb(fg.R, fg.G, fg.B));
        SetSystemBrush(System.Windows.SystemColors.GrayTextBrushKey, System.Windows.Media.Color.FromRgb(fgSubtle.R, fgSubtle.G, fgSubtle.B));

        SyncLegacyWindowChromeFillBrush(System.Windows.Media.Color.FromRgb(bc.R, bc.G, bc.B));
    }

    private static System.Windows.Media.Color DeriveBorder(System.Windows.Media.Color surfaceRgb, byte alpha)
    {
        // Border: slightly darker or lighter than surface depending on luminance.
        var lum = RelativeLuminance(surfaceRgb);
        var delta = lum > 0.5 ? -0.18 : 0.18;
        var adjusted = AdjustLuminance(surfaceRgb, delta);
        return System.Windows.Media.Color.FromArgb(alpha, adjusted.R, adjusted.G, adjusted.B);
    }

    private static (System.Windows.Media.Color sel, System.Windows.Media.Color border, System.Windows.Media.Color text) DeriveSelection(
        System.Windows.Media.Color surfaceRgb,
        System.Windows.Media.Color accentRgb,
        System.Windows.Media.Color themeBorderRgb,
        byte alpha)
    {
        // Selection fill: slight accent tint; border follows semantic border (not heavy accent blend) so tabs / Shuffle
        // do not pick up saturated accent blue when accent is the default steel or image-derived.
        var sel = Blend(surfaceRgb, accentRgb, 0.25);
        var text = PickForegroundForBackground(
            System.Windows.Media.Color.FromRgb(sel.R, sel.G, sel.B),
            minRatio: 4.5);
        var themeBorderOpaque = System.Windows.Media.Color.FromRgb(themeBorderRgb.R, themeBorderRgb.G, themeBorderRgb.B);
        var border = Blend(
            System.Windows.Media.Color.FromRgb(sel.R, sel.G, sel.B),
            themeBorderOpaque,
            0.68);
        return (
            System.Windows.Media.Color.FromArgb(alpha, sel.R, sel.G, sel.B),
            System.Windows.Media.Color.FromArgb(alpha, border.R, border.G, border.B),
            System.Windows.Media.Color.FromRgb(text.R, text.G, text.B)
        );
    }

    /// <summary>
    /// Caret selection inside text fields: must read clearly on the raised field surface, unlike list-row selection
    /// which stays intentionally neutral. Always opaque RGBA (alpha 255): theme chrome alpha must not dilute the band.
    /// </summary>
    private static (System.Windows.Media.Color fill, System.Windows.Media.Color text) DeriveTextFieldSelection(
        System.Windows.Media.Color surfaceRaisedRgb,
        System.Windows.Media.Color accentRgb)
    {
        var lightField = RelativeLuminance(surfaceRaisedRgb) > 0.55;
        // Heavier accent mix than list selection; image-derived accents are often close to the field chroma.
        var t = lightField ? 0.55 : 0.80;
        var fill = Blend(surfaceRaisedRgb, accentRgb, t);

        const double minVsField = 1.26;
        for (var i = 0; i < 16 && ContrastRatio(surfaceRaisedRgb, fill) < minVsField; i++)
        {
            t = Math.Min(0.98, t + (lightField ? 0.04 : 0.03));
            fill = Blend(surfaceRaisedRgb, accentRgb, t);
        }

        if (ContrastRatio(surfaceRaisedRgb, fill) < minVsField)
        {
            var alt = Blend(surfaceRaisedRgb, DefaultThemeMutedAccentRgb, lightField ? 0.50 : 0.84);
            if (ContrastRatio(surfaceRaisedRgb, alt) > ContrastRatio(surfaceRaisedRgb, fill))
                fill = alt;
        }

        if (ContrastRatio(surfaceRaisedRgb, fill) < 1.12)
            fill = AdjustLuminance(fill, lightField ? -0.10 : 0.12);

        var text = PickForegroundForBackground(
            System.Windows.Media.Color.FromRgb(fill.R, fill.G, fill.B),
            minRatio: 7.0);
        return (
            System.Windows.Media.Color.FromArgb(255, fill.R, fill.G, fill.B),
            System.Windows.Media.Color.FromRgb(text.R, text.G, text.B));
    }

    private static System.Windows.Media.Color PickForegroundForBackground(System.Windows.Media.Color bgRgb, double minRatio)
    {
        var black = System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10);
        var white = System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA);

        var cBlack = ContrastRatio(bgRgb, black);
        var cWhite = ContrastRatio(bgRgb, white);

        if (cBlack >= minRatio && cBlack >= cWhite)
            return black;
        if (cWhite >= minRatio)
            return white;

        // If neither meets target, choose the higher-contrast option.
        return cBlack >= cWhite ? black : white;
    }

    private static double RelativeLuminance(System.Windows.Media.Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double ContrastRatio(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        var l1 = RelativeLuminance(a);
        var l2 = RelativeLuminance(b);
        if (l2 > l1) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static System.Windows.Media.Color Blend(System.Windows.Media.Color a, System.Windows.Media.Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte BlendCh(byte x, byte y) => (byte)Math.Clamp((int)Math.Round(x + (y - x) * t), 0, 255);
        return System.Windows.Media.Color.FromArgb(
            a.A,
            BlendCh(a.R, b.R),
            BlendCh(a.G, b.G),
            BlendCh(a.B, b.B)
        );
    }

    private static System.Windows.Media.Color DeriveTabHeaderPassiveBackground(
        System.Windows.Media.Color baseSurfaceRgbOnly,
        System.Windows.Media.Color surfaceRaisedRgbOnly,
        bool preferDarkTheme,
        byte chromeAlpha,
        double darkBlendToBlack,
        double lightBlendToBlack,
        double alphaMultiplier)
    {
        // Keep the *hue* from the base surface/raised surface, and only bias toward black.
        // Alpha is applied separately so passive headers don't read as an opaque slab over images.
        var rgb = preferDarkTheme
            ? Blend(baseSurfaceRgbOnly, System.Windows.Media.Color.FromRgb(0, 0, 0), darkBlendToBlack)
            : Blend(surfaceRaisedRgbOnly, System.Windows.Media.Color.FromRgb(0, 0, 0), lightBlendToBlack);
        var a = (byte)Math.Clamp((int)Math.Round(chromeAlpha * alphaMultiplier), 0, 255);
        return System.Windows.Media.Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
    }

    private static System.Windows.Media.Color AdjustLuminance(System.Windows.Media.Color c, double delta)
    {
        // Simple lift/gamma-ish adjustment in RGB space; good enough for borders.
        int Adj(byte v)
        {
            var d = (int)Math.Round(v + 255 * delta);
            return Math.Clamp(d, 0, 255);
        }
        return System.Windows.Media.Color.FromRgb((byte)Adj(c.R), (byte)Adj(c.G), (byte)Adj(c.B));
    }

    private static System.Windows.Media.Color DeriveAccentFromBase(System.Windows.Media.Color baseRgb)
    {
        // Derive accent by keeping hue but raising luminance.
        var (h, s, v) = RgbToHsv(baseRgb);
        if (s < 0.08)
        {
            // Near-gray sample (typical dark wallpaper): do not jump to saturated #3B82F6 — matches Default theme accent.
            return DefaultThemeMutedAccentRgb;
        }

        s = Math.Clamp(s, 0.35, 0.75);
        v = Math.Clamp(v + 0.25, 0.60, 0.85);
        var c = HsvToRgb(h, s, v);
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    private static (double h, double s, double v) RgbToHsv(System.Windows.Media.Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double h;
        if (delta <= 0.00001) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);
        if (h < 0) h += 360;

        var s = max <= 0.00001 ? 0 : delta / max;
        var v = max;
        return (h, s, v);
    }

    private static System.Windows.Media.Color HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        var m = v - c;

        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        byte ToByte(double d) => (byte)Math.Clamp((int)Math.Round((d + m) * 255), 0, 255);
        return System.Windows.Media.Color.FromRgb(ToByte(r1), ToByte(g1), ToByte(b1));
    }

    private static void SetBrush(string key, System.Windows.Media.Color c)
    {
        try
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            try { b.Freeze(); } catch { /* ignore */ }
            System.Windows.Application.Current.Resources[key] = b;
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// <see cref="App.Brush.WindowBg"/> is legacy XAML chrome fill (App.xaml default is a constant near-black).
    /// Theme switching updates <see cref="App.Theme.Surface"/> but historically did not update this key, so dialogs
    /// that still bind <c>App.Brush.WindowBg</c> looked "stuck in dark mode" while text followed <c>App.Theme.*</c>.
    /// </summary>
    private static void SyncLegacyWindowChromeFillBrush(System.Windows.Media.Color surfaceRgb)
    {
        try
        {
            SetBrush("App.Brush.WindowBg", System.Windows.Media.Color.FromArgb(0xFF, surfaceRgb.R, surfaceRgb.G, surfaceRgb.B));
        }
        catch { /* ignore */ }
    }

    private static void SetColor(string key, System.Windows.Media.Color c)
    {
        try
        {
            System.Windows.Application.Current.Resources[key] = c;
        }
        catch { /* ignore */ }
    }

    private static void RestoreDefaultBrushes()
    {
        try
        {
            var res = System.Windows.Application.Current.Resources;
            if (res["App.Brush.DefaultControlLight"] is System.Windows.Media.Brush cl)
                res[System.Windows.SystemColors.ControlLightBrushKey] = cl;
            if (res["App.Brush.DefaultControlDark"] is System.Windows.Media.Brush cd)
                res[System.Windows.SystemColors.ControlDarkBrushKey] = cd;
            if (res["App.Brush.DefaultGrayText"] is System.Windows.Media.Brush gt)
                res[System.Windows.SystemColors.GrayTextBrushKey] = gt;
            if (res["App.Brush.DefaultHighlight"] is System.Windows.Media.SolidColorBrush hl)
            {
                var c = System.Windows.Media.Color.FromRgb(hl.Color.R, hl.Color.G, hl.Color.B);
                SetSystemBrush(System.Windows.SystemColors.HighlightBrushKey, c);
                SetSystemBrush(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, c);
            }

            if (res["App.Brush.DefaultHighlightText"] is System.Windows.Media.SolidColorBrush hlt)
            {
                var c = System.Windows.Media.Color.FromRgb(hlt.Color.R, hlt.Color.G, hlt.Color.B);
                SetSystemBrush(System.Windows.SystemColors.HighlightTextBrushKey, c);
                SetSystemBrush(System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey, c);
            }

            if (res["App.Brush.DefaultHotTrack"] is System.Windows.Media.SolidColorBrush hot)
                SetSystemBrush(System.Windows.SystemColors.HotTrackBrushKey,
                    System.Windows.Media.Color.FromRgb(hot.Color.R, hot.Color.G, hot.Color.B));
            if (res["App.Brush.DefaultMenuHighlight"] is System.Windows.Media.SolidColorBrush mh)
                SetSystemBrush(System.Windows.SystemColors.MenuHighlightBrushKey,
                    System.Windows.Media.Color.FromRgb(mh.Color.R, mh.Color.G, mh.Color.B));
        }
        catch { /* ignore */ }
    }

    private static void ApplyDefaultBrushesWithAlpha(byte alpha, bool darkTheme)
    {
        try
        {
            var a = MapThemeChromeAlpha(alpha);
            var res = System.Windows.Application.Current.Resources;

            // Read default RGB from stored defaults, but apply the user-selected alpha.
            System.Windows.Media.Color lightRgb;
            System.Windows.Media.Color darkRgb;
            System.Windows.Media.Color textRgb;
            if (darkTheme)
            {
                lightRgb = TryGetRgbFromBrush(res["App.Brush.DefaultControlLight"]) ?? System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x1B);
                darkRgb = TryGetRgbFromBrush(res["App.Brush.DefaultControlDark"]) ?? System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A);
                textRgb = TryGetRgbFromBrush(res["App.Brush.DefaultGrayText"]) ?? System.Windows.Media.Color.FromRgb(0xB8, 0xB8, 0xB8);
            }
            else
            {
                lightRgb = System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2);
                darkRgb = System.Windows.Media.Color.FromRgb(0xC9, 0xC9, 0xC9);
                textRgb = System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A);
            }

            SetSystemBrush(System.Windows.SystemColors.ControlLightBrushKey, System.Windows.Media.Color.FromArgb(a, lightRgb.R, lightRgb.G, lightRgb.B));
            SetSystemBrush(System.Windows.SystemColors.ControlDarkBrushKey, System.Windows.Media.Color.FromArgb(a, darkRgb.R, darkRgb.G, darkRgb.B));

            // Text should stay opaque/readable (do not apply alpha).
            SetSystemBrush(System.Windows.SystemColors.GrayTextBrushKey, System.Windows.Media.Color.FromRgb(textRgb.R, textRgb.G, textRgb.B));
        }
        catch { /* ignore */ }
    }

    private static System.Windows.Media.Color? TryGetRgbFromBrush(object? brushObj)
    {
        try
        {
            if (brushObj is System.Windows.Media.SolidColorBrush sb)
                return System.Windows.Media.Color.FromRgb(sb.Color.R, sb.Color.G, sb.Color.B);
        }
        catch { /* ignore */ }
        return null;
    }

    private static void SetSystemBrush(object key, System.Windows.Media.Color c)
    {
        try
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            try { b.Freeze(); } catch { /* ignore */ }
            System.Windows.Application.Current.Resources[key] = b;
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// WPF/Aero still references <see cref="SystemColors.HotTrackBrushKey"/> and menu highlight keys for many
    /// hover/selection visuals; Windows defaults are bright blue and ignore <c>App.Theme.*</c> unless overridden here.
    /// </summary>
    private static void SyncSystemChromeBrushesFromSelectionTheme(
        System.Windows.Media.Color selectionRgb,
        System.Windows.Media.Color selectionTextRgb,
        System.Windows.Media.Color hotTrackRgb,
        System.Windows.Media.Color controlTextRgb)
    {
        SetSystemBrush(System.Windows.SystemColors.HighlightBrushKey, selectionRgb);
        SetSystemBrush(System.Windows.SystemColors.HighlightTextBrushKey, selectionTextRgb);
        SetSystemBrush(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, selectionRgb);
        SetSystemBrush(System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey, selectionTextRgb);
        SetSystemBrush(System.Windows.SystemColors.HotTrackBrushKey, hotTrackRgb);
        SetSystemBrush(System.Windows.SystemColors.MenuHighlightBrushKey, selectionRgb);
        SetSystemBrush(System.Windows.SystemColors.ControlTextBrushKey, controlTextRgb);
    }

    private System.Windows.Media.Color? TryGetAverageColorFromCurrentBackgroundImage(double dropBrightestPercent)
    {
        try
        {
            // Use raw image (no scrim) so color sampling stays stable.
            var res = System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"]
                      ?? System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"];
            if (res is not System.Windows.Media.ImageBrush ib)
                return null;
            if (ib.ImageSource is not System.Windows.Media.Imaging.BitmapSource src)
                return null;

            return ComputeAverageColorDroppingBrightest(src, dropBrightestPercent);
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.Color? ComputeAverageColorDroppingBrightest(System.Windows.Media.Imaging.BitmapSource src, double dropBrightestPercent)
    {
        try
        {
            // Downscale for speed.
            var scale = new System.Windows.Media.ScaleTransform(64.0 / src.PixelWidth, 64.0 / src.PixelHeight);
            var tb = new System.Windows.Media.Imaging.TransformedBitmap(src, scale);
            tb.Freeze();

            // Ensure BGRA32.
            System.Windows.Media.Imaging.BitmapSource b = tb;
            if (b.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                b = new System.Windows.Media.Imaging.FormatConvertedBitmap(tb, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                b.Freeze();
            }

            var w = b.PixelWidth;
            var h = b.PixelHeight;
            var stride = w * 4;
            var pixels = new byte[h * stride];
            b.CopyPixels(pixels, stride, 0);

            var lums = new List<double>(w * h);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3] / 255.0;
                if (a <= 0.05) continue;
                var r = pixels[i + 2];
                var g = pixels[i + 1];
                var bl = pixels[i + 0];
                var lum = (0.2126 * r + 0.7152 * g + 0.0722 * bl) / 255.0;
                lums.Add(lum);
            }

            if (lums.Count == 0)
                return null;

            lums.Sort();
            var drop = Math.Clamp(dropBrightestPercent, 0.0, 0.9);
            var keepIdx = (int)Math.Floor((1.0 - drop) * (lums.Count - 1));
            if (keepIdx < 0) keepIdx = 0;
            if (keepIdx >= lums.Count) keepIdx = lums.Count - 1;
            var lumThresh = lums[keepIdx];

            double sumA = 0, sumR = 0, sumG = 0, sumB = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a01 = pixels[i + 3] / 255.0;
                if (a01 <= 0.05) continue;
                var r = pixels[i + 2];
                var g = pixels[i + 1];
                var bl = pixels[i + 0];
                var lum = (0.2126 * r + 0.7152 * g + 0.0722 * bl) / 255.0;
                if (lum > lumThresh) continue;

                sumA += a01;
                sumR += r * a01;
                sumG += g * a01;
                sumB += bl * a01;
            }

            if (sumA <= 0.0001)
                return null;

            var rr = (byte)Math.Clamp((int)Math.Round(sumR / sumA), 0, 255);
            var gg = (byte)Math.Clamp((int)Math.Round(sumG / sumA), 0, 255);
            var bb = (byte)Math.Clamp((int)Math.Round(sumB / sumA), 0, 255);
            return System.Windows.Media.Color.FromRgb(rr, gg, bb);
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.Color? TryParseHexColor(string? s)
    {
        try
        {
            var t = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(t))
                return null;
            if (!t.StartsWith('#'))
                t = "#" + t;
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(t);
            return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return null;
        }
    }

    private void CaptureOptionsWindowBounds()
    {
        try
        {
            if (_optionsWindow is null)
                return;

            var state = _optionsWindow.WindowState;
            var bounds = state == WindowState.Normal
                ? new Rect(_optionsWindow.Left, _optionsWindow.Top, _optionsWindow.Width, _optionsWindow.Height)
                : _optionsWindow.RestoreBounds;

            _lastOptionsBounds = bounds;
            _lastOptionsWindowState = state;
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyOptionsWindowSettings(AppSettings settings, Window w)
    {
        try
        {
            // Outer size is always 640×480 (design) × UiScale — see <see cref="ApplyOptionsWindowScaledChromeSize"/>.

            // If snapped, restore relative to Main rather than using absolute coords.
            var placedBySnap = false;
            try
            {
                if ((settings.OptionsWindowSnapped ?? false) &&
                    Enum.TryParse<OptionsSnapEdge>(settings.OptionsWindowSnapEdge, ignoreCase: true, out var edge) &&
                    edge != OptionsSnapEdge.None)
                {
                    var main = GetOuterBounds(this);
                    var dockX = settings.OptionsWindowDockXOffset ?? 0;
                    var dockY = settings.OptionsWindowDockYOffset ?? 0;
                    if (AuxWindowSnapHelper.TryApplySnapPlacement(
                        AuxSnapWindowKind.Options,
                        snapped: true,
                        edge.ToString(),
                        dockX,
                        dockY,
                        main,
                        w,
                        SnapGapPx,
                        out var outDx,
                        out var outDy))
                    {
                        _optionsSnapped = true;
                        _optionsSnapEdge = edge;
                        _optionsDockXOffset = outDx;
                        _optionsDockYOffset = outDy;
                        placedBySnap = true;
                    }
                }
            }
            catch { /* ignore */ }

            if (!placedBySnap && settings.OptionsWindowLeft is double l && settings.OptionsWindowTop is double t)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                var minVisible = 32;
                var clampedLeft = Math.Min(Math.Max(l, vsLeft - w.Width + minVisible), vsRight - minVisible);
                var clampedTop = Math.Min(Math.Max(t, vsTop - w.Height + minVisible), vsBottom - minVisible);
                w.Left = clampedLeft;
                w.Top = clampedTop;
            }

            if (!string.IsNullOrWhiteSpace(settings.OptionsWindowState) &&
                Enum.TryParse<System.Windows.WindowState>(settings.OptionsWindowState, out var ws) &&
                ws != WindowState.Minimized)
            {
                w.WindowState = ws;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Restores snap/dock and last-known bounds from settings into fields used by <see cref="SaveSettingsSnapshot"/>.
    /// When the app starts in compact mode (or before secondary windows are created), those fields would otherwise
    /// stay at defaults and the next save would wipe persisted snap geometry.
    /// </summary>
    private void ApplyStoredAuxiliaryWindowLayoutFromSettings(AppSettings s)
    {
        try
        {
            _playlistSnapped = s.PlaylistWindowSnapped ?? false;
            _playlistSnapEdge = Enum.TryParse<PlaylistSnapEdge>(s.PlaylistWindowSnapEdge, ignoreCase: true, out var pe)
                ? pe
                : PlaylistSnapEdge.None;
            if (!_playlistSnapped || _playlistSnapEdge == PlaylistSnapEdge.None)
            {
                _playlistSnapped = false;
                _playlistSnapEdge = PlaylistSnapEdge.None;
            }

            _playlistDockYOffset = s.PlaylistWindowDockYOffset ?? 0;
            _playlistDockXOffset = s.PlaylistWindowDockXOffset ?? 0;
            // Only one offset is meaningful for a snap edge; clear the other to avoid stale restores.
            if (_playlistSnapped)
            {
                if (_playlistSnapEdge is PlaylistSnapEdge.Left or PlaylistSnapEdge.Right)
                    _playlistDockXOffset = 0;
                else if (_playlistSnapEdge is PlaylistSnapEdge.Bottom or PlaylistSnapEdge.Top)
                    _playlistDockYOffset = 0;
            }

            if (s.PlaylistWindowLeft is double pl && s.PlaylistWindowTop is double pt &&
                s.PlaylistWindowWidth is double pw && pw > 50 && pw < 10000 &&
                s.PlaylistWindowHeight is double ph && ph > 50 && ph < 10000)
            {
                _lastPlaylistBounds = new Rect(pl, pt, pw, ph);
                if (!string.IsNullOrWhiteSpace(s.PlaylistWindowState) &&
                    Enum.TryParse<WindowState>(s.PlaylistWindowState, out var pws) &&
                    pws != WindowState.Minimized)
                    _lastPlaylistWindowState = pws;
            }

            _optionsSnapped = s.OptionsWindowSnapped ?? false;
            _optionsSnapEdge = Enum.TryParse<OptionsSnapEdge>(s.OptionsWindowSnapEdge, ignoreCase: true, out var oe)
                ? oe
                : OptionsSnapEdge.None;
            if (!_optionsSnapped || _optionsSnapEdge == OptionsSnapEdge.None)
            {
                _optionsSnapped = false;
                _optionsSnapEdge = OptionsSnapEdge.None;
            }

            _optionsDockYOffset = s.OptionsWindowDockYOffset ?? 0;
            _optionsDockXOffset = s.OptionsWindowDockXOffset ?? 0;
            if (_optionsSnapped)
            {
                if (_optionsSnapEdge is OptionsSnapEdge.Left or OptionsSnapEdge.Right)
                    _optionsDockXOffset = 0;
                else if (_optionsSnapEdge is OptionsSnapEdge.Bottom)
                    _optionsDockYOffset = 0;
            }

            if (s.OptionsWindowLeft is double ol && s.OptionsWindowTop is double ot &&
                s.OptionsWindowWidth is double ow && ow > 50 && ow < 10000 &&
                s.OptionsWindowHeight is double oh && oh > 50 && oh < 10000)
            {
                _lastOptionsBounds = new Rect(ol, ot, ow, oh);
                if (!string.IsNullOrWhiteSpace(s.OptionsWindowState) &&
                    Enum.TryParse<WindowState>(s.OptionsWindowState, out var ows) &&
                    ows != WindowState.Minimized)
                    _lastOptionsWindowState = ows;
            }

            _lyricsSnapped = s.LyricsWindowSnapped ?? false;
            _lyricsSnapEdge = Enum.TryParse<LyricsSnapEdge>(s.LyricsWindowSnapEdge, ignoreCase: true, out var le)
                ? le
                : LyricsSnapEdge.None;
            if (!_lyricsSnapped || _lyricsSnapEdge == LyricsSnapEdge.None)
            {
                _lyricsSnapped = false;
                _lyricsSnapEdge = LyricsSnapEdge.None;
            }

            _lyricsDockYOffset = s.LyricsWindowDockYOffset ?? 0;
            _lyricsDockXOffset = s.LyricsWindowDockXOffset ?? 0;
            if (_lyricsSnapped)
            {
                if (_lyricsSnapEdge is LyricsSnapEdge.Left or LyricsSnapEdge.Right)
                    _lyricsDockXOffset = 0;
                else if (_lyricsSnapEdge is LyricsSnapEdge.Bottom or LyricsSnapEdge.Top)
                    _lyricsDockYOffset = 0;
            }

            if (s.LyricsWindowLeft is double ll && s.LyricsWindowTop is double lt &&
                s.LyricsWindowWidth is double lw && lw > 50 && lw < 10000 &&
                s.LyricsWindowHeight is double lh && lh > 50 && lh < 10000)
            {
                _lastLyricsBounds = new Rect(ll, lt, lw, lh);
                if (!string.IsNullOrWhiteSpace(s.LyricsWindowState) &&
                    Enum.TryParse<WindowState>(s.LyricsWindowState, out var lws) &&
                    lws != WindowState.Minimized)
                    _lastLyricsWindowState = lws;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// After <see cref="ApplyPlaylistWindowSettings"/>, outer width/height must reflect <see cref="UiScale"/> the same way
    /// as <see cref="LayoutTransform"/> on the playlist root: defaults from XAML are scale-1 design units; persisted sizes
    /// were saved at <see cref="AppSettings.PlaylistWindowBoundsUiScalePercent"/> (legacy: 100%).
    /// </summary>
    private void NormalizePlaylistWindowOuterForUiScale(AppSettings settings, System.Windows.Window w)
    {
        try
        {
            var cur = UiScale;
            var hadPersisted = settings.PlaylistWindowWidth is > 200 and < 10000
                && settings.PlaylistWindowHeight is > 200 and < 10000;
            if (hadPersisted)
            {
                var basis = settings.PlaylistWindowBoundsUiScalePercent is >= 50 and <= 200
                    ? settings.PlaylistWindowBoundsUiScalePercent.Value / 100.0
                    : 1.0;
                var r = cur / basis;
                if (Math.Abs(r - 1.0) > 1e-6)
                {
                    w.Width *= r;
                    w.Height *= r;
                }
            }
            else
            {
                w.Width *= cur;
                w.Height *= cur;
            }
        }
        catch { /* ignore */ }
    }

    private void ApplyPlaylistWindowSettings(AppSettings settings, Window w)
    {
        try
        {
            if (settings.PlaylistWindowWidth is > 200 and < 10000)
                w.Width = settings.PlaylistWindowWidth.Value;
            if (settings.PlaylistWindowHeight is > 200 and < 10000)
                w.Height = settings.PlaylistWindowHeight.Value;

            // If snapped, restore relative to Main rather than using absolute coords.
            var placedBySnap = false;
            try
            {
                if ((settings.PlaylistWindowSnapped ?? false) &&
                    Enum.TryParse<PlaylistSnapEdge>(settings.PlaylistWindowSnapEdge, ignoreCase: true, out var edge) &&
                    edge != PlaylistSnapEdge.None)
                {
                    var main = GetOuterBounds(this);
                    var dockX = settings.PlaylistWindowDockXOffset ?? 0;
                    var dockY = settings.PlaylistWindowDockYOffset ?? 0;
                    if (AuxWindowSnapHelper.TryApplySnapPlacement(
                        AuxSnapWindowKind.Playlist,
                        snapped: true,
                        edge.ToString(),
                        dockX,
                        dockY,
                        main,
                        w,
                        SnapGapPx,
                        out var outDx,
                        out var outDy))
                    {
                        _playlistSnapped = true;
                        _playlistSnapEdge = edge;
                        _playlistDockXOffset = outDx;
                        _playlistDockYOffset = outDy;
                        placedBySnap = true;
                    }
                }
            }
            catch { /* ignore */ }

            if (!placedBySnap && settings.PlaylistWindowLeft is double l && settings.PlaylistWindowTop is double t)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                var minVisible = 32;
                var clampedLeft = Math.Min(Math.Max(l, vsLeft - w.Width + minVisible), vsRight - minVisible);
                var clampedTop = Math.Min(Math.Max(t, vsTop - w.Height + minVisible), vsBottom - minVisible);
                w.Left = clampedLeft;
                w.Top = clampedTop;
            }

            if (!string.IsNullOrWhiteSpace(settings.PlaylistWindowState) &&
                Enum.TryParse<System.Windows.WindowState>(settings.PlaylistWindowState, out var ws) &&
                ws != WindowState.Minimized)
            {
                w.WindowState = ws;
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyLyricsWindowSettings(AppSettings settings, Window w)
    {
        try
        {
            if (settings.LyricsWindowWidth is > 200 and < 10000)
                w.Width = settings.LyricsWindowWidth.Value;
            if (settings.LyricsWindowHeight is > 200 and < 10000)
                w.Height = settings.LyricsWindowHeight.Value;

            // If snapped, restore relative to Main rather than using absolute coords.
            var placedBySnap = false;
            try
            {
                if ((settings.LyricsWindowSnapped ?? false) &&
                    Enum.TryParse<LyricsSnapEdge>(settings.LyricsWindowSnapEdge, ignoreCase: true, out var edge) &&
                    edge != LyricsSnapEdge.None)
                {
                    var main = GetOuterBounds(this);
                    var dockX = settings.LyricsWindowDockXOffset ?? 0;
                    var dockY = settings.LyricsWindowDockYOffset ?? 0;
                    if (AuxWindowSnapHelper.TryApplySnapPlacement(
                        AuxSnapWindowKind.Lyrics,
                        snapped: true,
                        edge.ToString(),
                        dockX,
                        dockY,
                        main,
                        w,
                        SnapGapPx,
                        out var outDx,
                        out var outDy))
                    {
                        _lyricsSnapped = true;
                        _lyricsSnapEdge = edge;
                        _lyricsDockXOffset = outDx;
                        _lyricsDockYOffset = outDy;
                        placedBySnap = true;
                    }
                }
            }
            catch { /* ignore */ }

            if (!placedBySnap && settings.LyricsWindowLeft is double l && settings.LyricsWindowTop is double t)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                var minVisible = 32;
                var clampedLeft = Math.Min(Math.Max(l, vsLeft - w.Width + minVisible), vsRight - minVisible);
                var clampedTop = Math.Min(Math.Max(t, vsTop - w.Height + minVisible), vsBottom - minVisible);
                w.Left = clampedLeft;
                w.Top = clampedTop;
            }

            if (!string.IsNullOrWhiteSpace(settings.LyricsWindowState) &&
                Enum.TryParse<System.Windows.WindowState>(settings.LyricsWindowState, out var ws) &&
                ws != WindowState.Minimized)
            {
                w.WindowState = ws;
            }
        }
        catch
        {
            // ignore
        }
    }

    private Task TryResumePlaybackFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        // We need a valid current track selected.
        if (_engine.GetCurrent() is null)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();

        var pos = _startupSettings.CurrentPositionSeconds ?? 0;
        if (pos < 0) pos = 0;

        var cur = _engine.GetCurrent();
        if (cur is null)
            return Task.CompletedTask;

        // Safe-start: never auto-resume playback on startup. LibVLC init/play can hard-crash on some systems;
        // always require explicit user action (Play) and only restore the timeline.
        if (true)
        {
            if (pos > 1 && !string.IsNullOrWhiteSpace(cur.VideoId))
            {
                _pendingResumeVideoId = cur.VideoId;
                _pendingResumeSeconds = pos;

                // Update timeline UI to show the saved position.
                try
                {
                    _nowPlayingEntry = cur;
                    _nowPlayingStatus = "PAUSED";
                    UpdateNowPlayingText();
                    UpdatePlaylistTitleDisplayForNowPlaying();
                    UpdateDurationUi(cur.DurationSeconds);

                    if (cur.DurationSeconds is int dur && dur > 0)
                    {
                        var clamped = Math.Max(0, Math.Min(pos, dur));
                        _ignoreSeekBar = true;
                        try { SeekSlider.Value = clamped; }
                        finally { _ignoreSeekBar = false; }
                        ElapsedTextBlock.Text = FormatTime(clamped);
                    }
                }
                catch { /* ignore */ }
            }
            return Task.CompletedTask;
        }
    }

    private void ApplyWindowSettings(AppSettings settings)
    {
        try
        {
            if (settings.WindowWidth is > 200 and < 10000)
                Width = settings.WindowWidth.Value;
            if (settings.WindowHeight is > 200 and < 10000)
                Height = settings.WindowHeight.Value;

            if (settings.WindowLeft is double l && settings.WindowTop is double t)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                // Keep at least a bit of the window on-screen.
                var minVisible = 32;
                var clampedLeft = Math.Min(Math.Max(l, vsLeft - Width + minVisible), vsRight - minVisible);
                var clampedTop = Math.Min(Math.Max(t, vsTop - Height + minVisible), vsBottom - minVisible);
                Left = clampedLeft;
                Top = clampedTop;
            }

            if (!string.IsNullOrWhiteSpace(settings.WindowState) &&
                Enum.TryParse<System.Windows.WindowState>(settings.WindowState, out var ws) &&
                ws != WindowState.Minimized)
            {
                WindowState = ws;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static VisualizerMode ParseVisualizerMode(string? value)
        => Enum.TryParse<VisualizerMode>(value, ignoreCase: true, out var m) ? m : VisualizerMode.Vu;

    private void ApplyVisualizerMode(VisualizerMode mode)
    {
        _visualizerMode = mode;
        if (VuGrid is null || SpectrumCanvas is null || SpectrumPanelChrome is null)
            return;

        VuGrid.Visibility = mode == VisualizerMode.Vu ? Visibility.Visible : Visibility.Collapsed;
        SpectrumPanelChrome.Visibility = mode == VisualizerMode.Spectrum ? Visibility.Visible : Visibility.Collapsed;
        if (mode == VisualizerMode.Spectrum) EnsureSpectrumBars();
    }

    private void VisualizerBorder_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Global rule: Shift+click toggles visualizer mode (all layouts), normal click never toggles.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            var next = _visualizerMode switch
            {
                VisualizerMode.Vu => VisualizerMode.Spectrum,
                VisualizerMode.Spectrum => VisualizerMode.Off,
                VisualizerMode.Off => VisualizerMode.Vu,
                _ => VisualizerMode.Vu,
            };
            ApplyVisualizerMode(next);
            RequestPersistSnapshot();
            e.Handled = true;
            return;
        }

        // Ultra-compact: normal click seeks (when duration is known). Other layouts: no-op.
        try
        {
            var ultra = _mainWindowCompact && string.Equals(_compactModeLayout, "Ultra", StringComparison.OrdinalIgnoreCase);
            if (ultra && _engine.CurrentDurationSeconds is int dur && dur > 0)
            {
                var w = VisualizerHostGrid?.ActualWidth ?? 0;
                if (w > 1)
                {
                    var p = e.GetPosition(VisualizerHostGrid);
                    var ratio = Math.Max(0.0, Math.Min(1.0, p.X / w));
                    _ = _engine.SeekAsync(ratio * dur);
                    e.Handled = true;
                    return;
                }
            }
        }
        catch { /* ignore */ }

        e.Handled = true;
    }

    /// <summary>
    /// Integer pixel row for each horizontal spectrum rule. Rounds ideal spacing then nudges rows apart so
    /// several rules never land on the same scanline when the canvas height is small (stacked lines read as one thick bright bar).
    /// </summary>
    private static void AssignSpectrumGridLineTops(Span<int> tops, double canvasHeight, int maxTop)
    {
        var n = tops.Length;
        if (n == 0 || maxTop < 0)
            return;
        var h = canvasHeight;
        for (var i = 0; i < n; i++)
        {
            var y = (int)Math.Round((i + 1.0) * h / (n + 1.0));
            tops[i] = Math.Clamp(y, 0, maxTop);
        }

        const int maxIter = 12;
        for (var iter = 0; iter < maxIter; iter++)
        {
            var changed = false;
            for (var i = 1; i < n; i++)
            {
                if (tops[i] <= tops[i - 1])
                {
                    var ny = Math.Min(maxTop, tops[i - 1] + 1);
                    if (ny != tops[i])
                    {
                        tops[i] = ny;
                        changed = true;
                    }
                }
            }

            for (var i = n - 2; i >= 0; i--)
            {
                if (tops[i] >= tops[i + 1])
                {
                    var ny = Math.Max(0, tops[i + 1] - 1);
                    if (ny != tops[i])
                    {
                        tops[i] = ny;
                        changed = true;
                    }
                }
            }

            if (!changed)
                break;
        }
    }

    private void EnsureSpectrumBars()
    {
        if (SpectrumCanvas is null)
            return;

        if (_spectrumCurvePath is not null && _spectrumGridLines is { Length: SpectrumGridLineCount })
            return;

        SpectrumCanvas.Children.Clear();
        // Horizontal rules: linear in Y (screen), not in frequency — reference only. Behind spectrum (ZIndex −1).
        _spectrumGridLines = new System.Windows.Shapes.Rectangle[SpectrumGridLineCount];
        for (var i = 0; i < SpectrumGridLineCount; i++)
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Width = 1,
                RadiusX = 0,
                RadiusY = 0,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            System.Windows.Media.RenderOptions.SetEdgeMode(line, System.Windows.Media.EdgeMode.Aliased);
            System.Windows.Controls.Panel.SetZIndex(line, -1);
            _spectrumGridLines[i] = line;
            SpectrumCanvas.Children.Add(line);
        }

        ApplySpectrumGridLineFills();

        _spectrumCurvePath = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stroke = null,
            Opacity = 0.52,
        };
        ApplySpectrumCurveFill();
        System.Windows.Controls.Panel.SetZIndex(_spectrumCurvePath, 1);
        SpectrumCanvas.Children.Add(_spectrumCurvePath);
    }

    private void UpdateVisualizerUi()
    {
        if (_visualizerMode == VisualizerMode.Off)
            return;

        var (vuL, vuR, bands) = _engine.GetAudioAnalysisSnapshot();

        if (vuL > 0.001f || vuR > 0.001f)
            _lastNonZeroVuUtc = DateTime.UtcNow;

        // Always update VU meters (even when spectrum view is active).
        VuLeftBar.Value = Math.Clamp(vuL, 0, 1);
        VuRightBar.Value = Math.Clamp(vuR, 0, 1);

        if (_visualizerMode == VisualizerMode.Vu)
        {
            return;
        }

        EnsureSpectrumBars();
        if (_spectrumCurvePath is null)
            return;

        var w = SpectrumCanvas.ActualWidth;
        var h = SpectrumCanvas.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // Keep background grid lines sized/positioned (evenly spaced; count independent of UI scale).
        if (_spectrumGridLines is { Length: > 0 })
        {
            var n = _spectrumGridLines.Length;
            var ih = Math.Max(0, (int)Math.Floor(h));
            var maxTop = ih - 1;
            Span<int> tops = stackalloc int[n];
            if (maxTop >= 0)
                AssignSpectrumGridLineTops(tops, h, maxTop);
            for (var i = 0; i < n; i++)
            {
                var line = _spectrumGridLines[i];
                line.Width = w;
                Canvas.SetLeft(line, 0);
                Canvas.SetTop(line, maxTop >= 0 ? tops[i] : 0);
            }
        }

        var count = Math.Min(AudioAnalyzer.SpectrumBands, bands.Length);
        if (count < 1)
            return;

        // X = log(f): matches analyzer bands (geometric Hz edges = equal Δlog f per band, not linear Hz bins).
        var logLo = Math.Log(AudioAnalyzer.SpectrumFreqMinHz);
        AudioAnalyzer.GetSpectrumBandEdgesHz(count - 1, out _, out var bandMaxHz);
        var logDen = Math.Max(Math.Log(bandMaxHz) - logLo, 1e-12);

        double XAtHz(double fHz)
        {
            var x = w * (Math.Log(fHz) - logLo) / logDen;
            if (x < 0) x = 0;
            if (x > w) x = w;
            return x;
        }

        if (count < 2)
        {
            _spectrumCurvePath.Data = null;
            return;
        }

        var cPts = new System.Windows.Point[count];
        for (var i = 0; i < count; i++)
        {
            AudioAnalyzer.GetSpectrumBandEdgesHz(i, out var f0, out var f1);
            var xm = (XAtHz(f0) + XAtHz(f1)) * 0.5;
            var v = Math.Clamp(bands[i], 0f, 1f);
            cPts[i] = new System.Windows.Point(xm, h - v * h);
        }

        var nPts = count + 2;
        var pts = new System.Windows.Point[nPts];
        var dxPh = cPts.Length >= 2
            ? Math.Max(cPts[1].X - cPts[0].X, w * 0.012)
            : w * 0.04;
        pts[0] = new System.Windows.Point(cPts[0].X - Math.Max(dxPh * 0.62, w * 0.012), h);
        pts[1] = new System.Windows.Point(0, h);
        for (var i = 0; i < count; i++)
            pts[i + 2] = cPts[i];

        var sg = new System.Windows.Media.StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(pts[1], isFilled: true, isClosed: true);

            for (var j = 0; j < count; j++)
            {
                var p0 = pts[j];
                var p1 = pts[j + 1];
                var p2 = pts[j + 2];
                var p3 = pts[Math.Min(j + 3, nPts - 1)];

                var cp1 = new System.Windows.Point(
                    p1.X + (p2.X - p0.X) / 6.0,
                    p1.Y + (p2.Y - p0.Y) / 6.0);
                var cp2 = new System.Windows.Point(
                    p2.X - (p3.X - p1.X) / 6.0,
                    p2.Y - (p3.Y - p1.Y) / 6.0);

                ctx.BezierTo(cp1, cp2, p2, isStroked: true, isSmoothJoin: true);
            }

            ctx.LineTo(new System.Windows.Point(w, h), isStroked: false, isSmoothJoin: false);
        }

        sg.Freeze();
        _spectrumCurvePath.Data = sg;
    }
}
