package com.lyllyplayer.app.playback

import android.app.Notification
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Log
import androidx.annotation.OptIn
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.lyllyplayer.app.LyllyPlayerApp
import com.lyllyplayer.app.MainActivity
import com.lyllyplayer.app.R
import com.lyllyplayer.app.playlist.EntryKind
import com.lyllyplayer.app.playlist.PlaylistEntry
import com.lyllyplayer.app.playorder.RepeatMode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@OptIn(UnstableApi::class)
class PlaybackService : MediaSessionService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var player: ExoPlayer? = null
    private var sessionPlayer: PlaylistNavPlayer? = null
    private var session: MediaSession? = null
    private var notificationSession: NotificationMediaSession? = null
    private var sessionActivityPi: PendingIntent? = null
    private var warmJob: Job? = null
    private var playJob: Job? = null
    private var foregroundStarted: Boolean = false

    /** Ignore ENDED while we are swapping media / preparing a new track. */
    @Volatile private var suppressEnded: Boolean = false

    /** One automatic re-resolve after a mid-stream YouTube failure. */
    private var youtubeRetryId: String? = null

    /** Caps auto-skip cascades when many playlist rows are unavailable. */
    private var unplayableSkipCascade: Int = 0

    private val bridge: PlayerBridge
        get() = (application as LyllyPlayerApp).bridge

    override fun onCreate() {
        super.onCreate()

        val exo = ExoPlayer.Builder(this)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                true,
            )
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()

        exo.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_ENDED && !suppressEnded) {
                    handleUnexpectedOrNaturalEnd()
                }
                refreshNotification()
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                bridge.setPlaying(isPlaying)
                if (isPlaying) {
                    youtubeRetryId = null
                    scheduleWarmPrefetch()
                }
                refreshNotification()
            }

            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                // Position is owned by playEntry / ExoPlayer sync — do not force 0 here
                // (that wiped restored seek offsets on cold start).
                refreshNotification()
            }

            override fun onMediaMetadataChanged(mediaMetadata: MediaMetadata) {
                refreshNotification()
            }

            override fun onPlayerError(error: PlaybackException) {
                if (tryRecoverYoutube(error)) return
                val msg = error.message ?: error.errorCodeName
                Log.e(TAG, "Player error: $msg", error)
                bridge.setError(msg)
                refreshNotification()
                // Stop here — do not cascade through the playlist.
            }
        })

        player = exo
        bridge.attachPlayer(exo)

        val navPlayer = PlaylistNavPlayer(
            player = exo,
            onSkipToNext = { playNextInternal() },
            onSkipToPrevious = { playPreviousInternal() },
        )
        sessionPlayer = navPlayer

        sessionActivityPi = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        session = MediaSession.Builder(this, navPlayer)
            .setSessionActivity(sessionActivityPi!!)
            .setCallback(SessionCallback())
            .build()

        notificationSession = NotificationMediaSession(
            context = this,
            play = { handlePlayPause(forcePlay = true) },
            pause = { handlePlayPause(forcePause = true) },
            skipNext = { playNextInternal() },
            skipPrevious = { playPreviousInternal() },
            stop = { shutdownPlayback("session-stop") },
        )

        bridge.onPlayEntryRequest = { request ->
            playEntry(request.entry, request.startPositionMs, request.playWhenReady)
        }
        bridge.onPlayPauseRequest = { handlePlayPause() }
        bridge.onSeekRequest = { ms ->
            player?.seekTo(ms)
            bridge.persistSession(force = true)
        }
        bridge.onNextRequest = { playNextInternal() }
        bridge.onPreviousRequest = { playPreviousInternal() }
        bridge.onStopServiceRequest = {
            shutdownPlayback("app-quit")
        }

        drainPendingBridgeWork()
    }

    /**
     * Bypass Media3's DefaultMediaNotificationProvider path.
     * It often never posts for our single-item player (timeline/controller gates).
     */
    override fun onUpdateNotification(session: MediaSession, startInForegroundRequired: Boolean) {
        refreshNotification(forceForeground = startInForegroundRequired)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_PLAY_PAUSE -> handlePlayPause()
            ACTION_NEXT -> playNextInternal()
            ACTION_PREV -> playPreviousInternal()
            ACTION_STOP -> shutdownPlayback("notification-dismiss")
        }
        val result = super.onStartCommand(intent, flags, startId)
        drainPendingBridgeWork()
        return result
    }

    /** Notification swipe / Media3 dismiss — stop audio and end the service. */
    override fun pauseAllPlayersAndStopSelf() {
        shutdownPlayback("pauseAllPlayersAndStopSelf")
    }

    /** App removed from recents — stop audio and end the service. */
    override fun onTaskRemoved(rootIntent: Intent?) {
        shutdownPlayback("task-removed")
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    override fun onDestroy() {
        if (!playbackShuttingDown) {
            bridge.capturePositionFromPlayer()
            bridge.persistSession(force = true, commit = true)
        }
        warmJob?.cancel()
        playJob?.cancel()
        bridge.clearPlaybackHandlers()
        bridge.detachPlayer()
        clearNotification()
        notificationSession?.release()
        notificationSession = null
        session?.release()
        session = null
        sessionPlayer = null
        player?.release()
        player = null
        scope.cancel()
        super.onDestroy()
    }

    /** Persist, halt playback, tear down notification, and stop this service. */
    private var playbackShuttingDown: Boolean = false

    private fun shutdownPlayback(reason: String) {
        Log.i(TAG, "Shutdown playback ($reason)")
        playbackShuttingDown = true
        // Capture seek before stop/clear — clearing media makes ExoPlayer report 0.
        bridge.capturePositionFromPlayer()
        bridge.persistSession(force = true, commit = true)
        playJob?.cancel()
        warmJob?.cancel()
        try {
            player?.playWhenReady = false
            player?.stop()
            player?.clearMediaItems()
        } catch (t: Throwable) {
            Log.w(TAG, "Error stopping player during shutdown", t)
        }
        bridge.setPlaying(false)
        clearNotification()
        stopSelf()
    }
    private fun refreshNotification(forceForeground: Boolean = false) {
        val notifSession = notificationSession ?: return
        val p = player
        val entry = bridge.currentEntry
        val hasMedia = entry != null || (p != null && p.mediaItemCount > 0)
        if (!hasMedia) {
            clearNotification()
            return
        }

        val isPlaying = p?.isPlaying == true
        val wantsPlayback = forceForeground || isPlaying || p?.playWhenReady == true
        // Avoid posting a MediaStyle notification on cold restore (playWhenReady=false).
        if (!wantsPlayback && !foregroundStarted) return

        val track = p?.mediaMetadata?.title?.toString()?.takeIf { it.isNotBlank() }
            ?: entry?.title?.takeIf { it.isNotBlank() }
        val artist = p?.mediaMetadata?.artist?.toString()?.takeIf { it.isNotBlank() }
            ?: entry?.channel?.takeIf { it.isNotBlank() }
        val detail = when {
            track != null && artist != null -> "$track - $artist"
            track != null -> track
            artist != null -> artist
            else -> getString(R.string.app_name)
        }

        val playPauseIcon =
            if (isPlaying) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play
        val playPauseLabel = if (isPlaying) "Pause" else "Play"
        val positionMs = p?.currentPosition?.coerceAtLeast(0L) ?: bridge.ui.value.positionMs
        val durationMs = p?.duration?.takeIf { it > 0L } ?: bridge.ui.value.durationMs
        val art = notificationLargeIcon()

        try {
            notifSession.update(
                appName = getString(R.string.app_name),
                detail = detail,
                isPlaying = isPlaying,
                positionMs = positionMs,
                durationMs = durationMs,
                art = art,
                sessionActivity = sessionActivityPi,
            )

            val builder = NotificationCompat.Builder(this, LyllyPlayerApp.CHANNEL_ID)
                .setContentTitle(getString(R.string.app_name))
                .setContentText(detail)
                .setSmallIcon(android.R.drawable.ic_media_play)
                .setContentIntent(sessionActivityPi)
                .setDeleteIntent(serviceActionPi(ACTION_STOP))
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .setCategory(NotificationCompat.CATEGORY_TRANSPORT)
                .setOngoing(wantsPlayback)
                .addAction(
                    NotificationCompat.Action(
                        android.R.drawable.ic_media_previous,
                        "Previous",
                        serviceActionPi(ACTION_PREV),
                    ),
                )
                .addAction(
                    NotificationCompat.Action(
                        playPauseIcon,
                        playPauseLabel,
                        serviceActionPi(ACTION_PLAY_PAUSE),
                    ),
                )
                .addAction(
                    NotificationCompat.Action(
                        android.R.drawable.ic_media_next,
                        "Next",
                        serviceActionPi(ACTION_NEXT),
                    ),
                )
                // Legacy session: always PLAYING/PAUSED with play/pause actions (no BUFFERING spinner).
                .setStyle(notifSession.mediaStyle())

            // Do not setLargeIcon — SystemUI media template uses session metadata art instead,
            // and async large-icon loading brought back the spinner.

            val notification = builder.build()
            if (wantsPlayback) {
                val type = if (Build.VERSION.SDK_INT >= 29) {
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
                } else {
                    0
                }
                ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, type)
                foregroundStarted = true
            } else {
                androidx.core.app.NotificationManagerCompat.from(this)
                    .notify(NOTIFICATION_ID, notification)
            }
        } catch (t: Throwable) {
            Log.e(TAG, "Failed to post playback notification", t)
        }
    }

    private var cachedLargeIcon: Bitmap? = null

    /** Software ARGB bitmap only — hardware/adaptive icons can crash SystemUI. */
    private fun notificationLargeIcon(): Bitmap? {
        cachedLargeIcon?.let { return it }
        return try {
            val opts = android.graphics.BitmapFactory.Options().apply {
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
            // Prefer flat PNG foreground over adaptive mipmap XML.
            val decoded = android.graphics.BitmapFactory.decodeResource(
                resources,
                R.drawable.ic_launcher_foreground,
                opts,
            ) ?: android.graphics.BitmapFactory.decodeResource(
                resources,
                R.mipmap.ic_launcher,
                opts,
            ) ?: return null
            val safe = when (decoded.config) {
                Bitmap.Config.ARGB_8888 -> decoded
                else -> decoded.copy(Bitmap.Config.ARGB_8888, /* mutable = */ false)?.also {
                    if (it !== decoded) decoded.recycle()
                } ?: decoded
            }
            cachedLargeIcon = safe
            safe
        } catch (t: Throwable) {
            Log.w(TAG, "Notification large icon unavailable", t)
            null
        }
    }

    private fun clearNotification() {
        if (foregroundStarted) {
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            foregroundStarted = false
        }
        androidx.core.app.NotificationManagerCompat.from(this).cancel(NOTIFICATION_ID)
        notificationSession?.session?.isActive = false
    }

    private fun serviceActionPi(action: String): PendingIntent {
        val intent = Intent(this, PlaybackService::class.java).setAction(action)
        return PendingIntent.getService(
            this,
            action.hashCode(),
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }

    private fun drainPendingBridgeWork() {
        bridge.consumePendingPlay()?.let { req ->
            playEntry(req.entry, req.startPositionMs, req.playWhenReady)
            return
        }
        when (bridge.consumePendingTransport()) {
            "next" -> playNextInternal()
            "previous" -> playPreviousInternal()
        }
    }

    private fun handlePlayPause(forcePlay: Boolean = false, forcePause: Boolean = false) {
        val p = player ?: return
        when {
            forcePause || (!forcePlay && p.isPlaying) -> {
                p.pause()
                bridge.persistSession(force = true)
                refreshNotification()
            }
            p.playbackState == Player.STATE_IDLE || p.mediaItemCount == 0 -> {
                val target = bridge.currentEntry ?: bridge.entries.firstOrNull() ?: return
                val start = if (target.id.equals(bridge.currentEntry?.id, true)) {
                    bridge.ui.value.positionMs
                } else {
                    0L
                }
                playEntry(target, start, playWhenReady = true)
            }
            p.playbackState == Player.STATE_ENDED -> {
                p.seekTo(0)
                p.play()
                refreshNotification()
            }
            else -> {
                p.play()
                refreshNotification()
            }
        }
    }

    private inner class SessionCallback : MediaSession.Callback {
        override fun onConnect(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
        ): MediaSession.ConnectionResult {
            val playerCommands = MediaSession.ConnectionResult.DEFAULT_PLAYER_COMMANDS.buildUpon()
                .add(Player.COMMAND_PLAY_PAUSE)
                .add(Player.COMMAND_SEEK_TO_PREVIOUS)
                .add(Player.COMMAND_SEEK_TO_NEXT)
                .add(Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM)
                .add(Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
                .build()
            return MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                .setAvailablePlayerCommands(playerCommands)
                .build()
        }

        override fun onCustomCommand(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            customCommand: SessionCommand,
            args: Bundle,
        ): ListenableFuture<SessionResult> {
            return Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
        }
    }

    private fun handleUnexpectedOrNaturalEnd() {
        val p = player
        val entry = bridge.currentEntry
        if (p != null && entry?.kind == EntryKind.Youtube) {
            val dur = p.duration
            val pos = p.currentPosition
            val early = dur > 0L && pos < dur - EARLY_END_TOLERANCE_MS
            if (early && youtubeRetryId != entry.id) {
                Log.w(TAG, "YouTube ended early at ${pos}ms / ${dur}ms — re-resolving")
                youtubeRetryId = entry.id
                bridge.youtube.cancelPrefetch()
                playEntry(entry, pos.coerceAtLeast(0L), playWhenReady = true)
                return
            }
        }
        handleTrackEnded()
    }

    private fun tryRecoverYoutube(error: PlaybackException): Boolean {
        val entry = bridge.currentEntry ?: return false
        if (entry.kind != EntryKind.Youtube) return false
        if (youtubeRetryId == entry.id) return false
        if (!isRecoverableIoError(error)) return false
        youtubeRetryId = entry.id
        val pos = player?.currentPosition?.coerceAtLeast(0L) ?: bridge.ui.value.positionMs
        Log.w(TAG, "Recoverable YouTube error (${error.errorCodeName}) — re-resolving at ${pos}ms")
        bridge.youtube.cancelPrefetch()
        bridge.setError(null)
        playEntry(entry, pos, playWhenReady = true)
        return true
    }

    private fun isRecoverableIoError(error: PlaybackException): Boolean =
        when (error.errorCode) {
            PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS,
            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
            PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND,
            PlaybackException.ERROR_CODE_IO_INVALID_HTTP_CONTENT_TYPE,
            PlaybackException.ERROR_CODE_IO_READ_POSITION_OUT_OF_RANGE,
            PlaybackException.ERROR_CODE_TIMEOUT,
            -> true
            else -> error.errorCode == PlaybackException.ERROR_CODE_UNSPECIFIED
        }

    private fun handleTrackEnded() {
        if (bridge.resolver.repeatMode == RepeatMode.One) {
            player?.seekTo(0)
            player?.play()
            return
        }
        playNextInternal()
    }

    private fun playNextInternal() {
        val entries = bridge.entries
        val next = bridge.resolver.resolveNext(
            entries,
            bridge.currentIndex,
            bridge.currentEntry?.id,
        )
        if (next == null) {
            bridge.setPlaying(false)
            return
        }
        playEntry(next)
    }

    private fun playPreviousInternal() {
        val entries = bridge.entries
        val prev = bridge.resolver.resolvePrevious(entries, bridge.currentIndex)
        if (prev != null) {
            playEntry(prev)
        } else {
            player?.seekTo(0)
        }
    }

    private fun playEntry(
        entry: PlaylistEntry,
        startPositionMs: Long = 0L,
        playWhenReady: Boolean = true,
    ) {
        playJob?.cancel()
        warmJob?.cancel()
        playJob = scope.launch {
            suppressEnded = true
            bridge.setError(null)
            bridge.setCurrent(entry)
            bridge.setPositionMs(startPositionMs.coerceAtLeast(0L))
            bridge.resolver.onNowPlayingChanged(entry, bridge.entries.size)
            try {
                val mediaItem = withContext(Dispatchers.IO) { buildMediaItem(entry) }
                val p = player ?: return@launch
                val startAt = startPositionMs.coerceAtLeast(0L)
                // Pass start position into setMediaItem — seekTo right after prepare is flaky.
                p.setMediaItem(mediaItem, startAt)
                p.prepare()
                p.playWhenReady = playWhenReady
                if (playWhenReady) {
                    p.play()
                }
                // Keep UI / prefs on the intended offset (player may still be buffering at 0).
                bridge.setPositionMs(startAt)
                suppressEnded = false
                if (playWhenReady) {
                    youtubeRetryId = null
                    unplayableSkipCascade = 0
                }
                bridge.persistSession(force = true)
                refreshNotification(forceForeground = playWhenReady)
                if (playWhenReady) {
                    scheduleWarmPrefetch()
                }
            } catch (t: CancellationException) {
                throw t
            } catch (t: Throwable) {
                suppressEnded = false
                if (playWhenReady && looksLikeUnavailable(t) && trySkipUnplayable(entry, t)) {
                    return@launch
                }
                Log.e(TAG, "Failed to play ${entry.id}: ${t.message}", t)
                bridge.setError(t.message ?: "Failed to play")
                // Do not auto-advance — leave the row selected so the error is visible.
            }
        }
    }

    /**
     * Desktop-aligned: unavailable / private / deleted YouTube rows should advance,
     * not park the player on an error banner.
     */
    private fun looksLikeUnavailable(t: Throwable): Boolean {
        val text = buildString {
            var cur: Throwable? = t
            var depth = 0
            while (cur != null && depth < 6) {
                cur.message?.let { append(it).append('\n') }
                cur = cur.cause
                depth++
            }
        }
        if (text.isBlank()) return false
        // NewPipe: Got error UNPLAYABLE: "This video is not available"
        if (text.contains("UNPLAYABLE", ignoreCase = true)) return true
        if (text.contains("Previously failed YouTube id", ignoreCase = true)) return true
        // Keep in sync with desktop PlaybackEngine.LooksLikeUnavailable (avoid "format not available").
        return text.contains("Video unavailable", ignoreCase = true) ||
            text.contains("This video is not available", ignoreCase = true) ||
            text.contains("video is unavailable", ignoreCase = true) ||
            text.contains("private video", ignoreCase = true) ||
            text.contains("deleted video", ignoreCase = true)
    }

    private fun trySkipUnplayable(entry: PlaylistEntry, t: Throwable): Boolean {
        val videoId = when (entry.kind) {
            EntryKind.Youtube -> entry.id
            else -> null
        }
        if (videoId != null) {
            bridge.youtube.markSkip(videoId)
        }
        unplayableSkipCascade++
        val limit = bridge.entries.size.coerceAtLeast(1)
        if (unplayableSkipCascade > limit) {
            Log.w(TAG, "Unplayable skip cascade exhausted (${unplayableSkipCascade}/$limit)")
            unplayableSkipCascade = 0
            return false
        }
        Log.w(TAG, "Skipping unplayable ${entry.id}: ${t.message}")
        bridge.setError(null)
        playNextInternal()
        return true
    }

    private suspend fun buildMediaItem(entry: PlaylistEntry): MediaItem {
        val uri = when (entry.kind) {
            EntryKind.Youtube -> {
                val resolved = bridge.youtube.resolve(entry)
                if (resolved.streamUrl.isBlank()) {
                    error("YouTube resolve returned empty URL for ${entry.id}")
                }
                resolved.streamUrl
            }
            EntryKind.Stream -> {
                if (!entry.url.startsWith("http://", ignoreCase = true) &&
                    !entry.url.startsWith("https://", ignoreCase = true)
                ) {
                    error("Invalid stream URL: ${entry.url}")
                }
                entry.url
            }
            EntryKind.Local -> {
                val u = entry.url.trim()
                when {
                    u.startsWith("content://", ignoreCase = true) -> u
                    u.startsWith("file:", ignoreCase = true) -> u
                    // Absolute Windows / Unix paths from desktop playlists are not playable here.
                    u.length >= 2 && u[1] == ':' ->
                        error("Local path from desktop is not available on Android: $u")
                    u.startsWith("/") ->
                        error("Local path is not available via SAF: $u")
                    else -> u
                }
            }
        }

        // Validate URI parses
        runCatching { Uri.parse(uri) }.getOrElse {
            error("Bad media URI: $uri")
        }

        val meta = MediaMetadata.Builder()
            .setTitle(entry.title)
            .setArtist(entry.channel)
            .build()
        return MediaItem.Builder()
            .setMediaId(entry.id)
            .setUri(uri)
            .setMediaMetadata(meta)
            .build()
    }

    private fun scheduleWarmPrefetch() {
        warmJob?.cancel()
        warmJob = scope.launch {
            val peek = bridge.resolver.peekNext(
                bridge.entries,
                bridge.currentIndex,
                bridge.currentEntry?.id,
            ) ?: return@launch
            if (peek.kind == EntryKind.Youtube) {
                withContext(Dispatchers.IO) {
                    bridge.youtube.prefetchBestEffort(peek)
                }
            }
        }
    }

    companion object {
        private const val TAG = "LyllyPlayback"
        private const val EARLY_END_TOLERANCE_MS = 1_500L
        private const val NOTIFICATION_ID = 0x11A7
        const val ACTION_PLAY_PAUSE = "com.lyllyplayer.app.action.PLAY_PAUSE"
        const val ACTION_NEXT = "com.lyllyplayer.app.action.NEXT"
        const val ACTION_PREV = "com.lyllyplayer.app.action.PREV"
        const val ACTION_STOP = "com.lyllyplayer.app.action.STOP"
    }
}
