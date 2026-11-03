package com.peykich.sofapanel.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.peykich.sofapanel.api.ApiParser
import com.peykich.sofapanel.api.Credentials
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * トークンとシークレットを Android Keystore の鍵（AES-GCM）で暗号化して保存する。
 * 平文では保存しない。ログにも出さない。
 * （EncryptedSharedPreferences は非推奨になったため、同じ仕組みを Keystore で直接実装している）
 */
class SecretStore(context: Context) {
    private val prefs = context.getSharedPreferences("secure", Context.MODE_PRIVATE)

    @Volatile
    private var cached: Credentials? = null
    @Volatile
    private var loaded = false

    fun get(): Credentials? {
        if (!loaded) {
            cached = runCatching { read() }.getOrNull()
            loaded = true
        }
        return cached
    }

    fun save(c: Credentials) {
        val plain = ApiParser.json.encodeToString(Credentials.serializer(), c).toByteArray(Charsets.UTF_8)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val data = cipher.iv + cipher.doFinal(plain)
        prefs.edit().putString(KEY, Base64.encodeToString(data, Base64.NO_WRAP)).apply()
        cached = c
        loaded = true
    }

    fun clear() {
        prefs.edit().remove(KEY).apply()
        cached = null
        loaded = true
    }

    /** 任意の文字列を同じ鍵で暗号化して保存する（Google TV の証明書など） */
    fun saveText(name: String, text: String) {
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val data = cipher.iv + cipher.doFinal(text.toByteArray(Charsets.UTF_8))
        prefs.edit().putString("text_$name", Base64.encodeToString(data, Base64.NO_WRAP)).apply()
    }

    fun loadText(name: String): String? = runCatching {
        val stored = prefs.getString("text_$name", null) ?: return null
        val data = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data.copyOfRange(0, IV_SIZE)))
        String(cipher.doFinal(data.copyOfRange(IV_SIZE, data.size)), Charsets.UTF_8)
    }.getOrNull()

    fun removeText(name: String) {
        prefs.edit().remove("text_$name").apply()
    }

    private fun read(): Credentials? {
        val stored = prefs.getString(KEY, null) ?: return null
        val data = Base64.decode(stored, Base64.NO_WRAP)
        val iv = data.copyOfRange(0, IV_SIZE)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        val plain = cipher.doFinal(data.copyOfRange(IV_SIZE, data.size))
        return ApiParser.json.decodeFromString(Credentials.serializer(), String(plain, Charsets.UTF_8))
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private companion object {
        const val ALIAS = "switchbot_credentials"
        const val KEY = "credentials"
        const val TRANSFORM = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
    }
}
