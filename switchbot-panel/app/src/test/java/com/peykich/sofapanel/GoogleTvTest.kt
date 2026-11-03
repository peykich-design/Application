package com.peykich.sofapanel

import com.peykich.sofapanel.googletv.GoogleTvClient
import com.peykich.sofapanel.googletv.GoogleTvException
import com.peykich.sofapanel.googletv.GoogleTvIdentity
import com.peykich.sofapanel.googletv.PairingSecret
import com.peykich.sofapanel.googletv.ProtoMessage
import com.peykich.sofapanel.googletv.ProtoWriter
import com.peykich.sofapanel.googletv.TvKey
import com.peykich.sofapanel.googletv.readFrame
import com.peykich.sofapanel.googletv.sha256Hex
import com.peykich.sofapanel.googletv.writeFrame
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.math.BigInteger
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.X509Certificate
import java.security.interfaces.RSAPublicKey
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.thread

class GoogleTvTest {
    @Test
    fun protoRoundTrip() {
        val bytes = ProtoWriter().int(1, 2).int(2, 200).message(10) { string(1, "atvremote"); string(2, "名前") }.toByteArray()
        val msg = ProtoMessage.parse(bytes)
        assertEquals(2L, msg.int(1))
        assertEquals(200L, msg.int(2))
        assertEquals("名前", msg.message(10)?.string(2))
        // 200 は varint で 2 バイト（C8 01）
        assertArrayEquals(byteArrayOf(0x08, 0x02, 0x10, 0xc8.toByte(), 0x01), bytes.copyOfRange(0, 5))
    }

    @Test
    fun pairingSecretMatchesPythonFormula() {
        // Python: sha256(fromhex(f"{cm:X}") + fromhex(f"0{ce:X}") + fromhex(f"{sm:X}") + fromhex(f"0{se:X}") + fromhex(code[2:]))
        val cm = BigInteger("C3A1F0", 16)
        val sm = BigInteger("D2B4E6", 16)
        val e = BigInteger.valueOf(65537)
        val d = MessageDigest.getInstance("SHA-256")
        listOf("C3A1F0", "010001", "D2B4E6", "010001", "AB12").forEach { h ->
            d.update(ByteArray(h.length / 2) { h.substring(it * 2, it * 2 + 2).toInt(16).toByte() })
        }
        val hash = d.digest()
        val code = "%02X".format(hash[0].toInt() and 0xff) + "AB12"
        assertArrayEquals(hash, PairingSecret.compute(cm, e, sm, e, code))
        assertArrayEquals(hash, PairingSecret.compute(cm, e, sm, e, code.lowercase()))
        // 先頭2桁が合わない（打ち間違い）なら null
        val wrong = "%02X".format((hash[0].toInt() + 1) and 0xff) + "AB12"
        assertNull(PairingSecret.compute(cm, e, sm, e, wrong))
        assertNull(PairingSecret.compute(cm, e, sm, e, "12345"))
    }

    @Test
    fun catalogHasSleepAndWake() {
        val tv = com.peykich.sofapanel.api.Device("googletv", "リビングのTV", "Google TV")
        val cmds = com.peykich.sofapanel.domain.CommandCatalog.commandsFor(tv)
        assertEquals("スリープ", cmds[0].label)
        assertEquals("223", cmds[0].parameter)
        assertEquals("googletv", cmds[0].commandType)
        assertEquals("224", cmds.first { it.label == "起動" }.parameter)
        assertTrue(com.peykich.sofapanel.domain.CommandCatalog.isControllable(tv))
    }

    @Test
    fun identityRoundTrip() {
        val id = GoogleTvIdentity.generate()
        id.certificate.checkValidity()
        id.certificate.verify(id.certificate.publicKey)
        assertEquals(2048, (id.certificate.publicKey as RSAPublicKey).modulus.bitLength())
        val back = GoogleTvIdentity.fromBytes(id.privateKeyBytes(), id.certificateBytes())
        assertEquals(id.certificate, back.certificate)
    }

    /** 本物の TV の代わりに、androidtvremote2 と同じ手順で応答する TLS サーバー */
    private class FakeTv {
        val identity = GoogleTvIdentity.generate("fake-tv")
        val clientCert = AtomicReference<X509Certificate?>()
        val receivedKey = AtomicReference<Pair<Long, Long>?>()
        val error = AtomicReference<Throwable?>()

        private val context: SSLContext = SSLContext.getInstance("TLS").apply {
            val ks = KeyStore.getInstance(KeyStore.getDefaultType())
            ks.load(null, null)
            ks.setKeyEntry("tv", identity.privateKey, "pw".toCharArray(), arrayOf(identity.certificate))
            val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            kmf.init(ks, "pw".toCharArray())
            init(kmf.keyManagers, arrayOf(object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }), null)
        }

        private fun server(): SSLServerSocket =
            (context.serverSocketFactory.createServerSocket(0) as SSLServerSocket).apply { needClientAuth = true }

        private fun serve(server: SSLServerSocket, block: (SSLSocket) -> Unit) = thread {
            try {
                server.accept().use { s ->
                    s as SSLSocket
                    s.startHandshake()
                    clientCert.set(s.session.peerCertificates[0] as X509Certificate)
                    block(s)
                }
            } catch (t: Throwable) {
                error.compareAndSet(null, t)
            } finally {
                server.close()
            }
        }

