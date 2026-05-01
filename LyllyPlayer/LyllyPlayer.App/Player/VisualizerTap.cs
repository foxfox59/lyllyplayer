using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LyllyPlayer.Player;

/// <summary>
/// Background decoder that feeds <see cref="AudioAnalyzer"/> from a local/cached file without touching LibVLC audio callbacks.
/// Throttles to real-time so the UI visualizer doesn't "run ahead".
/// </summary>
public sealed class VisualizerTap : IDisposable
{
    private readonly AudioAnalyzer _analyzer;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);

    public VisualizerTap(AudioAnalyzer analyzer) => _analyzer = analyzer;

    public void SetPaused(bool paused)
    {
        try
        {
            if (paused) _pauseGate.Reset();
            else _pauseGate.Set();
        }
        catch { /* ignore */ }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
        _task = null;
    }

    public void StartFromLocalFile(string filePath)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(() => RunDecodeLoop(filePath, ct), ct);
    }

    private void RunDecodeLoop(string filePath, CancellationToken ct)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            using var reader = new MediaFoundationReader(filePath);

            // Convert to float samples.
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);

            if (sp.WaveFormat.SampleRate != 48000)
                sp = new WdlResamplingSampleProvider(sp, 48000);

            // Ensure 2ch @ 48kHz float.
            if (sp.WaveFormat.Channels != 2)
                return;

            // 20ms chunks @ 48k stereo.
            const int framesPerChunk = 960;
            var floatsPerChunk = framesPerChunk * 2;
            var floatBuf = new float[floatsPerChunk];
            var byteBuf = new byte[floatsPerChunk * sizeof(float)];

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long producedFrames = 0;

            while (!ct.IsCancellationRequested)
            {
                try { _pauseGate.Wait(ct); } catch { break; }

                var read = sp.Read(floatBuf, 0, floatsPerChunk);
                if (read <= 0)
                    break;

                // Convert float[] to little-endian bytes for analyzer (expects f32le stereo).
                Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, read * sizeof(float));
                _analyzer.ProcessPcmF32LeStereo(byteBuf, 0, read * sizeof(float));

                producedFrames += read / 2;

                // Throttle to real-time so the analyzer matches playback cadence.
                var targetMs = producedFrames * 1000.0 / 48000.0;
                var sleepMs = (int)Math.Floor(targetMs - sw.Elapsed.TotalMilliseconds);
                if (sleepMs > 0)
                {
                    try { Task.Delay(Math.Min(sleepMs, 25), ct).Wait(ct); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // best-effort: visualizer tap is optional
        }
    }

    public void Dispose() => Stop();
}

