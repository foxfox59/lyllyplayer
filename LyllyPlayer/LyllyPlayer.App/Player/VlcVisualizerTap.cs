using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace LyllyPlayer.Player;

/// <summary>
/// Tap-only LibVLC decoder for stream URLs: decodes audio into callbacks and feeds <see cref="AudioAnalyzer"/>.
/// Uses dummy audio output so it doesn't compete with the main player.
/// </summary>
public sealed class VlcVisualizerTap : IDisposable
{
    private readonly AudioAnalyzer _analyzer;
    private readonly object _gate = new();
    private MediaPlayer? _mp;
    private Media? _media;

    // Keep delegates rooted.
    private MediaPlayer.LibVLCAudioPlayCb? _playCb;
    private MediaPlayer.LibVLCAudioPauseCb? _pauseCb;
    private MediaPlayer.LibVLCAudioResumeCb? _resumeCb;
    private MediaPlayer.LibVLCAudioFlushCb? _flushCb;
    private MediaPlayer.LibVLCAudioDrainCb? _drainCb;

    public VlcVisualizerTap(AudioAnalyzer analyzer) => _analyzer = analyzer;

    public void Start(string url, IReadOnlyDictionary<string, string>? httpHeaders)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(url))
            return;

        LibVlcHost.EnsureInitialized();
        var lib = LibVlcHost.LibVLC;

        lock (_gate)
        {
            _mp = new MediaPlayer(lib);

            // Wire callbacks.
            _playCb = OnAudioPlay;
            _pauseCb = (_, __) => { };
            _resumeCb = (_, __) => { };
            _flushCb = (_, __) => { };
            _drainCb = _ => { };

            _mp.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);
            _mp.SetAudioFormat("S16N", 48000, 2);

            // Build media (location only; this is for stream URLs).
            if (File.Exists(url))
                _media = new Media(lib, url, FromType.FromPath);
            else
                _media = new Media(lib, url, FromType.FromLocation);

            // Prevent any real audio output; we only want decode callbacks.
            _media.AddOption(":no-video");
            _media.AddOption(":aout=dummy");

            if (httpHeaders is not null)
            {
                foreach (var kv in httpHeaders)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                        continue;
                    var k = kv.Key.Trim();
                    var v = kv.Value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

                    if (string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        _media.AddOption(":http-user-agent=" + v);
                    else if (string.Equals(k, "Referer", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(k, "Referrer", StringComparison.OrdinalIgnoreCase))
                        _media.AddOption(":http-referrer=" + v);
                    else if (string.Equals(k, "Cookie", StringComparison.OrdinalIgnoreCase))
                        _media.AddOption(":http-cookie=" + v);
                    else
                        _media.AddOption($":http-header={k}: {v}");
                }
            }

            _mp.Media = _media;
            try { _mp.Mute = true; } catch { /* ignore */ }
            try { _mp.Volume = 0; } catch { /* ignore */ }
            try { _mp.Play(); } catch { /* ignore */ }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            try { _mp?.SetPause(paused); } catch { /* ignore */ }
        }
    }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        // "count" is frames per channel. Format = S16N stereo @ 48k.
        if (samples == IntPtr.Zero || count == 0)
            return;

        try
        {
            var frames = (int)Math.Min(count, 48000); // cap at 1s
            var shorts = frames * 2;
            var bytes = shorts * sizeof(short);

            var rentedS16 = ArrayPool<short>.Shared.Rent(shorts);
            var rentedF32 = ArrayPool<byte>.Shared.Rent(frames * 2 * sizeof(float));
            try
            {
                Marshal.Copy(samples, rentedS16, 0, shorts);

                // Convert s16 -> f32le
                // (Write bytes directly to match AudioAnalyzer contract)
                var o = 0;
                for (var i = 0; i < shorts; i++)
                {
                    var f = rentedS16[i] / 32768f;
                    var b = BitConverter.GetBytes(f);
                    rentedF32[o++] = b[0];
                    rentedF32[o++] = b[1];
                    rentedF32[o++] = b[2];
                    rentedF32[o++] = b[3];
                }

                _analyzer.ProcessPcmF32LeStereo(rentedF32, 0, shorts * sizeof(float));
            }
            finally
            {
                try { ArrayPool<short>.Shared.Return(rentedS16); } catch { /* ignore */ }
                try { ArrayPool<byte>.Shared.Return(rentedF32); } catch { /* ignore */ }
            }
        }
        catch
        {
            // best-effort
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _mp?.Stop(); } catch { /* ignore */ }
            try { _mp?.Dispose(); } catch { /* ignore */ }
            _mp = null;

            try { _media?.Dispose(); } catch { /* ignore */ }
            _media = null;

            _playCb = null;
            _pauseCb = null;
            _resumeCb = null;
            _flushCb = null;
            _drainCb = null;
        }
    }

    public void Dispose() => Stop();
}

