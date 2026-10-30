package com.lyllyplayer.app.playlist

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.documentfile.provider.DocumentFile
import java.util.Locale

/**
 * Builds [PlaylistEntry] lists from SAF document URIs and folder trees.
 */
object LocalMediaImporter {
    data class ImportResult(
        val entries: List<PlaylistEntry>,
        val name: String,
    )

    fun fromDocumentUris(context: Context, uris: List<Uri>): ImportResult {
        val entries = ArrayList<PlaylistEntry>(uris.size)
        val seen = HashSet<String>(uris.size)
        for (uri in uris) {
            val name = queryDisplayName(context, uri) ?: uri.lastPathSegment ?: "Audio"
            if (!looksLikeAudio(name, context, uri)) continue
            val url = uri.toString()
            val id = PlaylistEntry.localIdForPath(url)
            if (!seen.add(id.lowercase(Locale.ROOT))) continue
            entries += PlaylistEntry(
                id = id,
                title = name.substringBeforeLast('.').ifBlank { name },
                url = url,
                kind = EntryKind.Local,
            )
        }
        val name = when {
            entries.isEmpty() -> "Local files"
            entries.size == 1 -> entries[0].title
            else -> "Local files (${entries.size})"
        }
        return ImportResult(entries, name)
    }

    fun fromTreeUri(
        context: Context,
        treeUri: Uri,
        includeSubfolders: Boolean,
    ): ImportResult {
        val root = DocumentFile.fromTreeUri(context, treeUri)
            ?: error("Could not open folder")
        val entries = ArrayList<PlaylistEntry>()
        val seen = HashSet<String>()
        collectAudioFiles(root, includeSubfolders, entries, seen)
        entries.sortWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.title })
        val folderName = root.name?.ifBlank { null } ?: "Folder"
        val name = if (entries.isEmpty()) folderName else "$folderName (${entries.size})"
        return ImportResult(entries, name)
    }

    private fun collectAudioFiles(
        dir: DocumentFile,
        recursive: Boolean,
        out: MutableList<PlaylistEntry>,
        seen: MutableSet<String>,
    ) {
        val children = dir.listFiles()
        // Files first in listing order, then recurse — final list is sorted by title.
        for (child in children) {
            if (child.isDirectory) {
                if (recursive) collectAudioFiles(child, recursive = true, out, seen)
                continue
            }
            if (!child.isFile) continue
            val name = child.name ?: continue
            if (!M3uPlaylistParser.isSupportedAudioFileName(name)) continue
            val url = child.uri.toString()
            val id = PlaylistEntry.localIdForPath(url)
            if (!seen.add(id.lowercase(Locale.ROOT))) continue
            out += PlaylistEntry(
                id = id,
                title = name.substringBeforeLast('.').ifBlank { name },
                url = url,
                kind = EntryKind.Local,
            )
        }
    }

    private fun looksLikeAudio(displayName: String, context: Context, uri: Uri): Boolean {
        if (M3uPlaylistParser.isSupportedAudioFileName(displayName)) return true
        // SAF may omit extensions; accept audio/* MIME from the provider.
        val mime = context.contentResolver.getType(uri)?.lowercase(Locale.ROOT) ?: return false
        return mime.startsWith("audio/")
    }

    private fun queryDisplayName(context: Context, uri: Uri): String? {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { c ->
                if (c.moveToFirst()) {
                    val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (idx >= 0) return c.getString(idx)
                }
            }
        return uri.lastPathSegment?.substringAfterLast(':')
    }
}
