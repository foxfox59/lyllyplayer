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
    /// Fetches lyrics from LRCLIB using a combined token-bag query.
    ///
    /// IMPORTANT:
    /// We intentionally do NOT treat "artist" and "title" as distinct concepts for matching.
    /// We score candidates purely by token overlap between:
    /// - the query tokens (derived from trackName) and
    /// - the candidate tokens (LRCLIB artistName + trackName)
    ///
<<<<<<< Updated upstream
    /// If no synced lyrics are found but the song was found, falls back to plain (non-synced) lyrics.
    /// Returns a tuple of (LrcText, LrclibDurationSeconds, Artist, Title, IsPlainLyrics).
=======
    /// Duration proximity is a small secondary nudge.
    ///
    /// Returns a tuple of (LrcText, LrclibDurationSeconds, ArtistName, TrackName, IsPlainLyrics, IsDefinitiveMiss).
    /// Network/timeout/parse failures are surfaced as exceptions and must NOT be treated as misses by callers.
>>>>>>> Stashed changes
    /// </summary>
    /// <param name="trackName">A best-effort combined query string (often "title + artist").</param>
    /// <param name="artist">Unused for matching; kept for API compatibility.</param>
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

<<<<<<< Updated upstream
        var searchQuery = CleanSearchQuery(trackName + " " + artist);
=======
        // Token-bag search: build query from trackName only.
        // (Callers should pass a combined string when they have multiple fields.)
        var searchQuery = BuildLrclibSearchQuery(trackName, artist: null);
>>>>>>> Stashed changes
        if (string.IsNullOrWhiteSpace(searchQuery))
            return (null, null, null, null, false);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

<<<<<<< Updated upstream
        // Try full query first (trackName + artist)
        var candidates = await SearchAndScoreAsync(client, searchQuery, trackName, artist, targetDurationSeconds, ct);
