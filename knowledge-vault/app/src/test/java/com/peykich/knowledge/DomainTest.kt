package com.peykich.knowledge

import com.peykich.knowledge.domain.FileNames
import com.peykich.knowledge.domain.Frontmatter
import com.peykich.knowledge.domain.IndexBuilder
import com.peykich.knowledge.domain.IndexEntry
import com.peykich.knowledge.domain.NoteDraft
import com.peykich.knowledge.domain.NoteMeta
import com.peykich.knowledge.domain.NotePlanner
import com.peykich.knowledge.domain.NoteSource
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.domain.Ulid
import com.peykich.knowledge.domain.VaultLayout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.ZoneId
import java.time.ZonedDateTime

class DomainTest {
    private val tokyo = ZoneId.of("Asia/Tokyo")
    private val now = ZonedDateTime.of(2026, 10, 25, 16, 40, 12, 345_000_000, tokyo)

    private fun meta(title: String = "Excelの条件付き書式で重複を色分けする", summary: String = "COUNTIF を使った手順。") = NoteMeta(
        id = "01JB7Q3K8Z0000000000000000",
        type = NoteType.WORK,
        title = title,
        created = "2026-10-25T16:40:00+09:00",
        updated = "2026-10-25T16:40:00+09:00",
        tags = listOf("excel", "業務効率化"),
        summary = summary,
        source = NoteSource.MEMO,
    )

    @Test fun ulidIs26CharsAndSortsByTime() {
        val a = Ulid.generate(1_000_000)
        val b = Ulid.generate(2_000_000)
        assertEquals(26, a.length)
        assertTrue(a.all { it in "0123456789ABCDEFGHJKMNPQRSTVWXYZ" })
        assertTrue(a < b)
    }

    @Test fun frontmatterRoundTrip() {
        val m = meta(title = "A: B #タグ \"引用\"", summary = "1行目\n2行目")
        val text = Frontmatter.compose(m, "# 本文\n\n内容")
        assertTrue(text.startsWith("---\nid: 01JB7Q3K8Z"))
        assertTrue(text.contains("tags: [excel, 業務効率化]"))
        assertFalse(text.contains("formatted"))
        val doc = Frontmatter.parse(text)
        assertEquals(m.copy(summary = "1行目 2行目"), doc.meta)
        assertEquals("# 本文\n\n内容\n", doc.body)
    }

    @Test fun frontmatterFormattedFalseIsWritten() {
        val text = Frontmatter.compose(meta().copy(formatted = false), "x")
        assertTrue(text.contains("formatted: false\n"))
        assertEquals(false, Frontmatter.parse(text).meta?.formatted)
    }

    @Test fun parsesObsidianStyleList() {
        val text = "---\nid: X1\ntype: hobby\ntitle: 'キャンプ'\ntags:\n  - camp\n  - \"道具\"\nsummary: テスト\naliases: [a]\n---\n本文\n"
        val doc = Frontmatter.parse(text)
        assertEquals(listOf("camp", "道具"), doc.meta?.tags)
        assertEquals("キャンプ", doc.meta?.title)
        assertEquals(NoteSource.EXTERNAL, doc.meta?.source)
        assertEquals("[a]", doc.extra["aliases"])
        assertEquals("本文\n", doc.body)
        // 知らないキーは書き戻しで残る
        assertTrue(Frontmatter.compose(doc.meta!!, doc.body, doc.extra).contains("aliases: [a]"))
    }

    @Test fun noFrontmatterOrMissingFields() {
        assertNull(Frontmatter.parse("# PCで作ったノート\n").meta)
        val partial = Frontmatter.parse("---\ntitle: x\n---\n本文")
        assertNull(partial.meta)
        assertEquals("本文", partial.body)
        assertEquals("x", partial.extra["title"])
    }

    @Test fun crlfAndBom() {
        val text = "﻿---\r\nid: A\r\ntype: diary\r\nsummary: s\r\n---\r\nbody\r\n"
        val doc = Frontmatter.parse(text)
        assertEquals(NoteType.DIARY, doc.meta?.type)
        assertEquals("body\n", doc.body)
    }

