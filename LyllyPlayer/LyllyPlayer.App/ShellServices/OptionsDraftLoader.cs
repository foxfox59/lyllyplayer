using System;
using LyllyPlayer.Services;
using LyllyPlayer.Settings;
using LyllyPlayer.Utils;

namespace LyllyPlayer.ShellServices;

public static class OptionsDraftLoader
{
    public static OptionsDraft LoadFromCurrent(
        Func<string> getYtDlpPath,
        Func<bool> getInternalYtDlpUpdateCheckEnabled,
        Func<string> getFfmpegPath,
        Func<string> getNodeJsPath,
        Func<string> getYtdlpEjsComponentSource,
        Func<bool> getYoutubeCookiesFromBrowserEnabled,
        Func<string> getYoutubeCookiesFromBrowser,
        Func<int> getCacheMaxMb,
        Func<int?> getPlaylistAutoRefreshMinutes,
        Func<bool> getGlobalMediaKeysEnabled,
        Func<string> getThemeMode,
        Func<string> getBackgroundMode,
        Func<string> getCustomBackgroundImagePath,
        Func<string> getBackgroundColorMode,
        Func<string> getCustomBackgroundColor,
        Func<int> getBackgroundAlpha,
        Func<int> getBackgroundScrimPercent,
        Func<string> getBackgroundImageStretch,
        Func<RectN?> getBackgroundUserDefinedMainNormal,
        Func<RectN?> getBackgroundUserDefinedMainCompact,
        Func<RectN?> getBackgroundUserDefinedMainUltra,
        Func<RectN?> getBackgroundUserDefinedPlaylist,
        Func<RectN?> getBackgroundUserDefinedOptionsLog,
        Func<RectN?> getBackgroundUserDefinedLyrics,
        Func<string> getAppTitleMode,
        Func<string> getCustomAppTitle,
        Func<int> getUiScalePercent,
        Func<string> getWindowBorderMode,
        Func<double> getWindowBorderCustomPx,
        Func<(int count, int minLengthSeconds)> getSearchDefaults,
        Func<bool> getIncludeSubfoldersOnFolderLoad,
        Func<bool> getReadMetadataOnLoad,
        Func<bool> getAlwaysOnTopPlaylistWindow,
        Func<bool> getAlwaysOnTopOptionsWindow,
        Func<bool> getAlwaysOnTopLyricsWindow,
        Func<bool> getCompactModeHidesAuxWindows,
        Func<string> getCompactModeLayout,
        Func<bool> getKeepIncompletePlaylistOnCancel,
        Func<bool> getExportM3uIncludeYoutube,
        Func<bool> getExportM3uPreferRelativePaths,
        Func<bool> getExportM3uIncludeLyllyMetadata,
        Func<string> getAppIconVisibility,
        Func<string> getAudioQuality,
        Func<string?> getAudioOutputDevice,
        Func<string> getAppLogLevel,
        Func<int> getAppLogMaxMb,
        Func<string> getOptionsSelectedTab,
        Func<bool> getLyricsEnabled,
        Func<bool> getLyricsLocalFilesEnabled,
        Func<string> getLameEncoderPath,
        Func<string> getMp3ExportEncodingMode,
        Func<int> getMp3ExportCbrQualityIndex,
        Func<int> getMp3ExportVbrQualityIndex,
        Func<bool> getMp3ExportReplacePlaylistEntryAfterExport)
    {
        var d = new OptionsDraft();

        try { d.YtDlpPath = getYtDlpPath() ?? ""; } catch { d.YtDlpPath = ""; }
        try { d.InternalYtDlpUpdateCheckEnabled = getInternalYtDlpUpdateCheckEnabled(); } catch { d.InternalYtDlpUpdateCheckEnabled = false; }
        try { d.FfmpegPath = getFfmpegPath() ?? ""; } catch { d.FfmpegPath = ""; }
        try { d.NodeJsPath = getNodeJsPath() ?? ""; } catch { d.NodeJsPath = ""; }
        try
        {
            var ejs = getYtdlpEjsComponentSource() ?? "github";
            d.YtdlpEjsComponentSource = string.Equals(ejs, "bundled", StringComparison.OrdinalIgnoreCase) ? "bundled" : "github";
        }
        catch { d.YtdlpEjsComponentSource = "github"; }
        try { d.YoutubeCookiesEnabled = getYoutubeCookiesFromBrowserEnabled(); } catch { d.YoutubeCookiesEnabled = false; }
        try { d.YoutubeCookiesText = getYoutubeCookiesFromBrowser() ?? ""; } catch { d.YoutubeCookiesText = ""; }

        try { d.CacheMaxMb = Math.Clamp(getCacheMaxMb(), 16, 102400); } catch { d.CacheMaxMb = 2048; }
        try { d.PlaylistAutoRefreshMinutes = getPlaylistAutoRefreshMinutes(); } catch { d.PlaylistAutoRefreshMinutes = null; }
        try { d.GlobalMediaKeysEnabled = getGlobalMediaKeysEnabled(); } catch { d.GlobalMediaKeysEnabled = false; }

        try { d.ThemeMode = SettingsStore.NormalizeThemeMode(getThemeMode()); } catch { d.ThemeMode = "Auto"; }
        try { d.BackgroundMode = SettingsStore.NormalizeBackgroundMode(getBackgroundMode()); } catch { d.BackgroundMode = "Default (Lylly)"; }
        try { d.CustomBackgroundImagePath = getCustomBackgroundImagePath() ?? ""; } catch { d.CustomBackgroundImagePath = ""; }
        try { d.BackgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(getBackgroundColorMode()); } catch { d.BackgroundColorMode = "Default"; }
        try { d.CustomBackgroundColor = getCustomBackgroundColor() ?? ""; } catch { d.CustomBackgroundColor = ""; }
        try { d.BackgroundAlpha = Math.Clamp(getBackgroundAlpha(), 0, 255); } catch { d.BackgroundAlpha = SettingsStore.DefaultBackgroundAlpha; }
        try { d.BackgroundScrimPercent = Math.Clamp(getBackgroundScrimPercent(), 0, 80); } catch { d.BackgroundScrimPercent = SettingsStore.DefaultBackgroundScrimPercent; }
        try { d.BackgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(getBackgroundImageStretch()); } catch { d.BackgroundImageStretch = "Stretch"; }

        try { d.BackgroundUserDefinedMainNormal = getBackgroundUserDefinedMainNormal() ?? RectN.Full; } catch { d.BackgroundUserDefinedMainNormal = RectN.Full; }
        try { d.BackgroundUserDefinedMainCompact = getBackgroundUserDefinedMainCompact() ?? d.BackgroundUserDefinedMainNormal; } catch { d.BackgroundUserDefinedMainCompact = d.BackgroundUserDefinedMainNormal; }
        try { d.BackgroundUserDefinedMainUltra = getBackgroundUserDefinedMainUltra() ?? d.BackgroundUserDefinedMainNormal; } catch { d.BackgroundUserDefinedMainUltra = d.BackgroundUserDefinedMainNormal; }
        try { d.BackgroundUserDefinedPlaylist = getBackgroundUserDefinedPlaylist() ?? RectN.Full; } catch { d.BackgroundUserDefinedPlaylist = RectN.Full; }
        try { d.BackgroundUserDefinedOptionsLog = getBackgroundUserDefinedOptionsLog() ?? RectN.Full; } catch { d.BackgroundUserDefinedOptionsLog = RectN.Full; }
        try { d.BackgroundUserDefinedLyrics = getBackgroundUserDefinedLyrics() ?? RectN.Full; } catch { d.BackgroundUserDefinedLyrics = RectN.Full; }

        try { d.AppTitleMode = string.IsNullOrWhiteSpace(getAppTitleMode()) ? "Default" : getAppTitleMode().Trim(); } catch { d.AppTitleMode = "Default"; }
        try { d.CustomAppTitle = getCustomAppTitle() ?? ""; } catch { d.CustomAppTitle = ""; }
        try { d.UiScalePercent = Math.Clamp(getUiScalePercent(), 50, 200); } catch { d.UiScalePercent = 100; }

        try { d.WindowBorderMode = string.IsNullOrWhiteSpace(getWindowBorderMode()) ? "None" : getWindowBorderMode().Trim(); } catch { d.WindowBorderMode = "None"; }
        try { d.WindowBorderCustomPx = Math.Clamp(getWindowBorderCustomPx(), 1, 24); } catch { d.WindowBorderCustomPx = 2; }

        try
        {
            var (c, min) = getSearchDefaults();
            d.SearchDefaultCount = Math.Clamp(c, 1, 200);
            d.SearchMinLengthSeconds = Math.Clamp(min, 0, 3600);
        }
        catch
        {
            d.SearchDefaultCount = 50;
            d.SearchMinLengthSeconds = 0;
        }

        try { d.IncludeSubfoldersOnFolderLoad = getIncludeSubfoldersOnFolderLoad(); } catch { d.IncludeSubfoldersOnFolderLoad = false; }
        try { d.ReadMetadataOnLoad = getReadMetadataOnLoad(); } catch { d.ReadMetadataOnLoad = false; }
        try { d.AlwaysOnTopPlaylistWindow = getAlwaysOnTopPlaylistWindow(); } catch { d.AlwaysOnTopPlaylistWindow = false; }
        try { d.AlwaysOnTopOptionsWindow = getAlwaysOnTopOptionsWindow(); } catch { d.AlwaysOnTopOptionsWindow = false; }
        try { d.AlwaysOnTopLyricsWindow = getAlwaysOnTopLyricsWindow(); } catch { d.AlwaysOnTopLyricsWindow = false; }
        try { d.CompactModeHidesAuxWindows = getCompactModeHidesAuxWindows(); } catch { d.CompactModeHidesAuxWindows = true; }
        try { d.CompactModeLayout = SettingsStore.NormalizeCompactModeLayout(getCompactModeLayout()); } catch { d.CompactModeLayout = "Normal"; }
        try { d.KeepIncompletePlaylistOnCancel = getKeepIncompletePlaylistOnCancel(); } catch { d.KeepIncompletePlaylistOnCancel = false; }
        try { d.ExportM3uIncludeYoutube = getExportM3uIncludeYoutube(); } catch { d.ExportM3uIncludeYoutube = true; }
        try { d.ExportM3uPreferRelativePaths = getExportM3uPreferRelativePaths(); } catch { d.ExportM3uPreferRelativePaths = false; }
        try { d.ExportM3uIncludeLyllyMetadata = getExportM3uIncludeLyllyMetadata(); } catch { d.ExportM3uIncludeLyllyMetadata = true; }
        try { d.AppIconVisibility = SettingsStore.NormalizeAppIconVisibility(getAppIconVisibility()); } catch { d.AppIconVisibility = "TaskbarOnly"; }

        try
        {
            var q = getAudioQuality();
            d.AudioQuality = q is "Auto" or "High" or "Medium" or "Low" ? q : "Auto";
        }
        catch { d.AudioQuality = "Auto"; }
        try { d.AudioOutputDevice = getAudioOutputDevice(); } catch { d.AudioOutputDevice = null; }
        try { d.AppLogLevel = AppLog.NormalizeLevelString(getAppLogLevel()); } catch { d.AppLogLevel = "ErrorsAndWarnings"; }
        try { d.AppLogMaxMb = Math.Clamp(getAppLogMaxMb(), 1, 200); } catch { d.AppLogMaxMb = SettingsStore.DefaultAppLogMaxMb; }
        try { d.OptionsSelectedTab = SettingsStore.NormalizeOptionsWindowSelectedTab(getOptionsSelectedTab()); } catch { d.OptionsSelectedTab = "Tools"; }
        try { d.LyricsEnabled = getLyricsEnabled(); } catch { d.LyricsEnabled = false; }
        try { d.LyricsLocalFilesEnabled = getLyricsLocalFilesEnabled(); } catch { d.LyricsLocalFilesEnabled = false; }

        try { d.LameEncoderPath = getLameEncoderPath() ?? ""; } catch { d.LameEncoderPath = ""; }
        try { d.Mp3ExportEncodingMode = SettingsStore.NormalizeMp3ExportEncodingMode(getMp3ExportEncodingMode()); } catch { d.Mp3ExportEncodingMode = "Vbr"; }
        try { d.Mp3ExportCbrQualityIndex = SettingsStore.ClampMp3SliderIndex(getMp3ExportCbrQualityIndex(), Mp3QualityMaps.DefaultCbrSliderIndex); } catch { d.Mp3ExportCbrQualityIndex = Mp3QualityMaps.DefaultCbrSliderIndex; }
        try { d.Mp3ExportVbrQualityIndex = SettingsStore.ClampMp3SliderIndex(getMp3ExportVbrQualityIndex(), Mp3QualityMaps.DefaultVbrSliderIndex); } catch { d.Mp3ExportVbrQualityIndex = Mp3QualityMaps.DefaultVbrSliderIndex; }
        try { d.Mp3ExportReplacePlaylistEntryAfterExport = getMp3ExportReplacePlaylistEntryAfterExport(); } catch { d.Mp3ExportReplacePlaylistEntryAfterExport = false; }

        return d;
    }
}

