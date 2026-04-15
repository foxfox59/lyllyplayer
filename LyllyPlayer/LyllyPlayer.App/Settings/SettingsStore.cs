using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Settings;

/// <summary>How <see cref="SettingsStore.Load(out SettingsStartupLoadInfo)"/> obtained settings.</summary>
public enum SettingsStartupRecoveryKind
{
    /// <summary>Normal load (strict or lenient JSON deserialize, or empty/valid object via document parse).</summary>
    None,
    /// <summary>No settings file was present.</summary>
    MissingFile,
    /// <summary>File existed but could not be read; defaults are in use.</summary>
    CorruptUsedDefaults,
    /// <summary>JSON was not fully valid for deserialization, but some fields were recovered from the file.</summary>
    PartialRecovery,
}

/// <summary>Outcome of loading <c>settings.json</c> at startup (see <see cref="SettingsStore.Load(out SettingsStartupLoadInfo)"/>).</summary>
public readonly record struct SettingsStartupLoadInfo(
    bool SettingsFileExisted,
    SettingsStartupRecoveryKind RecoveryKind,
    string? LastSavedByAppVersionReadFromFile);

public static class SettingsStore
{
    private const int DefaultCacheMaxMb = 512;
    private const string DefaultRepeatMode = "None";
    private const double DefaultVolume = 0.85;
    private static readonly int? DefaultPlaylistAutoRefreshMinutes = null;
    private const bool DefaultYoutubeCookiesFromBrowserEnabled = false;
    private const string DefaultAudioQuality = "Auto";
    private const string DefaultAppLogLevel = "ErrorsAndWarnings";
    public const int DefaultAppLogMaxMb = 50;
    private const int MinAppLogMaxMb = 1;
    private const int MaxAppLogMaxMb = 200;
    private const bool DefaultGlobalMediaKeysEnabled = true;
    private const bool DefaultIncludeSubfoldersOnFolderLoad = false;
    private const string DefaultLastPlaylistSourceType = "YouTube";
    private const string DefaultBackgroundMode = "Default";
    /// <summary>Default when settings omit color scheme: derive palette from background image when possible.</summary>
    private const string DefaultBackgroundColorMode = "From image";
    /// <summary>Default UI opacity (~50%) when settings.json is missing or keys are absent.</summary>
    public const int DefaultBackgroundAlpha = 128;
    /// <summary>Default image scrim when settings omit it.</summary>
    public const int DefaultBackgroundScrimPercent = 50;
    private const string DefaultBackgroundImageStretch = "BestFit";
    private const string DefaultThemeMode = "Auto";
    private const string DefaultAppTitleMode = "Default";
    private const string DefaultCustomAppTitle = "";
    private const string DefaultAppIconVisibility = "TaskbarOnly";
    private const int DefaultSearchCount = 50;
    private const int DefaultSearchMinLengthSeconds = 0;
    private const bool DefaultReadMetadataOnLoad = false;
    private const bool DefaultKeepIncompletePlaylistOnCancel = false;
    private const int DefaultUiScalePercent = 100;
    private const string DefaultWindowBorderMode = "1px";
    private const double DefaultWindowBorderCustomPx = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonDocumentOptions JsonDocumentLenientOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = SafeJson.MaxDepth,
    };

    public static string GetSettingsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LyllyPlayer"
        );

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load() => Load(out _);

    public static AppSettings Load(out SettingsStartupLoadInfo loadInfo)
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            loadInfo = new SettingsStartupLoadInfo(false, SettingsStartupRecoveryKind.MissingFile, null);
            return DefaultSettings();
        }

        string json;
        try
        {
            json = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxAppSettingsFileBytes);
        }
        catch
        {
            loadInfo = new SettingsStartupLoadInfo(true, SettingsStartupRecoveryKind.CorruptUsedDefaults, null);
            return DefaultSettings();
        }

        var versionHint = TryReadLastSavedByAppVersionLoose(json);

        AppSettings? loaded = null;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json, DeserializeOptions);
        }
        catch
        {
            // Try lenient document parse and regex salvage below.
        }

        if (loaded is not null)
        {
            loaded = PatchFromRawJsonIfNeeded(loaded, json);
            var ok = ApplyDefaults(loaded);
            loadInfo = new SettingsStartupLoadInfo(
                true,
                SettingsStartupRecoveryKind.None,
                ok.LastSavedByAppVersion ?? versionHint);
            return ok;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, JsonDocumentLenientOptions);
            var patched = PatchFromJsonRoot(AllNullSettings(), doc.RootElement);
            var result = ApplyDefaults(patched);
            var partial = patched != AllNullSettings();
            loadInfo = new SettingsStartupLoadInfo(
                true,
                partial ? SettingsStartupRecoveryKind.PartialRecovery : SettingsStartupRecoveryKind.None,
                result.LastSavedByAppVersion ?? versionHint);
            return result;
        }
        catch
        {
            // Fall through to regex salvage.
        }

        var salvaged = TrySalvageSettingsWithRegex(json);
        if (salvaged != AllNullSettings())
        {
            var result = ApplyDefaults(salvaged);
            loadInfo = new SettingsStartupLoadInfo(
                true,
                SettingsStartupRecoveryKind.PartialRecovery,
                result.LastSavedByAppVersion ?? versionHint);
            return result;
        }

        loadInfo = new SettingsStartupLoadInfo(true, SettingsStartupRecoveryKind.CorruptUsedDefaults, versionHint);
        return DefaultSettings();
    }

    private static string? TryReadLastSavedByAppVersionLoose(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json, JsonDocumentLenientOptions);
            if (doc.RootElement.TryGetProperty(nameof(AppSettings.LastSavedByAppVersion), out var p)
                && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
        }
        catch
        {
            // Invalid JSON: try to scrape the field for upgrade messaging.
        }

        var m = Regex.Match(
            json,
            """"LastSavedByAppVersion"\s*:\s*"([^"]*)"""",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        var raw = m.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static AppSettings PatchFromRawJsonIfNeeded(AppSettings loaded, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonDocumentLenientOptions);
            return PatchFromJsonRoot(loaded, doc.RootElement);
        }
        catch
        {
            return loaded;
        }
    }

    private static AppSettings PatchFromJsonRoot(AppSettings loaded, JsonElement root)
    {
        double? GetDouble(string name)
        {
            if (!root.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String
                && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return null;
        }

        int? GetInt(string name)
        {
            if (!root.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String
                && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;
            return null;
        }

        bool? GetBool(string name)
        {
            if (!root.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
            return null;
        }

        string? GetString(string name)
        {
            if (!root.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        List<string>? GetStringList(string name)
        {
            if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var el in p.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
                    list.Add(s);
            }

            return list.Count > 0 ? list : null;
        }

        return loaded with
        {
            YtDlpPath = loaded.YtDlpPath ?? GetString(nameof(AppSettings.YtDlpPath)),
            FfmpegPath = loaded.FfmpegPath ?? GetString(nameof(AppSettings.FfmpegPath)),
            LastPlaylistUrl = loaded.LastPlaylistUrl ?? GetString(nameof(AppSettings.LastPlaylistUrl)),
            LastPlaylistSourceType = loaded.LastPlaylistSourceType ?? GetString(nameof(AppSettings.LastPlaylistSourceType)),
            LastLocalPlaylistPath = loaded.LastLocalPlaylistPath ?? GetString(nameof(AppSettings.LastLocalPlaylistPath)),
            VisualizerMode = loaded.VisualizerMode ?? GetString(nameof(AppSettings.VisualizerMode)),
            ShuffleEnabled = loaded.ShuffleEnabled ?? GetBool(nameof(AppSettings.ShuffleEnabled)),
            GlobalMediaKeysEnabled = loaded.GlobalMediaKeysEnabled ?? GetBool(nameof(AppSettings.GlobalMediaKeysEnabled)),
            RepeatMode = loaded.RepeatMode ?? GetString(nameof(AppSettings.RepeatMode)),
            CurrentVideoId = loaded.CurrentVideoId ?? GetString(nameof(AppSettings.CurrentVideoId)),
            PlayOrderVideoIds = loaded.PlayOrderVideoIds ?? GetStringList(nameof(AppSettings.PlayOrderVideoIds)),
            CurrentPositionSeconds = loaded.CurrentPositionSeconds ?? GetDouble(nameof(AppSettings.CurrentPositionSeconds)),
            WasPlaying = loaded.WasPlaying ?? GetBool(nameof(AppSettings.WasPlaying)),

            WindowLeft = loaded.WindowLeft ?? GetDouble(nameof(AppSettings.WindowLeft)),
            WindowTop = loaded.WindowTop ?? GetDouble(nameof(AppSettings.WindowTop)),
            WindowWidth = loaded.WindowWidth ?? GetDouble(nameof(AppSettings.WindowWidth)),
            WindowHeight = loaded.WindowHeight ?? GetDouble(nameof(AppSettings.WindowHeight)),
            WindowState = loaded.WindowState ?? GetString(nameof(AppSettings.WindowState)),
            ThemeMode = loaded.ThemeMode ?? GetString(nameof(AppSettings.ThemeMode)),
            BackgroundMode = loaded.BackgroundMode ?? GetString(nameof(AppSettings.BackgroundMode)),
            CustomBackgroundImagePath = loaded.CustomBackgroundImagePath ?? GetString(nameof(AppSettings.CustomBackgroundImagePath)),
            BackgroundColorMode = loaded.BackgroundColorMode ?? GetString(nameof(AppSettings.BackgroundColorMode)),
            CustomBackgroundColor = loaded.CustomBackgroundColor ?? GetString(nameof(AppSettings.CustomBackgroundColor)),
            BackgroundAlpha = loaded.BackgroundAlpha ?? GetInt(nameof(AppSettings.BackgroundAlpha)),
            BackgroundScrimPercent = loaded.BackgroundScrimPercent ?? GetInt(nameof(AppSettings.BackgroundScrimPercent)),
            BackgroundImageStretch = loaded.BackgroundImageStretch ?? GetString(nameof(AppSettings.BackgroundImageStretch)),
            AppTitleMode = loaded.AppTitleMode ?? GetString(nameof(AppSettings.AppTitleMode)),
            CustomAppTitle = loaded.CustomAppTitle ?? GetString(nameof(AppSettings.CustomAppTitle)),
            AppIconVisibility = loaded.AppIconVisibility ?? GetString(nameof(AppSettings.AppIconVisibility)),
            SearchDefaultCount = loaded.SearchDefaultCount ?? GetInt(nameof(AppSettings.SearchDefaultCount)),
            SearchMinLengthSeconds = loaded.SearchMinLengthSeconds ?? GetInt(nameof(AppSettings.SearchMinLengthSeconds)),
            ReadMetadataOnLoad = loaded.ReadMetadataOnLoad ?? GetBool(nameof(AppSettings.ReadMetadataOnLoad)),
            UiScalePercent = loaded.UiScalePercent ?? GetInt(nameof(AppSettings.UiScalePercent)),
            WindowBorderMode = loaded.WindowBorderMode ?? GetString(nameof(AppSettings.WindowBorderMode)),
            WindowBorderCustomPx = loaded.WindowBorderCustomPx ?? GetDouble(nameof(AppSettings.WindowBorderCustomPx)),
            NodeJsPath = loaded.NodeJsPath ?? GetString(nameof(AppSettings.NodeJsPath)),
            YtdlpEjsComponentSource = loaded.YtdlpEjsComponentSource ?? GetString(nameof(AppSettings.YtdlpEjsComponentSource)),
            YoutubeCookiesFromBrowserEnabled = loaded.YoutubeCookiesFromBrowserEnabled ?? GetBool(nameof(AppSettings.YoutubeCookiesFromBrowserEnabled)),
            YoutubeCookiesFromBrowser = loaded.YoutubeCookiesFromBrowser ?? GetString(nameof(AppSettings.YoutubeCookiesFromBrowser)),
            AudioQuality = loaded.AudioQuality ?? GetString(nameof(AppSettings.AudioQuality)),
            AudioOutputDevice = loaded.AudioOutputDevice ?? GetString(nameof(AppSettings.AudioOutputDevice)),
            AppLogLevel = loaded.AppLogLevel ?? GetString(nameof(AppSettings.AppLogLevel)),
            AppLogMaxMb = loaded.AppLogMaxMb ?? GetInt(nameof(AppSettings.AppLogMaxMb)),
            MainWindowCompact = loaded.MainWindowCompact ?? GetBool(nameof(AppSettings.MainWindowCompact)),
            CompactModeHidesAuxWindows = loaded.CompactModeHidesAuxWindows ?? GetBool(nameof(AppSettings.CompactModeHidesAuxWindows)),
            KeepIncompletePlaylistOnCancel = loaded.KeepIncompletePlaylistOnCancel ?? GetBool(nameof(AppSettings.KeepIncompletePlaylistOnCancel)),
            LastSavedByAppVersion = loaded.LastSavedByAppVersion ?? GetString(nameof(AppSettings.LastSavedByAppVersion)),

            AlwaysOnTop = loaded.AlwaysOnTop ?? GetBool(nameof(AppSettings.AlwaysOnTop)),
            AlwaysOnTopPlaylistWindow = loaded.AlwaysOnTopPlaylistWindow ?? GetBool(nameof(AppSettings.AlwaysOnTopPlaylistWindow)),
            AlwaysOnTopOptionsWindow = loaded.AlwaysOnTopOptionsWindow ?? GetBool(nameof(AppSettings.AlwaysOnTopOptionsWindow)),

            PlaylistWindowLeft = GetDouble(nameof(AppSettings.PlaylistWindowLeft)) ?? loaded.PlaylistWindowLeft,
            PlaylistWindowTop = GetDouble(nameof(AppSettings.PlaylistWindowTop)) ?? loaded.PlaylistWindowTop,
            PlaylistWindowWidth = GetDouble(nameof(AppSettings.PlaylistWindowWidth)) ?? loaded.PlaylistWindowWidth,
            PlaylistWindowHeight = GetDouble(nameof(AppSettings.PlaylistWindowHeight)) ?? loaded.PlaylistWindowHeight,
            PlaylistWindowState = GetString(nameof(AppSettings.PlaylistWindowState)) ?? loaded.PlaylistWindowState,
            PlaylistWindowOpen = GetBool(nameof(AppSettings.PlaylistWindowOpen)) ?? loaded.PlaylistWindowOpen,
            PlaylistWindowFilter = loaded.PlaylistWindowFilter ?? GetString(nameof(AppSettings.PlaylistWindowFilter)),

            OptionsWindowLeft = GetDouble(nameof(AppSettings.OptionsWindowLeft)) ?? loaded.OptionsWindowLeft,
            OptionsWindowTop = GetDouble(nameof(AppSettings.OptionsWindowTop)) ?? loaded.OptionsWindowTop,
            OptionsWindowWidth = GetDouble(nameof(AppSettings.OptionsWindowWidth)) ?? loaded.OptionsWindowWidth,
            OptionsWindowHeight = GetDouble(nameof(AppSettings.OptionsWindowHeight)) ?? loaded.OptionsWindowHeight,
            OptionsWindowState = GetString(nameof(AppSettings.OptionsWindowState)) ?? loaded.OptionsWindowState,
            OptionsWindowOpen = GetBool(nameof(AppSettings.OptionsWindowOpen)) ?? loaded.OptionsWindowOpen,
            OptionsWindowSnapped = GetBool(nameof(AppSettings.OptionsWindowSnapped)) ?? loaded.OptionsWindowSnapped,
            OptionsWindowSnapEdge = GetString(nameof(AppSettings.OptionsWindowSnapEdge)) ?? loaded.OptionsWindowSnapEdge,
            OptionsWindowDockYOffset = GetDouble(nameof(AppSettings.OptionsWindowDockYOffset)) ?? loaded.OptionsWindowDockYOffset,
            OptionsWindowDockXOffset = GetDouble(nameof(AppSettings.OptionsWindowDockXOffset)) ?? loaded.OptionsWindowDockXOffset,
            OptionsWindowBottomAlignToPlaylist = GetBool(nameof(AppSettings.OptionsWindowBottomAlignToPlaylist)) ?? loaded.OptionsWindowBottomAlignToPlaylist,
            OptionsWindowSelectedTab = loaded.OptionsWindowSelectedTab ?? GetString(nameof(AppSettings.OptionsWindowSelectedTab)),

            PlaylistWindowSnapped = GetBool(nameof(AppSettings.PlaylistWindowSnapped)) ?? loaded.PlaylistWindowSnapped,
            PlaylistWindowSnapEdge = GetString(nameof(AppSettings.PlaylistWindowSnapEdge)) ?? loaded.PlaylistWindowSnapEdge,
            PlaylistWindowDockYOffset = GetDouble(nameof(AppSettings.PlaylistWindowDockYOffset)) ?? loaded.PlaylistWindowDockYOffset,
            PlaylistWindowDockXOffset = GetDouble(nameof(AppSettings.PlaylistWindowDockXOffset)) ?? loaded.PlaylistWindowDockXOffset,
            PlaylistWindowBoundsUiScalePercent = GetInt(nameof(AppSettings.PlaylistWindowBoundsUiScalePercent)) ?? loaded.PlaylistWindowBoundsUiScalePercent,

            CacheMaxMb = loaded.CacheMaxMb ?? GetInt(nameof(AppSettings.CacheMaxMb)),
            Volume = loaded.Volume ?? GetDouble(nameof(AppSettings.Volume)),
            PlaylistAutoRefreshMinutes = loaded.PlaylistAutoRefreshMinutes ?? GetInt(nameof(AppSettings.PlaylistAutoRefreshMinutes)),
            IncludeSubfoldersOnFolderLoad = loaded.IncludeSubfoldersOnFolderLoad ?? GetBool(nameof(AppSettings.IncludeSubfoldersOnFolderLoad)),
        };
    }

    private static AppSettings TrySalvageSettingsWithRegex(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return AllNullSettings();

        var any = false;

        void TakeString(string prop, ref AppSettings cur, Func<AppSettings, string?, AppSettings> apply)
        {
            var v = RegexScalarString(json, prop);
            if (v is null) return;
            cur = apply(cur, v);
            any = true;
        }

        void TakeDouble(string prop, ref AppSettings cur, Func<AppSettings, double, AppSettings> apply)
        {
            var v = RegexScalarDouble(json, prop);
            if (v is null) return;
            cur = apply(cur, v.Value);
            any = true;
        }

        void TakeInt(string prop, ref AppSettings cur, Func<AppSettings, int, AppSettings> apply)
        {
            var v = RegexScalarInt(json, prop);
            if (v is null) return;
            cur = apply(cur, v.Value);
            any = true;
        }

        void TakeBool(string prop, ref AppSettings cur, Func<AppSettings, bool, AppSettings> apply)
        {
            var v = RegexScalarBool(json, prop);
            if (v is null) return;
            cur = apply(cur, v.Value);
            any = true;
        }

        var s = AllNullSettings();
        TakeString(nameof(AppSettings.YtDlpPath), ref s, (c, v) => c with { YtDlpPath = v });
        TakeString(nameof(AppSettings.FfmpegPath), ref s, (c, v) => c with { FfmpegPath = v });
        TakeString(nameof(AppSettings.LastPlaylistUrl), ref s, (c, v) => c with { LastPlaylistUrl = v });
        TakeString(nameof(AppSettings.LastLocalPlaylistPath), ref s, (c, v) => c with { LastLocalPlaylistPath = v });
        TakeString(nameof(AppSettings.ThemeMode), ref s, (c, v) => c with { ThemeMode = v });
        TakeString(nameof(AppSettings.BackgroundMode), ref s, (c, v) => c with { BackgroundMode = v });
        TakeString(nameof(AppSettings.CustomBackgroundImagePath), ref s, (c, v) => c with { CustomBackgroundImagePath = v });
        TakeString(nameof(AppSettings.LastSavedByAppVersion), ref s, (c, v) => c with { LastSavedByAppVersion = v });
        TakeString(nameof(AppSettings.NodeJsPath), ref s, (c, v) => c with { NodeJsPath = v });
        TakeString(nameof(AppSettings.CurrentVideoId), ref s, (c, v) => c with { CurrentVideoId = v });
        TakeString(nameof(AppSettings.RepeatMode), ref s, (c, v) => c with { RepeatMode = v });
        TakeString(nameof(AppSettings.VisualizerMode), ref s, (c, v) => c with { VisualizerMode = v });
        TakeString(nameof(AppSettings.WindowState), ref s, (c, v) => c with { WindowState = v });
        TakeDouble(nameof(AppSettings.Volume), ref s, (c, v) => c with { Volume = v });
        TakeDouble(nameof(AppSettings.WindowLeft), ref s, (c, v) => c with { WindowLeft = v });
        TakeDouble(nameof(AppSettings.WindowTop), ref s, (c, v) => c with { WindowTop = v });
        TakeDouble(nameof(AppSettings.WindowWidth), ref s, (c, v) => c with { WindowWidth = v });
        TakeDouble(nameof(AppSettings.WindowHeight), ref s, (c, v) => c with { WindowHeight = v });
        TakeInt(nameof(AppSettings.UiScalePercent), ref s, (c, v) => c with { UiScalePercent = v });
        TakeInt(nameof(AppSettings.CacheMaxMb), ref s, (c, v) => c with { CacheMaxMb = v });
        TakeBool(nameof(AppSettings.ShuffleEnabled), ref s, (c, v) => c with { ShuffleEnabled = v });
        TakeBool(nameof(AppSettings.GlobalMediaKeysEnabled), ref s, (c, v) => c with { GlobalMediaKeysEnabled = v });
        TakeBool(nameof(AppSettings.WasPlaying), ref s, (c, v) => c with { WasPlaying = v });
        TakeBool(nameof(AppSettings.AlwaysOnTop), ref s, (c, v) => c with { AlwaysOnTop = v });
        TakeBool(nameof(AppSettings.AlwaysOnTopPlaylistWindow), ref s, (c, v) => c with { AlwaysOnTopPlaylistWindow = v });
        TakeBool(nameof(AppSettings.AlwaysOnTopOptionsWindow), ref s, (c, v) => c with { AlwaysOnTopOptionsWindow = v });
        TakeBool(nameof(AppSettings.CompactModeHidesAuxWindows), ref s, (c, v) => c with { CompactModeHidesAuxWindows = v });

        return any ? s : AllNullSettings();
    }

    private static string? RegexScalarString(string json, string propertyName)
    {
        var m = Regex.Match(
            json,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"([^\"]*)\"",
            RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double? RegexScalarDouble(string json, string propertyName)
    {
        var m = Regex.Match(
            json,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static int? RegexScalarInt(string json, string propertyName)
    {
        var m = Regex.Match(
            json,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*(-?\\d+)",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;
    }

    private static bool? RegexScalarBool(string json, string propertyName)
    {
        var m = Regex.Match(
            json,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        return bool.TryParse(m.Groups[1].Value, out var b) ? b : null;
    }

    /// <summary>All-null template used for JSON patch / recovery; do not mutate.</summary>
    private static readonly AppSettings AllNullSettingsInstance = new(
        YtDlpPath: null,
        FfmpegPath: null,
        LastPlaylistUrl: null,
        LastPlaylistSourceType: null,
        LastLocalPlaylistPath: null,
        VisualizerMode: null,
        ShuffleEnabled: null,
        GlobalMediaKeysEnabled: null,
        RepeatMode: null,
        CurrentVideoId: null,
        PlayOrderVideoIds: null,
        CurrentPositionSeconds: null,
        WasPlaying: null,
        CacheMaxMb: null,
        Volume: null,
        PlaylistAutoRefreshMinutes: null,
        IncludeSubfoldersOnFolderLoad: null,
        AlwaysOnTop: null,
        AlwaysOnTopPlaylistWindow: null,
        AlwaysOnTopOptionsWindow: null,
        WindowLeft: null,
        WindowTop: null,
        WindowWidth: null,
        WindowHeight: null,
        WindowState: null,
        PlaylistWindowLeft: null,
        PlaylistWindowTop: null,
        PlaylistWindowWidth: null,
        PlaylistWindowHeight: null,
        PlaylistWindowState: null,
        PlaylistWindowOpen: null,
        PlaylistWindowFilter: null,
        OptionsWindowLeft: null,
        OptionsWindowTop: null,
        OptionsWindowWidth: null,
        OptionsWindowHeight: null,
        OptionsWindowState: null,
        OptionsWindowOpen: null,
        PlaylistWindowSnapped: null,
        PlaylistWindowSnapEdge: null,
        PlaylistWindowDockYOffset: null,
        PlaylistWindowDockXOffset: null,
        PlaylistWindowBoundsUiScalePercent: null,
        OptionsWindowSnapped: null,
        OptionsWindowSnapEdge: null,
        OptionsWindowDockYOffset: null,
        OptionsWindowDockXOffset: null,
        OptionsWindowBottomAlignToPlaylist: null,
        OptionsWindowSelectedTab: null,
        ThemeMode: null,
        BackgroundMode: null,
        CustomBackgroundImagePath: null,
        BackgroundColorMode: null,
        CustomBackgroundColor: null,
        BackgroundAlpha: null,
        BackgroundScrimPercent: null,
        BackgroundImageStretch: null,
        AppTitleMode: null,
        CustomAppTitle: null,
        AppIconVisibility: null,
        SearchDefaultCount: null,
        SearchMinLengthSeconds: null,
        ReadMetadataOnLoad: null,
        UiScalePercent: null,
        WindowBorderMode: null,
        WindowBorderCustomPx: null,
        NodeJsPath: null,
        YtdlpEjsComponentSource: null,
        YoutubeCookiesFromBrowserEnabled: null,
        YoutubeCookiesFromBrowser: null,
        AudioQuality: null,
        AudioOutputDevice: null,
        AppLogLevel: null,
        AppLogMaxMb: null,
        MainWindowCompact: null,
        CompactModeHidesAuxWindows: null,
        KeepIncompletePlaylistOnCancel: null,
        LastSavedByAppVersion: null);

    private static AppSettings AllNullSettings() => AllNullSettingsInstance;

    public static void Save(AppSettings settings)
    {
        var path = GetSettingsPath();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static AppSettings DefaultSettings()
        => new(
            YtDlpPath: null,
            FfmpegPath: null,
            LastPlaylistUrl: null,
            LastPlaylistSourceType: DefaultLastPlaylistSourceType,
            LastLocalPlaylistPath: null,
            VisualizerMode: null,
            ShuffleEnabled: null,
            GlobalMediaKeysEnabled: DefaultGlobalMediaKeysEnabled,
            RepeatMode: DefaultRepeatMode,
            CurrentVideoId: null,
            PlayOrderVideoIds: null,
            CurrentPositionSeconds: null,
            WasPlaying: null,
            CacheMaxMb: DefaultCacheMaxMb,
            Volume: DefaultVolume,
            PlaylistAutoRefreshMinutes: DefaultPlaylistAutoRefreshMinutes,
            IncludeSubfoldersOnFolderLoad: DefaultIncludeSubfoldersOnFolderLoad,
            AlwaysOnTop: false,
            AlwaysOnTopPlaylistWindow: false,
            AlwaysOnTopOptionsWindow: false,
            WindowLeft: null,
            WindowTop: null,
            WindowWidth: null,
            WindowHeight: null,
            WindowState: null,
            PlaylistWindowLeft: null,
            PlaylistWindowTop: null,
            PlaylistWindowWidth: null,
            PlaylistWindowHeight: null,
            PlaylistWindowState: null,
            PlaylistWindowOpen: true,
            PlaylistWindowFilter: null,
            OptionsWindowLeft: null,
            OptionsWindowTop: null,
            OptionsWindowWidth: null,
            OptionsWindowHeight: null,
            OptionsWindowState: null,
            OptionsWindowOpen: true,
            PlaylistWindowSnapped: true,
            PlaylistWindowSnapEdge: "Right",
            PlaylistWindowDockYOffset: 0,
            PlaylistWindowDockXOffset: 0,
            PlaylistWindowBoundsUiScalePercent: null,
            OptionsWindowSnapped: true,
            OptionsWindowSnapEdge: "Bottom",
            OptionsWindowDockYOffset: 0,
            OptionsWindowDockXOffset: 0,
            OptionsWindowBottomAlignToPlaylist: false,
            OptionsWindowSelectedTab: null,
            ThemeMode: DefaultThemeMode,
            BackgroundMode: DefaultBackgroundMode,
            CustomBackgroundImagePath: null,
            BackgroundColorMode: DefaultBackgroundColorMode,
            CustomBackgroundColor: null,
            BackgroundAlpha: DefaultBackgroundAlpha,
            BackgroundScrimPercent: DefaultBackgroundScrimPercent,
            BackgroundImageStretch: DefaultBackgroundImageStretch,
            AppTitleMode: DefaultAppTitleMode,
            CustomAppTitle: DefaultCustomAppTitle,
            AppIconVisibility: DefaultAppIconVisibility,
            SearchDefaultCount: DefaultSearchCount,
            SearchMinLengthSeconds: DefaultSearchMinLengthSeconds,
            ReadMetadataOnLoad: DefaultReadMetadataOnLoad,
            UiScalePercent: DefaultUiScalePercent,
            WindowBorderMode: DefaultWindowBorderMode,
            WindowBorderCustomPx: DefaultWindowBorderCustomPx,
            NodeJsPath: null,
            YtdlpEjsComponentSource: "github",
            YoutubeCookiesFromBrowserEnabled: false,
            YoutubeCookiesFromBrowser: null,
            AudioQuality: DefaultAudioQuality,
            AudioOutputDevice: null,
            AppLogLevel: DefaultAppLogLevel,
            AppLogMaxMb: DefaultAppLogMaxMb,
            MainWindowCompact: null,
            CompactModeHidesAuxWindows: true,
            KeepIncompletePlaylistOnCancel: DefaultKeepIncompletePlaylistOnCancel,
            LastSavedByAppVersion: null
        );

    private static AppSettings ApplyDefaults(AppSettings s)
        => s with
        {
            CacheMaxMb = s.CacheMaxMb is > 0 and < 102400 ? s.CacheMaxMb : DefaultCacheMaxMb,
            RepeatMode = string.IsNullOrWhiteSpace(s.RepeatMode) ? DefaultRepeatMode : s.RepeatMode.Trim(),
            Volume = s.Volume is >= 0 and <= 1 ? s.Volume : DefaultVolume,
            PlaylistAutoRefreshMinutes = s.PlaylistAutoRefreshMinutes is null or 1 or 5 or 30 ? s.PlaylistAutoRefreshMinutes : null,
            GlobalMediaKeysEnabled = s.GlobalMediaKeysEnabled ?? DefaultGlobalMediaKeysEnabled,
            IncludeSubfoldersOnFolderLoad = s.IncludeSubfoldersOnFolderLoad ?? DefaultIncludeSubfoldersOnFolderLoad,
            LastPlaylistSourceType = string.IsNullOrWhiteSpace(s.LastPlaylistSourceType) ? DefaultLastPlaylistSourceType : s.LastPlaylistSourceType.Trim(),
            PlaylistWindowSnapped = s.PlaylistWindowSnapped ?? false,
            OptionsWindowSnapped = s.OptionsWindowSnapped ?? false,
            OptionsWindowBottomAlignToPlaylist = s.OptionsWindowBottomAlignToPlaylist ?? false,
            OptionsWindowSelectedTab = NormalizeOptionsWindowSelectedTab(s.OptionsWindowSelectedTab),
            ThemeMode = NormalizeThemeMode(s.ThemeMode),
            BackgroundMode = string.IsNullOrWhiteSpace(s.BackgroundMode) ? DefaultBackgroundMode : s.BackgroundMode.Trim(),
            BackgroundColorMode = NormalizeBackgroundColorMode(s.BackgroundColorMode),
            BackgroundAlpha = s.BackgroundAlpha is >= 0 and <= 255 ? s.BackgroundAlpha : DefaultBackgroundAlpha,
            BackgroundScrimPercent = s.BackgroundScrimPercent is >= 0 and <= 80 ? s.BackgroundScrimPercent : DefaultBackgroundScrimPercent,
            BackgroundImageStretch = NormalizeBackgroundImageStretch(s.BackgroundImageStretch),
            AppTitleMode = NormalizeAppTitleMode(s.AppTitleMode),
            CustomAppTitle = s.CustomAppTitle ?? DefaultCustomAppTitle,
            AppIconVisibility = NormalizeAppIconVisibility(s.AppIconVisibility),
            SearchDefaultCount = s.SearchDefaultCount is >= 1 and <= 200 ? s.SearchDefaultCount : DefaultSearchCount,
            SearchMinLengthSeconds = s.SearchMinLengthSeconds is >= 0 and <= 3600 ? s.SearchMinLengthSeconds : DefaultSearchMinLengthSeconds,
            ReadMetadataOnLoad = s.ReadMetadataOnLoad ?? DefaultReadMetadataOnLoad,
            UiScalePercent = s.UiScalePercent is >= 50 and <= 200 ? s.UiScalePercent : DefaultUiScalePercent,
            WindowBorderMode = NormalizeWindowBorderMode(s.WindowBorderMode),
            WindowBorderCustomPx = Math.Clamp(s.WindowBorderCustomPx ?? DefaultWindowBorderCustomPx, 1, 24),
            YtdlpEjsComponentSource = NormalizeYtdlpEjsSource(s.YtdlpEjsComponentSource),
            YoutubeCookiesFromBrowserEnabled = s.YoutubeCookiesFromBrowserEnabled ?? false,
            YoutubeCookiesFromBrowser = string.IsNullOrWhiteSpace(s.YoutubeCookiesFromBrowser) ? null : s.YoutubeCookiesFromBrowser.Trim(),
            AudioQuality = NormalizeAudioQuality(s.AudioQuality),
            AudioOutputDevice = string.IsNullOrWhiteSpace(s.AudioOutputDevice) ? null : s.AudioOutputDevice.Trim(),
            AppLogLevel = NormalizeAppLogLevel(s.AppLogLevel),
            AppLogMaxMb = s.AppLogMaxMb is >= MinAppLogMaxMb and <= MaxAppLogMaxMb ? s.AppLogMaxMb : DefaultAppLogMaxMb,
            MainWindowCompact = s.MainWindowCompact ?? false,
            CompactModeHidesAuxWindows = s.CompactModeHidesAuxWindows ?? true,
            KeepIncompletePlaylistOnCancel = s.KeepIncompletePlaylistOnCancel ?? DefaultKeepIncompletePlaylistOnCancel,
            LastSavedByAppVersion = string.IsNullOrWhiteSpace(s.LastSavedByAppVersion) ? null : s.LastSavedByAppVersion.Trim(),
            PlaylistWindowBoundsUiScalePercent = s.PlaylistWindowBoundsUiScalePercent is >= 50 and <= 200
                ? s.PlaylistWindowBoundsUiScalePercent
                : null,
        };

    public static string NormalizeAppTitleMode(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultAppTitleMode : v.Trim();
        if (string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase)) return "Default";
        if (string.Equals(t, "Custom", StringComparison.OrdinalIgnoreCase)) return "Custom";
        if (string.Equals(t, "Current song", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "CurrentSong", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "NowPlaying", StringComparison.OrdinalIgnoreCase))
            return "Current song";
        return DefaultAppTitleMode;
    }

    public static string NormalizeAppIconVisibility(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultAppIconVisibility : v.Trim();
        if (string.Equals(t, "TaskbarAndTray", StringComparison.OrdinalIgnoreCase)) return "TaskbarAndTray";
        if (string.Equals(t, "TaskbarOnly", StringComparison.OrdinalIgnoreCase)) return "TaskbarOnly";
        if (string.Equals(t, "TrayOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "NotificationAreaOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "SystemTrayOnly", StringComparison.OrdinalIgnoreCase))
            return "TrayOnly";
        return DefaultAppIconVisibility;
    }

    private static string NormalizeAudioQuality(string? q)
    {
        var t = string.IsNullOrWhiteSpace(q) ? DefaultAudioQuality : q.Trim();
        return t is "Auto" or "High" or "Medium" or "Low" ? t : DefaultAudioQuality;
    }

    private const string DefaultOptionsWindowSelectedTab = "Tools";

    public static string NormalizeOptionsWindowSelectedTab(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultOptionsWindowSelectedTab : v.Trim();
        if (string.Equals(t, "Tools", StringComparison.OrdinalIgnoreCase)) return "Tools";
        if (string.Equals(t, "System", StringComparison.OrdinalIgnoreCase)) return "System";
        if (string.Equals(t, "Audio", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (string.Equals(t, "Theme", StringComparison.OrdinalIgnoreCase)) return "Theme";
        if (string.Equals(t, "Search", StringComparison.OrdinalIgnoreCase)) return "Search";
        if (string.Equals(t, "Local", StringComparison.OrdinalIgnoreCase)) return "Local";
        if (string.Equals(t, "Advanced", StringComparison.OrdinalIgnoreCase)) return "Advanced";
        return DefaultOptionsWindowSelectedTab;
    }

    public static string NormalizeBackgroundImageStretch(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultBackgroundImageStretch : v.Trim();
        if (string.Equals(t, "BestFit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Uniform", StringComparison.OrdinalIgnoreCase))
            return "BestFit";
        if (string.Equals(t, "Tile", StringComparison.OrdinalIgnoreCase))
            return "Tile";
        if (string.Equals(t, "Stretch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Fill", StringComparison.OrdinalIgnoreCase))
            return "Stretch";
        return DefaultBackgroundImageStretch;
    }

    public static string NormalizeThemeMode(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultThemeMode : v.Trim();
        if (string.Equals(t, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
        if (string.Equals(t, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
        return "Auto";
    }

    public static string NormalizeBackgroundColorMode(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultBackgroundColorMode : v.Trim();
        if (string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase)) return "Default";
        if (string.Equals(t, "From image", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "FromImage", StringComparison.OrdinalIgnoreCase)) return "From image";
        if (string.Equals(t, "Windows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Windows theme", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "WindowsTheme", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (string.Equals(t, "Custom", StringComparison.OrdinalIgnoreCase)) return "Custom";
        return DefaultBackgroundColorMode;
    }

    private static string NormalizeAppLogLevel(string? v)
    {
        var t = string.IsNullOrWhiteSpace(v) ? DefaultAppLogLevel : v.Trim();
        if (string.Equals(t, "Basic", StringComparison.OrdinalIgnoreCase))
            return "Basic";
        if (string.Equals(t, "Verbose", StringComparison.OrdinalIgnoreCase))
            return "Verbose";
        return DefaultAppLogLevel;
    }

    private static string NormalizeWindowBorderMode(string? mode)
    {
        var m = string.IsNullOrWhiteSpace(mode) ? DefaultWindowBorderMode : mode.Trim();
        if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase))
            return "None";
        if (string.Equals(m, "1px", StringComparison.OrdinalIgnoreCase))
            return "1px";
        if (string.Equals(m, "Custom", StringComparison.OrdinalIgnoreCase))
            return "1px";
        return DefaultWindowBorderMode;
    }

    private static string NormalizeYtdlpEjsSource(string? s)
    {
        var t = string.IsNullOrWhiteSpace(s) ? "github" : s.Trim();
        if (string.Equals(t, "bundled", StringComparison.OrdinalIgnoreCase))
            return "bundled";
        return "github";
    }
}