=======
        // Single-query policy: do not issue a second LRCLIB request (reduces load / avoids double misses).
        // However, do one retry on timeout/transient IO to avoid poisoning UX under temporary LRCLIB slowness.
        //
        // NOTE: We also allow a *single* fallback query that uses only the cleaned track name when the richer query
        // produced 0 viable candidates. This improves hit rate when channel/tags are wrong (uploader handles, etc.).
        List<(double score, string lrc, double? duration, string? artistName, string? trackName)> candidates;
        try
        {
            candidates = await SearchAndScoreAsync(client, searchQuery, targetDurationSeconds, ct);
        }
        catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
        {
            AppLog.Warn($"LRCLIB request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
            candidates = await SearchAndScoreAsync(client, searchQuery, targetDurationSeconds, ct);
        }
>>>>>>> Stashed changes

        // If no synced candidates, try trackName-only as fallback
        string? fallbackQuery = null;
        if (candidates.Count == 0)
        {
            fallbackQuery = CleanSearchQuery(trackName);
            if (!string.IsNullOrWhiteSpace(fallbackQuery) && fallbackQuery != searchQuery)
            {
                AppLog.Info($"LRCLIB fallback: querying trackName-only: {fallbackQuery}");
                var fallbackCandidates = await SearchAndScoreAsync(client, fallbackQuery, trackName, artist, targetDurationSeconds, ct, fallback: true);
                // Deduplicate by LRCLIB track name (simple dedup: keep higher-scored candidate)
                var existingNames = new HashSet<string>(candidates.Select(c => c.trackName ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var fb in fallbackCandidates)
                {
<<<<<<< Updated upstream
                    if (!existingNames.Contains(fb.trackName ?? ""))
                        candidates.Add(fb);
=======
                    candidates = await SearchAndScoreAsync(client, titleOnlyQuery, targetDurationSeconds, ct, fallback: true);
                }
                catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
                {
                    AppLog.Warn($"LRCLIB title-only request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
                    candidates = await SearchAndScoreAsync(client, titleOnlyQuery, targetDurationSeconds, ct, fallback: true);
>>>>>>> Stashed changes
                }
            }
        }

        if (candidates.Count > 0)
        {
            // Pick the highest-scored synced candidate.
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var best = candidates[0];
            return (best.lrc, best.duration, best.artistName, best.trackName, false);
        }

<<<<<<< Updated upstream
        // No synced lyrics found — check plain lyrics. Try full query first, then fallback.
        var plainFallback = await SearchPlainLyricsAsync(client, searchQuery, trackName, artist, ct);
=======
        // No synced lyrics found — check plain lyrics (same single-query policy).
        (string? LrcText, double? Duration, string? Artist, string? Title, bool IsPlainLyrics)? plainFallback;
        try
        {
            plainFallback = await SearchPlainLyricsAsync(client, searchQuery, ct);
        }
        catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
        {
            AppLog.Warn($"LRCLIB plain-lyrics request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
            plainFallback = await SearchPlainLyricsAsync(client, searchQuery, ct);
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
                    plainFallback = await SearchPlainLyricsAsync(client, titleOnlyQuery, ct);
                }
                catch (Exception ex) when (IsTransientLrclibFailure(ex, ct))
                {
                    AppLog.Warn($"LRCLIB title-only plain-lyrics request transient failure (will retry once): {ex.GetType().Name}: {ex.Message}");
                    plainFallback = await SearchPlainLyricsAsync(client, titleOnlyQuery, ct);
                }
            }
        }
>>>>>>> Stashed changes
        if (plainFallback != null)
            return plainFallback.Value;

        // Fallback to trackName-only for plain lyrics
        if (!string.IsNullOrWhiteSpace(fallbackQuery))
        {
<<<<<<< Updated upstream
            var plainFallback2 = await SearchPlainLyricsAsync(client, fallbackQuery, trackName, artist, ct);
            if (plainFallback2 != null)
                return plainFallback2.Value;
        }

        return (null, null, null, null, false);
=======
            // Pre-drop common "Artist - Track - Label" tails before tokenization, so label words
            // cannot leak into the query even if generic junk-token filters later remove only the
            // suffix word ("Records") and leave the label name behind.
            try
            {
                trackName ??= "";
                var s0 = NormalizeUnicodeStylizedLetters(trackName).Trim();
                s0 = StripBracketedJunk(s0);
                s0 = NormalizeFeatureMarkers(s0);
                s0 = StripFeaturedArtistsTail(s0);
                s0 = NormalizeLooseTitleSeparators(s0);

                // Also handle cases where label tails are appended without clear separators (e.g. combinedName paths).
                // IMPORTANT: only do this when the string cannot be split into parts; otherwise we might accidentally
                // trim away the real track title and leave only the leading artist tokens.
                try
                {
                    var hasKnownSeparator =
                        s0.Contains(" - ", StringComparison.Ordinal) ||
                        s0.Contains(" – ", StringComparison.Ordinal) ||
                        s0.Contains(" — ", StringComparison.Ordinal) ||
                        s0.Contains(" : ", StringComparison.Ordinal) ||
                        s0.Contains(" | ", StringComparison.Ordinal);

                    if (!hasKnownSeparator)
                    {
                        var t0 = TokenizeBasic(s0).ToList();
                        static bool IsLabelKeyword0(string w)
                        {
                            var x = (w ?? "").Trim();
                            if (x.Length == 0) return false;
                            return x.Equals("records", StringComparison.OrdinalIgnoreCase)
                                   || x.Equals("record", StringComparison.OrdinalIgnoreCase)
                                   || x.Equals("recordings", StringComparison.OrdinalIgnoreCase)
                                   || x.Equals("recording", StringComparison.OrdinalIgnoreCase)
                                   || x.Equals("label", StringComparison.OrdinalIgnoreCase);
                        }

                        var k = -1;
                        for (var i = t0.Count - 1; i >= 0; i--)
                        {
                            if (IsLabelKeyword0(t0[i]))
                            {
                                k = i;
                                break;
                            }
                        }

                        if (k >= 0 && k >= Math.Max(0, t0.Count - 6))
                        {
                            // Remove up to 4 tokens before the keyword, but keep at least 2 tokens overall.
                            var start = Math.Max(0, k - 4);
                            var keepMin = 2;
                            if (start < keepMin)
                                start = keepMin;
                            if (start < t0.Count)
                            {
                                t0.RemoveRange(start, t0.Count - start);
                                if (t0.Count >= keepMin)
                                    s0 = string.Join(' ', t0);
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                // Diagnostics: do we still have visible separators to split on?
                try
                {
                    var dashCount = 0;
                    var enDashCount = 0;
                    var emDashCount = 0;
                    foreach (var ch in s0)
                    {
                        if (ch == '-') dashCount++;
                        else if (ch == '–') enDashCount++;
                        else if (ch == '—') emDashCount++;
                    }
                    AppLog.Info($"LRCLIB query build: sepStats len={s0.Length} hasSpacedDash={s0.Contains(" - ", StringComparison.Ordinal)} dashes={dashCount} enDashes={enDashCount} emDashes={emDashCount}");
                }
                catch { /* ignore */ }

                var parts0 = SplitOnSeparatorsUpTo3(s0, new[] { " - ", " – ", " — ", " : ", " | " });
                // Strict rule for LRCLIB queries:
                // - If the title is already a clean two-part "A - B", completely ignore any 3rd part.
                //   (Third parts are overwhelmingly uploader/label tags and pollute the query.)
                var forcedTwoPart = false;
                if (parts0.Count >= 2)
                {
                    trackName = $"{parts0[0]} - {parts0[1]}";
                    forcedTwoPart = true;
                }
                else
                {
                    // If we couldn't split into parts, still carry forward the normalized/trimmed string
                    // (this includes parenthetical cleanup and conservative tail trimming above).
                    trackName = s0;
                }

                // Diagnostic stamp: helps verify which build is running and what split logic did,
                // without logging the full raw title.
                try
                {
                    var hadThird = parts0.Count >= 3;
                    var thirdLen = hadThird ? (parts0[2]?.Length ?? 0) : 0;
                    AppLog.Info($"LRCLIB query build: forcedTwoPart={forcedTwoPart} parts={parts0.Count} hadThird={hadThird} thirdLen={thirdLen}");
                }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }

            var tokens = new List<string>(32);

            // 1) Extract tokens from the title itself (handles "Artist - Title", "A x B - Song", etc.)
            var normalizedArtistHint = string.IsNullOrWhiteSpace(artist) ? null : NormalizeYoutubeArtistHint(artist);
            var titleTokens = ExtractTokensFromTrackTitle(trackName, normalizedArtistHint).ToList();
            tokens.AddRange(titleTokens);
            var titleSignalTokenCount = CountDistinctMeaningfulTokens(titleTokens);

            // 2) Optionally include artist/channel tokens (weak signal for YouTube; avoid Topic/VEVO/etc).
            if (LooksLikeTrustworthyArtistHint(artist))
            {
                // If the title is "A - B - C" and C matches the channel/uploader name, don't add those tokens:
                // they are more likely uploader tags than the actual artist, and they pollute search.
                if (!TitleAppearsToEndWithUploaderTag(trackName, normalizedArtistHint))
                {
                    // Local files and some YouTube sources can have wrong "artist" tags that are actually uploader handles
                    // (e.g. SaturnSpectre). Only use such hints if they also appear in the title.
                    if (normalizedArtistHint is not null &&
                        LooksLikeUploaderOrChannelTag(normalizedArtistHint) &&
                        !TitleContainsComparableKey(trackName, normalizedArtistHint) &&
                        // If the title already has enough good tokens, don't pollute search with a likely-uploader handle.
                        // But if the title is very short, keep the hint even if it looks handle-like
                        // because tags often omit spaces and this improves hit rate.
                        titleSignalTokenCount >= 3)
                    {
                        // skip
                    }
                    else
                    {
                        tokens.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(NormalizeYoutubeArtistHint(artist!))));
                    }
                }
            }

            // 2.5) Drop label/publisher tails even when the keyword itself is later treated as junk.
            // Example tokens: ["Artist","Track","LabelName","Records"] — remove the whole tail, not just "Records".
            try
            {
                static bool IsLabelKeyword(string t)
                {
                    var w = (t ?? "").Trim();
                    if (w.Length == 0) return false;
                    return w.Equals("records", StringComparison.OrdinalIgnoreCase)
                           || w.Equals("record", StringComparison.OrdinalIgnoreCase)
                           || w.Equals("recordings", StringComparison.OrdinalIgnoreCase)
                           || w.Equals("recording", StringComparison.OrdinalIgnoreCase)
                           || w.Equals("label", StringComparison.OrdinalIgnoreCase);
                }

                var idx = -1;
                for (var i = tokens.Count - 1; i >= 0; i--)
                {
                    if (IsLabelKeyword(tokens[i]))
                    {
                        idx = i;
                        break;
                    }
                }

                // Treat as a tail only when the keyword appears near the end (last ~6 tokens).
                if (idx >= 0 && idx >= Math.Max(0, tokens.Count - 6))
                {
                    var start = Math.Max(0, idx - 4); // remove up to 4 tokens before keyword (label name)
                    tokens.RemoveRange(start, tokens.Count - start);
                }
            }
            catch { /* ignore */ }

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

            // Safety: never collapse to a 1-token query if we can derive a clean 2-part title.
            // This prevents over-pruning from producing queries like "Word" when we had "Word Word2 - Word3".
            if (filtered.Count < 2)
            {
                try
                {
                    var s1 = NormalizeUnicodeStylizedLetters(trackName ?? "").Trim();
                    s1 = StripBracketedJunk(s1);
                    s1 = NormalizeFeatureMarkers(s1);
                    s1 = StripFeaturedArtistsTail(s1);
                    s1 = NormalizeLooseTitleSeparators(s1);
                    var parts1 = SplitOnSeparatorsUpTo3(s1, new[] { " - ", " – ", " — ", " : ", " | " });
                    if (parts1.Count >= 2)
                    {
                        var rescue = new List<string>(16);
                        rescue.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(parts1[0])));
                        rescue.AddRange(TokenizeBasic(CleanTrackPrefixNumbers(parts1[1])));
                        var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var filtered2 = new List<string>(rescue.Count);
                        foreach (var rr in rescue)
                        {
                            var w2 = (rr ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(w2))
                                continue;
                            if (IsJunkToken(w2))
                                continue;
                            if (seen2.Add(w2))
                                filtered2.Add(w2);
                        }
                        if (filtered2.Count >= 2)
                            filtered = filtered2;
                    }
                }
                catch { /* ignore */ }
            }

            var built = CleanSearchQuery(string.Join(' ', filtered));
            try { AppLog.Info($"LRCLIB query build: finalTokenCount={filtered.Count} queryLen={built.Length}"); } catch { /* ignore */ }
            return built;
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
                // Common case: "Artist - Track - Label/Publisher". Drop it aggressively.
                // Even if the keyword is later token-filtered (e.g. dropping "records"), keeping the remaining label
                // words pollutes the query.
                static bool LooksLikeLabelTail(string s)
                {
                    try
                    {
                        var l = (s ?? "").Trim().ToLowerInvariant();
                        return l.Contains(" records") || l.Contains(" record") || l.Contains(" recordings") || l.Contains(" recording") ||
                               l.Contains(" label") || l.Contains(" record label");
                    }
                    catch { return false; }
                }

                if (!string.IsNullOrWhiteSpace(third) &&
                    (LooksLikeLabelTail(third) || LooksLikeUploaderOrChannelTag(third) || ThirdPartMatchesArtistHint(third, artistHint)))
                {
                    // keep only first two parts
                }
                else
                {
                    // keep the third as well (some songs legitimately contain multiple separators)
                    right = $"{right} {third}";
                }
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

        // Label / publisher tags are frequently appended as the 3rd chunk: "Artist - Track - Label".
        // These are almost never useful for LRCLIB lookup and actively cause wrong matches.
        try
        {
            if (lower.EndsWith(" records") || lower.EndsWith(" record") || lower.EndsWith(" recordings") || lower.EndsWith(" recording"))
                return true;
            if (lower.EndsWith(" record label") || lower.EndsWith(" label"))
                return true;
        }
        catch { /* ignore */ }

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
            // Also keep disambiguation qualifiers that are often crucial for LRCLIB matching (e.g. "Studio Outtake").
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
            // We keep a small allowlist of single-letter tokens (see IsJunkToken) for these cases.
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
        // Treat as junk even when glued to an artist token (e.g. "AlexCVEVO", "SomeArtistVEVO").
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
>>>>>>> Stashed changes
    }

    /// <summary>Searches LRCLIB and scores synced-lyric candidates against the given query.</summary>
    private static async Task<List<(double score, string lrc, double? duration, string? artistName, string? trackName)>> SearchAndScoreAsync(
        HttpClient client,
        string query,
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

<<<<<<< Updated upstream
                // 1. Artist cross-reference
                var artistScore = ComputeArtistMatchScore(lrclibArtist, ytArtist, ytTrackName);
                score += artistScore;

                // 2. Duration proximity
=======
                // 1) Token overlap match (primary): query tokens vs candidate tokens (LRCLIB artistName + trackName).
                // We do not privilege "artist" vs "title"; it's one combined bag-of-words.
                var combinedCandidate = BuildCombinedCandidateString(lrclibArtist, lrclibName);
                var combinedScore = ComputeCombinedMatchScore(query, combinedCandidate);
                score += combinedScore;

                // 2) Duration proximity (secondary nudge)
>>>>>>> Stashed changes
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

<<<<<<< Updated upstream
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
=======
                // 3) Diagnostic log: focus on overlap scoring.
                if (!fallback)
                {
                    // Emit extra overlap details when the candidate is near/above threshold; helps debug false positives.
                    var qTokens = TokenizeMeaningfulForMatch(query);
                    var cTokens = TokenizeMeaningfulForMatch(combinedCandidate);
                    var overlap = qTokens.Intersect(cTokens, StringComparer.OrdinalIgnoreCase).Count();
                    var coverage = qTokens.Count == 0 ? 0 : overlap / (double)qTokens.Count;
                    var shouldDetail = score >= (MinimumMatchScore - 5);
                    if (shouldDetail)
                    {
                        AppLog.Info(
                            $"LRCLIB scoring: track={lrclibName ?? "(none)"} artist={lrclibArtist ?? "(none)"} " +
                            $"combinedScore={combinedScore:F0} total={score:F0} " +
                            $"qTokens={qTokens.Count} cTokens={cTokens.Count} overlap={overlap} qCoverage={coverage:F2} " +
                            $"q=\"{query}\" cand=\"{combinedCandidate}\"");
                    }
                    else
                    {
                        AppLog.Info($"LRCLIB scoring: track={lrclibName ?? "(none)"} artist={lrclibArtist ?? "(none)"} combinedScore={combinedScore:F0} total={score:F0} query=\"{query}\"");
                    }
>>>>>>> Stashed changes
                }

                if (score >= MinimumMatchScore)
                    candidates.Add((score, lrc, lrclibDuration, lrclibArtist, lrclibName));
            }
        }
        catch
        {
            // Best-effort — LRCLIB may be down
        }

        return candidates;
    }

