namespace LyllyPlayer.Settings;

public sealed record AppSettings(
    string? YtDlpPath,
    bool? InternalYtDlpUpdateCheckEnabled,
    string? FfmpegPath,
    string? LastPlaylistUrl,
    string? LastPlaylistSourceType,
    string? LastLocalPlaylistPath,
    string? VisualizerMode,
    bool? ShuffleEnabled,
    bool? GlobalMediaKeysEnabled,
    string? RepeatMode,
    string? CurrentVideoId,
    List<string>? PlayOrderVideoIds,
    /// <summary>Queue (play next) instances, in order; duplicates allowed. Not exported with saved playlist files.</summary>
    List<string>? QueuedVideoIds,
    double? CurrentPositionSeconds,
    /// <summary>Best-effort duration of the current track at save time (used to restore seek UI promptly).</summary>
    int? CurrentDurationSeconds,
    bool? WasPlaying,
    int? CacheMaxMb,
    double? Volume,
    int? PlaylistAutoRefreshMinutes,
    bool? IncludeSubfoldersOnFolderLoad,
    bool? AlwaysOnTop,
    bool? AlwaysOnTopPlaylistWindow,
    bool? AlwaysOnTopOptionsWindow,
    bool? AlwaysOnTopLyricsWindow,
    double? WindowLeft,
    double? WindowTop,
    double? WindowWidth,
    double? WindowHeight,
    string? WindowState,
    double? PlaylistWindowLeft,
    double? PlaylistWindowTop,
    double? PlaylistWindowWidth,
    double? PlaylistWindowHeight,
    string? PlaylistWindowState,
    bool? PlaylistWindowOpen,
    /// <summary>Last playlist list filter query (view-only); persisted across playlist window close/reopen.</summary>
    string? PlaylistWindowFilter,
    /// <summary>Playlist sort mode (real sort affecting play order): None | NameOrTitle | ChannelOrPath | Duration.</summary>
    string? PlaylistWindowSortMode,
    /// <summary>Playlist sort direction: Asc | Desc.</summary>
    string? PlaylistWindowSortDirection,
    double? OptionsWindowLeft,
    double? OptionsWindowTop,
    double? OptionsWindowWidth,
    double? OptionsWindowHeight,
    string? OptionsWindowState,
    bool? OptionsWindowOpen,
    bool? PlaylistWindowSnapped,
    string? PlaylistWindowSnapEdge,
    double? PlaylistWindowDockYOffset,
    double? PlaylistWindowDockXOffset,
    /// <summary>UI scale % when <see cref="PlaylistWindowWidth"/> / <see cref="PlaylistWindowHeight"/> were saved; used to restore size at a different scale.</summary>
    int? PlaylistWindowBoundsUiScalePercent,
    bool? OptionsWindowSnapped,
    string? OptionsWindowSnapEdge,
    double? OptionsWindowDockYOffset,
    double? OptionsWindowDockXOffset,
    /// <summary>Legacy; always false. Options snap/dock uses only the main window.</summary>
    bool? OptionsWindowBottomAlignToPlaylist,
    /// <summary>Last selected Options tab header: Tools, System, Audio, Theme, Search, Local, Advanced.</summary>
    string? OptionsWindowSelectedTab,
    /// <summary>Lyrics window snapped to main window edge.</summary>
    bool? LyricsWindowSnapped,
    /// <summary>Lyrics window snap edge: None, Left, Right, Bottom, Top.</summary>
    string? LyricsWindowSnapEdge,
    /// <summary>Lyrics window dock Y offset from main window.</summary>
    double? LyricsWindowDockYOffset,
    /// <summary>Lyrics window dock X offset from main window.</summary>
    double? LyricsWindowDockXOffset,
    /// <summary>Lyrics window persisted left position.</summary>
    double? LyricsWindowLeft,
    /// <summary>Lyrics window persisted top position.</summary>
    double? LyricsWindowTop,
    /// <summary>Lyrics window persisted width.</summary>
    double? LyricsWindowWidth,
    /// <summary>Lyrics window persisted height.</summary>
    double? LyricsWindowHeight,
    /// <summary>Lyrics window persisted window state.</summary>
    string? LyricsWindowState,
    /// <summary>Whether the lyrics window was open when the app last saved settings.</summary>
    bool? LyricsWindowOpen,
    /// <summary>Light | Dark | Auto. Auto chooses based on background image luminance when possible.</summary>
    string? ThemeMode,
    string? BackgroundMode,
    string? CustomBackgroundImagePath,
    string? BackgroundColorMode,
    string? CustomBackgroundColor,
    int? BackgroundAlpha,
    int? BackgroundScrimPercent,
    /// <summary>Stretch (fill) | BestFit (uniform cover, may crop) | Tile — image window background layout.</summary>
    string? BackgroundImageStretch,
    RectN? BackgroundUserDefinedMainNormal,
    RectN? BackgroundUserDefinedMainCompact,
    RectN? BackgroundUserDefinedMainUltra,
    RectN? BackgroundUserDefinedPlaylist,
    RectN? BackgroundUserDefinedOptionsLog,
    RectN? BackgroundUserDefinedLyrics,
    string? AppTitleMode,
    string? CustomAppTitle,
    /// <summary>Where the app icon is shown: TaskbarAndTray | TaskbarOnly | TrayOnly.</summary>
    string? AppIconVisibility,
    int? SearchDefaultCount,
    int? SearchMinLengthSeconds,
    bool? ReadMetadataOnLoad,
    int? UiScalePercent,
    string? WindowBorderMode,
    double? WindowBorderCustomPx,
    string? NodeJsPath,
    string? YtdlpEjsComponentSource,
    bool? YoutubeCookiesFromBrowserEnabled,
    string? YoutubeCookiesFromBrowser,
    /// <summary>Last used YouTube playlist import behavior: true = Append, false = Replace.</summary>
    bool? YoutubeImportAppend,
    /// <summary>M3U export: include YouTube webpage URLs (not playable in most players).</summary>
    bool? ExportM3uIncludeYoutube,
    /// <summary>M3U export: prefer relative paths for local files under the export folder.</summary>
    bool? ExportM3uPreferRelativePaths,
    /// <summary>M3U export: include LyllyPlayer rich comment metadata (lines starting with #LYLLY:).</summary>
    bool? ExportM3uIncludeLyllyMetadata,
    /// <summary>Last used Local files import behavior: true = Append, false = Replace.</summary>
    bool? LocalImportAppend,
    /// <summary>Last used Local files import behavior: remove duplicates (local: full path).</summary>
    bool? LocalImportRemoveDuplicates,
    string? AudioQuality,
    string? AudioOutputDevice,
    /// <summary>When true, applies lightweight real-time AGC (NAudio path). (Stored setting name kept for backward compatibility.)</summary>
    bool? AudioNormalize,
    /// <summary>ErrorsAndWarnings | Basic | Verbose — controls INFO lines in app.log.</summary>
    string? AppLogLevel,
    /// <summary>Maximum on-disk size for <c>app.log</c> before older content is dropped (megabytes).</summary>
    int? AppLogMaxMb,
    /// <summary>When true, main window hides options/playlist row and shuffle/repeat for a minimal playback strip.</summary>
    bool? MainWindowCompact,
    /// <summary>Compact layout variant: Normal | Ultra.</summary>
    string? CompactModeLayout,
    /// <summary>When true, entering compact mode closes Playlist and Options windows (restored when leaving compact).</summary>
    bool? CompactModeHidesAuxWindows,
    /// <summary>When true, Cancel on YouTube search or playlist refresh keeps partial results; when false, restores the pre-operation playlist.</summary>
    bool? KeepIncompletePlaylistOnCancel,
    /// <summary>When true, synced lyrics are resolved for YouTube tracks and displayed in the status line / window title.</summary>
    bool? LyricsEnabled,
    /// <summary>When true, LRCLIB search is attempted for local files (VideoId starting with "local:") to resolve lyrics.</summary>
    bool? LyricsLocalFilesEnabled,
    /// <summary>App build that last wrote this file; used for upgrade / load-failure messaging.</summary>
    string? LastSavedByAppVersion,
    /// <summary>Optional full path to libmp3lame.32/64.dll; when null, use bundled DLL next to the app.</summary>
    string? LameEncoderPath,
    /// <summary>MP3 export: Cbr | Vbr.</summary>
    string? Mp3ExportEncodingMode,
    int? Mp3ExportCbrQualityIndex,
    int? Mp3ExportVbrQualityIndex,
    bool? Mp3ExportReplacePlaylistEntryAfterExport
);
