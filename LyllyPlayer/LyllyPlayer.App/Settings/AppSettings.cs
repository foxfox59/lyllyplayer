namespace LyllyPlayer.Settings;

public sealed record AppSettings(
    string? YtDlpPath,
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
    bool? WasPlaying,
    int? CacheMaxMb,
    double? Volume,
    int? PlaylistAutoRefreshMinutes,
    bool? IncludeSubfoldersOnFolderLoad,
    bool? AlwaysOnTop,
    bool? AlwaysOnTopPlaylistWindow,
    bool? AlwaysOnTopOptionsWindow,
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
    /// <summary>App build that last wrote this file; used for upgrade / load-failure messaging.</summary>
    string? LastSavedByAppVersion
);


