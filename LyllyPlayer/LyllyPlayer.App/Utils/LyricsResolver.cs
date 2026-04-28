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
    /// Fetches synced LRC lyrics from the LRCLIB API by track name and artist.
    /// LRCLIB is a free, open-source lyrics provider.
    /// Returns a tuple of (LrcText, LrclibDurationSeconds), where duration may be null if unavailable.
    /// </summary>
    public static async Task<(string? LrcText, double? LrclibDurationSeconds)> FetchLyricsFromLrclibAsync(
        string trackName,
        string? artistName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return (null, null);

        var queryString = System.Web.HttpUtility.UrlEncode(trackName.Trim());
        var artistQuery = string.IsNullOrWhiteSpace(artistName)
            ? ""
            : "&artistName=" + System.Web.HttpUtility.UrlEncode(artistName.Trim());

        var url = $"https://lrclib.net/api/search?q={queryString}{artistQuery}";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        try
        {
            var json = await client.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return (null, null);

            // LRCLIB search returns a JSON array of lyric objects.
            // We want the first one that has syncedLyrics.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

            if (items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("syncedLyrics", out var synced) &&
                        synced.ValueKind == System.Text.Json.JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(synced.GetString()))
                    {
                        var lrc = synced.GetString()!.Trim();
                        if (!IsLikelySyncedLrc(lrc))
                            continue;

                        // Capture LRCLIB track duration for sync offset calculation
                        double? lrclibDuration = null;
                        if (item.TryGetProperty("duration", out var dur) &&
                            dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            lrclibDuration = dur.GetDouble();
                        }

                        return (lrc, lrclibDuration);
                    }

                    // Fallback: try plainLyrics and convert (no sync, but at least text)
                    if (item.TryGetProperty("plainLyrics", out var plain) &&
                        plain.ValueKind == System.Text.Json.JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(plain.GetString()))
                    {
                        // LRCLIB plain lyrics have a simple format; skip these
                        // since we only want synced (timed) lyrics.
                    }
                }
            }
        }
        catch
        {
            // Best-effort — LRCLIB may be down or unreachable
        }

        return (null, null);
    }
}
