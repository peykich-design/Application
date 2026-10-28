package com.peykich.knowledge.domain

import java.time.OffsetDateTime

/** index.md に載せる1件分。 */
data class IndexEntry(
    val path: String,
    val type: NoteType,
    val title: String,
    val summary: String,
    val tags: List<String>,
    val updated: String,
)

/** index.md と 00_index.md を作る（仕様書 3.4）。中身は notes テーブルから毎回作り直す。 */
object IndexBuilder {
    const val HEADER = "<!-- このファイルはアプリが自動生成します。手で編集しないでください -->"
    const val RECENT_COUNT = 20

    private val byUpdatedDesc = Comparator<IndexEntry> { a, b ->
        val ta = Dates.parseIso(a.updated)
        val tb = Dates.parseIso(b.updated)
        when {
            ta != null && tb != null -> tb.compareTo(ta)
            else -> b.updated.compareTo(a.updated)
        }
    }.thenBy { it.path }

    fun category(type: NoteType, entries: List<IndexEntry>): String = buildString {
        append(HEADER).append("\n# ").append(type.label).append(" index\n")
        val mine = entries.filter { it.type == type }
        if (mine.isEmpty()) {
            append("\nまだノートがありません。\n")
            return@buildString
        }
        val groups = mine.groupBy { VaultLayout.subfolderOf(it.path) }
        // 日記は新しい月が上、それ以外はサブフォルダ名の順
        val keys = if (type == NoteType.DIARY) groups.keys.sortedDescending() else groups.keys.sorted()
        for (key in keys) {
            append("\n## ").append(key.ifEmpty { "（カテゴリ直下）" }).append('\n')
            for (e in groups.getValue(key).sortedWith(byUpdatedDesc)) {
                append(line(e)).append('\n')
            }
        }
    }

    fun root(entries: List<IndexEntry>): String = buildString {
        append(HEADER).append("\n# ナレッジ index\n\n## カテゴリ\n")
        for (t in NoteType.entries) {
            val n = entries.count { it.type == t }
            append("- [[").append(VaultLayout.categoryIndex(t).removeSuffix(".md")).append('|').append(t.label)
                .append("]] — ").append(n).append("件\n")
        }
        append("\n## 最近更新したノート\n")
        val recent = entries.sortedWith(byUpdatedDesc).take(RECENT_COUNT)
        if (recent.isEmpty()) append("\nまだノートがありません。\n")
        for (e in recent) {
            append(line(e)).append("（").append(e.type.label)
            OffsetDateTimeOrNull(e.updated)?.let { append(" / ").append(it.toLocalDate()) }
            append("）\n")
        }
    }

    fun line(e: IndexEntry): String = buildString {
        append("- [[").append(VaultLayout.stem(e.path)).append("]]")
        if (e.summary.isNotBlank()) append(" — ").append(e.summary.replace('\n', ' ').trim())
        val tags = e.tags.map { it.trim().replace(Regex("\\s+"), "_") }.filter { it.isNotEmpty() }
        if (tags.isNotEmpty()) append("  ").append(tags.joinToString(" ") { "#$it" })
    }

    @Suppress("FunctionName")
    private fun OffsetDateTimeOrNull(s: String): OffsetDateTime? = Dates.parseIso(s)
}
