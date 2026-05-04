using System.Diagnostics;
using System.Net.Http;
using System.Text;
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
    /// Hard cutoff for LRCLIB duration mismatch. If the absolute difference between the expected duration and the
    /// LRCLIB candidate duration exceeds this, the candidate is rejected (prevents clear misses).
    /// </summary>
    private const double LrclibMaxAbsoluteDurationMismatchSeconds = 10;

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
        //
        // NOTE: We also allow a *single* fallback query that uses only the cleaned track name when the richer query
        // produced 0 viable candidates. This improves hit rate when channel/tags are wrong (uploader handles, etc.).
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

        // Fallback query: title-only (cleaned), when the richer query finds nothing.
        // This is intentionally conservative: one extra request at most, and only when queries differ.
        if (candidates.Count == 0)
        {
            string? titleOnlyQuery = null;
            try
            {
                // Use the same tokenization path but with no artist hint to avoid including uploader/channel garbage.
                titleOnlyQuery = BuildLrclibSearchQuery(trackName, artist: null);
            }
            catch { /* ignore */ }

            if (!string.IsNullOrWhiteSpace(titleOnlyQuery) &&
                !string.Equals(titleOnlyQuery.Trim(), searchQuery.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    candidates = await SearchAndScoreAsync(client, titleOnlyQuery, trackName, ytArtist: null, targetDurationSeconds, ct, fallback: true);
                }
                catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
                {
                    AppLog.Warn($"LRCLIB title-only request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
                    candidates = await SearchAndScoreAsync(client, titleOnlyQuery, trackName, ytArtist: null, targetDurationSeconds, ct, fallback: true);
                }
            }
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

        // Plain-lyrics title-only fallback when the richer query produced nothing.
        if (plainFallback is null)
        {
            string? titleOnlyQuery = null;
            try { titleOnlyQuery = BuildLrclibSearchQuery(trackName, artist: null); } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(titleOnlyQuery) &&
                !string.Equals(titleOnlyQuery.Trim(), searchQuery.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    plainFallback = await SearchPlainLyricsAsync(client, titleOnlyQuery, trackName, ytArtist: null, ct);
                }
                catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
                {
                    AppLog.Warn($"LRCLIB title-only plain-lyrics request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
                    plainFallback = await SearchPlainLyricsAsync(client, titleOnlyQuery, trackName, ytArtist: null, ct);
                }
            }
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
            // Token-bag policy: treat the raw input string as the only source of truth.
            // Callers should pass a combined string when they have multiple fields (e.g. "Title - Channel").
            // We do not use the separate 'artist' parameter as a hint anymore.
            var titleTokens = ExtractTokensFromTrackTitle(trackName, artistHint: null).ToList();
            tokens.AddRange(titleTokens);
            var titleSignalTokenCount = CountDistinctMeaningfulTokens(titleTokens);

            // 2) Remove junk tokens and dedupe.
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

    private static int CountDistinctMeaningfulTokens(IEnumerable<string> tokens)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens ?? Array.Empty<string>())
            {
                var w = (t ?? "").Trim();
                if (string.IsNullOrWhiteSpace(w))
                    continue;
                if (IsJunkToken(w))
                    continue;
                seen.Add(w);
            }
            return seen.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TitleContainsComparableKey(string title, string needle)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(needle))
            return false;
        try
        {
            var kTitle = BuildComparableNameKey(title);
            var kNeedle = BuildComparableNameKey(needle);
            if (string.IsNullOrWhiteSpace(kTitle) || string.IsNullOrWhiteSpace(kNeedle))
                return false;
            return kTitle.Contains(kNeedle, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TitleAppearsToEndWithUploaderTag(string rawTitle, string? artistHint)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(artistHint))
            return false;

        try
        {
            // Mirror the same normalization used by ExtractTokensFromTrackTitle before splitting.
            var s = rawTitle.Trim();
            s = StripBracketedJunk(s);
            s = NormalizeFeatureMarkers(s);
            s = NormalizeLooseTitleSeparators(s);

            var parts = SplitOnSeparatorsUpTo3(s, new[] { " - ", " – ", " — ", " : ", " | " });
            if (parts.Count < 3)
                return false;

            var third = parts[2];
            if (string.IsNullOrWhiteSpace(third))
                return false;

            // If it matches the channel/artist hint, it's almost certainly a channel/uploader tag for this title style.
            // In that case, do not re-add channel tokens into the LRCLIB query.
            return ThirdPartMatchesArtistHint(third, artistHint);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeTrustworthyArtistHint(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;
        var a = NormalizeYoutubeArtistHint(artist);
        if (a.Length < 2)
            return false;
        var lower = a.ToLowerInvariant();
        // Very common non-artist channels.
        if (lower.Contains(" vevo")) return false;
        if (lower.Contains("records")) return false;
        if (lower.Contains("label")) return false;
        if (lower.Contains("official") && (lower.Contains("music") || lower.Contains("channel"))) return false;
        return true;
    }

    private static string NormalizeYoutubeArtistHint(string artist)
    {
        // Many "Topic" channels are actually the correct artist; keep the artist, drop "- Topic".
        var a = (artist ?? "").Trim();
        try
        {
            a = System.Text.RegularExpressions.Regex.Replace(
                a,
                @"\s*-\s*topic\s*$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }
        catch { /* ignore */ }
        return a;
    }

    private static IEnumerable<string> ExtractTokensFromTrackTitle(string raw, string? artistHint)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        // Preserve the raw for structural split, but normalize common separators and feature markers first.
        var s = NormalizeUnicodeStylizedLetters(raw).Trim();
        s = StripBracketedJunk(s);
        s = NormalizeFeatureMarkers(s);
        s = StripFeaturedArtistsTail(s);
        s = NormalizeLooseTitleSeparators(s);

        // Prefer safe separators with spaces to avoid splitting hyphenated words.
        // Also handle common "Artist - Title - Uploader" patterns by dropping the 3rd part when it looks like a channel tag.
        var splitParts = SplitOnSeparatorsUpTo3(s, new[]
        {
            " - ", " – ", " — ", " : ", " | "
        });

        var tokens = new List<string>(32);
        if (splitParts.Count >= 2)
        {
            var left = splitParts[0];
            var right = splitParts[1];

            // If we have 3 parts and the last part looks like an uploader/channel tag, ignore it.
            if (splitParts.Count >= 3)
            {
                var third = splitParts[2];
                // Strict rule: if we already have a clean two-part "A - B", ignore any 3rd part.
            }

            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(left)));
            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(right)));
        }
        else
        {
            tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(s)));
        }

        return tokens;
    }

    private static string StripFeaturedArtistsTail(string s)
    {
        // For search, featured artists tend to reduce hit rate (especially when tags are messy).
        // Keep the primary artist/title part and drop trailing "feat ..." clause.
        try
        {
            if (string.IsNullOrWhiteSpace(s))
                return s ?? "";
            // Only strip when "feat" occurs as a word.
            var cut = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\s+\bfeat\b\s+.*$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            cut = System.Text.RegularExpressions.Regex.Replace(cut, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(cut) ? s : cut;
        }
        catch
        {
            return s;
        }
    }

    private static string NormalizeLooseTitleSeparators(string s)
    {
        // Some YouTube titles use "-" / "–" / "—" as separators without consistent spacing:
        //   "A - B - C" / "A-B - C" / "A -B-C"
        // We only normalize when there are *multiple* separators, which strongly suggests "chunks" not hyphenated words.
        try
        {
            if (string.IsNullOrWhiteSpace(s))
                return s ?? "";

            var dashCount = 0;
            foreach (var ch in s)
            {
                if (ch is '-' or '–' or '—')
                    dashCount++;
            }
            if (dashCount < 2)
                return s;

            // Normalize various dashes into " - " with flexible whitespace.
            // Important: do this even if one separator is already spaced; mixed formatting is common ("A - B -C").
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\s*[-–—]\s*",
                " - ",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            // Clean up repeated spaces.
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }
        catch { /* ignore */ }
        return s;
    }

    private static bool ThirdPartMatchesArtistHint(string thirdPart, string? artistHint)
    {
        // Conservative: only drop the 3rd part if we also have an artist/channel hint and it substantially matches.
        // This covers cases like "Artist - Song - Uploader" where uploader == channel, without assuming all 3-part titles are uploader-tagged.
        if (string.IsNullOrWhiteSpace(thirdPart) || string.IsNullOrWhiteSpace(artistHint))
            return false;

        try
        {
            var a = NormalizeYoutubeArtistHint(artistHint);
            if (string.IsNullOrWhiteSpace(a))
                return false;

            // Compare using a "collapsed" key.
            var kThird = BuildComparableNameKey(thirdPart);
            var kHint = BuildComparableNameKey(a);
            if (!string.IsNullOrWhiteSpace(kThird) && !string.IsNullOrWhiteSpace(kHint) && kThird == kHint)
                return true;

            // Direct contains in either direction after normalization.
            var lowerThird = thirdPart.Trim().ToLowerInvariant();
            var lowerHint = a.Trim().ToLowerInvariant();
            if (lowerThird == lowerHint)
                return true;
            if (lowerThird.Contains(lowerHint) || lowerHint.Contains(lowerThird))
                return true;

            // Token overlap: require strong coverage to avoid false positives.
            var tThird = TokenizeBasic(thirdPart).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 3).Distinct().ToList();
            var tHint = TokenizeBasic(a).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 3).Distinct().ToList();
            if (tThird.Count == 0 || tHint.Count == 0)
                return false;

            var overlap = tThird.Intersect(tHint).Count();
            var denom = Math.Min(tThird.Count, tHint.Count);
            if (denom <= 0)
                return false;

            var ratio = overlap / (double)denom;
            return ratio >= 0.75;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildComparableNameKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        try
        {
            Span<char> buf = stackalloc char[Math.Min(256, s.Length)];
            var n = 0;
            foreach (var ch in s.Trim())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (n >= buf.Length)
                        break;
                    buf[n++] = char.ToLowerInvariant(ch);
                }
            }
            return n == 0 ? "" : new string(buf[..n]);
        }
        catch
        {
            return "";
        }
    }

    private static List<string> SplitOnSeparatorsUpTo3(string s, string[] seps)
    {
        var parts = new List<string>(3);
        if (string.IsNullOrWhiteSpace(s))
            return parts;

        var remaining = s.Trim();
        for (var i = 0; i < 2; i++)
        {
            (string Left, string Right)? found = null;
            foreach (var sep in seps)
            {
                var idx = remaining.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && idx < remaining.Length - sep.Length - 1)
                {
                    var left = remaining.Substring(0, idx).Trim();
                    var right = remaining.Substring(idx + sep.Length).Trim();
                    if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                    {
                        found = (left, right);
                        break;
                    }
                }
            }

            if (found is null)
                break;

            parts.Add(found.Value.Left);
            remaining = found.Value.Right;
        }

        if (!string.IsNullOrWhiteSpace(remaining))
            parts.Add(remaining.Trim());

        // If no separators were found, return single part.
        if (parts.Count == 0)
            parts.Add(s.Trim());

        return parts;
    }

    private static bool LooksLikeUploaderOrChannelTag(string s)
    {
        // Heuristic: many uploader/channel tags are a single token with no spaces,
        // or a short phrase that is clearly non-title (e.g. "Topic").
        var t = (s ?? "").Trim();
        if (t.Length < 3)
            return false;

        var lower = t.ToLowerInvariant();
        if (lower == "topic" || lower.EndsWith(" topic"))
            return true;

        // Long all-lowercase single-token tags are often uploader handles, not artists.
        // Keep this conservative: only treat as handle-like when it's long enough to be unlikely as a real artist casing.
        try
        {
            if (!t.Contains(' ') && t.Length >= 12)
            {
                var allLettersOrDigits = true;
                var anyUpper = false;
                var anyLower = false;
                var anyDigit = false;
                foreach (var ch in t)
                {
                    if (!char.IsLetterOrDigit(ch))
                    {
                        allLettersOrDigits = false;
                        break;
                    }
                    if (char.IsUpper(ch)) anyUpper = true;
                    if (char.IsLower(ch)) anyLower = true;
                    if (char.IsDigit(ch)) anyDigit = true;
                }

                if (allLettersOrDigits && anyLower && !anyUpper && !anyDigit)
                    return true;
            }
        }
        catch { /* ignore */ }

        // Two-token uploader handles — treat as uploader-like when both tokens
        // look like name parts and the string has no punctuation.
        try
        {
            if (t.Contains(' ') && !t.Contains('-') && !t.Contains('|') && !t.Contains(':') && !t.Contains('/') && !t.Contains('&'))
            {
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    var p0 = parts[0];
                    var p1 = parts[1];
                    if (p0.Length >= 4 && p1.Length >= 4 &&
                        char.IsUpper(p0[0]) && char.IsUpper(p1[0]) &&
                        p0.All(ch => char.IsLetterOrDigit(ch)) &&
                        p1.All(ch => char.IsLetterOrDigit(ch)))
                        return true;
                }
            }
        }
        catch { /* ignore */ }

        // Single-token camel/pascal case handles like "SomeUploader123"
        if (!t.Contains(' '))
        {
            var hasLetter = false;
            var hasUpper = false;
            var hasLower = false;
            var hasDigit = false;
            foreach (var ch in t)
            {
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                    if (char.IsUpper(ch)) hasUpper = true;
                    if (char.IsLower(ch)) hasLower = true;
                }
                if (char.IsDigit(ch))
                    hasDigit = true;
            }

            if (hasLetter && (hasDigit || (hasUpper && hasLower)) && t.Length >= 6)
                return true;
        }

        return false;
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
            // Also keep disambiguation qualifiers that are often crucial for LRCLIB matching (e.g. studio outtake).
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\s*\((?!\s*(?:feat|ft|featuring|studio|outtake|demo|live|acoustic|session|alternate|alt|take|out\-?take)\b)[^)]*\)\s*",
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
            // Normalize hyphenated letter segments like "Word-X" into "Word X".
            try
            {
                s = System.Text.RegularExpressions.Regex.Replace(
                    s,
                    @"(?<=\p{L})-(?=\p{L})",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            catch { /* ignore */ }

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
            // Single-letter tokens are usually noise, but keep ones that are meaningful in common hyphenated titles.
            if (t is "o")
                return false;

            // Keep numeric tokens (e.g. "Part 2", "Vol 1") — single digits are meaningful for disambiguation.
            // Still drop single-letter noise.
            return !char.IsDigit(t[0]);
        }

        // Common uploader/channel tags that pollute LRCLIB queries.
        try
        {
            if (t.EndsWith("vevo", StringComparison.OrdinalIgnoreCase) && t.Length >= 6)
                return true;
        }
        catch { /* ignore */ }

        // Glued uploader/channel handles like "ArtistOfficial" commonly get camel-split later into "Artist Official",
        // which pollutes the query. Drop the glued form up front.
        try
        {
            if (!t.Contains(' ') && t.EndsWith("official", StringComparison.OrdinalIgnoreCase) && t.Length >= 10)
                return true;
        }
        catch { /* ignore */ }

        return t is
               "official" or "video" or "music" or "mv" or "lyrics" or "lyric" or "audio" or "visualizer" or "hd" or "hq" or "uhd"
               or "4k" or "8k" or "1080p" or "720p" or "remastered" or "remaster" or "explicit"
               or "records" or "recordings" or "label";
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

                // Hard reject obvious duration mismatches.
                if (targetDurationSeconds is double ytDur0 &&
                    lrclibDuration is double lrDur0 &&
                    ytDur0 > 0.5 && lrDur0 > 0.5 &&
                    Math.Abs(ytDur0 - lrDur0) > LrclibMaxAbsoluteDurationMismatchSeconds)
                {
                    continue;
                }

                // 1) Token overlap match (primary): query tokens vs candidate tokens (LRCLIB artistName + trackName).
                // We do not privilege "artist" vs "title"; it's one combined bag-of-words.
                var combinedCandidate = BuildCombinedCandidateString(lrclibArtist, lrclibName);
                var combinedScore = ComputeCombinedMatchScore(query, combinedCandidate);
                score += combinedScore;

                // 2) Duration proximity (secondary nudge)
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

                // Minimal diagnostic log; query URL already logged for non-fallback calls.
                if (!fallback)
                    AppLog.Info($"LRCLIB scoring: track={lrclibName ?? "(none)"} artist={lrclibArtist ?? "(none)"} overlapScore={combinedScore:F0} total={score:F0} query=\"{query}\"");

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

    private static string BuildCombinedCandidateString(string? lrclibArtist, string? lrclibName)
        => $"{lrclibArtist ?? ""} {lrclibName ?? ""}".Trim();

    private static double ComputeCombinedMatchScore(string combinedQuery, string combinedCandidate)
    {
        // Returns roughly 0..140. High score when MORE meaningful query tokens appear in candidate.
        // Ranking goal: candidates with more matching tokens should always win.
        try
        {
            var qTokens = TokenizeMeaningfulForMatch(combinedQuery);
            var cTokens = TokenizeMeaningfulForMatch(combinedCandidate);
            if (qTokens.Count == 0 || cTokens.Count == 0)
                return 0;

            var overlap = qTokens.Intersect(cTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (overlap <= 0)
                return 0;

            // Guardrail: avoid false positives driven by a single shared short token.
            if (qTokens.Count >= 3 && overlap < 2)
                return 0;

            if (overlap == 1)
            {
                string? shared = null;
                foreach (var t in qTokens)
                {
                    if (cTokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                    {
                        shared = t;
                        break;
                    }
                }
                if (shared is null || shared.Length < 4)
                    return 0;
            }

            var qCount = qTokens.Count;
            var queryCoverage = overlap / (double)qCount; // 0..1

            // Primary: overlap count dominates so "more matching tokens" always outranks fewer matches.
            var score = overlap * 30.0;
            if (queryCoverage >= 0.80) score += 20;
            else if (queryCoverage >= 0.60) score += 12;
            else if (queryCoverage >= 0.40) score += 6;

            // Exact-ish collapsed-key containment bonus (handles spacing/case differences).
            var kQ = BuildComparableNameKey(combinedQuery);
            var kC = BuildComparableNameKey(combinedCandidate);
            if (!string.IsNullOrWhiteSpace(kQ) && !string.IsNullOrWhiteSpace(kC) && (kC.Contains(kQ) || kQ.Contains(kC)))
                score += 10;

            return Math.Clamp(score, 0, 140);
        }
        catch
        {
            return 0;
        }
    }

    private static List<string> TokenizeMeaningfulForMatch(string s)
    {
        var tokens = new List<string>(16);
        try
        {
            foreach (var t in TokenizeBasic(CleanSearchQuery(s)))
            {
                var w = (t ?? "").Trim();
                if (string.IsNullOrWhiteSpace(w))
                    continue;
                if (IsJunkToken(w))
                    continue;
                if (w.Length < 2)
                    continue;
                tokens.Add(w);
            }
        }
        catch { /* ignore */ }
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

            (double score, string plainLyrics, double? duration, string? artistName, string? trackName)? best = null;

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

                // Score plain-lyrics candidates; don't just take the first result (it can be wrong for ambiguous queries).
                var combinedCandidate = BuildCombinedCandidateString(lrclibArtist, lrclibName);
                var score = ComputeCombinedMatchScore(query, combinedCandidate);

                if (score >= MinimumMatchScore)
                {
                    if (best is null || score > best.Value.score)
                        best = (score, plainLyrics, lrclibDuration, lrclibArtist, lrclibName);
                }
            }

            if (best is not null)
                return (best.Value.plainLyrics, best.Value.duration, best.Value.artistName, best.Value.trackName, true);
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

        // Normalize stylized Unicode (e.g. mathematical italic/bold letters) to improve tokenization/search.
        // NFKC converts many "fancy" alphabets into regular letters (e.g. 𝘭 → l).
        var result = NormalizeUnicodeStylizedLetters(rawTitle).Trim();

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
        // Avoid splitting trailing single-letter suffixes like "PinocchioP" (would lose the "P").
        // Only split when the uppercase begins a multi-letter segment (Uppercase followed by lowercase).
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=[a-z])(?=[A-Z][a-z])", " ");

        // 4. Normalize whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

        return result;
    }

    private static string NormalizeUnicodeStylizedLetters(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return s ?? "";
        try
        {
            return s.Normalize(NormalizationForm.FormKC);
        }
        catch
        {
            return s;
        }
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

        // Normalize common YouTube channel suffixes for matching ("X - Topic" should still match "X").
        string? lowerChannelNorm = null;
        if (!string.IsNullOrWhiteSpace(ytChannel))
        {
            lowerChannelNorm = NormalizeYoutubeArtistHint(ytChannel).ToLowerInvariant().Trim();
        }

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
        if (!string.IsNullOrWhiteSpace(lowerChannelNorm))
        {
            if (lowerChannelNorm == lowerArtist)
                return 120;

            // Check if LRCLIB artist appears in channel or channel contains LRCLIB artist
            if (lowerChannelNorm.Contains(lowerArtist) || lowerArtist.Contains(lowerChannelNorm))
                return 100;
        }

        // If we have a channel name but it *doesn't* overlap the LRCLIB artist at all, treat title overlap as very weak.
        // This prevents wrong picks like "Frida" → "FRIDA GOLD ..." when the channel points to a different artist.
        var channelHasAnyArtistWord = false;
        if (!string.IsNullOrWhiteSpace(lowerChannelNorm))
        {
            var channelWords = lowerChannelNorm
                .Split(new[] { ' ', '-', '&', '/', '+', '.', ',', ';', ':', '(', ')', '[', ']', '|', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && !stopWords.Contains(w))
                .Distinct()
                .ToHashSet();
            channelHasAnyArtistWord = artistWords.Overlaps(channelWords);
        }

        // Check 2: LRCLIB artist appears in the raw YouTube title (very weak unless multiple words match)
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
                // Single-word overlap is often ambiguous (artist word appears as the *song* title).
                // Only give a meaningful score when multiple words match or coverage is high.
                if (matchingWords >= 2 || (artistWords.Count >= 2 && coverage >= 0.75))
                    return 20 + (int)(25 * coverage); // 20–45 range

                // If the channel also supports the artist, keep a small bump; otherwise treat as noise.
                return channelHasAnyArtistWord ? 10 : 0;
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
                // Same ambiguity rule: if we have channel info that doesn't overlap, don't reward title-based overlap.
                if (!string.IsNullOrWhiteSpace(lowerChannelNorm) && !channelHasAnyArtistWord)
                    return 0;
                return 8 + (int)(18 * coverage); // 8–26 range
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
