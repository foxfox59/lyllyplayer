package com.lyllyplayer.app.playlist

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Desktop .lyllylist / saved playlist JSON (SavedPlaylist + SavedPlaylistEntry).
 */
object LyllylistParser {
    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    @Serializable
    data class SavedPlaylist(
        val Id: String = "",
        val Name: String = "Playlist",
        val CreatedUtc: String? = null,
        val SourceType: String = "",
        val Source: String = "",
        val Entries: List<SavedPlaylistEntry> = emptyList(),
        val OriginByVideoId: Map<String, String>? = null,
        val OriginInfoByVideoId: Map<String, SavedPlaylistOrigin>? = null,
    )

    @Serializable
    data class SavedPlaylistEntry(
        val VideoId: String,
        val Title: String = "",
        val Channel: String? = null,
        val Url: String = "",
    )

    @Serializable
    data class SavedPlaylistOrigin(
        val Label: String = "",
        val Source: String = "",
    )

    data class LoadResult(
        val name: String,
        val entries: List<PlaylistEntry>,
    )

    fun parse(text: String): LoadResult {
        val pl = json.decodeFromString(SavedPlaylist.serializer(), text)
        val entries = pl.Entries.mapNotNull { e ->
            val id = e.VideoId.trim()
            if (id.isEmpty()) return@mapNotNull null
            val url = e.Url.trim().ifEmpty {
                when {
                    PlaylistEntry.looksLikeYoutubeId(id) ->
                        "https://www.youtube.com/watch?v=$id"
                    else -> id
                }
            }
            PlaylistEntry(
                id = id,
                title = e.Title.ifBlank { id },
                channel = e.Channel,
                url = url,
            )
        }
        return LoadResult(name = pl.Name.ifBlank { "Playlist" }, entries = entries)
    }
}
