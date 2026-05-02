using NAudio;
using NAudio.Wave;
using System;

namespace LyllyPlayer.Player;

public sealed class AudioOut : IDisposable
{
    private readonly WaveOutEvent _output;
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveFormat _format;

    /// <summary>Invokes <paramref name="onRead"/> when WaveOut pulls PCM (post-buffer), for visualizers.</summary>
    private sealed class AnalyzingWaveProvider(IWaveProvider source, Action<byte[], int, int>? onRead) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0)
                onRead?.Invoke(buffer, offset, read);
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

    public AudioOut(WaveFormat format, int deviceNumber = -1, Action<byte[], int, int>? onSamplesRead = null, bool normalize = false)
    {
        _format = format;
        // Larger buffer than default 1 s: network decode is bursty; too small + aggressive WaveOut latency causes
        // constant underruns. Keep in sync with PlaybackEngine reader throttle (must stay below this duration).
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
        };

        _output = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            DesiredLatency = 120,
            NumberOfBuffers = 3,
        };

        IWaveProvider source = _buffer;
        if (normalize)
            source = new NormalizingWaveProvider(source);
        if (onSamplesRead is not null)
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

    public void AddSamples(byte[] buffer, int offset, int count)
    {
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
