namespace LyllyPlayer.Services;

/// <summary>
/// Shuffle history tape + recently-played window (used for shuffle next/prev and candidate filtering).
/// </summary>
public sealed class PlayOrderService
{
    private readonly List<string> _shuffleTapeVideoIds = new();
    private readonly List<string> _recentlyPlayedVideoIds = new(5);
    private int _shuffleTapeCursor = -1;

    public IReadOnlyList<string> ShuffleTapeVideoIds => _shuffleTapeVideoIds;

    public int ShuffleTapeCursor => _shuffleTapeCursor;

    public int GetRecentlyPlayedWindowSize(int playlistEntryCount)
    {
        var n = Math.Max(0, playlistEntryCount);
        var scaled = (int)Math.Ceiling(Math.Sqrt(n));
        return Math.Clamp(scaled, 5, 40);
    }

    public int GetShuffleTapeMaxSize(int playlistEntryCount)
    {
        var n = Math.Max(0, playlistEntryCount);
        var scaled = (int)Math.Ceiling(n * 0.10);
        var max = Math.Clamp(scaled, 10, 200);
        return Math.Min(max, n == 0 ? max : n);
    }

    public void RecordNowPlayingForShuffleTape(string videoId, int playlistEntryCount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            if (_shuffleTapeCursor >= 0 && _shuffleTapeCursor < _shuffleTapeVideoIds.Count)
            {
                if (string.Equals(_shuffleTapeVideoIds[_shuffleTapeCursor], videoId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (_shuffleTapeCursor + 1 < _shuffleTapeVideoIds.Count &&
                    string.Equals(_shuffleTapeVideoIds[_shuffleTapeCursor + 1], videoId, StringComparison.OrdinalIgnoreCase))
                {
                    _shuffleTapeCursor++;
                    return;
                }

                if (_shuffleTapeCursor - 1 >= 0 &&
                    string.Equals(_shuffleTapeVideoIds[_shuffleTapeCursor - 1], videoId, StringComparison.OrdinalIgnoreCase))
                {
                    _shuffleTapeCursor--;
                    return;
                }

                if (_shuffleTapeCursor < _shuffleTapeVideoIds.Count - 1)
                    _shuffleTapeVideoIds.RemoveRange(_shuffleTapeCursor + 1, _shuffleTapeVideoIds.Count - (_shuffleTapeCursor + 1));
            }

            if (_shuffleTapeVideoIds.Count == 0 ||
                !string.Equals(_shuffleTapeVideoIds[^1], videoId, StringComparison.OrdinalIgnoreCase))
            {
                _shuffleTapeVideoIds.Add(videoId);
            }
            _shuffleTapeCursor = _shuffleTapeVideoIds.Count - 1;

            var max = GetShuffleTapeMaxSize(playlistEntryCount);
            while (_shuffleTapeVideoIds.Count > max)
            {
                _shuffleTapeVideoIds.RemoveAt(0);
                _shuffleTapeCursor--;
            }
            _shuffleTapeCursor = Math.Clamp(_shuffleTapeCursor, -1, _shuffleTapeVideoIds.Count - 1);
        }
        catch { /* ignore */ }
    }

    public void RecordRecentlyPlayedVideoId(string videoId, int playlistEntryCount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            _recentlyPlayedVideoIds.Add(videoId);
            var max = GetRecentlyPlayedWindowSize(playlistEntryCount);
            while (_recentlyPlayedVideoIds.Count > max)
                _recentlyPlayedVideoIds.RemoveAt(0);
        }
        catch { /* ignore */ }
    }

    public bool RecentlyPlayedContains(string videoId) =>
        _recentlyPlayedVideoIds.Exists(x => string.Equals(x, videoId, StringComparison.OrdinalIgnoreCase));

    public void ClearRecentlyPlayed() => _recentlyPlayedVideoIds.Clear();

    public int RecentlyPlayedCount => _recentlyPlayedVideoIds.Count;

    public void ClearShuffleTape()
    {
        _shuffleTapeVideoIds.Clear();
        _shuffleTapeCursor = -1;
    }
}
