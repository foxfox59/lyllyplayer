package com.lyllyplayer.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.lyllyplayer.app.playback.PlayerBridge
import com.lyllyplayer.app.playlist.PlaylistEntry
import com.lyllyplayer.app.playorder.RepeatMode
import java.util.Locale
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.isActive

/** Slow-changing UI; kept separate from seek ticks so the playlist does not recompose. */
private data class PlaylistUiSnapshot(
    val entries: List<PlaylistEntry>,
    val currentId: String?,
    val playlistName: String,
    val shuffle: Boolean,
    val repeat: RepeatMode,
    val spectrumEnabled: Boolean,
    val error: String?,
)

private data class ProgressUiSnapshot(
    val positionMs: Long,
    val durationMs: Long,
    val isPlaying: Boolean,
)

@Composable
fun PlayerScreen(
    bridge: PlayerBridge,
    onLoadPlaylist: () -> Unit,
    onQuit: () -> Unit,
) {
    val playlist by remember {
        bridge.ui
            .map {
                PlaylistUiSnapshot(
                    entries = it.entries,
                    currentId = it.current?.id,
                    playlistName = it.playlistName,
                    shuffle = it.shuffle,
                    repeat = it.repeat,
                    spectrumEnabled = it.spectrumEnabled,
                    error = it.error,
                )
            }
            .distinctUntilChanged()
    }.collectAsStateWithLifecycle(
        initialValue = bridge.ui.value.let {
            PlaylistUiSnapshot(
                entries = it.entries,
                currentId = it.current?.id,
                playlistName = it.playlistName,
                shuffle = it.shuffle,
                repeat = it.repeat,
                spectrumEnabled = it.spectrumEnabled,
                error = it.error,
            )
        },
    )

    var menuOpen by remember { mutableStateOf(false) }
    var optionsOpen by remember { mutableStateOf(false) }
    var openMode by remember {
        mutableStateOf(bridge.repository.getPlaylistOpenMode())
    }
    var listScrolling by remember { mutableStateOf(false) }
    val iconTint = MaterialTheme.colorScheme.onSurface
    val currentTitle = remember(playlist.currentId, playlist.entries, playlist.playlistName) {
        playlist.entries
            .firstOrNull { it.id.equals(playlist.currentId, ignoreCase = true) }
            ?.title
            ?: playlist.playlistName.ifBlank { "No playlist" }
    }

    LaunchedEffect(listScrolling) {
        while (isActive) {
            if (!listScrolling) {
                bridge.syncPlaybackProgress()
            }
            delay(if (listScrolling) 400 else 250)
        }
    }

    // Free CPU on the audio/FFT path while the user is flinging the playlist.
    LaunchedEffect(listScrolling, playlist.spectrumEnabled) {
        bridge.spectrumSink.enabled = playlist.spectrumEnabled && !listScrolling
    }

    val onPlayEntry = remember(bridge) { { entry: PlaylistEntry -> bridge.playEntry(entry) } }
    val onScrollActive = remember {
        { active: Boolean -> listScrolling = active }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .statusBarsPadding(),
    ) {
        // Collects isPlaying locally so position ticks don't rebuild this bar's parent scope.
        TransportBarHost(
            bridge = bridge,
            shuffle = playlist.shuffle,
            repeat = playlist.repeat,
            iconTint = iconTint,
            menuOpen = menuOpen,
            onMenuOpenChange = { menuOpen = it },
            onLoadPlaylist = onLoadPlaylist,
            onOpenOptions = {
                openMode = bridge.repository.getPlaylistOpenMode()
                optionsOpen = true
            },
            onQuit = onQuit,
        )

        val spectrumModifier = remember {
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 2.dp)
        }
        if (playlist.spectrumEnabled) {
            SpectrumVisualizer(
                analyzer = bridge.audioAnalyzer,
                enabled = true,
                active = !listScrolling,
                modifier = spectrumModifier,
            )
        }

        ProgressSectionHost(
            bridge = bridge,
            title = currentTitle,
            error = playlist.error,
        )

        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 10.dp)
                .height(1.dp)
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.18f)),
        )

        PlaylistRecycler(
            entries = playlist.entries,
            currentId = playlist.currentId,
            onPlayEntry = onPlayEntry,
            onScrollActiveChanged = onScrollActive,
            modifier = Modifier.fillMaxSize(),
        )
    }

    if (optionsOpen) {
        OptionsDialog(
            openMode = openMode,
            onOpenModeChange = { mode ->
                openMode = mode
                bridge.repository.setPlaylistOpenMode(mode)
            },
            spectrumEnabled = playlist.spectrumEnabled,
            onSpectrumEnabledChange = { enabled ->
                bridge.setSpectrumEnabled(enabled)
            },
            onDismiss = { optionsOpen = false },
        )
    }
}

