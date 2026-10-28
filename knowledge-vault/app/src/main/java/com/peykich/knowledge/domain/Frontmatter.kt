package com.peykich.knowledge.domain

/** frontmatter の中身。仕様書 3.2。 */
data class NoteMeta(
    val id: String,
    val type: NoteType,
    val title: String,
    val created: String,
    val updated: String,
    val tags: List<String>,
    val summary: String,
    val source: NoteSource,
    /** 原文のまま保存したノートは false（後で再整形できる）。true のときは frontmatter に書かない。 */
    val formatted: Boolean = true,
)

/** md ファイル1つ。frontmatter が無いファイルは meta = null、raw に元の値を残す。 */
data class NoteDocument(
    val meta: NoteMeta?,
    val body: String,
    /** 読み取った frontmatter のキーと値（知らないキーを書き戻すときに使う）。 */
    val extra: Map<String, String> = emptyMap(),
)

/**
 * frontmatter の読み書き。YAML の全仕様ではなく、このアプリが書く形
 * （1行1キー、tags は [a, b] 形式か「- a」のリスト）と Obsidian でよく使う形だけを扱う。
 */
object Frontmatter {
    private val KNOWN = listOf("id", "type", "title", "created", "updated", "tags", "summary", "source", "formatted")

    fun render(meta: NoteMeta, extra: Map<String, String> = emptyMap()): String = buildString {
        append("---\n")
        append("id: ").append(meta.id).append('\n')
        append("type: ").append(meta.type.key).append('\n')
        append("title: ").append(quoteIfNeeded(meta.title)).append('\n')
        append("created: ").append(meta.created).append('\n')
        append("updated: ").append(meta.updated).append('\n')
        append("tags: [").append(meta.tags.joinToString(", ") { quoteIfNeeded(it) }).append("]\n")
        append("summary: ").append(quoteIfNeeded(meta.summary)).append('\n')
        append("source: ").append(meta.source.key).append('\n')
        if (!meta.formatted) append("formatted: false\n")
        for ((k, v) in extra) {
            if (k !in KNOWN) append(k).append(": ").append(v).append('\n')
        }
        append("---\n")
    }

    fun compose(meta: NoteMeta, body: String, extra: Map<String, String> = emptyMap()): String =
        render(meta, extra) + "\n" + body.trimStart('\n').trimEnd() + "\n"

    fun parse(text: String): NoteDocument {
        val normalized = text.replace("\r\n", "\n").removePrefix("﻿")
        if (!normalized.startsWith("---\n")) return NoteDocument(null, normalized)
        val end = Regex("^---[ \\t]*$", RegexOption.MULTILINE).findAll(normalized).drop(1).firstOrNull()
            ?: return NoteDocument(null, normalized)
        val yaml = normalized.substring(4, end.range.first)
        val body = normalized.substring(minOf(normalized.length, end.range.last + 1)).trimStart('\n')
        val map = parseYaml(yaml)
        val type = NoteType.fromKey(map["type"]?.let(::unquote))
        val id = map["id"]?.let(::unquote)?.takeIf { it.isNotBlank() }
        val summary = map["summary"]?.let(::unquote)
        val extra = map.filterKeys { it !in KNOWN }
        if (type == null || id == null || summary == null) {
            // 必須項目が足りない frontmatter は「無いもの」として扱い、後で補う
            return NoteDocument(null, body, map)
        }
        val meta = NoteMeta(
            id = id,
            type = type,
            title = map["title"]?.let(::unquote).orEmpty(),
            created = map["created"]?.let(::unquote).orEmpty(),
            updated = map["updated"]?.let(::unquote).orEmpty(),
            tags = parseList(map["tags"]),
            summary = summary,
            source = NoteSource.fromKey(map["source"]?.let(::unquote)),
            formatted = map["formatted"]?.let(::unquote)?.lowercase() != "false",
        )
        return NoteDocument(meta, body, extra)
    }

    /** 「key: value」の行と、直後の「  - item」の行（リスト）を読む。値は生の文字列のまま返す。 */
    private fun parseYaml(yaml: String): Map<String, String> {
        val result = LinkedHashMap<String, String>()
        var currentKey: String? = null
        val listItems = mutableListOf<String>()
        fun flushList() {
            val key = currentKey
            if (key != null && listItems.isNotEmpty()) {
                result[key] = "[" + listItems.joinToString(", ") + "]"
            }
            listItems.clear()
        }
        for (line in yaml.lines()) {
            if (line.isBlank() || line.trimStart().startsWith("#")) continue
            val trimmed = line.trim()
            if (trimmed.startsWith("- ") && currentKey != null && result[currentKey].isNullOrEmpty()) {
                listItems += trimmed.removePrefix("- ").trim()
                continue
            }
            val idx = line.indexOf(':')
            if (idx <= 0 || line.first().isWhitespace()) continue
            flushList()
            val key = line.substring(0, idx).trim()
            val value = line.substring(idx + 1).trim()
            result[key] = value
            currentKey = key
        }
        flushList()
        return result
    }

    fun parseList(raw: String?): List<String> {
        if (raw == null) return emptyList()
        val s = raw.trim()
        if (s.isEmpty()) return emptyList()
        val inner = if (s.startsWith("[") && s.endsWith("]")) s.substring(1, s.length - 1) else s
        return splitTopLevel(inner).map { unquote(it.trim()).removePrefix("#") }.filter { it.isNotBlank() }
    }

    private fun splitTopLevel(s: String): List<String> {
        val out = mutableListOf<String>()
        val cur = StringBuilder()
        var quote: Char? = null
        var i = 0
        while (i < s.length) {
            val c = s[i]
            when {
                quote != null -> {
                    cur.append(c)
                    if (c == '\\' && quote == '"' && i + 1 < s.length) { cur.append(s[i + 1]); i++ }
                    else if (c == quote) quote = null
                }
                c == '"' || c == '\'' -> { quote = c; cur.append(c) }
                c == ',' -> { out += cur.toString(); cur.clear() }
                else -> cur.append(c)
            }
            i++
        }
        out += cur.toString()
        return out
    }

    fun unquote(v: String): String {
        val s = v.trim()
        if (s.length >= 2 && s.startsWith("\"") && s.endsWith("\"")) {
            val inner = s.substring(1, s.length - 1)
            val sb = StringBuilder()
            var i = 0
            while (i < inner.length) {
                val c = inner[i]
                if (c == '\\' && i + 1 < inner.length) {
                    when (val n = inner[i + 1]) {
                        'n' -> sb.append('\n')
                        't' -> sb.append('\t')
                        else -> sb.append(n)
                    }
                    i += 2
                } else { sb.append(c); i++ }
            }
            return sb.toString()
        }
        if (s.length >= 2 && s.startsWith("'") && s.endsWith("'")) {
            return s.substring(1, s.length - 1).replace("''", "'")
        }
        return s
    }

    /** YAML で意味を持つ文字を含むときだけ "..." で囲む。 */
    fun quoteIfNeeded(v: String): String {
        val s = v.replace("\n", " ")
        val special = s.isEmpty() || s != s.trim() ||
            s.any { it in ":#[]{},&*!|>'\"%@`" } ||
            s.first() in "-?" ||
            s.lowercase() in setOf("true", "false", "null", "yes", "no", "~") ||
            s.toDoubleOrNull() != null
        if (!special) return s
        return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
    }
}
