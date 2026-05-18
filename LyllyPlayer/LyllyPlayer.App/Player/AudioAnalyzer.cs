using NAudio.Dsp;

namespace LyllyPlayer.Player;

public sealed class AudioAnalyzer
{
    /// <summary>Number of log-spaced spectrum buckets (must match visualizer).</summary>
    public const int SpectrumBands = 32;

    // Assumes f32le stereo at 48kHz (matches our ffmpeg args / WaveFormat)
    private const int SampleRate = 48000;
    private const int FftSize = 2048; // power of 2
    /// <summary>Lower edge of the first log band (Hz). Display/analyzer range 10…22050 (CD Nyquist); sub-bin energy still maps into low bands.</summary>
    public const double SpectrumFreqMinHz = 10.0;
    /// <summary>Upper edge of the top band (Hz). <b>22050</b> = 44100/2 (CD Nyquist); aligns with common “full spectrum” labeling.</summary>
    public const double SpectrumFreqMaxHz = 22050.0;

    private readonly object _vuGate = new();
    private readonly object _spectrumGate = new();

    // VU (0..1) — never block on spectrum FFT.
    private float _vuL;
    private float _vuR;

    // Spectrum
    private readonly float[] _ring = new float[FftSize * 4];
    private int _ringWrite;
    private int _ringCount;
    private readonly float[] _bands = new float[SpectrumBands];
    private float _specScale = 1f;
    private long _lastSpectrumComputeTick;

    public void Reset()
    {
        lock (_vuGate)
        {
            _vuL = 0;
            _vuR = 0;
        }

        lock (_spectrumGate)
        {
            Array.Clear(_ring);
            _ringWrite = 0;
            _ringCount = 0;
            Array.Clear(_bands);
            _specScale = 0f;
            _lastSpectrumComputeTick = 0;
        }
    }

    public void ProcessPcmF32LeStereo(byte[] buffer, int offset, int count)
    {
        // 8 bytes per frame (L f32 + R f32); f32le values are already in −1..1 range
        var end = offset + (count - (count % 8));
        if (end <= offset)
            return;

        for (var i = offset; i < end; i += 8)
        {
            var fl = BitConverter.ToSingle(buffer, i);
            var fr = BitConverter.ToSingle(buffer, i + 4);
            var afl = Math.Abs(fl);
            var afr = Math.Abs(fr);

            lock (_vuGate)
            {
                _vuL = SmoothLevel(_vuL, afl);
                _vuR = SmoothLevel(_vuR, afr);
            }

            lock (_spectrumGate)
                WriteRing((fl + fr) * 0.5f);
        }
    }

    public (float vuL, float vuR, float[] bands) GetSnapshot()
    {
        MaybeUpdateSpectrumThrottled();

        float vuL;
        float vuR;
        lock (_vuGate)
        {
            vuL = _vuL;
            vuR = _vuR;
        }

        var copy = new float[SpectrumBands];
        lock (_spectrumGate)
            Array.Copy(_bands, copy, SpectrumBands);
        return (vuL, vuR, copy);
    }

    private void MaybeUpdateSpectrumThrottled()
    {
        var now = Environment.TickCount64;
        if (now - _lastSpectrumComputeTick < 33)
            return;

        lock (_spectrumGate)
        {
            if (_ringCount < FftSize)
                return;
            _lastSpectrumComputeTick = now;
            ComputeSpectrum();
        }
    }

    private static float SmoothLevel(float current, float target)
    {
        const float attack = 0.88f;
        const float release = 0.48f;
        var k = target > current ? attack : release;
        return current + (target - current) * k;
    }

    private void WriteRing(float sample)
    {
        _ring[_ringWrite] = sample;
        _ringWrite = (_ringWrite + 1) % _ring.Length;
        if (_ringCount < _ring.Length) _ringCount++;
    }

    /// <summary>
    /// Lower / upper edge of spectrum band <paramref name="band"/> in Hz (same grid as <see cref="ComputeSpectrum"/>).
    /// Edges follow a <b>geometric progression</b> in Hz — i.e. <b>equal width on a log-frequency axis</b>
    /// (same Δlog f per band), which is how typical log graphic EQs / RTA-style analyzers partition the spectrum — not equal Hz width.
    /// </summary>
    public static void GetSpectrumBandEdgesHz(int band, out double f0Hz, out double f1Hz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(band, 0, nameof(band));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(band, SpectrumBands, nameof(band));

        f0Hz = SpectrumFreqMinHz * Math.Pow(SpectrumFreqMaxHz / SpectrumFreqMinHz, (double)band / SpectrumBands);
        f1Hz = SpectrumFreqMinHz * Math.Pow(SpectrumFreqMaxHz / SpectrumFreqMinHz, (double)(band + 1) / SpectrumBands);
    }

