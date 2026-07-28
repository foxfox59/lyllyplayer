package com.lyllyplayer.app.playorder

import com.lyllyplayer.app.playlist.PlaylistEntry
import kotlin.random.Random

enum class RepeatMode {
    Off,
    One,
    All,
}

/**
 * Port of MainWindow ResolveNextTrack / PeekNextTrackForPreheatOrPrefetch (no song-queue).
 * UI list order is never shuffled — only play order.
 */
class NextTrackResolver(
    private val playOrder: PlayOrderService,
    private val random: Random = Random.Default,
) {
    private val shuffleNextBuffer = ArrayDeque<PlaylistEntry>()

    @Volatile
    var shuffleEnabled: Boolean = false
        set(value) {
            field = value
            if (value) {
                shuffleNextBuffer.clear()
                playOrder.clearShuffleTape()
            } else {
                shuffleNextBuffer.clear()
            }
        }

    @Volatile
    var repeatMode: RepeatMode = RepeatMode.Off

    fun clearShuffleBuffer() = shuffleNextBuffer.clear()

    fun onNowPlayingChanged(entry: PlaylistEntry?, playlistSize: Int) {
        if (entry == null) return
        if (shuffleEnabled) {
            playOrder.recordNowPlayingForShuffleTape(entry.id, playlistSize)
            playOrder.recordRecentlyPlayedVideoId(entry.id, playlistSize)
        }
    }

    fun onShuffleEnabled(current: PlaylistEntry?, playlistSize: Int) {
        shuffleNextBuffer.clear()
        playOrder.clearShuffleTape()
        playOrder.clearRecentlyPlayed()
        if (current != null) {
            playOrder.recordNowPlayingForShuffleTape(current.id, playlistSize)
        }
        ensureShuffleBufferHasItems(current?.id, emptyList())
    }

    /** Mutating: advances shuffle buffer / sequential index logic. */
    fun resolveNext(
        entries: List<PlaylistEntry>,
        currentIndex: Int,
        currentId: String?,
    ): PlaylistEntry? {
        if (entries.isEmpty()) return null

        if (shuffleEnabled) {
            // Tape-forward after Prev
            if (playOrder.tapeCursor >= 0 && playOrder.tapeCursor < playOrder.shuffleTapeIds.size - 1) {
                for (i in (playOrder.tapeCursor + 1) until playOrder.shuffleTapeIds.size) {
                    val vid = playOrder.shuffleTapeIds[i]
                    val e = entries.firstOrNull { it.id.equals(vid, ignoreCase = true) }
                    if (e != null) return e
                }
            }

            var candidates = entries.filter {
                !it.id.equals(currentId, ignoreCase = true) &&
                    !playOrder.recentlyPlayedContains(it.id)
            }.toMutableList()

            if (candidates.isEmpty()) {
                playOrder.clearRecentlyPlayed()
                candidates = entries.filter {
                    !it.id.equals(currentId, ignoreCase = true)
                }.toMutableList()
            }
            if (candidates.isEmpty()) return null

            val next = if (shuffleNextBuffer.isNotEmpty()) shuffleNextBuffer.removeFirst() else null
            while (shuffleNextBuffer.size < 3 && candidates.isNotEmpty()) {
                val idx = random.nextInt(candidates.size)
                shuffleNextBuffer.addLast(candidates.removeAt(idx))
            }
            return next
        }

        // Sequential
        var nextIndex = currentIndex + 1
        if (repeatMode == RepeatMode.All && nextIndex >= entries.size) {
            nextIndex = 0
        }
        if (nextIndex < 0 || nextIndex >= entries.size) return null
        return entries[nextIndex]
    }

    /** Non-mutating peek for prefetch / preload. */
    fun peekNext(
        entries: List<PlaylistEntry>,
        currentIndex: Int,
        currentId: String?,
    ): PlaylistEntry? {
        if (entries.isEmpty()) return null
        if (shuffleEnabled) {
            if (playOrder.tapeCursor >= 0 && playOrder.tapeCursor < playOrder.shuffleTapeIds.size - 1) {
                return null // history traversal — suppress prefetch
            }
            if (shuffleNextBuffer.isNotEmpty()) return shuffleNextBuffer.first()
            // Ensure buffer so peek can see something after shuffle just enabled
            ensureShuffleBufferHasItems(currentId, entries)
            return shuffleNextBuffer.firstOrNull()
        }
        var nextIndex = currentIndex + 1
        if (repeatMode == RepeatMode.All && nextIndex >= entries.size) nextIndex = 0
        if (nextIndex < 0 || nextIndex >= entries.size) return null
        return entries[nextIndex]
    }

    fun resolvePrevious(
        entries: List<PlaylistEntry>,
        currentIndex: Int,
    ): PlaylistEntry? {
        if (entries.isEmpty()) return null
        if (shuffleEnabled) {
            val tape = playOrder.shuffleTapeIds
            val cursor = playOrder.tapeCursor
            if (cursor > 0) {
                for (i in (cursor - 1) downTo 0) {
                    val e = entries.firstOrNull { it.id.equals(tape[i], ignoreCase = true) }
                    if (e != null) return e
                }
            }
            return null
        }
        val prev = currentIndex - 1
        if (prev < 0) {
            return if (repeatMode == RepeatMode.All) entries.lastOrNull() else null
        }
        return entries.getOrNull(prev)
    }

    private fun ensureShuffleBufferHasItems(currentId: String?, entries: List<PlaylistEntry>) {
        if (shuffleNextBuffer.size >= 3 || entries.isEmpty()) return
        var candidates = entries.filter {
            !it.id.equals(currentId, ignoreCase = true) &&
                !playOrder.recentlyPlayedContains(it.id) &&
                shuffleNextBuffer.none { b -> b.id.equals(it.id, ignoreCase = true) }
        }.toMutableList()
        if (candidates.isEmpty()) {
            playOrder.clearRecentlyPlayed()
            candidates = entries.filter {
                !it.id.equals(currentId, ignoreCase = true) &&
                    shuffleNextBuffer.none { b -> b.id.equals(it.id, ignoreCase = true) }
            }.toMutableList()
        }
        while (shuffleNextBuffer.size < 3 && candidates.isNotEmpty()) {
            val idx = random.nextInt(candidates.size)
            shuffleNextBuffer.addLast(candidates.removeAt(idx))
        }
    }
}
