package com.peykich.knowledge.domain

import java.time.ZonedDateTime

/**
 * Vault のフォルダ構成（仕様書 3.1）。パスは Vault ルートからの相対パスで、区切りは「/」。
 *   00_index.md / work/<サブフォルダ>/YYYY-MM-DD_タイトル.md / diary/YYYY/MM/YYYY-MM-DD.md
 */
object VaultLayout {
    const val ROOT_INDEX = "00_index.md"
    const val CATEGORY_INDEX = "index.md"
    const val UNSORTED = "未分類"

    fun categoryIndex(type: NoteType) = "${type.key}/$CATEGORY_INDEX"

    fun isIndex(path: String): Boolean =
        path == ROOT_INDEX || NoteType.entries.any { path == categoryIndex(it) }

    /** 「.」で始まるファイル・フォルダ（.obsidian/ .trash/ など）は対象外。 */
    fun isIgnored(path: String): Boolean = path.split('/').any { it.startsWith(".") }

    fun isNote(path: String): Boolean =
        path.endsWith(".md") && !isIndex(path) && !isIgnored(path) && typeOf(path) != null

    fun typeOf(path: String): NoteType? = NoteType.fromKey(path.substringBefore('/', ""))

    /** 仕事・趣味はサブフォルダ名、日記は「2026/10」。カテゴリ直下のノートは空文字。 */
    fun subfolderOf(path: String): String {
        val parts = path.split('/')
        return if (parts.size <= 2) "" else parts.subList(1, parts.size - 1).joinToString("/")
    }

    fun stem(path: String): String = path.substringAfterLast('/').removeSuffix(".md")

    fun notePath(type: NoteType, subfolder: String, fileName: String): String {
        val sub = FileNames.sanitizeFolder(subfolder)
        return "${type.key}/$sub/$fileName"
    }

    fun diaryPath(t: ZonedDateTime): String {
        val day = Dates.day(t)
        return "diary/%04d/%02d/%s.md".format(t.year, t.monthValue, day)
    }
}

object FileNames {
    private val REPLACE = mapOf(
        '\\' to '＼', '/' to '／', ':' to '：', '*' to '＊', '?' to '？', '"' to '＂',
        '<' to '＜', '>' to '＞', '|' to '｜',
        // Obsidian のリンク（[[...]] や #見出し、^ブロック）を壊す文字
        '#' to '＃', '^' to '＾', '[' to '［', ']' to '］',
    )
    const val MAX_TITLE = 40

    /** ファイル名に使えない文字を全角に置き換え、40文字で切る。 */
    fun sanitizeTitle(title: String, max: Int = MAX_TITLE): String {
        val replaced = title.map { REPLACE[it] ?: if (it.isISOControl()) ' ' else it }.joinToString("")
        val collapsed = replaced.replace(Regex("\\s+"), " ").trim().trimStart('.')
        val cut = takeCodePoints(collapsed, max).trim()
        return cut.ifEmpty { "無題" }
    }

    fun sanitizeFolder(name: String): String {
        val s = sanitizeTitle(name, 30)
        return if (s == "無題" || name.isBlank()) VaultLayout.UNSORTED else s
    }

    /** 2026-10-25_タイトル */
    fun noteBaseName(day: String, title: String) = "${day}_${sanitizeTitle(title)}"

    /** 重複したら _2, _3 … を付ける。exists は Vault 相対パスで聞く。 */
    fun uniquePath(dir: String, baseName: String, exists: (String) -> Boolean): String {
        val first = "$dir/$baseName.md"
        if (!exists(first)) return first
        var n = 2
        while (true) {
            val p = "$dir/${baseName}_$n.md"
            if (!exists(p)) return p
            n++
        }
    }

    private fun takeCodePoints(s: String, max: Int): String {
        val count = s.codePointCount(0, s.length)
        if (count <= max) return s
        return s.substring(0, s.offsetByCodePoints(0, max))
    }
}