@Composable
private fun TransportBarHost(
    bridge: PlayerBridge,
    shuffle: Boolean,
    repeat: RepeatMode,
    iconTint: Color,
    menuOpen: Boolean,
    onMenuOpenChange: (Boolean) -> Unit,
    onLoadPlaylist: () -> Unit,
    onOpenOptions: () -> Unit,
    onQuit: () -> Unit,
) {
    val isPlaying by remember {
        bridge.ui.map { it.isPlaying }.distinctUntilChanged()
    }.collectAsStateWithLifecycle(initialValue = bridge.ui.value.isPlaying)

    TransportBar(
        isPlaying = isPlaying,
        shuffle = shuffle,
        repeat = repeat,
        iconTint = iconTint,
        menuOpen = menuOpen,
        onMenuOpenChange = onMenuOpenChange,
        onLoadPlaylist = onLoadPlaylist,
        onOpenOptions = onOpenOptions,
        onQuit = onQuit,
        onPrevious = remember(bridge) { { bridge.previous() } },
        onPlayPause = remember(bridge) { { bridge.playPause() } },
        onNext = remember(bridge) { { bridge.next() } },
        onToggleShuffle = remember(bridge, shuffle) { { bridge.setShuffle(!shuffle) } },
        onCycleRepeat = remember(bridge) { { bridge.cycleRepeat() } },
    )
}

@Composable
private fun ProgressSectionHost(
    bridge: PlayerBridge,
    title: String,
    error: String?,
) {
    val progress by remember {
        bridge.ui
            .map { ProgressUiSnapshot(it.positionMs, it.durationMs, it.isPlaying) }
            .distinctUntilChanged()
    }.collectAsStateWithLifecycle(
        initialValue = bridge.ui.value.let {
            ProgressUiSnapshot(it.positionMs, it.durationMs, it.isPlaying)
        },
    )
    ProgressSection(
        positionMs = progress.positionMs,
        durationMs = progress.durationMs,
        title = title,
        error = error,
        onSeek = remember(bridge) { { ms: Long -> bridge.seekTo(ms) } },
    )
}

@Composable
private fun TransportBar(
    isPlaying: Boolean,
    shuffle: Boolean,
    repeat: RepeatMode,
    iconTint: Color,
    menuOpen: Boolean,
    onMenuOpenChange: (Boolean) -> Unit,
    onLoadPlaylist: () -> Unit,
    onOpenOptions: () -> Unit,
    onQuit: () -> Unit,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 4.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box {
            IconButton(onClick = { onMenuOpenChange(true) }) {
                Icon(
                    imageVector = Icons.Default.MoreVert,
                    contentDescription = "Menu",
                    tint = iconTint,
                )
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { onMenuOpenChange(false) }) {
                DropdownMenuItem(
                    text = { Text("Load playlist") },
                    onClick = {
                        onMenuOpenChange(false)
                        onLoadPlaylist()
                    },
                )
                DropdownMenuItem(
                    text = { Text("Options") },
                    onClick = {
                        onMenuOpenChange(false)
                        onOpenOptions()
                    },
                )
                DropdownMenuItem(
                    text = { Text("Quit") },
                    onClick = {
                        onMenuOpenChange(false)
                        onQuit()
                    },
                )
            }
        }

        Box(modifier = Modifier.weight(1f))

        IconButton(onClick = onPrevious) {
            Icon(
                imageVector = Icons.Default.SkipPrevious,
                contentDescription = "Previous",
                tint = iconTint,
            )
        }
        IconButton(
            onClick = onPlayPause,
            modifier = Modifier.size(52.dp),
        ) {
            Icon(
                imageVector = if (isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                contentDescription = if (isPlaying) "Pause" else "Play",
                modifier = Modifier.size(36.dp),
                tint = iconTint,
            )
        }
        IconButton(onClick = onNext) {
            Icon(
                imageVector = Icons.Default.SkipNext,
                contentDescription = "Next",
                tint = iconTint,
            )
        }

        Box(modifier = Modifier.weight(1f))

        IconButton(onClick = onToggleShuffle) {
            Icon(
                imageVector = Icons.Default.Shuffle,
                contentDescription = "Shuffle",
                tint = if (shuffle) {
                    MaterialTheme.colorScheme.primary
                } else {
                    iconTint.copy(alpha = 0.45f)
                },
            )
        }
        IconButton(onClick = onCycleRepeat) {
            Icon(
                imageVector = if (repeat == RepeatMode.One) {
                    Icons.Default.RepeatOne
                } else {
                    Icons.Default.Repeat
                },
                contentDescription = "Repeat",
                tint = if (repeat != RepeatMode.Off) {
                    MaterialTheme.colorScheme.primary
                } else {
                    iconTint.copy(alpha = 0.45f)
                },
            )
        }
    }
}

