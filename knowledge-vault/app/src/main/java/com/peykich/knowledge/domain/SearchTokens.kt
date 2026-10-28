package com.peykich.knowledge.domain

/**
 * 全文検索（FTS4）用の分かち書き。FTS4 の標準の区切りは日本語を単語に分けないため、
 * 漢字・かな・カナの連続は2文字ずつ（バイグラム）に分けて空白区切りで入れ、検索語も同じ形にする。
 * 英数字は小文字の単語のまま。
 */
object SearchTokens {
    fun index(text: String): String = tokens(text).joinToString(" ")

    /** FTS の MATCH に渡す式。語はすべて含む（AND）。何も無ければ null。 */
    fun query(input: String): String? {
        val parts = input.split(Regex("[\\s　]+")).filter { it.isNotBlank() }.mapNotNull { term ->
            val t = tokens(term)
            when {
                t.isEmpty() -> null
                t.size == 1 && (t[0].length == 1 || t[0].all { it.code < 128 }) -> t[0] + "*"
                t.size == 1 -> t[0]
                else -> "\"" + t.joinToString(" ") + "\""
            }
        }
        return parts.takeIf { it.isNotEmpty() }?.joinToString(" ")
    }

    fun tokens(text: String): List<String> {
        val out = mutableListOf<String>()
        val word = StringBuilder()
        val cjk = StringBuilder()
        fun flushWord() {
            if (word.isNotEmpty()) { out += word.toString().lowercase(); word.clear() }
        }
        fun flushCjk() {
            if (cjk.isEmpty()) return
            val cps = cjk.codePoints().toArray()
            if (cps.size == 1) out += cjk.toString()
            else for (i in 0 until cps.size - 1) out += String(cps, i, 2)
            cjk.clear()
        }
        var i = 0
        while (i < text.length) {
            val cp = text.codePointAt(i)
            when {
                isCjk(cp) -> { flushWord(); cjk.appendCodePoint(cp) }
                Character.isLetterOrDigit(cp) -> { flushCjk(); word.appendCodePoint(normalizeWidth(cp)) }
                else -> { flushWord(); flushCjk() }
            }
            i += Character.charCount(cp)
        }
        flushWord(); flushCjk()
        return out
    }

    private fun isCjk(cp: Int): Boolean {
        val block = Character.UnicodeBlock.of(cp)
        return block == Character.UnicodeBlock.HIRAGANA ||
            block == Character.UnicodeBlock.KATAKANA ||
            block == Character.UnicodeBlock.KATAKANA_PHONETIC_EXTENSIONS ||
            block == Character.UnicodeBlock.CJK_UNIFIED_IDEOGRAPHS ||
            block == Character.UnicodeBlock.CJK_UNIFIED_IDEOGRAPHS_EXTENSION_A ||
            block == Character.UnicodeBlock.CJK_COMPATIBILITY_IDEOGRAPHS ||
            (cp == 0x30FC) || (cp in 0xFF66..0xFF9F)
    }

    /** 全角英数字を半角にする（「ＥＸＣＥＬ」でも「excel」で探せるように）。 */
    private fun normalizeWidth(cp: Int): Int = if (cp in 0xFF10..0xFF5A) cp - 0xFEE0 else cp
}
