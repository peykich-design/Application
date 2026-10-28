package com.peykich.knowledge.ui.record

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.speech.RecognizerIntent
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Mic
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.ui.MarkdownText

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RecordScreen(onClose: () -> Unit, onSettings: () -> Unit, vm: RecordViewModel = hiltViewModel()) {
    val ui by vm.ui.collectAsStateWithLifecycle()
    var showRevise by remember { mutableStateOf(false) }

    LaunchedEffect(ui.savedId) { if (ui.savedId != null) onClose() }
    BackHandler(enabled = ui.stage == Stage.PREVIEW || ui.stage == Stage.FORMATTING) {
        if (ui.stage == Stage.FORMATTING) vm.cancelFormat() else vm.backToInput()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(if (ui.stage == Stage.PREVIEW) "プレビュー（${ui.type.label}）" else "${ui.type.label}の記録") },
                navigationIcon = {
                    IconButton(onClick = {
                        when (ui.stage) {
                            Stage.PREVIEW -> vm.backToInput()
                            Stage.FORMATTING -> vm.cancelFormat()
                            else -> onClose()
                        }
                    }) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "戻る") }
                },
            )
        },
    ) { padding ->
        Box(Modifier.padding(padding).fillMaxSize().imePadding()) {
            when (ui.stage) {
                Stage.INPUT -> InputContent(ui, vm, onSettings)
                Stage.FORMATTING -> Busy("AIで整形しています…", onCancel = vm::cancelFormat)
                Stage.SAVING -> Busy("保存しています…", onCancel = null)
                Stage.PREVIEW -> PreviewContent(ui, vm, onRevise = { showRevise = true })
            }
        }
    }

    if (ui.confirmSend) {
        AlertDialog(
            onDismissRequest = { vm.confirmSend(false) },
            title = { Text("Gemini に送信します") },
            text = { Text("仕事の内容を外部のAI（Gemini）に送ります。勤務先のルールで送ってよい内容か確認してください。") },
            confirmButton = { TextButton(onClick = { vm.confirmSend(true) }) { Text("送信する") } },
            dismissButton = { TextButton(onClick = { vm.confirmSend(false) }) { Text("やめる") } },
        )
    }
    if (showRevise) {
        ReviseDialog(onDismiss = { showRevise = false }, onSend = { showRevise = false; vm.format(it) })
    }
}

@Composable
private fun InputContent(ui: RecordUi, vm: RecordViewModel, onSettings: () -> Unit) {
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val context = LocalContext.current
    // カーソルを末尾に置くため TextFieldValue で持つ（下書きを復元したとき）
    var field by remember { mutableStateOf(TextFieldValue(ui.text, TextRange(ui.text.length))) }

    val speech = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { res ->
        if (res.resultCode == Activity.RESULT_OK) {
            res.data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()?.let { spoken ->
                val cur = field.text
                val next = if (cur.isBlank()) spoken else cur.trimEnd() + "\n" + spoken
                field = TextFieldValue(next, TextRange(next.length))
                vm.onText(next)
            }
        }
    }
    var speechError by rememberSaveable { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        // 画面を開いたらすぐ入力できるように（仕様：1秒以内に入力開始）
        focus.requestFocus()
        keyboard?.show()
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 16.dp, vertical = 8.dp)) {
        OutlinedTextField(
            value = field,
            onValueChange = { field = it; vm.onText(it.text) },
            modifier = Modifier.fillMaxWidth().weight(1f).focusRequester(focus),
            placeholder = {
                Text(
                    when (ui.type) {
                        NoteType.WORK -> "やったこと・分かったことを書き捨てで（例：Excelで重複に色を付ける方法…）"
                        NoteType.HOBBY -> "趣味で分かったこと・試したことを書き捨てで"
                        NoteType.DIARY -> "今日の出来事を書く（同じ日の日記に追記されます）"
                    },
                )
            },
        )
        ui.error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp))
            if (it.contains("APIキー")) {
                TextButton(onClick = onSettings) { Text("設定を開く") }
            }
        }
        if (speechError) {
            Text("この端末では音声入力を使えません", color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp))
        }
        Row(
            Modifier.fillMaxWidth().padding(top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            FilledTonalIconButton(onClick = {
                val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                    .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                    .putExtra(RecognizerIntent.EXTRA_LANGUAGE, "ja-JP")
                try { speech.launch(intent); speechError = false } catch (_: ActivityNotFoundException) { speechError = true }
            }) { Icon(Icons.Outlined.Mic, contentDescription = "音声入力") }
            Spacer(Modifier.weight(1f))
            if (ui.offerRawSave) {
                OutlinedButton(onClick = vm::saveRaw, enabled = ui.text.isNotBlank()) { Text("原文のまま保存") }
            }
            Button(onClick = vm::requestFormat, enabled = ui.text.isNotBlank()) {
                Icon(Icons.Outlined.AutoAwesome, contentDescription = null, modifier = Modifier.padding(end = 6.dp))
                Text(if (ui.offerRawSave) "もう一度" else "整形して保存")
            }
        }
    }
}

@Composable
private fun PreviewContent(ui: RecordUi, vm: RecordViewModel, onRevise: () -> Unit) {
    val result = ui.result ?: return
    Column(Modifier.fillMaxSize()) {
        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            if (ui.type != NoteType.DIARY) {
                OutlinedTextField(ui.title, vm::onTitle, label = { Text("タイトル") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(ui.subfolder, vm::onSubfolder, label = { Text("サブフォルダ") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                if (ui.subfolders.isNotEmpty()) {
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        for (s in ui.subfolders) {
                            FilterChip(selected = s == ui.subfolder.trim(), onClick = { vm.onSubfolder(s) }, label = { Text(s) })
                        }
                    }
                }
            }
            OutlinedTextField(ui.tags, vm::onTags, label = { Text("タグ（カンマ区切り）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Text("要約：" + result.summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Card(Modifier.fillMaxWidth()) {
                MarkdownText(result.bodyMarkdown, Modifier.padding(16.dp).fillMaxWidth())
            }
            ui.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            Spacer(Modifier.height(8.dp))
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedButton(onClick = onRevise, modifier = Modifier.weight(1f)) { Text("修正を指示") }
            OutlinedButton(onClick = vm::saveRaw, modifier = Modifier.weight(1f)) { Text("原文で保存") }
            Button(onClick = vm::saveFormatted, modifier = Modifier.weight(1f)) { Text("保存") }
        }
    }
}

@Composable
private fun Busy(message: String, onCancel: (() -> Unit)?) {
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        CircularProgressIndicator()
        Text(message, modifier = Modifier.padding(16.dp))
        if (onCancel != null) TextButton(onClick = onCancel) { Text("キャンセル") }
    }
}

@Composable
private fun ReviseDialog(onDismiss: () -> Unit, onSend: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("修正を指示") },
        text = {
            OutlinedTextField(
                text, { text = it },
                placeholder = { Text("例：手順を番号付きにして／タイトルをもっと短く") },
                modifier = Modifier.fillMaxWidth(),
            )
        },
        confirmButton = { TextButton(onClick = { onSend(text) }, enabled = text.isNotBlank()) { Text("整形し直す") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("やめる") } },
    )
}
