package com.peykich.knowledge.ai

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.io.InterruptedIOException
import java.util.concurrent.TimeUnit

/**
 * OkHttp で Gemini に送る。API キーはヘッダー（x-goog-api-key）で渡し、URL には入れない。
 * ノート本文やキーをログに出さないよう、ログ用の Interceptor は付けない。
 */
class OkHttpGeminiTransport(private val client: OkHttpClient) : GeminiTransport {
    override suspend fun post(url: String, apiKey: String, body: String, timeoutSeconds: Long): HttpResult =
        withContext(Dispatchers.IO) {
            val call = client.newBuilder().callTimeout(timeoutSeconds, TimeUnit.SECONDS).build().newCall(
                Request.Builder()
                    .url(url)
                    .header("x-goog-api-key", apiKey)
                    .post(body.toRequestBody("application/json; charset=utf-8".toMediaType()))
                    .build(),
            )
            try {
                call.execute().use { res -> HttpResult(res.code, res.body.string()) }
            } catch (e: InterruptedIOException) {
                throw AiException("AIの応答が${timeoutSeconds}秒以内に返りませんでした", AiException.Kind.TIMEOUT)
            } catch (e: IOException) {
                throw AiException("通信できませんでした。ネットワークを確認してください", AiException.Kind.NETWORK)
            }
        }
}
