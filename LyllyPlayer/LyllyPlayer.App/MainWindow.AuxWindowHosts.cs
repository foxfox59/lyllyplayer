using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LyllyPlayer.Models;
using LyllyPlayer.Services;
using LyllyPlayer.Settings;
using LyllyPlayer.ShellServices;
using LyllyPlayer.Utils;
using LyllyPlayer.Windows;

namespace LyllyPlayer;

public partial class MainWindow
{
    private AuxWindowHost<PlaylistWindow>? _playlistHost;
    private AuxWindowHost<OptionsWindow>? _optionsHost;
    private AuxWindowHost<LyricsWindow>? _lyricsHost;

    private AuxWindowHost<PlaylistWindow> GetPlaylistHost() =>
        _playlistHost ??= new AuxWindowHost<PlaylistWindow>(
            () => _playlistWindow,
            w => _playlistWindow = w,
            _playlistAuxCtl,
            () => _settingsService.LoadLatest(),
            CreatePlaylistWindow,
            ApplyPlaylistWindowPlacementFromSettings,
            AfterPlaylistWindowShown);

    private AuxWindowHost<OptionsWindow> GetOptionsHost() =>
        _optionsHost ??= new AuxWindowHost<OptionsWindow>(
            () => _optionsWindow,
            w => _optionsWindow = w,
            _optionsAuxCtl,
            () => _settingsService.LoadLatest(),
            CreateOptionsWindow,
            ApplyOptionsWindowPlacementFromSettings,
            AfterOptionsWindowShown);

    private AuxWindowHost<LyricsWindow> GetLyricsHost() =>
        _lyricsHost ??= new AuxWindowHost<LyricsWindow>(
            () => _lyricsWindow,
            w => _lyricsWindow = w,
            _lyricsAuxCtl,
            () => _settingsService.LoadLatest(),
            CreateLyricsWindow,
            ApplyLyricsWindowPlacementFromSettings,
            AfterLyricsWindowShown);

