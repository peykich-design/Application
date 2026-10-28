package com.peykich.knowledge.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.peykich.knowledge.data.AppEvents
import com.peykich.knowledge.data.AppSettings
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.data.SecretStore
import com.peykich.knowledge.data.SettingsStore
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val store: SettingsStore,
    private val secrets: SecretStore,
    private val repo: NoteRepository,
    private val events: AppEvents,
) : ViewModel() {
    val settings: StateFlow<AppSettings?> = store.settings.stateIn(viewModelScope, SharingStarted.Eagerly, null)
    val hasKey: StateFlow<Boolean> = secrets.hasKey
    val noteCount = MutableStateFlow<Int?>(null)
    val busy = MutableStateFlow(false)

    init { refreshCount() }

    fun keyHint(): String? = secrets.keyHint()

    fun saveKey(key: String) {
        if (key.isBlank()) return
        secrets.saveGeminiKey(key)
        events.post(AppEvents.Message("APIキーを保存しました"))
    }

    fun clearKey() {
        secrets.clear()
        events.post(AppEvents.Message("APIキーを削除しました"))
    }

    fun update(transform: (AppSettings) -> AppSettings) {
        viewModelScope.launch { store.update(transform) }
    }

    fun rebuild() {
        viewModelScope.launch {
            busy.value = true
            val n = runCatching { repo.rebuildFromVault() }
            busy.value = false
            refreshCount()
            events.post(AppEvents.Message(n.fold({ "Vault を読み直しました（$it 件）" }, { "読み直しに失敗しました" })))
        }
    }

    private fun refreshCount() {
        viewModelScope.launch { noteCount.value = repo.count() }
    }
}

@Composable
fun SettingsScreen(vm: SettingsViewModel = hiltViewModel()) {
    val settings by vm.settings.collectAsStateWithLifecycle()
    val hasKey by vm.hasKey.collectAsStateWithLifecycle()
    val count by vm.noteCount.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val s = settings ?: return

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("設定", style = MaterialTheme.typography.headlineSmall)

        Section("Gemini APIキー")
        var key by rememberSaveable { mutableStateOf("") }
        Text(
            if (hasKey) "設定済み（末尾 ${vm.keyHint() ?: "????"}）" else "未設定",
            color = if (hasKey) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.error,
        )
        OutlinedTextField(
            value = key,
            onValueChange = { key = it },
            label = { Text(if (hasKey) "新しいキーに変える" else "APIキーを貼り付け") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            modifier = Modifier.fillMaxWidth(),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = { vm.saveKey(key); key = "" }, enabled = key.isNotBlank()) { Text("保存") }
            if (hasKey) OutlinedButton(onClick = vm::clearKey) { Text("削除") }
        }
        Text(
            "キーは Google AI Studio で作れます。無料枠では入力内容が Google の製品改善に使われるため、有料の設定で使ってください。キーは端末内で暗号化して保存します。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        Section("モデル名")
        ModelField("整形・要約", s.formatModel) { v -> vm.update { it.copy(formatModel = v) } }
        ModelField("検索の選定", s.selectModel) { v -> vm.update { it.copy(selectModel = v) } }
        ModelField("検索の回答", s.answerModel) { v -> vm.update { it.copy(answerModel = v) } }
        Text("空にすると既定（${AppSettings.DEFAULT_MODEL}）に戻ります。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

        Section("仕事ノート")
        SwitchRow("固有名詞を一般化する", "会社名・顧客名・個人名を「A社」「Cさん」などに置き換えて整形", s.generalizeWork) { v ->
            vm.update { it.copy(generalizeWork = v) }
        }
        SwitchRow("送信前に確認する", "仕事カテゴリでAIに送る前に確認ダイアログを出す", s.confirmWorkSend) { v ->
            vm.update { it.copy(confirmWorkSend = v) }
        }

        Section("Vault")
        Text("ノート ${count ?: "-"} 件（端末内に保存。Google Drive との同期は今後追加します）")
        OutlinedButton(onClick = vm::rebuild, enabled = !busy) { Text(if (busy) "読み直し中…" else "Vault を読み直して index を作り直す") }
    }
}

@Composable
private fun Section(title: String) {
    HorizontalDivider()
    Text(title, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
}

/** 1文字ごとに保存する。空のまま保存すると既定値になる（SettingsStore.update）。 */
@Composable
private fun ModelField(label: String, value: String, onChange: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf(value) }
    OutlinedTextField(
        value = text,
        onValueChange = { text = it; onChange(it) },
        label = { Text(label) },
        singleLine = true,
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun SwitchRow(title: String, sub: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title)
            Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}
