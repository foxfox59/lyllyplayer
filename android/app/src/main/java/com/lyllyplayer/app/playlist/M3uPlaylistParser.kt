package com.lyllyplayer.app.playlist

import android.content.Context
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import java.io.BufferedReader
import java.io.InputStreamReader
import java.util.Locale

/**
 * Desktop-compatible M3U / M3U8 loader (see LocalPlaylistLoader on Windows).
 * Relative paths resolve against the playlist's parent DocumentFile when available.
 */
object M3uPlaylistParser {
    private val supportedAudioExt = setOf(
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".aifc",
    )

    fun parse(
        context: Context,
        playlistUri: Uri,
        text: String,
    ): List<PlaylistEntry> {
        val parent = DocumentFile.fromSingleUri(context, playlistUri)?.parentFile
        val entries = ArrayList<PlaylistEntry>()
        var pendingTitle: String? = null

        for (rawLine in text.lineSequence()) {
            val line = rawLine.trim()
            if (line.isEmpty()) continue
            if (line.startsWith("#EXTINF:", ignoreCase = true)) {
                val comma = line.indexOf(',')
                pendingTitle = if (comma >= 0 && comma + 1 < line.length) {
                    line.substring(comma + 1).trim()
                } else {
                    null
                }
                continue
            }
            if (line.startsWith("#")) continue

            val titleHint = pendingTitle
            pendingTitle = null

            when {
                line.startsWith("http://", ignoreCase = true) ||
                    line.startsWith("https://", ignoreCase = true) -> {
                    if (PlaylistEntry.isYoutubeUrl(line)) {
                        val id = extractYoutubeId(line) ?: PlaylistEntry.sha256Hex(line).take(11)
                        entries += PlaylistEntry(
                            id = id,
                            title = titleHint ?: id,
                            url = normalizeYoutubeUrl(line, id),
                            kind = EntryKind.Youtube,
                        )
                    } else {
                        entries += PlaylistEntry(
                            id = PlaylistEntry.streamIdForUrl(line),
                            title = titleHint ?: line.substringAfterLast('/').ifBlank { line },
                            url = line,
                            kind = EntryKind.Stream,
                        )
                    }
                }

                else -> {
                    val resolved = resolveLocal(context, parent, line)
                    if (resolved != null) {
                        val display = titleHint
                            ?: DocumentFile.fromSingleUri(context, Uri.parse(resolved))?.name
                            ?: line.substringAfterLast('/').substringAfterLast('\\')
                        entries += PlaylistEntry(
                            id = PlaylistEntry.localIdForPath(resolved),
                            title = display,
                            url = resolved,
                            kind = EntryKind.Local,
                        )
                    } else {
                        // Keep unresolvable locals visible but unplayable (Windows absolute paths, etc.)
                        val name = titleHint
                            ?: line.substringAfterLast('/').substringAfterLast('\\').ifBlank { line }
                        entries += PlaylistEntry(
                            id = PlaylistEntry.localIdForPath(line),
                            title = name,
                            url = line,
                            kind = EntryKind.Local,
                        )
                    }
                }
            }
        }
        return entries
    }

    private fun resolveLocal(context: Context, parent: DocumentFile?, path: String): String? {
        val trimmed = path.trim().trim('"')
        if (trimmed.startsWith("content://", ignoreCase = true) ||
            trimmed.startsWith("file:", ignoreCase = true)
        ) {
            return trimmed
        }

        // Absolute Windows / Unix paths won't resolve via SAF parent.
        if (trimmed.length >= 2 && trimmed[1] == ':') return null
        if (trimmed.startsWith("/")) return null

        val name = trimmed.substringAfterLast('/').substringAfterLast('\\')
        if (name.isBlank()) return null
        val ext = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
        if (ext.isNotEmpty() && ".${ext}" !in supportedAudioExt && !supportedAudioExt.contains(ext)) {
            // still allow; extension filter is soft for relative names
        }

        val child = parent?.findFile(name) ?: return null
        return child.uri.toString()
    }

    fun extractYoutubeId(url: String): String? {
        val u = url.trim()
        val patterns = listOf(
            Regex("""[?&]v=([A-Za-z0-9_-]{11})"""),
            Regex("""youtu\.be/([A-Za-z0-9_-]{11})"""),
            Regex("""/(?:embed|shorts|live)/([A-Za-z0-9_-]{11})"""),
            Regex("""^([A-Za-z0-9_-]{11})$"""),
        )
        for (p in patterns) {
            val m = p.find(u)
            if (m != null) return m.groupValues[1]
        }
        return null
    }

    fun normalizeYoutubeUrl(url: String, id: String): String {
        if (url.contains("youtube.com", true) || url.contains("youtu.be", true)) return url
        return "https://www.youtube.com/watch?v=$id"
    }

    fun readText(context: Context, uri: Uri): String {
        context.contentResolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "Could not open playlist" }
            return BufferedReader(InputStreamReader(input, Charsets.UTF_8)).readText()
        }
    }
}
