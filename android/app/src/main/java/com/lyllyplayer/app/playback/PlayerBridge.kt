package com.lyllyplayer.app.playback

import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import com.lyllyplayer.app.playlist.PlaylistEntry
import com.lyllyplayer.app.playlist.PlaylistRepository
import com.lyllyplayer.app.playorder.NextTrackResolver
import com.lyllyplayer.app.playorder.PlayOrderService
import com.lyllyplayer.app.playorder.RepeatMode
import com.lyllyplayer.app.youtube.YoutubeStreamResolver
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class PlayRequest(
    val entry: PlaylistEntry,
    val startPositionMs: Long = 0L,
    val playWhenReady: Boolean = true,
)

/**
 * Shared playback / playlist state between UI and [PlaybackService].
 */
class PlayerBridge(
    val repository: PlaylistRepository,
) {
    val playOrder = PlayOrderService()
    val resolver = NextTrackResolver(playOrder)
    val youtube = YoutubeStreamResolver()

    private val _ui = MutableStateFlow(PlayerUiState())
    val ui: StateFlow<PlayerUiState> = _ui.asStateFlow()

    var entries: List<PlaylistEntry>
        get() = _ui.value.entries
        private set(value) {
            _ui.update { it.copy(entries = value) }
        }

    val currentEntry: PlaylistEntry?
        get() = _ui.value.current

    val currentIndex: Int
        get() {
            val cur = currentEntry ?: return -1
            return entries.indexOfFirst { it.id.equals(cur.id, ignoreCase = true) }
        }

    val isPlaying: Boolean
        get() = _ui.value.isPlaying

    private var player: ExoPlayer? = null
    private var positionListener: Player.Listener? = null
    private var lastPersistedAtMs: Long = 0L

    var onPlayEntryRequest: ((PlayRequest) -> Unit)? = null
    var onPlayPauseRequest: (() -> Unit)? = null
    var onSeekRequest: ((Long) -> Unit)? = null
    var onNextRequest: (() -> Unit)? = null
    var onPreviousRequest: (() -> Unit)? = null
    var onStopServiceRequest: (() -> Unit)? = null

    /** Starts [PlaybackService] when UI actions arrive while handlers are detached. */
    var onEnsureService: (() -> Unit)? = null

    val isPlaybackConnected: Boolean
        get() = onPlayEntryRequest != null

    @Volatile
    private var pendingPlay: PlayRequest? = null

    private enum class PendingTransport { Next, Previous }

    @Volatile
    private var pendingTransport: PendingTransport? = null

    fun attachPlayer(exo: ExoPlayer) {
        player = exo
        val listener = object : Player.Listener {
            override fun onEvents(player: Player, events: Player.Events) {
                syncPlaybackProgress()
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                syncPlaybackProgress()
                persistSession(force = !isPlaying)
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                syncPlaybackProgress()
            }
        }
        positionListener = listener
        exo.addListener(listener)
        syncPlaybackProgress()
    }

    /** Pull position/duration from ExoPlayer into UI state (call while playing). */
    fun syncPlaybackProgress() {
        val p = player ?: return
        // After stop/clearMediaItems the player reports 0 — don't wipe a saved seek.
        if (p.mediaItemCount == 0 || p.playbackState == Player.STATE_IDLE) {
            _ui.update { it.copy(isPlaying = false) }
            return
        }
        val rawDuration = p.duration
        val durationMs = if (rawDuration > 0L) rawDuration else 0L
        val reported = p.currentPosition.coerceAtLeast(0L).let { pos ->
            if (durationMs > 0L) pos.coerceAtMost(durationMs) else pos
        }
        val held = _ui.value.positionMs
        // While buffering into a restored offset, Exo can briefly report ~0 — keep UI seek.
        val positionMs =
            if (
                reported < 1_000L &&
                held > reported + 1_500L &&
                p.playbackState == Player.STATE_BUFFERING
            ) {
                held
            } else {
                reported
            }
        _ui.update {
            it.copy(
                positionMs = positionMs,
                durationMs = durationMs,
                isPlaying = p.isPlaying,
            )
        }
        // Throttled autosave while playing.
        if (p.isPlaying) persistSession(force = false)
    }

    /**
     * Snapshot ExoPlayer position into UI (if media is loaded), then write session prefs.
     * Use [commit] on shutdown paths so the write survives process death.
     */
    fun capturePositionFromPlayer() {
        val p = player ?: return
        if (p.mediaItemCount == 0) return
        if (p.playbackState == Player.STATE_IDLE) return
        val rawDuration = p.duration
        val durationMs = if (rawDuration > 0L) rawDuration else 0L
        val positionMs = p.currentPosition.coerceAtLeast(0L).let { pos ->
            if (durationMs > 0L) pos.coerceAtMost(durationMs) else pos
        }
        _ui.update {
            it.copy(
                positionMs = positionMs,
                durationMs = if (durationMs > 0L) durationMs else it.durationMs,
                isPlaying = p.isPlaying,
            )
        }
    }

    fun persistSession(force: Boolean, commit: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force && !commit && now - lastPersistedAtMs < 4_000L) return
        lastPersistedAtMs = now
        val state = _ui.value
        repository.saveSession(
            currentId = state.current?.id,
            positionMs = state.positionMs,
            shuffle = state.shuffle,
            repeat = state.repeat,
            commit = commit,
        )
    }

    fun detachPlayer() {
        capturePositionFromPlayer()
        persistSession(force = true, commit = true)
        positionListener?.let { player?.removeListener(it) }
        positionListener = null
        player = null
    }

    fun clearPlaybackHandlers() {
        onPlayEntryRequest = null
        onPlayPauseRequest = null
        onSeekRequest = null
        onNextRequest = null
        onPreviousRequest = null
        onStopServiceRequest = null
    }

    fun consumePendingPlay(): PlayRequest? {
        val p = pendingPlay
        pendingPlay = null
        return p
    }

    /** Drain next/prev queued while the service was restarting. */
    fun consumePendingTransport(): String? {
        val t = pendingTransport
        pendingTransport = null
        return when (t) {
            PendingTransport.Next -> "next"
            PendingTransport.Previous -> "previous"
            null -> null
        }
    }

    private fun ensureService() {
        if (onPlayEntryRequest == null) {
            onEnsureService?.invoke()
        }
    }

    fun setPlaylist(list: List<PlaylistEntry>, name: String, clearCurrent: Boolean = true) {
        entries = list
        resolver.clearShuffleBuffer()
        _ui.update {
            it.copy(
                entries = list,
                playlistName = name,
                current = if (clearCurrent) null else it.current,
                error = null,
            )
        }
    }

    /** Append entries; keeps current track. Skips ids already in the list. */
    fun appendPlaylist(list: List<PlaylistEntry>, addedName: String) {
        if (list.isEmpty()) return
        val existingIds = entries.map { it.id.lowercase() }.toHashSet()
        val toAdd = list.filter { it.id.lowercase() !in existingIds }
        if (toAdd.isEmpty()) return
        val merged = entries + toAdd
        entries = merged
        resolver.clearShuffleBuffer()
        val label = when {
            _ui.value.playlistName.isBlank() -> addedName
            addedName.isBlank() -> _ui.value.playlistName
            else -> _ui.value.playlistName // keep compound name as-is for now
        }
        _ui.update {
            it.copy(
                entries = merged,
                playlistName = label.ifBlank { "Playlist" },
                error = null,
            )
        }
        if (resolver.shuffleEnabled) {
            // Refill shuffle lookahead against the larger catalog.
            resolver.onShuffleEnabled(currentEntry, entries.size)
        }
        persistSession(force = true)
    }

    fun setCurrent(entry: PlaylistEntry) {
        _ui.update { it.copy(current = entry) }
        persistSession(force = true)
    }

    fun setPlaying(playing: Boolean) {
        _ui.update { it.copy(isPlaying = playing) }
    }

    fun setPositionMs(ms: Long) {
        _ui.update { it.copy(positionMs = ms) }
    }

    fun setError(message: String?) {
        _ui.update { it.copy(error = message) }
    }

    fun playEntry(
        entry: PlaylistEntry,
        startPositionMs: Long = 0L,
        playWhenReady: Boolean = true,
    ) {
        val request = PlayRequest(entry, startPositionMs, playWhenReady)
        val handler = onPlayEntryRequest
        if (handler != null) {
            handler(request)
        } else {
            pendingTransport = null
            pendingPlay = request
            setCurrent(entry)
            _ui.update { it.copy(positionMs = startPositionMs.coerceAtLeast(0L)) }
            ensureService()
        }
    }

    fun playPause() {
        val handler = onPlayPauseRequest
        if (handler != null) {
            handler()
            return
        }
        // Service died — restart and resume/start the current row.
        val cur = currentEntry ?: entries.firstOrNull() ?: return
        pendingTransport = null
        pendingPlay = PlayRequest(cur, _ui.value.positionMs, playWhenReady = true)
        ensureService()
    }

    fun seekTo(ms: Long) {
        _ui.update { it.copy(positionMs = ms.coerceAtLeast(0L)) }
        persistSession(force = true)
        val handler = onSeekRequest
        if (handler != null) {
            handler(ms)
        } else {
            // Apply on next play once the service is back.
            val cur = currentEntry ?: return
            pendingPlay = PlayRequest(cur, ms, playWhenReady = isPlaying)
            ensureService()
        }
    }

    fun next() {
        val handler = onNextRequest
        if (handler != null) {
            handler()
            return
        }
        pendingPlay = null
        pendingTransport = PendingTransport.Next
        ensureService()
    }

    fun previous() {
        val handler = onPreviousRequest
        if (handler != null) {
            handler()
            return
        }
        pendingPlay = null
        pendingTransport = PendingTransport.Previous
        ensureService()
    }

    fun stopService() {
        persistSession(force = true)
        onStopServiceRequest?.invoke()
    }

    fun setShuffle(enabled: Boolean) {
        resolver.shuffleEnabled = enabled
        if (enabled) {
            resolver.onShuffleEnabled(currentEntry, entries.size)
        }
        _ui.update { it.copy(shuffle = enabled) }
        persistSession(force = true)
    }

    fun cycleRepeat() {
        val next = when (resolver.repeatMode) {
            RepeatMode.Off -> RepeatMode.All
            RepeatMode.All -> RepeatMode.One
            RepeatMode.One -> RepeatMode.Off
        }
        setRepeat(next)
    }

    fun setRepeat(mode: RepeatMode) {
        resolver.repeatMode = mode
        _ui.update { it.copy(repeat = mode) }
        persistSession(force = true)
    }

    /** Apply saved shuffle/repeat and optionally select the last track (without requiring service). */
    fun restoreSessionPreferences() {
        val session = repository.loadSession()
        resolver.shuffleEnabled = session.shuffle
        resolver.repeatMode = session.repeat
        _ui.update {
            it.copy(
                shuffle = session.shuffle,
                repeat = session.repeat,
            )
        }
        if (session.shuffle && currentEntry != null) {
            resolver.onShuffleEnabled(currentEntry, entries.size)
        }
    }

    fun findEntryById(id: String?): PlaylistEntry? {
        if (id.isNullOrBlank()) return null
        return entries.firstOrNull { it.id.equals(id, ignoreCase = true) }
    }
}

data class PlayerUiState(
    val entries: List<PlaylistEntry> = emptyList(),
    val playlistName: String = "",
    val current: PlaylistEntry? = null,
    val isPlaying: Boolean = false,
    val positionMs: Long = 0L,
    val durationMs: Long = 0L,
    val shuffle: Boolean = false,
    val repeat: RepeatMode = RepeatMode.Off,
    val error: String? = null,
)
