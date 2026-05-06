namespace LyllyPlayer.Models;

public sealed record ThemeSettings(
    /// <summary>Light | Dark | Auto (Auto chooses based on background image luminance when possible).</summary>
    string? ThemeMode,
    string? BackgroundMode,
    string? CustomBackgroundImagePath,
    string? BackgroundColorMode,
    string? CustomBackgroundColor,
    int? BackgroundAlpha,
    int? BackgroundScrimPercent,
    string? BackgroundImageStretch,
    // Background designer crops (RectN is normalized image-space coordinates).
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedMainNormal,
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedMainCompact,
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedMainUltra,
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedPlaylist,
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedOptionsLog,
    LyllyPlayer.Settings.RectN? BackgroundUserDefinedLyrics,
    string? AppTitleMode,
    string? CustomAppTitle,
    int? UiScalePercent,
    string? WindowBorderMode,
    double? WindowBorderCustomPx
);
