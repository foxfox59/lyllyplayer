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

    /// <summary>Minimum score a candidate must reach to be considered a valid match.
    /// Prevents wrong songs from being selected when title matches but artist doesn't.</summary>
    private const int MinimumMatchScore = 45;

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
    /// Fetches synced LRC lyrics from LRCLIB by track title, with optional artist name for cross-referencing.
    ///
    /// The YouTube title is cleaned (common suffixes, bracketed extras removed) before searching LRCLIB.
    /// Results are scored with artist cross-reference as the primary discriminator:
    ///   1. Artist cross-reference (LRCLIB artistName vs YouTube channel/title) — primary, -30 to +120
    ///   2. Duration proximity — secondary, 0 to +15
    ///   3. Track name substring match — tertiary, 0 to +60
    ///
    /// If no synced lyrics are found but the song was found, falls back to plain (non-synced) lyrics.
    /// Returns a tuple of (LrcText, LrclibDurationSeconds, Artist, Title, IsPlainLyrics).
    /// </summary>
    /// <param name="trackName">Raw YouTube video title (will be cleaned before searching).</param>
    /// <param name="artist">YouTube channel name, used for artist cross-referencing (not for searching).</param>
    /// <param name="targetDurationSeconds">Expected duration of the YouTube video.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<(string? LrcText, double? LrclibDurationSeconds, string? Artist, string? Title, bool IsPlainLyrics)> FetchLyricsFromLrclibAsync(
        string trackName,
        string? artist,
        double? targetDurationSeconds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return (null, null, null, null, false);

        var searchQuery = CleanSearchQuery(trackName + " " + artist);
        if (string.IsNullOrWhiteSpace(searchQuery))
            return (null, null, null, null, false);

        var queryString = System.Web.HttpUtility.UrlEncode(searchQuery.Trim());
        var url = $"https://lrclib.net/api/search?q={queryString}";
        //AppLog.Info($"LRCLIB FetchLyricsFromLrclibAsync: trackName={trackName}, artist={artist}");
        //AppLog.Info($"LRCLIB query url: {url}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            var json = await client.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return (null, null, null, null, false);

            var items = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (null, null, null, null, false);

            // Collect all valid synced-lyric candidates with their scores.
            var candidates = new List<(double score, string lrc, double? duration, string? artistName, string? trackName)>();

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

                // Capture LRCLIB metadata.
                double? lrclibDuration = null;
                if (item.TryGetProperty("duration", out var dur) &&
                    dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    lrclibDuration = dur.GetDouble();
                }

                string? lrclibArtist = null;
                if (item.TryGetProperty("artistName", out var artistProp) &&
                    artistProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    lrclibArtist = artistProp.GetString()?.Trim();
                }

                string? lrclibName = null;
                if (item.TryGetProperty("trackName", out var nameProp) &&
                    nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    lrclibName = nameProp.GetString()?.Trim();
                }

                // Calculate match score.
                double score = 0;

                // 1. Artist cross-reference (PRIMARY discriminator)
                var artistScore = ComputeArtistMatchScore(lrclibArtist, artist, trackName);
                score += artistScore;

                // 2. Duration proximity (SECONDARY discriminator)
                if (targetDurationSeconds.HasValue && lrclibDuration.HasValue)
                {
                    var ytDur = targetDurationSeconds.Value;
                    var lrclibDur = lrclibDuration.Value;
                    if (ytDur > 0)
                    {
                        var diffRatio = Math.Abs(ytDur - lrclibDur) / ytDur;
                        if (diffRatio < 0.02)
                            score += 15;
                        else if (diffRatio < 0.05)
                            score += 10;
                        else if (diffRatio < 0.10)
                            score += 5;
                        else if (diffRatio < 0.15)
                            score += 2;
                    }
                }

                // 3. Track name matching (TERTIARY)
                var trackScore = 0;
                if (!string.IsNullOrWhiteSpace(lrclibName))
                {
                    var lowerQuery = searchQuery.Trim().ToLowerInvariant();
                    var lowerName = lrclibName.ToLowerInvariant();

                    // When artist match is strong, strip the LRCLIB artist name from the query
                    // to avoid polluting the track name comparison.
                    // E.g., "Hiljainen Viikate" with LRCLIB artist "Viikate" → "Hiljainen"
                    if (artistScore >= 100 && !string.IsNullOrWhiteSpace(lrclibArtist))
                    {
                        var lowerArtist = lrclibArtist.ToLowerInvariant();
                        lowerQuery = lowerQuery.Replace(lowerArtist, "");
                        // Clean up leftover separators and whitespace
                        lowerQuery = System.Text.RegularExpressions.Regex.Replace(lowerQuery, @"\s*[-|/;:,]+\s*", " ");
                        lowerQuery = System.Text.RegularExpressions.Regex.Replace(lowerQuery, @"\s+", " ").Trim();
                    }

                    if (lowerName == lowerQuery)
                        trackScore = 60;
                    else if (lowerName.StartsWith(lowerQuery))
                        trackScore = 45; // Track name starts with query — strong match
                    else if (lowerName.Contains(lowerQuery) && lowerQuery.Length >= 3)
                        trackScore = 30; // Query is somewhere in the track name — moderate match
                    else if (lowerQuery.Contains(lowerName) && lowerName.Length >= 5)
                    {
                        // Query is longer than LRCLIB track name — weak match.
                        // Only award points if the LRCLIB track name is a significant portion of the query.
                        var coverage = (double)lowerName.Length / lowerQuery.Length;
                        trackScore = (int)(15 * coverage); // 0–15 points based on coverage
                    }
                    else
                    {
                        // Partial word match — check if key words overlap
                        var wordScore = ComputeWordOverlapScore(lowerQuery, lowerName);
                        // Cap word overlap at 15 points to prevent false positives from shared common words
                        trackScore = (int)Math.Min(wordScore, 15);
                    }

                    score += trackScore;

                    AppLog.Info($"LRCLIB scoring: track={lrclibName} artistScore={artistScore} trackScore={trackScore} total={score} strippedQuery=\"{lowerQuery}\"");
                }

                if (score >= MinimumMatchScore)
                    candidates.Add((score, lrc, lrclibDuration, lrclibArtist, lrclibName));
            }

            if (candidates.Count > 0)
            {
                // Pick the highest-scored synced candidate.
                candidates.Sort((a, b) => b.score.CompareTo(a.score));
                var best = candidates[0];
                return (best.lrc, best.duration, best.artistName, best.trackName, false);
            }

            // No synced lyrics found — fall back to plain lyrics if the song was found at all.
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("plainLyrics", out var plain) ||
                    plain.ValueKind != System.Text.Json.JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(plain.GetString()))
                    continue;

                var plainLyrics = plain.GetString()!.Trim();
                if (string.IsNullOrWhiteSpace(plainLyrics))
                    continue;

                // Capture LRCLIB metadata for display.
                double? lrclibDuration = null;
                if (item.TryGetProperty("duration", out var dur) &&
                    dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    lrclibDuration = dur.GetDouble();
                }

                string? lrclibArtist = null;
                if (item.TryGetProperty("artistName", out var artistProp) &&
                    artistProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    lrclibArtist = artistProp.GetString()?.Trim();
                }

                string? lrclibName = null;
                if (item.TryGetProperty("trackName", out var nameProp) &&
                    nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    lrclibName = nameProp.GetString()?.Trim();
                }

                return (plainLyrics, lrclibDuration, lrclibArtist, lrclibName, true);
            }

            return (null, null, null, null, false);
        }
        catch
        {
            // Best-effort — LRCLIB may be down or unreachable
        }

        return (null, null, null, null, false);
    }

    /// <summary>
    /// Strips common YouTube noise from a title to produce a cleaner search query for LRCLIB.
    ///
    /// Examples:
    ///   "Dua Lipa - Don't Start Now (Official Music Video) [4K Ultra HD]"
    ///     → "Dua Lipa - Don't Start Now"
    ///   "Taylor Swift - Blank Space (Official Video) Topic"
    ///     → "Taylor Swift - Blank Space"
    ///   "Imagine Dragons - Radioactive (Visualizer)"
    ///     → "Imagine Dragons - Radioactive"
    /// </summary>
    private static string CleanSearchQuery(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return rawTitle ?? "";

        var result = rawTitle.Trim();

        // 1. Remove bracketed content: [4K], [HD], [Ultra HD], [4K Ultra HD], [8K], etc.
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*\[[^\]]*\]\s*", " ");

        // 2. Remove parenthesized music-video-type suffixes (order matters: longer patterns first)
        var suffixPatterns = new[]
        {
            @"\s*\(official\s+lyric\s+video\)\s*",
            @"\s*\(official\s+visualizer\)\s*",
            @"\s*\(official\s+subtitled\)\s*",
            @"\s*\(official\s+music\s+video\)\s*",
            @"\s*\(official\s+lyric\s+video\s+.*?\)\s*",
            @"\s*\(official\s+video\)\s*",
            @"\s*\(official\s+audio\)\s*",
            @"\s*\(lyric\s+video\)\s*",
            @"\s*\(visualizer\)\s*",
            @"\s*\(audio\)\s*",
            // @"\s*\(topic\)\s*",
            @"\s*\(lyrics?\)\s*",
            @"\s*\(subtitled?\)\s*",
            @"\s*\(feat\.\s+.*?\)\s*",
        };
        foreach (var pattern in suffixPatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // 3. Remove standalone "Topic" suffix (common for Vevo/label uploads)
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+Topic\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3.3. Strip YouTube separator "-" — it's not part of the actual title/artist
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+-\s+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3.4. Strip standalone " x " separator — YouTube uses "x" for collabs but LRCLIB uses "feat." or "&"
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+x\s+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3.5. Split camelCase concatenated words (e.g., "ArtistName" → "Artist Name")
        // This helps LRCLIB match against properly spaced artist/track names in its database.
        result = System.Text.RegularExpressions.Regex.Replace(result, @"([a-z])([A-Z])", "$1 $2");

        // 4. Normalize whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

        return result;
    }

    /// <summary>
    /// Scores how well an LRCLIB artist name matches against the YouTube channel and raw title.
    ///
    /// Scoring:
    ///   +120 — Exact match between LRCLIB artistName and YouTube channel
    ///   +100 — LRCLIB artistName is contained in channel or vice versa
    ///    +60 — LRCLIB artistName appears in the raw YouTube title
    ///    +30 — Significant LRCLIB artist words appear in the raw YouTube title (partial)
    ///    +10–30 — Shared significant words between LRCLIB artist and YouTube channel/title
    ///    -30 — No overlap at all (penalty for likely wrong artist)
    ///     +0 — LRCLIB has no artist name (neutral)
    /// </summary>
    private static double ComputeArtistMatchScore(string? lrclibArtist, string? ytChannel, string? ytRawTitle)
    {
        if (string.IsNullOrWhiteSpace(lrclibArtist))
            return 0;

        var lowerArtist = lrclibArtist.ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lowerArtist))
            return 0;

        // Extract significant words from LRCLIB artist (3+ chars, excluding common stop words)
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "or", "but", "in", "on", "at", "to", "for",
            "with", "by", "from", "as", "is", "it", "this", "that", "are", "was", "be",
            "their", "has", "have", "had", "not", "don", "did", "does", "will", "would",
            "can", "could", "may", "might", "shall", "should"
        };

        var artistWords = lowerArtist
            .Split(new[] { ' ', '-', '&', '/', '+', '.', ',', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .ToHashSet();

        if (artistWords.Count == 0)
            return 0;

        // Check 1: Exact match with YouTube channel
        if (!string.IsNullOrWhiteSpace(ytChannel))
        {
            var lowerChannel = ytChannel.ToLowerInvariant().Trim();
            if (lowerChannel == lowerArtist)
                return 120;

            // Check if LRCLIB artist appears in channel or channel contains LRCLIB artist
            if (lowerChannel.Contains(lowerArtist) || lowerArtist.Contains(lowerChannel))
                return 100;
        }

        // Check 2: LRCLIB artist appears in the raw YouTube title
        if (!string.IsNullOrWhiteSpace(ytRawTitle))
        {
            var lowerTitle = ytRawTitle.ToLowerInvariant();
            if (lowerTitle.Contains(lowerArtist))
            {
                // LRCLIB artist appears in the YouTube title, but we can't tell if it's actually the artist
                // or part of the song title (e.g., "Artist Song - Artist Name" where "Artist Song" is the song name).
                // Give a moderate score to avoid false positives.
                return 25;
            }

            // Check if significant artist words appear in the title
            var titleWords = lowerTitle
                .Split(new[] { ' ', '-', '&', '/', '+', '(', ')', '[', ']', '|', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();

            var matchingWords = artistWords.Where(w => titleWords.Contains(w)).Count();
            if (matchingWords > 0)
            {
                var coverage = matchingWords / (double)artistWords.Count;
                return 30 + (int)(30 * coverage); // 30–60 range
            }
        }

        // Check 3: Shared significant words between LRCLIB artist and YouTube name sources
        string? combinedYtName = null;
        if (!string.IsNullOrWhiteSpace(ytChannel))
            combinedYtName = ytChannel;
        if (!string.IsNullOrWhiteSpace(ytRawTitle))
            combinedYtName = combinedYtName != null ? $"{combinedYtName} {ytRawTitle}" : ytRawTitle;

        if (!string.IsNullOrWhiteSpace(combinedYtName))
        {
            var lowerCombined = combinedYtName.ToLowerInvariant();
            var combinedWords = lowerCombined
                .Split(new[] { ' ', '-', '&', '/', '+', '(', ')', '[', ']', '|', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && !stopWords.Contains(w))
                .Distinct()
                .ToHashSet();

            var sharedWords = artistWords.Intersect(combinedWords);
            if (sharedWords.Any())
            {
                var coverage = sharedWords.Count() / (double)artistWords.Count;
                return 10 + (int)(20 * coverage); // 10–30 range
            }
        }

        // No overlap at all — likely wrong artist
        return -50;
    }

    /// <summary>
    /// Computes a small bonus score for shared significant words between two strings.
    /// Used as a fallback when exact/substring matching fails.
    /// Returns 0–15 points based on word overlap.
    /// </summary>
    private static double ComputeWordOverlapScore(string a, string b)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "or", "but", "in", "on", "at", "to", "for",
            "with", "by", "from", "as", "is", "it", "this", "that", "are", "was",
            "not", "don", "did", "does", "will", "would", "can", "could", "may",
            "live", "version", "remix", "remastered", "extended", "edit", "cover"
        };

        var wordsA = a
            .Split(new[] { ' ', '-', '&', '/', '+', '.', ',', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .ToHashSet();

        var wordsB = b
            .Split(new[] { ' ', '-', '&', '/', '+', '.', ',', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0)
            return 0;

        var overlap = wordsA.Intersect(wordsB).Count();
        var maxWords = Math.Max(wordsA.Count, wordsB.Count);
        var ratio = overlap / (double)maxWords;

        // 0–15 points based on overlap ratio
        return ratio * 15;
    }
}
