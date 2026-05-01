using LyllyPlayer.Models;

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
}
