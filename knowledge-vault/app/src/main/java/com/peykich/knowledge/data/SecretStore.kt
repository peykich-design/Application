package com.peykich.knowledge.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Gemini API キーを Android Keystore の鍵（AES-GCM）で暗号化して保存する。平文では保存しない。ログにも出さない。
 * （EncryptedSharedPreferences は非推奨になったため、同じ仕組みを Keystore で直接実装している）
 */
class SecretStore(context: Context) {
    private val prefs = context.getSharedPreferences("secure", Context.MODE_PRIVATE)

    @Volatile private var cached: String? = null
    @Volatile private var loaded = false

    private val _hasKey = MutableStateFlow(!geminiKey().isNullOrBlank())
    /** キーが保存されているか（画面の案内用）。 */
    val hasKey: StateFlow<Boolean> = _hasKey

    /** 画面に出すための末尾4文字。 */
    fun keyHint(): String? = geminiKey()?.takeLast(4)

    fun geminiKey(): String? {
        if (!loaded) {
            cached = runCatching { read() }.getOrNull()
            loaded = true
        }
        return cached
    }

    fun saveGeminiKey(key: String) {
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val data = cipher.iv + cipher.doFinal(key.trim().toByteArray(Charsets.UTF_8))
        prefs.edit().putString(KEY, Base64.encodeToString(data, Base64.NO_WRAP)).apply()
        cached = key.trim()
        loaded = true
        _hasKey.value = cached!!.isNotEmpty()
    }

    fun clear() {
        prefs.edit().remove(KEY).apply()
        cached = null
        loaded = true
        _hasKey.value = false
    }

    private fun read(): String? {
        val stored = prefs.getString(KEY, null) ?: return null
        val data = Base64.decode(stored, Base64.NO_WRAP)
        val iv = data.copyOfRange(0, IV_SIZE)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        return String(cipher.doFinal(data.copyOfRange(IV_SIZE, data.size)), Charsets.UTF_8)
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
        const val ALIAS = "gemini_api_key"
        const val KEY = "gemini_api_key"
        const val TRANSFORM = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
    }
}
