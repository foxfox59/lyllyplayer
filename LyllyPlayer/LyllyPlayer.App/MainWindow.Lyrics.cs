using LyllyPlayer.Models;
using LyllyPlayer.Utils;

namespace LyllyPlayer;

public partial class MainWindow
{
    private void TryLoadLyricsFromCacheForCurrentBestEffort()
    {
        try
        {
            if (!_lyricsEnabled)
                return;

            var entry = _engine.GetCurrent();
            if (entry is null || string.IsNullOrWhiteSpace(entry.VideoId))
                return;
            TryLoadLyricsFromCacheForEntryBestEffort(entry);
        }
        catch { /* ignore */ }
    }

    private void TryLoadLyricsFromCacheForEntryBestEffort(PlaylistEntry entry) =>
        _lyricsService.TryLoadFromCacheForEntry(
            entry,
            _lyricsEnabled,
            _lyricsLocalFilesEnabled,
            () => { try { _lyricsWindow?.Refresh(); } catch { /* ignore */ } });

    private void TryHydrateLyricsFromCacheForStartupVideoIdBestEffort() =>
        _lyricsService.TryHydrateFromCacheForVideoId(
            _startupSettings.CurrentVideoId,
            _lyricsEnabled,
            _lyricsLocalFilesEnabled);

    /// <summary>After a YouTube row is replaced by a local .mp3, rebind lyrics to the new id and on-disk LRC key.</summary>
    private void RefreshLyricsAfterExportReplace(PlaylistEntry newEntry)
    {
        try
        {
            if (!_lyricsEnabled)
                return;
            if (_engine.GetCurrent() is not { } play)
                return;
            if (!string.Equals(play.VideoId, newEntry.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            // Hydrate from lyr_{local id} (populated by migrating yt_{youtube id} on replace).
            // Bypass LyricsService local-files toggle so migrated YouTube lyrics still show when that option is off.
            var cacheKey = $"lyr_{play.VideoId}";
            if (!LyricsCache.IsMiss(cacheKey))
            {
                var cached = LyricsCache.Get(cacheKey);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    var metadata = LrcParser.TryExtractMetadata(cached);
                    var cacheArtist = metadata?.Artist ?? play.Channel;
                    var cacheTitle = metadata?.Title ?? play.Title;
                    _lyricsService.Manager.Parse(cached, artist: cacheArtist, title: cacheTitle);
                    _lyricsService.ResolvedVideoId = play.VideoId;
                    try { UpdateNowPlayingText(); } catch { /* ignore */ }
                    try { UpdatePlaylistTitleDisplayForNowPlaying(); } catch { /* ignore */ }
                    try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
                    return;
                }
            }

            if (_lyricsLocalFilesEnabled)
                _ = TryResolveLyricsAsync();
            else
            {
                try { UpdateNowPlayingText(); } catch { /* ignore */ }
                try { _lyricsWindow?.Refresh(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }
}
