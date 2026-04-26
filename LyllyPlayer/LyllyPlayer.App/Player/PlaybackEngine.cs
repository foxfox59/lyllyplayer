using NAudio.Wave;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>Emitted when background prefetch or disk-cache warm detects a definitive YouTube refusal (unavailable, age, premium, DRM).</summary>
public sealed record PlaybackPrefetchTag(string VideoId, string Category, string Message);

public sealed class PlaybackEngine : IDisposable
{
    public delegate PlaylistEntry? NextTrackResolver();
    private NextTrackResolver? _nextTrackResolver;
    private readonly YtDlpClient _ytDlp;
    private readonly FfmpegDecoder _decoder;
    private readonly string _ffmpegPath;
    private AudioOut? _audio;
    private readonly WaveFormat _format;
    private int _audioDeviceNumber = -1; // -1 = WAVE_MAPPER (default)

    private CancellationTokenSource? _playCts;
    private Task? _readerTask;
    /// <summary>Only one <see cref="PlayEntryAsync"/> may run at a time (slow yt-dlp resolve + seek must not overlap).</summary>
    private readonly SemaphoreSlim _playExclusive = new(1, 1);
    /// <summary>
    /// Briefly serializes <see cref="CurrentIndex"/> / <see cref="PlayOrder"/> navigation so concurrent
    /// <see cref="NextAsync"/> / <see cref="PrevAsync"/> cannot interleave: another call could otherwise change
    /// <see cref="CurrentIndex"/> after the first updated it but before <see cref="PlayCurrentAsync"/> read
    /// <see cref="GetCurrent"/>, playing the wrong track or leaving the queue stuck with no audio.
    /// </summary>
    private readonly SemaphoreSlim _queueNavLock = new(1, 1);
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly Stopwatch _positionSw = new();
    private double _startOffsetSeconds;
    private int? _currentDurationSeconds;
    private readonly CacheManager _cache;
    /// <summary>VideoIds known bad from prefetch/cache before we try <see cref="PlayEntryAsync"/> — <see cref="NextAsync"/> / <see cref="PrevAsync"/> skip them.</summary>
    private readonly ConcurrentDictionary<string, byte> _prefetchSkipVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private long _cacheMaxBytes;
    private float _volume = 0.85f;
    private readonly AudioAnalyzer _analyzer = new();

    /// <summary>Same <see cref="PlaylistEntry.VideoId"/> as <see cref="_resolvedInputCache"/> — skips yt-dlp on seek (saves ~4–6s per scrub).</summary>
    private string? _resolvedInputCacheVideoId;
    private string? _resolvedInputCache;
    /// <summary>HTTP headers for <see cref="_resolvedInputCache"/> when it is a direct stream URL (not pipe mode).</summary>
    private IReadOnlyDictionary<string, string>? _resolvedInputCacheHttpHeaders;
    private bool _resolvedInputDecodeViaYtdlpPipe;
    /// <summary><see cref="YtDlpClient.AudioQualityProfileKey"/> at the time <see cref="_resolvedInputCache"/> was stored.</summary>
    private string? _resolvedInputCacheQualityKey;

    /// <summary>
    /// Background-resolved stream for the upcoming YouTube entry when we do not full-file prefetch (long / unknown duration).
    /// Avoids yt-dlp resolve stall at track change without downloading the whole next track.
    /// </summary>
    private string? _prefetchNextStreamUrlVideoId;
    private YoutubeStreamInput? _prefetchedStreamInput;
    private Task? _prefetchNextStreamUrlTask;
    /// <summary>Cancels superseded next-track disk + URL + PCM warmup when the user skips ahead quickly.</summary>
    private CancellationTokenSource? _nextTrackWarmCts;

    /// <summary>Completes when the first PCM chunk is fed for the active play session (same moment the position clock starts).</summary>
    private TaskCompletionSource<bool>? _firstAudioForCurrentPlayTcs;

    /// <summary>Disk cache warm for the <b>current</b> track — deferred until first PCM so rapid Next does not enqueue many yt-dlp downloads.</summary>
    private PlaylistEntry? _pendingCurrentDiskWarmEntry;

    /// <summary>
    /// Full-pipeline PCM prefetch for the upcoming track.
    /// Started after the current track is actually playing (first PCM) so rapid Next does not stack yt-dlp/FFmpeg work.
    /// </summary>
    private PcmPrefetchSession? _pcmPrefetch;

    /// <summary>
    /// The FfmpegDecoder that is currently providing the live continuation of a stolen prefetch stream.
    /// Must be stopped when the session ends (seek, stop, next).
    /// </summary>
    private FfmpegDecoder? _activePrefetchDecoder;

    /// <summary>
    /// Background disk-cache warm is skipped above this duration (streaming only for long content).
    /// Playback always starts via stream URL / pipe — we never block on a full-file download before decode.
    /// </summary>
    private const int YoutubePreferStreamingOverFullDownloadSeconds = 20 * 60;

    /// <summary>
    /// Do not use an existing on-disk cache for YouTube at/above this length — always stream (multi‑GB cache, and
    /// FFmpeg seek on a local progressive file is less relevant than consistent DASH streaming for very long content).
    /// </summary>
    private const int YoutubeNeverUseDiskCachePlaybackSeconds = 12 * 60 * 60;

    /// <summary>
    /// YouTube seeks via <c>yt-dlp --download-sections</c> can take many seconds (large section + disk). Off by default:
    /// seeks use FFmpeg on the stream URL (sub-second to ~2s typical). Enable only if you get silence after seek on a stubborn stream.
    /// </summary>
    private const bool EnableYtdlpDownloadSectionSeek = false;

    public PlaybackEngine(YtDlpClient ytDlp, string ffmpegPath)
    {
        _ytDlp = ytDlp;
        _ffmpegPath = ffmpegPath;
        _decoder = new FfmpegDecoder(ffmpegPath);
        _ytDlp.SetFfmpegPath(ffmpegPath);
        _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        _cacheMaxBytes = 512L * 1024 * 1024;
        var cacheDir = Path.Combine(Path.GetTempPath(), "LyllyPlayer", "cache");
        _cache = new CacheManager(cacheDir, _cacheMaxBytes);
    }

    public IReadOnlyList<PlaylistEntry> PlayOrder { get; private set; } = Array.Empty<PlaylistEntry>();
    public int CurrentIndex { get; private set; } = -1;
    public bool IsPlaying => _audio?.IsPlaying ?? false;
    public bool CanResume => _audio is not null && _playCts is not null;
    public double CurrentPositionSeconds => _startOffsetSeconds + _positionSw.Elapsed.TotalSeconds;
    public int? CurrentDurationSeconds => _currentDurationSeconds;
    public (float vuL, float vuR, float[] bands) GetAudioAnalysisSnapshot() => _analyzer.GetSnapshot();

    public event EventHandler<PlaylistEntry?>? NowPlayingChanged;
    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<(PlaylistEntry entry, string message)>? PlaybackFailed;
    public event EventHandler<(PlaylistEntry entry, string status, string? detail)>? StatusChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<(PlaylistEntry entry, bool endedEarly)>? TrackEnded;
    public event EventHandler<PlaybackPrefetchTag>? PrefetchTagged;

    public void SetFfmpegPath(string ffmpegPath)
    {
        _decoder.SetPath(ffmpegPath);
        _ytDlp.SetFfmpegPath(ffmpegPath);
    }

    public void SetCacheMaxBytes(long maxBytes)
    {
        _cacheMaxBytes = Math.Max(0, maxBytes);
        _cache.SetMaxBytes(_cacheMaxBytes);
    }

    public void SetVolume(double volume01)
    {
        _volume = (float)Math.Clamp(volume01, 0, 1);
        if (_audio is not null)
            _audio.Volume = _volume;
    }

    /// <summary>
    /// Call after Options (or startup) changes YouTube stream quality so we do not reuse yt-dlp URLs, manifests,
    /// prefetches, or disk cache entries from another <see cref="YtDlpClient.AudioQualityProfileKey"/>.
    /// </summary>
    public void NotifyYoutubeAudioQualityChanged()
    {
        ClearResolvedInputCache();
        ClearDeferredWarmState();
        CancelNextTrackWarmBestEffort();
        _prefetchSkipVideoIds.Clear();
    }

    /// <summary>Disk cache index key: one cached file per video <b>and</b> quality profile.</summary>
    private string YoutubeDiskCacheStoreKey(string videoId)
        => $"{videoId}|aq={_ytDlp.AudioQualityProfileKey}";

    /// <summary>Sets WaveOut device (-1 = default). Hot-swaps only when <paramref name="deviceNumber"/> changes while playing.</summary>
    public void SetAudioOutputDevice(int deviceNumber)
    {
        if (deviceNumber == _audioDeviceNumber)
            return;

        _audioDeviceNumber = deviceNumber;

        var current = _audio;
        if (current is null || !current.IsPlaying)
            return;

        try
        {
            var next = new AudioOut(_format, deviceNumber, _analyzer.ProcessPcmF32LeStereo);
            next.Volume = _volume;
            if (!next.TryPlay())
            {
                try { next.Dispose(); } catch { /* ignore */ }
                return;
            }

            _audio = next;
            try { current.Stop(); } catch { /* ignore */ }
            try { current.Dispose(); } catch { /* ignore */ }
        }
        catch { /* device not available — keep current */ }
    }

    public void OverrideCurrentDurationSeconds(int? durationSeconds)
    {
        if (durationSeconds is not int d || d <= 0)
            return;
        _currentDurationSeconds = d;
    }