    public static double GetSpectrumBandCenterHz(int band)
    {
        GetSpectrumBandEdgesHz(band, out var f0, out var f1);
        return Math.Sqrt(f0 * f1);
    }

    /// <summary>
    /// Raw FFT energy is a poor match for “how loud it looks” in the subs: hearing is much less sensitive there at
    /// typical listening levels. Down-weight low centers so the graph isn’t dominated by bass you barely perceive.
    /// </summary>
    private static float PerceptualVisibilityWeightHz(float fcHz)
    {
        if (fcHz >= 500f)
            return 1f;
        var lo = MathF.Log10(28f);
        var hi = MathF.Log10(500f);
        var x = MathF.Log10(Math.Clamp(fcHz, 28f, 500f));
        var t = (x - lo) / (hi - lo);
        t = Math.Clamp(t, 0f, 1f);
        const float wLo = 0.34f;
        return wLo + (1f - wLo) * t;
    }

    private void ComputeSpectrum()
    {
        // Pull the latest FftSize samples from ring (mono)
        var temp = new Complex[FftSize];
        var start = (_ringWrite - FftSize + _ring.Length) % _ring.Length;

        for (var i = 0; i < FftSize; i++)
        {
            var s = _ring[(start + i) % _ring.Length];

            // Hann window
            var w = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (FftSize - 1));
            temp[i].X = s * w;
            temp[i].Y = 0;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), temp);

        // Convert to magnitudes and bucket into bands (log-ish)
        var mags = new float[FftSize / 2];
        for (var i = 1; i < mags.Length; i++)
        {
            var re = temp[i].X;
            var im = temp[i].Y;
            mags[i] = (float)Math.Sqrt(re * re + im * im);
        }

        // Log-spaced bands 10 Hz…22050 Hz (32 buckets; PCM here is 48 kHz but display matches common CD Nyquist labeling).
        var binDfHz = (float)SampleRate / FftSize;
        var raw = new float[SpectrumBands];
        var frameMax = 1e-6f;
        for (var b = 0; b < SpectrumBands; b++)
        {
            GetSpectrumBandEdgesHz(b, out var f0, out var f1);

            var i0 = (int)Math.Clamp(f0 * FftSize / SampleRate, 1, mags.Length - 1);
            var i1 = (int)Math.Clamp(f1 * FftSize / SampleRate, i0 + 1, mags.Length);

            var n = i1 - i0;
            var sumSq = 0f;
            for (var i = i0; i < i1; i++)
            {
                var m = mags[i];
                sumSq += m * m;
            }

            var rms = (float)Math.Sqrt(sumSq / Math.Max(1, n));

            var bandHz = (float)Math.Max(f1 - f0, 1e-3);
            var narrowBoost = MathF.Sqrt(binDfHz / MathF.Max(bandHz, 0.45f * binDfHz));
            if (narrowBoost < 1f) narrowBoost = 1f;
            if (narrowBoost > 3.4f) narrowBoost = 3.4f;

            var boosted = rms * narrowBoost * 220f;
            var v = (float)Math.Sqrt(boosted);

            raw[b] = v;
            if (v > frameMax) frameMax = v;
        }

        var maxR = frameMax;
        var sorted = new float[SpectrumBands];
        Array.Copy(raw, sorted, SpectrumBands);
        Array.Sort(sorted);
        var p82 = sorted[(int)MathF.Floor(0.82f * (SpectrumBands - 1))];
        var frameRef = MathF.Min(maxR, MathF.Max(p82 * 1.12f, maxR * 0.88f));
        frameRef = MathF.Max(frameRef, 1e-6f);

        const float decay = 0.96f;
        const float minScaleRelativeToPeak = 0.02f;
        const float absScaleFloor = 1e-6f;
        _specScale = Math.Max(frameRef, _specScale * decay);
        var minScale = Math.Max(absScaleFloor, frameRef * minScaleRelativeToPeak);
        if (_specScale < minScale) _specScale = minScale;

        for (var b = 0; b < SpectrumBands; b++)
        {
            var fc = (float)GetSpectrumBandCenterHz(b);
            var w = PerceptualVisibilityWeightHz(fc);
            _bands[b] = Math.Clamp(raw[b] / _specScale * w, 0, 1);
        }
    }
}
