package com.peykich.knowledge.domain

import java.time.ZonedDateTime

/** 保存する内容（整形結果、または原文）。 */
data class NoteDraft(
    val type: NoteType,
    val title: String,
    val subfolder: String,
    val tags: List<String>,
    val summary: String,
    val body: String,
    val source: NoteSource,
    val formatted: Boolean = true,
)

/** 書き込むファイル1つ。 */
data class PlannedFile(val path: String, val content: String, val meta: NoteMeta)

/** ノートの保存先とファイル内容を決める（ファイル操作はしない）。 */
object NotePlanner {
    const val SUMMARY_MAX = 100

    /** 仕事・趣味：新しいファイルを作る。 */
    fun planNew(
        draft: NoteDraft,
        now: ZonedDateTime,
        exists: (String) -> Boolean,
        id: String = Ulid.generate(now.toInstant().toEpochMilli()),
    ): PlannedFile {
        require(draft.type != NoteType.DIARY) { "日記は planDiary を使う" }
        val title = draft.title.trim().ifEmpty { "無題" }
        val dir = "${draft.type.key}/${FileNames.sanitizeFolder(draft.subfolder)}"
        val path = FileNames.uniquePath(dir, FileNames.noteBaseName(Dates.day(now), title), exists)
        val stamp = Dates.iso(now)
        val meta = NoteMeta(
            id = id,
            type = draft.type,
            title = title,
            created = stamp,
            updated = stamp,
            tags = normalizeTags(draft.tags),
            summary = clipSummary(draft.summary),
            source = draft.source,
            formatted = draft.formatted,
        )
        val body = ensureTitleHeading(draft.body, title)
        return PlannedFile(path, Frontmatter.compose(meta, body), meta)
    }

    /**
     * 日記：その日のファイルが無ければ作り、あれば「## HH:MM」の節を末尾に足す。
     * summary は1日分の内容を表すものを呼び出し側で決めて渡す（null なら既存＋今回を連結）。
     */
    fun planDiary(
        draft: NoteDraft,
        now: ZonedDateTime,
        existing: NoteDocument?,
        summaryOverride: String? = null,
        id: String = Ulid.generate(now.toInstant().toEpochMilli()),
    ): PlannedFile {
        val path = VaultLayout.diaryPath(now)
        val section = stripTitleHeading(draft.body).trim()
        val body = diaryBody(existing?.body, now, section)
        val stamp = Dates.iso(now)
        val old = existing?.meta
        val summary = summaryOverride ?: when {
            old == null || old.summary.isBlank() -> draft.summary
            draft.summary.isBlank() -> old.summary
            else -> old.summary + " / " + draft.summary
        }
        val meta = NoteMeta(
            id = old?.id ?: id,
            type = NoteType.DIARY,
            title = Dates.diaryTitle(now),
            created = old?.created?.takeIf { it.isNotBlank() } ?: stamp,
            updated = stamp,
            tags = normalizeTags((old?.tags ?: emptyList()) + draft.tags),
            summary = clipSummary(summary),
            source = old?.source ?: draft.source,
            formatted = (old?.formatted ?: true) && draft.formatted,
        )
        return PlannedFile(path, Frontmatter.compose(meta, body, existing?.extra ?: emptyMap()), meta)
    }

    fun diaryBody(existingBody: String?, now: ZonedDateTime, section: String): String {
        val heading = "## ${Dates.hourMinute(now)}"
        val base = existingBody?.trim().orEmpty().ifEmpty { "# ${Dates.diaryTitle(now)}" }
        return "$base\n\n$heading\n\n$section\n"
    }

    /** 本文が「# タイトル」で始まっていなければ付ける。 */
    fun ensureTitleHeading(body: String, title: String): String {
        val trimmed = body.trim()
        return if (trimmed.startsWith("# ")) trimmed else "# $title\n\n$trimmed"
    }

    fun stripTitleHeading(body: String): String {
        val trimmed = body.trimStart()
        if (!trimmed.startsWith("# ")) return body
        return trimmed.substringAfter('\n', "")
    }

    fun normalizeTags(tags: List<String>): List<String> =
        tags.map { it.trim().removePrefix("#").trim() }.filter { it.isNotEmpty() }.distinct()

    fun clipSummary(s: String): String {
        val one = s.replace(Regex("\\s*\\n\\s*"), " ").trim()
        if (one.codePointCount(0, one.length) <= SUMMARY_MAX) return one
        return one.substring(0, one.offsetByCodePoints(0, SUMMARY_MAX - 1)) + "…"
    }

    /** 整形できなかったときの原文保存用（仕様書 5.6）。 */
    fun rawDraft(type: NoteType, text: String, source: NoteSource): NoteDraft {
        val firstLine = text.trim().lineSequence().firstOrNull { it.isNotBlank() }?.trim().orEmpty()
        val title = firstLine.removePrefix("#").trim().let {
            if (it.codePointCount(0, it.length) > 30) it.substring(0, it.offsetByCodePoints(0, 30)) else it
        }.ifEmpty { "メモ" }
        return NoteDraft(
            type = type,
            title = if (type == NoteType.DIARY) "" else title,
            subfolder = if (type == NoteType.DIARY) "" else VaultLayout.UNSORTED,
            tags = emptyList(),
            summary = clipSummary(text),
            body = if (type == NoteType.DIARY) text.trim() else "# $title\n\n${text.trim()}",
            source = source,
            formatted = false,
        )
    }
}
