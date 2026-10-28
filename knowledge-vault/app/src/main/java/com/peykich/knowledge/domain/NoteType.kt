package com.peykich.knowledge.domain

/** ノートのカテゴリ。key は frontmatter の type とフォルダ名に使う。 */
enum class NoteType(val key: String, val label: String) {
    WORK("work", "仕事"),
    HOBBY("hobby", "趣味"),
    DIARY("diary", "日記");

    companion object {
        fun fromKey(key: String?): NoteType? = entries.firstOrNull { it.key == key?.trim()?.lowercase() }
    }
}

/** frontmatter の source。 */
enum class NoteSource(val key: String) {
    MEMO("memo"), CHAT("chat"), EDIT("edit"), EXTERNAL("external");

    companion object {
        fun fromKey(key: String?): NoteSource = entries.firstOrNull { it.key == key?.trim() } ?: EXTERNAL
    }
}
