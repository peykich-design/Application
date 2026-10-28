package com.peykich.knowledge.ui.home

import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Book
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Work
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.peykich.knowledge.data.NoteEntity
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.data.SecretStore
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.ui.NoteRow
import com.peykich.knowledge.ui.accent
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class HomeViewModel @Inject constructor(
    private val repo: NoteRepository,
    secrets: SecretStore,
) : ViewModel() {
    val recent: StateFlow<List<NoteEntity>> =
        repo.recent(5).stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val hasKey: StateFlow<Boolean> = secrets.hasKey

    init {
        // DB が空（初回・作り直し後）なら Vault から読み直す
        viewModelScope.launch { runCatching { repo.rebuildIfEmpty() } }
    }
}

@Composable
fun HomeScreen(
    onRecord: (NoteType) -> Unit,
    onSearch: () -> Unit,
    onOpen: (String) -> Unit,
    onSettings: () -> Unit,
    vm: HomeViewModel = hiltViewModel(),
) {
    val recent by vm.recent.collectAsStateWithLifecycle()
    val hasKey by vm.hasKey.collectAsStateWithLifecycle()
    val dark = isSystemInDarkTheme()

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                // 検索バー（タップで検索画面へ）
                Surface(
                    shape = RoundedCornerShape(28.dp),
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    modifier = Modifier.fillMaxWidth().height(56.dp).clickable(onClick = onSearch),
                ) {
                    Row(Modifier.padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.Search, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(12.dp))
                        Text("ノートを検索", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge)
                    }
                }

                if (!hasKey) {
                    Card(
                        onClick = onSettings,
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Outlined.Key, contentDescription = null)
                            Spacer(Modifier.width(12.dp))
                            Text("AI整形を使うには、設定で Gemini APIキーを入力してください（原文のまま保存はキー無しでもできます）")
                        }
                    }
                }

                Spacer(Modifier.height(4.dp))
                CategoryButton("仕事", "仕事の知見を記録", Icons.Outlined.Work, NoteType.WORK.accent(dark)) { onRecord(NoteType.WORK) }
                CategoryButton("趣味", "趣味の知見を記録", Icons.Outlined.Palette, NoteType.HOBBY.accent(dark)) { onRecord(NoteType.HOBBY) }
                CategoryButton("日記", "今日の日記に書く", Icons.Outlined.Book, NoteType.DIARY.accent(dark)) { onRecord(NoteType.DIARY) }

                Spacer(Modifier.height(8.dp))
                Text("最近の記録", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (recent.isEmpty()) {
            item {
                Text(
                    "まだ記録がありません。上のボタンから記録を始めましょう。",
                    modifier = Modifier.padding(horizontal = 16.dp),
                    color = MaterialTheme.colorScheme.outline,
                )
            }
        }
        items(recent, key = { it.id }) { NoteRow(it, onClick = { onOpen(it.id) }) }
        item { Spacer(Modifier.height(16.dp)) }
    }
}

@Composable
private fun CategoryButton(label: String, sub: String, icon: ImageVector, color: Color, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth().height(84.dp),
        shape = RoundedCornerShape(20.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = color,
            contentColor = if (isSystemInDarkTheme()) Color(0xFF10131A) else Color.White,
        ),
    ) {
        Icon(icon, contentDescription = null, modifier = Modifier.padding(end = 16.dp))
        Column(Modifier.weight(1f)) {
            Text(label, fontSize = 22.sp, fontWeight = FontWeight.Bold)
            Text(sub, style = MaterialTheme.typography.bodySmall)
        }
    }
}
