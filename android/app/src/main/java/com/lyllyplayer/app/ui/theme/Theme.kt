package com.lyllyplayer.app.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.graphics.Color
import androidx.compose.material3.LocalContentColor

private val LightColors = lightColorScheme(
    primary = Color(0xFF2C4A3E),
    onPrimary = Color(0xFFF4F7F5),
    secondary = Color(0xFF5A7A6C),
    background = Color(0xFFE8EEE9),
    onBackground = Color(0xFF1A2420),
    surface = Color(0xFFF2F6F3),
    onSurface = Color(0xFF1A2420),
    surfaceVariant = Color(0xFFD5E0D9),
    onSurfaceVariant = Color(0xFF3E4A44),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFF9BB8A8),
    onPrimary = Color(0xFF102018),
    secondary = Color(0xFF8FA89B),
    background = Color(0xFF121816),
    onBackground = Color(0xFFE4EBE6),
    surface = Color(0xFF1A221E),
    onSurface = Color(0xFFE4EBE6),
    surfaceVariant = Color(0xFF2A3530),
    onSurfaceVariant = Color(0xFFB8C5BE),
)

@Composable
fun LyllyPlayerTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) DarkColors else LightColors
    MaterialTheme(colorScheme = colors) {
        CompositionLocalProvider(LocalContentColor provides colors.onSurface) {
            content()
        }
    }
}
