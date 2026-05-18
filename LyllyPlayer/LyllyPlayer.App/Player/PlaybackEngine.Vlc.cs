using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;
using NAudio.Wave;

namespace LyllyPlayer.Player;

public sealed partial class PlaybackEngine
{
    // LibVLC audio callbacks enable perfect visualizer sync (pause/seek), but may be unstable on some systems.
    // This is an opt-in runtime setting (Options → Audio).
    private const int MaxPipeDiscardSeekSeconds = 90;
    private const int PipeDiscardSeekTimeoutMs = 4000;

    private MediaPlayer? _vlcMp;
    private Media? _vlcMedia;
    private YtdlpPipeMediaInput? _vlcPipeInput;
    private bool _vlcDecodeViaPipe;
    private bool _vlcFirstAudioRaised;
    private int _vlcFirstAudioGate;
    private bool _vlcEndHandled;
    private string? _vlcActiveVideoId;
    private bool _vlcIsPlayingFlag;
    /// <summary>When true, <see cref="OnVlcAudioPlay"/> also pushes PCM into <see cref="AudioOut"/> (required for pipe decode).</summary>
    private bool _playbackUsesWaveOutSink;
    private int _vlcAudioCallbackGeneration;
    private int _pendingSeekSinkFlush;
    private int _deferWaveOutStart;
    /// <summary>Bumped on every LibVLC teardown so stale <see cref="MediaPlayer.EndReached"/> events are ignored.</summary>
    private int _vlcPlaybackEpoch;
    /// <summary>While set, defer the position stopwatch and ignore spurious EndReached from pre-seek decode.</summary>
    private int _awaitingVlcSeekSettle;

    private MediaPlayer.LibVLCAudioPlayCb? _vlcPlayCb;
    private MediaPlayer.LibVLCAudioPauseCb? _vlcPauseCb;
    private MediaPlayer.LibVLCAudioResumeCb? _vlcResumeCb;
    private MediaPlayer.LibVLCAudioFlushCb? _vlcFlushCb;
    private MediaPlayer.LibVLCAudioDrainCb? _vlcDrainCb;

    private EventHandler<EventArgs>? _vlcEndReachedHandler;
    private EventHandler<EventArgs>? _vlcEncounteredErrorHandler;
    private EventHandler<EventArgs>? _vlcPlayingHandler;
    private EventHandler<EventArgs>? _vlcPausedHandler;
    private EventHandler<EventArgs>? _vlcStoppedHandler;

    private YoutubeStreamInput? _lastResolvedForWarmup;
    private bool _lastRaiseNowPlayingForWarmup;

    /// <summary>
    /// For cookie-pipe YouTube playback: seconds the user may seek to (from t=0), approximated from disk-cache growth.
    /// Local/direct streams: full duration when known.
    /// </summary>
    public double SeekableBufferedSeconds { get; private set; }

    /// <summary>True for YouTube stream/pipe (not local file) — seek is limited to the buffered window.</summary>
    public bool UsesLimitedSeekWindow
    {
        get
        {
            if (GetCurrent() is not { } cur)
                return false;
            if (_lastResolvedForWarmup is { } resolved)
                return IsLimitedSeekInput(resolved, cur);
            // Briefly null during seek restarts — still streaming via WaveOut sink.
            return _playbackUsesWaveOutSink && _vlcMp is not null;
        }
    }

    /// <summary>Upper bound for user seeks (UI + <see cref="SeekAsync"/>). On HTTP streams this is the live decode head, not disk-cache bytes.</summary>
    public double MaxSeekSecondsForUi
    {
        get
        {
            if (GetCurrent() is { } cur && _lastResolvedForWarmup is { } resolved &&
                IsLimitedSeekInput(resolved, cur))
            {
                try { UpdateSeekableBuffered(cur, resolved); } catch { /* ignore */ }
                return ComputeDiskBufferedSeekMaxSeconds(cur, resolved);
            }

            if (_currentDurationSeconds is int d0 && d0 > 0)
                return d0;

            return Math.Max(0, SeekableBufferedSeconds);
        }
    }

    /// <summary>Stay inside the decode window on HTTP — LibVLC <c>Time</c> past this crashes.</summary>
    private const double HttpSeekEndMarginSeconds = 2.5;

    private const double PipeSeekEndMarginSeconds = 2.0;

    private bool IsLimitedSeekInput(YoutubeStreamInput resolved, PlaylistEntry entry)
    {
        _ = entry;
        return !IsResolvedLocalMediaPath(resolved);
    }