        /** ペアリング。表示するコードを codeOut に入れる */
        fun pairing(codeOut: AtomicReference<String>): Int {
            val server = server()
            serve(server) { s ->
                val input = s.inputStream.buffered()
                val out = s.outputStream
                fun ok(body: ProtoWriter.() -> Unit) = writeFrame(out, ProtoWriter().int(1, 2).int(2, 200).apply(body).toByteArray())
                val req = ProtoMessage.parse(readFrame(input))
                check(req.message(10)?.string(1) == "atvremote")
                ok { message(11) { string(1, "fake") } }
                val opts = ProtoMessage.parse(readFrame(input)).message(20)!!
                check(opts.message(1)?.int(1) == 3L && opts.message(1)?.int(2) == 6L && opts.int(3) == 1L)
                ok { message(20) { message(1) { int(1, 3); int(2, 6) }; int(3, 1) } }
                val cfg = ProtoMessage.parse(readFrame(input)).message(30)!!
                check(cfg.int(2) == 1L)
                // TV 側でコードを決める（先頭2桁はハッシュの先頭バイト）
                val c = clientCert.get()!!.publicKey as RSAPublicKey
                val me = identity.certificate.publicKey as RSAPublicKey
                var tail = "3F9A"
                var expected: ByteArray? = null
                for (first in 0..255) {
                    val code = "%02X".format(first) + tail
                    expected = PairingSecret.compute(c.modulus, c.publicExponent, me.modulus, me.publicExponent, code)
                    if (expected != null) {
                        codeOut.set(code)
                        break
                    }
                }
                ok { message(31) {} }
                val secret = ProtoMessage.parse(readFrame(input)).message(40)!!.bytes(1)!!
                if (secret.contentEquals(expected)) {
                    ok { message(41) { bytes(1, secret) } }
                } else {
                    writeFrame(out, ProtoWriter().int(1, 2).int(2, 402).toByteArray())
                }
            }
            return server.localPort
        }

        /** 操作用ポート。設定 → 有効化 → ping → 開始 の順に送り、届いたキーを記録する */
        fun remote(started: Boolean): Int {
            val server = server()
            serve(server) { s ->
                val input = s.inputStream.buffered()
                val out = s.outputStream
                writeFrame(out, ProtoWriter().message(1) { int(1, 1023); message(2) { string(1, "Chromecast"); string(2, "Google") } }.toByteArray())
                val cfg = ProtoMessage.parse(readFrame(input)).message(1)!!
                check(cfg.int(1) == GoogleTvClient.FEATURES.toLong())
                writeFrame(out, ProtoWriter().message(2) { int(1, 622) }.toByteArray())
                check(ProtoMessage.parse(readFrame(input)).has(2))
                writeFrame(out, ProtoWriter().message(8) { int(1, 7); int(2, 9) }.toByteArray())
                check(ProtoMessage.parse(readFrame(input)).message(9)?.int(1) == 7L)
                writeFrame(out, ProtoWriter().message(40) { int(1, if (started) 1 else 0) }.toByteArray())
                val key = ProtoMessage.parse(readFrame(input)).message(10)!!
                receivedKey.set(key.int(1)!! to key.int(2)!!)
            }
            return server.localPort
        }
    }

    @Test
    fun pairThenSendSleep() {
        val tv = FakeTv()
        val me = GoogleTvIdentity.generate()
        val client = GoogleTvClient(me, timeoutMs = 5000)

        val code = AtomicReference<String>()
        val session = client.startPairing("127.0.0.1", "ソファパネル", tv.pairing(code))
        val fingerprint = session.finish(code.get())
        assertNull(tv.error.get())
        assertEquals(tv.identity.certificate.sha256Hex(), fingerprint)
        assertEquals(me.certificate, tv.clientCert.get())

        val wasOn = client.sendKey("127.0.0.1", fingerprint, TvKey.SLEEP, tv.remote(started = true))
        Thread.sleep(200)
        assertNull(tv.error.get())
        assertEquals(true, wasOn)
        assertEquals(223L to 3L, tv.receivedKey.get())
    }

    @Test
    fun wrongCodeIsRejectedBeforeSending() {
        val tv = FakeTv()
        val client = GoogleTvClient(GoogleTvIdentity.generate(), timeoutMs = 5000)
        val code = AtomicReference<String>()
        val session = client.startPairing("127.0.0.1", "test", tv.pairing(code))
        val real = code.get()
        val wrong = "%02X".format((real.substring(0, 2).toInt(16) + 1) and 0xff) + real.substring(2)
        try {
            session.finish(wrong)
            fail()
        } catch (e: GoogleTvException) {
            assertTrue(e.message!!.contains("コードが違います"))
        }
    }

    @Test
    fun changedTvCertificateIsRefused() {
        val tv = FakeTv()
        val client = GoogleTvClient(GoogleTvIdentity.generate(), timeoutMs = 5000)
        try {
            client.sendKey("127.0.0.1", "00".repeat(32), TvKey.WAKEUP, tv.remote(started = false))
            fail()
        } catch (e: GoogleTvException) {
            assertTrue(e.message!!.contains("ペアリングし直して"))
        }
    }

    @Test
    fun unreachableTvIsRetryable() {
        val client = GoogleTvClient(GoogleTvIdentity.generate(), timeoutMs = 2000)
        val port = java.net.ServerSocket(0).use { it.localPort } // 閉じたポート
        try {
            client.sendKey("127.0.0.1", "00".repeat(32), TvKey.SLEEP, port)
            fail()
        } catch (e: GoogleTvException) {
            assertTrue(e.retryable)
        }
        assertNotNull(client)
    }
}
