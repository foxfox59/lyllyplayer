package com.lyllyplayer.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import android.os.Build
import com.lyllyplayer.app.playback.PlaybackService
import com.lyllyplayer.app.playback.PlayerBridge
import com.lyllyplayer.app.playlist.PlaylistRepository

class LyllyPlayerApp : Application() {
    lateinit var bridge: PlayerBridge
        private set

    override fun onCreate() {
        super.onCreate()
        bridge = PlayerBridge(PlaylistRepository(this))
        bridge.onEnsureService = {
            // Regular start — MediaSessionService promotes to foreground when playback notifies.
            // Avoid startForegroundService here: restore/idle can exceed the FGS start timeout.
            startService(Intent(this, PlaybackService::class.java))
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notification_channel_name),
                NotificationManager.IMPORTANCE_DEFAULT,
            ).apply {
                description = getString(R.string.notification_channel_desc)
                setShowBadge(false)
                lockscreenVisibility = android.app.Notification.VISIBILITY_PUBLIC
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    companion object {
        const val CHANNEL_ID = "lylly_playback_v3"
    }
}