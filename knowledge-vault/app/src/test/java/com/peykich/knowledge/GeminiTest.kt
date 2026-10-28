package com.peykich.knowledge

import com.peykich.knowledge.ai.AiException
import com.peykich.knowledge.ai.CatalogEntry
import com.peykich.knowledge.ai.FormatRequest
import com.peykich.knowledge.ai.GeminiConfig
import com.peykich.knowledge.ai.GeminiProtocol
import com.peykich.knowledge.ai.GeminiProvider
import com.peykich.knowledge.ai.HttpResult
import com.peykich.knowledge.ai.NoteBody
import com.peykich.knowledge.ai.PromptBuilder
import com.peykich.knowledge.domain.NoteType
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.time.ZoneId
import java.time.ZonedDateTime

class GeminiTest {
    private val now = ZonedDateTime.of(2026, 10, 25, 16, 40, 0, 0, ZoneId.of("Asia/Tokyo"))

    /** テストでも本物の assets/prompts を読む（ファイル名の打ち間違いに気づけるように）。 */
    private val prompts = PromptBuilder { name ->
        val candidates = listOf("src/main/assets/prompts/$name", "app/src/main/assets/prompts/$name", "knowledge-vault/app/src/main/assets/prompts/$name")
        candidates.map(::File).first { it.exists() }.readText()
    }

    private fun geminiReply(text: String) =
        """{"candidates":[{"content":{"parts":[{"text":"thinking","thought":true},{"text":${JsonPrimitive(text)}}]},"finishReason":"STOP"}]}"""

    private class Recorder(val reply: (String) -> HttpResult) {
        val urls = mutableListOf<String>()
        val bodies = mutableListOf<String>()
        val keys = mutableListOf<String>()
    }

    private fun provider(rec: Recorder, key: String? = "KEY") = GeminiProvider(
        transport = { url, apiKey, body, _ ->
            rec.urls += url; rec.keys += apiKey; rec.bodies += body
            rec.reply(body)
        },
        prompts = prompts,
        config = { GeminiConfig(key, "fmt-model", "sel-model", "ans-model") },
        clock = { now },
    )

    private fun req(type: NoteType = NoteType.WORK, generalize: Boolean = true) = FormatRequest(
        type = type, text = "COUNTIFで重複に色を付けた。顧客XYZ向け", subfolders = listOf("excel", "git"),
        existingTags = listOf("excel"), now = now, generalize = generalize,
    )

