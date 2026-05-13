using LibVLCSharp.Shared;
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
using System.Windows.Threading;

namespace LyllyPlayer.Player;

/// <summary>Emitted when background prefetch or disk-cache warm detects a definitive YouTube refusal (unavailable, age, premium, DRM).</summary>
public sealed record PlaybackPrefetchTag(string VideoId, string Category, string Message);

public sealed partial class PlaybackEngine : IDisposable
{
    private readonly object _vlcGate = new();
    public delegate PlaylistEntry? NextTrackResolver();
    private NextTrackResolver? _nextTrackResolver;
    /// <summary>
    /// Optional "peek" resolver for background warm/prefetch that must NOT mutate shuffle/queue state.
    /// When not set, warm/prefetch uses sequential <see cref="PlayOrder"/> based logic.
    /// </summary>
    public delegate PlaylistEntry? NextTrackPeekResolver();
    private NextTrackPeekResolver? _nextTrackPeekResolver;
    private readonly YtDlpClient _ytDlp;
    private Func<CancellationToken, Task<bool>>? _ensureYtDlpReadyAsync;
    private AudioOut? _audio;
    private readonly WaveFormat _format;
    private int _audioDeviceNumber = -1; // -1 = WAVE_MAPPER (default)

    private CancellationTokenSource? _playCts;
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
    private readonly VisualizerTap _visualizerTap;
    private readonly VlcVisualizerTap _vlcVisualizerTap;
    private System.Threading.Timer? _visualizerResyncTimer;
    private int _visualizerResyncActive;

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
    /// Background disk-cache warm is skipped above this duration (streaming only for long content).
    /// Playback always starts via stream URL / pipe — we never block on a full-file download before decode.
    /// </summary>
    private const int YoutubePreferStreamingOverFullDownloadSeconds = 20 * 60;

    /// <summary>
    /// Do not use an existing on-disk cache for YouTube at/above this length — always stream (multi‑GB cache, and
    /// LibVLC seek on a local progressive file is less relevant than consistent DASH streaming for very long content).
    /// </summary>
    private const int YoutubeNeverUseDiskCachePlaybackSeconds = 12 * 60 * 60;

    public PlaybackEngine(YtDlpClient ytDlp)
    {
        _ytDlp = ytDlp;
        _ytDlp.SetFfmpegPath(null);
        _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _visualizerTap = new VisualizerTap(_analyzer);
        _vlcVisualizerTap = new VlcVisualizerTap(_analyzer);

        _cacheMaxBytes = 512L * 1024 * 1024;
        var cacheDir = Path.Combine(Path.GetTempPath(), "LyllyPlayer", "cache");
        _cache = new CacheManager(cacheDir, _cacheMaxBytes);
        _cache.CacheEntryAdded += (_, _) =>
        {
            try { YoutubeDiskCacheReady?.Invoke(this, EventArgs.Empty); } catch { /* ignore */ }
        };
    }

    public void SetVlcAudioCallbacksEnabled(bool enabled)
    {
        _enableVlcAudioCallbacks = enabled;
    }

    /// <summary>Called before any yt-dlp resolve/download so the shell can prompt or download internal yt-dlp.</summary>
    public void SetEnsureYtDlpReadyAsync(Func<CancellationToken, Task<bool>>? callback)
        => _ensureYtDlpReadyAsync = callback;

