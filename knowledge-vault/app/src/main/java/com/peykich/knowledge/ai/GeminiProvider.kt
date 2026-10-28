package com.peykich.knowledge.ai

import com.peykich.knowledge.domain.NoteType
import kotlinx.serialization.json.JsonObject
import java.time.ZonedDateTime

/** Gemini を呼ぶための設定（呼び出しのたびに最新の値を読む）。 */
data class GeminiConfig(
    val apiKey: String?,
    val formatModel: String,
    val selectModel: String,
    val answerModel: String,
)

/** HTTP の送受信だけを受け持つ。テストではフェイクに差し替える。 */
fun interface GeminiTransport {
    /** 成功・失敗にかかわらず、ステータスコードと本文を返す。通信できないときは AiException を投げる。 */
    suspend fun post(url: String, apiKey: String, body: String, timeoutSeconds: Long): HttpResult
}

data class HttpResult(val code: Int, val body: String)

class GeminiProvider(
    private val transport: GeminiTransport,
    private val prompts: PromptBuilder,
    private val config: suspend () -> GeminiConfig,
    private val existingTags: suspend () -> List<String> = { emptyList() },
    private val clock: () -> ZonedDateTime = { ZonedDateTime.now() },
) : AiProvider {

    override suspend fun format(req: FormatRequest): FormatResult {
        val cfg = config()
        val text = call(
            cfg, cfg.formatModel, prompts.formatSystem(req),
            listOf(GeminiProtocol.Turn(true, prompts.formatUser(req))),
            GeminiProtocol.formatSchema, FORMAT_TIMEOUT,
        )
        val result = runCatching {
            GeminiProtocol.json.decodeFromJsonElement(FormatResult.serializer(), GeminiProtocol.extractJson(text))
        }.getOrElse { throw AiException("AIの応答の形式が正しくありませんでした", AiException.Kind.BAD_RESPONSE) }
        if (result.bodyMarkdown.isBlank()) throw AiException("AIの整形結果が空でした", AiException.Kind.BAD_RESPONSE)
        return result
    }

    override suspend fun interviewTurn(type: NoteType, history: List<ChatMessage>): String {
        val cfg = config()
        // 最初の質問を出させるときは、会話が空なので開始の合図を入れる
        val turns = history.map { GeminiProtocol.Turn(it.role == ChatMessage.Role.USER, it.text) }
            .ifEmpty { listOf(GeminiProtocol.Turn(true, "（記録を始めます。最初の質問をしてください）")) }
            .let { if (!it.first().user) listOf(GeminiProtocol.Turn(true, "（記録を始めます）")) + it else it }
        return call(cfg, cfg.formatModel, prompts.interviewSystem(type), turns, null, FORMAT_TIMEOUT).trim()
    }

    override suspend fun selectNotes(question: String, catalog: List<CatalogEntry>, history: List<ChatMessage>): List<String> {
        if (catalog.isEmpty()) return emptyList()
        val cfg = config()
        val turns = historyTurns(history) + GeminiProtocol.Turn(true, prompts.selectUser(question, catalog))
        val text = call(cfg, cfg.selectModel, prompts.selectSystem(clock()), turns, GeminiProtocol.selectSchema, SEARCH_TIMEOUT)
        val known = catalog.map { it.id }.toSet()
        return GeminiProtocol.parseIds(text).filter { it in known }.distinct().take(5)
    }

    override suspend fun answer(question: String, notes: List<NoteBody>, history: List<ChatMessage>): AnswerResult {
        val cfg = config()
        val turns = historyTurns(history) + GeminiProtocol.Turn(true, prompts.answerUser(question, notes))
        return AnswerResult(call(cfg, cfg.answerModel, prompts.answerSystem(), turns, null, SEARCH_TIMEOUT).trim())
    }

    override suspend fun editByInstruction(body: String, instruction: String): String {
        val cfg = config()
        val text = call(
            cfg, cfg.formatModel, prompts.editSystem(),
            listOf(GeminiProtocol.Turn(true, prompts.editUser(body, instruction))), null, FORMAT_TIMEOUT,
        )
        return text.trim().removePrefix("```markdown").removePrefix("```md").removeSurrounding("```").trim()
    }

    override suspend fun summarize(body: String, type: NoteType): SummaryResult {
        val cfg = config()
        val text = call(
            cfg, cfg.formatModel, prompts.summarizeSystem(),
            listOf(GeminiProtocol.Turn(true, prompts.summarizeUser(body, type, existingTags()))),
            GeminiProtocol.summarySchema, FORMAT_TIMEOUT,
        )
        return runCatching {
            GeminiProtocol.json.decodeFromJsonElement(SummaryResult.serializer(), GeminiProtocol.extractJson(text))
        }.getOrElse { throw AiException("AIの応答の形式が正しくありませんでした", AiException.Kind.BAD_RESPONSE) }
    }

    /** 検索の会話履歴は直近だけ渡す（長くなりすぎないように）。 */
    private fun historyTurns(history: List<ChatMessage>): List<GeminiProtocol.Turn> =
        history.takeLast(6).map { GeminiProtocol.Turn(it.role == ChatMessage.Role.USER, it.text) }
            .dropWhile { !it.user }

    private suspend fun call(
        cfg: GeminiConfig,
        model: String,
        system: String,
        turns: List<GeminiProtocol.Turn>,
        schema: JsonObject?,
        timeoutSeconds: Long,
    ): String {
        val key = cfg.apiKey?.takeIf { it.isNotBlank() }
            ?: throw AiException("Gemini APIキーが未設定です。設定画面で入力してください", AiException.Kind.NO_KEY)
        val body = GeminiProtocol.buildRequest(system, turns, schema).toString()
        val res = transport.post(GeminiProtocol.endpoint(model), key, body, timeoutSeconds)
        if (res.code !in 200..299) throw GeminiProtocol.httpError(res.code, res.body)
        return GeminiProtocol.parseText(res.body)
    }

    companion object {
        /** 整形は30秒で打ち切る（仕様書 5.6）。 */
        const val FORMAT_TIMEOUT = 30L
        const val SEARCH_TIMEOUT = 60L
    }
}
