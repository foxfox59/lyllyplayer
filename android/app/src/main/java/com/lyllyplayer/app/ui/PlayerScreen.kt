package com.lyllyplayer.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.lyllyplayer.app.playback.PlayerBridge
import com.lyllyplayer.app.playlist.EntryKind
import com.lyllyplayer.app.playlist.PlaylistEntry
import com.lyllyplayer.app.playorder.RepeatMode
import java.util.Locale
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

@Composable
fun PlayerScreen(
    bridge: PlayerBridge,
    onLoadPlaylist: () -> Unit,
    onQuit: () -> Unit,
) {
    val state by bridge.ui.collectAsStateWithLifecycle()
    var menuOpen by remember { mutableStateOf(false) }
    var optionsOpen by remember { mutableStateOf(false) }
    var openMode by remember {
        mutableStateOf(bridge.repository.getPlaylistOpenMode())
    }
    val listState = rememberLazyListState()
    val iconTint = MaterialTheme.colorScheme.onSurface

    LaunchedEffect(state.current?.id) {
        val idx = state.entries.indexOfFirst { it.id.equals(state.current?.id, ignoreCase = true) }
        if (idx < 0) return@LaunchedEffect
        val alreadyVisible = listState.layoutInfo.visibleItemsInfo.any { it.index == idx }
        if (!alreadyVisible) {
            listState.animateScrollToItem(idx)
        }
    }

    LaunchedEffect(Unit) {
        while (isActive) {
            bridge.syncPlaybackProgress()
            delay(250)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .statusBarsPadding(),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 4.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(
                        imageVector = Icons.Default.MoreVert,
                        contentDescription = "Menu",
                        tint = iconTint,
                    )
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text("Load playlist") },
                        onClick = {
                            menuOpen = false
                            onLoadPlaylist()
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("Options") },
                        onClick = {
                            menuOpen = false
                            openMode = bridge.repository.getPlaylistOpenMode()
                            optionsOpen = true
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("Quit") },
                        onClick = {
                            menuOpen = false
                            onQuit()
                        },
                    )
                }
            }

            Box(modifier = Modifier.weight(1f))

            IconButton(onClick = { bridge.previous() }) {
                Icon(
                    imageVector = Icons.Default.SkipPrevious,
                    contentDescription = "Previous",
                    tint = iconTint,
                )
            }
            IconButton(
                onClick = { bridge.playPause() },
                modifier = Modifier.size(52.dp),
            ) {
                Icon(
                    imageVector = if (state.isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                    contentDescription = if (state.isPlaying) "Pause" else "Play",
                    modifier = Modifier.size(36.dp),
                    tint = iconTint,
                )
            }
            IconButton(onClick = { bridge.next() }) {
                Icon(
                    imageVector = Icons.Default.SkipNext,
                    contentDescription = "Next",
                    tint = iconTint,
                )
            }

            Box(modifier = Modifier.weight(1f))

            IconButton(onClick = { bridge.setShuffle(!state.shuffle) }) {
                Icon(
                    imageVector = Icons.Default.Shuffle,
                    contentDescription = "Shuffle",
                    tint = if (state.shuffle) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        iconTint.copy(alpha = 0.45f)
                    },
                )
            }
            IconButton(onClick = { bridge.cycleRepeat() }) {
                Icon(
                    imageVector = if (state.repeat == RepeatMode.One) {
                        Icons.Default.RepeatOne
                    } else {
                        Icons.Default.Repeat
                    },
                    contentDescription = "Repeat",
                    tint = if (state.repeat != RepeatMode.Off) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        iconTint.copy(alpha = 0.45f)
                    },
                )
            }
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp),
        ) {
            val duration = state.durationMs.coerceAtLeast(0L)
            val position = state.positionMs.coerceIn(0L, if (duration > 0) duration else Long.MAX_VALUE)
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
                    if (duration > 0) bridge.seekTo((frac * duration).toLong())
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
                    text = state.current?.title ?: state.playlistName.ifBlank { "No playlist" },
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
            state.error?.let { err ->
                Text(
                    text = err,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
        }

        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 10.dp)
                .height(1.dp)
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.18f)),
        )

        Box(modifier = Modifier.fillMaxSize()) {
            LazyColumn(
                state = listState,
                modifier = Modifier
                    .fillMaxSize()
                    .padding(end = 14.dp),
                contentPadding = PaddingValues(bottom = 24.dp),
            ) {
                itemsIndexed(state.entries, key = { index, e -> "${e.id}#$index" }) { _, entry ->
                    PlaylistRow(
                        entry = entry,
                        selected = entry.id.equals(state.current?.id, ignoreCase = true),
                        onClick = { bridge.playEntry(entry) },
                    )
                }
            }
            PlaylistScrollbar(
                listState = listState,
                modifier = Modifier
                    .align(Alignment.CenterEnd)
                    .fillMaxHeight()
                    .width(20.dp),
            )
        }
    }

    if (optionsOpen) {
        OptionsDialog(
            openMode = openMode,
            onOpenModeChange = { mode ->
                openMode = mode
                bridge.repository.setPlaylistOpenMode(mode)
            },
            onDismiss = { optionsOpen = false },
        )
    }
}