    @Test fun sanitizeTitleReplacesAndCuts() {
        assertEquals("a／b：c？＃［x］", FileNames.sanitizeTitle("a/b:c?#[x]"))
        assertEquals("無題", FileNames.sanitizeTitle("  "))
        assertEquals("hidden", FileNames.sanitizeTitle("..hidden"))
        val long = "あ".repeat(50)
        assertEquals(40, FileNames.sanitizeTitle(long).length)
        assertEquals(VaultLayout.UNSORTED, FileNames.sanitizeFolder(""))
    }

    @Test fun uniquePathAddsSuffix() {
        val taken = setOf("work/excel/2026-10-25_t.md", "work/excel/2026-10-25_t_2.md")
        assertEquals("work/excel/2026-10-25_t_3.md", FileNames.uniquePath("work/excel", "2026-10-25_t") { it in taken })
    }

    @Test fun planNewWorkNote() {
        val draft = NoteDraft(NoteType.WORK, "重複を色分け", "excel", listOf("#excel", "excel", " 効率 "), "要約", "## 要点\n- a", NoteSource.MEMO)
        val p = NotePlanner.planNew(draft, now, { false }, id = "ID1")
        assertEquals("work/excel/2026-10-25_重複を色分け.md", p.path)
        assertEquals(listOf("excel", "効率"), p.meta.tags)
        assertEquals("2026-10-25T16:40:12+09:00", p.meta.created)
        val doc = Frontmatter.parse(p.content)
        assertEquals(p.meta, doc.meta)
        assertTrue(doc.body.startsWith("# 重複を色分け\n\n## 要点"))
    }

    @Test fun diaryNewAndAppend() {
        val d1 = NoteDraft(NoteType.DIARY, "", "", listOf("散歩"), "朝の散歩", "# 見出しは消す\n朝、散歩した。", NoteSource.MEMO)
        val first = NotePlanner.planDiary(d1, now, null, id = "D1")
        assertEquals("diary/2026/10/2026-10-25.md", first.path)
        assertEquals("2026-10-25（日）", first.meta.title)
        val body1 = Frontmatter.parse(first.content).body
        assertEquals("# 2026-10-25（日）\n\n## 16:40\n\n朝、散歩した。\n", body1)

        val later = now.plusHours(5)
        val d2 = d1.copy(tags = listOf("夕食"), summary = "夕食", body = "夜はカレー。")
        val second = NotePlanner.planDiary(d2, later, Frontmatter.parse(first.content))
        val doc = Frontmatter.parse(second.content)
        assertEquals("D1", doc.meta?.id)
        assertEquals(first.meta.created, doc.meta?.created)
        assertEquals(listOf("散歩", "夕食"), doc.meta?.tags)
        assertEquals("朝の散歩 / 夕食", doc.meta?.summary)
        assertTrue(doc.body.endsWith("## 16:40\n\n朝、散歩した。\n\n## 21:40\n\n夜はカレー。\n"))
    }

    @Test fun rawDraftKeepsText() {
        val raw = NotePlanner.rawDraft(NoteType.HOBBY, "\n  ギターの弦交換\n手順メモ", NoteSource.MEMO)
        assertEquals("ギターの弦交換", raw.title)
        assertFalse(raw.formatted)
        assertEquals(VaultLayout.UNSORTED, raw.subfolder)
        assertTrue(raw.body.contains("手順メモ"))
        val p = NotePlanner.planNew(raw, now, { false })
        assertEquals(false, Frontmatter.parse(p.content).meta?.formatted)
    }

    @Test fun summaryIsClipped() {
        val s = NotePlanner.clipSummary("あ".repeat(150))
        assertEquals(100, s.length)
        assertTrue(s.endsWith("…"))
    }

