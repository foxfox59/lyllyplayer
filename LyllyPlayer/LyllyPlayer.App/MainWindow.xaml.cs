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
using LyllyPlayer.Models;
using LyllyPlayer.Utils;
using LyllyPlayer.Player;
using LyllyPlayer.Settings;
using LyllyPlayer.Windows;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace LyllyPlayer;

public partial class MainWindow : Window
{
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
                Original = w._originalEntries.ToList(),
                Current = w._currentEntries.ToList(),
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
            w._originalEntries = Original.ToList();
            w._currentEntries = Current.ToList();
            w._playlistSourceText = PlaylistSourceText;
            w._lastLocalPlaylistPath = LastLocalPlaylistPath;
            w._lastPlaylistSourceType = LastSourceType;
            w._loadedPlaylistId = LoadedPlaylistId;
            w._playlistTitle = PlaylistTitle;
            w._hasLoadedPlaylist = HasLoadedPlaylist;

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(CurrentVideoId))
            {
                var idx = FindIndexByVideoId(w._currentEntries, CurrentVideoId);
                if (idx >= 0 && idx < w._currentEntries.Count)
                    startIndex = idx;
            }

            var displayIndex = w.GetOriginalIndexByVideoId(CurrentVideoId) ?? 0;

            w.SetPlaylistTitle(PlaylistTitle);
            w.SetQueueList(w._originalEntries, w._originalEntries.Count == 0 ? -1 : displayIndex);
            w._engine.SetQueue(w._currentEntries, w._currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            w.UpdateRefreshEnabled();
            w.SyncNowPlayingFromEngine();
            try { w._playlistWindow?.SetSourceText(w._playlistSourceText ?? ""); } catch { /* ignore */ }
            try { w.SetStatusMessage("INFO", "Cancelled."); } catch { /* ignore */ }
            try { w.FocusPlaylistOnNowPlaying(); } catch { /* ignore */ }
        }
    }

    private readonly YtDlpClient _ytDlp;
    private readonly PlaybackEngine _engine;
    private string _ffmpegPath;
    /// <summary>Explicit path from settings, or null to resolve yt-dlp from PATH.</summary>
    private string? _savedYtDlpPath;
    /// <summary>Explicit path from settings, or null to resolve ffmpeg from PATH.</summary>
    private string? _savedFfmpegPath;
    /// <summary>Optional explicit node.exe path; Advanced features need a resolved Node.</summary>
    private string? _savedNodePath;
    private string _ytdlpEjsComponentSource = "github";
    private bool _youtubeCookiesFromBrowserEnabled;
    private string _youtubeCookiesFromBrowser = "";
    private readonly AppSettings _startupSettings;
    private List<PlaylistEntry> _currentEntries = new();
    private List<PlaylistEntry> _originalEntries = new();
    private readonly ObservableCollection<QueueItem> _queueItems = new();
    private readonly Dictionary<string, QueueItem> _queueItemById = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ageRestrictedVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _premiumVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _playlistTitle;
    private string _playlistSourceText = "";
    private int? _autoRefreshMinutes;
    private PlaylistWindow? _playlistWindow;
    private OptionsWindow? _optionsWindow;
    private LogWindow? _logWindow;
    private string _nowPlayingStatus = "STOPPED";
    private PlaylistEntry? _nowPlayingEntry;
    private string? _suppressAutoScrollVideoId;
    private DateTime _suppressAutoScrollUntilUtc;
    private bool _shuffleEnabled;
    private bool _startupResumeAttempted;
    private bool _hasLoadedPlaylist;
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

    // If we closed while paused, restore the timeline but only start playback when user hits Play.
    private string? _pendingResumeVideoId;
    private double _pendingResumeSeconds;
    private readonly DispatcherTimer _persistTimer;
    private bool _snapshotDirty;
    private bool _snapshotPersistInFlight;
    private int _youtubeDurationRequestId;
    private readonly Dictionary<string, int> _youtubeDurationByVideoId = new(StringComparer.OrdinalIgnoreCase);

    // Persist playlist window bounds even when it's closed.
    private Rect? _lastPlaylistBounds;
    private WindowState? _lastPlaylistWindowState;
    // Persist options window bounds even when it's closed.
    private Rect? _lastOptionsBounds;
    private WindowState? _lastOptionsWindowState;
    private enum PlaylistSnapEdge { None, Left, Right, Bottom }
    private bool _syncingWindowMove;
    private bool _playlistSnapped;
    private PlaylistSnapEdge _playlistSnapEdge = PlaylistSnapEdge.None;
    private double _playlistDockYOffset;
    private double _playlistDockXOffset;
    /// <summary>UI scale % that <see cref="_playlistWindow"/> outer Width/Height currently match (for proportional resize when scale changes).</summary>
    private int? _playlistWindowOuterAtUiScalePercent;
    private enum OptionsSnapEdge { None, Left, Right, Bottom }
    private bool _optionsSnapped;
    private OptionsSnapEdge _optionsSnapEdge = OptionsSnapEdge.None;
    private double _optionsDockYOffset;
    private double _optionsDockXOffset;
    /// <summary>Persisted Options tab (header id: Tools, System, …).</summary>
    private string _optionsSelectedTab = "Tools";
    private const double BaseSnapThresholdPx = 18;
    private const double BaseSnapUnsnapPx = 40;
    private const double SnapGapPx = 0;
    // Options should snap similarly to Playlist; too-large thresholds feel "magnetic" and accidental.
    private const double BaseOptionsSnapThresholdPx = 18;
    private const double BaseOptionsSnapUnsnapPx = 40;

    private double SnapThresholdPx => BaseSnapThresholdPx * UiScale;
    private double SnapUnsnapPx => BaseSnapUnsnapPx * UiScale;
    private double OptionsSnapThresholdPx => BaseOptionsSnapThresholdPx * UiScale;
    private double OptionsSnapUnsnapPx => BaseOptionsSnapUnsnapPx * UiScale;

    private const double ChromeDragPendingMoveThresholdDip = 4.0;
    /// <summary>Hold-still fallback so a drag can start without movement (double-click uses ClickCount, not this timer).</summary>
    private static readonly TimeSpan ChromeDragPendingHoldDelay = TimeSpan.FromMilliseconds(120);

    private bool _chromeDragging;
    private DispatcherTimer? _chromeDragStartDelayTimer;
    private bool _chromeDragPendingMoveListener;
    private System.Windows.Point _chromePendingDragWindowPoint;
    private System.Windows.Point _chromeDragStartScreen;
    private double _chromeDragStartLeft;
    private double _chromeDragStartTop;

    /// <summary>Minimal main layout: no options/playlist row; hide shuffle/repeat and playlist title in the card.</summary>
    private bool _mainWindowCompact;
    private bool _compactModeHidesAuxWindows = true;

    /// <summary>Restore <see cref="PlaylistWindow"/> when leaving compact (seeded at startup from settings or when windows were open).</summary>
    private bool _playlistWindowWasOpenBeforeCompact;

    /// <summary>Restore <see cref="OptionsWindow"/> when leaving compact.</summary>
    private bool _optionsWindowWasOpenBeforeCompact;

    private bool _suppressShuffleToggle;
    private bool _suppressCompactShuffleToggle;
    private bool _compactUserOpenedPlaylistWindow;

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
    private string _backgroundMode = "Default";
    private string _customBackgroundImagePath = "";
    private string _backgroundColorMode = "From image";
    private string _customBackgroundColor = "";
    private int _backgroundAlpha = SettingsStore.DefaultBackgroundAlpha;
    private int _backgroundScrimPercent = SettingsStore.DefaultBackgroundScrimPercent;
    private string _backgroundImageStretch = "BestFit";
    private string _appTitleMode = "Default";
    private string _customAppTitle = "";
    private string _appIconVisibility = "TaskbarOnly";
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

    private const uint TrayUid = 1;
    private static readonly Guid TrayGuid = new("d2e9f5c8-40c3-4a4f-a9bf-0f9a6a5f3c2d"); // legacy (no longer used)
    private int _searchDefaultCount = 50;
    private int _searchMinLengthSeconds;
    private bool _readMetadataOnLoad;
    private bool _alwaysOnTop;
    private bool _alwaysOnTopPlaylistWindow;
    private bool _alwaysOnTopOptionsWindow;
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
    private string _audioQuality = "Auto";
    private string? _audioOutputDevice;
    private string _appLogLevel = "ErrorsAndWarnings";
    private int _appLogMaxMb = 2;
    private int _uiScalePercent = 100;
    private string _windowBorderMode = "1px";
    private double _windowBorderCustomPx = 2;
    private double UiScale => Math.Clamp(_uiScalePercent / 100.0, 0.5, 2.0);

    public MainWindow()
    {
        _shellStyleHook = MainWindowShellStyleHwndHook;
        _isFreshSettingsInstall = !File.Exists(SettingsStore.GetSettingsPath());
        _startupSettings = SettingsStore.Load(out _settingsStartupLoadInfo);
        _mainWindowCompact = _startupSettings.MainWindowCompact ?? false;
        _compactModeHidesAuxWindows = _startupSettings.CompactModeHidesAuxWindows ?? true;
        if (_mainWindowCompact)
        {
            _playlistWindowWasOpenBeforeCompact = _startupSettings.PlaylistWindowOpen ?? false;
            _optionsWindowWasOpenBeforeCompact = _startupSettings.OptionsWindowOpen ?? false;
        }

        _savedYtDlpPath = NormalizeToolSave(_startupSettings.YtDlpPath);
        _savedFfmpegPath = NormalizeToolSave(_startupSettings.FfmpegPath);
        _savedNodePath = NormalizeToolSave(_startupSettings.NodeJsPath);
        _ytdlpEjsComponentSource = string.IsNullOrWhiteSpace(_startupSettings.YtdlpEjsComponentSource)
            ? "github"
            : _startupSettings.YtdlpEjsComponentSource.Trim();
        _youtubeCookiesFromBrowserEnabled = _startupSettings.YoutubeCookiesFromBrowserEnabled ?? false;
        _youtubeCookiesFromBrowser = _startupSettings.YoutubeCookiesFromBrowser ?? "";

        var yInit = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
        _ytDlp = new YtDlpClient(yInit.EffectiveFileName);
        var fInit = ToolPathResolver.Resolve(_savedFfmpegPath, "ffmpeg");
        _ffmpegPath = fInit.EffectiveFileName;

        InitializeComponent();

        _appLogLevel = AppLog.NormalizeLevelString(_startupSettings.AppLogLevel);
        try { AppLog.SetLevel(_appLogLevel); } catch { /* ignore */ }
        _appLogMaxMb = Math.Clamp(_startupSettings.AppLogMaxMb ?? SettingsStore.DefaultAppLogMaxMb, 1, 200);
        try { AppLog.SetMaxDiskMegabytes(_appLogMaxMb); } catch { /* ignore */ }

        var startupInfo = _settingsStartupLoadInfo;
        if (startupInfo.SettingsFileExisted)
        {
            var settingsPath = SettingsStore.GetSettingsPath();
            if (startupInfo.RecoveryKind == SettingsStartupRecoveryKind.CorruptUsedDefaults)
            {
                System.Windows.MessageBox.Show(
                    this,
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
                System.Windows.MessageBox.Show(
                    this,
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
        _backgroundMode = string.IsNullOrWhiteSpace(_startupSettings.BackgroundMode) ? "Default" : _startupSettings.BackgroundMode.Trim();
        _customBackgroundImagePath = _startupSettings.CustomBackgroundImagePath ?? "";
        _backgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(_startupSettings.BackgroundColorMode);
        _customBackgroundColor = _startupSettings.CustomBackgroundColor ?? "";
        _backgroundAlpha = _startupSettings.BackgroundAlpha is >= 0 and <= 255 ? _startupSettings.BackgroundAlpha.Value : SettingsStore.DefaultBackgroundAlpha;
        _backgroundScrimPercent = _startupSettings.BackgroundScrimPercent is >= 0 and <= 80 ? _startupSettings.BackgroundScrimPercent.Value : SettingsStore.DefaultBackgroundScrimPercent;
        _backgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(_startupSettings.BackgroundImageStretch);
        _appTitleMode = SettingsStore.NormalizeAppTitleMode(_startupSettings.AppTitleMode);
        _customAppTitle = _startupSettings.CustomAppTitle ?? "";
        _appIconVisibility = SettingsStore.NormalizeAppIconVisibility(_startupSettings.AppIconVisibility);
        _searchDefaultCount = _startupSettings.SearchDefaultCount is >= 1 and <= 200 ? _startupSettings.SearchDefaultCount.Value : 50;
        _searchMinLengthSeconds = _startupSettings.SearchMinLengthSeconds is >= 0 and <= 3600 ? _startupSettings.SearchMinLengthSeconds.Value : 0;
        _readMetadataOnLoad = _startupSettings.ReadMetadataOnLoad ?? false;
        _alwaysOnTop = _startupSettings.AlwaysOnTop ?? false;
        _alwaysOnTopPlaylistWindow = _startupSettings.AlwaysOnTopPlaylistWindow ?? false;
        _alwaysOnTopOptionsWindow = _startupSettings.AlwaysOnTopOptionsWindow ?? false;
        _keepIncompletePlaylistOnCancel = _startupSettings.KeepIncompletePlaylistOnCancel ?? false;
        _audioQuality = _startupSettings.AudioQuality ?? "Auto";
        _audioOutputDevice = string.IsNullOrWhiteSpace(_startupSettings.AudioOutputDevice) ? null : _startupSettings.AudioOutputDevice;
        _uiScalePercent = _startupSettings.UiScalePercent is >= 50 and <= 200 ? _startupSettings.UiScalePercent.Value : 100;
        _windowBorderMode = NormalizeWindowBorderMode(_startupSettings.WindowBorderMode);
        _windowBorderCustomPx = Math.Clamp(_startupSettings.WindowBorderCustomPx ?? 2, 1, 24);
        _optionsSelectedTab = SettingsStore.NormalizeOptionsWindowSelectedTab(_startupSettings.OptionsWindowSelectedTab);
        _cacheMaxMb = Math.Clamp(_startupSettings.CacheMaxMb ?? 512, 16, 102400);
        ApplyBackgroundFromSettings();
        ApplyBackgroundColorsFromSettings();
        ApplyAlwaysOnTopFromSettings();
        ApplyUiScale();
        ApplyAppTitleFromSettings();
        ApplyAppIconVisibilityFromSettings();
        ApplyVisualizerMode(ParseVisualizerMode(_startupSettings.VisualizerMode));
        // Secondary windows may not exist yet (compact startup). Without this, snap/dock/bounds stay at
        // defaults and the next SaveSettingsSnapshot overwrites good persisted layout.
        ApplyStoredAuxiliaryWindowLayoutFromSettings(_startupSettings);
        ApplyMainWindowCompactMode();
        // playlist list is hosted in PlaylistWindow

        _engine = new PlaybackEngine(_ytDlp, ffmpegPath: _ffmpegPath);
        ApplyYtdlpPlaybackOptions();
        try { _ytDlp.SetAudioQuality(_audioQuality); } catch { /* ignore */ }
        try { _engine.NotifyYoutubeAudioQualityChanged(); } catch { /* ignore */ }
        try { _engine.SetAudioOutputDevice(ResolveAudioDeviceNumber(_audioOutputDevice)); } catch { /* ignore */ }
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
        _engine.NowPlayingChanged += (_, entry) =>
        {
            Dispatcher.Invoke(() =>
            {
                // Invalidate in-progress seek so mouse-up cannot SeekAsync against a stale thumb after Next/Prev.
                _isSeeking = false;
                _seekMouseDownVideoId = null;
                try { SeekSlider.ReleaseMouseCapture(); } catch { /* ignore */ }

                _nowPlayingEntry = entry;
                _nowPlayingStatus = entry is null ? "STOPPED" : "FETCHING";
                UpdateNowPlayingText();
                UpdateNowPlayingFlag(entry);
                if (!ShouldSuppressAutoScroll(entry))
                    SelectAndScrollToNowPlaying(entry);
                UpdateDurationUi(entry?.DurationSeconds);
                try { ApplyMainWindowShellIntegration(); } catch { /* ignore */ }
            });

            _ = EnrichLocalNowPlayingAsync(entry);
            _ = EnrichYoutubeDurationNowPlayingAsync(entry);
        };
        _engine.PlaybackStateChanged += (_, isPlaying) =>
            Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = isPlaying ? "||" : ">";
                _nowPlayingStatus = isPlaying ? "BUFFERING" : (_engine.CanResume ? "PAUSED" : "STOPPED");
                UpdateNowPlayingText();
            });
        _engine.PlaybackFailed += (_, payload) =>
            Dispatcher.Invoke(() => HandlePlaybackFailed(payload.entry, payload.message));
        _engine.PrefetchTagged += (_, tag) =>
            Dispatcher.Invoke(() => HandlePrefetchTagged(tag));
        _engine.StatusChanged += (_, payload) =>
            Dispatcher.Invoke(() =>
            {
                // Only show fine-grained loading state for the currently selected/playing entry.
                if (_nowPlayingEntry is null || !string.Equals(_nowPlayingEntry.VideoId, payload.entry.VideoId, StringComparison.OrdinalIgnoreCase))
                    return;
                _nowPlayingStatus = payload.status;
                UpdateNowPlayingText(extraDetail: payload.detail);
            });
        _engine.Error += (_, msg) => Dispatcher.Invoke(() =>
        {
            if (!LooksLikeCancelled(msg))
            {
                _nowPlayingStatus = "ERROR";
                UpdateNowPlayingText(extraDetail: msg);
            }
        });
        _engine.TrackEnded += (_, payload) => Dispatcher.Invoke(async () =>
        {
            // Stream/cache starvation or transient URL expiry — or YouTube ending slightly short of metadata duration.
            if (payload.endedEarly)
            {
                // Log line is emitted in PlaybackEngine (single WARN per early end).
                // Do not flash FETCHING when repeating the same track — resolve will reuse disk cache / watch URL.
                if (_repeatMode != RepeatMode.Single)
                {
                    _nowPlayingStatus = "FETCHING";
                    UpdateNowPlayingText();
                }

                if (_engine.PlayOrder.Count == 0)
                    return;
                if (_repeatMode == RepeatMode.Single)
                {
                    await _engine.PlayCurrentAsync();
                    return;
                }

                if (_engine.CurrentIndex >= _engine.PlayOrder.Count - 1)
                {
                    if (_repeatMode == RepeatMode.Playlist)
                    {
                        _engine.SetQueue(_engine.PlayOrder, startIndex: 0, raiseNowPlayingChanged: true);
                        await _engine.PlayCurrentAsync();
                    }
                    return;
                }
                await _engine.NextAsync();
                return;
            }

            switch (_repeatMode)
            {
                case RepeatMode.Single:
                    await _engine.PlayCurrentAsync();
                    return;
                case RepeatMode.Playlist:
                    if (_engine.PlayOrder.Count == 0)
                        return;
                    if (_engine.CurrentIndex >= _engine.PlayOrder.Count - 1)
                    {
                        _engine.SetQueue(_engine.PlayOrder, startIndex: 0, raiseNowPlayingChanged: true);
                        await _engine.PlayCurrentAsync();
                        return;
                    }
                    await _engine.NextAsync();
                    return;
                default:
                    // None: fall through to normal next, but stop at end.
                    if (_engine.PlayOrder.Count == 0)
                        return;
                    if (_engine.CurrentIndex >= _engine.PlayOrder.Count - 1)
                        return;
                    await _engine.NextAsync();
                    return;
            }
        });

        _shuffleEnabled = _startupSettings.ShuffleEnabled ?? false;
        _suppressShuffleToggle = true;
        ShuffleToggle.IsChecked = _shuffleEnabled;
        _suppressShuffleToggle = false;
        UpdateShuffleToggleContent();

        _repeatMode = ParseRepeatMode(_startupSettings.RepeatMode);
        UpdateRepeatButtonContent();
        UpdateRefreshEnabled();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
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
                        ShowFirstRunExternalToolsNotice();
                    }
                }
                catch { /* ignore */ }
                try
                {
                    if ((_startupSettings.PlaylistWindowOpen ?? false) && !_mainWindowCompact)
                        EnsurePlaylistWindowOpen();
                }
                catch { /* ignore */ }
                try
                {
                    if ((_startupSettings.OptionsWindowOpen ?? false) && !_mainWindowCompact)
                        EnsureOptionsWindowOpen();
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
                                if (_playlistSnapped && _playlistSnapEdge != PlaylistSnapEdge.None)
                                    SyncPlaylistWindowToMain();
                                if (_optionsSnapped && _optionsSnapEdge != OptionsSnapEdge.None)
                                    SyncOptionsWindowToMain();
                                RequestPersistSnapshot();
                            }
                            catch { /* ignore */ }
                        }), DispatcherPriority.ContextIdle);
                    }
                }
                catch { /* ignore */ }
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
                var s = SettingsStore.Load();
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
        // NOTE: Avoid refreshing shell styles on Deactivated. It can interfere with taskbar minimize behavior
        // on borderless windows by forcing a frame republish at the wrong time.
        Deactivated += (_, _) => { };
        Closing += (_, _) =>
        {
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

            var info = await LocalMetadataService.TryGetInfoAsync(_ffmpegPath, localPath, CancellationToken.None);
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
                UpdateDurationUi(_engine.CurrentDurationSeconds);
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

            for (var i = 0; i < _originalEntries.Count; i++)
            {
                if (!string.Equals(_originalEntries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var e = _originalEntries[i];
                var newE = e with
                {
                    Title = string.IsNullOrWhiteSpace(title) ? e.Title : title!,
                    Channel = string.IsNullOrWhiteSpace(artist) ? e.Channel : artist,
                    DurationSeconds = durationSeconds ?? e.DurationSeconds
                };
                _originalEntries[i] = newE;
                updatedEntry = newE;
                break;
            }

            if (_currentEntries is List<PlaylistEntry> curList)
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

            // Update queue UI item by replacing the QueueItem instance.
            if (_queueItemById.TryGetValue(videoId, out var existing))
            {
                var idx = _queueItems.IndexOf(existing);
                if (idx >= 0)
                {
                    var replacement = new QueueItem(updatedEntry, existing.IndexPrefix)
                    {
                        IsUnavailable = existing.IsUnavailable,
                        IsAgeRestricted = existing.IsAgeRestricted,
                        IsPremium = existing.IsPremium,
                        IsNowPlaying = existing.IsNowPlaying,
                    };
                    _queueItems[idx] = replacement;
                    _queueItemById[videoId] = replacement;
                }
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
        try
        {
            var fullChromeVis = _mainWindowCompact ? Visibility.Collapsed : Visibility.Visible;
            MainToolsRowGrid.Visibility = fullChromeVis;
            PlaylistTitleTextBlock.Visibility = fullChromeVis;
            ShuffleToggle.Visibility = fullChromeVis;
            RepeatButton.Visibility = fullChromeVis;
            try { CompactShuffleToggleButton.Visibility = _mainWindowCompact ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactRepeatButton.Visibility = _mainWindowCompact ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
            try { CompactPlaylistButton.Visibility = _mainWindowCompact ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }

            ChromeCompactLayoutButton.Content = _mainWindowCompact ? "[+]" : "[-]";

            ApplyMainWindowCompactLayoutDensity();
            ApplyCompactAuxiliaryWindowState();

            // Saved window bounds may set an explicit Height; clear so SizeToContent can follow the card.
            SizeToContent = SizeToContent.Height;
            ClearValue(FrameworkElement.HeightProperty);
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

            try
            {
                if (_playlistSnapped && _playlistSnapEdge != PlaylistSnapEdge.None)
                    SyncPlaylistWindowToMain();
                if (_optionsSnapped && _optionsSnapEdge != OptionsSnapEdge.None)
                    SyncOptionsWindowToMain();
            }
            catch
            {
                // ignore
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void ApplyCompactAuxiliaryWindowState()
    {
        if (_mainWindowCompact)
        {
            if (_compactModeHidesAuxWindows)
            {
                _playlistWindowWasOpenBeforeCompact = (_playlistWindow is not null) || _playlistWindowWasOpenBeforeCompact;
                _optionsWindowWasOpenBeforeCompact = (_optionsWindow is not null) || _optionsWindowWasOpenBeforeCompact;
                CloseAuxiliaryWindowsForCompact();
            }
        }
        else
        {
            TryRestoreAuxiliaryWindowsAfterCompact();
        }
    }

    private void CloseAuxiliaryWindowsForCompact()
    {
        try
        {
            if (_optionsWindow is not null)
            {
                try { CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState); } catch { /* ignore */ }
                try { _optionsWindow.Close(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_playlistWindow is not null && !_compactUserOpenedPlaylistWindow)
            {
                try { CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState); } catch { /* ignore */ }
                try { _playlistWindow.Close(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
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
                // Expanded: transport * (clips if crowded) so the volume strip never extends past the card edge.
                TransportVolumeRowGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);

                TransportVolumeRowGrid.ColumnDefinitions[1].Width = new GridLength(0);
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
                VisualizerBorder.Padding = new Thickness(4, 0, 4, 0);
                VisualizerHostGrid.Height = 24.0;
                if (SpectrumPanelChrome is not null)
                    SpectrumPanelChrome.Height = 24.0;
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

    private void ToggleMainWindowCompactMode()
    {
        _mainWindowCompact = !_mainWindowCompact;
        ApplyMainWindowCompactMode();
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
            _chromeDragging = true;
            _chromeDragStartLeft = Left;
            _chromeDragStartTop = Top;
            _chromeDragStartScreen = screenPoint;

            CaptureMouse();
            MouseMove -= ChromeDrag_MouseMove;
            MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
            MouseMove += ChromeDrag_MouseMove;
            MouseLeftButtonUp += ChromeDrag_MouseLeftButtonUp;
        }
        catch
        {
            _chromeDragging = false;
        }
    }

    private void ChromeDrag_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_chromeDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndChromeDrag();
            return;
        }

        try
        {
            var cur = PointToScreen(e.GetPosition(this));
            var dx = cur.X - _chromeDragStartScreen.X;
            var dy = cur.Y - _chromeDragStartScreen.Y;
            Left = _chromeDragStartLeft + dx;
            Top = _chromeDragStartTop + dy;
        }
        catch
        {
            EndChromeDrag();
        }
    }

    private void ChromeDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndChromeDrag();

    private void EndChromeDrag()
    {
        if (!_chromeDragging)
            return;
        _chromeDragging = false;
        CancelChromeDragStartTimer();
        try { ReleaseMouseCapture(); } catch { /* ignore */ }
        MouseMove -= ChromeDrag_MouseMove;
        MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
    }

    private void InitializeGlobalMediaKeys()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            _mediaHotkeys = new GlobalMediaHotkeys(hwnd);
            _mediaHotkeys.PlayPausePressed += (_, _) => Dispatcher.Invoke(OnGlobalPlayPause);
            _mediaHotkeys.NextPressed += (_, _) => Dispatcher.Invoke(() => _ = _engine.NextAsync());
            _mediaHotkeys.PrevPressed += (_, _) => Dispatcher.Invoke(() => _ = _engine.PrevAsync());

            _mediaHotkeys.TryRegister();
        }
        catch
        {
            // ignore
        }
    }

    private void PlaylistToolsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_playlistWindow is not null)
        {
            try
            {
                // Capture before closing (the Closing event also captures, but don't rely on field state).
                CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState);
                _playlistWindow.Close();
            }
            catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }
            return;
        }

        EnsurePlaylistWindowOpen();
    }

    private void EnsurePlaylistWindowOpen()
    {
        try
        {
            if (_playlistWindow is not null)
                return;

            // Always restore playlist window bounds from the latest saved settings.
            // (_startupSettings is a startup snapshot and won't reflect changes made during this run.)
            var latestSettings = SettingsStore.Load();
            AppLog.Info($"Playlist bounds (open) settings: L={latestSettings.PlaylistWindowLeft} T={latestSettings.PlaylistWindowTop} W={latestSettings.PlaylistWindowWidth} H={latestSettings.PlaylistWindowHeight} State={latestSettings.PlaylistWindowState}");
            try
            {
                var path = SettingsStore.GetSettingsPath();
                if (File.Exists(path))
                {
                    var raw = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxAppSettingsFileBytes);
                    using var doc = JsonDocument.Parse(raw, SafeJson.CreateDocumentOptions());
                    var root = doc.RootElement;
                    var rawL = root.TryGetProperty("PlaylistWindowLeft", out var l) ? l.ToString() : "(missing)";
                    var rawT = root.TryGetProperty("PlaylistWindowTop", out var t) ? t.ToString() : "(missing)";
                    AppLog.Info($"Playlist bounds (open) raw-json: path={path} L={rawL} T={rawT}");
                }
                else
                {
                    AppLog.Info($"Playlist bounds (open) raw-json: settings file missing at {path}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception(ex, "Playlist bounds raw-json read failed");
            }

            _playlistWindow = new PlaylistWindow(
                loadUrlAsync: async (src) =>
                {
                    _playlistSourceText = src;

                    // Accept direct stream URLs (Icecast/Shoutcast/etc) as a single-item playlist.
                    if (TryParseHttpUrl(src, out var uri) && !LooksLikeYoutube(uri))
                    {
                        _lastPlaylistSourceType = PlaylistSourceType.M3U;
                        _lastLocalPlaylistPath = src;

                        var entries = new List<PlaylistEntry>
                        {
                            new PlaylistEntry(
                                VideoId: StreamIdFromUrl(src),
                                Title: uri.Host,
                                Channel: null,
                                DurationSeconds: null,
                                WebpageUrl: src
                            )
                        };

                        await LoadPlaylistFromEntriesAsync(entries, title: uri.Host, sourceKey: src, isStartupAutoLoad: false);
                        return;
                    }

                    await LoadPlaylistFromSourceAsync(forceFetch: false, isStartupAutoLoad: false);
                },
                searchYoutubeAsync: async (query, count, minLenSeconds, ct) =>
                {
                    // True only after at least one yt-dlp batch produced non-empty entries and we applied it (not the pre-search playlist).
                    var loadedAnySearchResults = false;
                    try
                    {
                        _lastPlaylistSourceType = PlaylistSourceType.SearchYoutubeMusic;
                        _lastLocalPlaylistPath = null;
                        _playlistSourceText = $"Search: {query}";

                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        linked.CancelAfter(TimeSpan.FromMinutes(3));
                        var token = linked.Token;

                        // Staged ytsearch fetches so [Stop] can keep the last completed batch (folder overlay pattern).
                        var maxFetch = Math.Clamp((int)Math.Round(count * 2.0), 20, 200);
                        const int step = 20;
                        var targets = new List<int>();
                        for (var t = step; t < maxFetch; t += step)
                            targets.Add(t);
                        if (targets.Count == 0 || targets[^1] != maxFetch)
                            targets.Add(maxFetch);

                        var batchCount = targets.Count;
                        for (var ti = 0; ti < targets.Count; ti++)
                        {
                            token.ThrowIfCancellationRequested();
                            var flat = targets[ti];
                            var res = await _ytDlp.ResolveYoutubeMusicSearchAsync(query, count, minLenSeconds, flat, token).ConfigureAwait(true);
                            var entries = PlaylistDeduper.DedupeForSearch(res.Entries);
                            token.ThrowIfCancellationRequested();
                            var isLastSearchBatch = ti == targets.Count - 1;
                            await LoadPlaylistFromEntriesAsync(
                                entries,
                                title: res.PlaylistTitle ?? $"Search: {query}",
                                sourceKey: _playlistSourceText ?? "",
                                isStartupAutoLoad: false,
                                token,
                                deferNowPlayingChanged: !isLastSearchBatch).ConfigureAwait(true);
                            if (entries.Count > 0)
                                loadedAnySearchResults = true;
                            if (batchCount > 0)
                            {
                                try
                                {
                                    _playlistWindow?.ReportBusyOverlayDeterminate((ti + 1) / (double)batchCount);
                                }
                                catch { /* ignore */ }
                            }
                        }

                        CommitCancelPlaylistSnapshot();
                    }
                    catch (OperationCanceledException)
                    {
                        var kind = 0;
                        try { kind = _playlistWindow?.TakeSearchDismissKind() ?? 0; } catch { /* ignore */ }
                        if (!loadedAnySearchResults)
                        {
                            try { RollbackCancelPlaylistSnapshot(); } catch { /* ignore */ }
                        }
                        else if (kind == 2 || (_keepIncompletePlaylistOnCancel && (kind == 1 || kind == 0)))
                        {
                            CommitCancelPlaylistSnapshot();
                            if (kind == 2)
                                try { SetStatusMessage("INFO", $"Search stopped; kept {_originalEntries.Count} tracks."); } catch { /* ignore */ }
                            else
                                try { SetStatusMessage("INFO", $"Search cancelled; kept {_originalEntries.Count} tracks."); } catch { /* ignore */ }
                        }
                        else
                        {
                            try { RollbackCancelPlaylistSnapshot(); } catch { /* ignore */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { RollbackCancelPlaylistSnapshot(); } catch { /* ignore */ }
                        AppLog.Exception(ex, "Search failed");
                        try { SetStatusMessage("ERROR", $"Search failed. {ex.Message}".Trim()); } catch { /* ignore */ }
                    }
                },
                getSearchDefaults: () => (_searchDefaultCount, _searchMinLengthSeconds),
                setSearchDefaults: (count, minLenSeconds) =>
                {
                    _searchDefaultCount = Math.Clamp(count, 1, 200);
                    _searchMinLengthSeconds = Math.Clamp(minLenSeconds, 0, 3600);
                    RequestPersistSnapshot();
                },
                savePlaylistToFileAsync: async (path, displayName) =>
                {
                    try
                    {
                        var name = string.IsNullOrWhiteSpace(displayName) ? (_playlistTitle ?? "Playlist") : displayName.Trim();
                        var srcType = _lastPlaylistSourceType.ToString();
                        var src = _playlistSourceText ?? "";
                        var pl = SavedPlaylistFile.FromEntries(name, srcType, src, _originalEntries);
                        SavedPlaylistFile.Save(path, pl);
                        SetStatusMessage("INFO", $"Saved playlist: {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Exception(ex, "Save playlist failed");
                        SetStatusMessage("ERROR", "Save playlist failed.");
                    }
                    await Task.CompletedTask;
                },
                loadPlaylistFromFileAsync: async (path) =>
                {
                    try
                    {
                        var result = SavedPlaylistFile.TryLoadPlaylist(path);
                        if (!result.Success)
                        {
                            System.Windows.MessageBox.Show(
                                this,
                                result.ErrorMessage ?? "Could not load the playlist file.",
                                "LyllyPlayer",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            SetStatusMessage("ERROR", "Load saved playlist failed.");
                            return;
                        }

                        var pl = result.Playlist!;
                        if (result.Entries.Count == 0)
                        {
                            System.Windows.MessageBox.Show(
                                this,
                                "The playlist file is valid but contains no playable tracks.",
                                "LyllyPlayer",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }

                        _lastPlaylistSourceType = ParsePlaylistSourceType(pl.SourceType);
                        _lastLocalPlaylistPath = path;
                        _playlistSourceText = path;
                        _playlistWindow?.SetSourceText(path);

                        await LoadPlaylistFromEntriesAsync(result.Entries, title: pl.Name, sourceKey: path, isStartupAutoLoad: false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Exception(ex, "Load saved playlist failed");
                        SetStatusMessage("ERROR", "Load saved playlist failed.");
                    }
                },
                loadEntriesAsync: async (entries, title, sourceKey, ct) =>
                {
                    // Infer type from source key; persisted separately.
                    _lastPlaylistSourceType =
                        Directory.Exists(sourceKey) ? PlaylistSourceType.Folder :
                        (File.Exists(sourceKey) && Path.GetExtension(sourceKey).Equals(".m3u", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(sourceKey).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                            ? PlaylistSourceType.M3U
                            : PlaylistSourceType.Folder;
                    _lastLocalPlaylistPath = sourceKey;
                    await LoadPlaylistFromEntriesAsync(entries, title, sourceKey, isStartupAutoLoad: false, ct);
                },
                refreshAsync: async (ct) =>
                {
                    try
                    {
                        if (_lastPlaylistSourceType == PlaylistSourceType.YouTube)
                            _playlistWindow?.ReportBusyOverlayIndeterminate();
                        else
                            _playlistWindow?.ClearBusyOverlayProgress();
                    }
                    catch { /* ignore */ }
                    await RefreshCurrentSourceAsync(preserveCurrentIfPossible: true, ct);
                },
                capturePlaylistForCancelRestore: BeginCancelPlaylistSnapshot,
                commitPlaylistCancelRestore: CommitCancelPlaylistSnapshot,
                rollbackPlaylistCancelRestore: RollbackCancelPlaylistSnapshot,
                sourceChanged: (src) => _playlistSourceText = src,
                getSource: () => _playlistSourceText,
                getLastYoutubeUrl: () => _lastYoutubeUrl,
                setLastYoutubeUrl: (url) =>
                {
                    var t = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(url);
                    if (!string.IsNullOrEmpty(t))
                        _lastYoutubeUrl = t;
                },
                getFfmpegPath: () => _ffmpegPath,
                getIncludeSubfoldersOnFolderLoad: () => _includeSubfoldersOnFolderLoad,
                getReadMetadataOnLoad: () => _readMetadataOnLoad,
                getKeepIncompletePlaylistOnCancel: () => _keepIncompletePlaylistOnCancel,
                getRefreshOffersMetadataSkip: () =>
                    _readMetadataOnLoad &&
                    (_lastPlaylistSourceType == PlaylistSourceType.Folder ||
                     _lastPlaylistSourceType == PlaylistSourceType.M3U),
                refreshLocalWithoutMetadataAsync: async (ct) =>
                {
                    await RefreshCurrentSourceAsync(
                        preserveCurrentIfPossible: true,
                        cancellationToken: ct,
                        forceLocalNoMetadata: true).ConfigureAwait(true);
                },
                selectedVideoIdChanged: (videoId) =>
                {
                    if (_engine.PlayOrder.Count == 0) return;
                    var playIdx = FindPlayOrderIndexByVideoId(videoId);
                    if (playIdx < 0)
                    {
                        try { AppLog.Warn($"Playlist selection: VideoId not in current play order ({videoId})."); } catch { /* ignore */ }
                        return;
                    }
                    _engine.SetQueue(_engine.PlayOrder, startIndex: playIdx, raiseNowPlayingChanged: false);
                },
                doubleClickPlayAsync: async (videoId) =>
                {
                    if (_engine.PlayOrder.Count == 0) return;
                    var playIdx = FindPlayOrderIndexByVideoId(videoId);
                    if (playIdx < 0)
                    {
                        try { AppLog.Warn($"Double-click play: VideoId not in current play order ({videoId})."); } catch { /* ignore */ }
                        return;
                    }
                    var selected = _engine.PlayOrder[playIdx];
                    _suppressAutoScrollVideoId = selected.VideoId;
                    _suppressAutoScrollUntilUtc = DateTime.UtcNow.AddSeconds(2);
                    // Same-track replay: clear resume override or UpdateTimelineUi keeps showing the saved position instead of 0.
                    _pendingResumeSeconds = 0;
                    _pendingResumeVideoId = null;
                    _engine.SetQueue(_engine.PlayOrder, startIndex: playIdx, raiseNowPlayingChanged: true);
                    UpdateTimelineUi();
                    await _engine.PlayCurrentAsync();
                }
            )
            {
                Owner = null,
            };
            try { _playlistWindow.Title = $"{GetAppTitleBase()} — Playlist"; } catch { /* ignore */ }

            // After the window is actually loaded (virtualization ready), center on now-playing.
            _playlistWindow.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(FocusPlaylistOnNowPlaying), DispatcherPriority.ContextIdle);

            _playlistWindow.Closing += (_, _) =>
            {
                if (_isShuttingDown)
                    return;
                if (_playlistWindow is not null)
                    CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState);
                // Persist bounds/state when the playlist window is closed.
                SaveSettingsSnapshot();
            };
            _playlistWindow.LocationChanged += (_, _) =>
            {
                if (_syncingWindowMove || _restoringAuxFromMinimize) return;
                if (_playlistWindow is not null)
                    CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState);
                UpdateSnapStateFromPlaylistPosition();
                RequestPersistSnapshot();
            };
            _playlistWindow.SizeChanged += (_, _) =>
            {
                if (_syncingWindowMove || _restoringAuxFromMinimize) return;
                if (_playlistWindow is not null)
                    CaptureWindowBounds(_playlistWindow, out _lastPlaylistBounds, out _lastPlaylistWindowState);
                // If snapped, keep it docked to the edge while resizing playlist window.
                if (_playlistSnapped)
                    SyncPlaylistWindowToMain();
                else
                    UpdateSnapStateFromPlaylistPosition();
                RequestPersistSnapshot();
            };
            _playlistWindow.Closed += (_, _) =>
            {
                _playlistWindow = null;
                _playlistWindowOuterAtUiScalePercent = null;
                // If user closes Playlist, clear the compact "pin open" override.
                _compactUserOpenedPlaylistWindow = false;
            };
            _playlistWindow.SetItemsSource(_queueItems);
            _playlistWindow.SetRefreshEnabled(_hasLoadedPlaylist);
            try { _playlistWindow.ApplyPersistedPlaylistFilter(latestSettings.PlaylistWindowFilter); } catch { /* ignore */ }
            ApplyPlaylistWindowSettings(latestSettings, _playlistWindow);
            NormalizePlaylistWindowOuterForUiScale(latestSettings, _playlistWindow);
            var plS = UiScale;
            _playlistWindow.MinWidth = 420.0 * plS;
            _playlistWindow.MinHeight = 320.0 * plS;
            _playlistWindowOuterAtUiScalePercent = _uiScalePercent;
            AppLog.Info($"Playlist bounds (open) pre-show: L={_playlistWindow.Left} T={_playlistWindow.Top} W={_playlistWindow.Width} H={_playlistWindow.Height} State={_playlistWindow.WindowState}");
            _playlistWindow.Show();
            ApplyAlwaysOnTopFromSettings();
            // Restore snapped positioning relative to main window if previously snapped.
            try
            {
                _playlistSnapped = latestSettings.PlaylistWindowSnapped ?? false;
                _playlistSnapEdge = Enum.TryParse<PlaylistSnapEdge>(latestSettings.PlaylistWindowSnapEdge, ignoreCase: true, out var e) ? e : PlaylistSnapEdge.None;
                _playlistDockYOffset = latestSettings.PlaylistWindowDockYOffset ?? _playlistDockYOffset;
                _playlistDockXOffset = latestSettings.PlaylistWindowDockXOffset ?? _playlistDockXOffset;
                if (_playlistSnapped && _playlistSnapEdge != PlaylistSnapEdge.None)
                    SyncPlaylistWindowToMain();
            }
            catch { /* ignore */ }
            // Critical: compute snap state immediately so moving the main window right after opening
            // keeps the playlist window docked.
            UpdateSnapStateFromPlaylistPosition();
            if (_playlistSnapped)
                SyncPlaylistWindowToMain();
            // Applying WindowState/position is more reliable after showing.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_playlistWindow is null) return;
                // IMPORTANT: don't re-apply saved bounds after showing.
                // That can race with snapping if the user moves the main window immediately.
                UpdateSnapStateFromPlaylistPosition();
                if (_playlistSnapped) SyncPlaylistWindowToMain();
            }), DispatcherPriority.Background);

            // NOTE: We intentionally avoid delayed re-application of bounds here.
            // It caused snapping to break by moving the window back to its persisted position.
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "Open playlist window failed");
            try { _playlistWindow = null; } catch { /* ignore */ }
            try
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"Failed to open Playlist window.\n\n{ex.Message}",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch { /* ignore */ }
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
                return;
            if (_originalEntries.Count == 0)
                return;

            var idx = _originalEntries.FindIndex(e => string.Equals(e.VideoId, cur.VideoId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx >= _originalEntries.Count)
                return;

            // Ensure it's visible even if centering math bails out.
            _playlistWindow.ScrollToIndex(idx);
            _playlistWindow.CenterIndex(idx);
        }
        catch
        {
            // ignore
        }
    }

    private void OptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_optionsWindow is not null)
        {
            try
            {
                CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState);
                _optionsWindow.Close();
            }
            catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }
            return;
        }

        EnsureOptionsWindowOpen();
    }

    private void EnsureOptionsWindowOpen()
    {
        if (_optionsWindow is not null)
            return;

        var latestSettings = SettingsStore.Load();
        AppLog.Info($"Options bounds (open) settings: L={latestSettings.OptionsWindowLeft} T={latestSettings.OptionsWindowTop} W={latestSettings.OptionsWindowWidth} H={latestSettings.OptionsWindowHeight} State={latestSettings.OptionsWindowState}");
        try
        {
            var path = SettingsStore.GetSettingsPath();
            if (File.Exists(path))
            {
                var raw = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxAppSettingsFileBytes);
                using var doc = JsonDocument.Parse(raw, SafeJson.CreateDocumentOptions());
                var root = doc.RootElement;
                var rawL = root.TryGetProperty("OptionsWindowLeft", out var l) ? l.ToString() : "(missing)";
                var rawT = root.TryGetProperty("OptionsWindowTop", out var t) ? t.ToString() : "(missing)";
                AppLog.Info($"Options bounds (open) raw-json: path={path} L={rawL} T={rawT}");
            }
            else
            {
                AppLog.Info($"Options bounds (open) raw-json: settings file missing at {path}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "Options bounds raw-json read failed");
        }
        _optionsWindow = new OptionsWindow(
                getYtDlpPath: () => _savedYtDlpPath ?? "",
                setYtDlpPath: (p) =>
                {
                    _savedYtDlpPath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                    ApplyResolvedToolPaths();
                    ApplyYtdlpPlaybackOptions();
                    RequestPersistSnapshot();
                },
                getFfmpegPath: () => _savedFfmpegPath ?? "",
                setFfmpegPath: (p) =>
                {
                    _savedFfmpegPath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                    ApplyResolvedToolPaths();
                    RequestPersistSnapshot();
                },
                getNodeJsPath: () => _savedNodePath ?? "",
                setNodeJsPath: (p) =>
                {
                    _savedNodePath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                    ApplyYtdlpPlaybackOptions();
                    RequestPersistSnapshot();
                },
                getYtdlpEjsComponentSource: () => _ytdlpEjsComponentSource,
                setYtdlpEjsComponentSource: (s) =>
                {
                    var t = (s ?? "").Trim();
                    _ytdlpEjsComponentSource = string.Equals(t, "bundled", StringComparison.OrdinalIgnoreCase) ? "bundled" : "github";
                    ApplyYtdlpPlaybackOptions();
                    RequestPersistSnapshot();
                },
                getYoutubeCookiesFromBrowserEnabled: () => _youtubeCookiesFromBrowserEnabled,
                setYoutubeCookiesFromBrowserEnabled: (v) =>
                {
                    _youtubeCookiesFromBrowserEnabled = v;
                    ApplyYtdlpPlaybackOptions();
                    RequestPersistSnapshot();
                },
                getYoutubeCookiesFromBrowser: () => _youtubeCookiesFromBrowser,
                setYoutubeCookiesFromBrowser: (t) =>
                {
                    _youtubeCookiesFromBrowser = t ?? "";
                    ApplyYtdlpPlaybackOptions();
                    RequestPersistSnapshot();
                },
                getCacheMaxMb: () => _cacheMaxMb,
                setCacheMaxMb: (mb) =>
                {
                    _cacheMaxMb = Math.Clamp(mb, 16, 102400);
                    try { _engine.SetCacheMaxBytes((long)_cacheMaxMb * 1024L * 1024L); } catch { /* ignore */ }
                    RequestPersistSnapshot();
                },
                getPlaylistAutoRefreshMinutes: () => _autoRefreshMinutes,
                setPlaylistAutoRefreshMinutes: (m) =>
                {
                    _autoRefreshMinutes = m;
                    ApplyAutoRefreshSelection();
                    RequestPersistSnapshot();
                },
                getGlobalMediaKeysEnabled: () => _globalMediaKeysEnabled,
                setGlobalMediaKeysEnabled: (v) => SetGlobalMediaKeysEnabled(v),
                getBackgroundMode: () => _backgroundMode,
                setBackgroundMode: (m) =>
                {
                    _backgroundMode = string.IsNullOrWhiteSpace(m) ? "Default" : m.Trim();
                    ApplyBackgroundFromSettings();
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                getCustomBackgroundImagePath: () => _customBackgroundImagePath,
                setCustomBackgroundImagePath: (p) =>
                {
                    _customBackgroundImagePath = p ?? "";
                    ApplyBackgroundFromSettings();
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                getBackgroundColorMode: () => _backgroundColorMode,
                setBackgroundColorMode: (m) =>
                {
                    _backgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(m);
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                getCustomBackgroundColor: () => _customBackgroundColor,
                setCustomBackgroundColor: (hex) =>
                {
                    _customBackgroundColor = hex ?? "";
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                getBackgroundAlpha: () => _backgroundAlpha,
                setBackgroundAlpha: (a) =>
                {
                    _backgroundAlpha = Math.Clamp(a, 0, 255);
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                getBackgroundScrimPercent: () => _backgroundScrimPercent,
                setBackgroundScrimPercent: (p) =>
                {
                    _backgroundScrimPercent = Math.Clamp(p, 0, 80);
                    ApplyBackgroundFromSettings();
                    RequestPersistSnapshot();
                },
                getBackgroundImageStretch: () => _backgroundImageStretch,
                setBackgroundImageStretch: (s) =>
                {
                    _backgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(s);
                    ApplyBackgroundFromSettings();
                    RequestPersistSnapshot();
                },
                getAppTitleMode: () => _appTitleMode,
                setAppTitleMode: (m) =>
                {
                    _appTitleMode = SettingsStore.NormalizeAppTitleMode(m);
                    ApplyAppTitleFromSettings();
                    RequestPersistSnapshot();
                },
                getCustomAppTitle: () => _customAppTitle,
                setCustomAppTitle: (t) =>
                {
                    _customAppTitle = t ?? "";
                    ApplyAppTitleFromSettings();
                    RequestPersistSnapshot();
                },
                getUiScalePercent: () => _uiScalePercent,
                setUiScalePercent: (p) =>
                {
                    var v = Math.Clamp(p, 50, 200);
                    if (v == _uiScalePercent)
                        return;
                    _uiScalePercent = v;
                    ApplyUiScale();
                    RequestPersistSnapshot();
                },
                getWindowBorderMode: () => _windowBorderMode,
                setWindowBorderMode: (m) =>
                {
                    _windowBorderMode = NormalizeWindowBorderMode(m);
                    ApplyWindowBorderFromSettings();
                    RequestPersistSnapshot();
                },
                getWindowBorderCustomPx: () => _windowBorderCustomPx,
                setWindowBorderCustomPx: (px) =>
                {
                    _windowBorderCustomPx = Math.Clamp(px, 1, 24);
                    ApplyWindowBorderFromSettings();
                    RequestPersistSnapshot();
                },
                getTheme: () => GetCurrentThemeSettings(),
                applyTheme: (t) => ApplyThemeSettings(t),
                getSearchDefaults: () => (_searchDefaultCount, _searchMinLengthSeconds),
                setSearchDefaults: (count, minLen) =>
                {
                    _searchDefaultCount = Math.Clamp(count, 1, 200);
                    _searchMinLengthSeconds = Math.Clamp(minLen, 0, 3600);
                    RequestPersistSnapshot();
                },
                getIncludeSubfoldersOnFolderLoad: () => _includeSubfoldersOnFolderLoad,
                setIncludeSubfoldersOnFolderLoad: (v) =>
                {
                    _includeSubfoldersOnFolderLoad = v;
                    RequestPersistSnapshot();
                },
                getReadMetadataOnLoad: () => _readMetadataOnLoad,
                setReadMetadataOnLoad: (v) =>
                {
                    _readMetadataOnLoad = v;
                    RequestPersistSnapshot();
                },
                getAlwaysOnTopPlaylistWindow: () => _alwaysOnTopPlaylistWindow,
                setAlwaysOnTopPlaylistWindow: (v) => SetAlwaysOnTopPlaylistWindow(v),
                getAlwaysOnTopOptionsWindow: () => _alwaysOnTopOptionsWindow,
                setAlwaysOnTopOptionsWindow: (v) => SetAlwaysOnTopOptionsWindow(v),
                getCompactModeHidesAuxWindows: () => _compactModeHidesAuxWindows,
                setCompactModeHidesAuxWindows: (v) => SetCompactModeHidesAuxWindows(v),
                getKeepIncompletePlaylistOnCancel: () => _keepIncompletePlaylistOnCancel,
                setKeepIncompletePlaylistOnCancel: (v) =>
                {
                    _keepIncompletePlaylistOnCancel = v;
                    RequestPersistSnapshot();
                },
                getAppIconVisibility: () => _appIconVisibility,
                setAppIconVisibility: (v) =>
                {
                    _appIconVisibility = SettingsStore.NormalizeAppIconVisibility(v);
                    ApplyAppIconVisibilityFromSettings();
                    RequestPersistSnapshot();
                },
                showLog: () => LogButton_OnClick(sender: this, e: new RoutedEventArgs()),
                getAppLogLevel: () => _appLogLevel,
                setAppLogLevel: (v) =>
                {
                    _appLogLevel = AppLog.NormalizeLevelString(v);
                    try { AppLog.SetLevel(_appLogLevel); } catch { /* ignore */ }
                    RequestPersistSnapshot();
                },
                getAppLogMaxMb: () => _appLogMaxMb,
                setAppLogMaxMb: (mb) =>
                {
                    _appLogMaxMb = Math.Clamp(mb, 1, 200);
                    try { AppLog.SetMaxDiskMegabytes(_appLogMaxMb); } catch { /* ignore */ }
                    RequestPersistSnapshot();
                },
                getAudioQuality: () => _audioQuality,
                setAudioQuality: (q) =>
                {
                    _audioQuality = q ?? "Auto";
                    try { _ytDlp.SetAudioQuality(_audioQuality); } catch { /* ignore */ }
                    try { _engine.NotifyYoutubeAudioQualityChanged(); } catch { /* ignore */ }
                    RequestPersistSnapshot();
                },
                getAudioOutputDevice: () => _audioOutputDevice,
                setAudioOutputDevice: (d) =>
                {
                    _audioOutputDevice = string.IsNullOrWhiteSpace(d) ? null : d;
                    try { _engine.SetAudioOutputDevice(ResolveAudioDeviceNumber(_audioOutputDevice)); } catch { /* ignore */ }
                    RequestPersistSnapshot();
                },
                getOptionsSelectedTab: () => _optionsSelectedTab,
                setOptionsSelectedTab: (tab) =>
                {
                    _optionsSelectedTab = SettingsStore.NormalizeOptionsWindowSelectedTab(tab);
                    RequestPersistSnapshot();
                },
                getThemeMode: () => _themeMode,
                setThemeMode: (m) =>
                {
                    _themeMode = SettingsStore.NormalizeThemeMode(m);
                    ApplyBackgroundFromSettings();
                    ApplyBackgroundColorsFromSettings();
                    RequestPersistSnapshot();
                },
                persistSettingsNow: () =>
                {
                    try { _persistTimer.Stop(); } catch { /* ignore */ }
                    try { SaveSettingsSnapshot(); } catch { /* ignore */ }
                }
            )
            {
                Owner = null,
            };
            try { _optionsWindow.Title = $"{GetAppTitleBase()} — Options"; } catch { /* ignore */ }
            _optionsWindow.Closing += (_, _) =>
            {
                if (_isShuttingDown)
                    return;
                if (_optionsWindow is not null)
                    CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState);
                SaveSettingsSnapshot();
            };
            _optionsWindow.LocationChanged += (_, _) =>
            {
                if (_syncingWindowMove || _restoringAuxFromMinimize) return;
                if (_optionsWindow is not null)
                    CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState);
                UpdateSnapStateFromOptionsPosition();
                RequestPersistSnapshot();
            };
            _optionsWindow.SizeChanged += (_, _) =>
            {
                if (_syncingWindowMove || _restoringAuxFromMinimize) return;
                if (_optionsWindow is not null)
                    CaptureWindowBounds(_optionsWindow, out _lastOptionsBounds, out _lastOptionsWindowState);
                if (_optionsSnapped)
                    SyncOptionsWindowToMain();
                // IMPORTANT: don't auto-snap on size changes (UI scale / theme / layout). Snapping should be
                // user-driven via moving the window near an edge (LocationChanged path).
                RequestPersistSnapshot();
            };
            _optionsWindow.Closed += (_, _) => _optionsWindow = null;
            ApplyOptionsWindowScaledChromeSize(_optionsWindow);
            ApplyOptionsWindowSettings(latestSettings, _optionsWindow);
            _optionsWindow.Show();
            ApplyAlwaysOnTopFromSettings();
            // Restore snapped positioning relative to main window if previously snapped.
            try
            {
                _optionsSnapped = latestSettings.OptionsWindowSnapped ?? false;
                _optionsSnapEdge = Enum.TryParse<OptionsSnapEdge>(latestSettings.OptionsWindowSnapEdge, ignoreCase: true, out var e) ? e : OptionsSnapEdge.None;
                _optionsDockYOffset = latestSettings.OptionsWindowDockYOffset ?? _optionsDockYOffset;
                _optionsDockXOffset = latestSettings.OptionsWindowDockXOffset ?? _optionsDockXOffset;
                if (_optionsSnapped && _optionsSnapEdge != OptionsSnapEdge.None)
                    SyncOptionsWindowToMain();
            }
            catch { /* ignore */ }
            // Do not auto-detect snap state here. If the window was snapped previously, we already synced above.
            // If it wasn't, opening it near an edge should not force it to become snapped.

            // NOTE: avoid delayed bounds re-apply; it races with snapping.
    }

    private void SyncPlaylistWindowToMain()
    {
        if (_playlistWindow is null) return;
        if (!_playlistSnapped) return;
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
                _ => mainTop + _playlistDockYOffset
            };

            // For bottom snap we preserve horizontal offset.
            if (_playlistSnapEdge == PlaylistSnapEdge.Bottom)
                desiredLeft = mainLeft + _playlistDockXOffset;

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

    private void OnMainWindowMovedOrSized()
    {
        if (_playlistSnapped)
            SyncPlaylistWindowToMain();
        if (_optionsSnapped)
            SyncOptionsWindowToMain();

        // If not snapped, do nothing. Snapping is only initiated by moving the secondary window near the main window.
    }

    /// <summary>
    /// Bottom-snapped Options is wider than the main window; keep its horizontal span over the main chrome only
    /// so it does not extend under a right-docked playlist on first launch and when the main window is narrow.
    /// </summary>
    private static double ClampOptionsBottomSnapLeft(double mainLeft, double mainRight, double optionsOuterWidth, double dockX)
    {
        var mainW = Math.Max(0, mainRight - mainLeft);
        var inner = mainLeft + dockX;
        if (optionsOuterWidth <= mainW + 1e-6)
            return Math.Clamp(inner, mainLeft, mainRight - optionsOuterWidth);
        return mainRight - optionsOuterWidth;
    }

    private void SyncOptionsWindowToMain()
    {
        if (_optionsWindow is null) return;
        if (!_optionsSnapped) return;
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
                _ => mainTop + _optionsDockYOffset
            };

            if (_optionsSnapEdge == OptionsSnapEdge.Bottom)
                desiredLeft = ClampOptionsBottomSnapLeft(mainLeft, mainRight, ow.Width, _optionsDockXOffset);

            _optionsWindow.Left = SnapRound(desiredLeft);
            _optionsWindow.Top = SnapRound(desiredTop);
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

            var owLeft = ow.Left;
            var owTop = ow.Top;
            var owRight = ow.Right;
            var owBottom = ow.Bottom;

            var verticalOverlap = Math.Min(mainBottom, owBottom) - Math.Max(mainTop, owTop);
            var hasOverlap = verticalOverlap > 64;

            var horizontalOverlap = Math.Min(mainRight, owRight) - Math.Max(mainLeft, owLeft);
            var hasHOverlap = horizontalOverlap > 64;

            var desiredRightLeft = mainRight + SnapGapPx;
            var desiredLeftLeft = mainLeft - ow.Width - SnapGapPx;
            var desiredBottomTop = mainBottom + SnapGapPx;

            var distToRight = Math.Abs(owLeft - desiredRightLeft);
            var distToLeft = Math.Abs(owLeft - desiredLeftLeft);
            var distToBottom = Math.Abs(owTop - desiredBottomTop);

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
                    _ => double.NaN
                };

                var movedFar = _optionsSnapEdge switch
                {
                    OptionsSnapEdge.Bottom => Math.Abs(owTop - desired) > OptionsSnapUnsnapPx || !hasHOverlap,
                    _ => Math.Abs(owLeft - desired) > OptionsSnapUnsnapPx || !hasOverlap
                };

                if (movedFar)
                {
                    _optionsSnapped = false;
                    _optionsSnapEdge = OptionsSnapEdge.None;
                    return;
                }

                if (_optionsSnapEdge == OptionsSnapEdge.Bottom)
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
                var bottomLeft = ClampOptionsBottomSnapLeft(mainLeft, mainRight, ow.Width, _optionsDockXOffset);
                _optionsDockXOffset = bottomLeft - mainLeft;
                SnapOptionsToEdge(bottomLeft, desiredBottomTop);
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
            _optionsWindow.Left = left;
            _optionsWindow.Top = top;
            _syncingWindowMove = false;
        }
        catch
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

            var plLeft = pl.Left;
            var plTop = pl.Top;
            var plRight = pl.Right;
            var plBottom = pl.Bottom;

            // Consider snap only if windows overlap vertically a bit (prevents accidental snap).
            var verticalOverlap = Math.Min(mainBottom, plBottom) - Math.Max(mainTop, plTop);
            var hasOverlap = verticalOverlap > 64;

            // For bottom snap require horizontal overlap.
            var horizontalOverlap = Math.Min(mainRight, plRight) - Math.Max(mainLeft, plLeft);
            var hasHOverlap = horizontalOverlap > 64;

            var desiredRightLeft = mainRight + SnapGapPx;
            var desiredLeftLeft = mainLeft - pl.Width - SnapGapPx;
            var desiredBottomTop = mainBottom + SnapGapPx;

            var distToRight = Math.Abs(plLeft - desiredRightLeft);
            var distToLeft = Math.Abs(plLeft - desiredLeftLeft);
            var distToBottom = Math.Abs(plTop - desiredBottomTop);

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
                    _ => double.NaN
                };

                var movedFar = _playlistSnapEdge switch
                {
                    PlaylistSnapEdge.Bottom => Math.Abs(plTop - desired) > SnapUnsnapPx || !hasHOverlap,
                    _ => Math.Abs(plLeft - desired) > SnapUnsnapPx || !hasOverlap
                };

                if (movedFar)
                {
                    _playlistSnapped = false;
                    _playlistSnapEdge = PlaylistSnapEdge.None;
                    return;
                }

                // Update vertical dock offset only (avoid jittery re-snapping).
                if (_playlistSnapEdge == PlaylistSnapEdge.Bottom)
                    _playlistDockXOffset = plLeft - mainLeft;
                else
                    _playlistDockYOffset = plTop - mainTop;
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

    /// <summary>Warns on first launch that yt-dlp and ffmpeg must be installed or configured (Options / PATH).</summary>
    private void ShowFirstRunExternalToolsNotice()
    {
        if (!_isFreshSettingsInstall)
            return;
        try
        {
            System.Windows.MessageBox.Show(
                this,
                "STOP!\n\n" +
                "The player requires yt-dlp and ffmpeg to work properly. " +
                "Make sure you have both downloaded and extracted, and on your PATH or set under Options.\n\n" +
                "Like seriously, we do require those. Without them, search and playback will not work.",
                GetAppTitleBase(),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
                CaptureWindowBounds(_playlistWindow, out var b, out var s);
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
                CaptureWindowBounds(_optionsWindow, out var b, out var s);
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

    private void SyncNowPlayingFromEngine()
    {
        try
        {
            var cur = _engine.GetCurrent();
            if (cur is null)
                return;

            _nowPlayingEntry = cur;
            _nowPlayingStatus = _engine.IsPlaying
                ? "PLAYING"
                : (_engine.CanResume ? "PAUSED" : "STOPPED");
            UpdateNowPlayingText();
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
                _originalEntries.Count > 0)
            {
                SetStatusMessage("INFO", $"Loaded {_originalEntries.Count} items (cached in memory).");
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
                    _originalEntries = cached.Entries.ToList();
                    // Only apply saved play-order from startup settings on initial app startup.
                    var applyIds = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
                    _currentEntries = ApplySavedOrderIfAny(_originalEntries, applyIds, shuffle: _shuffleEnabled).ToList();

                    var cacheStartIndex = 0;
                    var cacheDesiredId = isStartupAutoLoad ? _startupSettings.CurrentVideoId : (_originalEntries.Count > 0 ? _originalEntries[0].VideoId : null);
                    if (!string.IsNullOrWhiteSpace(cacheDesiredId))
                    {
                        var idx = FindIndexByVideoId(_currentEntries, cacheDesiredId);
                        if (idx >= 0 && idx < _currentEntries.Count)
                            cacheStartIndex = idx;
                    }

                    var cacheDisplayIndex = GetOriginalIndexByVideoId(cacheDesiredId) ?? 0;
                    _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : cacheStartIndex, raiseNowPlayingChanged: true);
                    SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : cacheDisplayIndex);
                    _hasLoadedPlaylist = true;
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

            if (!EnsureYtDlpReady())
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
            _originalEntries = entries.ToList();
            // Only apply saved play-order from startup settings on initial app startup.
            var applyIds2 = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
            _currentEntries = ApplySavedOrderIfAny(_originalEntries, applyIds2, shuffle: _shuffleEnabled).ToList();

            var startIndex = 0;
            var desiredId = isStartupAutoLoad
                ? _startupSettings.CurrentVideoId
                : (_originalEntries.Count > 0 ? _originalEntries[0].VideoId : null);
            if (!string.IsNullOrWhiteSpace(desiredId))
            {
                var idx = FindIndexByVideoId(_currentEntries, desiredId);
                if (idx >= 0 && idx < _currentEntries.Count)
                    startIndex = idx;
            }

            SetStatusMessage("INFO", entries.Count == 0 ? "Playlist is empty." : $"Loaded {entries.Count} items.");
            SyncNowPlayingFromEngine();

            _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            // Playlist window always shows original playlist order.
            var displayIndex = GetOriginalIndexByVideoId(desiredId) ?? 0;
            SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : displayIndex);
            _hasLoadedPlaylist = true;
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
            if (sourceChanged)
            {
                try { _engine.Stop(); } catch { /* ignore */ }
                _pendingResumeSeconds = 0;
                _pendingResumeVideoId = null;
                ResetTimelineUiToStart();
            }

            _loadedPlaylistId = sourceKey;
            _playlistSourceText = sourceKey;
            _lastLocalPlaylistPath = sourceKey;
            try { _playlistWindow?.SetSourceText(_playlistSourceText ?? ""); } catch { /* ignore */ }

            var list = entries?.ToList() ?? new List<PlaylistEntry>();
            _originalEntries = list;
            // Only apply saved play-order from startup settings on initial app startup.
            var applyIds3 = !_hasLoadedPlaylist ? _startupSettings.PlayOrderVideoIds : null;
            _currentEntries = ApplySavedOrderIfAny(_originalEntries, applyIds3, shuffle: _shuffleEnabled).ToList();

            var startIndex = 0;
            var desiredId = isStartupAutoLoad
                ? _startupSettings.CurrentVideoId
                : (_originalEntries.Count > 0 ? _originalEntries[0].VideoId : null);
            if (!string.IsNullOrWhiteSpace(desiredId))
            {
                var idx = FindIndexByVideoId(_currentEntries, desiredId);
                if (idx >= 0 && idx < _currentEntries.Count)
                    startIndex = idx;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetPlaylistTitle(title);
            _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: !deferNowPlayingChanged);
            var displayIndex = GetOriginalIndexByVideoId(desiredId) ?? 0;
            SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : displayIndex);
            _hasLoadedPlaylist = true;
            UpdateRefreshEnabled();
            if (!deferNowPlayingChanged)
                FocusPlaylistOnNowPlaying();

            if (!deferNowPlayingChanged)
            {
                SetStatusMessage("INFO", _originalEntries.Count == 0 ? "Playlist is empty." : $"Loaded {_originalEntries.Count} items.");
                SyncNowPlayingFromEngine();
            }
            else
                SetStatusMessage("INFO", _originalEntries.Count == 0 ? "Searching…" : $"Searching… ({_originalEntries.Count} found)");
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
            LastPlaylistSnapshot? snap = null;
            var snapshotUnreadable = false;
            try { snap = LastPlaylistSnapshotStore.TryLoad(out snapshotUnreadable); } catch { /* ignore */ }

            if (snapshotUnreadable)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "The saved last-playlist file (last-playlist.json) could not be read. It will be ignored.\n\n"
                        + "If the file is damaged, you can delete it from your LyllyPlayer app data folder.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { /* ignore */ }
            }

            if (snap?.Entries is { Count: > 0 } &&
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
                _ffmpegPath,
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
            _ffmpegPath,
            readMetadataOnLoad: _readMetadataOnLoad,
            CancellationToken.None).ConfigureAwait(true);
        await LoadPlaylistFromEntriesAsync(loaded.entries, loaded.title, source, isStartupAutoLoad: true).ConfigureAwait(true);
    }

    private void SaveLastPlaylistSnapshotBestEffort()
    {
        try
        {
            if (_originalEntries is null || _originalEntries.Count == 0)
                return;
            var snap = new LastPlaylistSnapshot(
                SourceType: _lastPlaylistSourceType.ToString(),
                SourceText: _playlistSourceText ?? "",
                Title: _playlistTitle,
                Entries: _originalEntries.ToList()
            );
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

    private LastPlaylistSnapshot? CaptureLastPlaylistSnapshotForWrite()
    {
        try
        {
            if (_originalEntries is null || _originalEntries.Count == 0)
                return null;
            return new LastPlaylistSnapshot(
                SourceType: _lastPlaylistSourceType.ToString(),
                SourceText: _playlistSourceText ?? "",
                Title: _playlistTitle,
                Entries: _originalEntries.ToList()
            );
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

        LastPlaylistSnapshot? snap;
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

    private async Task<bool> TryRestoreLastPlaylistSnapshotAsync(LastPlaylistSnapshot snap)
    {
        try
        {
            if (snap.Entries is null || snap.Entries.Count == 0)
                return false;

            _lastPlaylistSourceType = ParsePlaylistSourceType(snap.SourceType);
            _playlistSourceText = snap.SourceText ?? "";
            if (_lastPlaylistSourceType == PlaylistSourceType.YouTube &&
                PlaylistSourcePathHeuristics.IsStorableLastLoadedYoutubeUrl(_playlistSourceText))
                _lastYoutubeUrl = _playlistSourceText;
            _playlistWindow?.SetSourceText(_playlistSourceText);

            await LoadPlaylistFromEntriesAsync(
                entries: snap.Entries,
                title: snap.Title,
                sourceKey: string.IsNullOrWhiteSpace(_playlistSourceText) ? "snapshot" : _playlistSourceText,
                isStartupAutoLoad: true
            );
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshPlaylistAsync(preserveCurrentIfPossible: true);
    }

    // yt-dlp/ffmpeg are configured in OptionsWindow now.

    private void LogButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_logWindow is not null)
            {
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
        w.Closed += (_, _) => { _logWindow = null; };
        try { w.Title = $"{GetAppTitleBase()} — Log"; } catch { /* ignore */ }
        w.Show();
    }


    private void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        _pendingResumeSeconds = 0;
        _pendingResumeVideoId = null;
        _suppressAutoScrollVideoId = null;
        _ = _engine.PrevAsync();
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
        _pendingResumeSeconds = 0;
        _pendingResumeVideoId = null;
        _suppressAutoScrollVideoId = null;
        _ = _engine.NextAsync();
    }

    private void SetShuffleEnabled(bool enabled)
    {
        _shuffleEnabled = enabled;
        UpdateShuffleToggleContent();
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
                _compactUserOpenedPlaylistWindow = false;
                try { _playlistWindow.Close(); } catch { /* ignore */ }
                return;
            }
            if (_mainWindowCompact && _compactModeHidesAuxWindows)
                _compactUserOpenedPlaylistWindow = true;
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
                CompactRepeatButton.Content = _repeatMode == RepeatMode.Single ? "Ro" : "Ra";
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
            if (!EnsureYtDlpReady())
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
            _originalEntries = entries.ToList();
            _currentEntries = BuildPlayOrder(_originalEntries, shuffle: _shuffleEnabled).ToList();
            _loadedPlaylistId = playlistId;

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentVideoId))
            {
                var idx = FindIndexByVideoId(_currentEntries, currentVideoId);
                if (idx >= 0 && idx < _currentEntries.Count)
                    startIndex = idx;
            }

            _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            var displayIndex = GetOriginalIndexByVideoId(currentVideoId) ?? 0;
            SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : displayIndex);

            SetStatusMessage("INFO", _currentEntries.Count == 0 ? "Playlist is empty." : $"Refreshed {_currentEntries.Count} items.");
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

    private bool EnsureYtDlpReady()
    {
        ApplyResolvedToolPaths();
        var r = ToolPathResolver.Resolve(_savedYtDlpPath, "yt-dlp");
        if (r.IsFound)
            return true;

        SetStatusMessage("ERROR", "yt-dlp not found. Click yt-dlp… to set it.");
        AppLog.Error("yt-dlp not found on PATH and no valid configured path.");
        return false;
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

            if (dlg.ShowDialog(this) != true)
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
        // Refresh is supported for both YouTube and local sources.
        var canRefresh = _hasLoadedPlaylist && _lastPlaylistSourceType != PlaylistSourceType.SearchYoutubeMusic;
        // For direct stream URLs loaded via Load URL, refresh doesn't make sense.
        if (TryParseHttpUrl(_lastLocalPlaylistPath ?? _playlistSourceText, out var u) && !LooksLikeYoutube(u))
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
                _ffmpegPath,
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
                _ffmpegPath,
                readMetadataOnLoad: readMetaForLocal,
                cancellationToken,
                metadataProgress: metaProgress).ConfigureAwait(true);
            entries = loaded.entries;
            title = loaded.title;
        }

        // Load and try to keep the same current track if possible.
        await LoadPlaylistFromEntriesRuntimeAsync(entries, title, source, currentVideoId, cancellationToken);
    }

    private Task LoadPlaylistFromEntriesRuntimeAsync(
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

            _originalEntries = entries?.ToList() ?? new List<PlaylistEntry>();
            _currentEntries = BuildPlayOrder(_originalEntries, shuffle: _shuffleEnabled).ToList();

            var startIndex = 0;
            if (!string.IsNullOrWhiteSpace(preserveCurrentVideoId))
            {
                var idx = FindIndexByVideoId(_currentEntries, preserveCurrentVideoId);
                if (idx >= 0 && idx < _currentEntries.Count)
                    startIndex = idx;
            }

            SetPlaylistTitle(title);
            var displayIndex = GetOriginalIndexByVideoId(preserveCurrentVideoId) ?? 0;
            SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : displayIndex);
            _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: true);
            _hasLoadedPlaylist = true;
            UpdateRefreshEnabled();
            FocusPlaylistOnNowPlaying();

            SetStatusMessage("INFO", _originalEntries.Count == 0 ? "Playlist is empty." : $"Loaded {_originalEntries.Count} items.");
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

        return Task.CompletedTask;
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

        _currentEntries = BuildPlayOrder(_originalEntries, shuffle: _shuffleEnabled).ToList();

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(currentVideoId))
        {
            var idx = FindIndexByVideoId(_currentEntries, currentVideoId);
            if (idx >= 0 && idx < _currentEntries.Count)
                startIndex = idx;
        }

        // Shuffle is only changing play order; avoid raising NowPlayingChanged (it can auto-center the playlist and
        // look like the list "jerks" even when the current track didn't change).
        _engine.SetQueue(_currentEntries, startIndex: _currentEntries.Count == 0 ? -1 : startIndex, raiseNowPlayingChanged: false);

        // Playlist window always shows the original playlist order. Shuffle only changes engine play order — do not
        // clear/rebind the list when rows are already the same (that flashes the Playlist window).
        var displayIndex = GetOriginalIndexByVideoId(currentVideoId) ?? 0;
        SetQueueList(_originalEntries, selectedIndex: _originalEntries.Count == 0 ? -1 : displayIndex, forceFullRebuild: false);
    }

    private int? GetOriginalIndexByVideoId(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;
        var idx = _originalEntries.FindIndex(e => string.Equals(e.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx < _originalEntries.Count ? idx : null;
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

    private IReadOnlyList<PlaylistEntry> BuildPlayOrder(IReadOnlyList<PlaylistEntry> entries, bool shuffle)
    {
        if (!shuffle || entries.Count <= 1)
            return entries;

        var list = entries.ToList();

        // Fisher-Yates
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private IReadOnlyList<PlaylistEntry> ApplySavedOrderIfAny(
        IReadOnlyList<PlaylistEntry> entries,
        List<string>? savedOrderIds,
        bool shuffle)
    {
        if (savedOrderIds is null || savedOrderIds.Count == 0)
            return BuildPlayOrder(entries, shuffle);

        var byId = entries
            .GroupBy(e => e.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var ordered = new List<PlaylistEntry>(entries.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in savedOrderIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (byId.TryGetValue(id, out var e))
            {
                ordered.Add(e);
                used.Add(id);
            }
        }

        foreach (var e in entries)
        {
            if (!used.Contains(e.VideoId))
                ordered.Add(e);
        }

        return ordered;
    }

    private void SetQueueList(IReadOnlyList<PlaylistEntry> entries, int selectedIndex, bool forceFullRebuild = true)
    {
        if (!forceFullRebuild && PlaylistDisplaysSameEntryOrder(entries))
        {
            UpdateNowPlayingFlag(_engine.GetCurrent());
            return;
        }

        _queueItems.Clear();
        _queueItemById.Clear();
        var isLocal = _lastPlaylistSourceType != PlaylistSourceType.YouTube;
        var pad = Math.Max(1, entries.Count.ToString().Length);
        foreach (var e in entries)
        {
            var prefix = isLocal ? $"{( _queueItems.Count + 1).ToString().PadLeft(pad, '0')}. " : null;
            var qi = new QueueItem(e, prefix);
            if (_unavailableVideoIds.Contains(qi.VideoId) || LooksLikeUnavailableTitle(qi.Title))
                qi.IsUnavailable = true;
            if (_ageRestrictedVideoIds.Contains(qi.VideoId))
                qi.IsAgeRestricted = true;
            if (_premiumVideoIds.Contains(qi.VideoId))
                qi.IsPremium = true;
            if (_engine.GetCurrent() is { } cur && string.Equals(cur.VideoId, qi.VideoId, StringComparison.OrdinalIgnoreCase))
                qi.IsNowPlaying = true;
            _queueItems.Add(qi);
            _queueItemById[qi.VideoId] = qi;
        }
        _playlistWindow?.SetItemsSource(_queueItems);
        if (selectedIndex >= 0)
            _playlistWindow?.ScrollToIndex(selectedIndex);
    }

    private bool PlaylistDisplaysSameEntryOrder(IReadOnlyList<PlaylistEntry> entries)
    {
        if (_queueItems.Count != entries.Count)
            return false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(_queueItems[i].VideoId, entries[i].VideoId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void UpdateNowPlayingFlag(PlaylistEntry? now)
    {
        foreach (var qi in _queueItems)
            qi.IsNowPlaying = now is not null && string.Equals(qi.VideoId, now.VideoId, StringComparison.OrdinalIgnoreCase);
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
        if (NowPlayingStatusRun is not null)
            NowPlayingStatusRun.Text = $"[{status}] ";

        string title;
        if (_nowPlayingEntry is null)
        {
            title = "Not playing";
        }
        else
        {
            title = $"{_nowPlayingEntry.Title}{(string.IsNullOrWhiteSpace(_nowPlayingEntry.Channel) ? "" : $" \u2014 {_nowPlayingEntry.Channel}")}";
        }

        if (!string.IsNullOrWhiteSpace(extraDetail) && (status is "ERROR" or "AGE" or "UNAVAILABLE" or "PREMIUM" or "COOKIE" or "FETCHING"))
        {
            var shortMsg = extraDetail.Trim();
            if (shortMsg.Length > 80)
                shortMsg = shortMsg[..80] + "\u2026";
            title = $"{title} ({shortMsg})";
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
    }

    private void SelectAndScrollToNowPlaying(PlaylistEntry? entry)
    {
        if (entry is null)
            return;

        if (_originalEntries.Count == 0)
            return;

        var idx = _originalEntries
            .Select((e, i) => (e, i))
            .FirstOrDefault(x => string.Equals(x.e.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
            .i;

        if (idx < 0 || idx >= _originalEntries.Count)
            return;

        // Center the now playing item without affecting selection (avoid extra selection highlight).
        _playlistWindow?.CenterIndex(idx);
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

    private static void CenterListBoxItem(System.Windows.Controls.ListBox listBox, object? item, int attempt = 0)
    {
        if (item is null)
            return;

        // Defer until container is generated (virtualization).
        listBox.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                listBox.ApplyTemplate();
                var scrollViewer = FindListBoxScrollViewer(listBox);
                if (scrollViewer is null)
                    return;

                if (scrollViewer.ViewportHeight <= 0)
                    return;

                // First, ensure the item is realized and at least visible.
                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();

                var container = listBox.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container is null)
                {
                    if (attempt < 3)
                        CenterListBoxItem(listBox, item, attempt + 1);
                    return;
                }

                // Compute delta from viewport center, then adjust current offset.
                // This avoids coordinate-space confusion and prevents "jump to top".
                var p = container.TransformToAncestor(scrollViewer).Transform(new System.Windows.Point(0, 0));
                var itemMidInViewport = p.Y + (container.ActualHeight / 2.0);
                var delta = itemMidInViewport - (scrollViewer.ViewportHeight / 2.0);
                var target = scrollViewer.VerticalOffset + delta;

                var max = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
                if (target < 0) target = 0;
                if (target > max) target = max;

                scrollViewer.ScrollToVerticalOffset(target);

                // If virtualization/layout caused a mismatch, retry a couple times.
                if (attempt < 2 && Math.Abs(delta) > Math.Max(2, container.ActualHeight))
                    CenterListBoxItem(listBox, item, attempt + 1);
            }
            catch
            {
                // ignore UI scrolling failures
            }
        }), DispatcherPriority.ContextIdle);
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
                if (_queueItemById.TryGetValue(tag.VideoId, out var item))
                    item.IsPremium = true;
            }
            else if (string.Equals(tag.Category, "AgeRestricted", StringComparison.OrdinalIgnoreCase))
            {
                _ageRestrictedVideoIds.Add(tag.VideoId);
                if (_queueItemById.TryGetValue(tag.VideoId, out var item))
                    item.IsAgeRestricted = true;
            }
            else
            {
                _unavailableVideoIds.Add(tag.VideoId);
                if (_queueItemById.TryGetValue(tag.VideoId, out var item))
                    item.IsUnavailable = true;
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
            if (_queueItemById.TryGetValue(entry.VideoId, out var item))
                item.IsPremium = true;
            _nowPlayingStatus = "PREMIUM";
        }
        else if (isAge)
        {
            _ageRestrictedVideoIds.Add(entry.VideoId);
            if (_queueItemById.TryGetValue(entry.VideoId, out var item))
                item.IsAgeRestricted = true;
            _nowPlayingStatus = "AGE";
        }
        else
        {
            _unavailableVideoIds.Add(entry.VideoId);
            if (_queueItemById.TryGetValue(entry.VideoId, out var item))
                item.IsUnavailable = true;
            _nowPlayingStatus = "UNAVAILABLE";
        }

        _nowPlayingEntry = entry;
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
    }

    private void UpdateTimelineUi()
    {
        if (_isSeeking)
            return;

        var dur = _engine.CurrentDurationSeconds;
        var pos = _engine.CurrentPositionSeconds;

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

                return;
            }
        }
        catch { /* ignore */ }

        ElapsedTextBlock.Text = FormatTime(pos);

        if (dur is null or <= 0)
            return;

        var clamped = Math.Max(0, Math.Min(pos, dur.Value));

        _ignoreSeekBar = true;
        try
        {
            SeekSlider.Value = clamped;
        }
        finally
        {
            _ignoreSeekBar = false;
        }
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

        await _engine.SeekAsync(SeekSlider.Value);
    }

    private void SeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ignoreSeekBar)
            return;

        if (_isSeeking)
            ElapsedTextBlock.Text = FormatTime(SeekSlider.Value);
    }

    private const int MaxPersistedPlaylistFilterChars = 500;

    private static string? NormalizePersistedPlaylistFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var t = raw.Trim();
        if (t.Length > MaxPersistedPlaylistFilterChars)
            t = t.Substring(0, MaxPersistedPlaylistFilterChars);
        return t.Length == 0 ? null : t;
    }

    private void SaveSettingsSnapshot(double? overridePositionSeconds = null, bool? overrideWasPlaying = null)
    {
        var cur = SettingsStore.Load();
        var state = WindowState;
        var bounds = state == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        static double? FiniteOrNull(double v)
            => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

        var pState = _playlistWindow?.WindowState ?? WindowState.Normal;
        var pBounds = _playlistWindow is null
            ? new Rect(double.NaN, double.NaN, double.NaN, double.NaN)
            : (pState == WindowState.Normal ? new Rect(_playlistWindow.Left, _playlistWindow.Top, _playlistWindow.Width, _playlistWindow.Height) : _playlistWindow.RestoreBounds);

        var savePBounds = ChooseBestBounds(
            primary: pBounds,
            secondary: _lastPlaylistBounds,
            fallbackLeft: cur.PlaylistWindowLeft,
            fallbackTop: cur.PlaylistWindowTop,
            fallbackWidth: cur.PlaylistWindowWidth,
            fallbackHeight: cur.PlaylistWindowHeight
        );
        var savePState = _playlistWindow is not null ? pState : (_lastPlaylistWindowState ?? pState);

        var oState = _optionsWindow?.WindowState ?? WindowState.Normal;
        var oBounds = _optionsWindow is null
            ? new Rect(double.NaN, double.NaN, double.NaN, double.NaN)
            : (oState == WindowState.Normal ? new Rect(_optionsWindow.Left, _optionsWindow.Top, _optionsWindow.Width, _optionsWindow.Height) : _optionsWindow.RestoreBounds);
        var saveOBounds = ChooseBestBounds(
            primary: oBounds,
            secondary: _lastOptionsBounds,
            fallbackLeft: cur.OptionsWindowLeft,
            fallbackTop: cur.OptionsWindowTop,
            fallbackWidth: cur.OptionsWindowWidth,
            fallbackHeight: cur.OptionsWindowHeight
        );
        var saveOState = _optionsWindow is not null ? oState : (_lastOptionsWindowState ?? oState);

        var lastYtUrlMem = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(_lastYoutubeUrl);

        SettingsStore.Save(new AppSettings(
            YtDlpPath: _savedYtDlpPath,
            FfmpegPath: _savedFfmpegPath,
            // Keep last YouTube URL even if current playlist is local.
            LastPlaylistUrl: string.IsNullOrEmpty(lastYtUrlMem) ? null : lastYtUrlMem,
            LastPlaylistSourceType: _lastPlaylistSourceType.ToString(),
            LastLocalPlaylistPath: _lastPlaylistSourceType == PlaylistSourceType.YouTube ? null : (_lastLocalPlaylistPath ?? _playlistSourceText?.Trim()),
            VisualizerMode: _visualizerMode.ToString(),
            ShuffleEnabled: _shuffleEnabled,
            GlobalMediaKeysEnabled: _globalMediaKeysEnabled,
            RepeatMode: _repeatMode.ToString(),
            CurrentVideoId: _engine.GetCurrent()?.VideoId,
            PlayOrderVideoIds: _engine.PlayOrder.Select(e => e.VideoId).ToList(),
            CurrentPositionSeconds: overridePositionSeconds ?? _engine.CurrentPositionSeconds,
            WasPlaying: overrideWasPlaying ?? _engine.IsPlaying,
            CacheMaxMb: _cacheMaxMb,
            Volume: VolumeSlider?.Value,
            PlaylistAutoRefreshMinutes: _autoRefreshMinutes,
            IncludeSubfoldersOnFolderLoad: _includeSubfoldersOnFolderLoad,
            AlwaysOnTop: _alwaysOnTop,
            AlwaysOnTopPlaylistWindow: _alwaysOnTopPlaylistWindow,
            AlwaysOnTopOptionsWindow: _alwaysOnTopOptionsWindow,
            WindowLeft: FiniteOrNull(bounds.Left) ?? cur.WindowLeft,
            WindowTop: FiniteOrNull(bounds.Top) ?? cur.WindowTop,
            WindowWidth: FiniteOrNull(bounds.Width) ?? cur.WindowWidth,
            WindowHeight: FiniteOrNull(bounds.Height) ?? cur.WindowHeight,
            WindowState: state.ToString(),
            PlaylistWindowLeft: FiniteOrNull(savePBounds.Left) ?? cur.PlaylistWindowLeft,
            PlaylistWindowTop: FiniteOrNull(savePBounds.Top) ?? cur.PlaylistWindowTop,
            PlaylistWindowWidth: FiniteOrNull(savePBounds.Width) ?? cur.PlaylistWindowWidth,
            PlaylistWindowHeight: FiniteOrNull(savePBounds.Height) ?? cur.PlaylistWindowHeight,
            PlaylistWindowState: savePState.ToString(),
            PlaylistWindowOpen: _playlistWindow is not null,
            PlaylistWindowFilter: _playlistWindow is not null
                ? NormalizePersistedPlaylistFilter(_playlistWindow.GetPlaylistFilterText())
                : cur.PlaylistWindowFilter,
            OptionsWindowLeft: FiniteOrNull(saveOBounds.Left) ?? cur.OptionsWindowLeft,
            OptionsWindowTop: FiniteOrNull(saveOBounds.Top) ?? cur.OptionsWindowTop,
            OptionsWindowWidth: FiniteOrNull(saveOBounds.Width) ?? cur.OptionsWindowWidth,
            OptionsWindowHeight: FiniteOrNull(saveOBounds.Height) ?? cur.OptionsWindowHeight,
            OptionsWindowState: saveOState.ToString(),
            OptionsWindowOpen: _optionsWindow is not null,
            PlaylistWindowSnapped: _playlistSnapped,
            PlaylistWindowSnapEdge: _playlistSnapEdge.ToString(),
            PlaylistWindowDockYOffset: FiniteOrNull(_playlistDockYOffset) ?? 0,
            PlaylistWindowDockXOffset: FiniteOrNull(_playlistDockXOffset) ?? 0,
            PlaylistWindowBoundsUiScalePercent: _uiScalePercent,
            OptionsWindowSnapped: _optionsSnapped,
            OptionsWindowSnapEdge: _optionsSnapEdge.ToString(),
            OptionsWindowDockYOffset: FiniteOrNull(_optionsDockYOffset) ?? 0,
            OptionsWindowDockXOffset: FiniteOrNull(_optionsDockXOffset) ?? 0,
            OptionsWindowBottomAlignToPlaylist: false,
            OptionsWindowSelectedTab: _optionsSelectedTab,
            ThemeMode: _themeMode,
            BackgroundMode: _backgroundMode,
            CustomBackgroundImagePath: string.IsNullOrWhiteSpace(_customBackgroundImagePath) ? null : _customBackgroundImagePath.Trim(),
            BackgroundColorMode: _backgroundColorMode,
            CustomBackgroundColor: string.IsNullOrWhiteSpace(_customBackgroundColor) ? null : _customBackgroundColor.Trim(),
            BackgroundAlpha: _backgroundAlpha,
            BackgroundScrimPercent: _backgroundScrimPercent,
            BackgroundImageStretch: _backgroundImageStretch,
            AppTitleMode: _appTitleMode,
            CustomAppTitle: string.IsNullOrWhiteSpace(_customAppTitle) ? null : _customAppTitle.Trim(),
            AppIconVisibility: _appIconVisibility,
            SearchDefaultCount: _searchDefaultCount,
            SearchMinLengthSeconds: _searchMinLengthSeconds,
            ReadMetadataOnLoad: _readMetadataOnLoad,
            UiScalePercent: _uiScalePercent,
            WindowBorderMode: _windowBorderMode,
            WindowBorderCustomPx: _windowBorderCustomPx,
            NodeJsPath: _savedNodePath,
            YtdlpEjsComponentSource: _ytdlpEjsComponentSource,
            YoutubeCookiesFromBrowserEnabled: _youtubeCookiesFromBrowserEnabled,
            YoutubeCookiesFromBrowser: string.IsNullOrWhiteSpace(_youtubeCookiesFromBrowser) ? null : _youtubeCookiesFromBrowser.Trim(),
            AudioQuality: _audioQuality,
            AudioOutputDevice: string.IsNullOrWhiteSpace(_audioOutputDevice) ? null : _audioOutputDevice,
            AppLogLevel: _appLogLevel,
            AppLogMaxMb: _appLogMaxMb,
            MainWindowCompact: _mainWindowCompact,
            CompactModeHidesAuxWindows: _compactModeHidesAuxWindows,
            KeepIncompletePlaylistOnCancel: _keepIncompletePlaylistOnCancel,
            LastSavedByAppVersion: AppVersion.Current
        ));
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
        var f = ToolPathResolver.Resolve(_savedFfmpegPath, "ffmpeg");
        _ffmpegPath = f.EffectiveFileName;
        try { _engine.SetFfmpegPath(_ffmpegPath); } catch { /* ignore */ }
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
            System.Windows.Media.Brush brush;
            System.Windows.Media.ImageBrush? raw = null;

            if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
            {
                // Flat background: follow current theme palette.
                brush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["App.Theme.Surface"];
                // If the user sets Background opacity to 0, App.Theme.Surface can become fully transparent.
                // With BackgroundMode=None that reveals the default window black, which can make Light theme
                // look like "black on black". Force an opaque fill while still honoring the theme RGB.
                if (_backgroundAlpha <= 0 && brush is System.Windows.Media.SolidColorBrush sb)
                {
                    var c = System.Windows.Media.Color.FromRgb(sb.Color.R, sb.Color.G, sb.Color.B);
                    var b = new System.Windows.Media.SolidColorBrush(c);
                    try { b.Freeze(); } catch { /* ignore */ }
                    brush = b;
                }
            }
            else if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(_customBackgroundImagePath) &&
                     File.Exists(_customBackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(_customBackgroundImagePath, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();

                var img = new System.Windows.Media.ImageBrush(bi);
                ApplyBackgroundImageStretchToBrush(img, bi, _backgroundImageStretch);
                try { img.Freeze(); } catch { /* ignore */ }
                raw = img;
                brush = img;
            }
            else
            {
                // Default pack image — clone so Stretch/Tile can follow settings without mutating the frozen resource.
                if (System.Windows.Application.Current.Resources["App.Brush.DefaultWindowBgImage"] is System.Windows.Media.ImageBrush defBrush
                    && defBrush.ImageSource is System.Windows.Media.Imaging.BitmapSource srcDef)
                {
                    var img = new System.Windows.Media.ImageBrush(srcDef);
                    ApplyBackgroundImageStretchToBrush(img, srcDef, _backgroundImageStretch);
                    try { img.Freeze(); } catch { /* ignore */ }
                    raw = img;
                    brush = img;
                }
                else
                {
                    raw = System.Windows.Application.Current.Resources["App.Brush.DefaultWindowBgImage"] as System.Windows.Media.ImageBrush;
                    brush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["App.Brush.DefaultWindowBgImage"];
                }
            }

            // Keep a raw image brush around for theme color sampling.
            if (raw is not null)
                System.Windows.Application.Current.Resources["App.Brush.WindowBgImageRaw"] = raw;

            System.Windows.Application.Current.Resources["App.Brush.WindowBgImage"] =
                ApplyScrimIfNeeded(brush, scrimPercent: _backgroundScrimPercent);
        }
        catch
        {
            // ignore
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
            double contentW = 0, contentH = 0;
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
                // Use DIP-sized content bounds (bitmap aspect). Rect 0,0,1,1 squashes the image in drawing space and
                // changes apparent zoom/crop vs plain ImageBrush when scrim toggles — looks like UI "scaling" changed.
                (contentW, contentH) = GetBackgroundImageTileSizeDips(src);
                var contentRect = new System.Windows.Rect(0, 0, contentW, contentH);
                drawing.Children.Add(new System.Windows.Media.ImageDrawing(ib.ImageSource, contentRect));
                drawing.Children.Add(new System.Windows.Media.GeometryDrawing(scrim, null, new System.Windows.Media.RectangleGeometry(contentRect)));
            }

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
                db = new System.Windows.Media.DrawingBrush(drawing)
                {
                    Stretch = ib.Stretch,
                    AlignmentX = ib.AlignmentX,
                    AlignmentY = ib.AlignmentY,
                    TileMode = ib.TileMode,
                    Viewbox = new System.Windows.Rect(0, 0, contentW, contentH),
                    ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                    Viewport = ib.Viewport,
                    ViewportUnits = ib.ViewportUnits,
                };
            }

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
            if (_playlistSnapped) SyncPlaylistWindowToMain();
            if (_optionsSnapped) SyncOptionsWindowToMain();
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

    private static void TryRaiseWindowNoActivate(Window? w)
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
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
            if (string.IsNullOrWhiteSpace(song))
                return null;
            return string.IsNullOrWhiteSpace(artist) ? song : $"{song} - {artist}";
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

        var playPauseItem = new System.Windows.Controls.MenuItem { Header = "Play/Pause" };
        playPauseItem.Click += (_, _) => { try { PlayPauseButton_OnClick(this, new RoutedEventArgs()); } catch { /* ignore */ } };
        cm.Items.Add(playPauseItem);

        var nextItem = new System.Windows.Controls.MenuItem { Header = "Next" };
        nextItem.Click += (_, _) => { try { NextButton_OnClick(this, new RoutedEventArgs()); } catch { /* ignore */ } };
        cm.Items.Add(nextItem);

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

        cm.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => { try { Close(); } catch { /* ignore */ } };
        cm.Items.Add(exitItem);

        _hardcodetTrayIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
        {
            Icon = _trayIcon,
            ToolTipText = "LyllyPlayer",
            ContextMenu = cm,
            Visibility = Visibility.Collapsed,
        };
        _hardcodetTrayIcon.TrayMouseDoubleClick += (_, _) =>
        {
            try { ShowMainWindowFromTray(); } catch { /* ignore */ }
        };
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
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIcon.Handle,
            szTip = "LyllyPlayer",
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
            uFlags = NIF_STATE | NIF_TIP,
            dwStateMask = NIS_HIDDEN,
            dwState = hidden ? NIS_HIDDEN : 0,
            szTip = "LyllyPlayer",
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
                        try { _hardcodetTrayIcon.ToolTipText = "LyllyPlayer"; } catch { /* ignore */ }
                        _hardcodetTrayIcon.Visibility = showTray ? Visibility.Visible : Visibility.Collapsed;
                    }
                    // On affected systems, Explorer only removes the tray gap after a user interaction like
                    // double-clicking the tray icon (which foregrounds the app). Best-effort: do the same
                    // "foreground nudge" after a tray visibility toggle.
                    if (showTray)
                    {
                        try { QueueExplorerTrayLayoutNudge(); } catch { /* ignore */ }
                        try { _ = TrayRefresher.RefreshTrayLayoutBestEffortAsync(); } catch { /* ignore */ }
                    }
                    _lastAppliedShowTray = showTray;
                }
            }
            catch { /* ignore */ }

            // Avoid constantly "jolting" Explorer with style rebuilds when the mode hasn't changed.
            // Rebuilding the taskbar button can cause a temporary right-edge gap in the notification area
            // on some Win10 configurations until the window is activated again.
            var taskbarModeChanged = _lastAppliedShowInTaskbar != showTaskbar;
            if (_lastAppliedShowInTaskbar != showTaskbar)
            {
                ApplyTaskbarVisibilityNative(showTaskbar);
                _lastAppliedShowInTaskbar = showTaskbar;
            }
            // ShowInTaskbar is set inside ApplyTaskbarVisibilityNative (which also uses ITaskbarList).

            // Empirically, some Win10 Explorer builds leave a right-edge tray gap after taskbar button rebuilds
            // until the app is activated (e.g. double-clicking the tray icon). Nudge activation/focus in a
            // best-effort, no-op way by briefly foregrounding the main window and restoring the previous
            // foreground window.
            if (taskbarModeChanged)
            {
                try { QueueExplorerTrayLayoutNudge(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private void QueueExplorerTrayLayoutNudge()
    {
        if (_isShuttingDown)
            return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;
                var prev = GetForegroundWindow();
                // Bring us to foreground (like the user double-clicking the tray icon), then restore.
                SetForegroundWindow(hwnd);
                if (prev != IntPtr.Zero && prev != hwnd)
                    SetForegroundWindow(prev);
            }
            catch { /* ignore */ }
        }), DispatcherPriority.Background);
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

            // Hide/show the taskbar button via ITaskbarList (shell hint).
            try
            {
                _taskbarList ??= (ITaskbarList)new CTaskbarList();
                _taskbarList.HrInit();
                if (showInTaskbar)
                {
                    _taskbarList.AddTab(hwnd);
                }
                else
                {
                    _taskbarList.DeleteTab(hwnd);
                }
            }
            catch { /* ignore */ }

            // Keep WPF's property in sync (best-effort; it can be flaky with custom chrome).
            try { ShowInTaskbar = showInTaskbar; } catch { /* ignore */ }
            try
            {
                var tray = _trayMessageHwnd != IntPtr.Zero ? _trayMessageHwnd : hwnd;
                LogShellState(showInTaskbar ? "TaskbarNativeShow" : "TaskbarNativeHide", hwnd, tray);
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
            _backgroundMode = string.IsNullOrWhiteSpace(t.BackgroundMode) ? "Default" : t.BackgroundMode.Trim();
            _customBackgroundImagePath = t.CustomBackgroundImagePath ?? "";
            _backgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(t.BackgroundColorMode);
            _customBackgroundColor = t.CustomBackgroundColor ?? "";
            _backgroundAlpha = t.BackgroundAlpha is >= 0 and <= 255 ? t.BackgroundAlpha.Value : _backgroundAlpha;
            _backgroundScrimPercent = t.BackgroundScrimPercent is >= 0 and <= 80 ? t.BackgroundScrimPercent.Value : _backgroundScrimPercent;
            _backgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(t.BackgroundImageStretch ?? _backgroundImageStretch);
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
    private const byte ThemeChromeAlphaFloor = 48;

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

    private static void ApplyDefaultThemeBrushes(byte alpha, bool darkTheme)
    {
        try
        {
            var a = MapThemeChromeAlpha(alpha);
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
            SetBrush("App.Theme.Border", System.Windows.Media.Color.FromArgb(a, border.R, border.G, border.B));
            SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(a, border.R, border.G, border.B));
            var winBorderInactive = Blend(
                System.Windows.Media.Color.FromRgb(border.R, border.G, border.B),
                System.Windows.Media.Color.FromRgb(surface.R, surface.G, surface.B),
                0.55);
            SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(a, winBorderInactive.R, winBorderInactive.G, winBorderInactive.B));
            SetBrush("App.Theme.Foreground", fg);
            SetBrush("App.Theme.ForegroundSubtle", System.Windows.Media.Color.FromRgb(subtle.R, subtle.G, subtle.B));
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
            if (Math.Abs(lumA - lumI) < 0.18)
            {
                titleInactiveRgb = AdjustLuminance(titleInactiveRgb, darkTheme ? -0.22 : -0.28);
                lumI = RelativeLuminance(titleInactiveRgb);
                if (Math.Abs(lumA - lumI) < 0.18)
                    titleInactiveRgb = AdjustLuminance(titleInactiveRgb, darkTheme ? -0.14 : -0.18);
            }

            SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, titleInactiveRgb.R, titleInactiveRgb.G, titleInactiveRgb.B));
            var titleTextActive = PickForegroundForBackground(surfaceRaisedRgbOnly, minRatio: 7.0);
            var titleTextInactive = PickForegroundForBackground(titleInactiveRgb, minRatio: 7.0);
            var dimCandidate = Blend(titleTextInactive, surfaceRgbOnly, 0.25);
            if (ContrastRatio(titleInactiveRgb, dimCandidate) >= 4.5)
                titleTextInactive = dimCandidate;
            SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromRgb(titleTextActive.R, titleTextActive.G, titleTextActive.B));
            SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromRgb(titleTextInactive.R, titleTextInactive.G, titleTextInactive.B));
        }
        catch { /* ignore */ }
    }

    private static void ApplyWindowsThemeBrushes(byte alpha)
    {
        try
        {
            var a = MapThemeChromeAlpha(alpha);
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
            SetBrush("App.Theme.Border", System.Windows.Media.Color.FromArgb(a, borderRgb.R, borderRgb.G, borderRgb.B));
            // Window frame: WPF SystemColors.ActiveBorder/InactiveBorder are legacy and don't track Win10/11 DWM chrome.
            // Derive 1px borders from the same caption palette we use for title bars + ControlDark.
            var surfaceForTitleEarly = System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B);
            var (capActiveEarly, capInactiveEarly, capTxtAEarly, capTxtIEarly) = GetWindowsTitleBarPalette(surfaceForTitleEarly);
            var borderOnlyEarly = System.Windows.Media.Color.FromRgb(borderRgb.R, borderRgb.G, borderRgb.B);
            var winBorderAct = Blend(borderOnlyEarly, capActiveEarly, 0.26);
            var winBorderInact = Blend(borderOnlyEarly, capInactiveEarly, 0.38);
            SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(a, winBorderAct.R, winBorderAct.G, winBorderAct.B));
            SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(a, winBorderInact.R, winBorderInact.G, winBorderInact.B));
            SetBrush("App.Theme.Foreground", System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B));
            SetBrush("App.Theme.ForegroundSubtle", System.Windows.Media.Color.FromRgb(subtleRgb.R, subtleRgb.G, subtleRgb.B));
            var surfaceRgbOnlyWin = System.Windows.Media.Color.FromRgb(surfaceRgb.R, surfaceRgb.G, surfaceRgb.B);
            var darkWinChrome = RelativeLuminance(surfaceRgbOnlyWin) < 0.42;
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
            SetBrush("App.Theme.TitleBarActive", System.Windows.Media.Color.FromArgb(a, capActiveEarly.R, capActiveEarly.G, capActiveEarly.B));
            SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, capInactiveEarly.R, capInactiveEarly.G, capInactiveEarly.B));
            SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromRgb(capTxtAEarly.R, capTxtAEarly.G, capTxtAEarly.B));
            SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromRgb(capTxtIEarly.R, capTxtIEarly.G, capTxtIEarly.B));

            SyncSystemChromeBrushesFromSelectionTheme(
                System.Windows.Media.Color.FromRgb(selSoft.R, selSoft.G, selSoft.B),
                System.Windows.Media.Color.FromRgb(selTextFinal.R, selTextFinal.G, selTextFinal.B),
                System.Windows.Media.Color.FromRgb(hotTrackRgb.R, hotTrackRgb.G, hotTrackRgb.B),
                System.Windows.Media.Color.FromRgb(fgRgb.R, fgRgb.G, fgRgb.B));
        }
        catch { /* ignore */ }
    }

    private static void ApplyThemeBrushesFromBaseColor(System.Windows.Media.Color baseColor, byte alpha, bool preferDarkTheme)
    {
        var a = MapThemeChromeAlpha(alpha);
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
        SetBrush("App.Theme.WindowBorderActive", System.Windows.Media.Color.FromArgb(a, themeBorder.R, themeBorder.G, themeBorder.B));
        var themeBorderRgbOnly = System.Windows.Media.Color.FromRgb(themeBorder.R, themeBorder.G, themeBorder.B);
        var baseSurfaceRgb = System.Windows.Media.Color.FromRgb(bc.R, bc.G, bc.B);
        var themeBorderInactiveRgb = Blend(themeBorderRgbOnly, baseSurfaceRgb, 0.55);
        SetBrush("App.Theme.WindowBorderInactive", System.Windows.Media.Color.FromArgb(a, themeBorderInactiveRgb.R, themeBorderInactiveRgb.G, themeBorderInactiveRgb.B));
        SetBrush("App.Theme.Foreground", fg);
        SetBrush("App.Theme.ForegroundSubtle", fgSubtle);
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
        if (Math.Abs(lumA - lumI) < 0.18)
        {
            // Push harder in a direction that maintains the chosen polarity.
            titleInactiveRgb = AdjustLuminance(titleInactiveRgb, preferDarkTheme ? 0.28 : -0.28);
            lumI = RelativeLuminance(titleInactiveRgb);
            if (Math.Abs(lumA - lumI) < 0.18)
                titleInactiveRgb = AdjustLuminance(titleInactiveRgb, preferDarkTheme ? 0.18 : -0.18);
        }

        SetBrush("App.Theme.TitleBarActive", titleActive);
        SetBrush("App.Theme.TitleBarInactive", System.Windows.Media.Color.FromArgb(a, titleInactiveRgb.R, titleInactiveRgb.G, titleInactiveRgb.B));
        var titleTextActive = PickForegroundForBackground(titleActiveRgb, minRatio: 7.0);
        var titleTextInactive = PickForegroundForBackground(titleInactiveRgb, minRatio: 7.0);
        // Dim inactive slightly but keep contrast; fall back to the high-contrast pick if dimming breaks it.
        var dimCandidate = Blend(titleTextInactive, surfaceRgb, 0.25);
        if (ContrastRatio(titleInactiveRgb, dimCandidate) >= 4.5)
            titleTextInactive = dimCandidate;
        SetBrush("App.Theme.TitleBarTextActive", System.Windows.Media.Color.FromRgb(titleTextActive.R, titleTextActive.G, titleTextActive.B));
        SetBrush("App.Theme.TitleBarTextInactive", System.Windows.Media.Color.FromRgb(titleTextInactive.R, titleTextInactive.G, titleTextInactive.B));

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

    private static Rect ChooseBestBounds(
        Rect primary,
        Rect? secondary,
        double? fallbackLeft,
        double? fallbackTop,
        double? fallbackWidth,
        double? fallbackHeight)
    {
        static bool IsFinite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

        static bool LooksValid(Rect r)
            => IsFinite(r.Left) && IsFinite(r.Top) && IsFinite(r.Width) && IsFinite(r.Height) && r.Width > 50 && r.Height > 50;

        if (LooksValid(primary))
            return primary;
        if (secondary is Rect s && LooksValid(s))
            return s;
        if (fallbackLeft is double l && fallbackTop is double t && fallbackWidth is double w && fallbackHeight is double h &&
            LooksValid(new Rect(l, t, w, h)))
            return new Rect(l, t, w, h);
        return primary;
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

    private static void CaptureWindowBounds(Window w, out Rect? bounds, out WindowState? state)
    {
        bounds = null;
        state = null;
        try
        {
            var ws = w.WindowState;
            var b = ws == WindowState.Normal
                ? new Rect(w.Left, w.Top, w.Width, w.Height)
                : w.RestoreBounds;
            bounds = b;
            state = ws;
        }
        catch
        {
            // ignore
        }
    }

    private void RequestPersistSnapshot()
    {
        // Debounced to avoid excessive disk writes while dragging/resizing.
        try
        {
            _persistTimer.Stop();
            _persistTimer.Start();
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

            if (settings.OptionsWindowLeft is double l && settings.OptionsWindowTop is double t)
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

            if (settings.PlaylistWindowLeft is double l && settings.PlaylistWindowTop is double t)
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

    private async Task TryResumePlaybackFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        // We need a valid current track selected.
        if (_engine.GetCurrent() is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var pos = _startupSettings.CurrentPositionSeconds ?? 0;
        if (pos < 0) pos = 0;

        var cur = _engine.GetCurrent();
        if (cur is null)
            return;

        // If we were NOT playing when the app closed, keep it paused/stopped but restore the timeline.
        if (!(_startupSettings.WasPlaying ?? false))
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
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var ok = pos > 1
                ? await _engine.SeekAsync(pos)
                : await _engine.PlayCurrentAsync();

            if (!ok)
            {
                // If resume fails, explicitly do not autoplay further.
                _engine.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _engine.Stop();
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
            Opacity = 0.58,
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
