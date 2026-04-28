using System;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>
/// Manages lyrics state for the currently playing track.
/// Parses LRC lyrics, caches them on disk, and resolves the current lyric line at any playback position.
/// </summary>
public sealed class LyricsManager
{
    private IReadOnlyList<LrcParser.TimedLine> _lines = Array.Empty<LrcParser.TimedLine>();
    private string? _lastKey;
    private string? _lastLrcText;
    private int _lastIndex = -1;
    private double _lastPositionSeconds = -1;
    /// <summary>Offset to apply to playback position when looking up lyrics.
    /// Calculated as YouTubeDuration - LRCLIBDuration to align studio-synced lyrics with the video.</summary>
    private double _syncOffsetSeconds = 0;

    /// <summary>Whether the loaded lyrics are plain (non-synced) text from LRCLIB.</summary>
    private bool _isPlainLyrics = false;

    /// <summary>
    /// Gets whether lyrics are currently loaded for the active track.
    /// </summary>
    public bool HasLyrics => _lines.Count > 0;

    /// <summary>
    /// Gets the number of lyric lines currently loaded.
    /// </summary>
    public int LineCount => _lines.Count;

    /// <summary>
    /// Gets whether the loaded lyrics are plain (non-synced) text.
    /// Plain lyrics cannot be synced to playback position.
    /// </summary>
    public bool IsPlainLyrics => _isPlainLyrics;

    /// <summary>
    /// Gets the artist name reported by the lyrics resolver (yt-dlp or LRCLIB).
    /// </summary>
    public string? ResolvedArtist { get; private set; }

    /// <summary>
    /// Gets the track title reported by the lyrics resolver (yt-dlp or LRCLIB).
    /// </summary>
    public string? ResolvedTitle { get; private set; }

    /// <summary>
    /// Gets a formatted "Artist - Title" string, or just "Title" if artist is unavailable.
    /// </summary>
    public string? ResolvedTitleDisplay => ResolvedArtist != null
        ? $"{ResolvedArtist} - {ResolvedTitle}"
        : ResolvedTitle;

    /// <summary>
    /// Loads lyrics from cache or fetches fresh LRC text.
    /// Returns true if lyrics were loaded successfully.
    /// </summary>
    /// <param name="cacheKey">Unique key for this track's lyrics (e.g., "yt_{videoId}" or null for cache bypass).</param>
    /// <param name="lrcText">LRC-formatted lyrics text to parse. Pass null to use cached version.</param>
    /// <returns>True if lyrics were successfully loaded and parsed.</returns>
    public bool Load(string? cacheKey, string? lrcText)
    {
        // Try cache first if no fresh text provided
        if (lrcText == null && !string.IsNullOrEmpty(cacheKey))
            lrcText = LyricsCache.Get(cacheKey);

        if (string.IsNullOrWhiteSpace(lrcText))
            return false;

        Parse(lrcText);

        // Save to cache if we have a key
        if (!string.IsNullOrEmpty(cacheKey))
        {
            LyricsCache.Set(cacheKey, lrcText);
            _lastKey = cacheKey;
            _lastLrcText = lrcText;
        }

        return true;
    }

    /// <summary>
    /// Parses LRC-formatted lyrics text into timed lines.
    /// </summary>
    /// <param name="lrcText">Raw LRC text with [mm:ss.xx] timestamp tags, or plain text for non-synced lyrics.</param>
    /// <param name="syncOffsetSeconds">Sync offset to apply when looking up lyrics.</param>
    /// <param name="artist">Artist name reported by the lyrics resolver (optional).</param>
    /// <param name="title">Track title reported by the lyrics resolver (optional).</param>
    /// <param name="isPlainLyrics">Whether the lyrics are plain (non-synced) text from LRCLIB.</param>
    public void Parse(string lrcText, double syncOffsetSeconds = 0, string? artist = null, string? title = null, bool isPlainLyrics = false)
    {
        _lines = LrcParser.Parse(lrcText);
        _syncOffsetSeconds = syncOffsetSeconds;
        _isPlainLyrics = isPlainLyrics;
        ResolvedArtist = artist;
        ResolvedTitle = title;
        ResetPosition();
    }