    private async Task<bool> EnsureYtDlpReadyForResolveAsync(CancellationToken ct)
    {
        if (_ensureYtDlpReadyAsync is null)
            return true;
        return await _ensureYtDlpReadyAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<PlaylistEntry> PlayOrder { get; private set; } = Array.Empty<PlaylistEntry>();
    public int CurrentIndex { get; private set; } = -1;
    public bool IsPlaying
    {
        get
        {
            lock (_vlcGate)
            {
                // Avoid polling LibVLC state (can crash on some systems when called frequently from UI timer).
                return _vlcMp is not null && _vlcIsPlayingFlag;
            }
        }
    }

    public bool CanResume => (_vlcMp is not null || _audio is not null) && _playCts is not null;

    public double CurrentPositionSeconds
    {
        get
        {
            // Drive UI clock from our own stopwatch to avoid polling LibVLC Time at 30fps (can crash natively).
            return _startOffsetSeconds + _positionSw.Elapsed.TotalSeconds;
        }
    }
    public int? CurrentDurationSeconds => _currentDurationSeconds;
    public (float vuL, float vuR, float[] bands) GetAudioAnalysisSnapshot() => _analyzer.GetSnapshot();

    public event EventHandler<PlaylistEntry?>? NowPlayingChanged;
    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<(PlaylistEntry entry, string message)>? PlaybackFailed;
    public event EventHandler<(PlaylistEntry entry, string status, string? detail)>? StatusChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<(PlaylistEntry entry, bool endedEarly)>? TrackEnded;
    public event EventHandler<PlaybackPrefetchTag>? PrefetchTagged;
    /// <summary>Raised when a YouTube disk cache entry is fully written and indexed (export MP3 can proceed).</summary>
    public event EventHandler? YoutubeDiskCacheReady;

    private bool _audioNormalizeEnabled;

    public void SetAudioNormalizeEnabled(bool enabled) => _audioNormalizeEnabled = enabled;

    [Obsolete("FFmpeg is no longer used; call is ignored.")]
    public void SetFfmpegPath(string ffmpegPath)
    {
        _ytDlp.SetFfmpegPath(null);
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
        try
        {
            if (_vlcMp is not null)
            {
                lock (_vlcGate)
                {
                    // When LibVLC audio callbacks are enabled, LibVLC is not the audible output device.
                    // Apply volume only on our WaveOut sink to avoid double-scaling / non-linear behavior.
                    _vlcMp.Volume = _enableVlcAudioCallbacks ? 100 : (int)Math.Clamp(_volume * 100.0, 0, 100);
                }
            }
        }
        catch { /* ignore */ }
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
        if (current is null || (!current.IsPlaying && _vlcMp?.State != VLCState.Playing))
            return;

        try
        {
            var next = new AudioOut(_format, deviceNumber, onSamplesRead: _analyzer.ProcessPcmF32LeStereo, normalize: _audioNormalizeEnabled);
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

    public void SetNextTrackPeekResolver(NextTrackPeekResolver resolver)
    {
        _nextTrackPeekResolver = resolver;
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

        try { AppLog.Warn($"PlayCurrentAsync: dispatching PlayEntryAsync videoId={current.VideoId}"); } catch { /* ignore */ }
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

        try
        {
            RefreshSeekableBufferedFromCache();
            if (_lastResolvedForWarmup?.DecodeViaYtdlpStdoutPipe == true)
                target = Math.Min(target, Math.Max(0, MaxSeekSecondsForUi));
        }
        catch { /* ignore */ }

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
                RaisePlaybackStateChanged(false);
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
        if (_audio is null && _vlcMp is null)
            return;

        if (IsPlaying)
        {
            _pauseGate.Reset();
            try { _visualizerTap.SetPaused(true); } catch { /* ignore */ }
            try { _vlcVisualizerTap.SetPaused(true); } catch { /* ignore */ }
            try { _vlcMp?.SetPause(true); } catch { /* ignore */ }
            try { _audio?.Pause(); } catch { /* ignore */ }
            try { _positionSw.Stop(); } catch { /* ignore */ }
            RaisePlaybackStateChanged(false);
        }
        else
        {
            _pauseGate.Set();
            try { _visualizerTap.SetPaused(false); } catch { /* ignore */ }
            try { _vlcVisualizerTap.SetPaused(false); } catch { /* ignore */ }
            try { _vlcMp?.SetPause(false); } catch { /* ignore */ }
            if (_audio is not null && !_audio.TryPlay() && !TryRecoverAudioOutput(leaveStoppedSinkOnTotalFailure: true))
            {
                _pauseGate.Reset();
                try { _positionSw.Stop(); } catch { /* ignore */ }
                RaisePlaybackStateChanged(false);
                return;
            }

            try { _positionSw.Start(); } catch { /* ignore */ }
            RaisePlaybackStateChanged(true);
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
                var a = new AudioOut(_format, deviceNumber, onSamplesRead: _analyzer.ProcessPcmF32LeStereo, normalize: _audioNormalizeEnabled);
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
                var a = new AudioOut(_format, -1, onSamplesRead: _analyzer.ProcessPcmF32LeStereo, normalize: _audioNormalizeEnabled);
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

    private static void LogPlaybackError(PlaylistEntry entry, string message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim();
        var title = string.IsNullOrWhiteSpace(entry.Title) ? "" : $" title=\"{entry.Title}\"";
        try { AppLog.Error($"Playback VideoId={entry.VideoId}{title}. {detail}"); } catch { /* ignore */ }
    }

    private async Task<bool> PlayEntryAsync(PlaylistEntry entry, double startSeconds, bool raiseNowPlayingChanged)
    {
        try { AppLog.Warn($"PlayEntryAsync: begin videoId={entry.VideoId} start={startSeconds:0.###} nowPlayingEvent={raiseNowPlayingChanged}"); } catch { /* ignore */ }

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

            try
            {
                TeardownVlcBestEffort();
                await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }

            playbackTiming.Step("after_previous_reader_barrier");

            try { AppLog.Warn($"PlayEntryAsync: after_previous_reader_barrier videoId={entry.VideoId}"); } catch { /* ignore */ }

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

            try { AppLog.Warn($"PlayEntryAsync: timeline_reset_done videoId={entry.VideoId} startOffset={_startOffsetSeconds:0.###} dur={_currentDurationSeconds?.ToString() ?? "(null)"}"); } catch { /* ignore */ }

            if (raiseNowPlayingChanged)
            {
                try
                {
                    try { AppLog.Warn($"PlayEntryAsync: invoking NowPlayingChanged videoId={entry.VideoId}"); } catch { /* ignore */ }
                    RaiseNowPlayingChanged(entry);
                    try { AppLog.Warn($"PlayEntryAsync: NowPlayingChanged scheduled/returned videoId={entry.VideoId}"); } catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    try { AppLog.Exception(ex, "NowPlayingChanged handler failed"); } catch { /* ignore */ }
                }
            }

            try { AppLog.Warn($"PlayEntryAsync: after_now_playing_event videoId={entry.VideoId}"); } catch { /* ignore */ }

            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;
            var playbackSessionCts = _playCts;
            _firstAudioForCurrentPlayTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try { AppLog.Warn($"PlayEntryAsync: session_tokens_ready videoId={entry.VideoId}"); } catch { /* ignore */ }

            try
            {
                playbackTiming.Step("before_resolve_best_input");
                try { AppLog.Warn($"PlayEntryAsync: before_resolve videoId={entry.VideoId}"); } catch { /* ignore */ }
                var resolvedInput = await ResolveBestInputAsync(entry, ct, playbackTiming, publishResolveStatus: raiseNowPlayingChanged).ConfigureAwait(false);
                playbackTiming.Step("after_resolve_best_input");

                if (resolvedInput.DecodeViaYtdlpStdoutPipe)
                {
                    UpdateSeekableBuffered(entry, resolvedInput);
                    if (entry.DurationSeconds is int durClamp && durClamp > 0 &&
                        _startOffsetSeconds > SeekableBufferedSeconds + 0.25 &&
                        SeekableBufferedSeconds > 0.25)
                    {
                        _startOffsetSeconds = Math.Min(_startOffsetSeconds, Math.Max(0, Math.Min(durClamp - 1, SeekableBufferedSeconds)));
                    }
                }

                if (!await PlayResolvedWithLibVlcAsync(entry, resolvedInput, playbackTiming, ct, playbackSessionCts, raiseNowPlayingChanged).ConfigureAwait(false))
                    return false;

                return true;
            }
            catch (OperationCanceledException)
            {
                RaisePlaybackStateChanged(false);
                AbortPlaybackPipelineAfterFailure();
                return false;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                string? stderrTail = null;
                try
                {
                    stderrTail = GetPlaybackDiagTail(8000);
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

    private void RaiseNowPlayingChanged(PlaylistEntry entry)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null && !disp.CheckAccess())
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try { NowPlayingChanged?.Invoke(this, entry); }
                    catch (Exception ex)
                    {
                        try { AppLog.Exception(ex, "NowPlayingChanged(UI) handler failed"); } catch { /* ignore */ }
                    }
                }), DispatcherPriority.Render);
                return;
            }
        }
        catch { /* ignore */ }

        NowPlayingChanged?.Invoke(this, entry);
    }

    private void RaisePlaybackStateChanged(bool isPlaying)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null && !disp.CheckAccess())
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try { PlaybackStateChanged?.Invoke(this, isPlaying); }
                    catch (Exception ex)
                    {
                        try { AppLog.Exception(ex, "PlaybackStateChanged(UI) handler failed"); } catch { /* ignore */ }
                    }
                }), DispatcherPriority.Render);
                return;
            }
        }
        catch { /* ignore */ }

        PlaybackStateChanged?.Invoke(this, isPlaying);
    }

    private void RaiseStatusChanged(PlaylistEntry entry, string status, string? detail)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp is not null && !disp.CheckAccess())
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try { StatusChanged?.Invoke(this, (entry, status, detail)); }
                    catch (Exception ex)
                    {
                        try { AppLog.Exception(ex, "StatusChanged(UI) handler failed"); } catch { /* ignore */ }
                    }
                }), DispatcherPriority.Render);
                return;
            }
        }
        catch { /* ignore */ }

        StatusChanged?.Invoke(this, (entry, status, detail));
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

    /// <summary>
    /// Best-effort "next" used for background prefetch/warm. If a peek resolver is configured (e.g. Shuffle buffer),
    /// prefer it; otherwise fall back to sequential play order scanning.
    /// </summary>
    private PlaylistEntry? GetNextEntryForWarmPrefetch()
    {
        try
        {
            if (_nextTrackPeekResolver is not null)
            {
                var peek = _nextTrackPeekResolver();
                if (peek is not null && !_prefetchSkipVideoIds.ContainsKey(peek.VideoId))
                    return peek;
            }
        }
        catch { /* ignore */ }

        return GetNextPlayableEntryAfterCurrent();
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

        if (!await EnsureYtDlpReadyForResolveAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("yt-dlp is not configured. Open Options → Tools to download or browse for yt-dlp.");

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

            var next = GetNextEntryForWarmPrefetch();
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

            var next = GetNextEntryForWarmPrefetch();
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

            if (GetNextEntryForWarmPrefetch() is not { } expect0 || !string.Equals(expect0.VideoId, vid, StringComparison.Ordinal))
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

            if (GetNextEntryForWarmPrefetch() is not { } expect || !string.Equals(expect.VideoId, vid, StringComparison.Ordinal))
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

            // LibVLC path: next-track PCM prefetch removed — URL prefetch + disk cache warm remain.
            if (_cache.TryGetCachedPath(YoutubeDiskCacheStoreKey(vid)) is not null)
                return;
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

    /// <summary>Returns the completed on-disk cache file for a YouTube entry (same key as playback), or null when missing or ineligible.</summary>
    public bool TryGetYoutubeDiskCachePath(PlaylistEntry entry, out string? path)
    {
        path = null;
        if (!IsYoutubeDiskCacheEligible(entry))
            return false;
        var key = YoutubeDiskCacheStoreKey(entry.VideoId);
        var p = _cache.TryGetCachedPath(key);
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
            return false;
        path = p;
        return true;
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
        try { TeardownVlcBestEffort(); } catch { /* ignore */ }
        ClearResolvedInputCache();
        ClearDeferredWarmState();
        CancelNextTrackWarmBestEffort();
    }

    private void StopInternal(bool signalPlaybackStopped)
    {
        try { _playCts?.Cancel(); } catch { /* ignore */ }
        _playCts = null;
        ClearDeferredWarmState();

        try { _visualizerResyncTimer?.Dispose(); } catch { /* ignore */ }
        _visualizerResyncTimer = null;
        try { Interlocked.Exchange(ref _visualizerResyncActive, 0); } catch { /* ignore */ }

        try { _positionSw.Stop(); } catch { /* ignore */ }
        try { _positionSw.Reset(); } catch { /* ignore */ }
        _startOffsetSeconds = 0;
        try { _visualizerTap.Stop(); } catch { /* ignore */ }
        try { _vlcVisualizerTap.Stop(); } catch { /* ignore */ }
        try { _analyzer.Reset(); } catch { /* ignore */ }

        _pauseGate.Set();

        try { TeardownVlcBestEffort(); } catch { /* ignore */ }
        try { _audio?.Stop(); } catch { /* ignore */ }
        if (signalPlaybackStopped)
        {
            RaisePlaybackStateChanged(false);
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
        try { TeardownVlcBestEffort(); } catch { /* ignore */ }
        try { _vlcMp?.Dispose(); } catch { /* ignore */ }
        _vlcMp = null;
        _audio?.Dispose();
        try { _visualizerTap.Dispose(); } catch { /* ignore */ }
        try { _vlcVisualizerTap.Dispose(); } catch { /* ignore */ }
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

}


