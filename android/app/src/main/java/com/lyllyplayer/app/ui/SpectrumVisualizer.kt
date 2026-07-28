package com.lyllyplayer.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Fill
import androidx.compose.ui.unit.dp
import com.lyllyplayer.app.visualizer.AudioAnalyzer
import kotlin.math.ln
import kotlin.math.max
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

@Composable
fun SpectrumVisualizer(
    analyzer: AudioAnalyzer,
    enabled: Boolean,
    modifier: Modifier = Modifier,
    /** When false, keep last frame but stop polling (e.g. while playlist scrolls). */
    active: Boolean = true,
) {
    if (!enabled) return

    var bands by remember { mutableStateOf(FloatArray(AudioAnalyzer.SPECTRUM_BANDS)) }
    val fillColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.55f)
    val gridColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.10f)

    LaunchedEffect(enabled, active, analyzer) {
        while (isActive && enabled && active) {
            bands = analyzer.snapshotBands()
            delay(33)
        }
    }

    Canvas(
        modifier = modifier
            .fillMaxWidth()
            .height(28.dp),
    ) {
        val w = size.width
        val h = size.height
        if (w <= 0f || h <= 0f) return@Canvas

        // Light horizontal rules (like desktop spectrum chrome).
        for (i in 1..3) {
            val y = h * i / 4f
            drawLine(color = gridColor, start = Offset(0f, y), end = Offset(w, y), strokeWidth = 1f)
        }

        val count = bands.size.coerceAtMost(AudioAnalyzer.SPECTRUM_BANDS)
        if (count < 2) return@Canvas

        val logLo = ln(AudioAnalyzer.SPECTRUM_FREQ_MIN_HZ)
        val (_, bandMax) = AudioAnalyzer.spectrumBandEdgesHz(count - 1)
        val logDen = max(ln(bandMax) - logLo, 1e-12)

        fun xAtHz(fHz: Double): Float {
            val x = w * ((ln(fHz) - logLo) / logDen).toFloat()
            return x.coerceIn(0f, w)
        }

        val path = Path()
        path.moveTo(0f, h)
        for (i in 0 until count) {
            val (f0, f1) = AudioAnalyzer.spectrumBandEdgesHz(i)
            val xm = (xAtHz(f0) + xAtHz(f1)) * 0.5f
            val v = bands[i].coerceIn(0f, 1f)
            val y = h - v * h
            if (i == 0) path.lineTo(xm, y) else path.lineTo(xm, y)
        }
        path.lineTo(w, h)
        path.close()
        drawPath(path = path, color = fillColor, style = Fill)
    }
}