    /// <summary>
    /// Gets the current lyric line text at the given playback position.
    /// Uses binary search for efficiency and caches the last index to avoid redundant searches.
    /// Returns null for plain (non-synced) lyrics since they cannot be synced.
    /// </summary>
    /// <param name="positionSeconds">Current playback position in seconds.</param>
    /// <returns>The current lyric line text, or null if no lyrics loaded, position is before the first line, or lyrics are plain.</returns>
    public string? GetCurrentLine(double positionSeconds)
    {
        // Plain lyrics cannot be synced to time
        if (_isPlainLyrics)
            return null;

        // Apply sync offset so LRCLIB studio-synced lyrics align with the video's timing
        var adjustedPosition = positionSeconds - _syncOffsetSeconds;

        if (_lines.Count == 0 || adjustedPosition < 0)
            return null;

        // Reuse last search index if position moved forward (common case during playback)
        if (adjustedPosition >= _lastPositionSeconds && _lastIndex >= 0 && _lastIndex < _lines.Count)
        {
            // If current position is still within the last found line's range, return cached result
            if (_lastIndex + 1 < _lines.Count)
            {
                var nextTime = _lines[_lastIndex + 1].Seconds;
                if (adjustedPosition < nextTime)
                {
                    _lastPositionSeconds = adjustedPosition;
                    return _lines[_lastIndex].Text;
                }
            }
            else
            {
                // We're at or past the last line
                _lastPositionSeconds = adjustedPosition;
                return _lines[_lastIndex].Text;
            }
        }

        // Binary search for the last line where line.Seconds <= adjustedPosition
        int lo = 0;
        int hi = _lines.Count - 1;
        int result = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lines[mid].Seconds <= adjustedPosition)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        _lastIndex = result;
        _lastPositionSeconds = adjustedPosition;

        return result >= 0 ? _lines[result].Text : null;
    }

    /// <summary>
    /// Gets both the current and next lyric lines at the given playback position.
    /// Returns null for plain (non-synced) lyrics since they cannot be synced.
    /// </summary>
    /// <param name="positionSeconds">Current playback position in seconds.</param>
    /// <returns>A tuple of (currentLine, nextLine), or null if lyrics are plain, not loaded, or position is before the first line.</returns>
    public (string Current, string? Next)? GetCurrentAndNextLine(double positionSeconds)
    {
        // Plain lyrics cannot be synced to time
        if (_isPlainLyrics)
            return null;

        // Apply sync offset so LRCLIB studio-synced lyrics align with the video's timing
        var adjustedPosition = positionSeconds - _syncOffsetSeconds;

        if (_lines.Count == 0 || adjustedPosition < 0)
            return null;

        int result = -1;
        int lo = 0;
        int hi = _lines.Count - 1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lines[mid].Seconds <= adjustedPosition)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (result < 0)
            return null;

        var current = _lines[result].Text;
        var next = (result + 1 < _lines.Count) ? _lines[result + 1].Text : null;

        _lastIndex = result;
        _lastPositionSeconds = adjustedPosition;

        return (current, next);
    }

    /// <summary>
    /// Resets the position tracker without clearing lyrics.
    /// Call when playback seeks to a new position.
    /// </summary>
    public void ResetPosition()
    {
        _lastIndex = -1;
        _lastPositionSeconds = -1;
    }

    /// <summary>
    /// Gets the index of the current lyric line at the given playback position.
    /// Returns -1 if no lyrics loaded, position is before the first line, or lyrics are plain.
    /// Uses binary search for efficiency.
    /// </summary>
    /// <param name="positionSeconds">Current playback position in seconds.</param>
    /// <returns>The zero-based index of the current lyric line, or -1 if none.</returns>
    public int GetCurrentLineIndex(double positionSeconds)
    {
        // Plain lyrics cannot be synced to time
        if (_isPlainLyrics)
            return -1;

        // Apply sync offset so LRCLIB studio-synced lyrics align with the video's timing
        var adjustedPosition = positionSeconds - _syncOffsetSeconds;

        if (_lines.Count == 0 || adjustedPosition < 0)
            return -1;

        // Binary search for the last line where line.Seconds <= adjustedPosition
        int lo = 0;
        int hi = _lines.Count - 1;
        int result = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lines[mid].Seconds <= adjustedPosition)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the text of each lyric line, in order.
    /// </summary>
    public IReadOnlyList<string> GetLineTexts()
    {
        return _lines.Select(l => l.Text).ToArray();
    }

    /// <summary>
    /// Clears all lyrics state. Call when the track changes.
    /// </summary>
    public void Clear()
    {
        _lines = Array.Empty<LrcParser.TimedLine>();
        _lastIndex = -1;
        _lastPositionSeconds = -1;
        _lastKey = null;
        _lastLrcText = null;
        _isPlainLyrics = false;
        ResolvedArtist = null;
        ResolvedTitle = null;
    }
}
