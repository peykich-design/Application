package com.peykich.knowledge.ui.record

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.navigation.toRoute
import com.peykich.knowledge.ai.AiException
import com.peykich.knowledge.ai.AiProvider
import com.peykich.knowledge.ai.FormatRequest
import com.peykich.knowledge.ai.FormatResult
import com.peykich.knowledge.data.AppEvents
import com.peykich.knowledge.data.DraftStore
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.data.SettingsStore
import com.peykich.knowledge.domain.NoteDraft
import com.peykich.knowledge.domain.NotePlanner
import com.peykich.knowledge.domain.NoteSource
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.ui.RecordRoute
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.ZonedDateTime
import javax.inject.Inject

enum class Stage { INPUT, FORMATTING, PREVIEW, SAVING }

data class RecordUi(
    val type: NoteType,
    val text: String = "",
    val stage: Stage = Stage.INPUT,
    val result: FormatResult? = null,
    /** プレビューで編集できる欄。 */
    val title: String = "",
    val subfolder: String = "",
    val tags: String = "",
    val subfolders: List<String> = emptyList(),
    val error: String? = null,
    /** 整形に失敗したとき「原文のまま保存」を出す。 */
    val offerRawSave: Boolean = false,
    val confirmSend: Boolean = false,
    val savedId: String? = null,
)

@HiltViewModel
class RecordViewModel @Inject constructor(
    handle: SavedStateHandle,
    private val repo: NoteRepository,
    private val ai: AiProvider,
    private val settings: SettingsStore,
    private val drafts: DraftStore,
    private val events: AppEvents,
) : ViewModel() {
    private val type = NoteType.fromKey(handle.toRoute<RecordRoute>().type) ?: NoteType.WORK
    private val _ui = MutableStateFlow(RecordUi(type = type, text = drafts.get(type.key)))
    val ui: StateFlow<RecordUi> = _ui
    private var job: Job? = null

    init {
        viewModelScope.launch { _ui.update { it.copy(subfolders = repo.subfolders(type)) } }
    }

    fun onText(text: String) {
        _ui.update { it.copy(text = text, error = null, offerRawSave = false) }
        drafts.put(type.key, text)
    }

    /** ［整形して保存］。仕事カテゴリで確認が有効なら、まず確認ダイアログを出す。 */
    fun requestFormat() {
        if (_ui.value.text.isBlank()) return
        viewModelScope.launch {
            if (type == NoteType.WORK && settings.current().confirmWorkSend) {
                _ui.update { it.copy(confirmSend = true) }
            } else {
                format(null)
            }
        }
    }

    fun confirmSend(ok: Boolean) {
        _ui.update { it.copy(confirmSend = false) }
        if (ok) format(null)
    }

    /** instruction があれば「修正を指示」（前回の結果を直させる）。 */
    fun format(instruction: String?) {
        val state = _ui.value
        if (state.text.isBlank()) return
        job?.cancel()
        _ui.update { it.copy(stage = Stage.FORMATTING, error = null, offerRawSave = false) }
        job = viewModelScope.launch {
            try {
                val s = settings.current()
                val previous = if (instruction != null) state.result?.let { edited(state, it) } else null
                val r = ai.format(
                    FormatRequest(
                        type = type,
                        text = state.text,
                        subfolders = state.subfolders,
                        existingTags = repo.existingTags(),
                        now = ZonedDateTime.now(),
                        generalize = s.generalizeWork,
                        previous = previous,
                        instruction = instruction,
                    ),
                )
                _ui.update {
                    it.copy(
                        stage = Stage.PREVIEW,
                        result = r,
                        title = r.title,
                        subfolder = r.subfolder,
                        tags = r.tags.joinToString(", "),
                    )
                }
            } catch (e: AiException) {
                failed(e.message ?: "整形に失敗しました", backToPreview = instruction != null)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                failed("整形に失敗しました（${e.javaClass.simpleName}）", backToPreview = instruction != null)
            }
        }
    }

    private fun failed(message: String, backToPreview: Boolean) {
        _ui.update {
            it.copy(
                stage = if (backToPreview && it.result != null) Stage.PREVIEW else Stage.INPUT,
                error = message,
                offerRawSave = true,
            )
        }
    }

    fun cancelFormat() {
        job?.cancel()
        _ui.update { it.copy(stage = if (it.result != null) Stage.PREVIEW else Stage.INPUT) }
    }

    fun backToInput() = _ui.update { it.copy(stage = Stage.INPUT, error = null) }

    fun onTitle(v: String) = _ui.update { it.copy(title = v) }
    fun onSubfolder(v: String) = _ui.update { it.copy(subfolder = v) }
    fun onTags(v: String) = _ui.update { it.copy(tags = v) }

    fun saveFormatted() {
        val s = _ui.value
        val r = s.result ?: return
        val e = edited(s, r)
        save(
            NoteDraft(
                type = type,
                title = e.title,
                subfolder = e.subfolder,
                tags = e.tags,
                summary = e.summary,
                body = e.bodyMarkdown,
                source = NoteSource.MEMO,
            ),
        )
    }

    /** 整形せずに原文で保存する（formatted: false）。オフラインでもできる。 */
    fun saveRaw() {
        val text = _ui.value.text
        if (text.isBlank()) return
        save(NotePlanner.rawDraft(type, text, NoteSource.MEMO))
    }

    private fun save(draft: NoteDraft) {
        _ui.update { it.copy(stage = Stage.SAVING, error = null) }
        viewModelScope.launch {
            try {
                val id = repo.save(draft)
                drafts.clear(type.key)
                events.post(AppEvents.Message(if (type == NoteType.DIARY) "日記に追記しました" else "保存しました", id))
                _ui.update { it.copy(savedId = id) }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                _ui.update {
                    it.copy(stage = if (it.result != null) Stage.PREVIEW else Stage.INPUT, error = "保存できませんでした（${e.message ?: e.javaClass.simpleName}）")
                }
            }
        }
    }

    /** プレビューで書き換えたタイトル・サブフォルダ・タグを整形結果に反映する。 */
    private fun edited(s: RecordUi, r: FormatResult): FormatResult {
        val title = s.title.trim().ifEmpty { r.title }
        val body = if (type != NoteType.DIARY && title != r.title) retitle(r.bodyMarkdown, title) else r.bodyMarkdown
        return r.copy(
            title = title,
            subfolder = s.subfolder.trim(),
            tags = s.tags.split(',', '、', '，').map { it.trim() }.filter { it.isNotEmpty() },
            bodyMarkdown = body,
        )
    }

    /** 本文先頭の「# タイトル」をプレビューで変えたタイトルに合わせる。 */
    private fun retitle(body: String, title: String): String {
        val trimmed = body.trimStart()
        return if (trimmed.startsWith("# ")) "# $title\n" + trimmed.substringAfter('\n', "") else body
    }
}
