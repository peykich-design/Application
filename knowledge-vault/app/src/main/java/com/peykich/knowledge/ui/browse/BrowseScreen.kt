package com.peykich.knowledge.ui.browse

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.peykich.knowledge.data.NoteEntity
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.domain.VaultLayout
import com.peykich.knowledge.ui.NoteRow
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import javax.inject.Inject

@HiltViewModel
class BrowseViewModel @Inject constructor(repo: NoteRepository) : ViewModel() {
    val notes: StateFlow<List<NoteEntity>> =
        repo.observeAll().stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
}

/** カテゴリのタブ → サブフォルダごとのノート一覧（日記は月ごと）。日記のカレンダー表示は今後追加。 */
@Composable
fun BrowseScreen(onOpen: (String) -> Unit, vm: BrowseViewModel = hiltViewModel()) {
    val all by vm.notes.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val type = NoteType.entries[tab]

    Column(Modifier.fillMaxSize()) {
        PrimaryTabRow(selectedTabIndex = tab) {
            NoteType.entries.forEachIndexed { i, t ->
                Tab(selected = tab == i, onClick = { tab = i }, text = { Text(t.label) })
            }
        }
        val mine = all.filter { it.noteType == type }
        val groups = mine.groupBy { VaultLayout.subfolderOf(it.path) }
        val keys = if (type == NoteType.DIARY) groups.keys.sortedDescending() else groups.keys.sorted()
        LazyColumn(Modifier.fillMaxSize()) {
            if (mine.isEmpty()) {
                item { Text("まだノートがありません", Modifier.padding(16.dp), color = MaterialTheme.colorScheme.outline) }
            }
            for (k in keys) {
                item(key = "h:$k") {
                    Text(
                        "${k.ifEmpty { "（カテゴリ直下）" }}（${groups.getValue(k).size}）",
                        style = MaterialTheme.typography.titleSmall,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 4.dp),
                    )
                }
                items(groups.getValue(k), key = { it.id }) { NoteRow(it, onClick = { onOpen(it.id) }, showType = false) }
            }
        }
    }
}
