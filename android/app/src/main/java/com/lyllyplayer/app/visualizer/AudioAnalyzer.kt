package com.lyllyplayer.app.visualizer

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.floor
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Desktop-aligned spectrum analyzer: 2048 FFT → [SpectrumBands] log-spaced buckets.
 * Feed mono (or mid) PCM floats in −1..1 via [writeSample] / [writeStereoFloat] / [writePcm16].
 */
class AudioAnalyzer {
    private val spectrumGate = Any()
    private val ring = FloatArray(FFT_SIZE * 4)
    private var ringWrite = 0
    private var ringCount = 0
    private val bands = FloatArray(SPECTRUM_BANDS)
    private var specScale = 1f
    private var lastSpectrumComputeTick = 0L
    @Volatile private var sampleRateHz: Int = 48_000

    // Reused FFT scratch (only touched under spectrumGate).
    private val fftRe = FloatArray(FFT_SIZE)
    private val fftIm = FloatArray(FFT_SIZE)
    private val mags = FloatArray(FFT_SIZE / 2)
    private val raw = FloatArray(SPECTRUM_BANDS)
    private val sorted = FloatArray(SPECTRUM_BANDS)

    fun setInputFormat(sampleRateHz: Int) {
        if (sampleRateHz > 0) this.sampleRateHz = sampleRateHz
    }

    fun reset() {
        synchronized(spectrumGate) {
            ring.fill(0f)
            ringWrite = 0
            ringCount = 0
            bands.fill(0f)
            specScale = 0f
            lastSpectrumComputeTick = 0L
        }
    }

    fun writeSample(mono: Float) {
        synchronized(spectrumGate) {
            ring[ringWrite] = mono
            ringWrite = (ringWrite + 1) % ring.size
            if (ringCount < ring.size) ringCount++
        }
    }

    fun writeStereoFloat(left: Float, right: Float) {
        writeSample((left + right) * 0.5f)
    }

    /** Little-endian PCM 16-bit interleaved. */
    fun writePcm16(buffer: ByteArray, offset: Int, length: Int, channelCount: Int) {
        if (channelCount <= 0 || length < 2) return
        val frameBytes = 2 * channelCount
        val end = offset + (length - (length % frameBytes))
        var i = offset
        while (i < end) {
            if (channelCount == 1) {
                val s = pcm16At(buffer, i) / 32768f
                writeSample(s)
            } else {
                val l = pcm16At(buffer, i) / 32768f
                val r = pcm16At(buffer, i + 2) / 32768f
                writeStereoFloat(l, r)
            }
            i += frameBytes
        }
    }

    /** Little-endian float32 interleaved. */
    fun writePcmFloat(buffer: ByteArray, offset: Int, length: Int, channelCount: Int) {
        if (channelCount <= 0 || length < 4) return
        val frameBytes = 4 * channelCount
        val end = offset + (length - (length % frameBytes))
        var i = offset
        while (i < end) {
            if (channelCount == 1) {
                writeSample(floatAt(buffer, i))
            } else {
                writeStereoFloat(floatAt(buffer, i), floatAt(buffer, i + 4))
            }
            i += frameBytes
        }
    }

    /** Copy of current bands (0..1). Throttled FFT ~30 Hz. */
    fun snapshotBands(): FloatArray {
        maybeUpdateSpectrumThrottled()
        val copy = FloatArray(SPECTRUM_BANDS)
        synchronized(spectrumGate) {
            System.arraycopy(bands, 0, copy, 0, SPECTRUM_BANDS)
        }
        return copy
    }

