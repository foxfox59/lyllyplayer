using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace LyllyPlayer.Utils;

/// <summary>
/// Fetches synced (LRC) lyrics for YouTube videos via yt-dlp.
/// Lyrics are only resolved for YouTube-sourced entries (tracks with a VideoId).
/// </summary>
public static class LyricsResolver
{
    private const int FetchTimeoutMs = 15_000;

    /// <summary>
    /// Fetches lyrics for a YouTube video by its video ID, using the given yt-dlp path.
    /// Returns LRC-formatted lyrics, or null if none found / not synced.
    /// </summary>
    public static async Task<string?> FetchLyricsForYouTubeAsync(
        string ytDlpPath,
        string videoId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        var url = $"https://www.youtube.com/watch?v={videoId}";
        return await FetchLyricsForUrl(ytDlpPath, url, ct);
    }

    /// <summary>
    /// Fetches lyrics for a YouTube video URL.
    /// </summary>
    public static async Task<string?> FetchLyricsForUrl(
        string ytDlpPath,
        string videoUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ytDlpPath))
            ytDlpPath = "yt-dlp";

        if (string.IsNullOrWhiteSpace(videoUrl))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = $"--print lyrics \"{videoUrl}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            ChildToolProcessJob.TryAssign(proc);

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);

            var timeoutTask = Task.Delay(FetchTimeoutMs, ct);
            var exitTask = proc.WaitForExitAsync();
            var completed = await Task.WhenAny(timeoutTask, exitTask);
            if (completed == timeoutTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            var lyrics = stdout.Trim();
            if (string.IsNullOrWhiteSpace(lyrics))
                return null;

            // Only return if it looks like synced LRC (contains timestamp tags)
            return IsLikelySyncedLrc(lyrics) ? lyrics : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelySyncedLrc(string lyrics)
    {
        // LRC format uses [mm:ss.xx] timestamp tags
        foreach (var ch in lyrics)
        {
            if (ch == '[')
            {
                // Found a bracket — check if it looks like a timestamp
                var rest = lyrics.Substring(lyrics.IndexOf('['));
                if (rest.StartsWith("[0") || rest.StartsWith("[1") || rest.StartsWith("[2"))
                    return true;
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Fetches synced LRC lyrics from LRCLIB by track title only, scoring results by duration proximity.
    /// YouTube channel names are unreliable as artist names, so we search by title and pick the
    /// result whose duration most closely matches the target duration (if provided).
    /// Returns a tuple of (LrcText, LrclibDurationSeconds), where duration may be null if unavailable.
    /// </summary>
    /// <param name="trackName">Song title to search for.</param>
    /// <param name="targetDurationSeconds">Expected duration of the YouTube video (used for scoring matches).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<(string? LrcText, double? LrclibDurationSeconds)> FetchLyricsFromLrclibAsync(
        string trackName,
        double? targetDurationSeconds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return (null, null);

        var queryString = System.Web.HttpUtility.UrlEncode(trackName.Trim());
        var url = $"https://lrclib.net/api/search?q={queryString}";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            var json = await client.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return (null, null);

            var items = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (null, null);

            // Collect all valid synced-lyric candidates with their scores.
            var candidates = new List<(double score, string lrc, double? duration)>();

            foreach (var item in items.EnumerateArray())
            {
                // Only consider entries with synced lyrics.
                if (!item.TryGetProperty("syncedLyrics", out var synced) ||
                    synced.ValueKind != System.Text.Json.JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(synced.GetString()))
                    continue;

                var lrc = synced.GetString()!.Trim();
                if (!IsLikelySyncedLrc(lrc))
                    continue;

                // Capture LRCLIB track duration.
                double? lrclibDuration = null;
                if (item.TryGetProperty("duration", out var dur) &&
                    dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    lrclibDuration = dur.GetDouble();
                }

                // Calculate a match score (higher = better).
                double score = 0;

                // Track name match: bonus for the LRCLIB track name containing the search query.
                if (item.TryGetProperty("name", out var nameProp) &&
                    nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var lrclibName = nameProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(lrclibName))
                    {
                        var lowerQuery = trackName.Trim().ToLowerInvariant();
                        var lowerName = lrclibName.ToLowerInvariant();
                        if (lowerName == lowerQuery)
                            score += 100;
                        else if (lowerName.Contains(lowerQuery) || lowerQuery.Contains(lowerName))
                            score += 50;
                    }
                }

                // Artist match bonus if LRCLIB has an artist field.
                if (item.TryGetProperty("artist", out var artistProp) &&
                    artistProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // Don't score on artist — we didn't search by artist, so any match is neutral.
                    score += 5;
                }

                // Duration proximity is the primary tiebreaker.
                if (targetDurationSeconds.HasValue && lrclibDuration.HasValue)
                {
                    var ytDur = targetDurationSeconds.Value;
                    var lrclibDur = lrclibDuration.Value;
                    if (ytDur > 0)
                    {
                        var diffRatio = Math.Abs(ytDur - lrclibDur) / ytDur;
                        if (diffRatio < 0.02)
                            score += 200; // Within 2% — excellent match
                        else if (diffRatio < 0.05)
                            score += 100; // Within 5% — very good
                        else if (diffRatio < 0.10)
                            score += 50;  // Within 10% — good
                        else if (diffRatio < 0.15)
                            score += 20;  // Within 15% — acceptable
                        // Larger diffs get no bonus (duration mismatch likely wrong song)
                    }
                }

                if (score > 0)
                    candidates.Add((score, lrc, lrclibDuration));
            }

            if (candidates.Count == 0)
                return (null, null);

            // Pick the highest-scored candidate.
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var best = candidates[0];

            return (best.lrc, best.duration);
        }
        catch
        {
            // Best-effort — LRCLIB may be down or unreachable
        }

        return (null, null);
    }
}
