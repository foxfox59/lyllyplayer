package com.lyllyplayer.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import com.lyllyplayer.app.playback.PlaybackService
import com.lyllyplayer.app.playlist.PlaylistOpenMode
import com.lyllyplayer.app.ui.PlayerScreen
import com.lyllyplayer.app.ui.theme.LyllyPlayerTheme

class MainActivity : ComponentActivity() {
    private val bridge get() = (application as LyllyPlayerApp).bridge

    private val openPlaylist = registerForActivityResult(
        ActivityResultContracts.OpenDocument(),
    ) { uri ->
        if (uri == null) return@registerForActivityResult
        val result = bridge.repository.loadFromUri(uri)
        result.onSuccess { entries ->
            ensureService()
            when (bridge.repository.getPlaylistOpenMode()) {
                PlaylistOpenMode.Append -> {
                    val hadCurrent = bridge.ui.value.current != null || bridge.entries.isNotEmpty()
                    bridge.appendPlaylist(entries, bridge.repository.playlistName)
                    if (!hadCurrent) {
                        bridge.repository.clearTrackPosition()
                        entries.firstOrNull()?.let { bridge.playEntry(it, playWhenReady = true) }
                    }
                }
                PlaylistOpenMode.Replace -> {
                    bridge.setPlaylist(entries, bridge.repository.playlistName)
                    bridge.repository.clearTrackPosition()
                    entries.firstOrNull()?.let { bridge.playEntry(it, playWhenReady = true) }
                }
            }
        }.onFailure { e ->
            bridge.setError(e.message ?: "Failed to load playlist")
        }
    }

    private val requestNotif = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { /* best-effort */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        maybeRequestNotificationPermission()
        ensureService()

        if (bridge.entries.isEmpty()) {
            restoreLastSession()
        }

        setContent {
            LyllyPlayerTheme {
                PlayerScreen(
                    bridge = bridge,
                    onLoadPlaylist = {
                        openPlaylist.launch(
                            arrayOf(
                                "audio/*",
                                "application/vnd.apple.mpegurl",
                                "application/x-mpegURL",
                                "text/plain",
                                "application/json",
                                "application/octet-stream",
                                "*/*",
                            ),
                        )
                    },
                    onQuit = {
                        bridge.capturePositionFromPlayer()
                        bridge.persistSession(force = true, commit = true)
                        bridge.stopService()
                        finishAffinity()
                    },
                )
            }
        }
    }

    override fun onPause() {
        bridge.capturePositionFromPlayer()
        bridge.persistSession(force = true, commit = true)
        super.onPause()
    }

    override fun onStop() {
        bridge.capturePositionFromPlayer()
        bridge.persistSession(force = true, commit = true)
        super.onStop()
    }

    private fun restoreLastSession() {
        if (!bridge.repository.restoreLastIfPossible()) {
            bridge.restoreSessionPreferences()
            return
        }
        val entries = bridge.repository.entries
        bridge.setPlaylist(entries, bridge.repository.playlistName)
        bridge.restoreSessionPreferences()

        val session = bridge.repository.loadSession()
        val entry = bridge.findEntryById(session.currentId) ?: entries.firstOrNull() ?: return
        // Restore track + seek position into the UI immediately; prepare without auto-play.
        if (session.positionMs > 0L) {
            bridge.setPositionMs(session.positionMs)
        }
        bridge.playEntry(
            entry = entry,
            startPositionMs = session.positionMs,
            playWhenReady = false,
        )
    }

    private fun ensureService() {
        // Regular start — MediaSessionService promotes to foreground when playback notifies.
        startService(Intent(this, PlaybackService::class.java))
    }

    private fun maybeRequestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33) {
            val granted = ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                requestNotif.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }
}