    @Test fun layoutHelpers() {
        assertTrue(VaultLayout.isIndex("work/index.md"))
        assertTrue(VaultLayout.isIndex("00_index.md"))
        assertFalse(VaultLayout.isNote("work/index.md"))
        assertFalse(VaultLayout.isNote(".obsidian/app.md"))
        assertFalse(VaultLayout.isNote("work/.trash/a.md"))
        assertFalse(VaultLayout.isNote("other/a.md"))
        assertTrue(VaultLayout.isNote("work/excel/a.md"))
        assertEquals("excel", VaultLayout.subfolderOf("work/excel/a.md"))
        assertEquals("2026/10", VaultLayout.subfolderOf("diary/2026/10/2026-10-25.md"))
        assertEquals("", VaultLayout.subfolderOf("work/a.md"))
        assertEquals(NoteType.HOBBY, VaultLayout.typeOf("hobby/x/a.md"))
    }

    @Test fun categoryIndex() {
        val entries = listOf(
            IndexEntry("work/excel/2026-10-20_古い.md", NoteType.WORK, "古い", "古い要約", listOf("excel"), "2026-10-20T10:00:00+09:00"),
            IndexEntry("work/excel/2026-10-25_新しい.md", NoteType.WORK, "新しい", "新しい要約", listOf("excel", "業務 効率化"), "2026-10-25T10:00:00+09:00"),
            IndexEntry("work/git/2026-10-21_rebase.md", NoteType.WORK, "rebase", "", emptyList(), "2026-10-21T10:00:00+09:00"),
            IndexEntry("hobby/camp/2026-10-21_x.md", NoteType.HOBBY, "x", "y", emptyList(), "2026-10-21T10:00:00+09:00"),
        )
        val text = IndexBuilder.category(NoteType.WORK, entries)
        val expected = """
            <!-- このファイルはアプリが自動生成します。手で編集しないでください -->
            # 仕事 index

            ## excel
            - [[2026-10-25_新しい]] — 新しい要約  #excel #業務_効率化
            - [[2026-10-20_古い]] — 古い要約  #excel

            ## git
            - [[2026-10-21_rebase]]
        """.trimIndent() + "\n"
        assertEquals(expected, text)
    }

    @Test fun rootIndex() {
        val entries = listOf(
            IndexEntry("work/a/2026-10-20_a.md", NoteType.WORK, "a", "sa", emptyList(), "2026-10-20T10:00:00+09:00"),
            IndexEntry("diary/2026/10/2026-10-25.md", NoteType.DIARY, "2026-10-25（日）", "sd", emptyList(), "2026-10-25T22:00:00+09:00"),
        )
        val text = IndexBuilder.root(entries)
        assertTrue(text.contains("- [[work/index|仕事]] — 1件\n- [[hobby/index|趣味]] — 0件\n- [[diary/index|日記]] — 1件\n"))
        val recent = text.substringAfter("## 最近更新したノート\n")
        assertEquals("- [[2026-10-25]] — sd（日記 / 2026-10-25）\n- [[2026-10-20_a]] — sa（仕事 / 2026-10-20）\n", recent)
        assertNotNull(text)
    }
}

class SearchTokensTest {
    @Test fun bigramsForJapanese() {
        assertEquals(listOf("条件", "件付", "付き", "excel", "書式"), com.peykich.knowledge.domain.SearchTokens.tokens("条件付き Excel、書式"))
        assertEquals(listOf("excel"), com.peykich.knowledge.domain.SearchTokens.tokens("ＥＸＣＥＬ"))
    }

    @Test fun queryBuildsPhrases() {
        assertEquals("\"重複 複色\" excel*", com.peykich.knowledge.domain.SearchTokens.query("重複色　Excel"))
        assertEquals("猫*", com.peykich.knowledge.domain.SearchTokens.query("猫"))
        assertEquals("書式", com.peykich.knowledge.domain.SearchTokens.query("書式"))
        assertEquals(null, com.peykich.knowledge.domain.SearchTokens.query(" 、 "))
    }

    @Test fun indexContainsQueryPhrase() {
        val doc = com.peykich.knowledge.domain.SearchTokens.index("Excelの条件付き書式で重複を色分けする")
        val q = com.peykich.knowledge.domain.SearchTokens.query("条件付き書式")!!.trim('"')
        assertTrue(doc.contains(q))
    }
}
