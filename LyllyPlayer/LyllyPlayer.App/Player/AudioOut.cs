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

    public AudioOut(WaveFormat format, int deviceNumber = -1, Action<byte[], int, int>? onSamplesRead = null)
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

        IWaveProvider source = onSamplesRead is not null
            ? new AnalyzingWaveProvider(_buffer, onSamplesRead)
            : _buffer;
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
