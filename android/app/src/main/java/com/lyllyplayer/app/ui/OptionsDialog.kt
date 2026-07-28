package com.lyllyplayer.app.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.RadioButtonDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.lyllyplayer.app.playlist.PlaylistOpenMode

@Composable
fun OptionsDialog(
    openMode: PlaylistOpenMode,
    onOpenModeChange: (PlaylistOpenMode) -> Unit,
    spectrumEnabled: Boolean,
    onSpectrumEnabledChange: (Boolean) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                text = "Options",
                color = MaterialTheme.colorScheme.onSurface,
            )
        },
        text = {
            Column(modifier = Modifier.fillMaxWidth()) {
                Text(
                    text = "When opening a playlist",
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
                OpenModeRow(
                    label = "Replace",
                    selected = openMode == PlaylistOpenMode.Replace,
                    onClick = { onOpenModeChange(PlaylistOpenMode.Replace) },
                )
                OpenModeRow(
                    label = "Append",
                    selected = openMode == PlaylistOpenMode.Append,
                    onClick = { onOpenModeChange(PlaylistOpenMode.Append) },
                )

                Text(
                    text = "Spectrum visualizer",
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.padding(top = 16.dp, bottom = 8.dp),
                )
                OpenModeRow(
                    label = "On",
                    selected = spectrumEnabled,
                    onClick = { onSpectrumEnabledChange(true) },
                )
                OpenModeRow(
                    label = "Off",
                    selected = !spectrumEnabled,
                    onClick = { onSpectrumEnabledChange(false) },
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text("Done")
            }
        },
    )
}

@Composable
private fun OpenModeRow(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .selectable(
                selected = selected,
                onClick = onClick,
                role = Role.RadioButton,
            )
            .padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(
            selected = selected,
            onClick = onClick,
            colors = RadioButtonDefaults.colors(
                selectedColor = MaterialTheme.colorScheme.primary,
                unselectedColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
            ),
        )
        Text(
            text = label,
            color = MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.padding(start = 4.dp),
        )
    }
}
