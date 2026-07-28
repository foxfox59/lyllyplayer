package com.lyllyplayer.app.playback

import android.app.PendingIntent
import android.content.Context
import android.graphics.Bitmap
import android.support.v4.media.MediaMetadataCompat
import android.support.v4.media.session.MediaSessionCompat
import android.support.v4.media.session.PlaybackStateCompat
import androidx.media.app.NotificationCompat as MediaNotificationCompat

/**
 * Separate legacy session used only for the notification / lock-screen chrome.
 *
 * Media3's session often reports BUFFERING (SystemUI spinner in the play slot) and
 * may omit PLAY/PAUSE in the platform PlaybackState when we manage the playlist
 * outside ExoPlayer. This session always advertises play/pause/prev/next and only
 * publishes PLAYING or PAUSED — never BUFFERING.
 */
class NotificationMediaSession(
    context: Context,
    private val play: () -> Unit,
    private val pause: () -> Unit,
    private val skipNext: () -> Unit,
    private val skipPrevious: () -> Unit,
    private val stop: () -> Unit,
) {
    val session: MediaSessionCompat = MediaSessionCompat(context, "LyllyNotification").apply {
        setFlags(
            MediaSessionCompat.FLAG_HANDLES_MEDIA_BUTTONS or
                MediaSessionCompat.FLAG_HANDLES_TRANSPORT_CONTROLS,
        )
        setCallback(
            object : MediaSessionCompat.Callback() {
                override fun onPlay() {
                    play()
                }

                override fun onPause() {
                    pause()
                }

                override fun onSkipToNext() {
                    skipNext()
                }

                override fun onSkipToPrevious() {
                    skipPrevious()
                }

                override fun onStop() {
                    stop()
                }
            },
        )
        isActive = false
    }

    fun update(
        appName: String,
        detail: String,
        isPlaying: Boolean,
        positionMs: Long,
        durationMs: Long,
        art: Bitmap?,
        sessionActivity: PendingIntent?,
    ) {
        if (sessionActivity != null) {
            session.setSessionActivity(sessionActivity)
        }

        val actions =
            PlaybackStateCompat.ACTION_PLAY or
                PlaybackStateCompat.ACTION_PAUSE or
                PlaybackStateCompat.ACTION_PLAY_PAUSE or
                PlaybackStateCompat.ACTION_SKIP_TO_NEXT or
                PlaybackStateCompat.ACTION_SKIP_TO_PREVIOUS or
                PlaybackStateCompat.ACTION_STOP

        val state =
            if (isPlaying) PlaybackStateCompat.STATE_PLAYING
            else PlaybackStateCompat.STATE_PAUSED

        session.setPlaybackState(
            PlaybackStateCompat.Builder()
                .setActions(actions)
                .setState(
                    state,
                    positionMs.coerceAtLeast(0L),
                    if (isPlaying) 1f else 0f,
                )
                .build(),
        )

        val meta = MediaMetadataCompat.Builder()
            .putString(MediaMetadataCompat.METADATA_KEY_TITLE, appName)
            .putString(MediaMetadataCompat.METADATA_KEY_DISPLAY_TITLE, appName)
            .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, detail)
            .putString(MediaMetadataCompat.METADATA_KEY_DISPLAY_SUBTITLE, detail)
            .putLong(
                MediaMetadataCompat.METADATA_KEY_DURATION,
                if (durationMs > 0L) durationMs else -1L,
            )
        if (art != null) {
            meta.putBitmap(MediaMetadataCompat.METADATA_KEY_ALBUM_ART, art)
            meta.putBitmap(MediaMetadataCompat.METADATA_KEY_DISPLAY_ICON, art)
        }
        session.setMetadata(meta.build())
        session.isActive = true
    }

    fun mediaStyle(): MediaNotificationCompat.MediaStyle =
        MediaNotificationCompat.MediaStyle()
            .setMediaSession(session.sessionToken)
            .setShowActionsInCompactView(0, 1, 2)

    fun release() {
        session.isActive = false
        session.release()
    }
}
