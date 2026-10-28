package com.peykich.knowledge.ui.note

import android.content.Intent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import androidx.navigation.toRoute
import com.peykich.knowledge.data.LoadedNote
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.data.noteType
import com.peykich.knowledge.data.tagList
import com.peykich.knowledge.domain.NotePlanner
import com.peykich.knowledge.ui.MarkdownText
import com.peykich.knowledge.ui.NoteRoute
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import javax.inject.Inject

sealed interface NoteState {
    data object Loading : NoteState
    data object Missing : NoteState
    data class Loaded(val note: LoadedNote) : NoteState
}

@HiltViewModel
class NoteViewModel @Inject constructor(handle: SavedStateHandle, repo: NoteRepository) : ViewModel() {
    private val id = handle.toRoute<NoteRoute>().id

    /** DB の行が変わったら（保存し直しなど）読み直す。 */
    val state: StateFlow<NoteState> = repo.observe(id)
        .map { entity -> if (entity == null) NoteState.Missing else repo.load(id)?.let { NoteState.Loaded(it) } ?: NoteState.Missing }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), NoteState.Loading)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NoteScreen(onBack: () -> Unit, vm: NoteViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val loaded = (state as? NoteState.Loaded)?.note

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(loaded?.entity?.noteType?.label ?: "") },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "戻る") } },
                actions = {
                    if (loaded != null) {
                        IconButton(onClick = {
                            val send = Intent(Intent.ACTION_SEND)
                                .setType("text/plain")
                                .putExtra(Intent.EXTRA_SUBJECT, loaded.entity.title)
                                .putExtra(Intent.EXTRA_TEXT, loaded.raw)
                            context.startActivity(Intent.createChooser(send, "ノートを共有"))
                        }) { Icon(Icons.Outlined.Share, contentDescription = "共有") }
                    }
                },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
            when (val s = state) {
                NoteState.Loading -> {}
                NoteState.Missing -> Text("ノートが見つかりません（削除・移動された可能性があります）")
                is NoteState.Loaded -> NoteBodyView(s.note)
            }
        }
    }
}

@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun NoteBodyView(note: LoadedNote) {
    val e = note.entity
    // frontmatter は YAML のまま見せず、上部のヘッダーとして出す（仕様書 7.1）
    Text(e.title, style = MaterialTheme.typography.headlineSmall)
    if (e.tagList.isNotEmpty()) {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
            for (t in e.tagList) AssistChip(onClick = {}, label = { Text("#$t") })
        }
    }
    val dates = buildString {
        append("作成 ").append(e.created.take(16).replace('T', ' '))
        if (e.updated != e.created) append(" ・ 更新 ").append(e.updated.take(16).replace('T', ' '))
        if (!e.formatted) append(" ・ 未整形")
    }
    Text(dates, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.outline, modifier = Modifier.padding(top = 4.dp))
    Text(e.path, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.outline)
    HorizontalDivider(Modifier.padding(vertical = 12.dp))
    // 本文の「# タイトル」はヘッダーと重なるので省く
    MarkdownText(NotePlanner.stripTitleHeading(note.document.body).trim(), Modifier.fillMaxWidth())
}