    private fun maybeUpdateSpectrumThrottled() {
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastSpectrumComputeTick < 33L) return
        synchronized(spectrumGate) {
            if (ringCount < FFT_SIZE) return
            lastSpectrumComputeTick = now
            computeSpectrum()
        }
    }

    private fun computeSpectrum() {
        val start = (ringWrite - FFT_SIZE + ring.size) % ring.size
        for (i in 0 until FFT_SIZE) {
            val s = ring[(start + i) % ring.size]
            val w = (0.5 - 0.5 * cos(2.0 * PI * i / (FFT_SIZE - 1))).toFloat()
            fftRe[i] = s * w
            fftIm[i] = 0f
        }

        fft(fftRe, fftIm)

        for (i in 1 until mags.size) {
            val re = fftRe[i]
            val im = fftIm[i]
            mags[i] = sqrt(re * re + im * im)
        }

        val rate = sampleRateHz.coerceAtLeast(8_000)
        val binDfHz = rate.toFloat() / FFT_SIZE
        var frameMax = 1e-6f
        for (b in 0 until SPECTRUM_BANDS) {
            val (f0, f1) = spectrumBandEdgesHz(b)
            val i0 = ((f0 * FFT_SIZE / rate).toInt()).coerceIn(1, mags.size - 1)
            val i1 = ((f1 * FFT_SIZE / rate).toInt()).coerceIn(i0 + 1, mags.size)
            var sumSq = 0f
            for (i in i0 until i1) {
                val m = mags[i]
                sumSq += m * m
            }
            val n = (i1 - i0).coerceAtLeast(1)
            val rms = sqrt(sumSq / n)
            val bandHz = max((f1 - f0).toFloat(), 1e-3f)
            var narrowBoost = sqrt(binDfHz / max(bandHz, 0.45f * binDfHz))
            narrowBoost = narrowBoost.coerceIn(1f, 3.4f)
            val v = sqrt(rms * narrowBoost * 220f)
            raw[b] = v
            if (v > frameMax) frameMax = v
        }

        System.arraycopy(raw, 0, sorted, 0, SPECTRUM_BANDS)
        sorted.sort()
        val p82 = sorted[floor(0.82f * (SPECTRUM_BANDS - 1)).toInt()]
        var frameRef = min(frameMax, max(p82 * 1.12f, frameMax * 0.88f))
        frameRef = max(frameRef, 1e-6f)

        specScale = max(frameRef, specScale * 0.96f)
        val minScale = max(1e-6f, frameRef * 0.02f)
        if (specScale < minScale) specScale = minScale

        for (b in 0 until SPECTRUM_BANDS) {
            val fc = spectrumBandCenterHz(b).toFloat()
            val w = perceptualVisibilityWeightHz(fc)
            bands[b] = (raw[b] / specScale * w).coerceIn(0f, 1f)
        }
    }

    companion object {
        const val SPECTRUM_BANDS = 32
        const val SPECTRUM_FREQ_MIN_HZ = 10.0
        const val SPECTRUM_FREQ_MAX_HZ = 22050.0
        private const val FFT_SIZE = 2048

        fun spectrumBandEdgesHz(band: Int): Pair<Double, Double> {
            require(band in 0 until SPECTRUM_BANDS)
            val f0 = SPECTRUM_FREQ_MIN_HZ *
                Math.pow(SPECTRUM_FREQ_MAX_HZ / SPECTRUM_FREQ_MIN_HZ, band.toDouble() / SPECTRUM_BANDS)
            val f1 = SPECTRUM_FREQ_MIN_HZ *
                Math.pow(SPECTRUM_FREQ_MAX_HZ / SPECTRUM_FREQ_MIN_HZ, (band + 1).toDouble() / SPECTRUM_BANDS)
            return f0 to f1
        }

        fun spectrumBandCenterHz(band: Int): Double {
            val (f0, f1) = spectrumBandEdgesHz(band)
            return sqrt(f0 * f1)
        }

        private fun perceptualVisibilityWeightHz(fcHz: Float): Float {
            if (fcHz >= 500f) return 1f
            val lo = log10(28f)
            val hi = log10(500f)
            val x = log10(fcHz.coerceIn(28f, 500f))
            val t = ((x - lo) / (hi - lo)).coerceIn(0f, 1f)
            val wLo = 0.34f
            return wLo + (1f - wLo) * t
        }

        private fun pcm16At(buf: ByteArray, i: Int): Short {
            val lo = buf[i].toInt() and 0xff
            val hi = buf[i + 1].toInt()
            return ((hi shl 8) or lo).toShort()
        }

        private fun floatAt(buf: ByteArray, i: Int): Float {
            val bits = (buf[i].toInt() and 0xff) or
                ((buf[i + 1].toInt() and 0xff) shl 8) or
                ((buf[i + 2].toInt() and 0xff) shl 16) or
                ((buf[i + 3].toInt() and 0xff) shl 24)
            return Float.fromBits(bits)
        }

        /** In-place radix-2 Cooley–Tukey FFT. */
        private fun fft(re: FloatArray, im: FloatArray) {
            val n = re.size
            var j = 0
            for (i in 1 until n) {
                var bit = n shr 1
                while (j and bit != 0) {
                    j = j xor bit
                    bit = bit shr 1
                }
                j = j xor bit
                if (i < j) {
                    val tr = re[i]; re[i] = re[j]; re[j] = tr
                    val ti = im[i]; im[i] = im[j]; im[j] = ti
                }
            }
            var len = 2
            while (len <= n) {
                val ang = -2.0 * PI / len
                val wlenRe = cos(ang).toFloat()
                val wlenIm = sin(ang).toFloat()
                var i0 = 0
                while (i0 < n) {
                    var wRe = 1f
                    var wIm = 0f
                    for (k in 0 until len / 2) {
                        val i = i0 + k
                        val j2 = i + len / 2
                        val tRe = wRe * re[j2] - wIm * im[j2]
                        val tIm = wRe * im[j2] + wIm * re[j2]
                        re[j2] = re[i] - tRe
                        im[j2] = im[i] - tIm
                        re[i] += tRe
                        im[i] += tIm
                        val nextWRe = wRe * wlenRe - wIm * wlenIm
                        wIm = wRe * wlenIm + wIm * wlenRe
                        wRe = nextWRe
                    }
                    i0 += len
                }
                len = len shl 1
            }
        }
    }
}