@Composable
private fun ProgressSection(
    positionMs: Long,
    durationMs: Long,
    title: String,
    error: String?,
    onSeek: (Long) -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp),
    ) {
        val duration = durationMs.coerceAtLeast(0L)
        val position = positionMs.coerceIn(0L, if (duration > 0) duration else Long.MAX_VALUE)
        val progress = if (duration > 0) {
            (position.toFloat() / duration.toFloat()).coerceIn(0f, 1f)
        } else {
            0f
        }
        val onSurface = MaterialTheme.colorScheme.onSurface

        SeekBar(
            progress = progress,
            enabled = duration > 0,
            onSeek = { frac ->
                if (duration > 0) onSeek((frac * duration).toLong())
            },
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 4.dp),
        )

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(
                text = formatTime(position),
                style = MaterialTheme.typography.labelSmall,
                color = onSurface,
            )
            Text(
                text = title,
                style = MaterialTheme.typography.labelSmall,
                color = onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier
                    .weight(1f)
                    .padding(horizontal = 8.dp),
            )
            Text(
                text = if (duration > 0) formatTime(duration) else "--:--",
                style = MaterialTheme.typography.labelSmall,
                color = onSurface,
            )
        }
        error?.let { err ->
            Text(
                text = err,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
    }
}

/**
 * Simple seek control without Material3 Slider's thumb halo / track inset quirks.
 * Thumb center tracks the progress edge of the bar.
 */
@Composable
private fun SeekBar(
    progress: Float,
    onSeek: (Float) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val active = MaterialTheme.colorScheme.primary
    val inactive = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.22f)
    val thumbRadius = 7.dp
    val trackHeight = 3.dp
    val density = LocalDensity.current
    var widthPx by remember { mutableFloatStateOf(0f) }
    val thumbRpx = with(density) { thumbRadius.toPx() }

    fun fractionFromX(x: Float): Float {
        if (widthPx <= 0f) return 0f
        val usable = (widthPx - 2f * thumbRpx).coerceAtLeast(1f)
        return ((x - thumbRpx) / usable).coerceIn(0f, 1f)
    }

    Box(
        modifier = modifier
            .height(28.dp)
            .onSizeChanged { widthPx = it.width.toFloat() }
            .pointerInput(enabled, widthPx) {
                if (!enabled) return@pointerInput
                detectTapGestures { offset ->
                    onSeek(fractionFromX(offset.x))
                }
            }
            .pointerInput(enabled, widthPx) {
                if (!enabled) return@pointerInput
                detectHorizontalDragGestures { change, _ ->
                    change.consume()
                    onSeek(fractionFromX(change.position.x))
                }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(trackHeight)
                .clip(RoundedCornerShape(50))
                .background(inactive),
        )
        val fillFrac = progress.coerceIn(0f, 1f)
        Box(
            modifier = Modifier
                .fillMaxWidth(fillFrac)
                .height(trackHeight)
                .clip(RoundedCornerShape(50))
                .background(if (enabled) active else inactive),
        )
        if (widthPx > 0f) {
            val usable = (widthPx - 2f * thumbRpx).coerceAtLeast(1f)
            val cx = thumbRpx + fillFrac * usable
            Box(
                modifier = Modifier
                    .offset { IntOffset((cx - thumbRpx).roundToInt(), 0) }
                    .size(thumbRadius * 2)
                    .background(if (enabled) active else inactive, CircleShape),
            )
        }
    }
}

private fun formatTime(ms: Long): String {
    if (ms < 0) return "0:00"
    val totalSec = TimeUnit.MILLISECONDS.toSeconds(ms)
    val m = totalSec / 60
    val s = totalSec % 60
    return String.format(Locale.US, "%d:%02d", m, s)
}
