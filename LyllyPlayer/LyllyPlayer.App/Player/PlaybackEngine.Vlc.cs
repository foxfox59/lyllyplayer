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
    private bool _enableVlcAudioCallbacks;
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

    /// <summary>Upper bound for user seeks (UI + <see cref="SeekAsync"/>): full duration for local/direct; min(duration, buffered) for cookie-pipe until the disk cache is complete.</summary>
    public double MaxSeekSecondsForUi =>
        !_vlcDecodeViaPipe
            ? (_currentDurationSeconds is int d0 && d0 > 0 ? d0 : Math.Max(0, SeekableBufferedSeconds))
            : (_currentDurationSeconds is int d && d > 0
                ? Math.Min(d, Math.Max(0, SeekableBufferedSeconds))
                : Math.Max(0, SeekableBufferedSeconds));

    /// <summary>Re-reads partial disk cache size so <see cref="SeekableBufferedSeconds"/> grows while yt-dlp caches in the background.</summary>
    public void RefreshSeekableBufferedFromCache()
    {
        try
        {
            if (GetCurrent() is not { } cur || _lastResolvedForWarmup is null)
                return;
            UpdateSeekableBuffered(cur, _lastResolvedForWarmup);
        }
        catch
        {
            // ignore
        }
    }

    private void TeardownVlcBestEffort()
    {
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

            var sink = _audio;
            if (sink is null)
                return;

            // Never block LibVLC's audio thread. If our output buffer is too full (e.g. on seeks / pauses),
            // drop frames instead of sleeping, or LibVLC may stall or crash in native code.
            if (sink.BufferedSeconds <= 2.0)
                sink.AddSamples(rentedF32, 0, floatBytes);
            else
                return;

            if (Interlocked.CompareExchange(ref _vlcFirstAudioGate, 1, 0) != 0)
                return;

            _vlcFirstAudioRaised = true;
            try { _positionSw.Restart(); } catch { /* ignore */ }
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
        try { _audio?.Clear(); } catch { /* ignore */ }
    }

    private void OnVlcAudioDrain(IntPtr data)
    {
    }

    private void UpdateSeekableBuffered(PlaylistEntry entry, YoutubeStreamInput resolved)
    {
        try
        {
            if (!resolved.DecodeViaYtdlpStdoutPipe)
            {
                // Avoid querying LibVLC Length (can crash on some systems when polled concurrently).
                SeekableBufferedSeconds = entry.DurationSeconds ?? 0;
                return;
            }

            var storeKey = YoutubeDiskCacheStoreKey(entry.VideoId);
            if (!string.IsNullOrWhiteSpace(_cache.TryGetCachedPath(storeKey)))
            {
                SeekableBufferedSeconds = entry.DurationSeconds ?? 0;
                return;
            }

            var bytes = _cache.TryGetPartialCacheBytes(storeKey);
            const double nominalKbps = 160.0;
            var est = bytes <= 0 ? 0.0 : bytes * 8.0 / (nominalKbps * 1000.0);
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

    private async Task<bool> PlayResolvedWithLibVlcAsync(
        PlaylistEntry entry,
        YoutubeStreamInput resolvedInput,
        PlaybackTimingMark playbackTiming,
        CancellationToken ct,
        CancellationTokenSource playbackSessionCts,
        bool raiseNowPlayingChanged)
    {
        TeardownVlcBestEffort();

        try { AppLog.Warn($"LibVLC: ensure init videoId={entry.VideoId} pipe={resolvedInput.DecodeViaYtdlpStdoutPipe}"); } catch { /* ignore */ }
        LibVlcHost.EnsureInitialized();
        var lib = LibVlcHost.LibVLC;

        // Recreate MediaPlayer per session to avoid any stuck native audio callback state.
        try { _vlcMp?.Dispose(); } catch { /* ignore */ }
        _vlcMp = new MediaPlayer(lib);
        if (_enableVlcAudioCallbacks)
        {
            try { AppLog.Warn($"LibVLC: wiring audio callbacks videoId={entry.VideoId}"); } catch { /* ignore */ }
            EnsureVlcAudioCallbacksWired();
        }

        _vlcEndHandled = false;
        _vlcFirstAudioRaised = false;
        Interlocked.Exchange(ref _vlcFirstAudioGate, 0);
        _vlcActiveVideoId = entry.VideoId;
        _lastResolvedForWarmup = resolvedInput;
        _lastRaiseNowPlayingForWarmup = raiseNowPlayingChanged;
        _vlcDecodeViaPipe = resolvedInput.DecodeViaYtdlpStdoutPipe;

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

        _vlcEndReachedHandler = new EventHandler<EventArgs>((_, _) => VlcOnEndReached(entry, playbackSessionCts));
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
            playbackTiming.Step("vlc_before_ytdlp_pipe_input");
            _vlcPipeInput = await YtdlpPipeMediaInput.CreateWithClientProbeAsync(
                    resolvedInput.Url.Trim(),
                    _ytDlp.YtDlpPath,
                    psi => _ytDlp.ApplyLaunchPrefixTo(psi),
                    _ytDlp.AudioQualityFormat,
                    _ytDlp.UsesCookiesFromBrowser,
                    ct)
                .ConfigureAwait(false);
            playbackTiming.Step("vlc_after_ytdlp_pipe_input");

            _vlcMedia = new Media(lib, _vlcPipeInput, ":demux=any");
        }
        else
        {
            playbackTiming.Step("vlc_before_build_media");
            _vlcMedia = BuildVlcMedia(lib, resolvedInput);
            playbackTiming.Step("vlc_after_build_media");
        }

        try { AppLog.Warn($"LibVLC: assigning Media videoId={entry.VideoId}"); } catch { /* ignore */ }
        lock (_vlcGate)
        {
            _vlcMp.Media = _vlcMedia;
        }
        try { AppLog.Warn($"LibVLC: Media assigned videoId={entry.VideoId}"); } catch { /* ignore */ }
        lock (_vlcGate)
        {
            _vlcMp.Volume = _enableVlcAudioCallbacks ? 100 : (int)Math.Clamp(_volume * 100.0, 0, 100);
        }
        try { AppLog.Warn($"LibVLC: volume set videoId={entry.VideoId} vol={_vlcMp.Volume}"); } catch { /* ignore */ }

        UpdateSeekableBuffered(entry, resolvedInput);
        try { AppLog.Warn($"LibVLC: seekableBuffered updated videoId={entry.VideoId} buf={SeekableBufferedSeconds:0.###}"); } catch { /* ignore */ }

        if (_enableVlcAudioCallbacks && _audio is null)
        {
            try
            {
                try { AppLog.Warn($"AudioOut: creating sink videoId={entry.VideoId} dev={_audioDeviceNumber}"); } catch { /* ignore */ }
                _audio = new AudioOut(
                    _format,
                    _audioDeviceNumber,
                    onSamplesRead: _analyzer.ProcessPcmF32LeStereo,
                    normalize: _audioNormalizeEnabled,
                    analyzeOnRead: true);
                _audio.Volume = _volume;
                try { AppLog.Warn($"AudioOut: sink created videoId={entry.VideoId}"); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                LogPlaybackError(entry, $"Audio output init failed. {ex.Message}");
                Error?.Invoke(this, $"Audio output init failed. {ex.Message}");
                RaisePlaybackStateChanged(false);
                AbortPlaybackPipelineAfterFailure();
                return false;
            }
        }

        if (_enableVlcAudioCallbacks)
        {
            _audio?.Stop();
            try { AppLog.Warn($"AudioOut: stopped (pre-play) videoId={entry.VideoId}"); } catch { /* ignore */ }
            if (_audio is null || (!_audio.TryPlay() && !TryRecoverAudioOutput(leaveStoppedSinkOnTotalFailure: false)))
            {
                LogPlaybackError(entry, "Audio output failed to start (device may be disconnected).");
                Error?.Invoke(this, "Audio output failed to start. Check that an audio device is connected.");
                RaisePlaybackStateChanged(false);
                AbortPlaybackPipelineAfterFailure();
                return false;
            }
            try { AppLog.Warn($"AudioOut: TryPlay ok videoId={entry.VideoId}"); } catch { /* ignore */ }
        }

        RaisePlaybackStateChanged(true);
        RaiseStatusChanged(entry, "BUFFERING", null);
        _pauseGate.Set();
        try { _positionSw.Reset(); } catch { /* ignore */ }

        playbackTiming.Step("vlc_before_play");
        try { AppLog.Warn($"LibVLC: calling Play() videoId={entry.VideoId}"); } catch { /* ignore */ }
        bool ok;
        lock (_vlcGate) { ok = _vlcMp.Play(); }
        if (!ok)
        {
            LogPlaybackError(entry, "LibVLC failed to start playback.");
            Error?.Invoke(this, "LibVLC failed to start playback.");
            AbortPlaybackPipelineAfterFailure();
            return false;
        }

        playbackTiming.Step("vlc_after_play");

        // Visualizer: when LibVLC audio callbacks are disabled (for stability), feed AudioAnalyzer from a local/cached file decode tap.
        // Works for local playlist items and for YouTube items only when we are playing from a fully cached file (disk-cache hit).
        if (!_enableVlcAudioCallbacks)
        {
            try
            {
                var p = resolvedInput.Url;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    _visualizerTap.StartFromLocalFile(p, _startOffsetSeconds);
                else if (!resolvedInput.DecodeViaYtdlpStdoutPipe)
                {
                    _vlcVisualizerTap.Start(resolvedInput.Url, resolvedInput.HttpHeaders, _startOffsetSeconds);
                    // Keep the tap locked to the playback clock; YouTube buffering can otherwise drift early/late.
                    try
                    {
                        if (Interlocked.Exchange(ref _visualizerResyncActive, 1) == 0)
                        {
                            const double resyncThresholdSeconds = 0.25; // avoid constant seeking (can make tap lag)
                            _visualizerResyncTimer = new System.Threading.Timer(_ =>
                            {
                                try
                                {
                                    if (Interlocked.CompareExchange(ref _visualizerResyncActive, 1, 1) != 1)
                                        return;
                                    // Apply a small lead only when we started/seeking from a non-zero offset.
                                    // For normal playback from the beginning, any lead makes the visualizer feel "ahead".
                                    var lead = _startOffsetSeconds > 0.01 ? 0.95 : 0.0;
                                    var desired = Math.Max(0, CurrentPositionSeconds + lead);
                                    var curTap = _vlcVisualizerTap.GetTimeSecondsBestEffort();
                                    if (Math.Abs(curTap - desired) >= resyncThresholdSeconds)
                                        _vlcVisualizerTap.Resync(desired);
                                }
                                catch { /* ignore */ }
                            }, null, dueTime: 250, period: 250);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        if (!_vlcDecodeViaPipe && _startOffsetSeconds > 0.01)
        {
            try
            {
                await Task.Delay(80, ct).ConfigureAwait(false);
                lock (_vlcGate) { _vlcMp.Time = (long)(_startOffsetSeconds * 1000.0); }
            }
            catch { /* ignore */ }
        }

        if (_vlcDecodeViaPipe && _startOffsetSeconds > 0.01)
        {
            var target = Math.Min(_startOffsetSeconds, MaxPipeDiscardSeekSeconds);
            try
            {
                try { lock (_vlcGate) { _vlcMp.Mute = true; } } catch { /* ignore */ }
                var targetMs = (long)(target * 1000.0);
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(PipeDiscardSeekTimeoutMs);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    long tms;
                    lock (_vlcGate) { tms = _vlcMp.Time; }
                    if (tms + 250 >= targetMs)
                        break;
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            finally
            {
                try { lock (_vlcGate) { _vlcMp.Mute = false; } } catch { /* ignore */ }
            }
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

    private void VlcOnEndReached(PlaylistEntry entry, CancellationTokenSource playbackSessionCts)
    {
        if (!ReferenceEquals(_playCts, playbackSessionCts))
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
                    PlaybackFailed?.Invoke(this, (entry, failMsg));
                    PlaybackStateChanged?.Invoke(this, false);
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

            PlaybackStateChanged?.Invoke(this, false);
            TrackEnded?.Invoke(this, (entry, endedEarly));
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
            Error?.Invoke(this, string.IsNullOrWhiteSpace(tail) ? "LibVLC encountered an error." : tail);
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

        // When audio callbacks are disabled, approximate "first audio" with LibVLC entering Playing state.
        if (_enableVlcAudioCallbacks)
            return;

        if (Interlocked.CompareExchange(ref _vlcFirstAudioGate, 1, 0) != 0)
            return;

        _vlcFirstAudioRaised = true;
        try { _positionSw.Restart(); } catch { /* ignore */ }
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
