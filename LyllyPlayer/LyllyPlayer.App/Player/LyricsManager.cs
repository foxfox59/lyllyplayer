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

    /// <summary>
    /// Gets whether lyrics are currently loaded for the active track.
    /// </summary>
    public bool HasLyrics => _lines.Count > 0;

    /// <summary>
    /// Gets the number of lyric lines currently loaded.
    /// </summary>
    public int LineCount => _lines.Count;

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
    /// <param name="lrcText">Raw LRC text with [mm:ss.xx] timestamp tags.</param>
    public void Parse(string lrcText, double syncOffsetSeconds = 0)
    {
        _lines = LrcParser.Parse(lrcText);
        _syncOffsetSeconds = syncOffsetSeconds;
        ResetPosition();
    }

    /// <summary>
    /// Gets the current lyric line text at the given playback position.
    /// Uses binary search for efficiency and caches the last index to avoid redundant searches.
    /// </summary>
    /// <param name="positionSeconds">Current playback position in seconds.</param>
    /// <returns>The current lyric line text, or null if no lyrics loaded or position is before the first line.</returns>
    public string? GetCurrentLine(double positionSeconds)
    {
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
    /// </summary>
    /// <param name="positionSeconds">Current playback position in seconds.</param>
    /// <returns>A tuple of (currentLine, nextLine), where nextLine may be null if at the end.</returns>
    public (string Current, string? Next)? GetCurrentAndNextLine(double positionSeconds)
    {
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
    /// Clears all lyrics state. Call when the track changes.
    /// </summary>
    public void Clear()
    {
        _lines = Array.Empty<LrcParser.TimedLine>();
        _lastIndex = -1;
        _lastPositionSeconds = -1;
        _lastKey = null;
        _lastLrcText = null;
    }
}
