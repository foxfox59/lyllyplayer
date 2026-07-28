package com.lyllyplayer.app.playback

import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.Player
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Exposes always-on next/prev for the media notification / system session,
 * while our app owns the real playlist (one ExoPlayer item at a time).
 */
class PlaylistNavPlayer(
    player: Player,
    private val onSkipToNext: () -> Unit,
    private val onSkipToPrevious: () -> Unit,
) : ForwardingPlayer(player) {

    private val wrappers = CopyOnWriteArrayList<Pair<Player.Listener, Player.Listener>>()

    override fun getAvailableCommands(): Player.Commands =
        super.getAvailableCommands()
            .buildUpon()
            .add(COMMAND_PLAY_PAUSE)
            .add(COMMAND_SEEK_TO_NEXT)
            .add(COMMAND_SEEK_TO_PREVIOUS)
            .add(COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
            .add(COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM)
            .build()

    override fun isCommandAvailable(command: Int): Boolean =
        availableCommands.contains(command)

    override fun seekToNext() = onSkipToNext()

    override fun seekToNextMediaItem() = onSkipToNext()

    override fun seekToPrevious() = onSkipToPrevious()

    override fun seekToPreviousMediaItem() = onSkipToPrevious()

    override fun hasNextMediaItem(): Boolean = true

    override fun hasPreviousMediaItem(): Boolean = true

    override fun addListener(listener: Player.Listener) {
        val wrapper = CommandsListener(listener)
        wrappers.add(listener to wrapper)
        super.addListener(wrapper)
    }

    override fun removeListener(listener: Player.Listener) {
        val index = wrappers.indexOfFirst { it.first === listener }
        if (index >= 0) {
            super.removeListener(wrappers.removeAt(index).second)
        } else {
            super.removeListener(listener)
        }
    }

    private inner class CommandsListener(
        private val delegate: Player.Listener,
    ) : Player.Listener by delegate {
        override fun onAvailableCommandsChanged(availableCommands: Player.Commands) {
            delegate.onAvailableCommandsChanged(this@PlaylistNavPlayer.availableCommands)
        }

        override fun onEvents(player: Player, events: Player.Events) {
            // Replace the inner player so controllers see this wrapper.
            delegate.onEvents(this@PlaylistNavPlayer, events)
        }
    }
}
