package com.peykich.knowledge.domain

import java.security.SecureRandom

/** ULID（時刻48bit + 乱数80bit を Crockford Base32 で26文字）。ノートの id に使う。 */
object Ulid {
    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    private val random = SecureRandom()

    fun generate(timeMillis: Long = System.currentTimeMillis()): String {
        val sb = StringBuilder(26)
        var t = timeMillis
        val timeChars = CharArray(10)
        for (i in 9 downTo 0) {
            timeChars[i] = ALPHABET[(t and 31).toInt()]
            t = t ushr 5
        }
        sb.append(timeChars)
        val bytes = ByteArray(10).also { random.nextBytes(it) }
        // 80bit を 5bit ずつ 16 文字にする
        var buffer = 0
        var bits = 0
        for (b in bytes) {
            buffer = (buffer shl 8) or (b.toInt() and 0xFF)
            bits += 8
            while (bits >= 5) {
                bits -= 5
                sb.append(ALPHABET[(buffer shr bits) and 31])
            }
        }
        return sb.toString()
    }
}
