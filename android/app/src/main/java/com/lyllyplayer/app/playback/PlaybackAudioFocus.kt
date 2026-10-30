package com.lyllyplayer.app.playback

import android.content.Context
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Build
import android.util.Log
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import android.media.AudioAttributes as PlatformAudioAttributes

/**
 * Manual audio-focus handling tuned for music:
 * - Permanent [AudioManager.AUDIOFOCUS_LOSS]: pause (another media app took over).
 * - Transient / duckable loss (notifications, brief system sounds): duck volume and keep playing.
 * - Focus regain: restore volume; resume only if we paused for a permanent loss.
 *
 * ExoPlayer's built-in focus handler pauses on every transient loss, which makes notification
 * sounds feel like they "stop" playback.
 */
@UnstableApi
class PlaybackAudioFocus(
    context: Context,
    private val playerProvider: () -> Player?,
) : AudioManager.OnAudioFocusChangeListener {
    private val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private var focusRequest: AudioFocusRequest? = null
    private var hasFocus: Boolean = false
    private var ducked: Boolean = false
    private var resumeAfterPermanentLoss: Boolean = false

    fun request(): Boolean {
        if (hasFocus) return true
        val result = if (Build.VERSION.SDK_INT >= 26) {
            val attrs = PlatformAudioAttributes.Builder()
                .setUsage(PlatformAudioAttributes.USAGE_MEDIA)
                .setContentType(PlatformAudioAttributes.CONTENT_TYPE_MUSIC)
                .build()
            val req = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                .setAudioAttributes(attrs)
                .setOnAudioFocusChangeListener(this)
                .setWillPauseWhenDucked(false)
                .setAcceptsDelayedFocusGain(true)
                .build()
            focusRequest = req
            audioManager.requestAudioFocus(req)
        } else {
            @Suppress("DEPRECATION")
            audioManager.requestAudioFocus(
                this,
                AudioManager.STREAM_MUSIC,
                AudioManager.AUDIOFOCUS_GAIN,
            )
        }
        hasFocus = result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
        if (hasFocus) {
            unduck()
            resumeAfterPermanentLoss = false
        }
        return hasFocus
    }

    fun abandon() {
        if (!hasFocus && focusRequest == null) return
        unduck()
        resumeAfterPermanentLoss = false
        if (Build.VERSION.SDK_INT >= 26) {
            focusRequest?.let { audioManager.abandonAudioFocusRequest(it) }
        } else {
            @Suppress("DEPRECATION")
            audioManager.abandonAudioFocus(this)
        }
        focusRequest = null
        hasFocus = false
    }

    override fun onAudioFocusChange(focusChange: Int) {
        val p = playerProvider() ?: return
        when (focusChange) {
            AudioManager.AUDIOFOCUS_GAIN -> {
                hasFocus = true
                unduck()
                if (resumeAfterPermanentLoss) {
                    resumeAfterPermanentLoss = false
                    p.playWhenReady = true
                    p.play()
                }
            }
            AudioManager.AUDIOFOCUS_LOSS -> {
                hasFocus = false
                unduck()
                resumeAfterPermanentLoss = p.playWhenReady || p.isPlaying
                p.pause()
            }
            AudioManager.AUDIOFOCUS_LOSS_TRANSIENT,
            AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK,
            -> {
                // Notifications / brief system audio — keep playing quietly.
                duck()
            }
            else -> Log.d(TAG, "Unhandled audio focus change: $focusChange")
        }
    }

    private fun duck() {
        val p = playerProvider() ?: return
        if (!ducked) {
            p.volume = DUCK_VOLUME
            ducked = true
        }
    }

    private fun unduck() {
        val p = playerProvider() ?: return
        if (ducked || p.volume < 0.99f) {
            p.volume = 1f
        }
        ducked = false
    }

    companion object {
        private const val TAG = "LyllyAudioFocus"
        private const val DUCK_VOLUME = 0.2f
    }
}