    public void SetQueue(IReadOnlyList<PlaylistEntry> order, int startIndex = 0, bool raiseNowPlayingChanged = false)
    {
        _queueNavLock.Wait();
        try
        {
            ClearDeferredWarmState();
            CancelNextTrackWarmBestEffort();
            _prefetchSkipVideoIds.Clear();
            PlayOrder = order;
            CurrentIndex = order.Count == 0 ? -1 : Math.Clamp(startIndex, 0, order.Count - 1);
            if (!raiseNowPlayingChanged) {}
        }
        finally
        {
            try { _queueNavLock.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }
    }

    public void SetNextTrackResolver(NextTrackResolver resolver)
    {
        _nextTrackResolver = resolver;
    }

    public async Task PlayTrackAsync(PlaylistEntry entry)
    {
        await PlayEntryAsync(entry, 0, raiseNowPlayingChanged: true);
    }

    public void SetBasePlayOrder(IReadOnlyList<PlaylistEntry> order, int startIndex = 0)
    {
        _queueNavLock.Wait();
        try
        {
            ClearDeferredWarmState();
            CancelNextTrackWarmBestEffort();
            _prefetchSkipVideoIds.Clear();
            PlayOrder = order;
            CurrentIndex = order.Count == 0 ? -1 : Math.Clamp(startIndex, 0, order.Count - 1);
        }
        finally
        {
            try { _queueNavLock.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }
    }

    public PlaylistEntry? GetCurrent()
        => CurrentIndex >= 0 && CurrentIndex < PlayOrder.Count ? PlayOrder[CurrentIndex] : null;

  
    public async Task<bool> PlayCurrentAsync()
    {
        await _queueNavLock.WaitAsync().ConfigureAwait(false);
        PlaylistEntry? current;
        try
        {
            current = GetCurrent();
        }
        finally
        {
            try { _queueNavLock.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }

        if (current is null)
            return false;

        return await PlayEntryAsync(current, startSeconds: 0, raiseNowPlayingChanged: true).ConfigureAwait(false);
    }

    public async Task<bool> SeekAsync(double seconds)
    {
        await _queueNavLock.WaitAsync().ConfigureAwait(false);
        PlaylistEntry? current;
        try
        {
            current = GetCurrent();
        }
        finally
        {
            try { _queueNavLock.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }

        if (current is null)
            return false;

        // If there was already an audio pipeline (e.g. user paused mid-track), remember — do not confuse
        // with cold start (no _audio yet) where PlayEntryAsync must be allowed to stay playing.
        var hadAudioBeforeSeek = _audio is not null;
        var wasPlaying = IsPlaying;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            seconds = 0;
        var target = Math.Max(0, seconds);
        // Without duration, clamp to a sane upper bound so resume/settings can't seek past the end and break decode.
        if (current.DurationSeconds is int dur)
            target = Math.Min(target, Math.Max(0, dur - 1));
        else
            target = Math.Min(target, 48 * 3600.0);

        // Seeking should not be treated as a "track change" (avoid UI auto-centering).
        var ok = await PlayEntryAsync(current, startSeconds: target, raiseNowPlayingChanged: false).ConfigureAwait(false);
        if (!ok)
            return false;

        // If the user was paused *with audio already loaded*, stay paused after seeking.
        // Cold start (hadAudioBeforeSeek false, wasPlaying false) must NOT pause — e.g. startup resume
        // from settings calls SeekAsync with a saved position.
        if (!wasPlaying && hadAudioBeforeSeek)
        {
            try
            {
                _pauseGate.Reset();
                _audio?.Pause();
                _positionSw.Stop();
                PlaybackStateChanged?.Invoke(this, false);
            }
            catch { /* ignore */ }
        }

        return true;
    }

    public async Task NextAsync()
    {
        if (_nextTrackResolver is not null)
        {
            var nextR = _nextTrackResolver();
            if (nextR is not null)
            {
                // Fix: IReadOnlyList doesn't have FindIndex, so convert to List temporarily
                var idx = PlayOrder.ToList().FindIndex(e => string.Equals(e.VideoId, nextR.VideoId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) CurrentIndex = idx;
                
                await PlayEntryAsync(nextR, 0, raiseNowPlayingChanged: true);
                return;
            }
        }

        // Fallback
        var next = GetNextEntry();
        if (next == null) return;
        CurrentIndex++;
        await PlayCurrentAsync();
    }

    public async Task PrevAsync()
    {
        await _queueNavLock.WaitAsync().ConfigureAwait(false);
        PlaylistEntry? toPlay = null;
        try
        {
            if (PlayOrder.Count == 0)
                return;

            var idx = CurrentIndex - 1;
            if (idx < 0)
            {
                CurrentIndex = 0;
                toPlay = PlayOrder[CurrentIndex];
            }
            else
            {
                while (idx >= 0 && _prefetchSkipVideoIds.ContainsKey(PlayOrder[idx].VideoId))
                    idx--;
                var finalIdx = idx < 0 ? 0 : idx;
                CurrentIndex = finalIdx;
                toPlay = PlayOrder[finalIdx];
            }
        }
        finally
        {
            try { _queueNavLock.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }

        if (toPlay is null)
            return;

        await PlayEntryAsync(toPlay, startSeconds: 0, raiseNowPlayingChanged: true).ConfigureAwait(false);
    }

    public void TogglePlayPause()
    {
        if (_audio is null)
            return;

        if (IsPlaying)
        {
            _pauseGate.Reset();
            _audio.Pause();
            try { _positionSw.Stop(); } catch { /* ignore */ }
            PlaybackStateChanged?.Invoke(this, false);
        }
        else
        {
            _pauseGate.Set();
            if (!_audio.TryPlay() && !TryRecoverAudioOutput(leaveStoppedSinkOnTotalFailure: true))
            {
                _pauseGate.Reset();
                try { _positionSw.Stop(); } catch { /* ignore */ }
                PlaybackStateChanged?.Invoke(this, false);
                return;
            }

            try { _positionSw.Start(); } catch { /* ignore */ }
            PlaybackStateChanged?.Invoke(this, true);
        }
    }

    /// <summary>
    /// Disposes the current <see cref="_audio"/> and builds a new <see cref="AudioOut"/>, trying the configured device then the default (-1).
    /// Used when the OS reports no driver (device unplugged, Bluetooth drop, etc.).
    /// </summary>
    /// <param name="leaveStoppedSinkOnTotalFailure">
    /// When no device can play, still attach a default-device <see cref="AudioOut"/> in the stopped state so a running PCM reader never hits a null sink
    /// (discards PCM via buffer overflow). Used from UI resume; pipeline start uses <see langword="false"/> so the session can abort cleanly.
    /// </param>
    private bool TryRecoverAudioOutput(bool leaveStoppedSinkOnTotalFailure)
    {
        var old = _audio;
        _audio = null;
        if (old is not null)
        {
            try { old.Stop(); } catch { /* ignore */ }
            try { old.Dispose(); } catch { /* ignore */ }
        }

        bool TryOpen(int deviceNumber)
        {
            try
            {
                var a = new AudioOut(_format, deviceNumber, _analyzer.ProcessPcmF32LeStereo);
                a.Volume = _volume;
                if (!a.TryPlay())
                {
                    try { a.Dispose(); } catch { /* ignore */ }
                    return false;
                }

                _audio = a;
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (TryOpen(_audioDeviceNumber))
            return true;

        if (_audioDeviceNumber != -1 && TryOpen(-1))
        {
            _audioDeviceNumber = -1;
            Error?.Invoke(this,
                "The selected audio output is no longer available. Switched to the default device. You can pick another device in Options → Advanced.");
            return true;
        }

        Error?.Invoke(this,
            "No audio output device is available. Connect speakers or headphones and try Play again.");

        if (leaveStoppedSinkOnTotalFailure)
        {
            try
            {
                var a = new AudioOut(_format, -1, _analyzer.ProcessPcmF32LeStereo);
                a.Volume = _volume;
                _audio = a;
            }
            catch
            {
                // No usable default device — reader loop must tolerate null _audio until cancel.
            }
        }

        return false;
    }

    private async Task<Stream> OpenYoutubePcmFallbackAsync(YoutubeStreamInput resolvedInput, CancellationToken ct, PlaybackTimingMark? mark = null)
    {
        if (resolvedInput.DecodeViaYtdlpStdoutPipe)
        {
            mark?.Step("open_pcm_before_ytdlp_stdout_pipe");
            var s = await _decoder.StartPcmS16LeFromYtdlpStdoutPipeAsync(
                resolvedInput.Url,
                _startOffsetSeconds,
                _ytDlp.YtDlpPath,
                psi => _ytDlp.ApplyLaunchPrefixTo(psi),
                ytdlpAudioFormat: _ytDlp.AudioQualityFormat,
                ytDlpUsesCookiesFromBrowser: _ytDlp.UsesCookiesFromBrowser,
                cancellationToken: ct).ConfigureAwait(false);
            mark?.Step("open_pcm_after_ytdlp_stdout_pipe");
            return s;
        }

        // Seekable inputs (local files, direct CDN URLs).
        mark?.Step("open_pcm_before_ffmpeg_direct_stream");
        var stream = _decoder.StartPcmS16LeStream(
            resolvedInput.Url,
            startSeconds: _startOffsetSeconds,
            extraHttpHeaders: resolvedInput.HttpHeaders);
        mark?.Step("open_pcm_after_ffmpeg_direct_stream_ctor");
        return stream;
    }

    private static void LogPlaybackError(PlaylistEntry entry, string message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim();
        var title = string.IsNullOrWhiteSpace(entry.Title) ? "" : $" title=\"{entry.Title}\"";
        try { AppLog.Error($"Playback VideoId={entry.VideoId}{title}. {detail}"); } catch { /* ignore */ }
    }

    private async Task<bool> PlayEntryAsync(PlaylistEntry entry, double startSeconds, bool raiseNowPlayingChanged)
    {
        // Signal the currently running session to abort *before* waiting on the mutex.
        // Without this, a second seek blocks on WaitAsync until the first's slice download (or silence
        // scan) fully completes — that can be 30-60+ seconds.  Cancelling first causes the in-flight
        // OperationCanceledException to propagate, the holder's finally-block to Release() the
        // semaphore, and our WaitAsync to succeed within a few hundred ms.
        // Note: _playCts is written inside the mutex but read/cancelled here without holding it.
        // That is intentional and safe — CancellationTokenSource.Cancel() is thread-safe.
        try { _playCts?.Cancel(); } catch { /* ignore */ }

        await _playExclusive.WaitAsync().ConfigureAwait(false);
        try
        {
            var playbackTiming = new PlaybackTimingMark(entry.VideoId);
            playbackTiming.Step("play_mutex_acquired");

            // Internal restart for track change/seek: don't signal a full "stopped" state to UI.
            StopInternal(signalPlaybackStopped: false);

            // Wait for the previous reader to finish (or observe cancel). If we start a new FFmpeg while the old
            // reader's EOS path still runs, it can call _decoder.Stop() and kill the new process — no audio, further seeks break.
            try
            {
                if (_readerTask is not null)
                {
                    var prev = _readerTask;
                    var done = await Task.WhenAny(prev, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
                    if (done != prev)
                    {
                        try { AppLog.Warn("Previous PCM reader did not exit in time after stop; forcing decoder kill."); } catch { /* ignore */ }
                        try { _decoder.Stop(); } catch { /* ignore */ }
                        await Task.WhenAny(prev, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    }
                    else
                    {
                        try { await prev.ConfigureAwait(false); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }

            playbackTiming.Step("after_previous_reader_barrier");

            // Apply timeline state before NowPlayingChanged so UI/timer reads consistent duration/position (avoids
            // one frame of stale CurrentDurationSeconds vs new track).
            _currentDurationSeconds = entry.DurationSeconds;
            if (double.IsNaN(startSeconds) || double.IsInfinity(startSeconds))
                startSeconds = 0;
            _startOffsetSeconds = Math.Max(0, startSeconds);
            if (_currentDurationSeconds is int dlim && dlim > 0)
                _startOffsetSeconds = Math.Min(_startOffsetSeconds, Math.Max(0, dlim - 1));
            _positionSw.Reset();
            _analyzer.Reset();

            if (raiseNowPlayingChanged)
                NowPlayingChanged?.Invoke(this, entry);

            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;
            var playbackSessionCts = _playCts;
            _firstAudioForCurrentPlayTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                // ── PCM prefetch fast path ──────────────────────────────────────────────────
                // If the prefetch pipeline for this track is already running (or has buffered data),
                // steal its stream directly.  This eliminates yt-dlp startup + FFmpeg probe time.
                // Only applies when starting from the beginning (not a seek).
                Stream? prefetchedPcm = null;
                if (_startOffsetSeconds < 0.1 &&
                    _pcmPrefetch is { } activePrefetch &&
                    string.Equals(activePrefetch.VideoId, entry.VideoId, StringComparison.Ordinal))
                {
                    try
                    {
                        prefetchedPcm = await activePrefetch.StealAsync().ConfigureAwait(false);
                        _activePrefetchDecoder = activePrefetch.OwnedDecoder;
                        _pcmPrefetch = null;
                        try { activePrefetch.Dispose(); } catch { /* ignore */ }
                        try { AppLog.Info($"YouTube: using prefetched PCM pipeline for {entry.VideoId}", AppLogInfoTier.Crucial); } catch { /* ignore */ }
                    }
                    catch
                    {
                        prefetchedPcm = null;
                    }
                }

                playbackTiming.Step(prefetchedPcm is not null ? "prefetch_pcm_stolen" : "no_prefetch_pcm");

                YoutubeStreamInput? resolvedInput = null;
                Stream pcmStream;

                if (prefetchedPcm is not null)
                {
                    // Prefetch hit: jump straight to audio setup, no URL resolution needed.
                    pcmStream = prefetchedPcm;
                    // Stealing PCM skips ResolveBestInputAsync, which normally kicks background disk cache
                    // for the current YouTube track (cookies/pipe path relies on this for revisits).
                    RequestDiskWarmForCurrentAfterPlaying(entry);
                }
                else
                {
                playbackTiming.Step("before_resolve_best_input");
                resolvedInput = await ResolveBestInputAsync(entry, ct, playbackTiming, publishResolveStatus: raiseNowPlayingChanged).ConfigureAwait(false);
                playbackTiming.Step("after_resolve_best_input");
                if (ShouldUseYtdlpSectionForPlayback(entry, _startOffsetSeconds, resolvedInput!))
                {
                    if (raiseNowPlayingChanged)
                        StatusChanged?.Invoke(this, (entry, "FETCHING", "Preparing seek (downloading a time slice; usually under a minute)…"));
                    try
                    {
                        AppLog.Info($"YouTube seek: yt-dlp time slice from {_startOffsetSeconds:0.##}s (VideoId={entry.VideoId})", AppLogInfoTier.Crucial);
                    }
                    catch
                    {
                        // ignore
                    }

                    // Do not Release() _playExclusive around the slice: a second seek would take the lock while the first
                    // await's finally WaitAsync() waits forever — deadlock and stuck LOADING.
                    try
                    {
                        playbackTiming.Step("before_ytdlp_download_section");
                        pcmStream = await _decoder.StartPcmS16LeFromYtdlpDownloadSectionAsync(
                            entry.WebpageUrl,
                            _startOffsetSeconds,
                            entry.DurationSeconds,
                            _ytDlp.YtDlpPath,
                            psi => _ytDlp.ApplyLaunchPrefixTo(psi),
                            ct,
                            ytdlpAudioFormat: _ytDlp.AudioQualityFormat,
                            ytDlpUsesCookiesFromBrowser: _ytDlp.UsesCookiesFromBrowser).ConfigureAwait(false);
                        playbackTiming.Step("after_ytdlp_download_section");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            AppLog.Warn($"YouTube seek slice failed; falling back to stream decode. {ex.Message}");
                        }
                        catch
                        {
                            // ignore
                        }

                        LogPlaybackError(entry,
                            $"Seek slice failed ({ex.Message}). Using stream decode fallback; try updating yt-dlp.");
                        Error?.Invoke(this,
                            $"Seek slice failed ({ex.Message}). Using stream decode fallback; try updating yt-dlp.");
                        _decoder.Stop();
                        // yt-dlp→ffmpeg pipe cannot apply FFmpeg -ss on pipe:0 for a mid-stream seek; only direct URL / disk slice supports that fallback.
                        if (resolvedInput!.DecodeViaYtdlpStdoutPipe && _startOffsetSeconds > 0.01)
                            throw;
                        playbackTiming.Step("before_open_pcm_after_section_fail");
                        pcmStream = await OpenYoutubePcmFallbackAsync(resolvedInput, ct, playbackTiming).ConfigureAwait(false);
                        playbackTiming.Step("after_open_pcm_after_section_fail");
                    }

                    if (raiseNowPlayingChanged)
                        StatusChanged?.Invoke(this, (entry, "BUFFERING", null));
                }
                else
                {
                    playbackTiming.Step("before_open_pcm_main_path");
                    pcmStream = await OpenYoutubePcmFallbackAsync(resolvedInput!, ct, playbackTiming).ConfigureAwait(false);
                    playbackTiming.Step("after_open_pcm_main_path");
                }
                playbackTiming.Step("after_pcm_stream_ready");
                } // end of prefetch-miss else block

                playbackTiming.Step("before_audio_out");

                if (_audio is null)
                {
                    try
                    {
                        _audio = new AudioOut(_format, _audioDeviceNumber, _analyzer.ProcessPcmF32LeStereo);
                        _audio.Volume = _volume;
                    }
                    catch (Exception ex)
                    {
                        LogPlaybackError(entry, $"Audio output init failed. {ex.Message}");
                        Error?.Invoke(this, $"Audio output init failed. {ex.Message}");
                        PlaybackStateChanged?.Invoke(this, false);
                        AbortPlaybackPipelineAfterFailure();
                        return false;
                    }
                }

                // Stop() clears the buffer; avoids rare WaveOut/BufferedWaveProvider stuck state across pipeline restarts (seek).
                _audio.Stop();
                if (!_audio.TryPlay() && !TryRecoverAudioOutput(leaveStoppedSinkOnTotalFailure: false))
                {
                    LogPlaybackError(entry, "Audio output failed to start (device may be disconnected).");
                    Error?.Invoke(this, "Audio output failed to start. Check that an audio device is connected.");
                    PlaybackStateChanged?.Invoke(this, false);
                    AbortPlaybackPipelineAfterFailure();
                    return false;
                }

                playbackTiming.Step("after_waveout_tryplay");

                PlaybackStateChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, (entry, "BUFFERING", null));
                playbackTiming.Step("after_buffering_status_reader_scheduled");
                _pauseGate.Set();
                // _positionSw is intentionally NOT started here.  Starting it before the first real PCM
                // chunk would make the progress bar advance during the pipeline-initialization gap
                // (FFmpeg probing / yt-dlp prefetch) while the user hears nothing.
                // _positionSw.Start() fires on the first actual audio sample below in the reader task.
                // Next-track disk cache / stream URL / PCM prefetch starts in RunDeferredWarmupsAfterFirstAudio
                // only after the first real PCM chunk (PLAYING), so rapid Next during BUFFERING does not pile work.

                // For yt-dlp pipe mode the C# reader, yt-dlp, and FFmpeg form a three-process chain
                // connected by anonymous pipes.  If the reader pauses calling ReadAsync(FFmpeg pipe:1),
                // FFmpeg fills its OS pipe buffer and blocks.  Blocked FFmpeg then stops reading from
                // pipe:0, which blocks the pump writing to pipe:0, which starves yt-dlp stdout and
                // triggers Windows EINVAL (errno 22) on yt-dlp's stdout write.
                //
                // Fix: move the decode-rate throttle to AFTER ReadAsync, not before it.  The pipe is
                // always being drained (ReadAsync called continuously); we only pause before AddSamples
                // when the audio buffer is nearly full.  The brief pause between ReadAsync and AddSamples
                // is safe: we've already freed space in FFmpeg's pipe:1 by reading; FFmpeg may fill that
                // space during our wait but will stall only briefly before the next ReadAsync.
                //
                // The same post-read approach is used for all inputs (local files, direct URLs) because
                // it gives identical real-time throttling without any deadlock risk.

                _readerTask = Task.Run(async () =>
                {
                    // Slightly larger reads = fewer wakeups feeding WaveOut (helps avoid underruns on bursty network decode).
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        var sawAnyAudio = false;
                        var loggedFirstPcmRead = false;
                        var lastVuLogUtc = DateTime.UtcNow;
                        while (!ct.IsCancellationRequested)
                        {
                            _pauseGate.Wait(ct);

                            var read = await pcmStream.ReadAsync(buffer, 0, buffer.Length, ct);
                            if (!loggedFirstPcmRead)
                            {
                                loggedFirstPcmRead = true;
                                playbackTiming.Step("first_pcm_read_returned");
                            }
                            if (read <= 0)
                                break;

                            // If WaveOut was torn down (device unplug) and no fallback sink yet, block here (do not drop
                            // PCM — that would stall FFmpeg's pipe) until cancel or a new AudioOut exists.
                            while (_audio is null && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(15, ct);
                                _pauseGate.Wait(ct);
                            }

                            var sink = _audio;
                            if (sink is null)
                                continue;

                            // Throttle to real-time AFTER the read so FFmpeg's pipe:1 is always drained.
                            // Must stay below <see cref="AudioOut"/> BufferedWaveProvider capacity (3 s) so AddSamples
                            // never overflows; leave headroom for bursty decode so WaveOut does not underrun.
                            while (sink.BufferedSeconds > 2.0 && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(10, ct);
                                _pauseGate.Wait(ct);
                                sink = _audio;
                                if (sink is null)
                                    break;
                            }

                            if (sink is null)
                                continue;

                            sink.AddSamples(buffer, 0, read);

                            // On the very first PCM chunk: start the position clock so the progress bar
                            // only advances once real audio data is actually flowing.  _audio.Play() and
                            // PlaybackStateChanged were already raised before the reader task (see above).
                            if (!sawAnyAudio)
                            {
                                sawAnyAudio = true;
                                if (ReferenceEquals(_playCts, playbackSessionCts))
                                {
                                    _positionSw.Start();
                                    StatusChanged?.Invoke(this, (entry, "PLAYING", null));
                                    try { _firstAudioForCurrentPlayTcs?.TrySetResult(true); } catch { /* ignore */ }
                                    RunDeferredWarmupsAfterFirstAudio(entry, resolvedInput, raiseNowPlayingChanged);
                                }
                            }

                            // Debug: if VU never moves, something is wrong with analyzer feed.
                            if ((DateTime.UtcNow - lastVuLogUtc).TotalSeconds >= 2)
                            {
                                lastVuLogUtc = DateTime.UtcNow;
                                try
                                {
                                    var (vuL, vuR, _) = _analyzer.GetSnapshot();
                                    AppLog.Info($"Analyzer: vuL={vuL:0.000} vuR={vuR:0.000} buffered={_audio?.BufferedSeconds ?? 0:0.00}s", AppLogInfoTier.Diagnostic);
                                }
                                catch { /* ignore */ }
                            }
                        }

                        if (ct.IsCancellationRequested)
                            return;

                        // Only the active session may tear down shared decoder/CTS. A late EOF from an old reader
                        // must not run after a seek has already started a new FFmpeg process.
                        if (!ReferenceEquals(_playCts, playbackSessionCts))
                            return;

                        // Decode has hit EOF, but BufferedWaveProvider may still hold seconds of PCM. Stopping
                        // WaveOut immediately discards that tail (Repeat: Single then restarts while audio hadn't finished).
                        if (!ct.IsCancellationRequested)
                        {
                            var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
                            while (DateTime.UtcNow < drainDeadline
                                   && !ct.IsCancellationRequested
                                   && ReferenceEquals(_playCts, playbackSessionCts)
                                   && _pauseGate.IsSet)
                            {
                                var sink = _audio;
                                if (sink is null || sink.BufferedSeconds <= 0.05)
                                    break;
                                try
                                {
                                    await Task.Delay(20, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                            }
                        }

                        // After drain we may have awaited while the user hit Next/seek — a new session owns
                        // _playCts. Do not stop the position clock, WaveOut, or raise TrackEnded for this stale EOF
                        // (that caused extra NextAsync calls and a "stuck" / replay-from-start queue).
                        if (!ReferenceEquals(_playCts, playbackSessionCts))
                            return;

                        // End-of-stream: transition to a real stopped state (not "paused/resumable").
                        try { _positionSw.Stop(); } catch { /* ignore */ }

                        string? ffmpegStderrTail = null;
                        if (!sawAnyAudio)
                        {
                            try { ffmpegStderrTail = _decoder.GetRecordedStderrTail(4000); } catch { /* ignore */ }
                        }

                        try { _audio?.Stop(); } catch { /* ignore */ }
                        if (ReferenceEquals(_playCts, playbackSessionCts))
                            _playCts = null;

                        var dur = entry.DurationSeconds;
                        var endedEarly = false;
                        var skipTrackEndedHandledByPlaybackFailed = false;
                        // Immediate EOF / no PCM (common when URL or demuxer fails silently): no duration-based early check runs.
                        if (!sawAnyAudio)
                        {
                            try
                            {
                                var t = string.IsNullOrWhiteSpace(entry.Title) ? "" : $" Title=\"{entry.Title}\"";
                                var tail = (ffmpegStderrTail ?? "").Trim();
                                if (PlaybackFailureKindFromDiagnostics(tail, out var failMsg))
                                {
                                    TryMarkPrefetchSkipFromFailureMessage(entry.VideoId, failMsg);
                                    PlaybackFailed?.Invoke(this, (entry, failMsg));
                                    skipTrackEndedHandledByPlaybackFailed = true;
                                }
                                else
                                {
                                    var google403 = tail.Contains("403", StringComparison.OrdinalIgnoreCase)
                                                    || tail.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
                                    var tailForLog = ScrubPlaybackDiagForLog(tail);
                                    var ff = string.IsNullOrWhiteSpace(tailForLog)
                                        ? ""
                                        : $"FFmpeg: {tailForLog.Replace("\r\n", " | ", StringComparison.Ordinal).Replace("\n", " | ")} ";
                                    var hint = google403
                                        ? "googlevideo returned HTTP 403 — YouTube denied this media URL (signature/n-token drift, missing session cookies, IP/geo, or expired URL). Try latest yt-dlp, Options → Advanced → cookies from browser if you use a signed-in session, and check regional blocks/VPN. "
                                        : string.IsNullOrWhiteSpace(tailForLog)
                                            ? "If yt-dlp stderr was not logged, check Advanced (EJS/Node), cookies, and yt-dlp version. "
                                            : "";
                                    AppLog.Warn(
                                        $"Playback: decoder produced no PCM before EOF. VideoId={entry.VideoId}{t}. " +
                                        hint +
                                        ff +
                                        "Advancing to next track.");
                                }
                            }
                            catch { /* ignore */ }
                            if (!skipTrackEndedHandledByPlaybackFailed)
                                endedEarly = true;
                        }
                        else if (dur is int d && d > 0)
                        {
                            var pos = CurrentPositionSeconds;
                            // YouTube / DASH streams often end a few–many seconds before playlist metadata duration.
                            // Use a generous tail slack so normal completion is not treated as "early" (which skipped Repeat: Single).
                            var slackSeconds = Math.Clamp((int)Math.Round(d * 0.08), 8, 45);
                            if (pos > 0.5 && pos < Math.Max(0, d - slackSeconds))
                            {
                                try { AppLog.Warn($"Playback stopped early at {pos:0.0}s / {d}s (slack {slackSeconds}s). (VideoId={entry.VideoId}). Advancing to next track."); } catch { /* ignore */ }
                                endedEarly = true;
                            }
                        }

                        PlaybackStateChanged?.Invoke(this, false);
                        if (!skipTrackEndedHandledByPlaybackFailed)
                            TrackEnded?.Invoke(this, (entry, endedEarly));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        LogPlaybackError(entry, ex.Message);
                        Error?.Invoke(this, ex.Message);
                    }
                    finally
                    {
                        try { pcmStream.Dispose(); } catch { /* ignore */ }
                        try { _activePrefetchDecoder?.Stop(); } catch { /* ignore */ }
                        _activePrefetchDecoder = null;
                        try { _decoder.Stop(); } catch { /* ignore */ }
                        try { ArrayPool<byte>.Shared.Return(buffer); } catch { /* ignore */ }
                    }
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                PlaybackStateChanged?.Invoke(this, false);
                AbortPlaybackPipelineAfterFailure();
                return false;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                string? stderrTail = null;
                try
                {
                    stderrTail = _decoder.GetRecordedStderrTail(8000);
                }
                catch
                {
                    // ignore
                }

                var diag = string.IsNullOrWhiteSpace(stderrTail) ? msg : $"{msg}\n{stderrTail}".Trim();
                LogPlaybackError(entry, msg);
                Error?.Invoke(this, msg);
                if (PlaybackFailureKindFromDiagnostics(diag, out var failMsg))
                {
                    TryMarkPrefetchSkipFromFailureMessage(entry.VideoId, failMsg);
                    PlaybackFailed?.Invoke(this, (entry, failMsg));
                }
                else if (LooksLikeUnavailable(msg) || LooksLikeAgeRestricted(msg) || LooksLikeYoutubeDrmOrProtected(msg) || LooksLikePremiumRequired(msg))
                {
                    TryMarkPrefetchSkipFromFailureMessage(entry.VideoId, msg);
                    PlaybackFailed?.Invoke(this, (entry, msg));
                }
                AbortPlaybackPipelineAfterFailure();
                return false;
            }
        }
        finally
        {
            try
            {
                _playExclusive.Release();
            }
            catch (SemaphoreFullException)
            {
                // Should never happen; avoids wedging the semaphore if release logic drifts.
            }
        }
    }

    /// <summary>Redacts client IP from googlevideo query strings and truncates huge URLs for safer log pastes.</summary>
    private static string ScrubPlaybackDiagForLog(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return s;
        try
        {
            var t = Regex.Replace(s, @"([?&]ip=)[\d.]+", "$1REDACTED", RegexOptions.IgnoreCase);
            const int max = 2600;
            if (t.Length <= max)
                return t;
            const int head = 1400;
            const int tail = 900;
            return string.Concat(t.AsSpan(0, head), " …(truncated)… ", t.AsSpan(t.Length - tail));
        }
        catch
        {
            return s;
        }
    }

    private static bool LooksLikeUnavailable(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Keep in sync with <see cref="YtDlpClient"/> heuristics; avoid matching "Requested format is not available".
        return message.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)
               || message.Contains("video is unavailable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("private video", StringComparison.OrdinalIgnoreCase)
               || message.Contains("deleted video", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps combined exception + FFmpeg/yt-dlp stderr to <see cref="PlaybackFailed"/> (skip generic early-skip path).</summary>
    private static bool PlaybackFailureKindFromDiagnostics(string text, out string userMessage)
    {
        userMessage = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (LooksLikePremiumRequired(text))
        {
            userMessage = text.Length > 600 ? text[..600].Trim() + "…" : text.Trim();
            return true;
        }

        if (LooksLikeAgeRestricted(text))
        {
            userMessage = text.Length > 600 ? text[..600].Trim() + "…" : text.Trim();
            return true;
        }

        if (LooksLikeYoutubeDrmOrProtected(text))
        {
            userMessage = text.Length > 600 ? text[..600].Trim() + "…" : text.Trim();
            return true;
        }

        if (LooksLikeUnavailable(text))
        {
            userMessage = text.Length > 600 ? text[..600].Trim() + "…" : text.Trim();
            return true;
        }

        return false;
    }

    private static bool LooksLikeCancelledMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("operation canceled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("operation cancelled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("task was cancelled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When yt-dlp/cache prefetch proves a track will not play (unavailable, age, premium, DRM), remember it so
    /// <see cref="NextAsync"/> can skip without another failed decode, and notify UI via <see cref="PrefetchTagged"/>.
    /// </summary>
    private bool TryMarkPrefetchSkipFromFailureMessage(string videoId, string? message)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(message))
            return false;
        if (LooksLikeCancelledMessage(message))
            return false;
        if (message.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
            return false;

        string category;
        if (LooksLikePremiumRequired(message))
            category = "Premium";
        else if (LooksLikeAgeRestricted(message))
            category = "AgeRestricted";
        else if (LooksLikeYoutubeDrmOrProtected(message))
            category = "Drm";
        else if (LooksLikeUnavailable(message))
            category = "Unavailable";
        else
            return false;

        if (!_prefetchSkipVideoIds.TryAdd(videoId, 0))
            return true;

        try
        {
            PrefetchTagged?.Invoke(this, new PlaybackPrefetchTag(videoId, category, message));
        }
        catch
        {
            // ignore
        }

        return true;
    }

    private static bool LooksLikeAgeRestricted(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age restricted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("age verification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePremiumRequired(string? message)
        => YtDlpClient.LooksLikePremiumRequired(message);

    private static bool LooksLikeYoutubeDrmOrProtected(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("DRM protected", StringComparison.OrdinalIgnoreCase)
               || message.Contains("This video is DRM", StringComparison.OrdinalIgnoreCase);
    }

    private PlaylistEntry? GetNextEntry()
    {
        if (PlayOrder.Count == 0)
            return null;
        if (CurrentIndex < 0 || CurrentIndex >= PlayOrder.Count)
            return null;
        var idx = Math.Min(CurrentIndex + 1, PlayOrder.Count - 1);
        if (idx == CurrentIndex)
            return null;
        return PlayOrder[idx];
    }

    /// <summary>First entry after <see cref="CurrentIndex"/> not in <see cref="_prefetchSkipVideoIds"/> (used for prefetch / guards).</summary>
    private PlaylistEntry? GetNextPlayableEntryAfterCurrent()
    {
        if (PlayOrder.Count == 0 || CurrentIndex < 0)
            return null;
        var idx = CurrentIndex + 1;
        while (idx < PlayOrder.Count)
        {
            var e = PlayOrder[idx];
            if (!_prefetchSkipVideoIds.ContainsKey(e.VideoId))
                return e;
            idx++;
        }

        return null;
    }

    /// <summary>After a definitive prefetch/cache skip, warm the next playable YouTube track (if any).</summary>
    private void TryBeginPrefetchForNextPlayableAfterMarkedSkip()
    {
        var anchor = GetCurrent();
        if (anchor is null)
        {
            CancelNextTrackWarmBestEffort();
            return;
        }

        _ = StartNextTrackWarmAfterAnchorFirstAudioAsync(anchor);
    }

    /// <summary>
    /// YouTube + non-zero start + resolved input is an HTTP(S) media URL: use yt-dlp section download instead of FFmpeg <c>-ss</c>
    /// (which often decodes silence on googlevideo/DASH). Local cache files still use FFmpeg <c>-ss</c> on disk.
    /// </summary>
    private static bool ShouldUseYtdlpDownloadSectionSeek(PlaylistEntry entry, double startOff, string? resolvedInput)
    {
        if (!EnableYtdlpDownloadSectionSeek || startOff <= 0.01)
            return false;
        if (!IsYoutubeDiskCacheEligible(entry))
            return false;
        if (TryGetLocalPath(entry.WebpageUrl, out _))
            return false;
        if (entry.VideoId.StartsWith("stream:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(resolvedInput))
            return false;
        return resolvedInput.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || resolvedInput.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bounded yt-dlp <c>--download-sections</c> when the global slice flag is on, or when using stdout-pipe decode with a non-zero
    /// start (FFmpeg <c>-ss</c> before <c>-i pipe:0</c> cannot skip on a live pipe — session/cookie pipe would otherwise fail seeks).
    /// </summary>
    private static bool ShouldUseYtdlpSectionForPlayback(PlaylistEntry entry, double startOff, YoutubeStreamInput resolved)
    {
        if (ShouldUseYtdlpDownloadSectionSeek(entry, startOff, resolved.Url))
            return true;
        return resolved.DecodeViaYtdlpStdoutPipe && startOff > 0.01 && IsYoutubeDiskCacheEligible(entry);
    }

    /// <summary>
    /// When cookies-from-browser is on, yt-dlp does not expose session cookies in JSON for FFmpeg — use watch URL + stdout pipe instead.
    /// Otherwise: yt-dlp JSON first (manifest/direct + public <c>http_headers</c>), then <c>-g</c> fallback.
    /// </summary>
    private Task<YoutubeStreamInput> ResolveYoutubeStreamForPlaybackAsync(PlaylistEntry entry, CancellationToken ct, bool publishStatus = true, PlaybackTimingMark? mark = null)
    {
        // Only force cookie/pipe when the playlist metadata explicitly said it needs auth.
        // For normal public videos, prefer the no-cookie JSON/-g paths so playback doesn't depend on browser cookie state.
        if (entry.RequiresCookies && _ytDlp.UsesCookiesFromBrowser && IsYoutubeDiskCacheEligible(entry))
        {
            mark?.Step("resolve_youtube_cookie_pipe_fastpath");
            if (publishStatus)
            {
                // Use COOKIE status so the UI can surface a helpful "requires cookies" hint.
                var cookieDetail = "Login-gated video — streaming via browser cookies (slow start)…";
                StatusChanged?.Invoke(this, (entry, "COOKIE", cookieDetail));
            }
            return Task.FromResult(new YoutubeStreamInput(entry.WebpageUrl.Trim(), null, DecodeViaYtdlpStdoutPipe: true));
        }

        return ResolveYoutubeStreamInputWithCookieRetryAsync(entry, ct, publishStatus, mark);
    }

    private async Task<YoutubeStreamInput> ResolveYoutubeStreamInputWithCookieRetryAsync(PlaylistEntry entry, CancellationToken ct, bool publishStatus = true, PlaybackTimingMark? mark = null)
    {
        try
        {
            return await ResolveYoutubeStreamInputWithFallbackAsync(entry, ct, publishStatus, mark).ConfigureAwait(false);
        }
        catch (Exception ex) when (_ytDlp.UsesCookiesFromBrowser && IsYoutubeDiskCacheEligible(entry)
                                  && !LooksLikeNonRetryableError(ex.Message))
        {
            // Some videos are public but still fail without cookies (region, age, transient googlevideo 403s).
            // Best-effort: retry via yt-dlp stdout pipe using browser cookies.
            try { AppLog.Warn($"YouTube: no-cookie resolve failed; retrying with cookies. {ex.Message}".Trim()); } catch { /* ignore */ }
            if (publishStatus)
            {
                StatusChanged?.Invoke(this, (entry, "COOKIE", "Retrying with browser cookies…"));
            }
            return new YoutubeStreamInput(entry.WebpageUrl.Trim(), null, DecodeViaYtdlpStdoutPipe: true);
        }
    }

    internal static bool LooksLikeNonRetryableError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return LooksLikeUnavailable(text)
            || LooksLikePremiumRequired(text)
            || LooksLikeAgeRestricted(text);
    }

    /// <summary>Resolve a direct media URL for FFmpeg (no browser cookies in JSON <c>http_headers</c>).</summary>
    private async Task<YoutubeStreamInput> ResolveYoutubeStreamInputWithFallbackAsync(PlaylistEntry entry, CancellationToken ct, bool publishStatus = true, PlaybackTimingMark? mark = null)
    {
        Action<string, string?>? statusCb = publishStatus
            ? (s, d) => StatusChanged?.Invoke(this, (entry, s, d))
            : null;

        try
        {
            mark?.Step("ytdlp_before_resolve_best_playback_json");
            var r = await _ytDlp.ResolveBestYoutubePlaybackAsync(entry.WebpageUrl, ct, statusCb).ConfigureAwait(false);
            mark?.Step("ytdlp_after_resolve_best_playback_json");
            return r;
        }
        catch (Exception ex) when (!LooksLikeNonRetryableError(ex.Message))
        {
            try { AppLog.Warn($"YouTube: JSON playback resolve failed; trying -g. {ex.Message}"); } catch { /* ignore */ }
            mark?.Step("ytdlp_before_resolve_best_audio_url_g_fallback");
            var url = await _ytDlp.ResolveBestAudioUrlAsync(entry.WebpageUrl, ct, statusCb).ConfigureAwait(false);
            mark?.Step("ytdlp_after_resolve_best_audio_url_g_fallback");
            return new YoutubeStreamInput(url, null);
        }
        catch (Exception ex)
        {
            // Non-recoverable error (Premium, age-restricted, unavailable) — re-throw so the
            // exception bubbles up to HandlePlaybackFailed without wasting retries.
            try { AppLog.Warn($"YouTube: non-recoverable resolve error, skipping -g fallback. {ex.Message}"); } catch { /* ignore */ }
            throw;
        }
    }

    private async Task<YoutubeStreamInput> ResolveBestInputAsync(PlaylistEntry entry, CancellationToken ct, PlaybackTimingMark? mark = null, bool publishResolveStatus = true)
    {
        mark?.Step("resolve_enter");
        if (TryReuseCachedResolvedInput(entry, out var reused))
        {
            mark?.Step("resolve_return_reuse_memory");
            return reused;
        }

        if (!string.IsNullOrEmpty(_prefetchNextStreamUrlVideoId) &&
            !string.Equals(_prefetchNextStreamUrlVideoId, entry.VideoId, StringComparison.Ordinal))
            ClearPrefetchNextStreamUrl();

        // Local-file support (M3U / folder): bypass yt-dlp and cache.
        if (TryGetLocalPath(entry.WebpageUrl, out var localPath))
        {
            mark?.Step("resolve_return_local_file");
            return RememberResolvedInput(entry, localPath);
        }

        // Stream URLs from M3U (http/https): feed directly into ffmpeg — never disk cache.
        if (entry.VideoId.StartsWith("stream:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(entry.WebpageUrl, UriKind.Absolute, out var u) &&
            (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
            !IsYoutubeHost(u))
        {
            mark?.Step("resolve_return_stream_url_m3u");
            return RememberResolvedInput(entry, entry.WebpageUrl);
        }

        // Disk cache is only for YouTube web URLs (avoids transient stream URLs ending mid-track).
        if (IsYoutubeDiskCacheEligible(entry))
        {
            var cached = _cache.TryGetCachedPath(YoutubeDiskCacheStoreKey(entry.VideoId));
            if (!string.IsNullOrWhiteSpace(cached))
            {
                // Extremely long videos: never play from disk cache (avoid multi‑hour / multi‑GB files; always DASH stream).
                if (entry.DurationSeconds is int durCached && durCached >= YoutubeNeverUseDiskCachePlaybackSeconds)
                {
                    try
                    {
                        AppLog.Info(
                            $"YouTube: duration {durCached}s >= {YoutubeNeverUseDiskCachePlaybackSeconds}s; ignoring disk cache, streaming ({entry.VideoId}).",
                            AppLogInfoTier.Crucial);
                    }
                    catch
                    {
                        // ignore
                    }

                    if (publishResolveStatus)
                        StatusChanged?.Invoke(this, (entry, "FETCHING", "Starting stream (very long video)…"));
                    mark?.Step("resolve_before_youtube_stream_very_long_video");
                    var veryLong = await ResolveYoutubeStreamForPlaybackAsync(entry, ct, publishStatus: publishResolveStatus, mark: mark).ConfigureAwait(false);
                    mark?.Step("resolve_after_youtube_stream_very_long_video");
                    return RememberResolvedInput(entry, veryLong);
                }

                try { AppLog.Info($"Cache hit: {entry.VideoId} -> {cached}", AppLogInfoTier.Crucial); } catch { /* ignore */ }
                mark?.Step("resolve_return_disk_cache_hit");
                return RememberResolvedInput(entry, cached);
            }

            // Background download not yet complete — use prefetched stream URL if available so the
            // transition is instant rather than blocking on a fresh yt-dlp call or full download.
            // If the prefetch task is still in-flight (yt-dlp hasn't returned yet), wait for it —
            // it started when the current track began playing so it has had the whole track duration
            // to complete; the wait is typically < 1 s for tracks already playing for > a few seconds.
            var prefetchTask = _prefetchNextStreamUrlTask;
            if (prefetchTask is { IsCompleted: false } &&
                string.Equals(_prefetchNextStreamUrlVideoId, entry.VideoId, StringComparison.Ordinal))
            {
                try
                {
                    mark?.Step("resolve_before_await_prefetch_next_url_task");
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(TimeSpan.FromSeconds(12));
                    await prefetchTask.WaitAsync(linked.Token).ConfigureAwait(false);
                    mark?.Step("resolve_after_await_prefetch_next_url_task");
                }
                catch { /* ignore — timeout, cancellation, or yt-dlp failure; fall through */ }
            }

            if (TryConsumePrefetchedStreamUrl(entry, out var prefetchedStream))
            {
                mark?.Step("resolve_return_prefetched_next_track_url");
                // Prefetched URL path skips the cache-miss branch that calls TryKick; still warm disk cache
                // for this track (first EnsureCached may have lost to cookie DB contention with PCM prefetch).
                RequestDiskWarmForCurrentAfterPlaying(entry);
                return RememberResolvedInput(entry, prefetchedStream);
            }

            // Long videos: downloading the entire audio to disk before playback can take a very long time with no playback yet.
            if (entry.DurationSeconds is int ds && ds > YoutubePreferStreamingOverFullDownloadSeconds)
            {
                try
                {
                    AppLog.Info(
                        $"YouTube: duration {ds}s exceeds {YoutubePreferStreamingOverFullDownloadSeconds}s; streaming instead of full cache ({entry.VideoId}).",
                        AppLogInfoTier.Crucial);
                }
                catch
                {
                    // ignore
                }

                if (publishResolveStatus)
                    StatusChanged?.Invoke(this, (entry, "FETCHING", "Starting stream (long video)…"));
                mark?.Step("resolve_before_youtube_stream_long_video");
                var longVid = await ResolveYoutubeStreamForPlaybackAsync(entry, ct, publishStatus: publishResolveStatus, mark: mark).ConfigureAwait(false);
                mark?.Step("resolve_after_youtube_stream_long_video");
                return RememberResolvedInput(entry, longVid);
            }

            // No duration in playlist (common for flat resolves): full-file cache can be enormous — stream instead.
            if (entry.DurationSeconds is null)
            {
                try
                {
                    AppLog.Info($"YouTube: duration unknown in playlist; streaming instead of full-file cache ({entry.VideoId}).", AppLogInfoTier.Crucial);
                }
                catch
                {
                    // ignore
                }

                if (publishResolveStatus)
                    StatusChanged?.Invoke(this, (entry, "FETCHING", "Starting stream…"));
                mark?.Step("resolve_before_youtube_stream_unknown_duration");
                var unknownDurStream = await ResolveYoutubeStreamForPlaybackAsync(entry, ct, publishStatus: publishResolveStatus, mark: mark).ConfigureAwait(false);
                mark?.Step("resolve_after_youtube_stream_unknown_duration");
                RequestDiskWarmForCurrentAfterPlaying(entry);
                return RememberResolvedInput(entry, unknownDurStream);
            }

            // Never block playback on a full yt-dlp download to disk (typical 3–15 min songs were waiting until the
            // entire file existed). Stream immediately; optional background cache for faster revisits / scrub.
            try { AppLog.Info($"Cache miss (YouTube): streaming first ({entry.VideoId})", AppLogInfoTier.Crucial); } catch { /* ignore */ }
            if (publishResolveStatus)
                StatusChanged?.Invoke(this, (entry, "FETCHING", "Starting stream…"));
            try
            {
                mark?.Step("resolve_before_youtube_stream_cache_miss");
                var streamInput = await ResolveYoutubeStreamForPlaybackAsync(entry, ct, publishStatus: publishResolveStatus, mark: mark).ConfigureAwait(false);
                mark?.Step("resolve_after_youtube_stream_cache_miss");
                RequestDiskWarmForCurrentAfterPlaying(entry);
                return RememberResolvedInput(entry, streamInput);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when ((ex.Message ?? "").Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
            {
                try { AppLog.Info($"YouTube: format issue on stream resolve; trying -g URL ({entry.VideoId})", AppLogInfoTier.Crucial); } catch { /* ignore */ }
                mark?.Step("resolve_before_format_fallback_audio_url");
                var fallbackUrl = await _ytDlp.ResolveBestAudioUrlAsync(
                    entry.WebpageUrl,
                    ct,
                    status: publishResolveStatus ? (s, d) => StatusChanged?.Invoke(this, (entry, s, d)) : null).ConfigureAwait(false);
                mark?.Step("resolve_after_format_fallback_audio_url");
                RequestDiskWarmForCurrentAfterPlaying(entry);
                return RememberResolvedInput(entry, new YoutubeStreamInput(fallbackUrl, null));
            }
        }

        // Other remote sources: stream via yt-dlp URL only (no disk cache).
        mark?.Step("resolve_before_remote_resolve_best_audio_url");
        var remoteUrl = await _ytDlp.ResolveBestAudioUrlAsync(
            entry.WebpageUrl,
            ct,
            status: publishResolveStatus ? (s, d) => StatusChanged?.Invoke(this, (entry, s, d)) : null).ConfigureAwait(false);
        mark?.Step("resolve_after_remote_resolve_best_audio_url");
        return RememberResolvedInput(entry, new YoutubeStreamInput(remoteUrl, null));
    }

    private static bool IsYoutubeHost(Uri u)
    {
        try
        {
            var h = (u.Host ?? "").Trim();
            if (string.IsNullOrWhiteSpace(h)) return false;
            return h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                   || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeTransientYoutubeMediaUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        return url.Contains("googlevideo.", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/manifest/", StringComparison.OrdinalIgnoreCase)
               || url.Contains("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryReuseCachedResolvedInput(PlaylistEntry entry, out YoutubeStreamInput input)
    {
        input = new YoutubeStreamInput("", null);
        if (string.IsNullOrWhiteSpace(_resolvedInputCacheVideoId) ||
            !string.Equals(_resolvedInputCacheVideoId, entry.VideoId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(_resolvedInputCache))
            return false;

        if (!string.Equals(_resolvedInputCacheQualityKey, _ytDlp.AudioQualityProfileKey, StringComparison.Ordinal))
            return false;

        var c = _resolvedInputCache.Trim();
        if (c.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // In-memory URL is often a short-lived googlevideo / manifest link. After a full play it may be dead
            // (Repeat Single) while a completed disk cache file exists — prefer the disk path in ResolveBestInputAsync.
            if (!_resolvedInputDecodeViaYtdlpPipe &&
                IsYoutubeDiskCacheEligible(entry) &&
                LooksLikeTransientYoutubeMediaUrl(c) &&
                !string.IsNullOrWhiteSpace(_cache.TryGetCachedPath(YoutubeDiskCacheStoreKey(entry.VideoId))))
            {
                ClearResolvedInputCache();
                return false;
            }

            // Browser-cookie pipe uses the watch URL in memory; once a disk cache file exists, prefer disk so
            // Repeat Single + seek use FFmpeg on a file (no yt-dlp slice / no dead googlevideo URL reuse).
            if (_resolvedInputDecodeViaYtdlpPipe &&
                IsYoutubeDiskCacheEligible(entry) &&
                !string.IsNullOrWhiteSpace(_cache.TryGetCachedPath(YoutubeDiskCacheStoreKey(entry.VideoId))) &&
                !(entry.DurationSeconds is int dPipe && dPipe >= YoutubeNeverUseDiskCachePlaybackSeconds))
            {
                ClearResolvedInputCache();
                return false;
            }

            input = new YoutubeStreamInput(c, _resolvedInputCacheHttpHeaders, _resolvedInputDecodeViaYtdlpPipe);
            return true;
        }

        try
        {
            if (File.Exists(c))
            {
                input = new YoutubeStreamInput(c, null);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        ClearResolvedInputCache();
        return false;
    }

    private YoutubeStreamInput RememberResolvedInput(PlaylistEntry entry, string resolvedPathOrUrl)
        => RememberResolvedInput(entry, new YoutubeStreamInput(resolvedPathOrUrl.Trim(), null));

    private YoutubeStreamInput RememberResolvedInput(PlaylistEntry entry, YoutubeStreamInput input)
    {
        _resolvedInputCacheVideoId = entry.VideoId;
        _resolvedInputCache = input.Url.Trim();
        _resolvedInputCacheHttpHeaders = input.DecodeViaYtdlpStdoutPipe ? null : input.HttpHeaders;
        _resolvedInputDecodeViaYtdlpPipe = input.DecodeViaYtdlpStdoutPipe;
        _resolvedInputCacheQualityKey = _ytDlp.AudioQualityProfileKey;
        return input;
    }

    private void ClearPrefetchNextStreamUrl()
    {
        _prefetchNextStreamUrlVideoId = null;
        _prefetchedStreamInput = null;
        _prefetchNextStreamUrlTask = null;
        try { _pcmPrefetch?.Dispose(); } catch { /* ignore */ }
        _pcmPrefetch = null;
    }

    private void ClearPrefetchIfStillForVideo(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return;
        if (!string.Equals(_prefetchNextStreamUrlVideoId, videoId, StringComparison.OrdinalIgnoreCase))
            return;
        ClearPrefetchNextStreamUrl();
    }

    private void CancelNextTrackWarmBestEffort()
    {
        try { _nextTrackWarmCts?.Cancel(); } catch { /* ignore */ }
        try { _nextTrackWarmCts?.Dispose(); } catch { /* ignore */ }
        _nextTrackWarmCts = null;
        ClearPrefetchNextStreamUrl();
    }

    /// <summary>
    /// Call when the play order changes while a track is playing/buffering, so we don't prefetch the wrong "next" item.
    /// </summary>
    public void NotifyPlayOrderChanged()
    {
        try { CancelNextTrackWarmBestEffort(); } catch { /* ignore */ }
        try
        {
            if (GetCurrent() is { } cur)
                _ = StartNextTrackWarmAfterAnchorFirstAudioAsync(cur);
        }
        catch { /* ignore */ }
    }

    private void ClearDeferredWarmState()
    {
        _pendingCurrentDiskWarmEntry = null;
        try { _firstAudioForCurrentPlayTcs?.TrySetCanceled(); } catch { /* ignore */ }
        _firstAudioForCurrentPlayTcs = null;
    }

    private void RequestDiskWarmForCurrentAfterPlaying(PlaylistEntry entry)
        => _pendingCurrentDiskWarmEntry = entry;

    private void RunDeferredWarmupsAfterFirstAudio(PlaylistEntry playingEntry, YoutubeStreamInput? resolvedInputForProtect, bool raiseNowPlayingChanged)
    {
        try
        {
            if (GetCurrent() is not { } cur || !string.Equals(cur.VideoId, playingEntry.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            var pending = _pendingCurrentDiskWarmEntry;
            if (pending is not null && string.Equals(pending.VideoId, playingEntry.VideoId, StringComparison.OrdinalIgnoreCase))
            {
                TryKickYoutubeBackgroundDiskCache(pending);
                _pendingCurrentDiskWarmEntry = null;
            }

            // if (!raiseNowPlayingChanged)
            //     return;

            var next = GetNextPlayableEntryAfterCurrent();
            if (next is null)
                return;

            // Avoid redundant restart if prefetch is already in progress for the same next track.
            if (_prefetchNextStreamUrlVideoId is not null &&
                string.Equals(_prefetchNextStreamUrlVideoId, next.VideoId, StringComparison.Ordinal))
                return;

            CancelNextTrackWarmBestEffort();
            _nextTrackWarmCts = new CancellationTokenSource();
            var warmCt = _nextTrackWarmCts.Token;
            string[]? protect = null;
            try
            {
                if (resolvedInputForProtect?.Url is { } u &&
                    u.IndexOf("://", StringComparison.OrdinalIgnoreCase) < 0)
                    protect = new[] { u };
                protect ??= BuildProtectPathsForNextWarmFromResolvedInputCache(cur);
            }
            catch
            {
                // ignore
            }

            _ = EnsureCachedBestEffortAsync(next, warmCt, protectedPaths: protect);
            _prefetchNextStreamUrlTask = PrefetchNextYoutubeStreamUrlBestEffortAsync(next, warmCt);
        }
        catch
        {
            // ignore
        }
    }

    private string[]? BuildProtectPathsForNextWarmFromResolvedInputCache(PlaylistEntry cur)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_resolvedInputCache) &&
                string.Equals(_resolvedInputCacheVideoId, cur.VideoId, StringComparison.OrdinalIgnoreCase) &&
                _resolvedInputCache.IndexOf("://", StringComparison.OrdinalIgnoreCase) < 0)
                return new[] { _resolvedInputCache };
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task StartNextTrackWarmAfterAnchorFirstAudioAsync(PlaylistEntry anchor)
    {
        try
        {
            var tcs = _firstAudioForCurrentPlayTcs;
            if (tcs is not null)
                await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            if (GetCurrent() is not { } cur || !string.Equals(cur.VideoId, anchor.VideoId, StringComparison.OrdinalIgnoreCase))
                return;

            var next = GetNextPlayableEntryAfterCurrent();
            if (next is null || !IsYoutubeDiskCacheEligible(next))
            {
                CancelNextTrackWarmBestEffort();
                return;
            }

             // Avoid redundant restart if prefetch is already in progress for the same next track.
            if (_prefetchNextStreamUrlVideoId is not null &&
                string.Equals(_prefetchNextStreamUrlVideoId, next.VideoId, StringComparison.Ordinal))
                return;

            CancelNextTrackWarmBestEffort();
            _nextTrackWarmCts = new CancellationTokenSource();
            var warmCt = _nextTrackWarmCts.Token;
            var protect = BuildProtectPathsForNextWarmFromResolvedInputCache(cur);
            _ = EnsureCachedBestEffortAsync(next, warmCt, protectedPaths: protect);
            _prefetchNextStreamUrlTask = PrefetchNextYoutubeStreamUrlBestEffortAsync(next, warmCt);
        }
        catch
        {
            // ignore
        }
    }

    private bool TryConsumePrefetchedStreamUrl(PlaylistEntry entry, out YoutubeStreamInput stream)
    {
        stream = new YoutubeStreamInput("", null);
        if (string.IsNullOrWhiteSpace(_prefetchNextStreamUrlVideoId) ||
            !string.Equals(_prefetchNextStreamUrlVideoId, entry.VideoId, StringComparison.Ordinal) ||
            _prefetchedStreamInput is null ||
            string.IsNullOrWhiteSpace(_prefetchedStreamInput.Url))
            return false;

        stream = _prefetchedStreamInput;
        ClearPrefetchNextStreamUrl();
        try
        {
            AppLog.Info($"YouTube: using prefetched stream URL for next track {entry.VideoId}", AppLogInfoTier.Crucial);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    private async Task PrefetchNextYoutubeStreamUrlBestEffortAsync(PlaylistEntry next, CancellationToken ct)
    {
        if (!IsYoutubeDiskCacheEligible(next))
            return;
        var vid = next.VideoId;
        _prefetchNextStreamUrlVideoId = vid;
        _prefetchedStreamInput = null;
        try
        {
            if (ct.IsCancellationRequested)
            {
                ClearPrefetchIfStillForVideo(vid);
                return;
            }

            var resolved = await ResolveYoutubeStreamForPlaybackAsync(next, ct, publishStatus: false)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                ClearPrefetchIfStillForVideo(vid);
                return;
            }

            if (GetNextPlayableEntryAfterCurrent() is not { } expect0 || !string.Equals(expect0.VideoId, vid, StringComparison.Ordinal))
            {
                ClearPrefetchIfStillForVideo(vid);
                return;
            }

            if (string.IsNullOrWhiteSpace(resolved.Url))
            {
                if (TryMarkPrefetchSkipFromFailureMessage(vid, "Video unavailable. No playback URL returned."))
                {
                    ClearPrefetchIfStillForVideo(vid);
                    TryBeginPrefetchForNextPlayableAfterMarkedSkip();
                }
                else
                    ClearPrefetchIfStillForVideo(vid);
                return;
            }

            if (!string.Equals(_prefetchNextStreamUrlVideoId, vid, StringComparison.Ordinal))
                return;

            if (GetNextPlayableEntryAfterCurrent() is not { } expect || !string.Equals(expect.VideoId, vid, StringComparison.Ordinal))
                return;

            _prefetchedStreamInput = new YoutubeStreamInput(resolved.Url.Trim(), resolved.HttpHeaders, resolved.DecodeViaYtdlpStdoutPipe);
            try
            {
                AppLog.Info($"YouTube: prefetched next stream URL ({vid})", AppLogInfoTier.Crucial);
            }
            catch
            {
                // ignore
            }

            // ── PCM pipeline pre-start ──────────────────────────────────────────────
            // Kick off yt-dlp + FFmpeg for the next track now so when Next fires we
            // already have buffered audio — no yt-dlp startup or FFmpeg probe delay.
            // Skip if disk cache already has this track (local file starts in < 100 ms).
            if (_cache.TryGetCachedPath(YoutubeDiskCacheStoreKey(vid)) is not null)
                return;

            // Don't clobber an existing session for the same track.
            if (_pcmPrefetch?.VideoId == vid)
                return;

            try { _pcmPrefetch?.Dispose(); } catch { /* ignore */ }
            _pcmPrefetch = null;

            if (ct.IsCancellationRequested)
                return;

            var prefetchDecoder = new FfmpegDecoder(_ffmpegPath);
            Stream pcmStream;
            try
            {
                if (resolved.DecodeViaYtdlpStdoutPipe)
                    pcmStream = await prefetchDecoder.StartPcmS16LeFromYtdlpStdoutPipeAsync(
                        resolved.Url, startSeconds: 0,
                        _ytDlp.YtDlpPath, psi => _ytDlp.ApplyLaunchPrefixTo(psi),
                        ytdlpAudioFormat: _ytDlp.AudioQualityFormat,
                        ytDlpUsesCookiesFromBrowser: _ytDlp.UsesCookiesFromBrowser,
                        cancellationToken: ct).ConfigureAwait(false);
                else
                    pcmStream = prefetchDecoder.StartPcmS16LeStream(resolved.Url, startSeconds: 0, extraHttpHeaders: resolved.HttpHeaders);
            }
            catch (OperationCanceledException)
            {
                prefetchDecoder.Stop();
                ClearPrefetchIfStillForVideo(vid);
                return;
            }
            catch (Exception pcmStartEx)
            {
                prefetchDecoder.Stop();
                if (TryMarkPrefetchSkipFromFailureMessage(vid, pcmStartEx.Message))
                {
                    ClearPrefetchIfStillForVideo(vid);
                    TryBeginPrefetchForNextPlayableAfterMarkedSkip();
                }
                else
                    ClearPrefetchIfStillForVideo(vid);
                return;
            }

            // Final guard: next track may have changed while we were starting the pipeline.
            if (!string.Equals(_prefetchNextStreamUrlVideoId, vid, StringComparison.Ordinal) ||
                GetNextPlayableEntryAfterCurrent() is not { } guard || !string.Equals(guard.VideoId, vid, StringComparison.Ordinal))
            {
                prefetchDecoder.Stop();
                return;
            }

            _pcmPrefetch = new PcmPrefetchSession(vid, prefetchDecoder, pcmStream);
            try { AppLog.Info($"YouTube: PCM prefetch pipeline started for next track ({vid})", AppLogInfoTier.Crucial); } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            ClearPrefetchIfStillForVideo(vid);
        }
        catch (Exception ex)
        {
            if (TryMarkPrefetchSkipFromFailureMessage(vid, ex.Message))
            {
                ClearPrefetchIfStillForVideo(vid);
                TryBeginPrefetchForNextPlayableAfterMarkedSkip();
            }
            else
                ClearPrefetchIfStillForVideo(vid);
            try
            {
                AppLog.Warn($"YouTube: next-track stream prefetch failed ({vid}). {ex.Message}");
            }
            catch
            {
                // ignore
            }
        }
    }

    private void ClearResolvedInputCache()
    {
        _resolvedInputCacheVideoId = null;
        _resolvedInputCache = null;
        _resolvedInputCacheHttpHeaders = null;
        _resolvedInputDecodeViaYtdlpPipe = false;
        _resolvedInputCacheQualityKey = null;
    }

    /// <summary>
    /// Disk-backed cache is only used for normal YouTube watch URLs — not local files, not radio streams, not arbitrary HTTPS media.
    /// </summary>
    private static bool IsYoutubeDiskCacheEligible(PlaylistEntry entry)
    {
        if (TryGetLocalPath(entry.WebpageUrl, out _))
            return false;
        if (entry.VideoId.StartsWith("stream:", StringComparison.OrdinalIgnoreCase))
            return false;

        var url = entry.WebpageUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.IdnHost ?? uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            return false;
        return host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts a non-blocking best-effort full-file cache download for eligible YouTube tracks (same rules as
    /// <see cref="EnsureCachedBestEffortAsync"/>). Used after we begin streaming so revisits can hit disk cache.
    /// </summary>
    private void TryKickYoutubeBackgroundDiskCache(PlaylistEntry entry)
    {
        if (!IsYoutubeDiskCacheEligible(entry))
            return;
        try
        {
            _ = EnsureCachedBestEffortAsync(entry, CancellationToken.None, protectedPaths: null);
        }
        catch
        {
            // ignore
        }
    }

    private Task EnsureCachedBestEffortAsync(PlaylistEntry entry, CancellationToken ct, IEnumerable<string>? protectedPaths)
    {
        if (!IsYoutubeDiskCacheEligible(entry))
            return Task.CompletedTask;

        // Skip known-long videos (downloading a 2-hour concert in the background is not helpful).
        // Unknown-duration entries are attempted with --max-filesize so yt-dlp rejects oversized files on its own.
        if (entry.DurationSeconds > YoutubePreferStreamingOverFullDownloadSeconds)
            return Task.CompletedTask;

        var storeKey = YoutubeDiskCacheStoreKey(entry.VideoId);
        return _cache.EnsureCachedAsync(
            storeKey,
            downloadToCacheAsync: (token) => _ytDlp.DownloadBestAudioToCacheAsync(entry.WebpageUrl,
                cacheDir: Path.Combine(Path.GetTempPath(), "LyllyPlayer", "cache"),
                cacheKey: storeKey,
                cancellationToken: token,
                status: null,
                maxFilesize: "200m"),  // ~1.5 h at 256 kbps; protects against unknown-length concert recordings
            ct: ct,
            protectedPaths: protectedPaths,
            onFailureMessage: msg =>
            {
                if (!TryMarkPrefetchSkipFromFailureMessage(entry.VideoId, msg))
                    return;
                TryBeginPrefetchForNextPlayableAfterMarkedSkip();
            });
    }

    private static bool TryGetLocalPath(string? webpageUrlOrPath, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(webpageUrlOrPath))
            return false;

        var s = webpageUrlOrPath.Trim();

        try
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                uri.IsFile &&
                !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                var p = uri.LocalPath;
                if (File.Exists(p))
                {
                    path = p;
                    return true;
                }
                return false;
            }
        }
        catch
        {
            // ignore
        }

        // Also accept plain filesystem paths.
        try
        {
            if (File.Exists(s))
            {
                path = s;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public void Stop()
    {
        StopInternal(signalPlaybackStopped: true);
        NowPlayingChanged?.Invoke(this, null);  // Changed from GetCurrent() to null
    }

    /// <summary>Clears decoder/CTS after a failed <see cref="PlayEntryAsync"/> without resetting queue position state.</summary>
    private void AbortPlaybackPipelineAfterFailure()
    {
        try { _playCts?.Cancel(); } catch { /* ignore */ }
        _playCts = null;
        try { _decoder.Stop(); } catch { /* ignore */ }
        ClearResolvedInputCache();
        ClearDeferredWarmState();
        CancelNextTrackWarmBestEffort();
    }

    private void StopInternal(bool signalPlaybackStopped)
    {
        try { _playCts?.Cancel(); } catch { /* ignore */ }
        _playCts = null;
        ClearDeferredWarmState();

        try { _positionSw.Stop(); } catch { /* ignore */ }
        try { _positionSw.Reset(); } catch { /* ignore */ }
        _startOffsetSeconds = 0;
        try { _analyzer.Reset(); } catch { /* ignore */ }

        _pauseGate.Set();

        try { _decoder.Stop(); } catch { /* ignore */ }
        try { _activePrefetchDecoder?.Stop(); _activePrefetchDecoder = null; } catch { /* ignore */ }
        try { _audio?.Stop(); } catch { /* ignore */ }
        if (signalPlaybackStopped)
        {
            PlaybackStateChanged?.Invoke(this, false);
            ClearResolvedInputCache();
            CancelNextTrackWarmBestEffort();
        }
    }

    public void Dispose()
    {
        StopInternal(signalPlaybackStopped: true);
        CancelNextTrackWarmBestEffort();
        _pauseGate.Dispose();
        _playExclusive.Dispose();
        _queueNavLock.Dispose();
        _decoder.Dispose();
        _audio?.Dispose();
    }

    /// <summary>Optional buffering timeline; emits <c>[PlaybackTiming]</c> lines at diagnostic log level.</summary>
    private sealed class PlaybackTimingMark
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastMs;
        private readonly string _vid;

        public PlaybackTimingMark(string? videoId) => _vid = videoId ?? "";

        public void Step(string label)
        {
            var now = _sw.ElapsedMilliseconds;
            var delta = now - _lastMs;
            _lastMs = now;
            try
            {
                AppLog.Info($"[PlaybackTiming] vid={_vid} step={label} deltaMs={delta} totalMs={now}", AppLogInfoTier.Diagnostic);
            }
            catch
            {
                // ignore
            }
        }
    }

    // ── PCM pre-fetch infrastructure ─────────────────────────────────────────────────────

    /// <summary>
    /// Owns a dedicated <see cref="FfmpegDecoder"/> pipeline for the upcoming track and
    /// reads its PCM output into a memory buffer so the Next transition can start instantly.
    /// </summary>
    private sealed class PcmPrefetchSession : IDisposable
    {
        public string VideoId { get; }

        /// <summary>The decoder that owns yt-dlp + FFmpeg processes for this prefetch.</summary>
        public FfmpegDecoder OwnedDecoder { get; }

        private readonly CancellationTokenSource _cts = new();
        private readonly List<byte[]> _chunks = new();
        private long _totalBytes;
        private readonly Stream _liveStream;
        private bool _stolen;
        private readonly Task _fillTask;

        // Raw stereo float32 @ 48 kHz ≈ 384 kiB/s. Cap prefetch to limit RAM (was 96 MiB ≈ 4.2 min).
        private const long MaxBufferBytes = 48L * 1024 * 1024;

        public PcmPrefetchSession(string videoId, FfmpegDecoder decoder, Stream liveStream)
        {
            VideoId = videoId;
            OwnedDecoder = decoder;
            _liveStream = liveStream;
            _fillTask = RunAsync();
        }

        private async Task RunAsync()
        {
            var buf = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (_totalBytes < MaxBufferBytes)
                {
                    int read;
                    try { read = await _liveStream.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    if (read <= 0) break;
                    var chunk = new byte[read];
                    Array.Copy(buf, chunk, read);
                    lock (_chunks) { _chunks.Add(chunk); _totalBytes += read; }
                }
            }
            catch { /* ignore */ }
            finally
            {
                try { ArrayPool<byte>.Shared.Return(buf); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Stop background fill and return a stream that replays the buffered data
        /// then continues from the live pipeline.
        /// </summary>
        public async ValueTask<Stream> StealAsync()
        {
            _stolen = true;
            _cts.Cancel();
            // Wait briefly so the current ReadAsync exits and the chunk list is consistent.
            try { await _fillTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
            List<byte[]> snap;
            lock (_chunks)
            {
                snap = new List<byte[]>(_chunks);
                _chunks.Clear();
                _totalBytes = 0;
            }

            return new PrefetchedPcmStream(snap, _liveStream);
        }

        public void Dispose()
        {
            if (!_stolen)
            {
                _cts.Cancel();
                OwnedDecoder.Stop();
                try { _liveStream.Dispose(); } catch { /* ignore */ }
            }
            else
            {
                // StealAsync moved the live stream to PrefetchedPcmStream; only cancel the filler.
                try { _cts.Cancel(); } catch { /* ignore */ }
            }

            lock (_chunks)
            {
                _chunks.Clear();
                _totalBytes = 0;
            }

            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// A read-only stream that first replays <paramref name="buffered"/> chunks then falls
    /// through to reading from <paramref name="live"/>.
    /// </summary>
    private sealed class PrefetchedPcmStream : Stream
    {
        private readonly List<byte[]> _buf;
        private readonly Stream _live;
        private int _ci, _co;

        public PrefetchedPcmStream(List<byte[]> buf, Stream live) { _buf = buf; _live = live; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override int Read(byte[] b, int o, int c)
        {
            if (_ci < _buf.Count)
            {
                var chunk = _buf[_ci]; var avail = chunk.Length - _co; var n = Math.Min(avail, c);
                Array.Copy(chunk, _co, b, o, n); _co += n;
                if (_co >= chunk.Length) { _ci++; _co = 0; }
                return n;
            }
            return _live.Read(b, o, c);
        }

        public override async Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct)
        {
            if (_ci < _buf.Count)
            {
                var chunk = _buf[_ci]; var avail = chunk.Length - _co; var n = Math.Min(avail, c);
                Array.Copy(chunk, _co, b, o, n); _co += n;
                if (_co >= chunk.Length) { _ci++; _co = 0; }
                return n;
            }
            return await _live.ReadAsync(b, o, c, ct).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _live.Dispose(); } catch { /* ignore */ }
                _buf.Clear();
            }

            base.Dispose(disposing);
        }
    }
}