    /// <summary>googlevideo / DASH URL in LibVLC — <see cref="MediaPlayer.Time"/> here is crash-prone.</summary>
    private static bool IsRemoteHttpStreamInput(YoutubeStreamInput resolved)
    {
        if (resolved.DecodeViaYtdlpStdoutPipe)
            return false;
        if (IsResolvedLocalMediaPath(resolved))
            return false;

        var u = resolved.Url.Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private void RestartTimelineClock()
    {
        try { _positionSw.Restart(); } catch { /* ignore */ }
    }

    /// <summary>Re-reads partial disk cache size so <see cref="SeekableBufferedSeconds"/> grows while yt-dlp caches in the background.</summary>
    public void RefreshSeekableBufferedFromCache()
    {
        try
        {
            if (GetCurrent() is not { } cur)
                return;

            if (_lastResolvedForWarmup is { } resolved)
            {
                UpdateSeekableBuffered(cur, resolved);
                return;
            }

            // Brief gap while restarting — still advance the live decode head for the seek bar.
            if (_playbackUsesWaveOutSink &&
                string.Equals(cur.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyLivePlaybackBufferedEstimate(cur);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyLivePlaybackBufferedEstimate(PlaylistEntry entry)
    {
        var est = EstimateBufferedSecondsFromPlayback();
        if (entry.DurationSeconds is int d && d > 0)
            SeekableBufferedSeconds = Math.Min(d, est);
        else
            SeekableBufferedSeconds = est;
    }

    /// <summary>Buffered extent for the seek-bar track (includes live decode head while streaming).</summary>
    public double SeekBarBufferedSeconds
    {
        get
        {
            try { RefreshSeekableBufferedFromCache(); } catch { /* ignore */ }

            if (GetCurrent() is not { } cur)
                return Math.Max(0, SeekableBufferedSeconds);

            if (_lastResolvedForWarmup is not { } resolved)
            {
                if (_playbackUsesWaveOutSink &&
                    string.Equals(cur.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase))
                {
                    var live = EstimateBufferedSecondsFromPlayback();
                    if (_currentDurationSeconds is int d0 && d0 > 0)
                        return Math.Min(d0, live);
                    return Math.Max(0, live);
                }

                return Math.Max(0, SeekableBufferedSeconds);
            }

            if (!IsLimitedSeekInput(resolved, cur))
            {
                if (_currentDurationSeconds is int d && d > 0)
                    return d;
                return Math.Max(0, SeekableBufferedSeconds);
            }

            var est = Math.Max(SeekableBufferedSeconds, EstimateBufferedSecondsFromPlayback());
            if (_currentDurationSeconds is int dur && dur > 0)
                return Math.Min(dur, est);
            return Math.Max(0, est);
        }
    }

    private void TeardownVlcBestEffort()
    {
        try { _vlcPipeInput?.ForceStop(); } catch { /* ignore */ }
        LibVlcHost.RunOnUiThread(TeardownVlcBestEffortCore, TimeSpan.FromSeconds(2));
    }

    private async Task TeardownVlcBestEffortAsync(CancellationToken ct)
    {
        try { _vlcPipeInput?.ForceStop(); } catch { /* ignore */ }
        try
        {
            await LibVlcHost.RunOnUiThreadAsync(TeardownVlcBestEffortCore, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { /* ignore */ }
    }

    private void TeardownVlcBestEffortCore()
    {
        Interlocked.Increment(ref _vlcPlaybackEpoch);
        try
        {
            if (_vlcMp is not null)
            {
                if (_vlcEndReachedHandler is not null)
                {
                    try { _vlcMp.EndReached -= _vlcEndReachedHandler; } catch { /* ignore */ }
                }

                if (_vlcEncounteredErrorHandler is not null)
                {
                    try { _vlcMp.EncounteredError -= _vlcEncounteredErrorHandler; } catch { /* ignore */ }
                }

                if (_vlcPlayingHandler is not null)
                {
                    try { _vlcMp.Playing -= _vlcPlayingHandler; } catch { /* ignore */ }
                }
                if (_vlcPausedHandler is not null)
                {
                    try { _vlcMp.Paused -= _vlcPausedHandler; } catch { /* ignore */ }
                }
                if (_vlcStoppedHandler is not null)
                {
                    try { _vlcMp.Stopped -= _vlcStoppedHandler; } catch { /* ignore */ }
                }

                try { lock (_vlcGate) { _vlcMp.Stop(); } } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        _vlcEndReachedHandler = null;
        _vlcEncounteredErrorHandler = null;
        _vlcPlayingHandler = null;
        _vlcPausedHandler = null;
        _vlcStoppedHandler = null;

        try { _vlcMedia?.Dispose(); } catch { /* ignore */ }
        _vlcMedia = null;

        try { _vlcPipeInput?.ForceStop(); } catch { /* ignore */ }
        try { _vlcPipeInput?.Dispose(); } catch { /* ignore */ }
        _vlcPipeInput = null;
        _vlcDecodeViaPipe = false;
        _vlcFirstAudioRaised = false;
        Interlocked.Exchange(ref _vlcFirstAudioGate, 0);
        _vlcEndHandled = false;
        _vlcActiveVideoId = null;
        _vlcIsPlayingFlag = false;
    }

    /// <summary>
    /// Route LibVLC through app <see cref="AudioOut"/> (audio callbacks + NAudio). Required for yt-dlp pipe
    /// and remote HTTP(S) so VU taps audible PCM instead of a second LibVLC decoder.
    /// </summary>
    private static bool RequiresVlcWaveOutSink(YoutubeStreamInput input)
    {
        if (input.DecodeViaYtdlpStdoutPipe)
            return true;

        var url = input.Url.AsSpan().Trim();
        if (url.IsEmpty)
            return false;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (File.Exists(input.Url.Trim()))
                return false;
        }
        catch { /* ignore */ }

        return false;
    }

    private void EnsureVlcAudioCallbacksWired()
    {
        if (_vlcMp is null)
            return;

        _vlcPlayCb ??= OnVlcAudioPlay;
        _vlcPauseCb ??= OnVlcAudioPause;
        _vlcResumeCb ??= OnVlcAudioResume;
        _vlcFlushCb ??= OnVlcAudioFlush;
        _vlcDrainCb ??= OnVlcAudioDrain;

        // Do NOT assume FL32 is honored on all systems/codecs. S16N is broadly supported and avoids
        // crashing when VLC feeds 16-bit samples but we interpret them as float.
        lock (_vlcGate)
        {
            _vlcMp.SetAudioFormat("S16N", 48000, 2);
            _vlcMp.SetAudioCallbacks(_vlcPlayCb, _vlcPauseCb, _vlcResumeCb, _vlcFlushCb, _vlcDrainCb);
        }
    }

    private void OnVlcAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (samples == IntPtr.Zero || count == 0)
            return;

        if (_vlcAudioCallbackGeneration != Volatile.Read(ref _playGeneration))
            return;

        // libvlc_audio_play_cb contract: <count> is the number of samples per channel (frames).
        // Format is S16N stereo @ 48kHz (requested above).
        const int channels = 2;
        var frames = (int)Math.Min(count, 48000u); // cap to 1s of audio
        if (frames <= 0)
            return;

        var sampleCount = frames * channels; // interleaved samples (L,R,L,R,...)
        var rentedS16 = System.Buffers.ArrayPool<short>.Shared.Rent(sampleCount);
        var floatBytes = sampleCount * sizeof(float);
        var rentedF32 = System.Buffers.ArrayPool<byte>.Shared.Rent(floatBytes);
        try
        {
            Marshal.Copy(samples, rentedS16, 0, sampleCount);

            // Convert s16 -> f32le into rentedF32 without per-sample allocations.
            var f32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                rentedF32.AsSpan(0, floatBytes));
            for (var i = 0; i < sampleCount; i++)
                f32[i] = rentedS16[i] / 32768f;

            if (_playbackUsesWaveOutSink && _audio is { } sink)
            {
                sink.AddSamples(rentedF32, 0, floatBytes);
                TryStartDeferredWaveOut();
            }

            var isFirstAudio = Interlocked.CompareExchange(ref _vlcFirstAudioGate, 1, 0) == 0;
            if (isFirstAudio)
            {
                _vlcFirstAudioRaised = true;
                if (Volatile.Read(ref _awaitingVlcSeekSettle) == 0)
                    RestartTimelineClock();
            }

            // VU is fed from AudioOut on read (what WaveOut sends to the device), not here — avoids decode-ahead / PTS drift.

            if (!isFirstAudio)
                return;

            try
            {
                if (GetCurrent() is { } cur && _vlcActiveVideoId is not null &&
                    string.Equals(cur.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase))
                {
                    RaiseStatusChanged(cur, "PLAYING", null);
                    _firstAudioForCurrentPlayTcs?.TrySetResult(true);
                    RunDeferredWarmupsAfterFirstAudio(cur, _lastResolvedForWarmup, _lastRaiseNowPlayingForWarmup);
                }
            }
            catch { /* ignore */ }
        }
        finally
        {
            try { System.Buffers.ArrayPool<short>.Shared.Return(rentedS16); } catch { /* ignore */ }
            try { System.Buffers.ArrayPool<byte>.Shared.Return(rentedF32); } catch { /* ignore */ }
        }
    }

    private void OnVlcAudioPause(IntPtr data, long pts)
    {
        try { _audio?.Pause(); } catch { /* ignore */ }
    }

    private void OnVlcAudioResume(IntPtr data, long pts)
    {
        try { _audio?.TryPlay(); } catch { /* ignore */ }
    }

    private void OnVlcAudioFlush(IntPtr data, long pts)
    {
        _ = (data, pts);
        // After a user seek we must drop prefetched PCM; ignore routine network/pipe flushes otherwise.
        if (Volatile.Read(ref _pendingSeekSinkFlush) == 0)
            return;
        Interlocked.Exchange(ref _pendingSeekSinkFlush, 0);
        if (_playbackUsesWaveOutSink)
        {
            try { _audio?.Clear(); } catch { /* ignore */ }
            Volatile.Write(ref _deferWaveOutStart, 1);
            try { _audio?.Pause(); } catch { /* ignore */ }
            ApplyPlaybackVolume();
        }
    }

    private void OnVlcAudioDrain(IntPtr data)
    {
    }

    private void TryStartDeferredWaveOut()
    {
        if (Volatile.Read(ref _deferWaveOutStart) == 0)
            return;
        if (_audio is not { } sink)
            return;
        if (!sink.TryEnsurePlaybackStarted())
            return;
        Volatile.Write(ref _deferWaveOutStart, 0);
        ApplyPlaybackVolume();
    }

    /// <summary>If prebuffer never reaches the threshold (slow start), start WaveOut anyway so playback is not silent forever.</summary>
    private async Task EnsureWaveOutStartedFallbackAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            if (Volatile.Read(ref _deferWaveOutStart) == 0)
                return;

            await LibVlcHost.RunOnUiThreadAsync(() =>
            {
                if (Volatile.Read(ref _deferWaveOutStart) == 0)
                    return;
                if (_audio is not { } sink)
                    return;
                if (sink.IsPlaying)
                {
                    Volatile.Write(ref _deferWaveOutStart, 0);
                    return;
                }

                if (sink.TryEnsurePlaybackStarted(minBufferedSeconds: 0) || sink.TryPlay())
                {
                    Volatile.Write(ref _deferWaveOutStart, 0);
                    ApplyPlaybackVolume();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsResolvedLocalMediaPath(YoutubeStreamInput resolved)
    {
        try
        {
            var p = resolved.Url.Trim();
            return p.Length > 0 && File.Exists(p);
        }
        catch
        {
            return false;
        }
    }

    private static double EstimateBufferedSecondsFromPartialCache(PlaylistEntry entry, long bytes)
    {
        const double nominalKbps = 160.0;
        var est = bytes <= 0 ? 0.0 : bytes * 8.0 / (nominalKbps * 1000.0);
        if (entry.DurationSeconds is int d && d > 0)
            return Math.Min(d, est);
        return est;
    }

    private double EstimateBufferedSecondsFromPlayback()
    {
        var pos = _startOffsetSeconds;
        try
        {
            if (_positionSw.IsRunning)
                pos = _startOffsetSeconds + _positionSw.Elapsed.TotalSeconds;
        }
        catch { /* ignore */ }

        var ahead = _audio?.BufferedSeconds ?? 0;
        return Math.Max(0, pos + ahead + 0.5);
    }

    /// <summary>Disk-cache + decode extent for the orange bar (can exceed what HTTP in-place seek allows).</summary>
    private void UpdateSeekableBuffered(PlaylistEntry entry, YoutubeStreamInput resolved)
    {
        try
        {
            if (IsResolvedLocalMediaPath(resolved))
            {
                SeekableBufferedSeconds = 0;
                return;
            }

            var storeKey = YoutubeDiskCacheStoreKey(entry.VideoId);
            var bytes = _cache.TryGetPartialCacheBytes(storeKey);
            var completePath = _cache.TryGetCachedPath(storeKey);
            var estCache = !string.IsNullOrWhiteSpace(completePath) && File.Exists(completePath)
                ? (entry.DurationSeconds ?? EstimateBufferedSecondsFromPartialCache(entry, bytes))
                : EstimateBufferedSecondsFromPartialCache(entry, bytes);
            var estPlayback = string.Equals(entry.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase)
                ? EstimateBufferedSecondsFromPlayback()
                : 0;
            var est = Math.Max(estCache, estPlayback);

            if (entry.DurationSeconds is int d && d > 0)
                SeekableBufferedSeconds = Math.Min(d, est);
            else
                SeekableBufferedSeconds = est;
        }
        catch
        {
            SeekableBufferedSeconds = 0;
        }
    }

    /// <summary>Clamp user seek target before playback (UI + engine must agree on applied position).</summary>
    private double ClampSeekTargetSeconds(PlaylistEntry entry, double targetSeconds)
    {
        try
        {
            UpdateSeekableBuffered(entry, _lastResolvedForWarmup ?? new YoutubeStreamInput("", null));
        }
        catch { /* ignore */ }

        if (_lastResolvedForWarmup is { } r && IsResolvedLocalMediaPath(r))
            return targetSeconds;

        var max = MaxSeekSecondsForUi;
        if (max <= 0.25)
            return Math.Min(targetSeconds, Math.Max(0, CurrentPositionSeconds));

        return Math.Min(targetSeconds, max);
    }

    private double ClampInPlaceSeekOffset(PlaylistEntry entry, YoutubeStreamInput resolved, double startSeconds)
    {
        if (startSeconds <= 0.01)
            return 0;

        if (IsResolvedLocalMediaPath(resolved) || !IsLimitedSeekInput(resolved, entry))
            return startSeconds;

        UpdateSeekableBuffered(entry, resolved);
        return ClampToBufferedWindow(entry, resolved, startSeconds);
    }

    /// <summary>Clamp start offset for a full playback restart.</summary>
    private double ClampRestartStartOffset(PlaylistEntry entry, YoutubeStreamInput resolved, double startSeconds)
    {
        if (startSeconds <= 0.01)
            return 0;

        if (IsResolvedLocalMediaPath(resolved) || !IsLimitedSeekInput(resolved, entry))
        {
            if (entry.DurationSeconds is int d && d > 0)
                return Math.Min(startSeconds, Math.Max(0, d - 1));
            return startSeconds;
        }

        UpdateSeekableBuffered(entry, resolved);
        return ClampToBufferedWindow(entry, resolved, startSeconds);
    }

    private double ComputeLiveInPlaceSeekMaxSeconds(PlaylistEntry entry, YoutubeStreamInput resolved)
    {
        if (IsResolvedLocalMediaPath(resolved))
        {
            if (entry.DurationSeconds is int d && d > 0)
                return d;
            return SeekableBufferedSeconds;
        }

        if (!IsLimitedSeekInput(resolved, entry))
        {
            if (entry.DurationSeconds is int d && d > 0)
                return d;
            return SeekableBufferedSeconds;
        }

        if (IsRemoteHttpStreamInput(resolved))
            return Math.Max(0, EstimateBufferedSecondsFromPlayback() - HttpSeekEndMarginSeconds);

        return Math.Max(0, SeekableBufferedSeconds - PipeSeekEndMarginSeconds);
    }

    private double ComputeDiskBufferedSeekMaxSeconds(PlaylistEntry entry, YoutubeStreamInput resolved)
    {
        if (IsResolvedLocalMediaPath(resolved))
        {
            if (entry.DurationSeconds is int d && d > 0)
                return d;
            return SeekableBufferedSeconds;
        }

        if (!IsLimitedSeekInput(resolved, entry))
        {
            if (entry.DurationSeconds is int d && d > 0)
                return d;
            return SeekableBufferedSeconds;
        }

        var margin = IsRemoteHttpStreamInput(resolved) ? HttpSeekEndMarginSeconds : PipeSeekEndMarginSeconds;
        return Math.Max(0, SeekableBufferedSeconds - margin);
    }

    private double ClampToBufferedWindow(PlaylistEntry entry, YoutubeStreamInput resolved, double seconds)
    {
        var max = ComputeDiskBufferedSeekMaxSeconds(entry, resolved);
        if (max <= 0.25)
            return Math.Max(0, Math.Min(seconds, CurrentPositionSeconds));

        if (entry.DurationSeconds is int d && d > 0)
            seconds = Math.Min(seconds, Math.Max(0, d - 1));

        return Math.Max(0, Math.Min(seconds, max));
    }

    /// <summary>In-place seek when the target lies within the safe decode window (streams) or always (local files).</summary>
    private bool CanSeekInPlaceVlc(PlaylistEntry entry, YoutubeStreamInput? resolved, double targetSeconds)
    {
        if (_vlcMp is null || _playCts is null)
            return false;
        if (!string.Equals(entry.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase))
            return false;

        resolved ??= _lastResolvedForWarmup;
        if (resolved is null)
            return false;

        if (IsResolvedLocalMediaPath(resolved))
            return true;

        if (!IsLimitedSeekInput(resolved, entry))
            return true;

        UpdateSeekableBuffered(entry, resolved);
        return targetSeconds <= ComputeLiveInPlaceSeekMaxSeconds(entry, resolved) + 0.25;
    }

    private Media BuildVlcMedia(LibVLC lib, YoutubeStreamInput resolved)
    {
        if (resolved.DecodeViaYtdlpStdoutPipe)
            throw new InvalidOperationException("Pipe input uses Media(MediaInput) ctor.");

        var pathOrUrl = resolved.Url.Trim();
        if (File.Exists(pathOrUrl))
            return new Media(lib, pathOrUrl, FromType.FromPath);

        var media = new Media(lib, pathOrUrl, FromType.FromLocation);
        if (resolved.HttpHeaders is not null)
        {
            foreach (var kv in resolved.HttpHeaders)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                    continue;
                var k = kv.Key.Trim();
                var v = kv.Value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

                if (string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    media.AddOption(":http-user-agent=" + v);
                else if (string.Equals(k, "Referer", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(k, "Referrer", StringComparison.OrdinalIgnoreCase))
                    media.AddOption(":http-referrer=" + v);
                else if (string.Equals(k, "Cookie", StringComparison.OrdinalIgnoreCase))
                    media.AddOption(":http-cookie=" + v);
                else
                    media.AddOption($":http-header={k}: {v}");
            }
        }

        return media;
    }

    /// <summary>LibVLC <see cref="MediaPlayer"/> / <see cref="Media"/> must be created on the LibVLC STA thread.</summary>
    private bool StartVlcPlaybackOnUiThread(
        PlaylistEntry entry,
        YoutubeStreamInput resolvedInput,
        YtdlpPipeMediaInput? pipeInput,
        PlaybackTimingMark playbackTiming,
        CancellationTokenSource playbackSessionCts,
        bool raiseNowPlayingChanged)
    {
        try { AppLog.Info($"LibVLC: start on UI thread videoId={entry.VideoId} pipe={resolvedInput.DecodeViaYtdlpStdoutPipe}", AppLogInfoTier.Diagnostic); } catch { /* ignore */ }

        LibVlcHost.EnsureInitialized();
        var lib = LibVlcHost.LibVLC;

        try { _vlcMp?.Dispose(); } catch { /* ignore */ }
        _vlcMp = new MediaPlayer(lib);

        var useVlcAudioOutputSink = RequiresVlcWaveOutSink(resolvedInput);
        var feedAnalyzerFromMainVlc = useVlcAudioOutputSink;
        _playbackUsesWaveOutSink = useVlcAudioOutputSink;
        _vlcAudioCallbackGeneration = Volatile.Read(ref _playGeneration);

        if (feedAnalyzerFromMainVlc)
            EnsureVlcAudioCallbacksWired();

        _vlcEndHandled = false;
        _vlcFirstAudioRaised = false;
        Interlocked.Exchange(ref _vlcFirstAudioGate, 0);
        _vlcActiveVideoId = entry.VideoId;
        _lastResolvedForWarmup = resolvedInput;
        _lastRaiseNowPlayingForWarmup = raiseNowPlayingChanged;
        _vlcDecodeViaPipe = resolvedInput.DecodeViaYtdlpStdoutPipe;
        try { UpdateSeekableBuffered(entry, resolvedInput); } catch { /* ignore */ }

        if (_vlcEndReachedHandler is not null)
        {
            try { _vlcMp.EndReached -= _vlcEndReachedHandler; } catch { /* ignore */ }
            _vlcEndReachedHandler = null;
        }

        if (_vlcEncounteredErrorHandler is not null)
        {
            try { _vlcMp.EncounteredError -= _vlcEncounteredErrorHandler; } catch { /* ignore */ }
            _vlcEncounteredErrorHandler = null;
        }

        var vlcEpoch = Volatile.Read(ref _vlcPlaybackEpoch);
        var sessionPlayGen = Volatile.Read(ref _playGeneration);
        _vlcEndReachedHandler = new EventHandler<EventArgs>((_, _) =>
            VlcOnEndReached(entry, playbackSessionCts, vlcEpoch, sessionPlayGen));
        _vlcEncounteredErrorHandler = new EventHandler<EventArgs>((_, _) => VlcOnError(entry, playbackSessionCts));
        _vlcPlayingHandler = new EventHandler<EventArgs>((_, _) => VlcOnPlaying(entry, playbackSessionCts));
        _vlcPausedHandler = new EventHandler<EventArgs>((_, _) => VlcOnPaused(playbackSessionCts));
        _vlcStoppedHandler = new EventHandler<EventArgs>((_, _) => VlcOnStopped(playbackSessionCts));
        _vlcMp.EndReached += _vlcEndReachedHandler;
        _vlcMp.EncounteredError += _vlcEncounteredErrorHandler;
        _vlcMp.Playing += _vlcPlayingHandler;
        _vlcMp.Paused += _vlcPausedHandler;
        _vlcMp.Stopped += _vlcStoppedHandler;

        if (resolvedInput.DecodeViaYtdlpStdoutPipe)
        {
            _vlcPipeInput = pipeInput;
            if (_vlcPipeInput is null)
            {
                AbortPlaybackPipelineAfterFailure();
                return false;
            }

            _vlcMedia = new Media(lib, _vlcPipeInput, ":demux=any");
        }
        else
        {
            playbackTiming.Step("vlc_before_build_media");
            _vlcMedia = BuildVlcMedia(lib, resolvedInput);
            playbackTiming.Step("vlc_after_build_media");
        }

        lock (_vlcGate)
        {
            _vlcMp.Media = _vlcMedia;
            _vlcMp.Volume = useVlcAudioOutputSink ? 100 : (int)Math.Clamp(_volume * 100.0, 0, 100);
        }

        UpdateSeekableBuffered(entry, resolvedInput);

        if (useVlcAudioOutputSink)
        {
            try
            {
                var recreateSink = _audio is null
                                   || _waveOutSinkNormalizeEnabled != _audioNormalizeEnabled
                                   || _waveOutSinkDeviceNumber != _audioDeviceNumber;

                if (recreateSink)
                {
                    try { _audio?.Dispose(); } catch { /* ignore */ }
                    _audio = new AudioOut(
                        _format,
                        _audioDeviceNumber,
                        onSamplesRead: _analyzer.ProcessPcmF32LeStereo,
                        normalize: _audioNormalizeEnabled,
                        analyzeOnRead: true,
                        lowLatencyDeviceBuffer: true);
                    _waveOutSinkNormalizeEnabled = _audioNormalizeEnabled;
                    _waveOutSinkDeviceNumber = _audioDeviceNumber;
                }
                else
                {
                    try { _audio!.Stop(); } catch { /* ignore */ }
                    try { _audio!.Clear(); } catch { /* ignore */ }
                }

                ApplyPlaybackVolume();
            }
            catch (Exception ex)
            {
                LogPlaybackError(entry, $"Audio output init failed. {ex.Message}");
                RaiseError($"Audio output init failed. {ex.Message}");
                RaisePlaybackStateChanged(false);
                AbortPlaybackPipelineAfterFailure();
                return false;
            }

            Volatile.Write(ref _deferWaveOutStart, 1);
            // WaveOut starts after a short prebuffer in OnVlcAudioPlay (TryStartDeferredWaveOut).
        }
        else
        {
            try { _audio?.Dispose(); } catch { /* ignore */ }
            _audio = null;
            _waveOutSinkDeviceNumber = int.MinValue;
        }

        RaisePlaybackStateChanged(true);
        RaiseStatusChanged(entry, "BUFFERING", null);
        _pauseGate.Set();
        try { _positionSw.Reset(); } catch { /* ignore */ }

        playbackTiming.Step("vlc_before_play");
        bool ok;
        lock (_vlcGate) { ok = _vlcMp.Play(); }
        if (!ok)
        {
            LogPlaybackError(entry, "LibVLC failed to start playback.");
            RaiseError("LibVLC failed to start playback.");
            AbortPlaybackPipelineAfterFailure();
            return false;
        }

        return true;
    }

    private async Task<bool> PlayResolvedWithLibVlcAsync(
        PlaylistEntry entry,
        YoutubeStreamInput resolvedInput,
        PlaybackTimingMark playbackTiming,
        CancellationToken ct,
        CancellationTokenSource playbackSessionCts,
        bool raiseNowPlayingChanged)
    {
        await TeardownVlcBestEffortAsync(ct).ConfigureAwait(false);

        YtdlpPipeMediaInput? pipeInput = null;
        if (resolvedInput.DecodeViaYtdlpStdoutPipe)
        {
            playbackTiming.Step("vlc_before_ytdlp_pipe_input");
            pipeInput = await YtdlpPipeMediaInput.CreateWithClientProbeAsync(
                    resolvedInput.Url.Trim(),
                    _ytDlp.YtDlpPath,
                    psi => _ytDlp.ApplyLaunchPrefixTo(psi),
                    _ytDlp.AudioQualityFormat,
                    _ytDlp.UsesCookiesFromBrowser,
                    ct)
                .ConfigureAwait(false);
            playbackTiming.Step("vlc_after_ytdlp_pipe_input");
        }

        bool started;
        try
        {
            started = await LibVlcHost.RunOnUiThreadAsync(
                () => StartVlcPlaybackOnUiThread(
                    entry, resolvedInput, pipeInput, playbackTiming, playbackSessionCts, raiseNowPlayingChanged),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { pipeInput?.ForceStop(); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            try { pipeInput?.ForceStop(); } catch { /* ignore */ }
            try { AppLog.Warn($"PlayResolvedWithLibVlcAsync: UI start failed: {ex.Message}"); } catch { /* ignore */ }
            RaiseError("Playback failed to start on the UI thread.");
            AbortPlaybackPipelineAfterFailure();
            return false;
        }

        if (!started)
        {
            AbortPlaybackPipelineAfterFailure();
            return false;
        }

        playbackTiming.Step("vlc_after_play");

        if (RequiresVlcWaveOutSink(resolvedInput))
            _ = EnsureWaveOutStartedFallbackAsync(playbackSessionCts.Token);

        if (!RequiresVlcWaveOutSink(resolvedInput))
            await StartSecondaryAnalyzerTapAsync(entry, resolvedInput, ct).ConfigureAwait(false);

        if (_startOffsetSeconds > 0.01)
        {
            try { await ApplyVlcStartOffsetAfterPlayAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore */ }
        }

        // If we're using a secondary LibVLC tap for the visualizer, immediately resync it after seeking logic above.
        // This reduces the "late by ~1s" effect where the tap's internal buffering trails the audible playback.
        try
        {
            if (_startOffsetSeconds > 0.01 && Interlocked.CompareExchange(ref _visualizerResyncActive, 1, 1) == 1)
                _vlcVisualizerTap.Resync(Math.Max(0, CurrentPositionSeconds + 0.95));
        }
        catch { /* ignore */ }

        return true;
    }

    /// <summary>Secondary LibVLC decode for VU when the main player uses native LibVLC audio (no WaveOut sink).</summary>
    private async Task StartSecondaryAnalyzerTapAsync(PlaylistEntry entry, YoutubeStreamInput resolvedInput, CancellationToken ct)
    {
        _ = entry;
        try
        {
            try { _visualizerResyncTimer?.Dispose(); } catch { /* ignore */ }
            _visualizerResyncTimer = null;
            Interlocked.Exchange(ref _visualizerResyncActive, 0);

            var p = resolvedInput.Url.Trim();
            if (string.IsNullOrWhiteSpace(p))
                return;

            if (File.Exists(p))
            {
                try { _visualizerTap.StartFromLocalFile(p, _startOffsetSeconds); } catch { /* ignore */ }
                return;
            }

            if (resolvedInput.DecodeViaYtdlpStdoutPipe)
                return;

            await LibVlcHost.RunOnUiThreadAsync(
                () => _vlcVisualizerTap.Start(p, resolvedInput.HttpHeaders, _startOffsetSeconds),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore (best-effort visualizer)
        }
    }

    /// <summary>Seek on the live <see cref="MediaPlayer"/> without tearing down (fast, reliable repeat seeks).</summary>
    private async Task<bool> TrySeekInPlaceAsync(
        PlaylistEntry entry,
        double targetSeconds,
        bool resumePlayback,
        CancellationToken ct)
    {
        if (_playCts is null)
            return false;

        var resolved = _lastResolvedForWarmup;
        if (!CanSeekInPlaceVlc(entry, resolved, targetSeconds))
            return false;

        var playGen = Volatile.Read(ref _playGeneration);
        targetSeconds = resolved is not null
            ? ClampInPlaceSeekOffset(entry, resolved, targetSeconds)
            : Math.Max(0, targetSeconds);

        if (resolved is not null && IsLimitedSeekInput(resolved, entry))
        {
            UpdateSeekableBuffered(entry, resolved);
            targetSeconds = ClampToBufferedWindow(entry, resolved, targetSeconds);
        }

        var targetMs = (long)(targetSeconds * 1000.0);

        Volatile.Write(ref _awaitingVlcSeekSettle, 1);
        Volatile.Write(ref _pendingSeekSinkFlush, 1);

        try
        {
            if (_vlcDecodeViaPipe)
            {
                _startOffsetSeconds = targetSeconds;
                await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try
                    {
                        lock (_vlcGate)
                        {
                            if (_vlcMp is null)
                                return;
                            try { _vlcMp.Mute = true; } catch { /* ignore */ }
                            _vlcMp.Time = targetMs;
                        }
                    }
                    catch { /* ignore */ }
                }, ct).ConfigureAwait(false);

                await DiscardPipeStartOffsetAsync(ct).ConfigureAwait(false);

                await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try { lock (_vlcGate) { _vlcMp!.Mute = false; } } catch { /* ignore */ }
                }, ct).ConfigureAwait(false);

                ApplyPlaybackVolume();
                _startOffsetSeconds = targetSeconds;
            }
            else
            {
                var applied = await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try
                    {
                        lock (_vlcGate)
                        {
                            if (_vlcMp is null)
                                return false;

                            var state = _vlcMp.State;
                            if (state is not VLCState.Playing and not VLCState.Paused and not VLCState.Buffering)
                                return false;

                            _vlcMp.Time = targetMs;
                            return true;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }, ct).ConfigureAwait(false);

                if (!applied)
                    return false;

                var settledMs = await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try
                    {
                        lock (_vlcGate)
                            return _vlcMp is null ? -1L : _vlcMp.Time;
                    }
                    catch
                    {
                        return -1L;
                    }
                }, ct).ConfigureAwait(false);

                if (targetMs > 3000 &&
                    settledMs >= 0 &&
                    Math.Abs(settledMs - targetMs) > 4000)
                    return false;

                _startOffsetSeconds = targetSeconds;

                if (_playbackUsesWaveOutSink)
                {
                    try { _audio?.Clear(); } catch { /* ignore */ }
                    Volatile.Write(ref _deferWaveOutStart, 1);
                    if (!resumePlayback)
                    {
                        try { _audio?.Pause(); } catch { /* ignore */ }
                    }
                    else if (_audio is { } sink)
                    {
                        if (!sink.TryEnsurePlaybackStarted(minBufferedSeconds: 0.05))
                            Volatile.Write(ref _deferWaveOutStart, 1);
                        else
                            Volatile.Write(ref _deferWaveOutStart, 0);
                    }
                }
            }

            try { _positionSw.Restart(); } catch { /* ignore */ }
            if (resolved is null || !IsLimitedSeekInput(resolved, entry))
                try { _analyzer.Reset(); } catch { /* ignore */ }
            Interlocked.Exchange(ref _pendingSeekSinkFlush, 0);
            RestartTimelineClock();
            ApplyPlaybackVolume();
            return playGen == Volatile.Read(ref _playGeneration);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (playGen == Volatile.Read(ref _playGeneration))
                Volatile.Write(ref _awaitingVlcSeekSettle, 0);
        }
    }

    /// <summary>Seek LibVLC after playback starts; retries until the demuxer accepts the target (80 ms was too short for many streams).</summary>
    private async Task ApplyVlcStartOffsetAfterPlayAsync(CancellationToken ct)
    {
        var playGen = Volatile.Read(ref _playGeneration);
        try
        {
            if (_startOffsetSeconds <= 0.01)
                return;

            if (_vlcDecodeViaPipe)
            {
                await DiscardPipeStartOffsetAsync(ct).ConfigureAwait(false);
                try { _positionSw.Restart(); } catch { /* ignore */ }
                return;
            }

            // Never set Time on remote HTTP/DASH — native crashes; offset must use disk cache or pipe skip.
            if (_lastResolvedForWarmup is { } resolved && IsRemoteHttpStreamInput(resolved))
            {
                Interlocked.Exchange(ref _pendingSeekSinkFlush, 0);
                return;
            }

            var localTargetMs = (long)(_startOffsetSeconds * 1000.0);
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(4500);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (playGen != Volatile.Read(ref _playGeneration))
                    return;

                var applied = await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try
                    {
                        lock (_vlcGate)
                        {
                            if (_vlcMp is null)
                                return false;

                            var state = _vlcMp.State;
                            if (state is not VLCState.Playing and not VLCState.Paused and not VLCState.Buffering)
                                return false;

                            _vlcMp.Time = localTargetMs;
                            return true;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }, ct).ConfigureAwait(false);

                if (!applied)
                {
                    await Task.Delay(60, ct).ConfigureAwait(false);
                    continue;
                }

                try { _positionSw.Restart(); } catch { /* ignore */ }

                if (_playbackUsesWaveOutSink)
                {
                    try { _audio?.Clear(); } catch { /* ignore */ }
                    Volatile.Write(ref _deferWaveOutStart, 1);
                    try { _audio?.Pause(); } catch { /* ignore */ }
                }

                Interlocked.Exchange(ref _pendingSeekSinkFlush, 0);
                ApplyPlaybackVolume();
                return;
            }

            try
            {
                AppLog.Warn(
                    $"LibVLC: seek to {_startOffsetSeconds:0.###}s did not settle before timeout (videoId={_vlcActiveVideoId ?? "?"})");
            }
            catch { /* ignore */ }

            try { _positionSw.Restart(); } catch { /* ignore */ }
        }
        finally
        {
            if (playGen == Volatile.Read(ref _playGeneration))
                Volatile.Write(ref _awaitingVlcSeekSettle, 0);
        }
    }

    /// <summary>Fast-forward pipe decode by polling LibVLC time on the LibVLC thread without blocking playback workers for the whole timeout.</summary>
    private async Task DiscardPipeStartOffsetAsync(CancellationToken ct)
    {
        var target = Math.Min(_startOffsetSeconds, MaxPipeDiscardSeekSeconds);
        if (SeekableBufferedSeconds > 0.25)
            target = Math.Min(target, SeekableBufferedSeconds);
        var targetMs = (long)(target * 1000.0);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(PipeDiscardSeekTimeoutMs);

        await LibVlcHost.RunOnUiThreadAsync(() =>
        {
            try { lock (_vlcGate) { _vlcMp!.Mute = true; } } catch { /* ignore */ }
        }, ct).ConfigureAwait(false);

        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var done = await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try
                    {
                        lock (_vlcGate) { return _vlcMp!.Time + 250 >= targetMs; }
                    }
                    catch
                    {
                        return true;
                    }
                }, ct).ConfigureAwait(false);

                if (done)
                    break;

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await LibVlcHost.RunOnUiThreadAsync(() =>
                {
                    try { lock (_vlcGate) { _vlcMp!.Mute = false; } } catch { /* ignore */ }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }

            ApplyPlaybackVolume();
        }
    }

    private void VlcOnEndReached(
        PlaylistEntry entry,
        CancellationTokenSource playbackSessionCts,
        int vlcEpoch,
        int sessionPlayGen)
    {
        if (vlcEpoch != Volatile.Read(ref _vlcPlaybackEpoch))
            return;
        if (sessionPlayGen != Volatile.Read(ref _playGeneration))
            return;
        if (!ReferenceEquals(_playCts, playbackSessionCts))
            return;
        if (Volatile.Read(ref _awaitingVlcSeekSettle) != 0)
            return;
        if (_vlcEndHandled)
            return;
        _vlcEndHandled = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
                while (DateTime.UtcNow < drainDeadline &&
                       !playbackSessionCts.Token.IsCancellationRequested &&
                       ReferenceEquals(_playCts, playbackSessionCts))
                {
                    var sink = _audio;
                    if (sink is null || sink.BufferedSeconds <= 0.05)
                        break;
                    try
                    {
                        await Task.Delay(20, playbackSessionCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch { /* ignore */ }

            if (!ReferenceEquals(_playCts, playbackSessionCts))
                return;

            try { _positionSw.Stop(); } catch { /* ignore */ }
            try { _audio?.Stop(); } catch { /* ignore */ }
            if (ReferenceEquals(_playCts, playbackSessionCts))
                _playCts = null;

            var dur = entry.DurationSeconds;
            var endedEarly = false;
            if (!_vlcFirstAudioRaised)
            {
                var tail = _vlcPipeInput?.GetStderrTail(4000) ?? "";
                if (PlaybackFailureKindFromDiagnostics(tail, out var failMsg))
                {
                    TryMarkPrefetchSkipFromFailureMessage(entry.VideoId, failMsg);
                    RaisePlaybackFailed(entry, failMsg);
                    RaisePlaybackStateChanged(false);
                    return;
                }

                endedEarly = true;
            }
            else if (dur is int d && d > 0)
            {
                var pos = CurrentPositionSeconds;
                var slackSeconds = Math.Clamp((int)Math.Round(d * 0.08), 8, 45);
                if (pos > 0.5 && pos < Math.Max(0, d - slackSeconds))
                    endedEarly = true;
            }

            RaisePlaybackStateChanged(false);
            RaiseTrackEnded(entry, endedEarly);
        });
    }

    private void VlcOnError(PlaylistEntry entry, CancellationTokenSource playbackSessionCts)
    {
        if (!ReferenceEquals(_playCts, playbackSessionCts))
            return;

        try
        {
            var tail = _vlcPipeInput?.GetStderrTail(8000) ?? "";
            LogPlaybackError(entry, "LibVLC encountered an error.");
            RaiseError(string.IsNullOrWhiteSpace(tail) ? "LibVLC encountered an error." : tail);
        }
        catch { /* ignore */ }
    }

    private string? GetPlaybackDiagTail(int maxChars)
        => _vlcPipeInput?.GetStderrTail(maxChars);

    private void VlcOnPlaying(PlaylistEntry entry, CancellationTokenSource playbackSessionCts)
    {
        if (!ReferenceEquals(_playCts, playbackSessionCts))
            return;

        _vlcIsPlayingFlag = true;

        if (Interlocked.CompareExchange(ref _vlcFirstAudioGate, 1, 0) != 0)
            return;

        _vlcFirstAudioRaised = true;
        if (Volatile.Read(ref _awaitingVlcSeekSettle) == 0)
            try { _positionSw.Restart(); } catch { /* ignore */ }
        ApplyPlaybackVolume();
        try
        {
            if (GetCurrent() is { } cur && _vlcActiveVideoId is not null &&
                string.Equals(cur.VideoId, _vlcActiveVideoId, StringComparison.OrdinalIgnoreCase))
            {
                RaiseStatusChanged(cur, "PLAYING", null);
                _firstAudioForCurrentPlayTcs?.TrySetResult(true);
                RunDeferredWarmupsAfterFirstAudio(cur, _lastResolvedForWarmup, _lastRaiseNowPlayingForWarmup);
            }
        }
        catch { /* ignore */ }
    }

    private void VlcOnPaused(CancellationTokenSource playbackSessionCts)
    {
        if (!ReferenceEquals(_playCts, playbackSessionCts))
            return;
        _vlcIsPlayingFlag = false;
        try { _positionSw.Stop(); } catch { /* ignore */ }
        RaisePlaybackStateChanged(false);
    }

    private void VlcOnStopped(CancellationTokenSource playbackSessionCts)
    {
        if (!ReferenceEquals(_playCts, playbackSessionCts))
            return;
        _vlcIsPlayingFlag = false;
        try { _positionSw.Stop(); } catch { /* ignore */ }
        RaisePlaybackStateChanged(false);
    }
}
