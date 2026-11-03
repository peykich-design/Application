package com.peykich.sofapanel.googletv

import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/** 必要な分だけの protobuf 書き込み（varint と長さ付きフィールドのみ） */
class ProtoWriter {
    private val out = ByteArrayOutputStream()

    fun int(field: Int, value: Long): ProtoWriter {
        writeVarint(out, (field.toLong() shl 3) or 0)
        writeVarint(out, value)
        return this
    }

    fun int(field: Int, value: Int) = int(field, value.toLong())

    fun bytes(field: Int, value: ByteArray): ProtoWriter {
        writeVarint(out, (field.toLong() shl 3) or 2)
        writeVarint(out, value.size.toLong())
        out.write(value)
        return this
    }

    fun string(field: Int, value: String) = bytes(field, value.toByteArray(Charsets.UTF_8))

    fun message(field: Int, build: ProtoWriter.() -> Unit) = bytes(field, ProtoWriter().apply(build).toByteArray())

    fun toByteArray(): ByteArray = out.toByteArray()
}

/** 読み取ったメッセージ。同じ番号のフィールドが複数あれば最後の値を使う */
class ProtoMessage(private val fields: Map<Int, List<Any>>) {
    fun has(field: Int) = fields.containsKey(field)
    fun int(field: Int): Long? = fields[field]?.lastOrNull() as? Long
    fun bytes(field: Int): ByteArray? = fields[field]?.lastOrNull() as? ByteArray
    fun string(field: Int): String? = bytes(field)?.toString(Charsets.UTF_8)
    fun message(field: Int): ProtoMessage? = bytes(field)?.let { parse(it) }

    companion object {
        fun parse(data: ByteArray): ProtoMessage {
            val map = mutableMapOf<Int, MutableList<Any>>()
            var i = 0
            fun varint(): Long {
                var result = 0L
                var shift = 0
                while (true) {
                    if (i >= data.size) throw ProtoException("途中で切れたメッセージ")
                    val b = data[i++].toInt() and 0xff
                    result = result or ((b and 0x7f).toLong() shl shift)
                    if (b < 0x80) return result
                    shift += 7
                    if (shift > 63) throw ProtoException("varint が長すぎます")
                }
            }
            while (i < data.size) {
                val key = varint()
                val field = (key ushr 3).toInt()
                val value: Any = when ((key and 7).toInt()) {
                    0 -> varint()
                    1 -> data.copyOfRange(i, i + 8).also { i += 8 }
                    2 -> {
                        val len = varint().toInt()
                        if (len < 0 || i + len > data.size) throw ProtoException("長さが不正です")
                        data.copyOfRange(i, i + len).also { i += len }
                    }
                    5 -> data.copyOfRange(i, i + 4).also { i += 4 }
                    else -> throw ProtoException("未対応の型です")
                }
                map.getOrPut(field) { mutableListOf() } += value
            }
            return ProtoMessage(map)
        }
    }
}

class ProtoException(message: String) : Exception(message)

fun writeVarint(out: OutputStream, value: Long) {
    var v = value
    while (true) {
        if (v and 0x7fL.inv() == 0L) {
            out.write(v.toInt())
            return
        }
        out.write(((v and 0x7f) or 0x80).toInt())
        v = v ushr 7
    }
}

/** 長さ（varint）＋本体の形で1メッセージ送る */
fun writeFrame(out: OutputStream, message: ByteArray) {
    val buf = ByteArrayOutputStream()
    writeVarint(buf, message.size.toLong())
    buf.write(message)
    out.write(buf.toByteArray())
    out.flush()
}

/** 長さ（varint）＋本体の形で1メッセージ受け取る */
fun readFrame(input: InputStream): ByteArray {
    var len = 0L
    var shift = 0
    while (true) {
        val b = input.read()
        if (b < 0) throw EOFException("接続が切れました")
        len = len or ((b and 0x7f).toLong() shl shift)
        if (b < 0x80) break
        shift += 7
        if (shift > 35) throw ProtoException("長さが不正です")
    }
    if (len > MAX_FRAME) throw ProtoException("メッセージが大きすぎます")
    val data = ByteArray(len.toInt())
    var read = 0
    while (read < data.size) {
        val n = input.read(data, read, data.size - read)
        if (n < 0) throw EOFException("接続が切れました")
        read += n
    }
    return data
}

private const val MAX_FRAME = 1 shl 20
