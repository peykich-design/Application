package com.peykich.knowledge.ui

import android.widget.TextView
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import io.noties.markwon.Markwon
import io.noties.markwon.ext.strikethrough.StrikethroughPlugin
import io.noties.markwon.ext.tasklist.TaskListPlugin

/** Markdown を表示する（Markwon）。見出し・リスト・コード・リンク・チェックボックスに対応。 */
@Composable
fun MarkdownText(markdown: String, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val textColor = MaterialTheme.colorScheme.onSurface.toArgb()
    val linkColor = MaterialTheme.colorScheme.primary.toArgb()
    val markwon = remember(context) {
        Markwon.builder(context)
            .usePlugin(StrikethroughPlugin.create())
            .usePlugin(TaskListPlugin.create(context))
            .build()
    }
    AndroidView(
        modifier = modifier,
        factory = { TextView(it).apply { textSize = 16f; setLineSpacing(0f, 1.15f) } },
        update = {
            it.setTextColor(textColor)
            it.setLinkTextColor(linkColor)
            markwon.setMarkdown(it, markdown)
        },
    )
}
