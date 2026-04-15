using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

public sealed class FfmpegDecoder : IDisposable
{
    private string _ffmpegPath;
    private Process? _process;
    /// <summary>When decoding <c>yt-dlp -o - | ffmpeg</c>, the yt-dlp process feeding FFmpeg stdin.</summary>
    private Process? _ytdlpPipeProcess;
    private Task? _ytdlpStdoutPumpTask;
    /// <summary>Seek slice folder — deleted on next <see cref="Stop"/>.</summary>
    private string? _ytdlpSeekSliceWorkDir;

    private readonly object _stderrCaptureLock = new();
    private readonly StringBuilder _ffmpegStderrSession = new();
    private const int MaxFfmpegStderrSessionChars = 12000;

    /// <summary>Bounded demux probe for HTTPS googlevideo / manifests (time-to-first-PCM).</summary>
    private const string FastProbeAnalyzeDurationUs = "200000";
    private const string FastProbeProbesizeBytes = "524288";

    /// <summary>Probe for yt-dlp stdout — slightly tighter than HTTPS but not so small that decode starves.</summary>
    private const string PipeProbeAnalyzeDurationUs = "200000";
    private const string PipeProbeProbesizeBytes = "524288";

    public FfmpegDecoder(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public string FfmpegPath => _ffmpegPath;

    public void SetPath(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public Stream StartPcmS16LeStream(
        string inputUrl,
        double? startSeconds = null,
        int sampleRate = 48000,
        int channels = 2,
        IReadOnlyDictionary<string, string>? extraHttpHeaders = null)
    {
        Stop();
        return StartPcmS16LeStreamCore(inputUrl, startSeconds, sampleRate, channels, extraHttpHeaders);
    }

    /// <summary>
    /// Convenience async wrapper — resolves to <see cref="StartPcmS16LeStream"/> synchronously.
    /// </summary>
    public Task<Stream> StartPcmS16LeStreamAsync(
        string inputUrl,
        double? startSeconds = null,
        int sampleRate = 48000,
        int channels = 2,
        IReadOnlyDictionary<string, string>? extraHttpHeaders = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StartPcmS16LeStream(inputUrl, startSeconds, sampleRate, channels, extraHttpHeaders));
    }

    private Stream StartPcmS16LeStreamCore(
        string inputUrl,
        double? startSeconds,
        int sampleRate,
        int channels,
        IReadOnlyDictionary<string, string>? extraHttpHeaders = null)
    {
        ResetStderrCapture();

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var args = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "warning",
        };

        var start = startSeconds is { } s && s > 0 ? s : (double?)null;
        var network = IsNetworkInput(inputUrl);
        var youtubeCdn = network && IsLikelyYoutubeCdnUrl(inputUrl);

        // HTTP(S): reconnect + allow DASH/HLS protocols on fragmented inputs.
        if (network)
        {
            args.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "4" });
            args.AddRange(new[] { "-protocol_whitelist", "file,http,https,tcp,tls,crypto" });
        }

        // googlevideo (and similar) URLs from yt-dlp require browser-like identity + session headers (Cookie) or FFmpeg often gets HTTP 403 / no PCM.
        if (youtubeCdn)
        {
            args.AddRange(new[]
            {
                "-headers",
                BuildYoutubeGooglevideoHeaderBlock(extraHttpHeaders),
            });
        }

        // Local files: -ss before -i is fast and accurate enough for progressive audio files.
        if (start is not null && !network)
        {
            args.Add("-ss");
            args.Add(start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Network seek strategy:
        // - DASH/HLS manifest URL: decode-time seek only (-ss after -i).
        // - YouTube CDN (typical yt-dlp -g URL): hybrid — used only when slice seek is off or as fallback after slice failure.
        //   Primary YouTube seeks use yt-dlp --download-sections (bounded) so FFmpeg never decodes from t=0 on long videos.
        // - Other HTTP(S): hybrid with a 12s fine window.
        const double youtubeSeekFineWindowSeconds = 10.0;
        const double genericSeekFineWindowSeconds = 12.0;
        double? netCoarseBeforeI = null;
        double? netFineAfterI = null;
        if (start is not null && network)
        {
            var t = start.Value;
            if (IsLikelyStreamingManifestInput(inputUrl))
                netFineAfterI = t;
            else if (youtubeCdn)
            {
                var coarse = Math.Max(0.0, t - youtubeSeekFineWindowSeconds);
                var fine = t - coarse;
                if (coarse > 0.001)
                    netCoarseBeforeI = coarse;
                if (fine > 0.001)
                    netFineAfterI = fine;
            }
            else
            {
                var coarse = Math.Max(0.0, t - genericSeekFineWindowSeconds);
                var fine = t - coarse;
                if (coarse > 0.001)
                    netCoarseBeforeI = coarse;
                if (fine > 0.001)
                    netFineAfterI = fine;
            }
        }

        // Bounded demux probe: keeps time-to-first-audio low (especially googlevideo / manifests). Defaults are much larger.
        if (youtubeCdn || (network && IsLikelyStreamingManifestInput(inputUrl)))
            args.AddRange(new[] { "-analyzeduration", FastProbeAnalyzeDurationUs, "-probesize", FastProbeProbesizeBytes });

        if (netCoarseBeforeI is { } cbi)
        {
            args.Add("-ss");
            args.Add(cbi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputUrl);

        if (netFineAfterI is { } fai)
        {
            args.Add("-ss");
            args.Add(fai.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-vn",
            "-ac", channels.ToString(),
            "-ar", sampleRate.ToString(),
            "-f", "f32le",
            "-acodec", "pcm_f32le",
            "pipe:1",
        });

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            AppLog.ToolStderrLine("ffmpeg", e.Data);
            try
            {
                var line = e.Data;
                lock (_stderrCaptureLock)
                {
                    if (_ffmpegStderrSession.Length > 0)
                        _ffmpegStderrSession.AppendLine();
                    _ffmpegStderrSession.Append(line);
                    if (_ffmpegStderrSession.Length > MaxFfmpegStderrSessionChars)
                        _ffmpegStderrSession.Remove(0, _ffmpegStderrSession.Length - MaxFfmpegStderrSessionChars);
                }
            }
            catch
            {
                // ignore
            }
        };

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start ffmpeg process.");

        ChildToolProcessJob.TryAssign(_process);
        _process.BeginErrorReadLine();

        return _process.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Decode YouTube audio by piping <c>yt-dlp -o -</c> into FFmpeg. Uses yt-dlp’s HTTP stack (cookie jar, n/sig) — required when
    /// session cookies are not available to FFmpeg on raw <c>googlevideo</c> URLs.
    /// </summary>
    /// <param name="ytDlpUsesCookiesFromBrowser">
    /// When true, only Innertube clients that declare cookie support are used (see yt-dlp <c>INNERTUBE_CLIENTS</c>); android is omitted.
    /// Order is <c>web_embedded</c> → <c>web_safari</c> → <c>web</c> (embedded often works well with browser cookies). No timed probe between those clients.
    /// </param>
    public async Task<Stream> StartPcmS16LeFromYtdlpStdoutPipeAsync(
        string youtubeWatchUrl,
        double? startSeconds,
        string ytDlpPath,
        Action<ProcessStartInfo> applyYtdlpLaunchPrefix,
        int sampleRate = 48000,
        int channels = 2,
        string ytdlpAudioFormat = "bestaudio/best",
        bool ytDlpUsesCookiesFromBrowser = false,
        CancellationToken cancellationToken = default)
    {
        Stop();

        var ytdlpExe = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        var android = new[] { "--extractor-args", "youtube:player_client=android" };
        var web = new[] { "--extractor-args", "youtube:player_client=web" };
        var webEmbedded = new[] { "--extractor-args", "youtube:player_client=web_embedded" };
        var webSafari = new[] { "--extractor-args", "youtube:player_client=web_safari" };

        // INNERTUBE_CLIENTS: android does not set SUPPORTS_COOKIES — yt-dlp skips it with --cookies-from-browser.
        var attempts = ytDlpUsesCookiesFromBrowser
            ? new[] { webEmbedded, webSafari, web }
            : new[] { android, web };

        const int firstByteProbeMs = 8000;
        Exception? last = null;
        var scratch = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
        for (var i = 0; i < attempts.Length; i++)
        {
            if (i > 0)
                Stop();

            try
            {
                var stream = StartPcmS16LeFromYtdlpStdoutPipeCore(
                    youtubeWatchUrl,
                    startSeconds,
                    ytdlpExe,
                    applyYtdlpLaunchPrefix,
                    attempts[i],
                    sampleRate,
                    channels,
                    ytdlpAudioFormat);

                var isLastClient = i == attempts.Length - 1;
                // Timed probe only for non-cookie android → web: avoid cutting off web_embedded / web_safari with cookies.
                var useTimeoutProbe = !ytDlpUsesCookiesFromBrowser && !isLastClient;
                int n;
                if (useTimeoutProbe)
                {
                    using var probeTimeout = new CancellationTokenSource(firstByteProbeMs);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, probeTimeout.Token);
                    try
                    {
                        n = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { stream.Dispose(); } catch { /* ignore */ }
                            Stop();
                            throw;
                        }

                        try
                        {
                            AppLog.Warn(
                                $"yt-dlp→ffmpeg pipe: no PCM within {firstByteProbeMs} ms (YouTube client attempt {i + 1}/{attempts.Length}); trying next client.");
                        }
                        catch { /* ignore */ }

                        try { stream.Dispose(); } catch { /* ignore */ }
                        TryThrowIfStableYoutubePipeFailureFromSession();
                        Stop();
                        last = new TimeoutException("First PCM byte probe timed out.");
                        continue;
                    }
                }
                else
                {
                    n = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), cancellationToken).ConfigureAwait(false);
                }

                if (n <= 0)
                {
                    try { stream.Dispose(); } catch { /* ignore */ }
                    TryThrowIfStableYoutubePipeFailureFromSession();
                    Stop();
                    last = new InvalidOperationException("yt-dlp stdout pipe reached EOF before PCM.");
                    continue;
                }

                var prefix = new byte[n];
                Array.Copy(scratch, prefix, n);
                return new PcmFfmpegStdoutWithPrefixStream(stream, prefix);
            }
            catch (OperationCanceledException)
            {
                Stop();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Stop();
                TryThrowIfStableYoutubePipeFailureFromSession();
            }
        }

        throw last ?? new InvalidOperationException("yt-dlp stdout pipe decode failed.");
        }
        finally
        {
            try { ArrayPool<byte>.Shared.Return(scratch); } catch { /* ignore */ }
        }
    }

    /// <summary>If captured stderr shows a definitive YouTube refusal (unavailable, premium-only, DRM), throw immediately.</summary>
    private void TryThrowIfStableYoutubePipeFailureFromSession()
    {
        string tail;
        try
        {
            tail = GetRecordedStderrTail(MaxFfmpegStderrSessionChars);
        }
        catch
        {
            return;
        }

        if (!YtDlpClient.IsStableYoutubePipeFailure(tail))
            return;

        var msg = tail.Trim();
        if (msg.Length > 2400)
            msg = msg[^2400..].Trim();
        throw new InvalidOperationException(string.IsNullOrEmpty(msg)
            ? "YouTube reported this video cannot be played."
            : msg);
    }

    private Stream StartPcmS16LeFromYtdlpStdoutPipeCore(
        string youtubeWatchUrl,
        double? startSeconds,
        string ytdlpExe,
        Action<ProcessStartInfo> applyYtdlpLaunchPrefix,
        string[] extractorArgs,
        int sampleRate,
        int channels,
        string ytdlpAudioFormat = "bestaudio/best")
    {
        ResetStderrCapture();

        var ffPsi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        ffPsi.ArgumentList.Add("-hide_banner");
        ffPsi.ArgumentList.Add("-loglevel");
        ffPsi.ArgumentList.Add("warning");
        ffPsi.ArgumentList.Add("-probesize");
        ffPsi.ArgumentList.Add(PipeProbeProbesizeBytes);
        ffPsi.ArgumentList.Add("-analyzeduration");
        ffPsi.ArgumentList.Add(PipeProbeAnalyzeDurationUs);
        if (startSeconds is { } ss && ss > 0.001)
        {
            ffPsi.ArgumentList.Add("-ss");
            ffPsi.ArgumentList.Add(ss.ToString(CultureInfo.InvariantCulture));
        }

        ffPsi.ArgumentList.Add("-i");
        ffPsi.ArgumentList.Add("pipe:0");
        ffPsi.ArgumentList.Add("-vn");
        ffPsi.ArgumentList.Add("-ac");
        ffPsi.ArgumentList.Add(channels.ToString());
        ffPsi.ArgumentList.Add("-ar");
        ffPsi.ArgumentList.Add(sampleRate.ToString());
        ffPsi.ArgumentList.Add("-f");
        ffPsi.ArgumentList.Add("f32le");
        ffPsi.ArgumentList.Add("-acodec");
        ffPsi.ArgumentList.Add("pcm_f32le");
        ffPsi.ArgumentList.Add("pipe:1");

        var ytdlpPsi = new ProcessStartInfo
        {
            FileName = ytdlpExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (TryResolveFfmpegDirForYtdlp(out var ffmpegDir))
        {
            ytdlpPsi.ArgumentList.Add("--ffmpeg-location");
            ytdlpPsi.ArgumentList.Add(ffmpegDir);
        }

        applyYtdlpLaunchPrefix(ytdlpPsi);
        ytdlpPsi.ArgumentList.Add("--no-playlist");
        foreach (var a in extractorArgs)
            ytdlpPsi.ArgumentList.Add(a);
        ytdlpPsi.ArgumentList.Add("--socket-timeout");
        ytdlpPsi.ArgumentList.Add("30");
        ytdlpPsi.ArgumentList.Add("--retries");
        ytdlpPsi.ArgumentList.Add("6");
        ytdlpPsi.ArgumentList.Add("-f");
        ytdlpPsi.ArgumentList.Add(ytdlpAudioFormat);
        ytdlpPsi.ArgumentList.Add("--no-part");
        ytdlpPsi.ArgumentList.Add("-o");
        ytdlpPsi.ArgumentList.Add("-");
        ytdlpPsi.ArgumentList.Add(youtubeWatchUrl);

        var ffmpeg = new Process { StartInfo = ffPsi };
        var ytdlp = new Process { StartInfo = ytdlpPsi };

        ffmpeg.ErrorDataReceived += (_, e) => AppendFfmpegStderrLine(e.Data);
        ytdlp.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            try
            {
                AppLog.ToolStderrLine("yt-dlp", e.Data);
                lock (_stderrCaptureLock)
                {
                    if (_ffmpegStderrSession.Length > 0)
                        _ffmpegStderrSession.AppendLine();
                    _ffmpegStderrSession.Append("[yt-dlp] ").Append(e.Data);
                    TrimStderrSession();
                }
            }
            catch
            {
                // ignore
            }
        };

        if (!ffmpeg.Start())
            throw new InvalidOperationException("Failed to start ffmpeg (pipe decode).");

        ChildToolProcessJob.TryAssign(ffmpeg);
        ffmpeg.BeginErrorReadLine();
        _process = ffmpeg;

        if (!ytdlp.Start())
        {
            try { ffmpeg.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException("Failed to start yt-dlp (stdout pipe).");
        }

        ChildToolProcessJob.TryAssign(ytdlp);
        ytdlp.BeginErrorReadLine();
        _ytdlpPipeProcess = ytdlp;

        var ytdlpOut = ytdlp.StandardOutput.BaseStream;
        var ffmpegIn = ffmpeg.StandardInput.BaseStream;
        _ytdlpStdoutPumpTask = Task.Run(async () =>
        {
            try
            {
                // Do not tie pump I/O to playback CTS — cancel during seek/teardown must come from killing processes
                // (closing streams); ReadAsync(..., ct) was logging spurious "canceled" when Task.Run(..., ct) interacted with teardown.
                await PumpYtdlpStdoutToFfmpegStdinAsync(ytdlpOut, ffmpegIn).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex) when (IsBenignStdoutPumpTeardown(ex))
            {
                // Killing yt-dlp/FFmpeg closes the pipe — expected on stop/seek/track change.
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Warn($"yt-dlp→ffmpeg stdout pump ended: {ex.Message}");
                }
                catch
                {
                    // ignore
                }
            }
        });

        return ffmpeg.StandardOutput.BaseStream;
    }

    private void AppendFfmpegStderrLine(string? line)
    {
        if (line is null)
            return;
        AppLog.ToolStderrLine("ffmpeg", line);
        try
        {
            lock (_stderrCaptureLock)
            {
                if (_ffmpegStderrSession.Length > 0)
                    _ffmpegStderrSession.AppendLine();
                _ffmpegStderrSession.Append(line);
                TrimStderrSession();
            }
        }
        catch
        {
            // ignore
        }
    }

    private void TrimStderrSession()
    {
        if (_ffmpegStderrSession.Length > MaxFfmpegStderrSessionChars)
            _ffmpegStderrSession.Remove(0, _ffmpegStderrSession.Length - MaxFfmpegStderrSessionChars);
    }

    private static bool IsBenignStdoutPumpTeardown(Exception ex)
    {
        if (ex is ObjectDisposedException)
            return true;
        if (ex is IOException io)
        {
            var m = io.Message ?? "";
            return m.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("broken", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("closed", StringComparison.OrdinalIgnoreCase)
                   || m.Contains("not connected", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task PumpYtdlpStdoutToFfmpegStdinAsync(Stream src, Stream dst)
    {
        var buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            while (true)
            {
                var n = await src.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
                if (n <= 0)
                    break;
                await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            }
        }
        finally
        {
            try { dst.Close(); } catch { /* ignore */ }
            try { src.Close(); } catch { /* ignore */ }
            try { ArrayPool<byte>.Shared.Return(buf); } catch { /* ignore */ }
        }
    }

    private bool TryResolveFfmpegDirForYtdlp(out string dirOrExe)
    {
        dirOrExe = "";
        try
        {
            var full = Path.GetFullPath(_ffmpegPath.Trim());
            if (!File.Exists(full))
                return false;
            dirOrExe = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Seek by letting yt-dlp write a time-sliced file (<c>--download-sections</c>), then FFmpeg decodes that file.
    /// Stdout piping is unreliable for DASH; a temp file is deterministic.
    /// </summary>
    /// <param name="totalDurationSeconds">Full video length when known; used to cap section end (see <see cref="BuildYoutubeDownloadSectionArg"/>).</param>
    /// <param name="ytDlpUsesCookiesFromBrowser">When true, yt-dlp skips android with cookies — tries web_embedded, then web_safari, then web.</param>
    public async Task<Stream> StartPcmS16LeFromYtdlpDownloadSectionAsync(
        string youtubeWatchUrl,
        double startSeconds,
        int? totalDurationSeconds,
        string ytDlpPath,
        Action<ProcessStartInfo> applyYtdlpLaunchPrefix,
        CancellationToken cancellationToken,
        int sampleRate = 48000,
        int channels = 2,
        string ytdlpAudioFormat = "bestaudio/best",
        bool ytDlpUsesCookiesFromBrowser = false)
    {
        Stop();

        var ytdlpExe = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        const int sliceTimeoutMinutes = 6;

        var sectionHms = BuildYoutubeDownloadSectionArg(startSeconds, totalDurationSeconds, usePlainSeconds: false);
        var androidExtractor = new[] { "--extractor-args", "youtube:player_client=android" };
        var webExtractor = new[] { "--extractor-args", "youtube:player_client=web" };
        var webEmbeddedExtractor = new[] { "--extractor-args", "youtube:player_client=web_embedded" };
        var webSafariExtractor = new[] { "--extractor-args", "youtube:player_client=web_safari" };
        var attempts = ytDlpUsesCookiesFromBrowser
            ? new[]
            {
                (ytdlpAudioFormat, sectionHms, webEmbeddedExtractor),
                (ytdlpAudioFormat, sectionHms, webSafariExtractor),
                (ytdlpAudioFormat, sectionHms, webExtractor),
            }
            : new[]
            {
                (ytdlpAudioFormat, sectionHms, androidExtractor),
                (ytdlpAudioFormat, sectionHms, webExtractor),
            };

        Exception? lastError = null;
        var firstAttempt = true;
        foreach (var (format, section, extractor) in attempts)
        {
            if (!firstAttempt)
                Stop();
            firstAttempt = false;

            try
            {
                try
                {
                    AppLog.Info($"yt-dlp seek slice try: -f {format} --download-sections {section} extractor={(extractor is null ? "default" : string.Join(" ", extractor))}", AppLogInfoTier.Diagnostic);
                }
                catch
                {
                    // ignore
                }

                var workDir = Path.Combine(Path.GetTempPath(), "LyllyPlayer", "seek", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);
                var outTemplate = Path.Combine(workDir, "slice.%(ext)s");

                var ytdlpPsi = new ProcessStartInfo
                {
                    FileName = ytdlpExe,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var ffmpegFull = Path.GetFullPath(_ffmpegPath);
                if (File.Exists(ffmpegFull))
                {
                    ytdlpPsi.ArgumentList.Add("--ffmpeg-location");
                    ytdlpPsi.ArgumentList.Add(ffmpegFull);
                }

                applyYtdlpLaunchPrefix(ytdlpPsi);

                // Faster section fetch: avoid long per-fragment stalls; keep retries modest so fallback triggers sooner.
                ytdlpPsi.ArgumentList.Add("--socket-timeout");
                ytdlpPsi.ArgumentList.Add("20");
                ytdlpPsi.ArgumentList.Add("--retries");
                ytdlpPsi.ArgumentList.Add("4");
                ytdlpPsi.ArgumentList.Add("--fragment-retries");
                ytdlpPsi.ArgumentList.Add("4");
                ytdlpPsi.ArgumentList.Add("--concurrent-fragments");
                ytdlpPsi.ArgumentList.Add("8");

                ytdlpPsi.ArgumentList.Add("--no-playlist");
                ytdlpPsi.ArgumentList.Add("-f");
                ytdlpPsi.ArgumentList.Add(format);
                if (extractor is not null)
                {
                    foreach (var x in extractor)
                        ytdlpPsi.ArgumentList.Add(x);
                }

                ytdlpPsi.ArgumentList.Add("--download-sections");
                ytdlpPsi.ArgumentList.Add(section);
                ytdlpPsi.ArgumentList.Add("--no-part");
                ytdlpPsi.ArgumentList.Add("-o");
                ytdlpPsi.ArgumentList.Add(outTemplate);
                ytdlpPsi.ArgumentList.Add(youtubeWatchUrl);

                var ytdlpStderr = new StringBuilder();
                using var ytdlp = new Process { StartInfo = ytdlpPsi };
                ytdlp.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        ytdlpStderr.AppendLine(e.Data);
                };

                if (!ytdlp.Start())
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    lastError = new InvalidOperationException("Failed to start yt-dlp process.");
                    continue;
                }

                ChildToolProcessJob.TryAssign(ytdlp);
                ytdlp.BeginErrorReadLine();

                try
                {
                    await WaitForProcessExitOrKillAsync(ytdlp, TimeSpan.FromMinutes(sliceTimeoutMinutes), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    throw;
                }
                catch (TimeoutException)
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    lastError = new TimeoutException(
                        $"yt-dlp seek slice exceeded {sliceTimeoutMinutes} minutes (process killed).");
                    continue;
                }

                var sliceStderr = ytdlpStderr.ToString();
                if (ytdlp.ExitCode != 0)
                {
                    AppLog.ToolStderrCompleted("yt-dlp --download-sections", sliceStderr, ytdlp.ExitCode);
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    lastError = new InvalidOperationException($"yt-dlp seek slice failed (exit {ytdlp.ExitCode}).");
                    continue;
                }

                var sliceFile = PickLargestNonPartFile(workDir);
                if (string.IsNullOrEmpty(sliceFile) || !File.Exists(sliceFile))
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    lastError = new InvalidOperationException("yt-dlp did not produce a seek slice file.");
                    continue;
                }

                var len = new FileInfo(sliceFile).Length;
                if (len < 256)
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    lastError = new InvalidOperationException($"yt-dlp seek slice file is too small ({len} bytes).");
                    continue;
                }

                _ytdlpSeekSliceWorkDir = workDir;
                try
                {
                    return StartPcmS16LeStreamCore(sliceFile, startSeconds: null, sampleRate, channels, null);
                }
                catch (Exception ex)
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                    _ytdlpSeekSliceWorkDir = null;
                    lastError = ex;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("yt-dlp seek slice failed after retries.");
    }

    private static string? PickLargestNonPartFile(string workDir)
    {
        var sliceFile = Directory
            .EnumerateFiles(workDir, "slice.*")
            .Where(static p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static p => new FileInfo(p).Length)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(sliceFile))
        {
            sliceFile = Directory
                .EnumerateFiles(workDir)
                .Where(static p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static p => new FileInfo(p).Length)
                .FirstOrDefault();
        }

        return sliceFile;
    }

    /// <summary>
    /// yt-dlp <c>--download-sections</c> time range (bounded chunk, not to infinity).
    /// </summary>
    private static string BuildYoutubeDownloadSectionArg(double startSeconds, int? totalDurationSeconds, bool usePlainSeconds)
    {
        // Max audio per seek chunk (~45 min). User can seek again to continue past this window.
        const double sliceMaxSeconds = 45 * 60;

        var start = Math.Max(0, startSeconds);
        double end;
        if (totalDurationSeconds is int d && d > 0)
        {
            var cap = start + sliceMaxSeconds;
            end = Math.Min(cap, d);
            if (end <= start)
                end = Math.Min(d, start + 1);
        }
        else
            end = start + sliceMaxSeconds;

        if (usePlainSeconds)
            return $"*{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}";

        return $"*{FormatYoutubeSectionTimestamp(start)}-{FormatYoutubeSectionTimestamp(end)}";
    }

    private static string FormatYoutubeSectionTimestamp(double totalSeconds)
    {
        var x = Math.Max(0, totalSeconds);
        var h = (int)(x / 3600.0);
        var rem = x - h * 3600.0;
        var m = (int)(rem / 60.0);
        var s = rem - m * 60.0;
        if (Math.Abs(s - Math.Floor(s)) < 1e-3)
            return $"{h}:{m:00}:{(int)s:00}";
        return $"{h}:{m:00}:{s.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task WaitForProcessExitOrKillAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sw.Elapsed >= timeout)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                throw new TimeoutException();
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>FFmpeg <c>-headers</c> block for YouTube CDN URLs; merges defaults with yt-dlp <c>http_headers</c> (Cookie, etc.).</summary>
    private static string BuildYoutubeGooglevideoHeaderBlock(IReadOnlyDictionary<string, string>? fromYtdlp)
    {
        const string defaultUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = defaultUa,
            ["Referer"] = "https://www.youtube.com/",
            ["Origin"] = "https://www.youtube.com",
        };

        if (fromYtdlp is not null)
        {
            foreach (var kv in fromYtdlp)
                merged[kv.Key] = kv.Value;
        }

        var sb = new StringBuilder(capacity: 768);
        foreach (var kv in merged)
        {
            if (string.IsNullOrEmpty(kv.Value))
                continue;
            var v = kv.Value.Replace("\r", "").Replace("\n", "");
            sb.Append(kv.Key).Append(": ").Append(v).Append("\r\n");
        }

        return sb.ToString();
    }

    private static bool IsNetworkInput(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl))
            return false;
        return inputUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || inputUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Direct media URLs resolved from YouTube (yt-dlp -g) hit googlevideo and related hosts.</summary>
    private static bool IsLikelyYoutubeCdnUrl(string inputUrl)
    {
        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.IdnHost ?? uri.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        return host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase)
               || host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
               || host.Contains("ggpht", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyStreamingManifestInput(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl))
            return false;
        return inputUrl.Contains("/manifest/", StringComparison.OrdinalIgnoreCase)
               || inputUrl.Contains("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase)
               || inputUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase)
               || inputUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clear captured stderr before starting a new FFmpeg session (call after <see cref="Stop"/>).</summary>
    public void ResetStderrCapture()
    {
        try
        {
            lock (_stderrCaptureLock)
                _ffmpegStderrSession.Clear();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Last lines of stderr for the current/just-ended PCM decode process (for diagnostics).</summary>
    public string GetRecordedStderrTail(int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 256, MaxFfmpegStderrSessionChars);
        try
        {
            lock (_stderrCaptureLock)
            {
                if (_ffmpegStderrSession.Length == 0)
                    return "";
                var s = _ffmpegStderrSession.ToString();
                if (s.Length <= maxChars)
                    return s.Trim();
                return s.AsSpan(s.Length - maxChars).ToString().Trim();
            }
        }
        catch
        {
            return "";
        }
    }

    public void Stop()
    {
        // Kill yt-dlp first so it stops writing to its stdout pipe.
        try
        {
            if (_ytdlpPipeProcess is not null && !_ytdlpPipeProcess.HasExited)
                _ytdlpPipeProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        try { _ytdlpPipeProcess?.Dispose(); } catch { /* ignore */ }
        _ytdlpPipeProcess = null;

        // Kill FFmpeg BEFORE waiting for the pump.
        // The pump may be blocked in WriteAsync(FFmpegStdin) — it cannot unblock until FFmpeg's stdin
        // pipe is closed.  Killing FFmpeg first collapses the pipe, unblocks WriteAsync, and lets the
        // pump task exit within a few milliseconds.  The old order (kill yt-dlp → wait pump → kill
        // FFmpeg) caused a full 3-second blocking wait on every track transition.
        try
        {
            if (_process is not null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;

        if (_ytdlpStdoutPumpTask is { } pump)
        {
            // Both processes are dead; the pump should exit in well under 100 ms.
            // The short wait is purely a safety net so we don't discard a live Task reference.
            try
            {
                pump.Wait(TimeSpan.FromMilliseconds(200));
            }
            catch
            {
                // ignore
            }

            _ytdlpStdoutPumpTask = null;
        }

        if (_ytdlpSeekSliceWorkDir is not null)
        {
            try { Directory.Delete(_ytdlpSeekSliceWorkDir, recursive: true); } catch { /* ignore */ }
            _ytdlpSeekSliceWorkDir = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>Replays bytes already consumed from FFmpeg stdout during first-byte probing, then forwards reads.</summary>
    private sealed class PcmFfmpegStdoutWithPrefixStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _prefixOffset;
        private bool _disposed;

        public PcmFfmpegStdoutWithPrefixStream(Stream inner, byte[] prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var avail = _prefix.Length - _prefixOffset;
                var n = Math.Min(count, avail);
                Array.Copy(_prefix, _prefixOffset, buffer, offset, n);
                _prefixOffset += n;
                return n;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var avail = _prefix.Length - _prefixOffset;
                var n = Math.Min(buffer.Length, avail);
                _prefix.AsMemory(_prefixOffset, n).CopyTo(buffer);
                _prefixOffset += n;
                return n;
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing)
            {
                try { _inner.Dispose(); } catch { /* ignore */ }
            }

            base.Dispose(disposing);
        }
    }
}
