using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>
/// Resolved playback: either a direct media URL for FFmpeg, or (when <see cref="DecodeViaYtdlpStdoutPipe"/>)
/// the watch URL so the player runs <c>yt-dlp -o - | ffmpeg</c> — yt-dlp never puts session <c>Cookie</c> into JSON <c>http_headers</c>.
/// </summary>
public sealed record YoutubeStreamInput(
    string Url,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    bool DecodeViaYtdlpStdoutPipe = false);

public sealed class YtDlpClient
{
    public sealed record PlaylistResolveResult(string? PlaylistTitle, IReadOnlyList<PlaylistEntry> Entries);

    /// <summary>
    /// <c>android</c> first for JSON without cookies: WEB progressive <c>googlevideo</c> URLs often 403 in FFmpeg with headers-only.
    /// <c>tv</c> last: DRM experiment issues (#12563). With cookies-from-browser, use <see cref="YoutubeStrategyClients"/> instead (android is skipped by yt-dlp).
    /// </summary>
    private static readonly string[] YoutubePlayerClientOrder = ["android", "mweb", "web", "tv"];

    /// <summary>Per-<c>player_client</c> yt-dlp retries. Omits android when cookies-from-browser is on to avoid noisy failed runs.</summary>
    private IEnumerable<string> YoutubeStrategyClients()
    {
        if (UsesCookiesFromBrowser)
        {
            yield return "web_embedded";
            yield return "mweb";
            yield return "web_safari";
            yield return "web";
            yield return "tv";
        }
        else
        {
            foreach (var c in YoutubePlayerClientOrder)
                yield return c;
        }
    }

    private string _ytDlpPath;
    /// <summary>Same path the app uses for FFmpeg decode; when it is a real file, passed to yt-dlp as <c>--ffmpeg-location</c>.</summary>
    private string? _ffmpegPath;

    private bool _useEjsFromGithub = true;
    private string? _nodeExeForJsRuntimes;
    private bool _cookiesFromBrowserEnabled;
    private string? _cookiesFromBrowserValue;
    private string _audioQualityFormat = "bestaudio/best";
    /// <summary>Stable label for cache keys / resolve reuse (Auto, High, Medium, Low).</summary>
    private string _audioQualityProfileKey = "Auto";

    public YtDlpClient(string ytDlpPath)
    {
        _ytDlpPath = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
    }

    public string YtDlpPath => _ytDlpPath;

    /// <summary>yt-dlp -f format string derived from the current AudioQuality setting.</summary>
    public string AudioQualityFormat => _audioQualityFormat;

    /// <summary>Matches Options → Audio profile; used so disk cache and in-memory resolve are not reused across quality changes.</summary>
    public string AudioQualityProfileKey => _audioQualityProfileKey;

    public void SetAudioQuality(string quality)
    {
        var q = (quality ?? "Auto").Trim();
        _audioQualityProfileKey = q switch
        {
            "High" => "High",
            "Medium" => "Medium",
            "Low" => "Low",
            _ => "Auto",
        };

        // Chains end with bestaudio/best so JSON strategies that fall through still get audio.
        // Medium/Low include worstaudio so YouTube rows missing abr still step down in bitrate.
        _audioQualityFormat = _audioQualityProfileKey switch
        {
            "High" => "bestaudio[ext=webm]/bestaudio[ext=m4a]/bestaudio/best",
            "Medium" => "bestaudio[abr<=128]/worstaudio/bestaudio/best",
            "Low" => "bestaudio[abr<=64]/worstaudio/bestaudio/best",
            _ => "bestaudio/best", // Auto or unknown
        };
    }

    public void SetPath(string ytDlpPath)
    {
        _ytDlpPath = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
    }

    /// <summary>Keep in sync with <see cref="PlaybackEngine"/> / Options so yt-dlp does not warn that ffmpeg is missing.</summary>
    public void SetFfmpegPath(string? ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath.Trim();
    }

    /// <summary>
    /// YouTube / EJS: remote EJS from GitHub vs bundled, optional <c>--js-runtimes node:…</c>, optional <c>--cookies-from-browser</c>.
    /// </summary>
    public void SetYoutubePlaybackOptions(bool useEjsFromGithub, string? nodeExeFullPath, bool cookiesFromBrowserEnabled, string? cookiesFromBrowserValue)
    {
        _useEjsFromGithub = useEjsFromGithub;
        _nodeExeForJsRuntimes = string.IsNullOrWhiteSpace(nodeExeFullPath) ? null : nodeExeFullPath.Trim();
        _cookiesFromBrowserEnabled = cookiesFromBrowserEnabled;
        _cookiesFromBrowserValue = string.IsNullOrWhiteSpace(cookiesFromBrowserValue) ? null : cookiesFromBrowserValue.Trim();
    }

