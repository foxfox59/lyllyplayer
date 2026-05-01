using LyllyPlayer.Settings;

namespace LyllyPlayer.ShellServices;

/// <summary>Non-UI settings draft for Options. Kept separate so OptionsWindow can be mostly view-only.</summary>
public sealed class OptionsDraft
{
    public string YtDlpPath = "";
    public string FfmpegPath = "";
    public string NodeJsPath = "";
    public string YtdlpEjsComponentSource = "github";
    public bool YoutubeCookiesEnabled;
    public string YoutubeCookiesText = "";

    public int CacheMaxMb;
    public int? PlaylistAutoRefreshMinutes;
    public bool GlobalMediaKeysEnabled;

    public string ThemeMode = "Auto";
    public string BackgroundMode = "Default";
    public string CustomBackgroundImagePath = "";
    public string BackgroundColorMode = "Default";
    public string CustomBackgroundColor = "";
    public int BackgroundAlpha = SettingsStore.DefaultBackgroundAlpha;
    public int BackgroundScrimPercent = SettingsStore.DefaultBackgroundScrimPercent;
    public string BackgroundImageStretch = "Stretch";
    public RectN BackgroundUserDefinedMainNormal = RectN.Full;
    public RectN BackgroundUserDefinedMainCompact = RectN.Full;
    public RectN BackgroundUserDefinedMainUltra = RectN.Full;
    public RectN BackgroundUserDefinedPlaylist = RectN.Full;
    public RectN BackgroundUserDefinedOptionsLog = RectN.Full;
    public RectN BackgroundUserDefinedLyrics = RectN.Full;

    public string AppTitleMode = "Default";
    public string CustomAppTitle = "";
    public int UiScalePercent = 100;

    public string WindowBorderMode = "None";
    public double WindowBorderCustomPx = 2;

    public int SearchDefaultCount = 50;
    public int SearchMinLengthSeconds;

    public bool IncludeSubfoldersOnFolderLoad;
    public bool ReadMetadataOnLoad;
    public bool AlwaysOnTopPlaylistWindow;
    public bool AlwaysOnTopOptionsWindow;
    public bool AlwaysOnTopLyricsWindow;
    public bool CompactModeHidesAuxWindows;
    public string CompactModeLayout = "Normal";
    public bool KeepIncompletePlaylistOnCancel;
    public bool LyricsEnabled;
    public bool LyricsLocalFilesEnabled;
    public bool ExportM3uIncludeYoutube;
    public bool ExportM3uPreferRelativePaths;
    public bool ExportM3uIncludeLyllyMetadata;
    public string AppIconVisibility = "TaskbarOnly";

    public string AudioQuality = "Auto";
    public string? AudioOutputDevice; // null = Default (WAVE_MAPPER)
    public string AppLogLevel = "ErrorsAndWarnings";
    public int AppLogMaxMb = SettingsStore.DefaultAppLogMaxMb;
    public string OptionsSelectedTab = "Tools";
}

