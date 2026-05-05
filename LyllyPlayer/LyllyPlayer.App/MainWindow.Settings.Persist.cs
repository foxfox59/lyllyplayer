using System.Linq;
using System.Windows;
using LyllyPlayer.Models;
using LyllyPlayer.ShellServices;
using LyllyPlayer.Settings;
using LyllyPlayer.Utils;

namespace LyllyPlayer;

public partial class MainWindow
{
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

    private void SaveSettingsSnapshot(double? overridePositionSeconds = null, bool? overrideWasPlaying = null)
    {
        var cur = _settingsService.LoadLatest();
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

        var lState = _lyricsWindow?.WindowState ?? WindowState.Normal;
        var lBounds = _lyricsWindow is null
            ? new Rect(double.NaN, double.NaN, double.NaN, double.NaN)
            : (lState == WindowState.Normal ? new Rect(_lyricsWindow.Left, _lyricsWindow.Top, _lyricsWindow.Width, _lyricsWindow.Height) : _lyricsWindow.RestoreBounds);
        var saveLBounds = ChooseBestBounds(
            primary: lBounds,
            secondary: _lastLyricsBounds,
            fallbackLeft: cur.LyricsWindowLeft,
            fallbackTop: cur.LyricsWindowTop,
            fallbackWidth: cur.LyricsWindowWidth,
            fallbackHeight: cur.LyricsWindowHeight
        );
        var saveLState = _lyricsWindow is not null ? lState : (_lastLyricsWindowState ?? lState);

        var lastYtUrlMem = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(_lastYoutubeUrl);

        // IMPORTANT:
        // When an aux window is hidden, we only have its last absolute bounds. If Main moved since then,
        // adjacency inference will (correctly) say "not snapped", but that's NOT what we want to persist.
        //
        // Rule:
        // - If the aux window is visible, infer snap from current geometry (ground truth).
        // - If the aux window is hidden, persist the last-known snap relation from settings (edge + offsets).
        var mainOuterForPersist = bounds;

        var playlistVisible = _playlistWindow is not null && _playlistWindow.IsVisible;
        var optionsVisible = _optionsWindow is not null && _optionsWindow.IsVisible;
        var lyricsVisible = _lyricsWindow is not null && _lyricsWindow.IsVisible;

        var inferredPlaylist = AuxWindowSnapHelper.InferPersistedSnap(mainOuterForPersist, savePBounds, SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx, AuxSnapWindowKind.Playlist);
        var inferredOptions = AuxWindowSnapHelper.InferPersistedSnap(mainOuterForPersist, saveOBounds, SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx, AuxSnapWindowKind.Options);
        var inferredLyrics = AuxWindowSnapHelper.InferPersistedSnap(mainOuterForPersist, saveLBounds, SnapGapPx, SnapPersistAdjacencyPx, SnapPersistMinOverlapPx, AuxSnapWindowKind.Lyrics);

        var persistPlaylistSnap = playlistVisible ? inferredPlaylist : new AuxSnapPersistResult
        {
            Snapped = cur.PlaylistWindowSnapped ?? false,
            Edge = string.IsNullOrWhiteSpace(cur.PlaylistWindowSnapEdge) ? "None" : cur.PlaylistWindowSnapEdge!,
            DockXOffset = cur.PlaylistWindowDockXOffset ?? 0,
            DockYOffset = cur.PlaylistWindowDockYOffset ?? 0,
        };
        var persistOptionsSnap = optionsVisible ? inferredOptions : new AuxSnapPersistResult
        {
            Snapped = cur.OptionsWindowSnapped ?? false,
            Edge = string.IsNullOrWhiteSpace(cur.OptionsWindowSnapEdge) ? "None" : cur.OptionsWindowSnapEdge!,
            DockXOffset = cur.OptionsWindowDockXOffset ?? 0,
            DockYOffset = cur.OptionsWindowDockYOffset ?? 0,
        };
        var persistLyricsSnap = lyricsVisible ? inferredLyrics : new AuxSnapPersistResult
        {
            Snapped = cur.LyricsWindowSnapped ?? false,
            Edge = string.IsNullOrWhiteSpace(cur.LyricsWindowSnapEdge) ? "None" : cur.LyricsWindowSnapEdge!,
            DockXOffset = cur.LyricsWindowDockXOffset ?? 0,
            DockYOffset = cur.LyricsWindowDockYOffset ?? 0,
        };

        var playlistSnappedNow = persistPlaylistSnap.Snapped;
        var optionsSnappedNow = persistOptionsSnap.Snapped;
        var lyricsSnappedNow = persistLyricsSnap.Snapped;

        // Snap persistence: keep only the offset that matters for the snapped edge.
        var playlistDockX = playlistSnappedNow ? (FiniteOrNull(persistPlaylistSnap.DockXOffset) ?? 0) : 0;
        var playlistDockY = playlistSnappedNow ? (FiniteOrNull(persistPlaylistSnap.DockYOffset) ?? 0) : 0;

        var optionsDockX = optionsSnappedNow ? (FiniteOrNull(persistOptionsSnap.DockXOffset) ?? 0) : 0;
        var optionsDockY = optionsSnappedNow ? (FiniteOrNull(persistOptionsSnap.DockYOffset) ?? 0) : 0;

        var lyricsDockX = lyricsSnappedNow ? (FiniteOrNull(persistLyricsSnap.DockXOffset) ?? 0) : 0;
        var lyricsDockY = lyricsSnappedNow ? (FiniteOrNull(persistLyricsSnap.DockYOffset) ?? 0) : 0;

        _settingsService.Save(new AppSettings(
            YtDlpPath: _savedYtDlpPath,
            InternalYtDlpUpdateCheckEnabled: _internalYtDlpUpdateCheckEnabled,
            FfmpegPath: _savedFfmpegPath,
            LastPlaylistUrl: string.IsNullOrEmpty(lastYtUrlMem) ? null : lastYtUrlMem,
            LastPlaylistSourceType: _lastPlaylistSourceType.ToString(),
            LastLocalPlaylistPath: _lastPlaylistSourceType == PlaylistSourceType.YouTube ? null : (_lastLocalPlaylistPath ?? _playlistSourceText?.Trim()),
            VisualizerMode: _visualizerMode.ToString(),
            ShuffleEnabled: _shuffleEnabled,
            GlobalMediaKeysEnabled: _globalMediaKeysEnabled,
            RepeatMode: _repeatMode.ToString(),
            CurrentVideoId: _engine.GetCurrent()?.VideoId,
            PlayOrderVideoIds: _engine.PlayOrder.Select(e => e.VideoId).ToList(),
            QueuedVideoIds: _queuedNext.Select(e => e.Entry.VideoId).ToList(),
            CurrentPositionSeconds: overridePositionSeconds ?? _engine.CurrentPositionSeconds,
            CurrentDurationSeconds: _engine.CurrentDurationSeconds ?? _nowPlayingEntry?.DurationSeconds,
            WasPlaying: overrideWasPlaying ?? _engine.IsPlaying,
            CacheMaxMb: _cacheMaxMb,
            Volume: VolumeSlider?.Value,
            PlaylistAutoRefreshMinutes: _autoRefreshMinutes,
            IncludeSubfoldersOnFolderLoad: _includeSubfoldersOnFolderLoad,
            AlwaysOnTop: _alwaysOnTop,
            AlwaysOnTopPlaylistWindow: _alwaysOnTopPlaylistWindow,
            AlwaysOnTopOptionsWindow: _alwaysOnTopOptionsWindow,
            AlwaysOnTopLyricsWindow: _alwaysOnTopLyricsWindow,
            WindowLeft: FiniteOrNull(bounds.Left) ?? cur.WindowLeft,
            WindowTop: FiniteOrNull(bounds.Top) ?? cur.WindowTop,
            WindowWidth: FiniteOrNull(bounds.Width) ?? cur.WindowWidth,
            WindowHeight: FiniteOrNull(bounds.Height) ?? cur.WindowHeight,
            WindowState: state.ToString(),
            // When snapped, persist edge + offset (relative) and avoid rewriting absolute screen coordinates.
            // This prevents "closed snapped aux" from drifting to stale absolute Left/Top after main moves.
            PlaylistWindowLeft: playlistSnappedNow ? null : (FiniteOrNull(savePBounds.Left) ?? cur.PlaylistWindowLeft),
            PlaylistWindowTop: playlistSnappedNow ? null : (FiniteOrNull(savePBounds.Top) ?? cur.PlaylistWindowTop),
            PlaylistWindowWidth: FiniteOrNull(savePBounds.Width) ?? cur.PlaylistWindowWidth,
            PlaylistWindowHeight: FiniteOrNull(savePBounds.Height) ?? cur.PlaylistWindowHeight,
            PlaylistWindowState: savePState.ToString(),
            PlaylistWindowOpen: (_playlistWindow is not null && _playlistWindow.IsVisible) || (_mainWindowCompact && _compactModeHidesAuxWindows && _playlistWindowWasOpenBeforeCompact),
            PlaylistWindowFilter: _playlistWindow is not null && _playlistWindow.IsVisible
                ? NormalizePersistedPlaylistFilter(_playlistWindow.GetPlaylistFilterText())
                : cur.PlaylistWindowFilter,
            PlaylistWindowSortMode: _playlistWindow is not null && _playlistWindow.IsVisible ? _playlistWindow.GetSortSpec().Mode.ToString() : cur.PlaylistWindowSortMode,
            PlaylistWindowSortDirection: _playlistWindow is not null && _playlistWindow.IsVisible ? _playlistWindow.GetSortSpec().Direction.ToString() : cur.PlaylistWindowSortDirection,
            OptionsWindowLeft: optionsSnappedNow ? null : (FiniteOrNull(saveOBounds.Left) ?? cur.OptionsWindowLeft),
            OptionsWindowTop: optionsSnappedNow ? null : (FiniteOrNull(saveOBounds.Top) ?? cur.OptionsWindowTop),
            OptionsWindowWidth: FiniteOrNull(saveOBounds.Width) ?? cur.OptionsWindowWidth,
            OptionsWindowHeight: FiniteOrNull(saveOBounds.Height) ?? cur.OptionsWindowHeight,
            OptionsWindowState: saveOState.ToString(),
            OptionsWindowOpen: (_optionsWindow is not null && _optionsWindow.IsVisible) || (_mainWindowCompact && _compactModeHidesAuxWindows && _optionsWindowWasOpenBeforeCompact),
            PlaylistWindowSnapped: playlistSnappedNow,
            PlaylistWindowSnapEdge: persistPlaylistSnap.Edge,
            PlaylistWindowDockYOffset: playlistDockY,
            PlaylistWindowDockXOffset: playlistDockX,
            PlaylistWindowBoundsUiScalePercent: _uiScalePercent,
            OptionsWindowSnapped: optionsSnappedNow,
            OptionsWindowSnapEdge: persistOptionsSnap.Edge,
            OptionsWindowDockYOffset: optionsDockY,
            OptionsWindowDockXOffset: optionsDockX,
            OptionsWindowBottomAlignToPlaylist: false,
            OptionsWindowSelectedTab: _optionsSelectedTab,
            LyricsWindowSnapped: lyricsSnappedNow,
            LyricsWindowSnapEdge: persistLyricsSnap.Edge,
            LyricsWindowDockYOffset: lyricsDockY,
            LyricsWindowDockXOffset: lyricsDockX,
            LyricsWindowLeft: lyricsSnappedNow ? null : (FiniteOrNull(saveLBounds.Left) ?? cur.LyricsWindowLeft),
            LyricsWindowTop: lyricsSnappedNow ? null : (FiniteOrNull(saveLBounds.Top) ?? cur.LyricsWindowTop),
            LyricsWindowWidth: FiniteOrNull(saveLBounds.Width) ?? cur.LyricsWindowWidth,
            LyricsWindowHeight: FiniteOrNull(saveLBounds.Height) ?? cur.LyricsWindowHeight,
            LyricsWindowState: saveLState.ToString(),
            LyricsWindowOpen: (_lyricsWindow is not null && _lyricsWindow.IsVisible) || (_mainWindowCompact && _compactModeHidesAuxWindows && _lyricsWindowWasOpenBeforeCompact),
            ThemeMode: _themeMode,
            BackgroundMode: _backgroundMode,
            CustomBackgroundImagePath: string.IsNullOrWhiteSpace(_customBackgroundImagePath) ? null : _customBackgroundImagePath.Trim(),
            BackgroundColorMode: _backgroundColorMode,
            CustomBackgroundColor: string.IsNullOrWhiteSpace(_customBackgroundColor) ? null : _customBackgroundColor.Trim(),
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
            CustomAppTitle: string.IsNullOrWhiteSpace(_customAppTitle) ? null : _customAppTitle.Trim(),
            AppIconVisibility: _appIconVisibility,
            LyricsEnabled: _lyricsEnabled,
            LyricsLocalFilesEnabled: _lyricsLocalFilesEnabled,
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
            YoutubeImportAppend: _youtubeImportAppend,
            ExportM3uIncludeYoutube: _exportM3uIncludeYoutube,
            ExportM3uPreferRelativePaths: _exportM3uPreferRelativePaths,
            ExportM3uIncludeLyllyMetadata: _exportM3uIncludeLyllyMetadata,
            LocalImportAppend: _localImportAppend,
            LocalImportRemoveDuplicates: _localImportRemoveDuplicates,
            PlaylistDragDropAppend: _playlistDragDropAppend,
            PlaylistDragDropRemoveDuplicates: _playlistDragDropRemoveDuplicates,
            AudioQuality: _audioQuality,
            AudioOutputDevice: string.IsNullOrWhiteSpace(_audioOutputDevice) ? null : _audioOutputDevice,
            AudioNormalize: _audioNormalizeEnabled,
            AppLogLevel: _appLogLevel,
            AppLogMaxMb: _appLogMaxMb,
            MainWindowCompact: _mainWindowCompact,
            CompactModeLayout: _compactModeLayout,
            CompactModeHidesAuxWindows: _compactModeHidesAuxWindows,
            KeepIncompletePlaylistOnCancel: _keepIncompletePlaylistOnCancel,
            LameEncoderPath: string.IsNullOrWhiteSpace(_lameEncoderPath) ? null : _lameEncoderPath.Trim(),
            Mp3ExportEncodingMode: _mp3ExportEncodingMode,
            Mp3ExportCbrQualityIndex: _mp3ExportCbrQualityIndex,
            Mp3ExportVbrQualityIndex: _mp3ExportVbrQualityIndex,
            Mp3ExportReplacePlaylistEntryAfterExport: _mp3ExportReplacePlaylistEntryAfterExport,
            LastSavedByAppVersion: AppVersion.Current
        ));
    }

    private void RequestPersistSnapshot()
    {
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
}
