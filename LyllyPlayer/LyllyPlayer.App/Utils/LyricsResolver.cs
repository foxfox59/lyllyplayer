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
    private const int LrclibTimeoutSeconds = 25;
    private const int FetchTimeoutMs = 15_000;

    /// <summary>Minimum score a candidate must reach to be considered a valid match.
    /// Prevents wrong songs from being selected when title matches but artist doesn't.</summary>
    private const int MinimumMatchScore = 30;

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
    /// Returns a tuple of (LrcText, LrclibDurationSeconds, Artist, Title, IsPlainLyrics, IsDefinitiveMiss).
    /// IsDefinitiveMiss is true only when LRCLIB was successfully queried but no acceptable lyrics were found.
    /// Network/timeout/parse failures are surfaced as exceptions and must NOT be treated as misses by callers.
    /// </summary>
    /// <param name="trackName">Raw YouTube video title (will be cleaned before searching).</param>
    /// <param name="artist">YouTube channel name, used for artist cross-referencing (not for searching).</param>
    /// <param name="targetDurationSeconds">Expected duration of the YouTube video.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<(string? LrcText, double? LrclibDurationSeconds, string? Artist, string? Title, bool IsPlainLyrics, bool IsDefinitiveMiss)> FetchLyricsFromLrclibAsync(
        string trackName,
        string? artist,
        double? targetDurationSeconds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return (null, null, null, null, false, false);

        var searchQuery = BuildLrclibSearchQuery(trackName, artist);
        if (string.IsNullOrWhiteSpace(searchQuery))
            return (null, null, null, null, false, false);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(LrclibTimeoutSeconds) };

        // Single-query policy: do not issue a second LRCLIB request (reduces load / avoids double misses).
        // However, do one retry on timeout/transient IO to avoid poisoning UX under temporary LRCLIB slowness.
        List<(double score, string lrc, double? duration, string? artistName, string? trackName)> candidates;
        try
        {
            candidates = await SearchAndScoreAsync(client, searchQuery, trackName, artist, targetDurationSeconds, ct);
        }
        catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
        {
            AppLog.Warn($"LRCLIB request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
            candidates = await SearchAndScoreAsync(client, searchQuery, trackName, artist, targetDurationSeconds, ct);
        }

        if (candidates.Count > 0)
        {
            // Pick the highest-scored synced candidate.
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var best = candidates[0];
            return (best.lrc, best.duration, best.artistName, best.trackName, false, false);
        }

        // No synced lyrics found — check plain lyrics (same single-query policy).
        (string? LrcText, double? Duration, string? Artist, string? Title, bool IsPlainLyrics)? plainFallback;
        try
        {
            plainFallback = await SearchPlainLyricsAsync(client, searchQuery, trackName, artist, ct);
        }
        catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
        {
            AppLog.Warn($"LRCLIB plain-lyrics request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
            plainFallback = await SearchPlainLyricsAsync(client, searchQuery, trackName, artist, ct);
        }
        if (plainFallback != null)
            return (plainFallback.Value.LrcText, plainFallback.Value.Duration, plainFallback.Value.Artist, plainFallback.Value.Title, true, false);

        // Successful query but no lyrics found.
        return (null, null, null, null, false, true);
    }

    private static bool IsTransientLrclibFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        // HttpClient timeouts commonly surface as TaskCanceledException / TimeoutException chains.
        if (ex is TimeoutException)
            return true;
        if (ex is TaskCanceledException)
            return true;

        // Also treat IO-level aborts as transient.
        if (ex is System.IO.IOException)
            return true;
        if (ex.InnerException is not null)
            return IsTransientLrclibFailure(ex.InnerException, ct);
        return false;
    }

    private static string BuildLrclibSearchQuery(string trackName, string? artist)
    {
        try
        {
            var tokens = new List<string>(32);

            // 1) Extract tokens from the title itself (handles "Artist - Title", "A x B - Song", etc.)
            tokens.AddRange(ExtractTokensFromTrackTitle(trackName));

            // 2) Optionally include artist/channel tokens (weak signal for YouTube; avoid Topic/VEVO/etc).
            if (LooksLikeTrustworthyArtistHint(artist))
                tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(artist!)));

            // 3) Remove junk tokens and dedupe.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filtered = new List<string>(tokens.Count);
            foreach (var t in tokens)
            {
                var w = (t ?? "").Trim();
                if (string.IsNullOrWhiteSpace(w))
                    continue;
                if (IsJunkToken(w))
                    continue;
                if (seen.Add(w))
                    filtered.Add(w);
            }

            return CleanSearchQuery(string.Join(' ', filtered));
        }
        catch
        {
            return CleanSearchQuery(trackName);
        }
    }

    private static bool LooksLikeTrustworthyArtistHint(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;
        var a = artist.Trim();
        if (a.Length < 2)
            return false;
        var lower = a.ToLowerInvariant();
        // Very common non-artist channels.
        if (lower.Contains("- topic")) return false;
        if (lower.Contains(" vevo")) return false;
        if (lower.Contains("records")) return false;
        if (lower.Contains("label")) return false;
        if (lower.Contains("official") && (lower.Contains("music") || lower.Contains("channel"))) return false;
        return true;
    }

    private static IEnumerable<string> ExtractTokensFromTrackTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        // Preserve the raw for structural split, but normalize common separators and feature markers first.
        var s = raw.Trim();
        s = StripBracketedJunk(s);
        s = NormalizeFeatureMarkers(s);

        // Prefer safe separators with spaces to avoid splitting hyphenated words.
        var parts = SplitOnFirstSeparator(s, new[]
        {
            " - ", " – ", " — ", " : ", " | "
        });

        var tokens = new List<string>(32);
        if (parts is { Left: { } l, Right: { } r })
        {
            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(l)));
            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(r)));
        }
        else
        {
            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(s)));
        }

        return tokens;
    }

    private static (string Left, string Right)? SplitOnFirstSeparator(string s, string[] seps)
    {
        try
        {
            foreach (var sep in seps)
            {
                var idx = s.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && idx < s.Length - sep.Length - 1)
                {
                    var left = s.Substring(0, idx).Trim();
                    var right = s.Substring(idx + sep.Length).Trim();
                    if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                        return (left, right);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string StripBracketedJunk(string s)
    {
        try
        {
            // Remove [...] blocks (resolutions, etc).
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\[[^\]]*\]\s*", " ");
            // Remove most (...) blocks, but keep those that contain feat/ft (we normalize later).
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\s*\((?!\s*(?:feat|ft|featuring)\b)[^)]*\)\s*",
                " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { /* ignore */ }
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string NormalizeFeatureMarkers(string s)
    {
        try
        {
            // Normalize to word boundaries so tokenization is stable.
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bfeaturing\b", " feat ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bfeat\.?\b", " feat ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bft\.?\b", " feat ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Common collab separator used as "A x B" or "A × B"
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s[×xX]\s", " feat ", System.Text.RegularExpressions.RegexOptions.None);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bw/\b", " feat ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { /* ignore */ }
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string CleanTrackPrefixNumbers(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return s ?? "";

        var t = s.Trim();
        try
        {
            // Common filename-derived prefixes: "02 - Song", "02. Song", "1-02 - Song", "CD1-02 - Song"
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*\d{1,3}\s*[-._)\]]\s+", "", System.Text.RegularExpressions.RegexOptions.None);
            // Dual-number prefixes: "1-02 - Song", "06-06 - Song" (allow optional whitespace before the separator after the 2nd number).
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*\d{1,3}\s*-\s*\d{1,3}\s*[-._]\s+", "", System.Text.RegularExpressions.RegexOptions.None);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*(?:cd|disc)\s*\d+\s*[-._]\s*\d{1,3}\s*[-._]\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { /* ignore */ }

        return t.Trim();
    }

    private static IEnumerable<string> TokenizeBasic(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return Array.Empty<string>();
        try
        {
            // Collapse apostrophes inside words so "I've" -> "Ive" (prevents losing the leading letter as junk).
            try
            {
                s = System.Text.RegularExpressions.Regex.Replace(
                    s,
                    @"(?<=\p{L})['’](?=\p{L})",
                    "",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            catch { /* ignore */ }

            // Preserve dotted acronyms (LRCLIB may differentiate "C.R.E.A.M" vs "CREAM").
            var extra = new List<string>(4);
            try
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             s,
                             @"\b(?:[A-Za-z]\.){2,}[A-Za-z]\b|\b(?:[A-Za-z]\.){2,}\b",
                             System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    var raw = m.Value.Trim();
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    extra.Add(raw);
                }
                // Remove them from the base string so punctuation stripping below doesn't shred them into single-letter tokens.
                s = System.Text.RegularExpressions.Regex.Replace(
                    s,
                    @"\b(?:[A-Za-z]\.){2,}[A-Za-z]\b|\b(?:[A-Za-z]\.){2,}\b",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            catch { /* ignore */ }

            // Keep letters/numbers, split on punctuation.
            var cleaned = System.Text.RegularExpressions.Regex.Replace(s, @"[^\p{L}\p{N}]+", " ");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
            var baseTokens = string.IsNullOrWhiteSpace(cleaned)
                ? Array.Empty<string>()
                : cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (extra.Count == 0)
                return baseTokens;
            if (baseTokens.Length == 0)
                return extra;
            var all = new List<string>(extra.Count + baseTokens.Length);
            all.AddRange(extra);
            all.AddRange(baseTokens);
            return all;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsJunkToken(string token)
    {
        var t = token.Trim().ToLowerInvariant();
        if (t.Length <= 1)
        {
            // Keep numeric tokens (e.g. "Part 2", "Vol 1") — single digits are meaningful for disambiguation.
            // Still drop single-letter noise.
            return !char.IsDigit(t[0]);
        }

        return t is
               "official" or "video" or "music" or "mv" or "lyrics" or "lyric" or "audio" or "visualizer" or "hd" or "hq" or "uhd"
               or "4k" or "8k" or "1080p" or "720p" or "remastered" or "remaster" or "explicit";
    }

    /// <summary>Searches LRCLIB and scores synced-lyric candidates against the given query.</summary>
    private static async Task<List<(double score, string lrc, double? duration, string? artistName, string? trackName)>> SearchAndScoreAsync(
        HttpClient client,
        string query,
        string ytTrackName,
        string? ytArtist,
        double? targetDurationSeconds,
        CancellationToken ct,
        bool fallback = false)
    {
        var candidates = new List<(double score, string lrc, double? duration, string? artistName, string? trackName)>();
        var queryString = System.Web.HttpUtility.UrlEncode(query.Trim());
        var url = $"https://lrclib.net/api/search?q={queryString}";
        if (!fallback)
            AppLog.Info($"LRCLIB query url: {url}");

        try
        {
            var json = await client.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return candidates;

            var items = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
                return candidates;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var synced) ||
                    synced.ValueKind != System.Text.Json.JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(synced.GetString()))
                    continue;

                var lrc = synced.GetString()!.Trim();
                if (!IsLikelySyncedLrc(lrc))
                    continue;

                double? lrclibDuration = null;
                if (item.TryGetProperty("duration", out var dur) && dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                    lrclibDuration = dur.GetDouble();

                string? lrclibArtist = null;
                if (item.TryGetProperty("artistName", out var artistProp) && artistProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    lrclibArtist = artistProp.GetString()?.Trim();

                string? lrclibName = null;
                if (item.TryGetProperty("trackName", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    lrclibName = nameProp.GetString()?.Trim();

                double score = 0;

                // 1. Artist cross-reference
                var artistScore = ComputeArtistMatchScore(lrclibArtist, ytArtist, ytTrackName);
                score += artistScore;

                // 2. Duration proximity
                if (targetDurationSeconds.HasValue && lrclibDuration.HasValue)
                {
                    var ytDur = targetDurationSeconds.Value;
                    var lrclibDur = lrclibDuration.Value;
                    if (ytDur > 0)
                    {
                        var diffRatio = Math.Abs(ytDur - lrclibDur) / ytDur;
                        if (diffRatio < 0.02) score += 15;
                        else if (diffRatio < 0.05) score += 10;
                        else if (diffRatio < 0.10) score += 5;
                        else if (diffRatio < 0.15) score += 2;
                    }
                }

                // 3. Track name matching
                var trackScore = 0;
                if (!string.IsNullOrWhiteSpace(lrclibName))
                {
                    var lowerQuery = query.Trim().ToLowerInvariant();
                    var lowerName = lrclibName.ToLowerInvariant();

                    if (artistScore >= 100 && !string.IsNullOrWhiteSpace(lrclibArtist))
                    {
                        var lowerArtist = lrclibArtist.ToLowerInvariant();
                        lowerQuery = lowerQuery.Replace(lowerArtist, "");
                        lowerQuery = System.Text.RegularExpressions.Regex.Replace(lowerQuery, @"\s*[-|/;:,]+\s*", " ");
                        lowerQuery = System.Text.RegularExpressions.Regex.Replace(lowerQuery, @"\s+", " ").Trim();
                    }

                    if (lowerName == lowerQuery)
                        trackScore = 60;
                    else if (lowerName.StartsWith(lowerQuery))
                        trackScore = 45;
                    else if (lowerName.Contains(lowerQuery) && lowerQuery.Length >= 3)
                        trackScore = 30;
                    else if (lowerQuery.Contains(lowerName) && lowerName.Length >= 5)
                    {
                        var coverage = (double)lowerName.Length / lowerQuery.Length;
                        trackScore = (int)(15 * coverage);
                    }
                    else
                    {
                        var wordScore = ComputeWordOverlapScore(lowerQuery, lowerName);
                        trackScore = (int)Math.Min(wordScore, 15);
                    }

                    score += trackScore;
                    AppLog.Info($"LRCLIB scoring: track={lrclibName} artistScore={artistScore} trackScore={trackScore} total={score} query=\"{query}\"");
                }

                if (score >= MinimumMatchScore)
                    candidates.Add((score, lrc, lrclibDuration, lrclibArtist, lrclibName));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Do NOT treat network/parse failures as "no results" — callers must not cache misses for failures.
            throw;
        }

        return candidates;
    }

    /// <summary>Searches LRCLIB for plain (non-synced) lyrics fallback.</summary>
    private static async Task<(string? LrcText, double? Duration, string? Artist, string? Title, bool IsPlainLyrics)?> SearchPlainLyricsAsync(
        HttpClient client,
        string query,
        string ytTrackName,
        string? ytArtist,
        CancellationToken ct)
    {
        var queryString = System.Web.HttpUtility.UrlEncode(query.Trim());
        var url = $"https://lrclib.net/api/search?q={queryString}";

        try
        {
            var json = await client.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var items = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("plainLyrics", out var plain) ||
                    plain.ValueKind != System.Text.Json.JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(plain.GetString()))
                    continue;

                var plainLyrics = plain.GetString()!.Trim();
                if (string.IsNullOrWhiteSpace(plainLyrics))
                    continue;

                double? lrclibDuration = null;
                if (item.TryGetProperty("duration", out var dur) && dur.ValueKind == System.Text.Json.JsonValueKind.Number)
                    lrclibDuration = dur.GetDouble();

                string? lrclibArtist = null;
                if (item.TryGetProperty("artistName", out var artistProp) && artistProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    lrclibArtist = artistProp.GetString()?.Trim();

                string? lrclibName = null;
                if (item.TryGetProperty("trackName", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    lrclibName = nameProp.GetString()?.Trim();

                return (plainLyrics, lrclibDuration, lrclibArtist, lrclibName, true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Do NOT treat network/parse failures as "no results" — callers must not cache misses for failures.
            throw;
        }

        return null;
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
            // Catch-all for parenthesized metadata suffixes (edit/remix/version typos)
            @"\s*\(.*?(?:edit|remix|v.?r.?s?o?n).*?\)\s*",
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
