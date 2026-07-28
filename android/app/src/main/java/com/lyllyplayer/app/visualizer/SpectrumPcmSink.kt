package com.lyllyplayer.app.visualizer

import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.audio.TeeAudioProcessor
import java.nio.ByteBuffer

/**
 * PCM tap for [TeeAudioProcessor]. Quiet when [enabled] is false.
 * Runs on ExoPlayer's audio thread — keep work light.
 */
@OptIn(UnstableApi::class)
class SpectrumPcmSink(
    private val analyzer: AudioAnalyzer,
) : TeeAudioProcessor.AudioBufferSink {
    @Volatile
    var enabled: Boolean = false

    @Volatile
    private var channelCount: Int = 2

    @Volatile
    private var encoding: Int = C.ENCODING_PCM_16BIT

    private val scratch = ByteArray(64 * 1024)

    override fun flush(sampleRateHz: Int, channelCount: Int, encoding: Int) {
        this.channelCount = channelCount.coerceAtLeast(1)
        this.encoding = encoding
        analyzer.setInputFormat(sampleRateHz)
        if (!enabled) analyzer.reset()
    }

    override fun handleBuffer(buffer: ByteBuffer) {
        if (!enabled) return
        val remaining = buffer.remaining()
        if (remaining <= 0) return
        // Buffer is read-only duplicate from TeeAudioProcessor — safe to consume.
        val bytes = if (remaining <= scratch.size) scratch else ByteArray(remaining)
        buffer.get(bytes, 0, remaining)
        when (encoding) {
            C.ENCODING_PCM_FLOAT -> analyzer.writePcmFloat(bytes, 0, remaining, channelCount)
            C.ENCODING_PCM_16BIT -> analyzer.writePcm16(bytes, 0, remaining, channelCount)
            else -> {
                if (remaining >= 2) analyzer.writePcm16(bytes, 0, remaining, channelCount)
            }
        }
    }
}