    private PlaylistWindow CreatePlaylistWindow(AppSettings latestSettings)
    {
        AppLog.Info($"Playlist bounds (open) settings: L={latestSettings.PlaylistWindowLeft} T={latestSettings.PlaylistWindowTop} W={latestSettings.PlaylistWindowWidth} H={latestSettings.PlaylistWindowHeight} State={latestSettings.PlaylistWindowState}");
        try
        {
            var path = _settingsService.GetSettingsPath();
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

        var w = new PlaylistWindow(new PlaylistWindowOps(
            LoadUrlAsync: async (src) => await LoadUrlAsync(src, CancellationToken.None).ConfigureAwait(true),
            GetSearchDefaults: () => (_searchDefaultCount, _searchMinLengthSeconds),
            SetSearchDefaults: (count, minLenSeconds) =>
            {
                _searchDefaultCount = Math.Clamp(count, 1, 200);
                _searchMinLengthSeconds = Math.Clamp(minLenSeconds, 0, 3600);
                RequestPersistSnapshot();
            },
            SearchYoutubeVideosAsync: async (query, count, minLenSeconds, append, removeDuplicates, ct) =>
                await SearchYoutubeVideosAsync(query, count, minLenSeconds, append, removeDuplicates, ct).ConfigureAwait(true),
            SearchYoutubePlaylistsAsync: async (query, count, ct) =>
                await _ytDlp.ResolveYoutubePlaylistSearchAsync(query, count, ct).ConfigureAwait(true),
            ListYoutubeAccountPlaylistsAsync: async (count, ct) =>
                await _ytDlp.ResolveAccountPlaylistsBestEffortAsync(count, ct).ConfigureAwait(true),
            ImportYoutubePlaylistAsync: async (urlOrId, append, dedupe, ct) =>
                await ImportYoutubePlaylistAsync(urlOrId, append, dedupe, ct).ConfigureAwait(true),
            TryGetYoutubePlaylistItemCountAsync: async (urlOrId, ct) =>
                await _ytDlp.TryGetPlaylistItemCountBestEffortAsync(urlOrId, ct).ConfigureAwait(true),
            OpenUrlAsync: async (url, ct) => await LoadUrlAsync(url, ct).ConfigureAwait(true),
            GetLastYoutubeUrl: () => _lastYoutubeUrl,
            SetLastYoutubeUrl: (url) =>
            {
                var t = PlaylistSourcePathHeuristics.SanitizePersistedLastYoutubeUrl(url);
                if (!string.IsNullOrEmpty(t))
                    _lastYoutubeUrl = t;
            },
            GetYoutubeImportAppendDefault: () => _youtubeImportAppend,
            SetYoutubeImportAppendDefault: v =>
            {
                _youtubeImportAppend = v;
                SaveSettingsSnapshot();
            },
            GetLocalImportAppendDefault: () => _localImportAppend,
            SetLocalImportAppendDefault: v =>
            {
                _localImportAppend = v;
                SaveSettingsSnapshot();
            },
            GetLocalImportRemoveDuplicatesDefault: () => _localImportRemoveDuplicates,
            SetLocalImportRemoveDuplicatesDefault: v =>
            {
                _localImportRemoveDuplicates = v;
                SaveSettingsSnapshot();
            },
            AddLocalFolderAsync: async (folder, append, dedupe, forceNoMetadata, ct, progress) =>
                await AddFolderAsync(folder, append, dedupe, forceNoMetadata, ct, progress).ConfigureAwait(true),
            AddLocalFilesAsync: async (files, append, dedupe, ct, progress) =>
                await AddFilesAsync(files, append, dedupe, ct, progress).ConfigureAwait(true),
            ApplySortAsync: async (spec, ct) => await ApplyPlaylistSortAsync(spec, ct).ConfigureAwait(true),
            GetIsYoutubeSource: () => IsYoutubeLikeSource(_lastPlaylistSourceType),
            SavePlaylistToFileAsync: async (path, displayName) =>
            {
                try
                {
                    var name = string.IsNullOrWhiteSpace(displayName) ? (_playlistTitle ?? "Playlist") : displayName.Trim();
                    var origin = PlaylistFileService.BuildOriginInfoByVideoId(_playlistCore.OriginByVideoId);
                    var outcome = _playlistFiles.SavePlaylist(
                        path: path,
                        playlistName: name,
                        sourceType: _lastPlaylistSourceType.ToString(),
                        source: _playlistSourceText ?? "",
                        entries: _playlistCore.Entries,
                        originInfoByVideoId: origin,
                        exportM3uIncludeYoutube: _exportM3uIncludeYoutube,
                        exportM3uPreferRelativePaths: _exportM3uPreferRelativePaths,
                        exportM3uIncludeLyllyMetadata: _exportM3uIncludeLyllyMetadata);

                    ShowInfoToast(outcome.Format == PlaylistSaveFormat.M3U
                        ? $"Saved M3U: {outcome.FileName}"
                        : $"Saved playlist: {outcome.FileName}");
                }
                catch (Exception ex)
                {
                    AppLog.Exception(ex, "Save playlist failed");
                    SetStatusMessage("ERROR", "Save playlist failed.");
                }
                await Task.CompletedTask;
            },
            LoadPlaylistFromFileAsync: async (path) =>
            {
                try
                {
                    var result = _playlistFiles.LoadSavedPlaylist(path);
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
                    ApplySavedPlaylistOriginsIfAny(pl, result.Entries);
                    UpdateRefreshEnabled();
                    UpdatePlaylistTitleDisplayForNowPlaying();
                }
                catch (Exception ex)
                {
                    AppLog.Exception(ex, "Load saved playlist failed");
                    SetStatusMessage("ERROR", "Load saved playlist failed.");
                }
            },
            NewPlaylistAsync: (ct) =>
            {
                ct.ThrowIfCancellationRequested();
                try { _engine.Stop(); } catch { /* ignore */ }
                _pendingResumeSeconds = 0;
                _pendingResumeVideoId = null;
                ResetTimelineUiToStart();

                _hasLoadedPlaylist = false;
                _loadedPlaylistId = null;
                _lastPlaylistSourceType = PlaylistSourceType.YouTube;
                _lastLocalPlaylistPath = null;
                _playlistSourceText = "";
                _playlistIsCompound = false;
                try { SetPlaylistTitle(null); } catch { /* ignore */ }
                try { _playlistCore.Clear(); } catch { /* ignore */ }
                _engine.SetQueue(_playlistCore.Entries, startIndex: -1, raiseNowPlayingChanged: false);
                SetQueueList(Array.Empty<PlaylistEntry>(), selectedIndex: -1);
                UpdateRefreshEnabled();
                MarkLastPlaylistSnapshotDirty();
                RequestPersistSnapshot();
                return Task.CompletedTask;
            },
            LoadEntriesAsync: async (entries, title, sourceKey, ct) =>
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
            RefreshAsync: async (ct) =>
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
            CleanInvalidItemsAsync: async (ct) => await CleanInvalidPlaylistItemsAsync(ct).ConfigureAwait(true),
            RemoveDuplicatesAsync: async (ct) => await RemoveDuplicatePlaylistItemsAsync(ct).ConfigureAwait(true),
            CapturePlaylistForCancelRestore: BeginCancelPlaylistSnapshot,
            CommitPlaylistCancelRestore: CommitCancelPlaylistSnapshot,
            RollbackPlaylistCancelRestore: RollbackCancelPlaylistSnapshot,
            SourceChanged: (src) => _playlistSourceText = src,
            GetSource: () => _playlistSourceText,
            GetFfmpegPath: () => _savedFfmpegPath ?? "",
            GetIncludeSubfoldersOnFolderLoad: () => _includeSubfoldersOnFolderLoad,
            GetReadMetadataOnLoad: () => _readMetadataOnLoad,
            GetKeepIncompletePlaylistOnCancel: () => _keepIncompletePlaylistOnCancel,
            GetRefreshOffersMetadataSkip: () =>
                _readMetadataOnLoad &&
                (_lastPlaylistSourceType == PlaylistSourceType.Folder ||
                 _lastPlaylistSourceType == PlaylistSourceType.M3U),
            RefreshLocalWithoutMetadataAsync: async (ct) =>
            {
                await RefreshCurrentSourceAsync(
                    preserveCurrentIfPossible: true,
                    cancellationToken: ct,
                    forceLocalNoMetadata: true).ConfigureAwait(true);
            },
            SelectedVideoIdChanged: (videoId) =>
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
            DoubleClickPlayAsync: (videoId) =>
            {
                try
                {
                    try { AppLog.Warn($"DoubleClickPlay: enter videoId={videoId} playOrderCount={_engine.PlayOrder.Count}"); } catch { /* ignore */ }
                    if (_engine.PlayOrder.Count == 0) return Task.CompletedTask;

                    if (!string.IsNullOrWhiteSpace(videoId) &&
                        videoId.StartsWith("queue:", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(videoId["queue:".Length..], out var qid))
                    {
                        try
                        {
                            var inst = _queuedNext.FirstOrDefault(q => q.Id == qid);
                            if (inst is not null)
                            {
                                _manualQueuedPlayInstanceId = qid;
                                var idxBase = _playlistCore.Entries.FindIndex(e => e.VideoId.Equals(inst.Entry.VideoId));
                                if (idxBase >= 0)
                                    _engine.SetQueue(_playlistCore.Entries, startIndex: idxBase, raiseNowPlayingChanged: true);
                            }
                        }
                        catch { /* ignore */ }
                    }
                    else
                    {
                        // Manual play from base playlist: align engine play order to the base list around the clicked row.
                        var selectedEntry = _playlistCore.Entries.FirstOrDefault(e =>
                            string.Equals(e.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
                        if (selectedEntry is not null)
                        {
                            _manualQueuedPlayInstanceId = null;
                            var idxBase = _playlistCore.Entries.FindIndex(e => e.VideoId.Equals(selectedEntry.VideoId));
                            if (idxBase < 0)
                                idxBase = FindIndexByVideoId(_playlistCore.Entries, selectedEntry.VideoId);
                            if (idxBase >= 0)
                                _engine.SetQueue(_playlistCore.Entries, startIndex: idxBase, raiseNowPlayingChanged: true);
                        }
                    }

                    // After any SetQueue call above, indices may have shifted; always resolve against the *current* engine order.
                    var playIdx = FindPlayOrderIndexByVideoId(videoId);
                    if (playIdx < 0)
                    {
                        try { AppLog.Warn($"Double-click play: VideoId not in current play order ({videoId})."); } catch { /* ignore */ }
                        return Task.CompletedTask;
                    }

                    if (playIdx < 0 || playIdx >= _engine.PlayOrder.Count)
                    {
                        try { AppLog.Warn($"Double-click play: index out of range ({videoId}) idx={playIdx} count={_engine.PlayOrder.Count}."); } catch { /* ignore */ }
                        return Task.CompletedTask;
                    }

                    var selected = _engine.PlayOrder[playIdx];
                    try { AppLog.Warn($"DoubleClickPlay: resolved playIdx={playIdx} selected={selected.VideoId}"); } catch { /* ignore */ }
                    _suppressAutoScrollVideoId = selected.VideoId;
                    _suppressAutoScrollUntilUtc = DateTime.UtcNow.AddSeconds(2);
                    // Same-track replay: clear resume override or UpdateTimelineUi keeps showing the saved position instead of 0.
                    _pendingResumeSeconds = 0;
                    _pendingResumeVideoId = null;
                    _engine.SetQueue(_engine.PlayOrder, startIndex: playIdx, raiseNowPlayingChanged: true);
                    UpdateTimelineUi();
                    try { AppLog.Warn($"DoubleClickPlay: calling PlayCurrentAsync videoId={selected.VideoId}"); } catch { /* ignore */ }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _engine.PlayCurrentAsync().ConfigureAwait(false);
                            try { AppLog.Warn($"DoubleClickPlay: PlayCurrentAsync returned videoId={selected.VideoId}"); } catch { /* ignore */ }
                        }
                        catch (Exception ex)
                        {
                            try { AppLog.Exception(ex, "DoubleClickPlay PlayCurrentAsync failed"); } catch { /* ignore */ }
                        }
                    });
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    try { AppLog.Exception(ex, "Double-click play failed"); } catch { /* ignore */ }
                    try { System.Windows.MessageBox.Show(this, $"Play failed:\n\n{ex.GetType().Name}: {ex.Message}", GetAppTitleBase(), MessageBoxButton.OK, MessageBoxImage.Error); } catch { /* ignore */ }
                }
                return Task.CompletedTask;
            },
            AddToQueueAsync: (entry) =>
            {
                try
                {
                    AddToQueue(entry);
                    // NO rebuild needed - queue is handled separately
                    RequestPersistSnapshot();
                }
                catch { /* ignore */ }
                return Task.CompletedTask;
            },
            RemoveQueuedInstanceAsync: (id) =>
            {
                try
                {
                    if (RemoveQueuedInstance(id))
                    {
                        // NO rebuild needed
                        RequestPersistSnapshot();
                    }
                }
                catch { /* ignore */ }
                return Task.CompletedTask;
            },
            HandleDroppedLocalPathsAsync: async (paths, ct) => await HandleDroppedLocalPathsAsync(paths, ct).ConfigureAwait(true),
            HandleDroppedUrlsAsync: async (urls, ct) => await HandleDroppedUrlsAsync(urls, ct).ConfigureAwait(true)
        ))
        {
            Owner = null,
        };
        try { w.Title = $"{GetAppTitleBase()} — Playlist"; } catch { /* ignore */ }

        try
        {
            var sortModeRaw = (latestSettings.PlaylistWindowSortMode ?? "None").Trim();
            var sortDirRaw = (latestSettings.PlaylistWindowSortDirection ?? "Asc").Trim();
            _ = Enum.TryParse<PlaylistSortMode>(sortModeRaw, ignoreCase: true, out var sm);
            _ = Enum.TryParse<PlaylistSortDirection>(sortDirRaw, ignoreCase: true, out var sd);
            var spec = new PlaylistSortSpec(sm, sd);
            w.SetSortSpec(spec);
            if (spec.Mode != PlaylistSortMode.None && _playlistCore.Entries.Count > 1)
                _ = ApplyPlaylistSortAsync(spec, CancellationToken.None);
        }
        catch { /* ignore */ }

        // Center on now-playing during Loaded (sync + first center pass on UI thread) so we do not paint the
        // top of the list for a frame before ContextIdle scroll. Queue list opacity is suppressed until then.
        w.Loaded += (_, _) =>
        {
            try { FocusPlaylistOnNowPlaying(); }
            catch { /* ignore */ }
        };

        w.Closing += (_, _) =>
        {
            if (_isShuttingDown)
                return;
            WindowCoordinator.CaptureWindowBounds(w, out _lastPlaylistBounds, out _lastPlaylistWindowState);
            try { UpdatePlaylistSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            // Persist bounds/state when the playlist window is closed.
            SaveSettingsSnapshot();
        };
        w.LocationChanged += (_, _) =>
        {
            if (_syncingWindowMove || _restoringAuxFromMinimize) return;
            WindowCoordinator.CaptureWindowBounds(w, out _lastPlaylistBounds, out _lastPlaylistWindowState);
            try { UpdatePlaylistSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            // Legacy "dock to main" snapping fights the new snap service.
            // Let WM_MOVING-based snapping control interactive positioning.
            RequestPersistSnapshot();
        };
        w.SizeChanged += (_, _) =>
        {
            if (_syncingWindowMove || _restoringAuxFromMinimize) return;
            WindowCoordinator.CaptureWindowBounds(w, out _lastPlaylistBounds, out _lastPlaylistWindowState);
            try { UpdatePlaylistSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            // Do not enforce legacy snapping on resize; WM_MOVING snapping is interactive-only.
            RequestPersistSnapshot();
        };
        w.IsVisibleChanged += (_, _) =>
        {
            if (_isShuttingDown || _syncingWindowMove || _restoringAuxFromMinimize)
                return;
            if (!w.IsVisible)
            {
                try { WindowCoordinator.CaptureWindowBounds(w, out _lastPlaylistBounds, out _lastPlaylistWindowState); } catch { /* ignore */ }
                try { UpdatePlaylistSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
                try { SaveSettingsSnapshot(); } catch { /* ignore */ }
            }
        };
        w.Closed += (_, _) =>
        {
            _playlistWindow = null;
            _playlistAuxCtl.Clear();
            _playlistWindowOuterAtUiScalePercent = null;
            // If user closes Playlist, clear the compact "pin open" override.
            _compactUserOpenedPlaylistWindow = false;
        };
        try { WindowCoordinator.RegisterSnapping(w); } catch { /* ignore */ }
        w.SetItemsSource(_queueItems, _playlistItems);
        UpdateRefreshEnabled();
        try { w.ApplyPersistedPlaylistFilter(latestSettings.PlaylistWindowFilter); } catch { /* ignore */ }
        return w;
    }

    private void ApplyPlaylistWindowPlacementFromSettings(PlaylistWindow w, AppSettings latestSettings, bool warmReopen)
    {
        if (warmReopen)
        {
            try { ApplyPlaylistWindowSettings(latestSettings, w); } catch { /* ignore */ }
            return;
        }

        try { ApplyPlaylistWindowSettings(latestSettings, w); } catch { /* ignore */ }
        try { NormalizePlaylistWindowOuterForUiScale(latestSettings, w); } catch { /* ignore */ }
        var plS = UiScale;
        w.MinWidth = 560.0 * plS;
        w.MinHeight = 320.0 * plS;
        _playlistWindowOuterAtUiScalePercent = _uiScalePercent;
        AppLog.Info($"Playlist bounds (open) pre-show: L={w.Left} T={w.Top} W={w.Width} H={w.Height} State={w.WindowState}");
        try { w.BeginSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
    }

    private void AfterPlaylistWindowShown(PlaylistWindow w, bool warmReopen)
    {
        if (warmReopen)
        {
            try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            }), DispatcherPriority.Background);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        }), DispatcherPriority.Background);
        try
        {
            var current = _engine.GetCurrent();
            if (current is not null)
                w.CenterNowPlaying(current);
        }
        catch { /* ignore */ }
        ApplyAlwaysOnTopFromSettings();
    }

    private OptionsWindow CreateOptionsWindow(AppSettings latestSettings)
    {
        AppLog.Info($"Options bounds (open) settings: L={latestSettings.OptionsWindowLeft} T={latestSettings.OptionsWindowTop} W={latestSettings.OptionsWindowWidth} H={latestSettings.OptionsWindowHeight} State={latestSettings.OptionsWindowState}");
        try
        {
        var path = _settingsService.GetSettingsPath();
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
        var w = new OptionsWindow(
            getYtDlpPath: () => _savedYtDlpPath ?? "",
            setYtDlpPath: (p) =>
            {
                _savedYtDlpPath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                ApplyResolvedToolPaths();
                ApplyYtdlpPlaybackOptions();
                RequestPersistSnapshot();
            },
            getInternalYtDlpUpdateCheckEnabled: () => _internalYtDlpUpdateCheckEnabled,
            setInternalYtDlpUpdateCheckEnabled: (v) =>
            {
                _internalYtDlpUpdateCheckEnabled = v;
                RequestPersistSnapshot();
            },
            checkInternalYtDlpNowAsync: async () => await CheckInternalYtDlpNowAsync().ConfigureAwait(true),
            getLyricsEnabled: () => _lyricsEnabled,
            setLyricsEnabled: (v) =>
            {
                _lyricsEnabled = v;
                if (!v)
                    _stopLyricsTimer();
                else if (_engine.IsPlaying)
                    _startLyricsTimer();
                try { UpdateLyricsDisplay(force: true); } catch { /* ignore */ }
                RequestPersistSnapshot();
            },
            getLyricsLocalFilesEnabled: () => _lyricsLocalFilesEnabled,
            setLyricsLocalFilesEnabled: (v) =>
            {
                _lyricsLocalFilesEnabled = v;
                // if (v)
                //     _startLyricsTimer();
                // else
                //     _lyricsTimer?.Stop();
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
                _backgroundMode = SettingsStore.NormalizeBackgroundMode(m);
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
            getBackgroundUserDefinedMainNormal: () => _backgroundUserDefinedMainNormal,
            setBackgroundUserDefinedMainNormal: (r) => { _backgroundUserDefinedMainNormal = r; },
            getBackgroundUserDefinedMainCompact: () => _backgroundUserDefinedMainCompact,
            setBackgroundUserDefinedMainCompact: (r) => { _backgroundUserDefinedMainCompact = r; },
            getBackgroundUserDefinedMainUltra: () => _backgroundUserDefinedMainUltra,
            setBackgroundUserDefinedMainUltra: (r) => { _backgroundUserDefinedMainUltra = r; },
            getBackgroundUserDefinedPlaylist: () => _backgroundUserDefinedPlaylist,
            setBackgroundUserDefinedPlaylist: (r) => { _backgroundUserDefinedPlaylist = r; },
            getBackgroundUserDefinedOptionsLog: () => _backgroundUserDefinedOptionsLog,
            setBackgroundUserDefinedOptionsLog: (r) => { _backgroundUserDefinedOptionsLog = r; },
            getBackgroundUserDefinedLyrics: () => _backgroundUserDefinedLyrics,
            setBackgroundUserDefinedLyrics: (r) => { _backgroundUserDefinedLyrics = r; },
            openBackgroundDesigner: () => OpenBackgroundDesigner(),
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
            getAlwaysOnTopLyricsWindow: () => _alwaysOnTopLyricsWindow,
            setAlwaysOnTopLyricsWindow: (v) => SetAlwaysOnTopLyricsWindow(v),
            getCompactModeHidesAuxWindows: () => _compactModeHidesAuxWindows,
            setCompactModeHidesAuxWindows: (v) => SetCompactModeHidesAuxWindows(v),
            getCompactModeLayout: () => _compactModeLayout,
            setCompactModeLayout: (v) => SetCompactModeLayout(v),
            getKeepIncompletePlaylistOnCancel: () => _keepIncompletePlaylistOnCancel,
            setKeepIncompletePlaylistOnCancel: (v) =>
            {
                _keepIncompletePlaylistOnCancel = v;
                RequestPersistSnapshot();
            },
            getExportM3uIncludeYoutube: () => _exportM3uIncludeYoutube,
            setExportM3uIncludeYoutube: (v) =>
            {
                _exportM3uIncludeYoutube = v;
                RequestPersistSnapshot();
            },
            getExportM3uPreferRelativePaths: () => _exportM3uPreferRelativePaths,
            setExportM3uPreferRelativePaths: (v) =>
            {
                _exportM3uPreferRelativePaths = v;
                RequestPersistSnapshot();
            },
            getExportM3uIncludeLyllyMetadata: () => _exportM3uIncludeLyllyMetadata,
            setExportM3uIncludeLyllyMetadata: (v) =>
            {
                _exportM3uIncludeLyllyMetadata = v;
                RequestPersistSnapshot();
            },
            getPlaylistDragDropAppend: () => _playlistDragDropAppend,
            setPlaylistDragDropAppend: (v) =>
            {
                _playlistDragDropAppend = v;
                RequestPersistSnapshot();
            },
            getPlaylistDragDropRemoveDuplicates: () => _playlistDragDropRemoveDuplicates,
            setPlaylistDragDropRemoveDuplicates: (v) =>
            {
                _playlistDragDropRemoveDuplicates = v;
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
            getAudioNormalize: () => _audioNormalizeEnabled,
            setAudioNormalize: (v) =>
            {
                _audioNormalizeEnabled = v;
                try { _engine.SetAudioNormalizeEnabled(_audioNormalizeEnabled); } catch { /* ignore */ }
                RequestPersistSnapshot();
            },
            getOptionsSelectedTab: () => _optionsSelectedTab,
            setOptionsSelectedTab: (tab) =>
            {
                _optionsSelectedTab = SettingsStore.NormalizeOptionsWindowSelectedTab(tab);
                RequestPersistSnapshot();
            },
            getLameEncoderPath: () => _lameEncoderPath ?? "",
            setLameEncoderPath: (p) =>
            {
                _lameEncoderPath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                RequestPersistSnapshot();
            },
            getMp3ExportEncodingMode: () => _mp3ExportEncodingMode,
            setMp3ExportEncodingMode: (m) =>
            {
                _mp3ExportEncodingMode = SettingsStore.NormalizeMp3ExportEncodingMode(m);
                RequestPersistSnapshot();
            },
            getMp3ExportCbrQualityIndex: () => _mp3ExportCbrQualityIndex,
            setMp3ExportCbrQualityIndex: (i) =>
            {
                _mp3ExportCbrQualityIndex = SettingsStore.ClampMp3SliderIndex(i, Mp3QualityMaps.DefaultCbrSliderIndex);
                RequestPersistSnapshot();
            },
            getMp3ExportVbrQualityIndex: () => _mp3ExportVbrQualityIndex,
            setMp3ExportVbrQualityIndex: (i) =>
            {
                _mp3ExportVbrQualityIndex = SettingsStore.ClampMp3SliderIndex(i, Mp3QualityMaps.DefaultVbrSliderIndex);
                RequestPersistSnapshot();
            },
            getMp3ExportReplacePlaylistEntryAfterExport: () => _mp3ExportReplacePlaylistEntryAfterExport,
            setMp3ExportReplacePlaylistEntryAfterExport: (v) =>
            {
                _mp3ExportReplacePlaylistEntryAfterExport = v;
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
        try { w.Title = $"{GetAppTitleBase()} — Options"; } catch { /* ignore */ }
        w.Closing += (_, _) =>
        {
        if (_isShuttingDown)
            return;
        WindowCoordinator.CaptureWindowBounds(w, out _lastOptionsBounds, out _lastOptionsWindowState);
        try { UpdateOptionsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        SaveSettingsSnapshot();
        };
        w.LocationChanged += (_, _) =>
        {
        if (_syncingWindowMove || _restoringAuxFromMinimize) return;
        WindowCoordinator.CaptureWindowBounds(w, out _lastOptionsBounds, out _lastOptionsWindowState);
        try { UpdateOptionsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        // Legacy "dock to main" snapping fights the new snap service.
        RequestPersistSnapshot();
        };
        w.SizeChanged += (_, _) =>
        {
        if (_syncingWindowMove || _restoringAuxFromMinimize) return;
        WindowCoordinator.CaptureWindowBounds(w, out _lastOptionsBounds, out _lastOptionsWindowState);
        try { UpdateOptionsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        // Do not enforce legacy snapping on resize; WM_MOVING snapping is interactive-only.
        RequestPersistSnapshot();
        };
        w.IsVisibleChanged += (_, _) =>
        {
        if (_isShuttingDown || _syncingWindowMove || _restoringAuxFromMinimize)
            return;
        if (!w.IsVisible)
        {
            try { WindowCoordinator.CaptureWindowBounds(w, out _lastOptionsBounds, out _lastOptionsWindowState); } catch { /* ignore */ }
            try { UpdateOptionsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }
        }
        };
        w.Closed += (_, _) =>
        {
        _optionsWindow = null;
        _optionsAuxCtl.Clear();
        };
        try { WindowCoordinator.RegisterSnapping(w); } catch { /* ignore */ }
        return w;
    }

    private void ApplyOptionsWindowPlacementFromSettings(OptionsWindow w, AppSettings latestSettings, bool warmReopen)
    {
        try { ApplyOptionsWindowScaledChromeSize(w); } catch { /* ignore */ }
        try { ApplyOptionsWindowSettings(latestSettings, w); } catch { /* ignore */ }
        if (!warmReopen)
            try { w.SetLogPopoutOpen(_logWindow is not null); } catch { /* ignore */ }
    }

    private void AfterOptionsWindowShown(OptionsWindow w, bool warmReopen)
    {
        if (warmReopen)
        {
            try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            }), DispatcherPriority.Background);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        }), DispatcherPriority.Background);
        try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
        ApplyAlwaysOnTopFromSettings();
    }

    private LyricsWindow CreateLyricsWindow(AppSettings latestSettings)
    {
        _ = latestSettings;
        // Fast path: if we have cached lyrics for the current track, load them before showing the window
        // so the initial render isn't blank while other startup work is happening.
        try { TryLoadLyricsFromCacheForCurrentBestEffort(); } catch { /* ignore */ }

        var w = new LyricsWindow(
            lyricsEnabled: () => _lyricsEnabled,
            hasLyrics: () => _lyricsService.Manager.HasLyrics,
            lyricsTitle: () => _lyricsService.Manager.ResolvedTitleDisplay,
            lyricsLines: () => _lyricsService.Manager.HasLyrics ? _lyricsService.Manager.GetLineTexts() : Array.Empty<string>(),
            isPlainLyrics: () => _lyricsService.Manager.IsPlainLyrics,
            getCurrentLineIndex: () => _lyricsService.Manager.GetCurrentLineIndex(_engine.CurrentPositionSeconds)
        )
        {
            Owner = null,
        };
        try { w.Title = $"{GetAppTitleBase()} — Lyrics"; } catch { /* ignore */ }

        w.Closing += (_, _) =>
        {
            if (_isShuttingDown)
                return;
            WindowCoordinator.CaptureWindowBounds(w, out _lastLyricsBounds, out _lastLyricsWindowState);
            try { UpdateLyricsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            SaveSettingsSnapshot();
        };
        w.LocationChanged += (_, _) =>
        {
            if (_syncingWindowMove || _restoringAuxFromMinimize) return;
            WindowCoordinator.CaptureWindowBounds(w, out var lastLyricsBounds, out var lastLyricsState);
            try { UpdateLyricsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            // Legacy "dock to main" snapping fights the new snap service.
            RequestPersistSnapshot();
        };
        w.SizeChanged += (_, _) =>
        {
            if (_syncingWindowMove || _restoringAuxFromMinimize) return;
            WindowCoordinator.CaptureWindowBounds(w, out var lastLyricsBounds, out var lastLyricsState);
            try { UpdateLyricsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            // Do not enforce legacy snapping on resize; WM_MOVING snapping is interactive-only.
            RequestPersistSnapshot();
        };
        w.IsVisibleChanged += (_, _) =>
        {
            if (_isShuttingDown || _syncingWindowMove || _restoringAuxFromMinimize)
                return;
            if (!w.IsVisible)
            {
                try { WindowCoordinator.CaptureWindowBounds(w, out _lastLyricsBounds, out _lastLyricsWindowState); } catch { /* ignore */ }
                try { UpdateLyricsSnapStateFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
                try { SaveSettingsSnapshot(); } catch { /* ignore */ }
                try { UpdateLyricsDisplay(force: true); } catch { /* ignore */ }
            }
        };
        w.Closed += (_, _) =>
        {
            _lyricsWindow = null;
            _lyricsAuxCtl.Clear();
            // Ultra-compact title bar shows lyric line only when the lyrics window is closed.
            // Force a refresh on close (even when paused) so the title bar switches immediately.
            try { UpdateLyricsDisplay(force: true); } catch { /* ignore */ }
        };
        try { WindowCoordinator.RegisterSnapping(w); } catch { /* ignore */ }
        return w;
    }

    private void ApplyLyricsWindowPlacementFromSettings(LyricsWindow w, AppSettings latestSettings, bool warmReopen)
    {
        if (warmReopen)
        {
            try { ApplyLyricsWindowSettings(latestSettings, w); } catch { /* ignore */ }
            return;
        }

        try { ApplyLyricsWindowSettings(latestSettings, w); } catch { /* ignore */ }
        w.MinWidth = 400.0 * UiScale;
        w.MinHeight = 300.0 * UiScale;
    }

    private void AfterLyricsWindowShown(LyricsWindow w, bool warmReopen)
    {
        if (warmReopen)
        {
            try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
            }), DispatcherPriority.Background);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { WindowCoordinator.RestoreLatchRelationsFromCurrentPositionsBestEffort(); } catch { /* ignore */ }
        }), DispatcherPriority.Background);
        try { QueueAuxSnapSyncAfterLayout(); } catch { /* ignore */ }
        try { UpdateLyricsDisplay(force: true); } catch { /* ignore */ }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { TryLoadLyricsFromCacheForCurrentBestEffort(); } catch { /* ignore */ }
            try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
            try { _ = TryResolveLyricsAsync(); } catch { /* ignore */ }
        }), DispatcherPriority.Background);
        ApplyAlwaysOnTopFromSettings();
    }
}
