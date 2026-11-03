package com.peykich.sofapanel.googletv

import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.interfaces.RSAPublicKey
import java.security.spec.PKCS8EncodedKeySpec
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Google TV に名乗るためのクライアント証明書（RSA 2048・自己署名）。
 * ペアリングすると TV がこの証明書を覚えるので、同じものを使い続ける。
 */
class GoogleTvIdentity(val privateKey: PrivateKey, val certificate: X509Certificate) {
    fun privateKeyBytes(): ByteArray = privateKey.encoded
    fun certificateBytes(): ByteArray = certificate.encoded

    companion object {
        fun generate(commonName: String = "atvremote", now: Long = System.currentTimeMillis()): GoogleTvIdentity {
            val gen = KeyPairGenerator.getInstance("RSA")
            gen.initialize(2048)
            val pair = gen.generateKeyPair()
            val der = SelfSignedCertificate.build(pair.public.encoded, pair.private, commonName, now)
            return GoogleTvIdentity(pair.private, parseCertificate(der))
        }

        fun fromBytes(privateKey: ByteArray, certificate: ByteArray): GoogleTvIdentity {
            val key = KeyFactory.getInstance("RSA").generatePrivate(PKCS8EncodedKeySpec(privateKey))
            return GoogleTvIdentity(key, parseCertificate(certificate))
        }

        fun parseCertificate(der: ByteArray): X509Certificate =
            CertificateFactory.getInstance("X.509").generateCertificate(der.inputStream()) as X509Certificate
    }
}

/** 証明書の SHA-256（TV の証明書をペアリング後に固定するため） */
fun X509Certificate.sha256Hex(): String =
    MessageDigest.getInstance("SHA-256").digest(encoded).joinToString("") { "%02x".format(it) }

/**
 * ペアリングで送る値：SHA-256(クライアントの法・指数, TV の法・指数, コードの後ろ4桁)。
 * コードの先頭2桁は、このハッシュの先頭1バイトと一致するはず（入力ミスの検出）。
 * 一致しなければ null。
 */
object PairingSecret {
    fun compute(client: X509Certificate, server: X509Certificate, code: String): ByteArray? {
        val c = client.publicKey as RSAPublicKey
        val s = server.publicKey as RSAPublicKey
        return compute(c.modulus, c.publicExponent, s.modulus, s.publicExponent, code)
    }

    fun compute(
        clientModulus: BigInteger,
        clientExponent: BigInteger,
        serverModulus: BigInteger,
        serverExponent: BigInteger,
        code: String,
    ): ByteArray? {
        val normalized = code.trim().uppercase(Locale.ROOT)
        if (!isValidCode(normalized)) return null
        val digest = MessageDigest.getInstance("SHA-256")
        digest.update(unsigned(clientModulus))
        digest.update(unsigned(clientExponent))
        digest.update(unsigned(serverModulus))
        digest.update(unsigned(serverExponent))
        digest.update(hex(normalized.substring(2)))
        val hash = digest.digest()
        return if ((hash[0].toInt() and 0xff) == normalized.substring(0, 2).toInt(16)) hash else null
    }

    fun isValidCode(code: String) = code.length == 6 && code.all { it in '0'..'9' || it in 'A'..'F' || it in 'a'..'f' }

    /** 符号用の先頭 0 を除いた大きさのバイト列 */
    private fun unsigned(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        return if (b.size > 1 && b[0] == 0.toByte()) b.copyOfRange(1, b.size) else b
    }

    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

/** 自己署名の X.509 v3 証明書を DER で組み立てる（外部ライブラリを使わないため） */
internal object SelfSignedCertificate {
    private val SHA256_WITH_RSA = byteArrayOf(0x2a, 0x86.toByte(), 0x48, 0x86.toByte(), 0xf7.toByte(), 0x0d, 0x01, 0x01, 0x0b)
    private val COMMON_NAME = byteArrayOf(0x55, 0x04, 0x03)

    fun build(subjectPublicKeyInfo: ByteArray, key: PrivateKey, commonName: String, now: Long): ByteArray {
        val algorithm = seq(tlv(0x06, SHA256_WITH_RSA), byteArrayOf(0x05, 0x00))
        val name = seq(tlv(0x31, seq(tlv(0x06, COMMON_NAME), tlv(0x0c, commonName.toByteArray(Charsets.UTF_8)))))
        val validity = seq(time(now - DAY), time(now + 3650 * DAY))
        val tbs = seq(
            tlv(0xa0, tlv(0x02, byteArrayOf(2))),
            tlv(0x02, BigInteger.valueOf(now / 1000).toByteArray()),
            algorithm,
            name,
            validity,
            name,
            subjectPublicKeyInfo,
        )
        val signer = Signature.getInstance("SHA256withRSA")
        signer.initSign(key)
        signer.update(tbs)
        val signature = byteArrayOf(0) + signer.sign()
        return seq(tbs, algorithm, tlv(0x03, signature))
    }

    private fun time(millis: Long): ByteArray {
        val f = SimpleDateFormat("yyMMddHHmmss'Z'", Locale.US)
        f.timeZone = TimeZone.getTimeZone("UTC")
        return tlv(0x17, f.format(Date(millis)).toByteArray(Charsets.US_ASCII))
    }

    private fun seq(vararg parts: ByteArray) = tlv(0x30, parts.fold(ByteArray(0)) { a, b -> a + b })

    private fun tlv(tag: Int, value: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(tag)
        val len = value.size
        when {
            len < 0x80 -> out.write(len)
            len < 0x100 -> {
                out.write(0x81)
                out.write(len)
            }
            len < 0x10000 -> {
                out.write(0x82)
                out.write(len shr 8)
                out.write(len and 0xff)
            }
            else -> {
                out.write(0x83)
                out.write(len shr 16)
                out.write((len shr 8) and 0xff)
                out.write(len and 0xff)
            }
        }
        out.write(value)
        return out.toByteArray()
    }

    private const val DAY = 24 * 60 * 60 * 1000L
}
