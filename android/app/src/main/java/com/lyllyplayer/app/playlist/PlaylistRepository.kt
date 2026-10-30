package com.lyllyplayer.app.playlist

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.edit
import com.lyllyplayer.app.playorder.RepeatMode
import java.io.File
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

private enum class LastImportKind {
    PlaylistFile,
    LocalFiles,
    LocalFolder,
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
        takeReadPermission(uri)

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
        rememberPlaylistFile(uri, playlistName)
        loaded
    }

    fun importLocalDocuments(uris: List<Uri>): Result<LocalMediaImporter.ImportResult> = runCatching {
        require(uris.isNotEmpty()) { "No files selected" }
        for (uri in uris) takeReadPermission(uri)
        val result = LocalMediaImporter.fromDocumentUris(context, uris)
        require(result.entries.isNotEmpty()) { "No supported audio files in selection" }
        applyImport(result, LastImportKind.LocalFiles, primaryUri = null, localUris = uris)
        result
    }

    fun importLocalFolder(treeUri: Uri): Result<LocalMediaImporter.ImportResult> = runCatching {
        takeReadPermission(treeUri)
        val result = LocalMediaImporter.fromTreeUri(
            context = context,
            treeUri = treeUri,
            includeSubfolders = isIncludeSubfoldersOnFolderLoad(),
        )
        require(result.entries.isNotEmpty()) { "No supported audio files in folder" }
        applyImport(result, LastImportKind.LocalFolder, primaryUri = treeUri, localUris = null)
        result
    }

    fun restoreLastIfPossible(): Boolean {
        return when (lastImportKind()) {
            LastImportKind.LocalFolder -> {
                val raw = prefs.getString(KEY_LAST_TREE_URI, null) ?: return false
                importLocalFolder(Uri.parse(raw)).isSuccess && entries.isNotEmpty()
            }
            LastImportKind.LocalFiles -> {
                val uris = readRememberedLocalUris()
                if (uris.isEmpty()) return false
                importLocalDocuments(uris).isSuccess && entries.isNotEmpty()
            }
            LastImportKind.PlaylistFile -> {
                val raw = prefs.getString(KEY_LAST_URI, null) ?: return false
                try {
                    loadFromUri(Uri.parse(raw)).isSuccess && entries.isNotEmpty()
                } catch (_: Exception) {
                    false
                }
            }
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

    fun isIncludeSubfoldersOnFolderLoad(): Boolean =
        prefs.getBoolean(KEY_INCLUDE_SUBFOLDERS, true)

    fun setIncludeSubfoldersOnFolderLoad(enabled: Boolean) {
        prefs.edit { putBoolean(KEY_INCLUDE_SUBFOLDERS, enabled) }
    }

    fun isSpectrumEnabled(): Boolean = prefs.getBoolean(KEY_SPECTRUM_ENABLED, true)

    fun setSpectrumEnabled(enabled: Boolean) {
        prefs.edit { putBoolean(KEY_SPECTRUM_ENABLED, enabled) }
    }

    private fun applyImport(
        result: LocalMediaImporter.ImportResult,
        kind: LastImportKind,
        primaryUri: Uri?,
        localUris: List<Uri>?,
    ) {
        entries = result.entries
        playlistName = result.name
        playlistUri = primaryUri
        prefs.edit {
            putString(KEY_LAST_KIND, kind.name)
            putString(KEY_LAST_NAME, playlistName)
            when (kind) {
                LastImportKind.LocalFolder -> {
                    putString(KEY_LAST_TREE_URI, primaryUri.toString())
                    remove(KEY_LAST_URI)
                }
                LastImportKind.LocalFiles -> {
                    remove(KEY_LAST_URI)
                    remove(KEY_LAST_TREE_URI)
                }
                LastImportKind.PlaylistFile -> Unit
            }
        }
        if (kind == LastImportKind.LocalFiles && localUris != null) {
            writeRememberedLocalUris(localUris)
        } else {
            rememberedLocalUrisFile().delete()
        }
    }

    private fun rememberPlaylistFile(uri: Uri, name: String) {
        prefs.edit {
            putString(KEY_LAST_KIND, LastImportKind.PlaylistFile.name)
            putString(KEY_LAST_URI, uri.toString())
            putString(KEY_LAST_NAME, name)
            remove(KEY_LAST_TREE_URI)
        }
        rememberedLocalUrisFile().delete()
    }

    private fun lastImportKind(): LastImportKind {
        val raw = prefs.getString(KEY_LAST_KIND, null)
        if (raw != null) {
            return runCatching { LastImportKind.valueOf(raw) }.getOrDefault(LastImportKind.PlaylistFile)
        }
        // Legacy: only playlist URI was stored.
        return if (prefs.contains(KEY_LAST_URI)) LastImportKind.PlaylistFile else LastImportKind.PlaylistFile
    }

    private fun takeReadPermission(uri: Uri) {
        try {
            context.contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION,
            )
        } catch (_: SecurityException) {
            // Some providers don't support persistable grants; continue with transient access.
        }
    }

    private fun rememberedLocalUrisFile(): File =
        File(context.filesDir, "last_local_uris.txt")

    private fun writeRememberedLocalUris(uris: List<Uri>) {
        rememberedLocalUrisFile().writeText(
            uris.joinToString("\n") { it.toString() },
            Charsets.UTF_8,
        )
    }

    private fun readRememberedLocalUris(): List<Uri> {
        val file = rememberedLocalUrisFile()
        if (!file.isFile) return emptyList()
        return file.readLines(Charsets.UTF_8)
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .mapNotNull { runCatching { Uri.parse(it) }.getOrNull() }
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
        private const val KEY_LAST_TREE_URI = "last_local_tree_uri"
        private const val KEY_LAST_KIND = "last_import_kind"
        private const val KEY_LAST_NAME = "last_playlist_name"
        private const val KEY_CURRENT_ID = "last_current_id"
        private const val KEY_POSITION_MS = "last_position_ms"
        private const val KEY_SHUFFLE = "shuffle_enabled"
        private const val KEY_REPEAT = "repeat_mode"
        private const val KEY_OPEN_APPEND = "playlist_open_append"
        private const val KEY_INCLUDE_SUBFOLDERS = "include_subfolders_on_folder_load"
        private const val KEY_SPECTRUM_ENABLED = "spectrum_enabled"
    }
}
