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
    string? AppTitleMode,
    string? CustomAppTitle,
    int? UiScalePercent,
    string? WindowBorderMode,
    double? WindowBorderCustomPx
);