    @Test fun formatSendsSchemaAndParsesResult() = runBlocking {
        val result = """{"title":"重複の色分け","subfolder":"excel","tags":["excel"],"summary":"要約","body_markdown":"# 重複の色分け\n\n## 要点\n- a"}"""
        val rec = Recorder { HttpResult(200, geminiReply(result)) }
        val r = provider(rec).format(req())
        assertEquals("重複の色分け", r.title)
        assertEquals("excel", r.subfolder)
        assertTrue(r.bodyMarkdown.startsWith("# 重複の色分け"))
        assertEquals("https://generativelanguage.googleapis.com/v1beta/models/fmt-model:generateContent", rec.urls.single())
        assertEquals("KEY", rec.keys.single())
        val body = GeminiProtocol.json.parseToJsonElement(rec.bodies.single()).jsonObject
        val config = body["generationConfig"]!!.jsonObject
        assertEquals("application/json", config["responseMimeType"]!!.jsonPrimitive.content)
        assertEquals(5, config["responseSchema"]!!.jsonObject["required"]!!.jsonArray.size)
        val system = body["systemInstruction"]!!.jsonObject["parts"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content
        assertTrue(system.contains("事実を足さない"))
        assertTrue("仕事＋一般化オンなら固有名詞のルールが付く", system.contains("A社"))
        val user = body["contents"]!!.jsonArray[0].jsonObject["parts"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content
        assertTrue(user.contains("既存サブフォルダ: excel, git"))
        assertTrue(user.contains("<input>\nCOUNTIFで重複に色を付けた。顧客XYZ向け\n</input>"))
    }

    @Test fun generalizeOnlyForWork() {
        assertFalse(prompts.formatSystem(req(NoteType.HOBBY)).contains("A社"))
        assertFalse(prompts.formatSystem(req(NoteType.WORK, generalize = false)).contains("A社"))
    }

    @Test fun revisionIncludesPreviousAndInstruction() {
        val prev = com.peykich.knowledge.ai.FormatResult("t", "excel", listOf("a"), "s", "# t")
        val text = prompts.formatUser(req().copy(previous = prev, instruction = "タイトルを短く"))
        assertTrue(text.contains("<previous>"))
        assertTrue(text.contains("\"body_markdown\":\"# t\""))
        assertTrue(text.contains("<instruction>\nタイトルを短く\n</instruction>"))
    }

    @Test fun missingKeyFailsWithoutCalling() = runBlocking {
        val rec = Recorder { HttpResult(200, "") }
        try {
            provider(rec, key = " ").format(req()); fail()
        } catch (e: AiException) {
            assertEquals(AiException.Kind.NO_KEY, e.kind)
        }
        assertTrue(rec.urls.isEmpty())
    }

    @Test fun httpErrorsBecomeMessages() {
        assertEquals(AiException.Kind.AUTH, GeminiProtocol.httpError(400, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""").kind)
        assertEquals(AiException.Kind.RATE_LIMIT, GeminiProtocol.httpError(429, "{}").kind)
        assertEquals(AiException.Kind.SERVER, GeminiProtocol.httpError(503, "").kind)
    }

    @Test fun blockedPrompt() {
        try {
            GeminiProtocol.parseText("""{"promptFeedback":{"blockReason":"SAFETY"}}"""); fail()
        } catch (e: AiException) {
            assertEquals(AiException.Kind.BLOCKED, e.kind)
            assertTrue(e.message!!.contains("SAFETY"))
        }
    }

    @Test fun badJsonFromModel() = runBlocking {
        val rec = Recorder { HttpResult(200, geminiReply("これはJSONではありません")) }
        try {
            provider(rec).format(req()); fail()
        } catch (e: AiException) {
            assertEquals(AiException.Kind.BAD_RESPONSE, e.kind)
        }
    }

    @Test fun selectFiltersUnknownIds() = runBlocking {
        val rec = Recorder { HttpResult(200, geminiReply("""```json
{"ids":["A","ZZZ","B","A"],"reason":"r"}
```""")) }
        val catalog = listOf(
            CatalogEntry("A", NoteType.WORK, "a", "sa", listOf("x"), "2026-10-01T00:00:00+09:00"),
            CatalogEntry("B", NoteType.DIARY, "b", "sb", emptyList(), "2026-10-02T00:00:00+09:00"),
        )
        assertEquals(listOf("A", "B"), provider(rec).selectNotes("質問", catalog, emptyList()))
        assertTrue(rec.urls.single().contains("sel-model"))
        val system = GeminiProtocol.json.parseToJsonElement(rec.bodies.single()).jsonObject["systemInstruction"].toString()
        assertTrue(system.contains("2026-10-25"))
    }

    @Test fun answerTruncatesLongNotes() {
        val text = prompts.answerUser("q", listOf(NoteBody("1", "t1", "a".repeat(80)), NoteBody("2", "t2", "b".repeat(80))), maxChars = 100)
        assertTrue(text.contains("<note number=\"1\" title=\"t1\">\n" + "a".repeat(80)))
        assertTrue(text.contains("b".repeat(20) + "\n…（以下省略）"))
        assertFalse(text.contains("b".repeat(21)))
    }

    @Test fun interviewStartsWithUserTurn() = runBlocking {
        val rec = Recorder { HttpResult(200, geminiReply("今日はどんな仕事の知見を記録しますか？")) }
        val q = provider(rec).interviewTurn(NoteType.WORK, emptyList())
        assertEquals("今日はどんな仕事の知見を記録しますか？", q)
        val contents = GeminiProtocol.json.parseToJsonElement(rec.bodies.single()).jsonObject["contents"]!!.jsonArray
        assertEquals("user", (contents[0] as JsonObject)["role"]!!.jsonPrimitive.content)
        assertTrue(rec.bodies.single().contains("仕事の知見"))
    }
}
