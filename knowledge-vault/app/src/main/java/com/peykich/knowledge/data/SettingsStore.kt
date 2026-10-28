package com.peykich.knowledge.data

import android.content.Context
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

data class AppSettings(
    val formatModel: String = DEFAULT_MODEL,
    val selectModel: String = DEFAULT_MODEL,
    val answerModel: String = DEFAULT_MODEL,
    /** 仕事ノートの固有名詞を一般化する（既定オン）。 */
    val generalizeWork: Boolean = true,
    /** 仕事カテゴリで AI に送る前に確認する（既定オフ）。 */
    val confirmWorkSend: Boolean = false,
) {
    companion object {
        /** 2026年10月時点の最新 Flash 系。設定画面で変えられる。 */
        const val DEFAULT_MODEL = "gemini-3.8-flash"
    }
}

private val Context.dataStore by preferencesDataStore(name = "settings")

class SettingsStore(private val context: Context) {
    private object Keys {
        val formatModel = stringPreferencesKey("format_model")
        val selectModel = stringPreferencesKey("select_model")
        val answerModel = stringPreferencesKey("answer_model")
        val generalize = booleanPreferencesKey("generalize_work")
        val confirm = booleanPreferencesKey("confirm_work_send")
    }

    val settings: Flow<AppSettings> = context.dataStore.data.map { it.toSettings() }

    suspend fun current(): AppSettings = settings.first()

    suspend fun update(transform: (AppSettings) -> AppSettings) {
        context.dataStore.edit { prefs ->
            val next = transform(prefs.toSettings())
            prefs[Keys.formatModel] = next.formatModel.trim().ifEmpty { AppSettings.DEFAULT_MODEL }
            prefs[Keys.selectModel] = next.selectModel.trim().ifEmpty { AppSettings.DEFAULT_MODEL }
            prefs[Keys.answerModel] = next.answerModel.trim().ifEmpty { AppSettings.DEFAULT_MODEL }
            prefs[Keys.generalize] = next.generalizeWork
            prefs[Keys.confirm] = next.confirmWorkSend
        }
    }

    private fun Preferences.toSettings() = AppSettings(
        formatModel = this[Keys.formatModel] ?: AppSettings.DEFAULT_MODEL,
        selectModel = this[Keys.selectModel] ?: AppSettings.DEFAULT_MODEL,
        answerModel = this[Keys.answerModel] ?: AppSettings.DEFAULT_MODEL,
        generalizeWork = this[Keys.generalize] ?: true,
        confirmWorkSend = this[Keys.confirm] ?: false,
    )
}

/** 記録画面の入力中テキスト。1文字ごとに保存し、アプリが落ちても失わない（仕様書 4章）。 */
class DraftStore(context: Context) {
    private val prefs = context.getSharedPreferences("drafts", Context.MODE_PRIVATE)

    fun get(type: String): String = prefs.getString(type, "").orEmpty()

    fun put(type: String, text: String) {
        prefs.edit().apply { if (text.isEmpty()) remove(type) else putString(type, text) }.apply()
    }

    fun clear(type: String) = put(type, "")
}