<<<<<<< Updated upstream
=======
    private static string BuildCombinedCandidateString(string? lrclibArtist, string? lrclibName)
        => $"{lrclibArtist ?? ""} {lrclibName ?? ""}".Trim();

    private static double ComputeCombinedMatchScore(string combinedQuery, string combinedCandidate)
    {
        // Returns roughly 0..140. High score when MORE meaningful query tokens appear in candidate.
        // Ranking goal: items with more matching tokens should always win (before secondary nudges like duration).
        try
        {
            var qTokens = TokenizeMeaningfulForMatch(combinedQuery);
            var cTokens = TokenizeMeaningfulForMatch(combinedCandidate);
            if (qTokens.Count == 0 || cTokens.Count == 0)
                return 0;

            var overlap = qTokens.Intersect(cTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (overlap <= 0)
                return 0;

            // Guardrails:
            // - Avoid false positives driven by a single shared token (e.g. "noc").
            // - If the query has >= 3 meaningful tokens, require >= 2 overlaps.
            // - If overlap is 1, allow it only when the overlapping token is "strong" (length >= 4).
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
            // (Coverage still matters for tie-breakers.)
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

>>>>>>> Stashed changes
    /// <summary>Searches LRCLIB for plain (non-synced) lyrics fallback.</summary>
    private static async Task<(string? LrcText, double? Duration, string? Artist, string? Title, bool IsPlainLyrics)?> SearchPlainLyricsAsync(
        HttpClient client,
        string query,
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

            (double score, string plainLyrics, double? duration, string? artistName, string? trackName)? bestCandidate = null;

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
                    try
                    {
                        var qTokens = TokenizeMeaningfulForMatch(query);
                        var cTokens = TokenizeMeaningfulForMatch(combinedCandidate);
                        var overlap = qTokens.Intersect(cTokens, StringComparer.OrdinalIgnoreCase).Count();
                        var coverage = qTokens.Count == 0 ? 0 : overlap / (double)qTokens.Count;
                        AppLog.Info(
                            $"LRCLIB plain scoring: track={lrclibName ?? "(none)"} artist={lrclibArtist ?? "(none)"} total={score:F0} " +
                            $"qTokens={qTokens.Count} cTokens={cTokens.Count} overlap={overlap} qCoverage={coverage:F2} q=\"{query}\"");
                    }
                    catch { /* ignore */ }

                    // Keep the best match; if scores tie, prefer one that at least has an artist+title.
                    // (We don't use duration proximity here because plain lyrics often lack a reliable duration.)
                    if (bestCandidate is null || score > bestCandidate.Value.score)
                        bestCandidate = (score, plainLyrics, lrclibDuration, lrclibArtist, lrclibName);
                }
            }

            if (bestCandidate is not null)
                return (bestCandidate.Value.plainLyrics, bestCandidate.Value.duration, bestCandidate.Value.artistName, bestCandidate.Value.trackName, true);
        }
        catch
        {
            // Best-effort
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
