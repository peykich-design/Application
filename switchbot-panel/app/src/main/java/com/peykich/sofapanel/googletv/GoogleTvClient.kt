package com.peykich.sofapanel.googletv

import java.io.Closeable
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.KeyStore
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** Google TV 操作の失敗。メッセージは上部バーにそのまま出す */
class GoogleTvException(message: String, val retryable: Boolean = false) : Exception(message)

/** Android TV リモコン（v2）のキー。値は Android の KeyEvent と同じ */
object TvKey {
    const val HOME = 3
    const val VOLUME_UP = 24
    const val VOLUME_DOWN = 25
    const val POWER = 26
    const val MUTE = 164
    const val SLEEP = 223
    const val WAKEUP = 224
}

/**
 * Google TV / Android TV の「Android TV Remote Service」と話す（スマホの Google TV アプリのリモコンと同じ方式）。
 * - ペアリング：ポート 6467。TV に出る6桁のコードで、この端末の証明書を TV に覚えさせる
 * - 操作：ポート 6466。覚えてもらった証明書で接続し、キーを送る
 * 通信は TLS。TV の証明書は自己署名なので、ペアリング時に取得して以後はその指紋で確認する。
 */
class GoogleTvClient(
    private val identity: GoogleTvIdentity,
    private val timeoutMs: Int = 8_000,
) {
    /** ペアリングを始める。TV にコードが表示された状態で返る（コードの入力待ち） */
    fun startPairing(host: String, clientName: String, port: Int = PAIRING_PORT): PairingSession {
        val socket = connect(host, port, expectedServerSha256 = null)
        try {
            val io = Frames(socket)
            io.send(pairing { message(10) { string(1, "atvremote"); string(2, clientName) } })
            io.expectPairing(11)
            io.send(
                pairing {
                    message(20) {
                        message(1) { int(1, ENCODING_HEX); int(2, 6) }
                        int(3, ROLE_INPUT)
                    }
                },
            )
            io.expectPairing(20)
            io.send(pairing { message(30) { message(1) { int(1, ENCODING_HEX); int(2, 6) }; int(2, ROLE_INPUT) } })
            io.expectPairing(31)
            return PairingSession(socket, io, serverCertificate(socket))
        } catch (e: Exception) {
            socket.closeQuietly()
            throw translate(e, "ペアリングを開始できません")
        }
    }

    inner class PairingSession internal constructor(
        private val socket: SSLSocket,
        private val io: Frames,
        val serverCertificate: X509Certificate,
    ) : Closeable {
        /** TV に表示されたコードを送る。成功したら TV の証明書の指紋（以後の接続確認用）を返す */
        fun finish(code: String): String {
            try {
                val secret = PairingSecret.compute(identity.certificate, serverCertificate, code)
                    ?: throw GoogleTvException("コードが違います。テレビに表示された6文字を入力してください")
                io.send(pairing { message(40) { bytes(1, secret) } })
                io.expectPairing(41)
                return serverCertificate.sha256Hex()
            } catch (e: Exception) {
                throw translate(e, "ペアリングできませんでした")
            } finally {
                close()
            }
        }

        override fun close() = socket.closeQuietly()
    }

    /**
     * キーを1回送る。接続 → 初期設定のやりとり → キー送信 → 切断。
     * 送る前のテレビの状態（画面がついていたか）を返す（分からなければ null）。
     */
    fun sendKey(host: String, serverSha256: String, keyCode: Int, port: Int = REMOTE_PORT): Boolean? {
        val socket = connect(host, port, serverSha256)
        try {
            val io = Frames(socket)
            var isOn: Boolean? = null
            var features = FEATURES
            // TV からの設定要求に答え、remote_start（操作できる状態）を待つ
            while (true) {
                val msg = ProtoMessage.parse(io.receive())
                when {
                    msg.has(1) -> {
                        msg.message(1)?.int(1)?.let { features = (FEATURES.toLong() and it).toInt() }
                        io.send(
                            ProtoWriter().message(1) {
                                int(1, features)
                                message(2) { int(3, 1); string(4, "1"); string(5, "atvremote"); string(6, "1.0.0") }
                            }.toByteArray(),
                        )
                    }
                    msg.has(2) -> io.send(ProtoWriter().message(2) { int(1, features) }.toByteArray())
                    msg.has(8) -> io.send(ProtoWriter().message(9) { int(1, msg.message(8)?.int(1) ?: 0) }.toByteArray())
                    msg.has(40) -> {
                        isOn = (msg.message(40)?.int(1) ?: 0L) != 0L
                        break
                    }
                    msg.has(3) -> throw GoogleTvException("テレビがエラーを返しました")
                }
            }
            io.send(ProtoWriter().message(10) { int(1, keyCode); int(2, DIRECTION_SHORT) }.toByteArray())
            // 届く前に切断しないよう少し待つ
            Thread.sleep(300)
            return isOn
        } catch (e: Exception) {
            throw translate(e, "テレビに送れませんでした")
        } finally {
            socket.closeQuietly()
        }
    }

    private fun connect(host: String, port: Int, expectedServerSha256: String?): SSLSocket {
        val context = sslContext()
        val raw = Socket()
        try {
            raw.connect(InetSocketAddress(host, port), timeoutMs)
            raw.soTimeout = timeoutMs
            val ssl = context.socketFactory.createSocket(raw, host, port, true) as SSLSocket
            ssl.startHandshake()
            if (expectedServerSha256 != null && serverCertificate(ssl).sha256Hex() != expectedServerSha256) {
                ssl.closeQuietly()
                throw GoogleTvException("テレビの証明書が変わっています。設定でペアリングし直してください")
            }
            return ssl
        } catch (e: GoogleTvException) {
            raw.closeQuietly()
            throw e
        } catch (e: SSLException) {
            raw.closeQuietly()
            throw GoogleTvException(
                if (expectedServerSha256 != null) "テレビに拒否されました。設定でペアリングし直してください" else "テレビと暗号化通信できません",
            )
        } catch (e: SocketTimeoutException) {
            raw.closeQuietly()
            throw GoogleTvException("テレビが応答しません（電源・ネットワーク待機を確認）", retryable = true)
        } catch (e: IOException) {
            raw.closeQuietly()
            throw GoogleTvException("テレビに接続できません（同じ Wi-Fi か、IP アドレスを確認）", retryable = true)
        }
    }

    private fun sslContext(): SSLContext {
        val ks = KeyStore.getInstance(KeyStore.getDefaultType())
        ks.load(null, null)
        ks.setKeyEntry("client", identity.privateKey, KEY_PASSWORD, arrayOf(identity.certificate))
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
        kmf.init(ks, KEY_PASSWORD)
        val ctx = SSLContext.getInstance("TLS")
        // TV の証明書は自己署名。代わりにペアリング時の指紋と照合する（connect 内）
        ctx.init(kmf.keyManagers, arrayOf(AcceptAllTrustManager), null)
        return ctx
    }

    private fun serverCertificate(socket: SSLSocket): X509Certificate =
        socket.session.peerCertificates.firstOrNull() as? X509Certificate
            ?: throw GoogleTvException("テレビの証明書を取得できません")

    private fun translate(e: Exception, fallback: String): Exception = when (e) {
        is GoogleTvException -> e
        is SocketTimeoutException -> GoogleTvException("テレビが応答しません", retryable = true)
        is IOException -> GoogleTvException("$fallback（接続が切れました）", retryable = true)
        is ProtoException -> GoogleTvException("$fallback（応答を読み取れません）")
        else -> GoogleTvException(fallback)
    }

    /** ペアリング用メッセージ（OuterMessage）の共通部分 */
    private fun pairing(body: ProtoWriter.() -> Unit): ByteArray =
        ProtoWriter().int(1, 2).int(2, STATUS_OK).apply(body).toByteArray()

    internal class Frames(socket: Socket) {
        private val input = socket.getInputStream().buffered()
        private val output = socket.getOutputStream()

        fun send(message: ByteArray) = writeFrame(output, message)

        fun receive(): ByteArray = readFrame(input)

        /** ペアリングの返事を受け取り、状態と種類を確かめる */
        fun expectPairing(field: Int) {
            val msg = ProtoMessage.parse(receive())
            val status = msg.int(2) ?: STATUS_OK.toLong()
            if (status != STATUS_OK.toLong()) {
                throw GoogleTvException(
                    when (status) {
                        STATUS_BAD_SECRET.toLong() -> "コードが違います"
                        else -> "テレビがペアリングを受け付けませんでした（$status）"
                    },
                )
            }
            if (!msg.has(field)) throw GoogleTvException("テレビの応答が想定と違います")
        }
    }

    private object AcceptAllTrustManager : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    companion object {
        const val PAIRING_PORT = 6467
        const val REMOTE_PORT = 6466
        const val STATUS_OK = 200
        const val STATUS_BAD_SECRET = 402
        const val ENCODING_HEX = 3
        const val ROLE_INPUT = 1
        const val DIRECTION_SHORT = 3

        /** PING | KEY | POWER | VOLUME | APP_LINK（androidtvremote2 と同じ） */
        const val FEATURES = 1 or 2 or 32 or 64 or 512
        private val KEY_PASSWORD = "sofapanel".toCharArray()
    }
}

private fun Closeable.closeQuietly() {
    try {
        close()
    } catch (_: Exception) {
    }
}
