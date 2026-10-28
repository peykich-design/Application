package com.peykich.knowledge.ai

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlinx.serialization.json.putJsonObject

/** Gemini API（generateContent）の要求の組み立てと応答の読み取り。通信はしない（テスト対象）。 */
object GeminiProtocol {
    const val BASE_URL = "https://generativelanguage.googleapis.com/v1beta/models/"

    val json = Json { ignoreUnknownKeys = true; isLenient = true }

    data class Turn(val user: Boolean, val text: String)

    fun endpoint(model: String) = BASE_URL + model.trim() + ":generateContent"

    fun buildRequest(system: String, turns: List<Turn>, schema: JsonObject? = null): JsonObject = buildJsonObject {
        putJsonObject("systemInstruction") {
            putJsonArray("parts") { add(buildJsonObject { put("text", system) }) }
        }
        putJsonArray("contents") {
            for (t in turns) {
                add(buildJsonObject {
                    put("role", if (t.user) "user" else "model")
                    putJsonArray("parts") { add(buildJsonObject { put("text", t.text) }) }
                })
            }
        }
        if (schema != null) {
            putJsonObject("generationConfig") {
                put("responseMimeType", "application/json")
                put("responseSchema", schema)
            }
        }
    }

    /** 応答本文から回答テキストを取り出す。思考部分（thought: true）は除く。 */
    fun parseText(body: String): String {
        val root = runCatching { json.parseToJsonElement(body).jsonObject }.getOrElse {
            throw AiException("AIの応答を読み取れませんでした", AiException.Kind.BAD_RESPONSE)
        }
        val candidates = root["candidates"] as? JsonArray
        val first = candidates?.firstOrNull() as? JsonObject
        if (first == null) {
            val reason = (root["promptFeedback"] as? JsonObject)?.get("blockReason")?.jsonPrimitive?.contentOrNull
            throw AiException(
                if (reason != null) "AIが応答を断りました（$reason）" else "AIから回答がありませんでした",
                AiException.Kind.BLOCKED,
            )
        }
        val parts = (first["content"] as? JsonObject)?.get("parts") as? JsonArray
        val text = parts.orEmpty()
            .mapNotNull { it as? JsonObject }
            .filter { (it["thought"] as? JsonPrimitive)?.booleanOrNull != true }
            .mapNotNull { (it["text"] as? JsonPrimitive)?.contentOrNull }
            .joinToString("")
        if (text.isBlank()) {
            val finish = (first["finishReason"] as? JsonPrimitive)?.contentOrNull
            throw AiException(
                "AIから回答がありませんでした" + (finish?.let { "（$it）" } ?: ""),
                if (finish == "SAFETY" || finish == "PROHIBITED_CONTENT") AiException.Kind.BLOCKED else AiException.Kind.BAD_RESPONSE,
            )
        }
        return text
    }

    /** エラー応答の本文（{"error":{"message":...,"status":...}}）から状態を取り出す。 */
    fun errorStatus(body: String): String? = runCatching {
        json.parseToJsonElement(body).jsonObject["error"]?.jsonObject?.get("status")?.jsonPrimitive?.contentOrNull
    }.getOrNull()

    fun httpError(code: Int, body: String): AiException {
        val status = errorStatus(body)
        return when {
            code == 400 && status == "INVALID_ARGUMENT" && body.contains("API key", ignoreCase = true) ->
                AiException("Gemini APIキーが正しくありません。設定画面で確認してください", AiException.Kind.AUTH)
            code == 401 || code == 403 ->
                AiException("Gemini APIキーが使えません（$code）。設定画面で確認してください", AiException.Kind.AUTH)
            code == 404 -> AiException("モデルが見つかりません。設定画面でモデル名を確認してください", AiException.Kind.OTHER)
            code == 429 -> AiException("Gemini の利用上限に達しました。しばらく待ってから試してください", AiException.Kind.RATE_LIMIT)
            code >= 500 -> AiException("Gemini のサーバーでエラーが起きました（$code）", AiException.Kind.SERVER)
            else -> AiException("Gemini の呼び出しに失敗しました（$code${status?.let { " $it" } ?: ""}）")
        }
    }

    /** JSON で返させた応答を読む。```json で囲まれていても読めるようにする。 */
    fun extractJson(text: String): JsonElement {
        val t = text.trim().removePrefix("```json").removePrefix("```").removeSuffix("```").trim()
        return runCatching { json.parseToJsonElement(t) }.getOrElse {
            throw AiException("AIの応答の形式が正しくありませんでした", AiException.Kind.BAD_RESPONSE)
        }
    }

    // ---- 応答の形（responseSchema。OpenAPI の一部） ----

    private fun str() = buildJsonObject { put("type", "STRING") }
    private fun strArray() = buildJsonObject { put("type", "ARRAY"); put("items", str()) }
    private fun obj(props: Map<String, JsonObject>) = buildJsonObject {
        put("type", "OBJECT")
        put("properties", JsonObject(props))
        put("required", buildJsonArray { props.keys.forEach { add(JsonPrimitive(it)) } })
        put("propertyOrdering", buildJsonArray { props.keys.forEach { add(JsonPrimitive(it)) } })
    }

    val formatSchema: JsonObject = obj(
        linkedMapOf(
            "title" to str(),
            "subfolder" to str(),
            "tags" to strArray(),
            "summary" to str(),
            "body_markdown" to str(),
        ),
    )

    val selectSchema: JsonObject = obj(linkedMapOf("ids" to strArray(), "reason" to str()))

    val summarySchema: JsonObject = obj(linkedMapOf("summary" to str(), "tags" to strArray(), "type" to str()))

    fun parseIds(text: String): List<String> {
        val o = extractJson(text) as? JsonObject ?: return emptyList()
        return (o["ids"] as? JsonArray).orEmpty().mapNotNull { (it as? JsonPrimitive)?.contentOrNull }
    }

    private fun JsonArray?.orEmpty(): List<JsonElement> = this ?: emptyList()
}
