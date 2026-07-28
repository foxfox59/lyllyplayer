package com.lyllyplayer.app.playlist

import java.security.MessageDigest
import java.util.Locale

enum class EntryKind {
    Local,
    Stream,
    Youtube,
}

data class PlaylistEntry(
    val id: String,
    val title: String,
    val channel: String? = null,
    val durationSeconds: Int? = null,
    /** content://, file path, http(s) stream, or YouTube watch URL */
    val url: String,
    val kind: EntryKind = detectKind(id, url),
) {
    companion object {
        fun detectKind(id: String, url: String): EntryKind {
            val idLower = id.lowercase(Locale.ROOT)
            if (idLower.startsWith("local:")) return EntryKind.Local
            if (idLower.startsWith("stream:")) return EntryKind.Stream
            if (isYoutubeUrl(url) || looksLikeYoutubeId(id)) return EntryKind.Youtube
            val u = url.trim()
            if (u.startsWith("http://", ignoreCase = true) || u.startsWith("https://", ignoreCase = true)) {
                return EntryKind.Stream
            }
            if (u.startsWith("content://", ignoreCase = true) || u.startsWith("file:", ignoreCase = true)) {
                return EntryKind.Local
            }
            return EntryKind.Local
        }

        fun isYoutubeUrl(raw: String): Boolean {
            val u = raw.trim().lowercase(Locale.ROOT)
            if (u.isEmpty()) return false
            return u.contains("youtube.com") ||
                u.contains("youtu.be") ||
                u.contains("youtube-nocookie.com") ||
                u.contains("music.youtube.com")
        }

        fun looksLikeYoutubeId(id: String): Boolean {
            val t = id.trim()
            if (t.startsWith("local:", true) || t.startsWith("stream:", true)) return false
            // Classic 11-char YouTube ids
            return t.length == 11 && t.all { it.isLetterOrDigit() || it == '-' || it == '_' }
        }

        fun streamIdForUrl(url: String): String {
            val hex = sha256Hex(url.trim()).take(12)
            return "stream:$hex"
        }

        fun localIdForPath(pathOrName: String): String {
            val file = pathOrName.substringAfterLast('/').substringAfterLast('\\')
            val hex = sha256Hex(pathOrName.trim()).take(12)
            return "local:$file:$hex"
        }

        fun sha256Hex(input: String): String {
            val digest = MessageDigest.getInstance("SHA-256").digest(input.toByteArray(Charsets.UTF_8))
            return digest.joinToString("") { "%02x".format(it) }
        }
    }
}
