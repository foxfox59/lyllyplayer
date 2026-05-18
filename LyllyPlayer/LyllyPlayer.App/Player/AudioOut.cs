using NAudio;
using NAudio.Wave;
using System;

namespace LyllyPlayer.Player;

public sealed class AudioOut : IDisposable
{
    private readonly WaveOutEvent _output;
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveFormat _format;
    private readonly Action<byte[], int, int>? _onSamplesAdded;
    private readonly bool _analyzeOnRead;

    private sealed class AnalyzingWaveProvider(IWaveProvider source, Action<byte[], int, int> onSamplesRead) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0)
            {
                try { onSamplesRead(buffer, offset, read); } catch { /* ignore */ }
            }
            return read;
        }
    }

    /// <summary>Simple AGC/normalizer for float PCM. Keeps loudness steadier without pre-scanning.</summary>
    private sealed class NormalizingWaveProvider(IWaveProvider source) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        private const float TargetRms = 0.125f; // ~ -18 dBFS
        private const float MinGain = 0.35f;
        private const float MaxGain = 3.0f;
        private float _gain = 1.0f;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read <= 0)
                return read;

            if (WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || WaveFormat.BitsPerSample != 32)
                return read;

            var sampleCount = read / 4;
            if (sampleCount <= 0)
                return read;

            double sumSq = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var idx = offset + i * 4;
                var f = BitConverter.ToSingle(buffer, idx);
                sumSq += f * f;
            }

            var rms = (float)Math.Sqrt(sumSq / sampleCount);
            if (rms > 1e-6f)
            {
                var desired = Math.Clamp(TargetRms / rms, MinGain, MaxGain);
                // Limit upward steps so post-seek buffer drains do not spike perceived loudness.
                if (desired > _gain)
                    desired = Math.Min(desired, _gain * 1.35f);
                var attack = 0.25f;  // faster when reducing gain
                var release = 0.04f; // slower when increasing gain
                var a = desired < _gain ? attack : release;
                _gain = _gain + (desired - _gain) * a;
            }

            var g = _gain;
            for (var i = 0; i < sampleCount; i++)
            {
                var idx = offset + i * 4;
                var f = BitConverter.ToSingle(buffer, idx) * g;
                if (f > 1f) f = 1f;
                else if (f < -1f) f = -1f;
                var bytes = BitConverter.GetBytes(f);
                buffer[idx + 0] = bytes[0];
                buffer[idx + 1] = bytes[1];
                buffer[idx + 2] = bytes[2];
                buffer[idx + 3] = bytes[3];
            }

            return read;
        }
    }

    public AudioOut(
        WaveFormat format,
        int deviceNumber = -1,
        Action<byte[], int, int>? onSamplesRead = null,
        bool normalize = false,
        bool analyzeOnRead = false,
        bool lowLatencyDeviceBuffer = false)
    {
        _format = format;
        _onSamplesAdded = onSamplesRead;
        _analyzeOnRead = analyzeOnRead;
        // Absorb bursty network/pipe decode. Never discard *played* samples (causes crackle/skip); drop newest overflow instead.
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(3.0),
            DiscardOnBufferOverflow = false,
        };

        _output = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            // Balance VU sync vs underruns: moderate driver buffers when tapping on read.
            DesiredLatency = lowLatencyDeviceBuffer ? 100 : 150,
            NumberOfBuffers = lowLatencyDeviceBuffer ? 3 : 4,
        };

        IWaveProvider source = _buffer;
        if (normalize)
            source = new NormalizingWaveProvider(source);
        if (analyzeOnRead && onSamplesRead is not null)
            source = new AnalyzingWaveProvider(source, onSamplesRead);
        _output.Init(source);
    }

    public bool IsPlaying { get; private set; }
    public double BufferedSeconds
        => _format.AverageBytesPerSecond <= 0 ? 0 : (double)_buffer.BufferedBytes / _format.AverageBytesPerSecond;

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0f, 1f);
    }

    public void Clear()
    {
        _buffer.ClearBuffer();
    }

    /// <summary>Start WaveOut once enough PCM is queued (avoids initial underrun crackle).</summary>
    public bool TryEnsurePlaybackStarted(double minBufferedSeconds = 0.12)
    {
        if (IsPlaying)
            return true;
        if (BufferedSeconds + 1e-6 < minBufferedSeconds)
            return false;
        return TryPlay();
    }

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        try
        {
            if (count > 0)
                if (!_analyzeOnRead)
                    _onSamplesAdded?.Invoke(buffer, offset, count);
        }
        catch
        {
            // ignore (analysis is best-effort)
        }
        _buffer.AddSamples(buffer, offset, count);
    }

    /// <returns><see langword="false"/> when the wave device is gone or cannot start (e.g. unplugged USB headset).</returns>
    public bool TryPlay()
    {
        try
        {
            _output.Play();
            IsPlaying = true;
            return true;
        }
        catch (MmException)
        {
            IsPlaying = false;
            return false;
        }
    }

    public void Pause()
    {
        try
        {
            _output.Pause();
        }
        catch (MmException)
        {
            // Device removed while playing — treat as paused.
        }

        IsPlaying = false;
    }

    public void Stop()
    {
        try
        {
            _output.Stop();
        }
        catch (MmException)
        {
            // Device already gone.
        }

        IsPlaying = false;
        Clear();
    }

    public void Dispose()
    {
        try
        {
            _output.Dispose();
        }
        catch (MmException)
        {
            // Ignore — device handle may already be invalid.
        }
    }
}