@Composable
private fun PlaylistScrollbar(
    listState: LazyListState,
    modifier: Modifier = Modifier,
) {
    val info = listState.layoutInfo
    val total = info.totalItemsCount
    val visible = info.visibleItemsInfo
    if (total <= 0 || visible.isEmpty()) return
    if (!listState.canScrollForward && !listState.canScrollBackward) return

    val visibleCount = visible.size.coerceAtLeast(1)
    val thumbFraction = (visibleCount.toFloat() / total.toFloat()).coerceIn(0.12f, 1f)
    val maxIndex = (total - visibleCount).coerceAtLeast(1)
    val first = visible.first()
    // Include partial-item offset so the thumb can sit flush with the top.
    val scrollFraction = (
        (first.index.toFloat() - first.offset.toFloat() / first.size.coerceAtLeast(1).toFloat()) /
            maxIndex.toFloat()
        ).coerceIn(0f, 1f)
    val thumbColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.38f)
    val trackColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.10f)
    val scope = rememberCoroutineScope()

    fun scrollToFraction(fraction: Float) {
        val target = (maxIndex * fraction.coerceIn(0f, 1f)).roundToInt().coerceIn(0, total - 1)
        scope.launch {
            listState.scrollToItem(target)
        }
    }

    BoxWithConstraints(
        modifier = modifier
            .pointerInput(total, maxIndex) {
                detectTapGestures { offset ->
                    val h = size.height.coerceAtLeast(1)
                    scrollToFraction(offset.y / h.toFloat())
                }
            }
            .pointerInput(total, maxIndex) {
                detectVerticalDragGestures { change, _ ->
                    change.consume()
                    val h = size.height.coerceAtLeast(1)
                    scrollToFraction(change.position.y / h.toFloat())
                }
            },
    ) {
        val trackWidth = 3.dp
        val thumbHeight = maxHeight * thumbFraction
        val thumbOffset = (maxHeight - thumbHeight) * scrollFraction
        // Slim visual track, full height; wide modifier is the tap/drag hit target.
        Box(
            modifier = Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .width(trackWidth)
                .clip(RoundedCornerShape(50))
                .background(trackColor),
        )
        Box(
            modifier = Modifier
                .align(Alignment.TopEnd)
                .width(trackWidth)
                .height(thumbHeight)
                .offset(y = thumbOffset)
                .clip(RoundedCornerShape(50))
                .background(thumbColor),
        )
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
        // Map x so 0 and 1 sit at the track ends (thumb center clamped to [r, width-r]).
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
        // Full track
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(trackHeight)
                .clip(RoundedCornerShape(50))
                .background(inactive),
        )
        // Filled portion — ends at thumb center
        val fillFrac = progress.coerceIn(0f, 1f)
        Box(
            modifier = Modifier
                .fillMaxWidth(fillFrac)
                .height(trackHeight)
                .clip(RoundedCornerShape(50))
                .background(if (enabled) active else inactive),
        )
        // Thumb — center on progress point along full width
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

@Composable
private fun PlaylistRow(
    entry: PlaylistEntry,
    selected: Boolean,
    onClick: () -> Unit,
) {
    val bg = if (selected) {
        MaterialTheme.colorScheme.surfaceVariant
    } else {
        MaterialTheme.colorScheme.background
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(bg)
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = entry.title,
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            val subtitle = buildString {
                entry.channel?.takeIf { it.isNotBlank() }?.let { append(it) }
                val kindLabel = when (entry.kind) {
                    EntryKind.Youtube -> "YouTube"
                    EntryKind.Stream -> "Stream"
                    EntryKind.Local -> "Local"
                }
                if (isNotEmpty()) append(" · ")
                append(kindLabel)
            }
            Text(
                text = subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.65f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        entry.durationSeconds?.let { sec ->
            Box(modifier = Modifier.width(8.dp))
            Text(
                text = formatTime(sec * 1000L),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
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