    /// <summary>Matches Options → Advanced when cookies-from-browser is enabled with a non-empty browser specifier.</summary>
    public bool UsesCookiesFromBrowser =>
        _cookiesFromBrowserEnabled && !string.IsNullOrWhiteSpace(_cookiesFromBrowserValue);

    /// <summary>
    /// Adds the same global yt-dlp flags as <see cref="RunAsync"/> (cookies-from-browser, <c>--remote-components ejs:github</c> when enabled, <c>--js-runtimes</c>).
    /// Used by seek-slice and any other secondary yt-dlp process that must match Options → Advanced.
    /// </summary>
    public void ApplyLaunchPrefixTo(ProcessStartInfo psi)
    {
        if (_cookiesFromBrowserEnabled && !string.IsNullOrWhiteSpace(_cookiesFromBrowserValue))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(_cookiesFromBrowserValue!);
        }

        if (_useEjsFromGithub)
        {
            psi.ArgumentList.Add("--remote-components");
            psi.ArgumentList.Add("ejs:github");
        }

        if (!string.IsNullOrWhiteSpace(_nodeExeForJsRuntimes))
        {
            try
            {
                var nodeFull = Path.GetFullPath(_nodeExeForJsRuntimes!);
                if (File.Exists(nodeFull))
                {
                    psi.ArgumentList.Add("--js-runtimes");
                    psi.ArgumentList.Add("node:" + nodeFull);
                }
            }
            catch
            {
                // ignore invalid node path
            }
        }
    }

    public async Task<PlaylistResolveResult> ResolvePlaylistEntriesAsync(string playlistUrlOrId, CancellationToken cancellationToken)
    {
        // Accept raw ID by converting to a canonical URL; yt-dlp accepts both, but this keeps things consistent.
        var url = playlistUrlOrId.Contains("://", StringComparison.OrdinalIgnoreCase)
            ? playlistUrlOrId
            : $"https://www.youtube.com/playlist?list={playlistUrlOrId}";

        // --dump-single-json prints a single JSON object including an "entries" array for playlists.
        var args = new[]
        {
            "--dump-single-json",
            "--flat-playlist",
            url,
        };

        var (exitCode, stdout, stderr) = await RunAsync(args, cancellationToken, longRunningLogHint: "fetching playlist metadata");
        if (exitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed ({exitCode}). {stderr}".Trim());

        using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
        var playlistTitle =
            GetString(doc.RootElement, "title")
            ?? GetString(doc.RootElement, "playlist_title");

        if (!doc.RootElement.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
            return new PlaylistResolveResult(playlistTitle, Array.Empty<PlaylistEntry>());

        var entries = new List<PlaylistEntry>(capacity: entriesEl.GetArrayLength());
        foreach (var e in entriesEl.EnumerateArray())
        {
            var id = GetString(e, "id");
            var title = GetString(e, "title") ?? "(untitled)";
            var channel = GetString(e, "channel") ?? GetString(e, "uploader");
            var duration = GetInt(e, "duration");
            var webpageUrl = GetString(e, "url") ?? GetString(e, "webpage_url");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(webpageUrl))
                continue;

            // In flat-playlist mode, "url" may be a video ID; normalize to a real URL.
            if (!webpageUrl.Contains("://", StringComparison.OrdinalIgnoreCase))
                webpageUrl = $"https://www.youtube.com/watch?v={webpageUrl}";

            var requiresCookies = IsNeedsAuthEntry(e);

            entries.Add(new PlaylistEntry(
                VideoId: id,
                Title: title,
                Channel: channel,
                DurationSeconds: duration,
                WebpageUrl: webpageUrl,
                RequiresCookies: requiresCookies
            ));
        }

        return new PlaylistResolveResult(playlistTitle, entries);
    }

    public Task<PlaylistResolveResult> ResolveYoutubeMusicSearchAsync(string query, int count, int minLengthSeconds, CancellationToken cancellationToken)
    {
        var fetch = Math.Clamp((int)Math.Round(count * 2.0), 20, 200);
        return ResolveYoutubeMusicSearchAsync(query, count, minLengthSeconds, flatFetchCount: fetch, cancellationToken);
    }

    /// <param name="flatFetchCount">Size passed to <c>ytsearch{N}:…</c> (larger pulls a superset for the same query).</param>
    /// <remarks>
    /// Uses <c>--flat-playlist</c>: fast metadata from the search extractor only. Entries are <b>not</b> play-tested
    /// (no per-video format probe); some hits can fail at playback (DRM, geo, format changes, long/remaster uploads, etc.).
    /// </remarks>
    public async Task<PlaylistResolveResult> ResolveYoutubeMusicSearchAsync(string query, int count, int minLengthSeconds, int flatFetchCount, CancellationToken cancellationToken)
    {
        var q = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return new PlaylistResolveResult($"Search: {q}", Array.Empty<PlaylistEntry>());

        if (count <= 0) count = 50;
        if (count > 200) count = 200;
        if (minLengthSeconds < 0) minLengthSeconds = 0;

        flatFetchCount = Math.Clamp(flatFetchCount, 1, 200);
        var url = $"ytsearch{flatFetchCount}:{q}";

        var args = new List<string>
        {
            "--dump-single-json",
            "--flat-playlist",
            "--extractor-args",
            "youtube:player_client=web_music",
        };

        if (minLengthSeconds > 0)
        {
            args.Add("--match-filter");
            args.Add($"duration >= {minLengthSeconds}");
        }

        args.Add(url);

        var (exitCode, stdout, stderr) = await RunAsync(args.ToArray(), cancellationToken, longRunningLogHint: "YouTube Music search");
        if (exitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed ({exitCode}). {stderr}".Trim());

        using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
        var title = $"Search: {q}";

        if (!doc.RootElement.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
            return new PlaylistResolveResult(title, Array.Empty<PlaylistEntry>());

        var entries = new List<PlaylistEntry>(capacity: entriesEl.GetArrayLength());
        foreach (var e in entriesEl.EnumerateArray())
        {
            var id = GetString(e, "id");
            var t = GetString(e, "title") ?? "";
            var channel = GetString(e, "channel") ?? GetString(e, "uploader");
            var duration = GetInt(e, "duration");
            var webpageUrl = GetString(e, "url") ?? GetString(e, "webpage_url");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (id.Length != 11)
                continue;

            if (string.IsNullOrWhiteSpace(t) || string.Equals(t.Trim(), "(untitled)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(webpageUrl))
                webpageUrl = $"https://www.youtube.com/watch?v={id}";
            else if (!webpageUrl.Contains("://", StringComparison.OrdinalIgnoreCase))
                webpageUrl = $"https://www.youtube.com/watch?v={webpageUrl}";

            var requiresCookies = IsNeedsAuthEntry(e);

            entries.Add(new PlaylistEntry(
                VideoId: id,
                Title: t.Trim(),
                Channel: channel,
                DurationSeconds: duration,
                WebpageUrl: webpageUrl,
                RequiresCookies: requiresCookies
            ));
        }

        var trimmed = entries.Take(count).ToList();
        return new PlaylistResolveResult(title, trimmed);
    }

    public async Task<int?> TryGetDurationSecondsAsync(string videoUrl, CancellationToken cancellationToken)
    {
        try
        {
            var url = (videoUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return null;

            async Task<int?> TryOnceAsync(string[] args)
            {
                var (exitCode, stdout, _) = await RunAsync(args, cancellationToken, longRunningLogHint: "fetching video duration", suppressNonZeroExitLog: true);
                if (exitCode != 0)
                    return null;

                var line = stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(line))
                    return null;
                if (string.Equals(line, "NA", StringComparison.OrdinalIgnoreCase))
                    return null;

                // yt-dlp prints duration in seconds for %(duration)s (usually an integer).
                if (double.TryParse(line, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    var rounded = (int)Math.Round(d);
                    return rounded > 0 ? rounded : null;
                }
                return null;
            }

            if (IsLikelyYoutubeUrl(url))
            {
                var d0 = await TryOnceAsync(new[]
                {
                    "--no-playlist",
                    "--extractor-args",
                    "youtube:player_client=web",
                    "--print",
                    "%(duration)s",
                    url
                });
                if (d0 is int ok0 && ok0 > 0)
                    return ok0;
            }

            var d1 = await TryOnceAsync(new[]
            {
                "--no-playlist",
                "--print",
                "%(duration)s",
                url
            });
            if (d1 is int ok1 && ok1 > 0)
                return ok1;

            // Fallback: music web client.
            return await TryOnceAsync(new[]
            {
                "--no-playlist",
                "--print",
                "%(duration)s",
                "--extractor-args",
                "youtube:player_client=web_music",
                url
            });
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> ResolveBestAudioUrlAsync(string videoUrl, CancellationToken cancellationToken, Action<string, string?>? status = null)
    {
        // -g prints the direct media URL.
        // We try a couple of format strategies because some videos expose no "bestaudio".
        var strategies = BuildAudioUrlStrategies(videoUrl, _audioQualityFormat).ToArray();

        Exception? last = null;
        var sawAgeGate = false;
        foreach (var s in strategies)
        {
            try
            {
                var st = s.isWorkaround ? (sawAgeGate ? "AGE" : "FETCHING") : "FETCHING";
                status?.Invoke(st, s.detail);
                var hint = $"resolving stream URL ({s.detail ?? "default"})";
                var (exitCode, stdout, stderr) = await RunAsync(s.args, cancellationToken, longRunningLogHint: hint);
                if (exitCode != 0)
                    throw new InvalidOperationException($"yt-dlp failed ({exitCode}). {stderr}".Trim());

                var url = stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("yt-dlp did not return a media URL.");

                return url;
            }
            catch (Exception ex)
            {
                last = ex;
                if (LooksLikeAgeRestricted(ex.Message ?? ""))
                    sawAgeGate = true;
                if (ShouldTryNextStrategy(ex))
                    continue;
            }
        }

        throw last ?? new InvalidOperationException("yt-dlp failed to return a media URL.");
    }

    /// <summary>
    /// Resolves a stream URL + <see cref="YoutubeStreamInput.HttpHeaders"/> from yt-dlp JSON.
    /// Without cookies, prefers DASH/HLS <c>manifest_url</c> over a direct progressive <c>googlevideo</c> <c>url</c> when both exist (WEB progressive URLs often 403 in FFmpeg with headers-only).
    /// Session cookies are not in JSON headers — when <see cref="UsesCookiesFromBrowser"/> is true the engine usually uses <c>yt-dlp -o - | ffmpeg</c> instead of raw <c>googlevideo</c>.
    /// </summary>
    public async Task<YoutubeStreamInput> ResolveBestYoutubePlaybackAsync(string videoUrl, CancellationToken cancellationToken, Action<string, string?>? status = null)
    {
        var strategies = BuildYoutubePlaybackJsonStrategies(videoUrl, _audioQualityFormat).ToArray();

        Exception? last = null;
        var sawAgeGate = false;
        foreach (var s in strategies)
        {
            try
            {
                var st = s.isWorkaround ? (sawAgeGate ? "AGE" : "FETCHING") : "FETCHING";
                status?.Invoke(st, s.detail);
                var hint = $"resolving playback URL ({s.detail ?? "default"})";
                var (exitCode, stdout, stderr) = await RunAsync(s.args, cancellationToken, longRunningLogHint: hint);
                if (exitCode != 0)
                    throw new InvalidOperationException($"yt-dlp failed ({exitCode}). {stderr}".Trim());

                if (string.IsNullOrWhiteSpace(stdout))
                    throw new InvalidOperationException("yt-dlp returned empty JSON.");

                var resolved = TryExtractYoutubePlaybackFromDumpInternal(stdout);
                if (resolved is null || string.IsNullOrWhiteSpace(resolved.Url))
                    throw new InvalidOperationException("No manifest or media URL in yt-dlp JSON.");

                return resolved;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (LooksLikeAgeRestricted(ex.Message ?? ""))
                    sawAgeGate = true;
                if (ShouldTryNextStrategy(ex))
                    continue;
            }
        }

        throw last ?? new InvalidOperationException("yt-dlp failed to resolve a playback URL.");
    }

    private static Dictionary<string, string>? TryReadHttpHeaders(JsonElement el)
    {
        if (!el.TryGetProperty("http_headers", out var h) || h.ValueKind != JsonValueKind.Object)
            return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in h.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;
            var v = prop.Value.GetString();
            if (string.IsNullOrEmpty(v))
                continue;
            dict[prop.Name] = v;
        }

        return dict.Count > 0 ? dict : null;
    }

    private YoutubeStreamInput? TryExtractYoutubePlaybackFromDumpInternal(string jsonStdout)
    {
        using var doc = JsonDocument.Parse(jsonStdout, SafeJson.CreateDocumentOptions());
        var root = doc.RootElement;

        // Without cookies, WEB progressive googlevideo URLs often 403 in FFmpeg; prefer manifest (DASH/HLS) when both exist.
        // With cookies-from-browser, JSON is rarely used for main YouTube play; direct url is still fine when chosen.
        var preferDirectUrlOverManifest = UsesCookiesFromBrowser;
        // Merged formats (video+audio): skip video-only entries; we need the audio track for PCM.
        if (root.TryGetProperty("requested_formats", out var requested) && requested.ValueKind == JsonValueKind.Array)
        {
            foreach (var fmt in requested.EnumerateArray())
            {
                if (IsVideoOnlyFormat(fmt))
                    continue;
                if (preferDirectUrlOverManifest)
                {
                    if (TryGetHttpUrl(fmt, "url", out var u))
                        return new YoutubeStreamInput(u, TryReadHttpHeaders(fmt));
                    if (TryGetHttpUrl(fmt, "manifest_url", out var m))
                        return new YoutubeStreamInput(m, TryReadHttpHeaders(fmt));
                }
                else
                {
                    if (TryGetHttpUrl(fmt, "manifest_url", out var m2))
                        return new YoutubeStreamInput(m2, TryReadHttpHeaders(fmt));
                    if (TryGetHttpUrl(fmt, "url", out var u2))
                        return new YoutubeStreamInput(u2, TryReadHttpHeaders(fmt));
                }
            }
        }

        if (!IsVideoOnlyFormat(root))
        {
            if (preferDirectUrlOverManifest)
            {
                if (TryGetHttpUrl(root, "url", out var ru))
                    return new YoutubeStreamInput(ru, TryReadHttpHeaders(root));
                if (TryGetHttpUrl(root, "manifest_url", out var rm))
                    return new YoutubeStreamInput(rm, TryReadHttpHeaders(root));
            }
            else
            {
                if (TryGetHttpUrl(root, "manifest_url", out var rm2))
                    return new YoutubeStreamInput(rm2, TryReadHttpHeaders(root));
                if (TryGetHttpUrl(root, "url", out var ru2))
                    return new YoutubeStreamInput(ru2, TryReadHttpHeaders(root));
            }
        }

        // Last resort: scan all audio-capable formats. (Previously we always picked the *highest* abr manifest,
        // which undid Low/Medium — and manifests let FFmpeg pick a high variant.)
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            return SelectYoutubeAudioFromFormatsArray(formats);

        return null;
    }

    private YoutubeStreamInput? SelectYoutubeAudioFromFormatsArray(JsonElement formats)
    {
        var rows = new List<(double sortKbps, bool hasUrl, string url, string? manifest, Dictionary<string, string>? headers)>();
        foreach (var fmt in formats.EnumerateArray())
        {
            var vcodec = GetString(fmt, "vcodec");
            if (!string.IsNullOrEmpty(vcodec) && !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase))
                continue;
            var acodec = GetString(fmt, "acodec");
            if (string.IsNullOrEmpty(acodec) || string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase))
                continue;

            TryGetHttpUrl(fmt, "url", out var url);
            TryGetHttpUrl(fmt, "manifest_url", out var manifest);
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(manifest))
                continue;

            var abr = GetDouble(fmt, "abr");
            var tbr = GetDouble(fmt, "tbr");
            var sort = 0.0;
            if (abr is > 0)
                sort = abr.Value;
            else if (tbr is > 0)
                sort = tbr.Value;

            rows.Add((sort, !string.IsNullOrWhiteSpace(url), url, string.IsNullOrWhiteSpace(manifest) ? null : manifest, TryReadHttpHeaders(fmt)));
        }

        if (rows.Count == 0)
            return null;

        YoutubeStreamInput? PickRow((double sortKbps, bool hasUrl, string url, string? manifest, Dictionary<string, string>? headers) r, bool directFirst)
        {
            if (directFirst && r.hasUrl && !string.IsNullOrWhiteSpace(r.url))
                return new YoutubeStreamInput(r.url.Trim(), r.headers);
            if (!string.IsNullOrWhiteSpace(r.manifest))
                return new YoutubeStreamInput(r.manifest!.Trim(), r.headers);
            if (r.hasUrl && !string.IsNullOrWhiteSpace(r.url))
                return new YoutubeStreamInput(r.url.Trim(), r.headers);
            return null;
        }

        var preferDirect = UsesCookiesFromBrowser;

        if (string.Equals(_audioQualityProfileKey, "Low", StringComparison.OrdinalIgnoreCase))
        {
            var r = rows
                .OrderBy(x => x.sortKbps > 0 ? x.sortKbps : double.PositiveInfinity)
                .ThenBy(x => x.hasUrl ? 0 : 1)
                .First();
            return PickRow(r, preferDirect);
        }

        if (string.Equals(_audioQualityProfileKey, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            var cappedList = rows
                .Where(x => x.sortKbps > 0 && x.sortKbps <= 160)
                .OrderByDescending(x => x.sortKbps)
                .ThenBy(x => x.hasUrl ? 0 : 1)
                .ToList();
            if (cappedList.Count > 0)
                return PickRow(cappedList[0], preferDirect);

            var r = rows
                .OrderBy(x => x.sortKbps > 0 ? x.sortKbps : double.PositiveInfinity)
                .ThenBy(x => x.hasUrl ? 0 : 1)
                .First();
            return PickRow(r, preferDirect);
        }

        // Auto / High: best tagged bitrate; without cookies prefer manifest when both exist (fewer googlevideo 403s).
        {
            var r = rows
                .OrderByDescending(x => x.sortKbps > 0 ? x.sortKbps : double.MinValue)
                .ThenBy(x => string.IsNullOrWhiteSpace(x.url) ? 1 : 0)
                .First();
            return PickRow(r, preferDirect);
        }
    }

    private static bool IsVideoOnlyFormat(JsonElement fmt)
    {
        var acodec = GetString(fmt, "acodec");
        var vcodec = GetString(fmt, "vcodec");
        var hasAudio = !string.IsNullOrEmpty(acodec) && !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);
        var hasVideo = !string.IsNullOrEmpty(vcodec) && !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
        return hasVideo && !hasAudio;
    }

    private static bool TryGetHttpUrl(JsonElement el, string name, out string url)
    {
        url = "";
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        var s = p.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        url = s;
        return true;
    }

    private static double? GetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(p.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ds) => ds,
            _ => null
        };
    }

    private IEnumerable<(string[] args, bool isWorkaround, string? detail)> BuildYoutubePlaybackJsonStrategies(string videoUrl, string qualityFormat)
    {
        // Use plain bestaudio/best — chains like bestaudio/best/ba/best confused yt-dlp and yielded "format not available".
        foreach (var client in YoutubeStrategyClients())
        {
            yield return (new[] { "--no-playlist", "--extractor-args", $"youtube:player_client={client}", "-f", qualityFormat, "--dump-single-json", videoUrl }, isWorkaround: false, $"JSON ({client})");
        }

        yield return (new[] { "--no-playlist", "-f", qualityFormat, "--dump-single-json", videoUrl }, isWorkaround: true, "JSON (bestaudio, default client)");
        yield return (new[] { "--no-playlist", "-f", "best", "--dump-single-json", videoUrl }, isWorkaround: true, "JSON (best, default client)");
        yield return (new[] { "--no-playlist", "--dump-single-json", videoUrl }, isWorkaround: true, "JSON (any format, default client)");
    }

    public async Task<string> DownloadBestAudioToCacheAsync(string videoUrl, string cacheDir, string cacheKey, CancellationToken cancellationToken, Action<string, string?>? status = null, string? maxFilesize = null)
    {
        Directory.CreateDirectory(cacheDir);

        // Keep a stable base name so we can find the file regardless of extension.
        var safeKey = string.Concat(cacheKey.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        var template = Path.Combine(cacheDir, $"vp-cache-{safeKey}.%(ext)s");

        // Download to a local file (no playlist, no partial file). Retry with built-in age gate workarounds.
        var strategies = BuildDownloadStrategies(videoUrl, template, maxFilesize, _audioQualityFormat).ToArray();

        Exception? last = null;
        var sawAgeGate = false;
        foreach (var s in strategies)
        {
            try
            {
                var st = s.isWorkaround ? (sawAgeGate ? "AGE" : "FETCHING") : "FETCHING";
                status?.Invoke(st, s.detail);
                var hint = $"downloading audio to cache ({s.detail ?? "default"})";
                var (exitCode, _, stderr) = await RunAsync(s.args, cancellationToken, longRunningLogHint: hint);
                if (exitCode != 0)
                    throw new InvalidOperationException($"yt-dlp download failed ({exitCode}). {stderr}".Trim());
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                if (LooksLikeAgeRestricted(ex.Message ?? ""))
                    sawAgeGate = true;
                if (ShouldTryNextStrategy(ex))
                    continue;
                break;
            }
        }

        if (last is not null)
            throw last;

        var prefix = $"vp-cache-{safeKey}.";
        var matches = Directory
            .EnumerateFiles(cacheDir)
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        var best = matches.FirstOrDefault()?.FullName;
        if (string.IsNullOrWhiteSpace(best))
            throw new InvalidOperationException("yt-dlp download did not produce a cache file.");

        // Keep only the latest cache file for this key.
        foreach (var extra in matches.Skip(1))
        {
            try { extra.Delete(); } catch { /* ignore */ }
        }

        return best;
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        string? longRunningLogHint = null,
        bool suppressNonZeroExitLog = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (TryResolveFfmpegLocationForYtdlp(out var ffmpegLoc))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegLoc);
        }

        ApplyLaunchPrefixTo(psi);

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!p.Start())
            throw new InvalidOperationException("Failed to start yt-dlp process.");

        ChildToolProcessJob.TryAssign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var heartbeatCts = new CancellationTokenSource();
        Task? heartbeatTask = null;
        if (!string.IsNullOrWhiteSpace(longRunningLogHint))
        {
            heartbeatTask = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(45_000, heartbeatCts.Token).ConfigureAwait(false);
                        try
                        {
                            AppLog.Info($"yt-dlp still running: {longRunningLogHint}", AppLogInfoTier.Diagnostic);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // stopped when process finished or heartbeat cancelled
                }
            }, CancellationToken.None);
        }

        try
        {
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            try { heartbeatCts.Cancel(); } catch { /* ignore */ }
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask.ConfigureAwait(false); } catch { /* ignore */ }
            }
        }

        var errText = stderr.ToString();
        if (!suppressNonZeroExitLog || p.ExitCode == 0)
            AppLog.ToolStderrCompleted("yt-dlp", errText, p.ExitCode);
        return (p.ExitCode, stdout.ToString(), errText);
    }

    /// <summary>yt-dlp accepts a path to the ffmpeg binary or its directory; we pass the full exe when it exists on disk.</summary>
    private bool TryResolveFfmpegLocationForYtdlp(out string location)
    {
        location = "";
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
            return false;
        try
        {
            var full = Path.GetFullPath(_ffmpegPath.Trim());
            if (!File.Exists(full))
                return false;
            location = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyYoutubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAgeRestricted(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
               || text.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("age restricted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("age verification", StringComparison.OrdinalIgnoreCase)
               || text.Contains("This video may be inappropriate for some users", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUnavailable(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
               || text.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)
               || text.Contains("video is unavailable", StringComparison.OrdinalIgnoreCase)
               || text.Contains("private video", StringComparison.OrdinalIgnoreCase)
               || text.Contains("deleted video", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikePremiumRequired(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("Music Premium", StringComparison.OrdinalIgnoreCase)
               || text.Contains("YouTube Premium", StringComparison.OrdinalIgnoreCase)
               || text.Contains("only available to Music Premium", StringComparison.OrdinalIgnoreCase)
               || text.Contains("only available to Premium", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTryNextStrategy(Exception ex)
    {
        if (ex is JsonException)
            return true;
        var msg = ex.Message ?? "";
        if (LooksLikeUnavailable(msg))
            return false;
        if (LooksLikePremiumRequired(msg))
            return false;
        if (LooksLikeYoutubeNonRetryableExtractionFailure(msg))
            return false;
        return msg.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("Only images are available", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("No manifest or media URL", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("yt-dlp returned empty JSON", StringComparison.OrdinalIgnoreCase)
               || LooksLikeAgeRestricted(msg);
    }

    /// <summary>
    /// When stderr already shows one of these, rotating <c>youtube:player_client=</c> in the yt-dlp→FFmpeg pipe will not help.
    /// Used to fail fast instead of trying every Innertube client.
    /// </summary>
    internal static bool IsStableYoutubePipeFailure(string? stderrOrMessage)
    {
        if (string.IsNullOrWhiteSpace(stderrOrMessage))
            return false;
        return LooksLikeUnavailable(stderrOrMessage)
               || LooksLikePremiumRequired(stderrOrMessage)
               || LooksLikeYoutubeNonRetryableExtractionFailure(stderrOrMessage);
    }

    /// <summary>Do not burn through every client strategy — these failures will not be fixed by the next <c>-f</c> attempt.</summary>
    private static bool LooksLikeYoutubeNonRetryableExtractionFailure(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
            return false;
        if (msg.Contains("DRM protected", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("This video is DRM", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("video is DRM", StringComparison.OrdinalIgnoreCase))
            return true;
        // tv client experiment: all formats DRM — further tv retries won't help.
        if (msg.Contains("applies DRM to all videos on the tv client", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private IEnumerable<(string[] args, bool isWorkaround, string? detail)> BuildAudioUrlStrategies(string videoUrl, string qualityFormat)
    {
        if (IsLikelyYoutubeUrl(videoUrl))
        {
            foreach (var client in YoutubeStrategyClients())
            {
                yield return (new[] { "--extractor-args", $"youtube:player_client={client}", "-f", qualityFormat, "-g", videoUrl }, isWorkaround: false, $"audio URL ({client}, bestaudio)");
                yield return (new[] { "--extractor-args", $"youtube:player_client={client}", "-f", "140/ba[ext=m4a]/bestaudio/best", "-g", videoUrl }, isWorkaround: false, $"audio URL ({client}, m4a)");
            }
        }

        // itag 128k m4a (AAC) often behaves better with FFmpeg HTTP seeks than DASH opus/webm.
        yield return (new[] { "-f", "140/ba[ext=m4a]/bestaudio/best", "-g", videoUrl }, isWorkaround: true, "resolving audio URL (140/m4a)");
        // Prefer progressive / single-file HTTPS audio when available. DASH segment URLs often decode silently
        // after FFmpeg -ss (decode-time seek); progressive m4a/webm behaves like a normal file for seeking.
        yield return (new[] { "-f", "bestaudio[protocol!=http_dash_segments][protocol!=m3u8_native]/bestaudio/best", "-g", videoUrl }, isWorkaround: true, "resolving audio URL (non-DASH)");
        yield return (new[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "-g", videoUrl }, isWorkaround: true, "resolving audio URL (m4a)");
        yield return (new[] { "-f", qualityFormat, "-g", videoUrl }, isWorkaround: true, "resolving audio URL");
        yield return (new[] { "-f", "best", "-g", videoUrl }, isWorkaround: true, "resolving audio URL (best)");
        yield return (new[] { "-g", videoUrl }, isWorkaround: true, "resolving audio URL (any)");
    }

    private IEnumerable<(string[] args, bool isWorkaround, string? detail)> BuildDownloadStrategies(string videoUrl, string template, string? maxFilesize = null, string qualityFormat = "bestaudio/best")
    {
        var sizeArg = string.IsNullOrWhiteSpace(maxFilesize) ? Array.Empty<string>() : new[] { "--max-filesize", maxFilesize };

        if (IsLikelyYoutubeUrl(videoUrl))
        {
            foreach (var client in YoutubeStrategyClients())
            {
                yield return (sizeArg.Concat(new[] { "--extractor-args", $"youtube:player_client={client}", "--no-playlist", "--no-part", "-f", qualityFormat, "-o", template, videoUrl }).ToArray(), isWorkaround: false, $"downloading audio ({client})");
            }
        }

        yield return (sizeArg.Concat(new[] { "--no-playlist", "--no-part", "-f", qualityFormat, "-o", template, videoUrl }).ToArray(), isWorkaround: true, "downloading audio");
        yield return (sizeArg.Concat(new[] { "--no-playlist", "--no-part", "-f", "best", "-o", template, videoUrl }).ToArray(), isWorkaround: true, "downloading audio (best)");
        yield return (sizeArg.Concat(new[] { "--no-playlist", "--no-part", "-o", template, videoUrl }).ToArray(), isWorkaround: true, "downloading audio (any)");
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    /// <summary>
    /// Detects whether a flat-playlist entry is known to require authentication.
    /// yt-dlp sets <c>availability</c> to <c>"needs_auth"</c> for login-gated videos, and
    /// <c>is_private</c> to <c>true</c> for private uploads that appear in public playlists.
    /// In practice YouTube often omits these fields for borderline content, so this is a
    /// best-effort hint — it will not catch every case.
    /// </summary>
    private static bool IsNeedsAuthEntry(JsonElement e)
    {
        var availability = GetString(e, "availability");
        if (string.Equals(availability, "needs_auth", StringComparison.OrdinalIgnoreCase))
            return true;

        if (e.TryGetProperty("is_private", out var priv) && priv.ValueKind == JsonValueKind.True)
            return true;

        return false;
    }
}


