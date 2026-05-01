using LyllyPlayer.Models;
using LyllyPlayer.Player;
using LyllyPlayer.Utils;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Owns lyrics parse state, resolved track binding, cache hydration, and tick/display line tracking.
/// Network resolution may stay in the WPF host until further extraction.
/// </summary>
public sealed class LyricsService
{
    public LyricsManager Manager { get; } = new();

    /// <summary>VideoId for which <see cref="Manager"/> lyrics are considered authoritative (may be mid-resolve).</summary>
    public string? ResolvedVideoId { get; set; }

    /// <summary>Last line index pushed to UI (compact title / status) to avoid redundant updates.</summary>
    public int LastDisplayedLyricLineIndex { get; set; } = int.MinValue;

    public void ClearParsedLyricsState()
    {
        Manager.Clear();
        LastDisplayedLyricLineIndex = int.MinValue;
        ResolvedVideoId = null;
    }

    public void ClearResolvedIfStillCurrent(string videoId)
    {
        try
        {
            if (string.Equals(ResolvedVideoId, videoId, StringComparison.OrdinalIgnoreCase))
                ResolvedVideoId = null;
        }
        catch { /* ignore */ }
    }

    public void TryLoadFromCacheForEntry(
        PlaylistEntry entry,
        bool lyricsEnabled,
        bool lyricsLocalFilesEnabled,
        Action? refreshLyricsWindow)
    {
        try
        {
            if (!lyricsEnabled)
                return;
            if (entry is null || string.IsNullOrWhiteSpace(entry.VideoId))
                return;

            if (Manager.HasLyrics && string.Equals(ResolvedVideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            var isLocal = entry.VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
            if (isLocal && !lyricsLocalFilesEnabled)
                return;

            var cacheKey = isLocal ? $"lyr_{entry.VideoId}" : $"yt_{entry.VideoId}";
            if (LyricsCache.IsMiss(cacheKey))
                return;

            var cached = LyricsCache.Get(cacheKey);
            if (string.IsNullOrWhiteSpace(cached))
                return;

            var metadata = LrcParser.TryExtractMetadata(cached);
            var cacheArtist = metadata?.Artist ?? entry.Channel;
            var cacheTitle = metadata?.Title ?? entry.Title;
            Manager.Parse(cached, artist: cacheArtist, title: cacheTitle);
            ResolvedVideoId = entry.VideoId;
            try { refreshLyricsWindow?.Invoke(); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    public void TryHydrateFromCacheForVideoId(
        string? videoId,
        bool lyricsEnabled,
        bool lyricsLocalFilesEnabled)
    {
        try
        {
            if (!lyricsEnabled)
                return;
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            if (Manager.HasLyrics && string.Equals(ResolvedVideoId, videoId, StringComparison.OrdinalIgnoreCase))
                return;

            var isLocal = videoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
            if (isLocal && !lyricsLocalFilesEnabled)
                return;

            var cacheKey = isLocal ? $"lyr_{videoId}" : $"yt_{videoId}";
            if (LyricsCache.IsMiss(cacheKey))
                return;

            var cached = LyricsCache.Get(cacheKey);
            if (string.IsNullOrWhiteSpace(cached))
                return;

            var metadata = LrcParser.TryExtractMetadata(cached);
            Manager.Parse(cached, artist: metadata?.Artist, title: metadata?.Title);
            ResolvedVideoId = videoId;
        }
        catch { /* ignore */ }
    }
}
