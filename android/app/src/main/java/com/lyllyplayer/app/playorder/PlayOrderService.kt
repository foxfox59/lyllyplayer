package com.lyllyplayer.app.playorder

import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/**
 * Port of LyllyPlayer.Core PlayOrderService — shuffle tape + recently-played window.
 */
class PlayOrderService {
    private val shuffleTapeVideoIds = ArrayList<String>()
    private val recentlyPlayedVideoIds = ArrayList<String>(5)
    private var shuffleTapeCursor: Int = -1

    val shuffleTapeIds: List<String> get() = shuffleTapeVideoIds.toList()
    val tapeCursor: Int get() = shuffleTapeCursor
    val recentlyPlayedCount: Int get() = recentlyPlayedVideoIds.size

    fun getRecentlyPlayedWindowSize(playlistEntryCount: Int): Int {
        val n = max(0, playlistEntryCount)
        val scaled = ceil(sqrt(n.toDouble())).toInt()
        return scaled.coerceIn(5, 40)
    }

    fun getShuffleTapeMaxSize(playlistEntryCount: Int): Int {
        val n = max(0, playlistEntryCount)
        val scaled = ceil(n * 0.10).toInt()
        val maxSize = scaled.coerceIn(10, 200)
        return if (n == 0) maxSize else min(maxSize, n)
    }

    fun recordNowPlayingForShuffleTape(videoId: String, playlistEntryCount: Int) {
        if (videoId.isBlank()) return
        try {
            if (shuffleTapeCursor >= 0 && shuffleTapeCursor < shuffleTapeVideoIds.size) {
                if (shuffleTapeVideoIds[shuffleTapeCursor].equals(videoId, ignoreCase = true)) return

                if (shuffleTapeCursor + 1 < shuffleTapeVideoIds.size &&
                    shuffleTapeVideoIds[shuffleTapeCursor + 1].equals(videoId, ignoreCase = true)
                ) {
                    shuffleTapeCursor++
                    return
                }

                if (shuffleTapeCursor - 1 >= 0 &&
                    shuffleTapeVideoIds[shuffleTapeCursor - 1].equals(videoId, ignoreCase = true)
                ) {
                    shuffleTapeCursor--
                    return
                }

                if (shuffleTapeCursor < shuffleTapeVideoIds.size - 1) {
                    shuffleTapeVideoIds.subList(shuffleTapeCursor + 1, shuffleTapeVideoIds.size).clear()
                }
            }

            if (shuffleTapeVideoIds.isEmpty() ||
                !shuffleTapeVideoIds.last().equals(videoId, ignoreCase = true)
            ) {
                shuffleTapeVideoIds.add(videoId)
            }
            shuffleTapeCursor = shuffleTapeVideoIds.lastIndex

            val maxSize = getShuffleTapeMaxSize(playlistEntryCount)
            while (shuffleTapeVideoIds.size > maxSize) {
                shuffleTapeVideoIds.removeAt(0)
                shuffleTapeCursor--
            }
            shuffleTapeCursor = shuffleTapeCursor.coerceIn(-1, shuffleTapeVideoIds.lastIndex)
        } catch (_: Exception) {
            // ignore
        }
    }

    fun recordRecentlyPlayedVideoId(videoId: String, playlistEntryCount: Int) {
        if (videoId.isBlank()) return
        try {
            recentlyPlayedVideoIds.add(videoId)
            val maxSize = getRecentlyPlayedWindowSize(playlistEntryCount)
            while (recentlyPlayedVideoIds.size > maxSize) {
                recentlyPlayedVideoIds.removeAt(0)
            }
        } catch (_: Exception) {
            // ignore
        }
    }

    fun recentlyPlayedContains(videoId: String): Boolean =
        recentlyPlayedVideoIds.any { it.equals(videoId, ignoreCase = true) }

    fun clearRecentlyPlayed() = recentlyPlayedVideoIds.clear()

    fun clearShuffleTape() {
        shuffleTapeVideoIds.clear()
        shuffleTapeCursor = -1
    }
}
