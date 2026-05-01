using Microsoft.Win32;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LyllyPlayer.Settings;
using LyllyPlayer.Utils;
using LyllyPlayer.ShellServices;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace LyllyPlayer.Windows;

public partial class OptionsWindow : Window
{
    private sealed class Win32OwnerWrapper : Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public Win32OwnerWrapper(IntPtr handle) => Handle = handle;
    }

    private Window GetDialogOwnerWindow()
        => System.Windows.Application.Current?.MainWindow ?? this;

    private bool _suppressAutoRefreshEvent;
    private bool _suppressOptionsTabSelection;
    private bool _suppressBackgroundUiEvents;
    private bool _chromeDragging;
    private System.Windows.Point _chromeDragStartScreen;
    private double _chromeDragStartLeft;
    private double _chromeDragStartTop;

    private readonly Func<string> _getYtDlpPath;
    private readonly Action<string> _setYtDlpPath;
    private readonly Func<string> _getFfmpegPath;
    private readonly Action<string> _setFfmpegPath;
    private readonly Func<string> _getNodeJsPath;
    private readonly Action<string> _setNodeJsPath;
    private readonly Func<string> _getYtdlpEjsComponentSource;
    private readonly Action<string> _setYtdlpEjsComponentSource;
    private readonly Func<bool> _getYoutubeCookiesFromBrowserEnabled;
    private readonly Action<bool> _setYoutubeCookiesFromBrowserEnabled;
    private readonly Func<string> _getYoutubeCookiesFromBrowser;
    private readonly Action<string> _setYoutubeCookiesFromBrowser;
    private readonly Func<int> _getCacheMaxMb;
    private readonly Action<int> _setCacheMaxMb;
    private readonly Func<int?> _getPlaylistAutoRefreshMinutes;
    private readonly Action<int?> _setPlaylistAutoRefreshMinutes;
    private readonly Func<bool> _getGlobalMediaKeysEnabled;
    private readonly Action<bool> _setGlobalMediaKeysEnabled;
    private readonly Func<string> _getBackgroundMode;
    private readonly Action<string> _setBackgroundMode;
    private readonly Func<string> _getCustomBackgroundImagePath;
    private readonly Action<string> _setCustomBackgroundImagePath;
    private readonly Func<string> _getBackgroundColorMode;
    private readonly Action<string> _setBackgroundColorMode;
    private readonly Func<string> _getCustomBackgroundColor;
    private readonly Action<string> _setCustomBackgroundColor;
    private readonly Func<int> _getBackgroundAlpha;
    private readonly Action<int> _setBackgroundAlpha;
    private readonly Func<int> _getBackgroundScrimPercent;
    private readonly Action<int> _setBackgroundScrimPercent;
    private readonly Func<string> _getBackgroundImageStretch;
    private readonly Action<string> _setBackgroundImageStretch;
    private readonly Action? _openBackgroundDesigner;
    private readonly Func<string> _getAppTitleMode;
    private readonly Action<string> _setAppTitleMode;
    private readonly Func<string> _getCustomAppTitle;
    private readonly Action<string> _setCustomAppTitle;
    private readonly Func<int> _getUiScalePercent;
    private readonly Action<int> _setUiScalePercent;
    private readonly Func<string> _getWindowBorderMode;
    private readonly Action<string> _setWindowBorderMode;
    private readonly Func<double> _getWindowBorderCustomPx;
    private readonly Action<double> _setWindowBorderCustomPx;
    private readonly Func<LyllyPlayer.Models.ThemeSettings> _getTheme;
    private readonly Action<LyllyPlayer.Models.ThemeSettings> _applyTheme;
    private readonly Func<(int count, int minLengthSeconds)> _getSearchDefaults;
    private readonly Action<int, int> _setSearchDefaults;
    private readonly Func<bool> _getIncludeSubfoldersOnFolderLoad;
    private readonly Action<bool> _setIncludeSubfoldersOnFolderLoad;
    private readonly Func<bool> _getReadMetadataOnLoad;
    private readonly Action<bool> _setReadMetadataOnLoad;
    private readonly Func<bool> _getAlwaysOnTopPlaylistWindow;
    private readonly Action<bool> _setAlwaysOnTopPlaylistWindow;
    private readonly Func<bool> _getAlwaysOnTopOptionsWindow;
    private readonly Action<bool> _setAlwaysOnTopOptionsWindow;
    private readonly Func<bool> _getAlwaysOnTopLyricsWindow;
    private readonly Action<bool> _setAlwaysOnTopLyricsWindow;
    private readonly Func<bool> _getCompactModeHidesAuxWindows;
    private readonly Action<bool> _setCompactModeHidesAuxWindows;
    private readonly Func<string> _getCompactModeLayout;
    private readonly Action<string> _setCompactModeLayout;
    private readonly Func<bool> _getKeepIncompletePlaylistOnCancel;
    private readonly Action<bool> _setKeepIncompletePlaylistOnCancel;
    private readonly Func<bool> _getLyricsEnabled;
    private readonly Action<bool> _setLyricsEnabled;
    private readonly Func<bool> _getLyricsLocalFilesEnabled;
    private readonly Action<bool> _setLyricsLocalFilesEnabled;
    private readonly Func<bool> _getExportM3uIncludeYoutube;
    private readonly Action<bool> _setExportM3uIncludeYoutube;
    private readonly Func<bool> _getExportM3uPreferRelativePaths;
    private readonly Action<bool> _setExportM3uPreferRelativePaths;
    private readonly Func<bool> _getExportM3uIncludeLyllyMetadata;
    private readonly Action<bool> _setExportM3uIncludeLyllyMetadata;
    private readonly Func<string> _getAppIconVisibility;
    private readonly Action<string> _setAppIconVisibility;
    private readonly Func<string> _getAppLogLevel;
    private readonly Action<string> _setAppLogLevel;
    private readonly Func<int> _getAppLogMaxMb;
    private readonly Action<int> _setAppLogMaxMb;
    private readonly Action _showLog;
    private bool _logPopoutOpen;
    private readonly Action? _persistSettingsNow;
    private readonly Func<string> _getAudioQuality;
    private readonly Action<string> _setAudioQuality;
    private readonly Func<string?> _getAudioOutputDevice;
    private readonly Action<string?> _setAudioOutputDevice;
    private readonly Func<string> _getOptionsSelectedTab;
    private readonly Action<string> _setOptionsSelectedTab;
    private readonly Func<string> _getThemeMode;
    private readonly Action<string> _setThemeMode;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedMainNormal;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedMainNormal;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedMainCompact;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedMainCompact;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedMainUltra;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedMainUltra;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedPlaylist;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedPlaylist;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedOptionsLog;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedOptionsLog;
    private readonly Func<LyllyPlayer.Settings.RectN?> _getBackgroundUserDefinedLyrics;
    private readonly Action<LyllyPlayer.Settings.RectN?> _setBackgroundUserDefinedLyrics;

    private OptionsDraft _draft = new();

    public OptionsWindow(
        Func<string> getYtDlpPath,
        Action<string> setYtDlpPath,
        Func<string> getFfmpegPath,
        Action<string> setFfmpegPath,
        Func<string> getNodeJsPath,
        Action<string> setNodeJsPath,
        Func<string> getYtdlpEjsComponentSource,
        Action<string> setYtdlpEjsComponentSource,
        Func<bool> getYoutubeCookiesFromBrowserEnabled,
        Action<bool> setYoutubeCookiesFromBrowserEnabled,
        Func<string> getYoutubeCookiesFromBrowser,
        Action<string> setYoutubeCookiesFromBrowser,
        Func<int> getCacheMaxMb,
        Action<int> setCacheMaxMb,
        Func<int?> getPlaylistAutoRefreshMinutes,
        Action<int?> setPlaylistAutoRefreshMinutes,
        Func<bool> getGlobalMediaKeysEnabled,
        Action<bool> setGlobalMediaKeysEnabled,
        Func<string> getThemeMode,
        Action<string> setThemeMode,
        Func<string> getBackgroundMode,
        Action<string> setBackgroundMode,
        Func<string> getCustomBackgroundImagePath,
        Action<string> setCustomBackgroundImagePath,
        Func<string> getBackgroundColorMode,
        Action<string> setBackgroundColorMode,
        Func<string> getCustomBackgroundColor,
        Action<string> setCustomBackgroundColor,
        Func<int> getBackgroundAlpha,
        Action<int> setBackgroundAlpha,
        Func<int> getBackgroundScrimPercent,
        Action<int> setBackgroundScrimPercent,
        Func<string> getBackgroundImageStretch,
        Action<string> setBackgroundImageStretch,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedMainNormal,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedMainNormal,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedMainCompact,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedMainCompact,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedMainUltra,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedMainUltra,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedPlaylist,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedPlaylist,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedOptionsLog,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedOptionsLog,
        Func<LyllyPlayer.Settings.RectN?> getBackgroundUserDefinedLyrics,
        Action<LyllyPlayer.Settings.RectN?> setBackgroundUserDefinedLyrics,
        Action openBackgroundDesigner,
        Func<string> getAppTitleMode,
        Action<string> setAppTitleMode,
        Func<string> getCustomAppTitle,
        Action<string> setCustomAppTitle,
        Func<int> getUiScalePercent,
        Action<int> setUiScalePercent,
        Func<string> getWindowBorderMode,
        Action<string> setWindowBorderMode,
        Func<double> getWindowBorderCustomPx,
        Action<double> setWindowBorderCustomPx,
        Func<LyllyPlayer.Models.ThemeSettings> getTheme,
        Action<LyllyPlayer.Models.ThemeSettings> applyTheme,
        Func<(int count, int minLengthSeconds)> getSearchDefaults,
        Action<int, int> setSearchDefaults,
        Func<bool> getIncludeSubfoldersOnFolderLoad,
        Action<bool> setIncludeSubfoldersOnFolderLoad,
        Func<bool> getReadMetadataOnLoad,
        Action<bool> setReadMetadataOnLoad,
        Func<bool> getAlwaysOnTopPlaylistWindow,
        Action<bool> setAlwaysOnTopPlaylistWindow,
        Func<bool> getAlwaysOnTopOptionsWindow,
        Action<bool> setAlwaysOnTopOptionsWindow,
        Func<bool> getAlwaysOnTopLyricsWindow,
        Action<bool> setAlwaysOnTopLyricsWindow,
        Func<bool> getCompactModeHidesAuxWindows,
        Action<bool> setCompactModeHidesAuxWindows,
        Func<string> getCompactModeLayout,
        Action<string> setCompactModeLayout,
        Func<bool> getKeepIncompletePlaylistOnCancel,
        Action<bool> setKeepIncompletePlaylistOnCancel,
        Func<bool> getLyricsEnabled,
        Action<bool> setLyricsEnabled,
        Func<bool> getLyricsLocalFilesEnabled,
        Action<bool> setLyricsLocalFilesEnabled,
        Func<bool> getExportM3uIncludeYoutube,
    Action<bool> setExportM3uIncludeYoutube,
    Func<bool> getExportM3uPreferRelativePaths,
        Action<bool> setExportM3uPreferRelativePaths,
        Func<bool> getExportM3uIncludeLyllyMetadata,
        Action<bool> setExportM3uIncludeLyllyMetadata,
        Func<string> getAppIconVisibility,
        Action<string> setAppIconVisibility,
        Func<string> getAppLogLevel,
        Action<string> setAppLogLevel,
        Func<int> getAppLogMaxMb,
        Action<int> setAppLogMaxMb,
        Action showLog,
        Func<string> getAudioQuality,
        Action<string> setAudioQuality,
        Func<string?> getAudioOutputDevice,
        Action<string?> setAudioOutputDevice,
        Func<string> getOptionsSelectedTab,
        Action<string> setOptionsSelectedTab,
        Action? persistSettingsNow = null)
    {
        _getYtDlpPath = getYtDlpPath;
        _setYtDlpPath = setYtDlpPath;
        _getFfmpegPath = getFfmpegPath;
        _setFfmpegPath = setFfmpegPath;
        _getNodeJsPath = getNodeJsPath;
        _setNodeJsPath = setNodeJsPath;
        _getYtdlpEjsComponentSource = getYtdlpEjsComponentSource;
        _setYtdlpEjsComponentSource = setYtdlpEjsComponentSource;
        _getYoutubeCookiesFromBrowserEnabled = getYoutubeCookiesFromBrowserEnabled;
        _setYoutubeCookiesFromBrowserEnabled = setYoutubeCookiesFromBrowserEnabled;
        _getYoutubeCookiesFromBrowser = getYoutubeCookiesFromBrowser;
        _setYoutubeCookiesFromBrowser = setYoutubeCookiesFromBrowser;
        _getCacheMaxMb = getCacheMaxMb;
        _setCacheMaxMb = setCacheMaxMb;
        _getPlaylistAutoRefreshMinutes = getPlaylistAutoRefreshMinutes;
        _setPlaylistAutoRefreshMinutes = setPlaylistAutoRefreshMinutes;
        _getGlobalMediaKeysEnabled = getGlobalMediaKeysEnabled;
        _setGlobalMediaKeysEnabled = setGlobalMediaKeysEnabled;
        _getBackgroundMode = getBackgroundMode;
        _setBackgroundMode = setBackgroundMode;
        _getCustomBackgroundImagePath = getCustomBackgroundImagePath;
        _setCustomBackgroundImagePath = setCustomBackgroundImagePath;
        _getBackgroundColorMode = getBackgroundColorMode;
        _setBackgroundColorMode = setBackgroundColorMode;
        _getCustomBackgroundColor = getCustomBackgroundColor;
        _setCustomBackgroundColor = setCustomBackgroundColor;
        _getBackgroundAlpha = getBackgroundAlpha;
        _setBackgroundAlpha = setBackgroundAlpha;
        _getBackgroundScrimPercent = getBackgroundScrimPercent;
        _setBackgroundScrimPercent = setBackgroundScrimPercent;
        _getBackgroundImageStretch = getBackgroundImageStretch;
        _setBackgroundImageStretch = setBackgroundImageStretch;
        _getBackgroundUserDefinedMainNormal = getBackgroundUserDefinedMainNormal;
        _setBackgroundUserDefinedMainNormal = setBackgroundUserDefinedMainNormal;
        _getBackgroundUserDefinedMainCompact = getBackgroundUserDefinedMainCompact;
        _setBackgroundUserDefinedMainCompact = setBackgroundUserDefinedMainCompact;
        _getBackgroundUserDefinedMainUltra = getBackgroundUserDefinedMainUltra;
        _setBackgroundUserDefinedMainUltra = setBackgroundUserDefinedMainUltra;
        _getBackgroundUserDefinedPlaylist = getBackgroundUserDefinedPlaylist;
        _setBackgroundUserDefinedPlaylist = setBackgroundUserDefinedPlaylist;
        _getBackgroundUserDefinedOptionsLog = getBackgroundUserDefinedOptionsLog;
        _setBackgroundUserDefinedOptionsLog = setBackgroundUserDefinedOptionsLog;
        _getBackgroundUserDefinedLyrics = getBackgroundUserDefinedLyrics;
        _setBackgroundUserDefinedLyrics = setBackgroundUserDefinedLyrics;
        _openBackgroundDesigner = openBackgroundDesigner;
        _getAppTitleMode = getAppTitleMode;
        _setAppTitleMode = setAppTitleMode;
        _getCustomAppTitle = getCustomAppTitle;
        _setCustomAppTitle = setCustomAppTitle;
        _getUiScalePercent = getUiScalePercent;
        _setUiScalePercent = setUiScalePercent;
        _getWindowBorderMode = getWindowBorderMode;
        _setWindowBorderMode = setWindowBorderMode;
        _getWindowBorderCustomPx = getWindowBorderCustomPx;
        _setWindowBorderCustomPx = setWindowBorderCustomPx;
        _getTheme = getTheme;
        _applyTheme = applyTheme;
        _getSearchDefaults = getSearchDefaults;
        _setSearchDefaults = setSearchDefaults;
        _getIncludeSubfoldersOnFolderLoad = getIncludeSubfoldersOnFolderLoad;
        _setIncludeSubfoldersOnFolderLoad = setIncludeSubfoldersOnFolderLoad;
        _getReadMetadataOnLoad = getReadMetadataOnLoad;
        _setReadMetadataOnLoad = setReadMetadataOnLoad;
        _getAlwaysOnTopPlaylistWindow = getAlwaysOnTopPlaylistWindow;
        _setAlwaysOnTopPlaylistWindow = setAlwaysOnTopPlaylistWindow;
        _getAlwaysOnTopOptionsWindow = getAlwaysOnTopOptionsWindow;
        _setAlwaysOnTopOptionsWindow = setAlwaysOnTopOptionsWindow;
        _getAlwaysOnTopLyricsWindow = getAlwaysOnTopLyricsWindow;
        _setAlwaysOnTopLyricsWindow = setAlwaysOnTopLyricsWindow;
        _getCompactModeHidesAuxWindows = getCompactModeHidesAuxWindows;
        _setCompactModeHidesAuxWindows = setCompactModeHidesAuxWindows;
        _getCompactModeLayout = getCompactModeLayout;
        _setCompactModeLayout = setCompactModeLayout;
        _getKeepIncompletePlaylistOnCancel = getKeepIncompletePlaylistOnCancel;
        _setKeepIncompletePlaylistOnCancel = setKeepIncompletePlaylistOnCancel;
        _getLyricsEnabled = getLyricsEnabled;
        _setLyricsEnabled = setLyricsEnabled;
        _getLyricsLocalFilesEnabled = getLyricsLocalFilesEnabled;
        _setLyricsLocalFilesEnabled = setLyricsLocalFilesEnabled;
        _getExportM3uIncludeYoutube = getExportM3uIncludeYoutube;
        _setExportM3uIncludeYoutube = setExportM3uIncludeYoutube;
        _getExportM3uPreferRelativePaths = getExportM3uPreferRelativePaths;
        _setExportM3uPreferRelativePaths = setExportM3uPreferRelativePaths;
        _getExportM3uIncludeLyllyMetadata = getExportM3uIncludeLyllyMetadata;
        _setExportM3uIncludeLyllyMetadata = setExportM3uIncludeLyllyMetadata;
        _getAppIconVisibility = getAppIconVisibility;
        _setAppIconVisibility = setAppIconVisibility;
        _getAppLogLevel = getAppLogLevel;
        _setAppLogLevel = setAppLogLevel;
        _getAppLogMaxMb = getAppLogMaxMb;
        _setAppLogMaxMb = setAppLogMaxMb;
        _showLog = showLog;
        _getAudioQuality = getAudioQuality;
        _setAudioQuality = setAudioQuality;
        _getAudioOutputDevice = getAudioOutputDevice;
        _setAudioOutputDevice = setAudioOutputDevice;
        _getOptionsSelectedTab = getOptionsSelectedTab;
        _setOptionsSelectedTab = setOptionsSelectedTab;
        _getThemeMode = getThemeMode;
        _setThemeMode = setThemeMode;
        _persistSettingsNow = persistSettingsNow;

        InitializeComponent();
        Loaded += (_, _) =>
            {
                try
                {
                    // Some global styles/templates can accidentally stamp IsEnabled/HitTestVisible.
                    // Force this control to be interactive.
                    PlaylistAutoRefreshComboBox.ClearValue(UIElement.IsEnabledProperty);
                    PlaylistAutoRefreshComboBox.ClearValue(UIElement.IsHitTestVisibleProperty);
                    PlaylistAutoRefreshComboBox.IsEnabled = true;
                    PlaylistAutoRefreshComboBox.IsHitTestVisible = true;
                    PlaylistAutoRefreshComboBox.Focusable = true;

                    GlobalMediaKeysCheckBox.ClearValue(UIElement.IsEnabledProperty);
                    GlobalMediaKeysCheckBox.ClearValue(UIElement.IsHitTestVisibleProperty);
                    GlobalMediaKeysCheckBox.IsEnabled = true;
                    GlobalMediaKeysCheckBox.IsHitTestVisible = true;
                    GlobalMediaKeysCheckBox.Focusable = true;

                    var parent = PlaylistAutoRefreshComboBox.Parent as DependencyObject;
                    var parentIsEnabled = true;
                    try
                    {
                        if (parent is UIElement uie)
                            parentIsEnabled = uie.IsEnabled;
                    }
                    catch { /* ignore */ }

                    AppLog.Info(
                        $"Options AutoRefresh Combo runtime: IsEnabled={PlaylistAutoRefreshComboBox.IsEnabled} " +
                        $"IsHitTestVisible={PlaylistAutoRefreshComboBox.IsHitTestVisible} " +
                        $"Focusable={PlaylistAutoRefreshComboBox.Focusable} " +
                        $"Opacity={PlaylistAutoRefreshComboBox.Opacity} " +
                        $"ParentIsEnabled={parentIsEnabled}",
                        AppLogInfoTier.Diagnostic);
                }
                catch { /* ignore */ }
            };
        LoadDraftFromCurrent();
        RefreshUi();
    }

    private void LoadDraftFromCurrent()
    {
        _draft = OptionsDraftLoader.LoadFromCurrent(
            _getYtDlpPath,
            _getFfmpegPath,
            _getNodeJsPath,
            _getYtdlpEjsComponentSource,
            _getYoutubeCookiesFromBrowserEnabled,
            _getYoutubeCookiesFromBrowser,
            _getCacheMaxMb,
            _getPlaylistAutoRefreshMinutes,
            _getGlobalMediaKeysEnabled,
            _getThemeMode,
            _getBackgroundMode,
            _getCustomBackgroundImagePath,
            _getBackgroundColorMode,
            _getCustomBackgroundColor,
            _getBackgroundAlpha,
            _getBackgroundScrimPercent,
            _getBackgroundImageStretch,
            _getBackgroundUserDefinedMainNormal,
            _getBackgroundUserDefinedMainCompact,
            _getBackgroundUserDefinedMainUltra,
            _getBackgroundUserDefinedPlaylist,
            _getBackgroundUserDefinedOptionsLog,
            _getBackgroundUserDefinedLyrics,
            _getAppTitleMode,
            _getCustomAppTitle,
            _getUiScalePercent,
            _getWindowBorderMode,
            _getWindowBorderCustomPx,
            _getSearchDefaults,
            _getIncludeSubfoldersOnFolderLoad,
            _getReadMetadataOnLoad,
            _getAlwaysOnTopPlaylistWindow,
            _getAlwaysOnTopOptionsWindow,
            _getAlwaysOnTopLyricsWindow,
            _getCompactModeHidesAuxWindows,
            _getCompactModeLayout,
            _getKeepIncompletePlaylistOnCancel,
            _getExportM3uIncludeYoutube,
            _getExportM3uPreferRelativePaths,
            _getExportM3uIncludeLyllyMetadata,
            _getAppIconVisibility,
            _getAudioQuality,
            _getAudioOutputDevice,
            _getAppLogLevel,
            _getAppLogMaxMb,
            _getOptionsSelectedTab,
            _getLyricsEnabled,
            _getLyricsLocalFilesEnabled);
    }

    private void RefreshUi()
    {
        try { _suppressBackgroundUiEvents = true; } catch { /* ignore */ }

        try
        {
            _suppressOptionsTabSelection = true;
            var tab = SettingsStore.NormalizeOptionsWindowSelectedTab(_draft.OptionsSelectedTab);
            _draft.OptionsSelectedTab = tab;
            TabItem? match = null;
            foreach (var obj in OptionsTabControl.Items)
            {
                if (obj is TabItem ti && ti.Header is string h && string.Equals(h, tab, StringComparison.OrdinalIgnoreCase))
                {
                    match = ti;
                    break;
                }
            }
            OptionsTabControl.SelectedItem = match ?? OptionsTabControl.Items.Cast<object>().FirstOrDefault();
        }
        catch { /* ignore */ }
        finally
        {
            try { _suppressOptionsTabSelection = false; } catch { /* ignore */ }
        }

        RefreshToolsResolution();
        CacheMaxMbTextBox.Text = Math.Clamp(_draft.CacheMaxMb, 16, 102400).ToString();
        try { GlobalMediaKeysCheckBox.IsChecked = _draft.GlobalMediaKeysEnabled; } catch { /* ignore */ }
        try
        {
            PlaylistAutoRefreshComboBox.ClearValue(UIElement.IsEnabledProperty);
            PlaylistAutoRefreshComboBox.ClearValue(UIElement.IsHitTestVisibleProperty);
            PlaylistAutoRefreshComboBox.IsEnabled = true;
            PlaylistAutoRefreshComboBox.IsHitTestVisible = true;
        }
        catch { /* ignore */ }
        try
        {
            GlobalMediaKeysCheckBox.ClearValue(UIElement.IsEnabledProperty);
            GlobalMediaKeysCheckBox.ClearValue(UIElement.IsHitTestVisibleProperty);
            GlobalMediaKeysCheckBox.IsEnabled = true;
            GlobalMediaKeysCheckBox.IsHitTestVisible = true;
        }
        catch { /* ignore */ }
        ApplyAutoRefreshSelection(_draft.PlaylistAutoRefreshMinutes);
        try { ApplyThemeModeSelection(_draft.ThemeMode); } catch { /* ignore */ }
        try { ApplyBackgroundSelection(_draft.BackgroundMode); } catch { /* ignore */ }
        try { CustomBackgroundPathTextBox.Text = _draft.CustomBackgroundImagePath ?? ""; } catch { /* ignore */ }
        try { UpdateBackgroundBrowseEnabled(); } catch { /* ignore */ }

        try { ApplyBackgroundColorSelection(_draft.BackgroundColorMode); } catch { /* ignore */ }
        try { CoerceBackgroundColorModeIfNoImageBackground(); } catch { /* ignore */ }
        try
        {
            var a = Math.Clamp(_draft.BackgroundAlpha, 0, 255);
            BackgroundAlphaSlider.Value = a;
            BackgroundAlphaValueTextBlock.Text = $"{(int)Math.Round(a / 255.0 * 100)}%";
        }
        catch { /* ignore */ }
        try { UpdateBackgroundColorPickEnabled(); } catch { /* ignore */ }

        try
        {
            var s = Math.Clamp(_draft.BackgroundScrimPercent, 0, 80);
            BackgroundScrimSlider.Value = s;
            BackgroundScrimValueTextBlock.Text = $"{s}%";
        }
        catch { /* ignore */ }
        try
        {
            var stretchNorm = SettingsStore.NormalizeBackgroundImageStretch(_draft.BackgroundImageStretch);
            _draft.BackgroundImageStretch = stretchNorm;
            var stretchItems = BackgroundImageStretchComboBox.Items.Cast<ComboBoxItem>().ToList();
            var sIdx = stretchItems.FindIndex(i => string.Equals(i.Tag as string, stretchNorm, StringComparison.OrdinalIgnoreCase));
            BackgroundImageStretchComboBox.SelectedIndex = sIdx >= 0 ? sIdx : 0;
        }
        catch { /* ignore */ }
        try { UpdateBackgroundImageDerivedControls(); } catch { /* ignore */ }

        try
        {
            ApplyAppTitleModeSelection(_draft.AppTitleMode);
            CustomAppTitleTextBox.Text = _draft.CustomAppTitle ?? "";
            UpdateCustomAppTitleEnabled();
        }
        catch { /* ignore */ }

        try
        {
            var p = Math.Clamp(_draft.UiScalePercent, 50, 200);
            UiScaleSlider.Value = p;
            UiScaleValueTextBlock.Text = $"{p}%";
        }
        catch { /* ignore */ }

        try { ApplyWindowBorderModeSelection(_draft.WindowBorderMode); } catch { /* ignore */ }
        try
        {
            var px = (int)Math.Clamp(Math.Round(_draft.WindowBorderCustomPx), 1, 24);
            WindowBorderCustomPxSlider.Value = px;
            WindowBorderCustomPxValueTextBlock.Text = $"{px} px";
            UpdateWindowBorderCustomEnabled();
        }
        catch { /* ignore */ }

        try
        {
            SearchDefaultCountTextBox.Text = Math.Clamp(_draft.SearchDefaultCount, 1, 200).ToString();
            var idx = _draft.SearchMinLengthSeconds switch
            {
                >= 120 => 3,
                >= 60 => 2,
                >= 30 => 1,
                _ => 0
            };
            SearchMinLengthComboBox.SelectedIndex = idx;
        }
        catch { /* ignore */ }

        try { IncludeSubfoldersOnFolderLoadCheckBox.IsChecked = _draft.IncludeSubfoldersOnFolderLoad; } catch { /* ignore */ }
        try { ReadMetadataOnLoadCheckBox.IsChecked = _draft.ReadMetadataOnLoad; } catch { /* ignore */ }
        try { AlwaysOnTopPlaylistWindowCheckBox.IsChecked = _draft.AlwaysOnTopPlaylistWindow; } catch { /* ignore */ }
        try { AlwaysOnTopOptionsWindowCheckBox.IsChecked = _draft.AlwaysOnTopOptionsWindow; } catch { /* ignore */ }
        try { AlwaysOnTopLyricsWindowCheckBox.IsChecked = _draft.AlwaysOnTopLyricsWindow; } catch { /* ignore */ }
        try { CompactHidesAuxWindowsCheckBox.IsChecked = _draft.CompactModeHidesAuxWindows; } catch { /* ignore */ }
        try
        {
            var m = SettingsStore.NormalizeCompactModeLayout(_draft.CompactModeLayout);
            CompactLayoutNormalRadio.IsChecked = string.Equals(m, "Normal", StringComparison.OrdinalIgnoreCase);
            CompactLayoutUltraRadio.IsChecked = string.Equals(m, "Ultra", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }
        try { KeepIncompletePlaylistOnCancelCheckBox.IsChecked = _draft.KeepIncompletePlaylistOnCancel; } catch { /* ignore */ }
        try { LyricsEnabledCheckBox.IsChecked = _draft.LyricsEnabled; } catch { /* ignore */ }
        try { LyricsLocalFilesEnabledCheckBox.IsChecked = _draft.LyricsLocalFilesEnabled; } catch { /* ignore */ }
        try { ExportM3uIncludeYoutubeCheckBox.IsChecked = _draft.ExportM3uIncludeYoutube; } catch { /* ignore */ }
        try { ExportM3uPreferRelativePathsCheckBox.IsChecked = _draft.ExportM3uPreferRelativePaths; } catch { /* ignore */ }
        try { ExportM3uIncludeLyllyMetadataCheckBox.IsChecked = _draft.ExportM3uIncludeLyllyMetadata; } catch { /* ignore */ }

        try
        {
            var m = SettingsStore.NormalizeAppIconVisibility(_draft.AppIconVisibility);
            AppIconTaskbarAndTrayRadio.IsChecked = string.Equals(m, "TaskbarAndTray", StringComparison.OrdinalIgnoreCase);
            AppIconTaskbarOnlyRadio.IsChecked = string.Equals(m, "TaskbarOnly", StringComparison.OrdinalIgnoreCase);
            AppIconTrayOnlyRadio.IsChecked = string.Equals(m, "TrayOnly", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }

        try
        {
            var nodeRes = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.NodeJsPath) ? null : _draft.NodeJsPath, "node");
            var nodeOk = nodeRes.IsFound;
            AdvancedNodeGatingBorder.Visibility = nodeOk ? Visibility.Collapsed : Visibility.Visible;
            EjsSourcePanel.IsEnabled = nodeOk;
            CookiesPanel.IsEnabled = nodeOk;

            if (string.Equals(_draft.YtdlpEjsComponentSource, "bundled", StringComparison.OrdinalIgnoreCase))
                EjsBundledRadio.IsChecked = true;
            else
                EjsGithubRadio.IsChecked = true;
            YoutubeCookiesCheckBox.IsChecked = _draft.YoutubeCookiesEnabled;
            YoutubeCookiesTextBox.Text = _draft.YoutubeCookiesText ?? "";
            YoutubeCookiesTextBox.IsEnabled = nodeOk && _draft.YoutubeCookiesEnabled;
        }
        catch { /* ignore */ }

        try
        {
            var qualityItems = AudioQualityComboBox.Items.Cast<ComboBoxItem>().ToList();
            var qIdx = qualityItems.FindIndex(i => string.Equals(i.Tag as string, _draft.AudioQuality, StringComparison.OrdinalIgnoreCase));
            AudioQualityComboBox.SelectedIndex = qIdx >= 0 ? qIdx : 0;
        }
        catch { /* ignore */ }

        try { RefreshAudioDeviceList(); } catch { /* ignore */ }

        try
        {
            var logItems = AppLogLevelComboBox.Items.Cast<ComboBoxItem>().ToList();
            var logIdx = logItems.FindIndex(i => string.Equals(i.Tag as string, _draft.AppLogLevel, StringComparison.OrdinalIgnoreCase));
            AppLogLevelComboBox.SelectedIndex = logIdx >= 0 ? logIdx : 0;
        }
        catch { /* ignore */ }

        try { AppLogMaxMbTextBox.Text = Math.Clamp(_draft.AppLogMaxMb, 1, 200).ToString(); } catch { /* ignore */ }

        try { _suppressBackgroundUiEvents = false; } catch { /* ignore */ }
    }

    private void ApplyThemeModeSelection(string? mode)
    {
        if (ThemeModeComboBox is null)
            return;
        var label = string.IsNullOrWhiteSpace(mode) ? "Auto" : mode.Trim();
        foreach (var obj in ThemeModeComboBox.Items)
        {
            if (obj is not ComboBoxItem item)
                continue;
            if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                ThemeModeComboBox.SelectedItem = item;
                return;
            }
        }
        ThemeModeComboBox.SelectedIndex = 0;
    }

    private void ThemeModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (ThemeModeComboBox.SelectedItem is not ComboBoxItem item)
                return;
            var m = (item.Content as string)?.Trim() ?? "Auto";
            _draft.ThemeMode = SettingsStore.NormalizeThemeMode(m);
        }
        catch { /* ignore */ }
    }

    private static string SourceSubtitle(ToolPathSource source)
        => source switch
        {
            ToolPathSource.Explicit => "Source: explicit path",
            ToolPathSource.Path => "Source: PATH",
            _ => "Source: not found",
        };

    private void RefreshAudioDeviceList()
    {
        var prev = _draft.AudioOutputDevice;
        AudioOutputDeviceComboBox.Items.Clear();
        AudioOutputDeviceComboBox.Items.Add(new ComboBoxItem { Content = "Default", Tag = (string?)null });
        int selectIdx = 0;
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var name = WaveOut.GetCapabilities(i).ProductName;
            AudioOutputDeviceComboBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            if (string.Equals(name, prev, StringComparison.OrdinalIgnoreCase))
                selectIdx = i + 1;
        }
        AudioOutputDeviceComboBox.SelectedIndex = selectIdx;
    }

    private void RefreshToolsResolution()
    {
        var y = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.YtDlpPath) ? null : _draft.YtDlpPath, "yt-dlp");
        var f = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.FfmpegPath) ? null : _draft.FfmpegPath, "ffmpeg");
        var n = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.NodeJsPath) ? null : _draft.NodeJsPath, "node");

        YtDlpPathTextBox.Text = y.DisplayText;
        YtDlpSourceTextBlock.Text = SourceSubtitle(y.Source);
        FfmpegPathTextBox.Text = f.DisplayText;
        FfmpegSourceTextBlock.Text = SourceSubtitle(f.Source);
        NodePathTextBox.Text = n.DisplayText;
        NodeSourceTextBlock.Text = SourceSubtitle(n.Source);

        var pathLines = new List<string>();
        if (y is { IsFound: true, Source: ToolPathSource.Path })
            pathLines.Add($"yt-dlp → {y.DisplayText}");
        if (f is { IsFound: true, Source: ToolPathSource.Path })
            pathLines.Add($"ffmpeg → {f.DisplayText}");
        if (n is { IsFound: true, Source: ToolPathSource.Path })
            pathLines.Add($"node → {n.DisplayText}");

        if (pathLines.Count > 0)
            ToolsPathNoticeTextBlock.Text = "Using PATH:\n" + string.Join("\n", pathLines);
        else
            ToolsPathNoticeTextBlock.Text = "No tools are using PATH auto-detection (each tool uses an explicit path, or is unset).";
    }

    private void ApplyBackgroundSelection(string? mode)
    {
        var label = string.IsNullOrWhiteSpace(mode) ? "Default (Lylly)" : mode.Trim();
        if (string.Equals(label, "Default", StringComparison.OrdinalIgnoreCase))
            label = "Default (Lylly)";
        foreach (var obj in BackgroundModeComboBox.Items)
        {
            if (obj is not ComboBoxItem item)
                continue;
            if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                BackgroundModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateBackgroundBrowseEnabled()
    {
        var mode = "Default (Lylly)";
        try
        {
            if (BackgroundModeComboBox.SelectedItem is ComboBoxItem item)
                mode = (item.Content as string)?.Trim() ?? "Default (Lylly)";
        }
        catch { /* ignore */ }

        var isCustom = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        BackgroundBrowseButton.IsEnabled = isCustom;
        CustomBackgroundPathTextBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"From image" colors only apply when a wallpaper is shown.</summary>
    private void CoerceBackgroundColorModeIfNoImageBackground()
    {
        if (!string.Equals(_draft.BackgroundMode, "None", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(_draft.BackgroundColorMode, "From image", StringComparison.OrdinalIgnoreCase))
            return;
        _draft.BackgroundColorMode = "Default";
        ApplyBackgroundColorSelection("Default");
    }

    private void UpdateBackgroundImageDerivedControls()
    {
        var bgNone = string.Equals(_draft.BackgroundMode, "None", StringComparison.OrdinalIgnoreCase);
        try
        {
            foreach (var obj in BackgroundColorModeComboBox.Items)
            {
                if (obj is not ComboBoxItem cbi)
                    continue;
                var label = (cbi.Content as string)?.Trim() ?? "";
                if (string.Equals(label, "From image", StringComparison.OrdinalIgnoreCase))
                    cbi.IsEnabled = !bgNone;
            }
        }
        catch { /* ignore */ }

        try { BackgroundScrimSlider.IsEnabled = !bgNone; } catch { /* ignore */ }
        try { BackgroundImageStretchComboBox.IsEnabled = !bgNone; } catch { /* ignore */ }
        try
        {
            var userDefined = string.Equals(SettingsStore.NormalizeBackgroundImageStretch(_draft.BackgroundImageStretch), "UserDefined", StringComparison.OrdinalIgnoreCase);
            BackgroundDesignerButton.IsEnabled = !bgNone && userDefined;
        }
        catch { /* ignore */ }
    }

    /// <summary>Normalized color mode is <c>Windows</c>; the combo shows <c>Windows theme</c>.</summary>
    private static string BackgroundColorModeToComboDisplayLabel(string? normalizedMode)
    {
        var m = string.IsNullOrWhiteSpace(normalizedMode) ? "Default" : normalizedMode.Trim();
        if (string.Equals(m, "Windows", StringComparison.OrdinalIgnoreCase))
            return "Windows theme";
        return m;
    }

    private void ApplyBackgroundColorSelection(string? mode)
    {
        var label = BackgroundColorModeToComboDisplayLabel(string.IsNullOrWhiteSpace(mode) ? "Default" : mode.Trim());
        foreach (var obj in BackgroundColorModeComboBox.Items)
        {
            if (obj is not ComboBoxItem item)
                continue;
            if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                BackgroundColorModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateBackgroundColorPickEnabled()
    {
        var mode = "Default";
        try
        {
            if (BackgroundColorModeComboBox.SelectedItem is ComboBoxItem item)
                mode = (item.Content as string)?.Trim() ?? "Default";
        }
        catch { /* ignore */ }

        BackgroundColorPickButton.IsEnabled = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        UpdateCustomBackgroundColorPreview();
    }

    private void UpdateCustomBackgroundColorPreview()
    {
        try
        {
            if (CustomBackgroundColorPreviewBorder is null)
                return;
            var hex = (_draft.CustomBackgroundColor ?? "").Trim();
            if (string.IsNullOrEmpty(hex) || !TryParseCustomBackgroundHex(hex, out var dc))
            {
                CustomBackgroundColorPreviewBorder.ClearValue(Border.BackgroundProperty);
                CustomBackgroundColorPreviewBorder.SetResourceReference(Border.BackgroundProperty, "App.Theme.SurfaceRaised");
                CustomBackgroundColorPreviewBorder.ClearValue(FrameworkElement.ToolTipProperty);
                return;
            }

            var wc = System.Windows.Media.Color.FromRgb(dc.R, dc.G, dc.B);
            var b = new System.Windows.Media.SolidColorBrush(wc);
            try { b.Freeze(); } catch { /* ignore */ }
            CustomBackgroundColorPreviewBorder.Background = b;
            CustomBackgroundColorPreviewBorder.ClearValue(FrameworkElement.ToolTipProperty);
        }
        catch { /* ignore */ }
    }

    private void ApplyAutoRefreshSelection(int? minutes)
    {
        if (PlaylistAutoRefreshComboBox is null)
            return;

        string label = minutes switch
        {
            1 => "1 minute",
            5 => "5 minutes",
            30 => "30 minutes",
            _ => "Disabled"
        };

        try
        {
            _suppressAutoRefreshEvent = true;
            foreach (var obj in PlaylistAutoRefreshComboBox.Items)
            {
                if (obj is not ComboBoxItem item)
                    continue;
                if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
                {
                    PlaylistAutoRefreshComboBox.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _suppressAutoRefreshEvent = false;
        }
    }

    private void YtDlpBrowseButton_OnClick(object sender, RoutedEventArgs e)
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
            var current = _getYtDlpPath();
            if (!string.IsNullOrWhiteSpace(current) && (current.Contains('\\') || current.Contains('/')) && File.Exists(current))
                dlg.InitialDirectory = Path.GetDirectoryName(current);
        }
        catch { }

        if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
            return;

        _draft.YtDlpPath = dlg.FileName;
        RefreshUi();
    }

    private void FfmpegBrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ffmpeg executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            var current = _getFfmpegPath();
            if (!string.IsNullOrWhiteSpace(current) && (current.Contains('\\') || current.Contains('/')) && File.Exists(current))
                dlg.InitialDirectory = Path.GetDirectoryName(current);
        }
        catch { }

        if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
            return;

        _draft.FfmpegPath = dlg.FileName;
        RefreshUi();
    }

    /// <summary>
    /// "Use PATH" clears the draft only when the tool exists on PATH; otherwise the draft stays as-is (no broken readout in-session).
    /// </summary>
    private void TrySwitchToolDraftToPath(ref string draftPathField, string pathProbeName, string displayNameForMessage)
    {
        var previous = draftPathField ?? "";
        draftPathField = "";
        if (ToolPathResolver.Resolve(null, pathProbeName).IsFound)
        {
            RefreshUi();
            return;
        }

        draftPathField = previous;
        if (!string.IsNullOrWhiteSpace(previous))
        {
            System.Windows.MessageBox.Show(this,
                $"{displayNameForMessage} was not found on PATH. The path shown in Options is unchanged.",
                "Options",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        RefreshUi();
    }

    private void YtDlpUsePathButton_OnClick(object sender, RoutedEventArgs e)
        => TrySwitchToolDraftToPath(ref _draft.YtDlpPath, "yt-dlp", "yt-dlp");

    private void FfmpegUsePathButton_OnClick(object sender, RoutedEventArgs e)
        => TrySwitchToolDraftToPath(ref _draft.FfmpegPath, "ffmpeg", "ffmpeg");

    private void NodeBrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select node.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            var current = _draft.NodeJsPath;
            if (!string.IsNullOrWhiteSpace(current) && (current.Contains('\\') || current.Contains('/')) && File.Exists(current))
                dlg.InitialDirectory = Path.GetDirectoryName(current);
        }
        catch { }

        if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
            return;

        _draft.NodeJsPath = dlg.FileName;
        RefreshUi();
    }

    private void NodeUsePathButton_OnClick(object sender, RoutedEventArgs e)
        => TrySwitchToolDraftToPath(ref _draft.NodeJsPath, "node", "Node.js");

    private void EjsGithubRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        if (EjsGithubRadio.IsChecked == true)
            _draft.YtdlpEjsComponentSource = "github";
    }

    private void EjsBundledRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        if (EjsBundledRadio.IsChecked == true)
            _draft.YtdlpEjsComponentSource = "bundled";
    }

    private void YoutubeCookiesCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.YoutubeCookiesEnabled = true;
        try
        {
            var nodeOk = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.NodeJsPath) ? null : _draft.NodeJsPath, "node").IsFound;
            YoutubeCookiesTextBox.IsEnabled = nodeOk;
        }
        catch { /* ignore */ }
    }

    private void YoutubeCookiesCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.YoutubeCookiesEnabled = false;
        try { YoutubeCookiesTextBox.IsEnabled = false; } catch { /* ignore */ }
    }

    private void YoutubeCookiesTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try { _draft.YoutubeCookiesText = YoutubeCookiesTextBox.Text ?? ""; } catch { /* ignore */ }
    }

    private void OptionsTabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptionsTabSelection)
            return;
        try
        {
            if (OptionsTabControl.SelectedItem is not TabItem ti)
                return;
            var h = ti.Header as string;
            if (string.IsNullOrWhiteSpace(h))
                return;
            var norm = SettingsStore.NormalizeOptionsWindowSelectedTab(h);
            _draft.OptionsSelectedTab = norm;
            _setOptionsSelectedTab(norm);
        }
        catch { /* ignore */ }
    }

    private void PlaylistAutoRefreshComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressAutoRefreshEvent)
            return;
        try
        {
            if (PlaylistAutoRefreshComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
                return;

            var text = (item.Content as string)?.Trim();
            int? minutes = text switch
            {
                "Disabled" => null,
                "1 minute" => 1,
                "5 minutes" => 5,
                "30 minutes" => 30,
                _ => null,
            };

            _draft.PlaylistAutoRefreshMinutes = minutes;
        }
        catch { }
    }

    private void PlaylistAutoRefreshComboBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            AppLog.Info(
                $"Options AutoRefresh Combo click: IsEnabled={PlaylistAutoRefreshComboBox.IsEnabled} " +
                $"IsHitTestVisible={PlaylistAutoRefreshComboBox.IsHitTestVisible}",
                AppLogInfoTier.Diagnostic);
        }
        catch { /* ignore */ }
    }

    private void OpenLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetLogPopoutOpen(true);
            _showLog();
        }
        catch
        {
            SetLogPopoutOpen(false);
        }
    }

    public void SetLogPopoutOpen(bool isOpen)
    {
        _logPopoutOpen = isOpen;
        try { EmbeddedLogViewer?.SetSuspended(isOpen); } catch { /* ignore */ }
    }

    private void GlobalMediaKeysCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.GlobalMediaKeysEnabled = true;
    }

    private void GlobalMediaKeysCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.GlobalMediaKeysEnabled = false;
    }

    private void SaveThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save theme",
                Filter = "Theme JSON (*.json)|*.json|All files (*.*)|*.*",
                AddExtension = true,
                DefaultExt = ".json",
                FileName = "theme.json",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            var theme = new LyllyPlayer.Models.ThemeSettings(
                ThemeMode: _draft.ThemeMode,
                BackgroundMode: _draft.BackgroundMode,
                CustomBackgroundImagePath: _draft.CustomBackgroundImagePath,
                BackgroundColorMode: _draft.BackgroundColorMode,
                CustomBackgroundColor: _draft.CustomBackgroundColor,
                BackgroundAlpha: _draft.BackgroundAlpha,
                BackgroundScrimPercent: _draft.BackgroundScrimPercent,
                BackgroundImageStretch: SettingsStore.NormalizeBackgroundImageStretch(_draft.BackgroundImageStretch),
                AppTitleMode: _draft.AppTitleMode,
                CustomAppTitle: _draft.CustomAppTitle,
                UiScalePercent: _draft.UiScalePercent,
                WindowBorderMode: _draft.WindowBorderMode,
                WindowBorderCustomPx: _draft.WindowBorderCustomPx
            );
            var json = System.Text.Json.JsonSerializer.Serialize(theme, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
        }
        catch { /* ignore */ }
    }

    private void LoadThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load theme",
                Filter = "Theme JSON (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            string json;
            try
            {
                json = SafeJson.ReadUtf8TextForJson(dlg.FileName, SafeJson.MaxThemeImportFileBytes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(
                    this,
                    ex.Message,
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Quick schema sanity check: theme deserialization allows missing/unknown fields, so a non-theme JSON can
            // deserialize into an all-null ThemeSettings without throwing. Detect that early and show feedback.
            try
            {
                using var doc = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "This file is valid JSON, but it is not a theme file (expected a JSON object at the root).",
                        "Load theme",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                static bool HasAnyThemeKey(JsonElement root)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        var n = p.Name;
                        if (string.Equals(n, "ThemeMode", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "BackgroundMode", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "CustomBackgroundImagePath", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "BackgroundColorMode", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "CustomBackgroundColor", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "BackgroundAlpha", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "BackgroundScrimPercent", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "BackgroundImageStretch", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "AppTitleMode", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "CustomAppTitle", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "UiScalePercent", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "WindowBorderMode", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "WindowBorderCustomPx", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                }

                if (!HasAnyThemeKey(doc.RootElement))
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "This file is valid JSON, but it doesn't look like a LyllyPlayer theme file.\n\n" +
                        "Tip: theme files typically contain keys like ThemeMode, BackgroundColorMode, BackgroundAlpha, UiScalePercent, etc.",
                        "Load theme",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            catch (JsonException)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "This file is not valid JSON.",
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            LyllyPlayer.Models.ThemeSettings? theme;
            try
            {
                theme = JsonSerializer.Deserialize<LyllyPlayer.Models.ThemeSettings>(
                    json,
                    SafeJson.CreateDeserializerOptions());
            }
            catch (JsonException)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "This file is not valid JSON or does not match the LyllyPlayer theme format.",
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (theme is null)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "The theme file could not be read.",
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (theme.ThemeMode is null
                && theme.BackgroundMode is null
                && theme.CustomBackgroundImagePath is null
                && theme.BackgroundColorMode is null
                && theme.CustomBackgroundColor is null
                && theme.BackgroundAlpha is null
                && theme.BackgroundScrimPercent is null
                && theme.BackgroundImageStretch is null
                && theme.AppTitleMode is null
                && theme.CustomAppTitle is null
                && theme.UiScalePercent is null
                && theme.WindowBorderMode is null
                && theme.WindowBorderCustomPx is null)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "This file deserialized, but it doesn't contain any theme settings to apply.",
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _draft.ThemeMode = SettingsStore.NormalizeThemeMode(theme.ThemeMode ?? _draft.ThemeMode);
            _draft.BackgroundMode = string.IsNullOrWhiteSpace(theme.BackgroundMode) ? _draft.BackgroundMode : theme.BackgroundMode.Trim();
            _draft.CustomBackgroundImagePath = theme.CustomBackgroundImagePath ?? _draft.CustomBackgroundImagePath;
            _draft.BackgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(theme.BackgroundColorMode ?? _draft.BackgroundColorMode);
            _draft.CustomBackgroundColor = theme.CustomBackgroundColor ?? _draft.CustomBackgroundColor;
            if (theme.BackgroundAlpha is >= 0 and <= 255) _draft.BackgroundAlpha = theme.BackgroundAlpha.Value;
            if (theme.BackgroundScrimPercent is >= 0 and <= 80) _draft.BackgroundScrimPercent = theme.BackgroundScrimPercent.Value;
            _draft.BackgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(theme.BackgroundImageStretch ?? _draft.BackgroundImageStretch);
            _draft.AppTitleMode = string.IsNullOrWhiteSpace(theme.AppTitleMode) ? _draft.AppTitleMode : theme.AppTitleMode.Trim();
            _draft.CustomAppTitle = theme.CustomAppTitle ?? _draft.CustomAppTitle;
            if (theme.UiScalePercent is >= 50 and <= 200) _draft.UiScalePercent = theme.UiScalePercent.Value;
            if (!string.IsNullOrWhiteSpace(theme.WindowBorderMode))
            {
                var wbm = theme.WindowBorderMode.Trim();
                _draft.WindowBorderMode = string.Equals(wbm, "Custom", StringComparison.OrdinalIgnoreCase)
                    ? "1px"
                    : wbm;
            }
            if (theme.WindowBorderCustomPx is not null)
                _draft.WindowBorderCustomPx = Math.Clamp(theme.WindowBorderCustomPx.Value, 1, 24);
            RefreshUi();

            // If load succeeded, apply immediately (theme-only fields) so the user sees the result right away.
            ApplyDraftThemeOnlyNow();
        }
        catch (Exception ex)
        {
            try
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"Theme load failed.\n\n{ex.Message}",
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
    }

    private void ApplyDraftThemeOnlyNow()
    {
        var failures = new List<string>();
        void TryApply(string label, Action apply)
        {
            try { apply(); }
            catch (Exception ex) { failures.Add($"{label}: {ex.Message}"); }
        }

        TryApply("Theme mode", () => _setThemeMode(SettingsStore.NormalizeThemeMode(_draft.ThemeMode)));
        TryApply("Background mode", () => _setBackgroundMode((_draft.BackgroundMode ?? "Default").Trim()));
        TryApply("Custom background path", () => _setCustomBackgroundImagePath(_draft.CustomBackgroundImagePath ?? ""));
        TryApply("Color scheme", () => _setBackgroundColorMode(SettingsStore.NormalizeBackgroundColorMode(_draft.BackgroundColorMode)));
        TryApply("Custom background color", () => _setCustomBackgroundColor(_draft.CustomBackgroundColor ?? ""));
        TryApply("Overall UI opacity", () => _setBackgroundAlpha(Math.Clamp(_draft.BackgroundAlpha, 0, 255)));
        TryApply("Background scrim", () => _setBackgroundScrimPercent(Math.Clamp(_draft.BackgroundScrimPercent, 0, 80)));
        TryApply("Background image stretch", () => _setBackgroundImageStretch(SettingsStore.NormalizeBackgroundImageStretch(_draft.BackgroundImageStretch)));
        TryApply("Background crop (Main default)", () => _setBackgroundUserDefinedMainNormal(_draft.BackgroundUserDefinedMainNormal));
        TryApply("Background crop (Main compact)", () => _setBackgroundUserDefinedMainCompact(_draft.BackgroundUserDefinedMainCompact));
        TryApply("Background crop (Main ultra)", () => _setBackgroundUserDefinedMainUltra(_draft.BackgroundUserDefinedMainUltra));
        TryApply("Background crop (Playlist)", () => _setBackgroundUserDefinedPlaylist(_draft.BackgroundUserDefinedPlaylist));
        TryApply("Background crop (Options/Log)", () => _setBackgroundUserDefinedOptionsLog(_draft.BackgroundUserDefinedOptionsLog));
        TryApply("Background crop (Lyrics)", () => _setBackgroundUserDefinedLyrics(_draft.BackgroundUserDefinedLyrics));
        TryApply("Title mode", () => _setAppTitleMode((_draft.AppTitleMode ?? "Default").Trim()));
        TryApply("Custom title", () => _setCustomAppTitle(_draft.CustomAppTitle ?? ""));
        TryApply("UI scale", () => _setUiScalePercent(Math.Clamp(_draft.UiScalePercent, 50, 200)));
        TryApply("Window border mode", () => _setWindowBorderMode(string.IsNullOrWhiteSpace(_draft.WindowBorderMode) ? "None" : _draft.WindowBorderMode.Trim()));
        TryApply("Window border size", () => _setWindowBorderCustomPx(Math.Clamp((int)Math.Round(_draft.WindowBorderCustomPx), 1, 24)));

        // Persist once after applying (avoid many incremental saves).
        TryApply("Save settings", () => _persistSettingsNow?.Invoke());

        // Sync the draft back from live state so controls reflect any normalization.
        TryApply("Refresh options UI", () =>
        {
            LoadDraftFromCurrent();
            RefreshUi();
        });

        if (failures.Count > 0)
        {
            try
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Theme loaded, but some settings could not be applied:\n\n- " + string.Join("\n- ", failures),
                    "Load theme",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
    }

    private void ApplyWindowBorderModeSelection(string? mode)
    {
        var label = string.IsNullOrWhiteSpace(mode) ? "None" : mode.Trim();
        foreach (var obj in WindowBorderModeComboBox.Items)
        {
            if (obj is not ComboBoxItem item)
                continue;
            if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                WindowBorderModeComboBox.SelectedItem = item;
                return;
            }
        }
        WindowBorderModeComboBox.SelectedIndex = 0;
    }

    private void UpdateWindowBorderCustomEnabled()
    {
        WindowBorderCustomPxPanel.Visibility = Visibility.Collapsed;
        WindowBorderCustomPxSlider.IsEnabled = false;
        WindowBorderCustomPxValueTextBlock.IsEnabled = false;
    }

    private void WindowBorderModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (WindowBorderModeComboBox.SelectedItem is not ComboBoxItem item)
                return;
            var m = (item.Content as string)?.Trim() ?? "None";
            _draft.WindowBorderMode = m;
            UpdateWindowBorderCustomEnabled();
        }
        catch { /* ignore */ }
    }

    private void WindowBorderCustomPxSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var v = (int)Math.Round(WindowBorderCustomPxSlider.Value);
            v = Math.Clamp(v, 1, 24);
            WindowBorderCustomPxValueTextBlock.Text = $"{v} px";
            _draft.WindowBorderCustomPx = v;
        }
        catch { /* ignore */ }
    }

    private void SearchDefaultCountTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var s = (SearchDefaultCountTextBox.Text ?? "").Trim();
            if (!int.TryParse(s, out var n))
                return;
            n = Math.Clamp(n, 1, 200);
            _draft.SearchDefaultCount = n;
            SearchDefaultCountTextBox.Text = n.ToString();
        }
        catch { /* ignore */ }
    }

    private void SearchMinLengthComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var idx = SearchMinLengthComboBox.SelectedIndex;
            var min = idx switch
            {
                1 => 30,
                2 => 60,
                3 => 120,
                _ => 0
            };
            _draft.SearchMinLengthSeconds = min;
        }
        catch { /* ignore */ }
    }

    private void IncludeSubfoldersOnFolderLoadCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.IncludeSubfoldersOnFolderLoad = true;
    }

    private void IncludeSubfoldersOnFolderLoadCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.IncludeSubfoldersOnFolderLoad = false;
    }

    private void ReadMetadataOnLoadCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ReadMetadataOnLoad = true;
    }

    private void ReadMetadataOnLoadCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ReadMetadataOnLoad = false;
    }

    private void KeepIncompletePlaylistOnCancelCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.KeepIncompletePlaylistOnCancel = true;
    }

    private void KeepIncompletePlaylistOnCancelCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.KeepIncompletePlaylistOnCancel = false;
    }

    private void LyricsEnabledCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.LyricsEnabled = true;
    }

    private void LyricsEnabledCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.LyricsEnabled = false;
    }

    private void LyricsLocalFilesEnabledCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.LyricsLocalFilesEnabled = true;
    }

    private void LyricsLocalFilesEnabledCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.LyricsLocalFilesEnabled = false;
    }

    private void ExportM3uIncludeYoutubeCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uIncludeYoutube = true;
        try { _setExportM3uIncludeYoutube(true); } catch { /* ignore */ }
    }

    private void ExportM3uIncludeYoutubeCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uIncludeYoutube = false;
        try { _setExportM3uIncludeYoutube(false); } catch { /* ignore */ }
    }

    private void ExportM3uPreferRelativePathsCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uPreferRelativePaths = true;
        try { _setExportM3uPreferRelativePaths(true); } catch { /* ignore */ }
    }

    private void ExportM3uPreferRelativePathsCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uPreferRelativePaths = false;
        try { _setExportM3uPreferRelativePaths(false); } catch { /* ignore */ }
    }

    private void ExportM3uIncludeLyllyMetadataCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uIncludeLyllyMetadata = true;
        try { _setExportM3uIncludeLyllyMetadata(true); } catch { /* ignore */ }
    }

    private void ExportM3uIncludeLyllyMetadataCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.ExportM3uIncludeLyllyMetadata = false;
        try { _setExportM3uIncludeLyllyMetadata(false); } catch { /* ignore */ }
    }

    private void AlwaysOnTopPlaylistWindowCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopPlaylistWindow = true;
        try { _setAlwaysOnTopPlaylistWindow(true); } catch { /* ignore */ }
    }

    private void AlwaysOnTopPlaylistWindowCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopPlaylistWindow = false;
        try { _setAlwaysOnTopPlaylistWindow(false); } catch { /* ignore */ }
    }

    private void AlwaysOnTopOptionsWindowCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopOptionsWindow = true;
        try { _setAlwaysOnTopOptionsWindow(true); } catch { /* ignore */ }
    }

    private void AlwaysOnTopOptionsWindowCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopOptionsWindow = false;
        try { _setAlwaysOnTopOptionsWindow(false); } catch { /* ignore */ }
    }

    private void AlwaysOnTopLyricsWindowCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopLyricsWindow = true;
        try { _setAlwaysOnTopLyricsWindow(true); } catch { /* ignore */ }
    }

    private void AlwaysOnTopLyricsWindowCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.AlwaysOnTopLyricsWindow = false;
        try { _setAlwaysOnTopLyricsWindow(false); } catch { /* ignore */ }
    }

    private void CompactHidesAuxWindowsCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.CompactModeHidesAuxWindows = true;
        try { _setCompactModeHidesAuxWindows(true); } catch { /* ignore */ }
    }

    private void CompactHidesAuxWindowsCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.CompactModeHidesAuxWindows = false;
        try { _setCompactModeHidesAuxWindows(false); } catch { /* ignore */ }
    }

    private void CompactLayoutNormalRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.CompactModeLayout = "Normal";
        try { _setCompactModeLayout("Normal"); } catch { /* ignore */ }
    }

    private void CompactLayoutUltraRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        _draft.CompactModeLayout = "Ultra";
        try { _setCompactModeLayout("Ultra"); } catch { /* ignore */ }
    }

    private void AppIconTaskbarAndTrayRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try { _draft.AppIconVisibility = "TaskbarAndTray"; } catch { /* ignore */ }
    }

    private void AppIconTaskbarOnlyRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try { _draft.AppIconVisibility = "TaskbarOnly"; } catch { /* ignore */ }
    }

    private void AppIconTrayOnlyRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try { _draft.AppIconVisibility = "TrayOnly"; } catch { /* ignore */ }
    }

    private void AudioQualityComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        if (AudioQualityComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _draft.AudioQuality = tag;
    }

    private void AppLogLevelComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        if (AppLogLevelComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _draft.AppLogLevel = tag;
    }

    private void AudioOutputDeviceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        if (AudioOutputDeviceComboBox.SelectedItem is ComboBoxItem item)
            _draft.AudioOutputDevice = item.Tag as string; // null = Default
    }

    private void BackgroundModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (BackgroundModeComboBox.SelectedItem is not ComboBoxItem item)
                return;
            var mode = (item.Content as string)?.Trim() ?? "Default";
            _draft.BackgroundMode = mode;
            UpdateBackgroundBrowseEnabled();
            CoerceBackgroundColorModeIfNoImageBackground();
            UpdateBackgroundImageDerivedControls();
            try { UpdateBackgroundColorPickEnabled(); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    private void BackgroundImageStretchComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (BackgroundImageStretchComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                _draft.BackgroundImageStretch = SettingsStore.NormalizeBackgroundImageStretch(tag);
        }
        catch { /* ignore */ }
        try { UpdateBackgroundImageDerivedControls(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Reads the currently selected Background image stretch Tag from the UI (not the draft snapshot).
    /// Used to keep <see cref="MainWindow"/> in sync when the user opens the designer before clicking Options Apply.
    /// </summary>
    public string? TryGetBackgroundImageStretchUiTag()
    {
        try
        {
            if (BackgroundImageStretchComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
        }
        catch { /* ignore */ }
        return null;
    }

    private void BackgroundDesignerButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _openBackgroundDesigner?.Invoke();
        }
        catch (Exception ex)
        {
            try
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Background designer failed to open.\n\n" + ex.Message,
                    "Options",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
    }

    public void UpdateBackgroundDesignerDraft(LyllyPlayer.Settings.RectN mainNormal, LyllyPlayer.Settings.RectN mainCompact, LyllyPlayer.Settings.RectN mainUltra, LyllyPlayer.Settings.RectN playlist, LyllyPlayer.Settings.RectN optionsLog, LyllyPlayer.Settings.RectN lyrics)
    {
        try
        {
            _draft.BackgroundUserDefinedMainNormal = mainNormal;
            _draft.BackgroundUserDefinedMainCompact = mainCompact;
            _draft.BackgroundUserDefinedMainUltra = mainUltra;
            _draft.BackgroundUserDefinedPlaylist = playlist;
            _draft.BackgroundUserDefinedOptionsLog = optionsLog;
            _draft.BackgroundUserDefinedLyrics = lyrics;
        }
        catch { /* ignore */ }
    }

    public (LyllyPlayer.Settings.RectN mainNormal, LyllyPlayer.Settings.RectN mainCompact, LyllyPlayer.Settings.RectN mainUltra, LyllyPlayer.Settings.RectN playlist, LyllyPlayer.Settings.RectN optionsLog, LyllyPlayer.Settings.RectN lyrics)
        GetBackgroundDesignerDraft()
    {
        try
        {
            return (
                _draft.BackgroundUserDefinedMainNormal,
                _draft.BackgroundUserDefinedMainCompact,
                _draft.BackgroundUserDefinedMainUltra,
                _draft.BackgroundUserDefinedPlaylist,
                _draft.BackgroundUserDefinedOptionsLog,
                _draft.BackgroundUserDefinedLyrics
            );
        }
        catch
        {
            return (
                LyllyPlayer.Settings.RectN.Full,
                LyllyPlayer.Settings.RectN.Full,
                LyllyPlayer.Settings.RectN.Full,
                LyllyPlayer.Settings.RectN.Full,
                LyllyPlayer.Settings.RectN.Full,
                LyllyPlayer.Settings.RectN.Full
            );
        }
    }

    private void BackgroundBrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select background image",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            try
            {
                var current = _getCustomBackgroundImagePath();
                if (!string.IsNullOrWhiteSpace(current) && (current.Contains('\\') || current.Contains('/')) && File.Exists(current))
                    dlg.InitialDirectory = Path.GetDirectoryName(current);
            }
            catch { /* ignore */ }

            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            _draft.CustomBackgroundImagePath = dlg.FileName;
            RefreshUi();
        }
        catch { /* ignore */ }
    }

    private void BackgroundColorModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (BackgroundColorModeComboBox.SelectedItem is not ComboBoxItem item)
                return;
            var mode = (item.Content as string)?.Trim() ?? "Default";
            _draft.BackgroundColorMode = SettingsStore.NormalizeBackgroundColorMode(mode);
            UpdateBackgroundColorPickEnabled();
        }
        catch { /* ignore */ }
    }

    private void BackgroundColorPickButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dlg = new Forms.ColorDialog
            {
                FullOpen = true,
            };

            // Seed from draft (this session) or last saved value so the picker reopens on the previous choice.
            var hexHint = (_draft.CustomBackgroundColor ?? "").Trim();
            if (string.IsNullOrEmpty(hexHint))
                hexHint = (_getCustomBackgroundColor() ?? "").Trim();
            if (TryParseCustomBackgroundHex(hexHint, out var initial))
                dlg.Color = initial;

            var owner = new Win32OwnerWrapper(new System.Windows.Interop.WindowInteropHelper(GetDialogOwnerWindow()).Handle);
            if (dlg.ShowDialog(owner) != Forms.DialogResult.OK)
                return;

            var c = dlg.Color;
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _draft.CustomBackgroundColor = hex;
            RefreshUi();
        }
        catch { /* ignore */ }
    }

    private static bool TryParseCustomBackgroundHex(string? s, out Color c)
    {
        c = Color.Black;
        var t = (s ?? "").Trim();
        if (string.IsNullOrEmpty(t))
            return false;
        if (t[0] != '#')
            t = "#" + t;
        try
        {
            c = ColorTranslator.FromHtml(t);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void BackgroundAlphaSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var v = (int)Math.Round(BackgroundAlphaSlider.Value);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            BackgroundAlphaValueTextBlock.Text = $"{(int)Math.Round(v / 255.0 * 100)}%";
            _draft.BackgroundAlpha = v;
        }
        catch { /* ignore */ }
    }

    private void BackgroundScrimSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var v = (int)Math.Round(BackgroundScrimSlider.Value);
            v = Math.Clamp(v, 0, 80);
            BackgroundScrimValueTextBlock.Text = $"{v}%";
            _draft.BackgroundScrimPercent = v;
        }
        catch { /* ignore */ }
    }

    private void ApplyAppTitleModeSelection(string? mode)
    {
        var label = string.IsNullOrWhiteSpace(mode) ? "Default" : mode.Trim();
        foreach (var obj in AppTitleModeComboBox.Items)
        {
            if (obj is not ComboBoxItem item)
                continue;
            if (string.Equals((item.Content as string)?.Trim(), label, StringComparison.OrdinalIgnoreCase))
            {
                AppTitleModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateCustomAppTitleEnabled()
    {
        var mode = "Default";
        try
        {
            if (AppTitleModeComboBox.SelectedItem is ComboBoxItem item)
                mode = (item.Content as string)?.Trim() ?? "Default";
        }
        catch { /* ignore */ }

        // Keep TextBox enabled so the app theme Background isn't replaced by the default
        // Aero disabled template (IsEnabled=false forces a system ControlBrush background).
        var isCustom = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        try
        {
            CustomAppTitleTextBox.ClearValue(UIElement.IsEnabledProperty);
            CustomAppTitleTextBox.IsEnabled = true;
        }
        catch { /* ignore */ }
        CustomAppTitleTextBox.IsReadOnly = !isCustom;
        CustomAppTitleTextBox.Opacity = isCustom ? 1.0 : 0.45;
        CustomAppTitleTextBox.IsTabStop = isCustom;
        CustomAppTitleTextBox.Focusable = isCustom;
    }

    private void AppTitleModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            if (AppTitleModeComboBox.SelectedItem is not ComboBoxItem item)
                return;
            var mode = (item.Content as string)?.Trim() ?? "Default";
            _draft.AppTitleMode = mode;
            UpdateCustomAppTitleEnabled();
        }
        catch { /* ignore */ }
    }

    private void CustomAppTitleTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            _draft.CustomAppTitle = CustomAppTitleTextBox.Text ?? "";
        }
        catch { /* ignore */ }
    }

    private void UiScaleSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressBackgroundUiEvents)
            return;
        try
        {
            var v = (int)Math.Round(UiScaleSlider.Value);
            if (v < 50) v = 50;
            if (v > 200) v = 200;
            UiScaleValueTextBlock.Text = $"{v}%";
            _draft.UiScalePercent = v;
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Draft empty means "Use PATH". If the tool is not on PATH, revert the draft to the current saved path
    /// so Apply does not persist <c>null</c> and wipe a previously valid explicit path.
    /// </summary>
    private static void RevertDraftPathIfPathIntentWouldMiss(ref string draftPath, Func<string> getSavedPath, string pathProbeName, ICollection<string> warningLabels, string warningLabel)
    {
        if (!string.IsNullOrWhiteSpace((draftPath ?? "").Trim()))
            return;
        if (ToolPathResolver.Resolve(null, pathProbeName).IsFound)
            return;
        var saved = (getSavedPath() ?? "").Trim();
        // If there was no previously saved path, don't warn: the tool is simply unset and not on PATH.
        if (string.IsNullOrWhiteSpace(saved))
            return;
        draftPath = saved;
        warningLabels.Add(warningLabel);
    }

    private bool TryApplyDraft()
    {
        try
        {
            // Parse/validate free-text fields.
            var cacheText = (CacheMaxMbTextBox.Text ?? "").Trim();
            if (int.TryParse(cacheText, out var mb))
                _draft.CacheMaxMb = Math.Clamp(mb, 16, 102400);

            var logMbText = (AppLogMaxMbTextBox.Text ?? "").Trim();
            if (int.TryParse(logMbText, out var logMb))
                _draft.AppLogMaxMb = Math.Clamp(logMb, 1, 200);

            var sCount = (SearchDefaultCountTextBox.Text ?? "").Trim();
            if (int.TryParse(sCount, out var cnt))
                _draft.SearchDefaultCount = Math.Clamp(cnt, 1, 200);
        }
        catch { /* ignore */ }

        try
        {
            var nodeOkApply = ToolPathResolver.Resolve(string.IsNullOrWhiteSpace(_draft.NodeJsPath) ? null : _draft.NodeJsPath, "node").IsFound;
            if (nodeOkApply && _draft.YoutubeCookiesEnabled && string.IsNullOrWhiteSpace((_draft.YoutubeCookiesText ?? "").Trim()))
            {
                System.Windows.MessageBox.Show(this, "Enter a value for cookies from browser (see yt-dlp --help), or turn off the option.", "Options",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        catch { /* ignore */ }

        try
        {
            var pathWarnings = new List<string>();
            RevertDraftPathIfPathIntentWouldMiss(ref _draft.YtDlpPath, _getYtDlpPath, "yt-dlp", pathWarnings, "yt-dlp");
            RevertDraftPathIfPathIntentWouldMiss(ref _draft.FfmpegPath, _getFfmpegPath, "ffmpeg", pathWarnings, "ffmpeg");
            RevertDraftPathIfPathIntentWouldMiss(ref _draft.NodeJsPath, _getNodeJsPath, "node", pathWarnings, "Node.js");
            if (pathWarnings.Count > 0)
            {
                System.Windows.MessageBox.Show(this,
                    "Not on PATH: " + string.Join(", ", pathWarnings) + ". Those tools keep the previously saved paths (if any). Other changes below are still applied.",
                    "Options",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch { /* ignore */ }

        try { _setYtDlpPath(_draft.YtDlpPath ?? ""); } catch { /* ignore */ }
        try { _setFfmpegPath(_draft.FfmpegPath ?? ""); } catch { /* ignore */ }
        try { _setNodeJsPath(_draft.NodeJsPath ?? ""); } catch { /* ignore */ }
        try { _setYtdlpEjsComponentSource(_draft.YtdlpEjsComponentSource ?? "github"); } catch { /* ignore */ }
        try { _setYoutubeCookiesFromBrowserEnabled(_draft.YoutubeCookiesEnabled); } catch { /* ignore */ }
        try { _setYoutubeCookiesFromBrowser(_draft.YoutubeCookiesText ?? ""); } catch { /* ignore */ }
        try { _setCacheMaxMb(Math.Clamp(_draft.CacheMaxMb, 16, 102400)); } catch { /* ignore */ }
        try { _setPlaylistAutoRefreshMinutes(_draft.PlaylistAutoRefreshMinutes); } catch { /* ignore */ }
        try { _setGlobalMediaKeysEnabled(_draft.GlobalMediaKeysEnabled); } catch { /* ignore */ }

        try { _setThemeMode(SettingsStore.NormalizeThemeMode(_draft.ThemeMode)); } catch { /* ignore */ }
        try { _setBackgroundMode((_draft.BackgroundMode ?? "Default").Trim()); } catch { /* ignore */ }
        try { _setCustomBackgroundImagePath(_draft.CustomBackgroundImagePath ?? ""); } catch { /* ignore */ }
        try { _setBackgroundColorMode(SettingsStore.NormalizeBackgroundColorMode(_draft.BackgroundColorMode)); } catch { /* ignore */ }
        try { _setCustomBackgroundColor(_draft.CustomBackgroundColor ?? ""); } catch { /* ignore */ }
        try { _setBackgroundAlpha(Math.Clamp(_draft.BackgroundAlpha, 0, 255)); } catch { /* ignore */ }
        try { _setBackgroundScrimPercent(Math.Clamp(_draft.BackgroundScrimPercent, 0, 80)); } catch { /* ignore */ }
        try { _setBackgroundImageStretch(SettingsStore.NormalizeBackgroundImageStretch(_draft.BackgroundImageStretch)); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedMainNormal(_draft.BackgroundUserDefinedMainNormal); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedMainCompact(_draft.BackgroundUserDefinedMainCompact); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedMainUltra(_draft.BackgroundUserDefinedMainUltra); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedPlaylist(_draft.BackgroundUserDefinedPlaylist); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedOptionsLog(_draft.BackgroundUserDefinedOptionsLog); } catch { /* ignore */ }
        try { _setBackgroundUserDefinedLyrics(_draft.BackgroundUserDefinedLyrics); } catch { /* ignore */ }
        try { _setAppTitleMode((_draft.AppTitleMode ?? "Default").Trim()); } catch { /* ignore */ }
        try { _setCustomAppTitle(_draft.CustomAppTitle ?? ""); } catch { /* ignore */ }
        try { _setUiScalePercent(Math.Clamp(_draft.UiScalePercent, 50, 200)); } catch { /* ignore */ }

        try { _setWindowBorderMode(string.IsNullOrWhiteSpace(_draft.WindowBorderMode) ? "None" : _draft.WindowBorderMode.Trim()); } catch { /* ignore */ }
        try { _setWindowBorderCustomPx(Math.Clamp((int)Math.Round(_draft.WindowBorderCustomPx), 1, 24)); } catch { /* ignore */ }

        try
        {
            _setSearchDefaults(
                Math.Clamp(_draft.SearchDefaultCount, 1, 200),
                Math.Clamp(_draft.SearchMinLengthSeconds, 0, 3600)
            );
        }
        catch { /* ignore */ }

        try { _setIncludeSubfoldersOnFolderLoad(_draft.IncludeSubfoldersOnFolderLoad); } catch { /* ignore */ }
        try { _setReadMetadataOnLoad(_draft.ReadMetadataOnLoad); } catch { /* ignore */ }
        try { _setAlwaysOnTopPlaylistWindow(_draft.AlwaysOnTopPlaylistWindow); } catch { /* ignore */ }
        try { _setAlwaysOnTopOptionsWindow(_draft.AlwaysOnTopOptionsWindow); } catch { /* ignore */ }
        try { _setCompactModeHidesAuxWindows(_draft.CompactModeHidesAuxWindows); } catch { /* ignore */ }
        try { _setCompactModeLayout(SettingsStore.NormalizeCompactModeLayout(_draft.CompactModeLayout)); } catch { /* ignore */ }
        try { _setKeepIncompletePlaylistOnCancel(_draft.KeepIncompletePlaylistOnCancel); } catch { /* ignore */ }
        try { _setLyricsEnabled(_draft.LyricsEnabled); } catch { /* ignore */ }
        try { _setLyricsLocalFilesEnabled(_draft.LyricsLocalFilesEnabled); } catch { /* ignore */ }
        try { _setExportM3uIncludeYoutube(_draft.ExportM3uIncludeYoutube); } catch { /* ignore */ }
        try { _setExportM3uPreferRelativePaths(_draft.ExportM3uPreferRelativePaths); } catch { /* ignore */ }
        try { _setExportM3uIncludeLyllyMetadata(_draft.ExportM3uIncludeLyllyMetadata); } catch { /* ignore */ }
        try { _setAppIconVisibility(SettingsStore.NormalizeAppIconVisibility(_draft.AppIconVisibility)); } catch { /* ignore */ }
        try { _setAudioQuality(_draft.AudioQuality ?? "Auto"); } catch { /* ignore */ }
        try { _setAudioOutputDevice(string.IsNullOrWhiteSpace(_draft.AudioOutputDevice) ? null : _draft.AudioOutputDevice); } catch { /* ignore */ }
        try { _setAppLogLevel(_draft.AppLogLevel ?? "ErrorsAndWarnings"); } catch { /* ignore */ }
        try { _setAppLogMaxMb(Math.Clamp(_draft.AppLogMaxMb, 1, 200)); } catch { /* ignore */ }
        try { _setOptionsSelectedTab(SettingsStore.NormalizeOptionsWindowSelectedTab(_draft.OptionsSelectedTab)); } catch { /* ignore */ }

        // Save the current tab so RefreshUi() doesn't jump away from it.
        var savedTab = _draft.OptionsSelectedTab;

        LoadDraftFromCurrent();
        RefreshUi();

        // Restore the draft's tab so the UI stays on the user's tab.
        _draft.OptionsSelectedTab = savedTab;

        // One disk write after all callbacks (avoids many synchronous Load+Save cycles on the UI thread during Apply).
        try { _persistSettingsNow?.Invoke(); } catch { /* ignore */ }

        return true;
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryApplyDraft();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TryApplyDraft())
        {
            try { Hide(); } catch { /* ignore */ }
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Hide(); } catch { /* ignore */ }
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Hide(); } catch { }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            _chromeDragging = true;
            _chromeDragStartLeft = Left;
            _chromeDragStartTop = Top;
            _chromeDragStartScreen = PointToScreen(e.GetPosition(this));

            CaptureMouse();
            MouseMove -= ChromeDrag_MouseMove;
            MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
            MouseMove += ChromeDrag_MouseMove;
            MouseLeftButtonUp += ChromeDrag_MouseLeftButtonUp;

            e.Handled = true;
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
        try { ReleaseMouseCapture(); } catch { }
        MouseMove -= ChromeDrag_MouseMove;
        MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
    }
}
