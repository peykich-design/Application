package com.peykich.knowledge.ai

import com.peykich.knowledge.domain.NoteType
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.time.ZonedDateTime

/**
 * AI 呼び出しの窓口（仕様書 9.1）。MVP は Gemini の実装だけ。
 * Claude などを足すときは、このインターフェースの実装を追加して設定で切り替える。
 */
interface AiProvider {
    suspend fun format(req: FormatRequest): FormatResult
    suspend fun interviewTurn(type: NoteType, history: List<ChatMessage>): String
    suspend fun selectNotes(question: String, catalog: List<CatalogEntry>, history: List<ChatMessage>): List<String>
    suspend fun answer(question: String, notes: List<NoteBody>, history: List<ChatMessage>): AnswerResult
    suspend fun editByInstruction(body: String, instruction: String): String
    suspend fun summarize(body: String, type: NoteType): SummaryResult
}

data class FormatRequest(
    val type: NoteType,
    /** 入力テキスト（チャットなら会話ログ）。 */
    val text: String,
    val subfolders: List<String>,
    val existingTags: List<String>,
    val now: ZonedDateTime,
    /** 仕事ノートの固有名詞を一般化するか（設定）。 */
    val generalize: Boolean,
    /** 「修正を指示」のとき：前回の整形結果と指示。 */
    val previous: FormatResult? = null,
    val instruction: String? = null,
)

@Serializable
data class FormatResult(
    val title: String = "",
    val subfolder: String = "",
    val tags: List<String> = emptyList(),
    val summary: String = "",
    @SerialName("body_markdown") val bodyMarkdown: String = "",
)

@Serializable
data class SummaryResult(
    val summary: String = "",
    val tags: List<String> = emptyList(),
    val type: String = "",
)

data class ChatMessage(val role: Role, val text: String) {
    enum class Role { USER, AI }
}

data class CatalogEntry(
    val id: String,
    val type: NoteType,
    val title: String,
    val summary: String,
    val tags: List<String>,
    val updated: String,
)

data class NoteBody(val id: String, val title: String, val body: String)

data class AnswerResult(val text: String)

/** 画面にそのまま出せる日本語のメッセージを持つ例外。 */
class AiException(message: String, val kind: Kind = Kind.OTHER) : Exception(message) {
    enum class Kind { NO_KEY, AUTH, RATE_LIMIT, TIMEOUT, NETWORK, BLOCKED, BAD_RESPONSE, SERVER, OTHER }
}
