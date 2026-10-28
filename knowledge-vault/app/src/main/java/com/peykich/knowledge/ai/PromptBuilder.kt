package com.peykich.knowledge.ai

import com.peykich.knowledge.domain.Dates
import com.peykich.knowledge.domain.NoteType
import java.time.ZonedDateTime

/** プロンプトの文面を読み込む（Android では assets/prompts フォルダの md ファイル）。 */
fun interface PromptSource {
    fun load(name: String): String
}

/** システムプロンプトと、AI に渡す入力文を組み立てる。 */
class PromptBuilder(private val source: PromptSource) {
    private val cache = HashMap<String, String>()

    private fun template(name: String): String = synchronized(cache) {
        cache.getOrPut(name) { source.load("$name.md").trim() }
    }

    fun formatSystem(req: FormatRequest): String {
        val base = template("format")
        return if (req.type == NoteType.WORK && req.generalize) base + "\n" + template("format_generalize") else base
    }

    fun formatUser(req: FormatRequest): String = buildString {
        append("カテゴリ: ").append(categoryLabel(req.type)).append('\n')
        append("現在日時: ").append(Dates.iso(req.now)).append('\n')
        if (req.type != NoteType.DIARY) {
            append("既存サブフォルダ: ").append(req.subfolders.joinToString(", ").ifEmpty { "（なし）" }).append('\n')
        }
        append("既存タグ: ").append(req.existingTags.take(MAX_TAGS).joinToString(", ").ifEmpty { "（なし）" }).append('\n')
        append("\n<input>\n").append(req.text.trim()).append("\n</input>\n")
        val prev = req.previous
        if (prev != null && !req.instruction.isNullOrBlank()) {
            append("\n<previous>\n")
            append(GeminiProtocol.json.encodeToString(FormatResult.serializer(), prev))
            append("\n</previous>\n<instruction>\n").append(req.instruction.trim()).append("\n</instruction>\n")
        }
    }

    fun interviewSystem(type: NoteType): String =
        template("interview").replace("{category}", categoryLabel(type))

    fun selectSystem(today: ZonedDateTime): String = template("select").replace("{today}", Dates.day(today))

    fun selectUser(question: String, catalog: List<CatalogEntry>): String = buildString {
        append("<catalog>\n")
        for (c in catalog) {
            append("- id: ").append(c.id)
                .append(" | type: ").append(c.type.key)
                .append(" | updated: ").append(c.updated.take(10))
                .append(" | title: ").append(c.title)
                .append(" | tags: ").append(c.tags.joinToString(","))
                .append(" | summary: ").append(c.summary.replace('\n', ' '))
                .append('\n')
        }
        append("</catalog>\n\n質問: ").append(question.trim())
    }

    fun answerSystem(): String = template("answer")

    /** 本文の合計は約3万字まで。超える分は末尾を省略する（仕様書 6.1）。 */
    fun answerUser(question: String, notes: List<NoteBody>, maxChars: Int = MAX_NOTE_CHARS): String = buildString {
        var remaining = maxChars
        notes.forEachIndexed { i, n ->
            val body = if (n.body.length > remaining) n.body.take(maxOf(0, remaining)) + "\n…（以下省略）" else n.body
            remaining -= minOf(n.body.length, remaining)
            append("<note number=\"").append(i + 1).append("\" title=\"").append(n.title.replace("\"", "'")).append("\">\n")
            append(body.trim()).append("\n</note>\n\n")
        }
        append("質問: ").append(question.trim())
    }

    fun editSystem(): String = template("edit")

    fun editUser(body: String, instruction: String): String =
        "<body>\n${body.trim()}\n</body>\n\n<instruction>\n${instruction.trim()}\n</instruction>"

    fun summarizeSystem(): String = template("summarize")

    fun summarizeUser(body: String, type: NoteType?, existingTags: List<String>): String = buildString {
        if (type != null) append("現在のカテゴリ: ").append(type.key).append('\n')
        append("既存タグ: ").append(existingTags.take(MAX_TAGS).joinToString(", ").ifEmpty { "（なし）" }).append('\n')
        append("\n<body>\n").append(body.trim().take(MAX_NOTE_CHARS)).append("\n</body>")
    }

    fun categoryLabel(type: NoteType) = when (type) {
        NoteType.WORK -> "仕事の知見"
        NoteType.HOBBY -> "趣味の知見"
        NoteType.DIARY -> "日記"
    }

    companion object {
        const val MAX_NOTE_CHARS = 30_000
        const val MAX_TAGS = 200
    }
}
