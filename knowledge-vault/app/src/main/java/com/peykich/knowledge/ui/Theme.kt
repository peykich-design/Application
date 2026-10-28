package com.peykich.knowledge.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import com.peykich.knowledge.domain.NoteType

private val Light = lightColorScheme(primary = Color(0xFF3F5AA8), secondary = Color(0xFF5B6B8C))
private val Dark = darkColorScheme(primary = Color(0xFFB4C5FF), secondary = Color(0xFFBFC6DC))

@Composable
fun KnowledgeTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val context = LocalContext.current
    val scheme = when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> Dark
        else -> Light
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

/** カテゴリごとの色（ボタンと一覧の目印）。 */
fun NoteType.accent(dark: Boolean): Color = when (this) {
    NoteType.WORK -> if (dark) Color(0xFF8FB4FF) else Color(0xFF2F5BB7)
    NoteType.HOBBY -> if (dark) Color(0xFF7FD8A6) else Color(0xFF1F8A55)
    NoteType.DIARY -> if (dark) Color(0xFFFFB88A) else Color(0xFFC0611E)
}
