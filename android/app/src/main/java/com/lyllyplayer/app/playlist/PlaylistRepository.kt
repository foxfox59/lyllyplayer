package com.lyllyplayer.app.playlist

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.edit
import com.lyllyplayer.app.playorder.RepeatMode
import java.util.Locale

data class PlaybackSession(
    val currentId: String? = null,
    val positionMs: Long = 0L,
    val shuffle: Boolean = false,
    val repeat: RepeatMode = RepeatMode.Off,
)

enum class PlaylistOpenMode {
    Replace,
    Append,
}

class PlaylistRepository(private val context: Context) {
    private val prefs = context.getSharedPreferences("lylly_playlist", Context.MODE_PRIVATE)

    var entries: List<PlaylistEntry> = emptyList()
        private set

    var playlistName: String = ""
        private set

    var playlistUri: Uri? = null
        private set

    fun loadFromUri(uri: Uri): Result<List<PlaylistEntry>> = runCatching {
        try {
            context.contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION,
            )
        } catch (_: SecurityException) {
            // Some providers don't support persistable grants; continue with transient access.
        }

        val name = queryDisplayName(uri) ?: "Playlist"
        val text = M3uPlaylistParser.readText(context, uri)
        val lower = name.lowercase(Locale.ROOT)
        val loaded = when {
            lower.endsWith(".lyllylist") || lower.endsWith(".json") -> {
                val r = LyllylistParser.parse(text)
                playlistName = r.name
                r.entries
            }
            else -> {
                playlistName = name.substringBeforeLast('.')
                M3uPlaylistParser.parse(context, uri, text)
            }
        }

        entries = loaded
        playlistUri = uri
        prefs.edit {
            putString(KEY_LAST_URI, uri.toString())
            putString(KEY_LAST_NAME, playlistName)
        }
        loaded
    }

    fun restoreLastIfPossible(): Boolean {
        val raw = prefs.getString(KEY_LAST_URI, null) ?: return false
        return try {
            loadFromUri(Uri.parse(raw)).isSuccess && entries.isNotEmpty()
        } catch (_: Exception) {
            false
        }
    }

    fun loadSession(): PlaybackSession {
        val repeat = when (prefs.getString(KEY_REPEAT, RepeatMode.Off.name)) {
            RepeatMode.All.name -> RepeatMode.All
            RepeatMode.One.name -> RepeatMode.One
            else -> RepeatMode.Off
        }
        return PlaybackSession(
            currentId = prefs.getString(KEY_CURRENT_ID, null),
            positionMs = prefs.getLong(KEY_POSITION_MS, 0L).coerceAtLeast(0L),
            shuffle = prefs.getBoolean(KEY_SHUFFLE, false),
            repeat = repeat,
        )
    }

    fun saveSession(
        currentId: String?,
        positionMs: Long,
        shuffle: Boolean,
        repeat: RepeatMode,
        commit: Boolean = false,
    ) {
        prefs.edit(commit = commit) {
            if (currentId.isNullOrBlank()) remove(KEY_CURRENT_ID) else putString(KEY_CURRENT_ID, currentId)
            putLong(KEY_POSITION_MS, positionMs.coerceAtLeast(0L))
            putBoolean(KEY_SHUFFLE, shuffle)
            putString(KEY_REPEAT, repeat.name)
        }
    }

    fun clearTrackPosition() {
        prefs.edit {
            remove(KEY_CURRENT_ID)
            putLong(KEY_POSITION_MS, 0L)
        }
    }

    fun getPlaylistOpenMode(): PlaylistOpenMode =
        if (prefs.getBoolean(KEY_OPEN_APPEND, false)) PlaylistOpenMode.Append
        else PlaylistOpenMode.Replace

    fun setPlaylistOpenMode(mode: PlaylistOpenMode) {
        prefs.edit {
            putBoolean(KEY_OPEN_APPEND, mode == PlaylistOpenMode.Append)
        }
    }

    private fun queryDisplayName(uri: Uri): String? {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { c ->
                if (c.moveToFirst()) {
                    val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (idx >= 0) return c.getString(idx)
                }
            }
        return uri.lastPathSegment
    }

    companion object {
        private const val KEY_LAST_URI = "last_playlist_uri"
        private const val KEY_LAST_NAME = "last_playlist_name"
        private const val KEY_CURRENT_ID = "last_current_id"
        private const val KEY_POSITION_MS = "last_position_ms"
        private const val KEY_SHUFFLE = "shuffle_enabled"
        private const val KEY_REPEAT = "repeat_mode"
        private const val KEY_OPEN_APPEND = "playlist_open_append"
    }
}
