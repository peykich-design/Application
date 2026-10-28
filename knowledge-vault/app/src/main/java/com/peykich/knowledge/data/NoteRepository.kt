package com.peykich.knowledge.data

import com.peykich.knowledge.domain.Frontmatter
import com.peykich.knowledge.domain.IndexBuilder
import com.peykich.knowledge.domain.NoteDocument
import com.peykich.knowledge.domain.NoteDraft
import com.peykich.knowledge.domain.NoteMeta
import com.peykich.knowledge.domain.NotePlanner
import com.peykich.knowledge.domain.NoteType
import com.peykich.knowledge.domain.SearchTokens
import com.peykich.knowledge.domain.VaultLayout
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.time.ZonedDateTime

/** 開いたノート1件分。 */
data class LoadedNote(val entity: NoteEntity, val document: NoteDocument, val raw: String)

/**
 * ノートの保存・読み込みと index.md の再生成。画面は必ずここを通してローカル Vault を読み書きする。
 * （M2 で、保存のあとに同期キューへ積む処理をここに足す）
 */
class NoteRepository(
    private val vault: VaultStorage,
    private val dao: NoteDao,
    private val clock: () -> ZonedDateTime = { ZonedDateTime.now() },
) {
    private val lock = Mutex()

    fun recent(limit: Int = 5): Flow<List<NoteEntity>> = dao.recent(limit)

    fun observeAll(): Flow<List<NoteEntity>> = dao.observeAll()

    fun observe(id: String): Flow<NoteEntity?> = dao.observeById(id)

    /** 既存サブフォルダの一覧（整形のとき Gemini に渡す）。 */
    suspend fun subfolders(type: NoteType): List<String> =
        dao.pathsOfType(type.key).map { VaultLayout.subfolderOf(it) }.filter { it.isNotEmpty() }.distinct().sorted()

    /** 既存タグ（使われている回数の多い順）。 */
    suspend fun existingTags(): List<String> =
        dao.allTags().flatMap { it.split('\n') }.filter { it.isNotBlank() }
            .groupingBy { it }.eachCount().entries.sortedByDescending { it.value }.map { it.key }

    suspend fun load(id: String): LoadedNote? = withContext(Dispatchers.IO) {
        val entity = dao.byId(id) ?: return@withContext null
        val raw = vault.read(entity.path) ?: return@withContext null
        LoadedNote(entity, Frontmatter.parse(raw), raw)
    }

    /** 保存して、保存したノートの id を返す。日記はその日のファイルに追記する。 */
    suspend fun save(draft: NoteDraft): String = withContext(Dispatchers.IO) {
        lock.withLock {
            val now = clock()
            val planned = if (draft.type == NoteType.DIARY) {
                val path = VaultLayout.diaryPath(now)
                val existing = vault.read(path)?.let(Frontmatter::parse)
                NotePlanner.planDiary(draft, now, existing)
            } else {
                NotePlanner.planNew(draft, now, exists = { vault.exists(it) })
            }
            vault.write(planned.path, planned.content)
            index(planned.path, planned.meta, Frontmatter.parse(planned.content).body)
            regenerateIndexes()
            planned.meta.id
        }
    }

    /** 検索（FTS）。オフラインでも使える。 */
    suspend fun search(query: String, limit: Int = 50): List<NoteEntity> {
        val match = SearchTokens.query(query) ?: return emptyList()
        return runCatching { dao.search(match, limit) }.getOrDefault(emptyList())
    }

    suspend fun count(): Int = dao.count()

    /** DB が空なら（初回・DB の作り直し後）Vault から作り直す。 */
    suspend fun rebuildIfEmpty() {
        if (dao.count() == 0 && vault.listMarkdown().any(VaultLayout::isNote)) rebuildFromVault()
    }

    /** Vault の全ファイルを読み直して DB と index.md を作り直す。 */
    suspend fun rebuildFromVault(): Int = withContext(Dispatchers.IO) {
        lock.withLock {
            dao.clearNotes()
            dao.clearFts()
            var n = 0
            for (path in vault.listMarkdown().filter(VaultLayout::isNote)) {
                val raw = vault.read(path) ?: continue
                val doc = Frontmatter.parse(raw)
                val meta = doc.meta ?: fallbackMeta(path, doc)
                index(path, meta, doc.body)
                n++
            }
            regenerateIndexes()
            n
        }
    }

    private suspend fun index(path: String, meta: NoteMeta, body: String) {
        val entity = NoteEntity(
            id = meta.id,
            path = path,
            type = (VaultLayout.typeOf(path) ?: meta.type).key,
            title = meta.title.ifBlank { VaultLayout.stem(path) },
            summary = meta.summary,
            tags = meta.tags.joinToString("\n"),
            created = meta.created,
            updated = meta.updated,
            formatted = meta.formatted,
        )
        dao.put(
            entity,
            NoteFtsEntity(
                noteId = meta.id,
                title = SearchTokens.index(entity.title),
                summary = SearchTokens.index(meta.summary + " " + meta.tags.joinToString(" ")),
                body = SearchTokens.index(body),
            ),
        )
    }

    /**
     * frontmatter の無いノート（PC で作ったものなど）。id はパスから作る（ファイルには書かない）。
     * M2 で、取り込み時に Gemini で summary などを作って frontmatter を追記する。
     */
    private fun fallbackMeta(path: String, doc: NoteDocument): NoteMeta {
        val file = vault.file(path)
        val stamp = com.peykich.knowledge.domain.Dates.iso(
            ZonedDateTime.ofInstant(java.time.Instant.ofEpochMilli(file.lastModified()), clock().zone),
        )
        return NoteMeta(
            id = "path:$path",
            type = VaultLayout.typeOf(path) ?: NoteType.WORK,
            title = doc.extra["title"]?.let(Frontmatter::unquote) ?: VaultLayout.stem(path),
            created = stamp,
            updated = stamp,
            tags = Frontmatter.parseList(doc.extra["tags"]),
            summary = "",
            source = com.peykich.knowledge.domain.NoteSource.EXTERNAL,
        )
    }

    /** index.md と 00_index.md を notes テーブルから作り直す（内容が同じなら書かない）。 */
    private suspend fun regenerateIndexes() {
        val entries = dao.all().map { it.toIndexEntry() }
        for (t in NoteType.entries) vault.writeIfChanged(VaultLayout.categoryIndex(t), IndexBuilder.category(t, entries))
        vault.writeIfChanged(VaultLayout.ROOT_INDEX, IndexBuilder.root(entries))
    }
}
