package com.lyllyplayer.app.youtube

import android.util.Log
import com.lyllyplayer.app.playlist.EntryKind
import com.lyllyplayer.app.playlist.M3uPlaylistParser
import com.lyllyplayer.app.playlist.PlaylistEntry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.schabi.newpipe.extractor.NewPipe
import org.schabi.newpipe.extractor.ServiceList
import org.schabi.newpipe.extractor.localization.Localization
import org.schabi.newpipe.extractor.stream.AudioStream
import org.schabi.newpipe.extractor.stream.DeliveryMethod
import org.schabi.newpipe.extractor.stream.StreamInfo
import java.util.concurrent.atomic.AtomicReference

data class ResolvedStream(
    val videoId: String,
    val streamUrl: String,
    val title: String? = null,
)

/**
 * YouTube watch URL → audio stream URL via NewPipe Extractor.
 * Prefetch slot mirrors desktop PlaybackEngine warm path.
 */
class YoutubeStreamResolver {
    private val initMutex = Mutex()
    @Volatile private var initialized = false

    private val prefetched = AtomicReference<ResolvedStream?>(null)
    private val prefetchSkipIds = HashSet<String>()

    private suspend fun ensureInit() {
        if (initialized) return
        initMutex.withLock {
            if (initialized) return
            NewPipe.init(ExtractorDownloader(), Localization.DEFAULT)
            initialized = true
        }
    }

    fun tryConsumePrefetched(videoId: String): ResolvedStream? {
        val p = prefetched.get() ?: return null
        if (!p.videoId.equals(videoId, ignoreCase = true)) return null
        prefetched.compareAndSet(p, null)
        return p
    }

    fun cancelPrefetch() {
        prefetched.set(null)
    }

    fun markSkip(videoId: String) {
        prefetchSkipIds.add(videoId)
        val p = prefetched.get()
        if (p != null && p.videoId.equals(videoId, ignoreCase = true)) {
            prefetched.compareAndSet(p, null)
        }
    }

    suspend fun resolve(entry: PlaylistEntry): ResolvedStream = withContext(Dispatchers.IO) {
        ensureInit()
        val videoId = M3uPlaylistParser.extractYoutubeId(entry.url)
            ?: M3uPlaylistParser.extractYoutubeId(entry.id)
            ?: entry.id.takeIf { PlaylistEntry.looksLikeYoutubeId(it) }
            ?: error("Not a YouTube id: ${entry.id}")

        tryConsumePrefetched(videoId)?.let { return@withContext it }

        if (videoId in prefetchSkipIds) {
            error("Previously failed YouTube id: $videoId")
        }

        val url = "https://www.youtube.com/watch?v=$videoId"
        Log.i(TAG, "Resolving YouTube $videoId")
        val info = StreamInfo.getInfo(ServiceList.YouTube, url)
        val audio = pickBestAudio(info.audioStreams)
            ?: error("No playable audio streams for $videoId (got ${info.audioStreams.size})")
        val streamUrl = audio.content
        if (streamUrl.isNullOrBlank()) {
            error("Empty audio content URL for $videoId")
        }
        Log.i(TAG, "Resolved $videoId → ${streamUrl.take(80)}…")
        ResolvedStream(
            videoId = videoId,
            streamUrl = streamUrl,
            title = info.name,
        )
    }

    suspend fun prefetchBestEffort(entry: PlaylistEntry) {
        if (entry.kind != EntryKind.Youtube) return
        val videoId = M3uPlaylistParser.extractYoutubeId(entry.url)
            ?: M3uPlaylistParser.extractYoutubeId(entry.id)
            ?: return
        if (videoId in prefetchSkipIds) return
        val existing = prefetched.get()
        if (existing != null && existing.videoId.equals(videoId, ignoreCase = true)) return

        runCatching {
            ensureInit()
            val url = "https://www.youtube.com/watch?v=$videoId"
            val info = StreamInfo.getInfo(ServiceList.YouTube, url)
            val audio = pickBestAudio(info.audioStreams) ?: return
            val streamUrl = audio.content ?: return
            if (streamUrl.isBlank()) return
            prefetched.set(
                ResolvedStream(
                    videoId = videoId,
                    streamUrl = streamUrl,
                    title = info.name,
                ),
            )
        }.onFailure { e ->
            Log.w(TAG, "Prefetch failed for $videoId: ${e.message}")
        }
    }

    private fun pickBestAudio(streams: List<AudioStream>): AudioStream? {
        if (streams.isEmpty()) return null
        // Prefer progressive HTTP so ExoPlayer can play with a plain URI (no HLS module required).
        val progressive = streams.filter {
            it.deliveryMethod == DeliveryMethod.PROGRESSIVE_HTTP && !it.content.isNullOrBlank()
        }
        val pool = progressive.ifEmpty {
            streams.filter { !it.content.isNullOrBlank() }
        }
        return pool.maxByOrNull { it.averageBitrate.takeIf { b -> b > 0 } ?: it.bitrate }
            ?: pool.firstOrNull()
    }

    companion object {
        private const val TAG = "LyllyYoutube"
    }
}
