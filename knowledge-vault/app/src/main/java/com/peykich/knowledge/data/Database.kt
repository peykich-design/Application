package com.peykich.knowledge.data

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Fts4
import androidx.room.Index
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.Upsert
import com.peykich.knowledge.domain.IndexEntry
import com.peykich.knowledge.domain.NoteType
import kotlinx.coroutines.flow.Flow

/**
 * ノートのメタ情報のキャッシュ（仕様書 8.3 の notes）。正本は Vault の md ファイルで、
 * DB が空になっても Vault から作り直せる（NoteRepository.rebuildFromVault）。
 */
@Entity(tableName = "notes", indices = [Index(value = ["path"], unique = true)])
data class NoteEntity(
    @PrimaryKey val id: String,
    val path: String,
    val type: String,
    val title: String,
    val summary: String,
    /** タグを改行区切りで入れる。 */
    val tags: String,
    val created: String,
    val updated: String,
    val formatted: Boolean,
)

// Room の列にしないよう、計算で出す値は拡張プロパティにしている
val NoteEntity.noteType: NoteType get() = NoteType.fromKey(type) ?: NoteType.WORK
val NoteEntity.tagList: List<String> get() = tags.split('\n').filter { it.isNotBlank() }

fun NoteEntity.toIndexEntry() = IndexEntry(path, noteType, title, summary, tagList, updated)

/** オフライン検索と候補の絞り込み用（FTS4）。中身は SearchTokens で2文字ずつに分けた文字列。 */
@Fts4
@Entity(tableName = "notes_fts")
data class NoteFtsEntity(
    @ColumnInfo(name = "note_id") val noteId: String,
    val title: String,
    val summary: String,
    val body: String,
)

@Dao
abstract class NoteDao {
    @Query("SELECT * FROM notes ORDER BY updated DESC LIMIT :limit")
    abstract fun recent(limit: Int): Flow<List<NoteEntity>>

    @Query("SELECT * FROM notes ORDER BY updated DESC")
    abstract fun observeAll(): Flow<List<NoteEntity>>

    @Query("SELECT * FROM notes")
    abstract suspend fun all(): List<NoteEntity>

    @Query("SELECT COUNT(*) FROM notes")
    abstract suspend fun count(): Int

    @Query("SELECT * FROM notes WHERE id = :id")
    abstract suspend fun byId(id: String): NoteEntity?

    @Query("SELECT * FROM notes WHERE id = :id")
    abstract fun observeById(id: String): Flow<NoteEntity?>

    @Query("SELECT * FROM notes WHERE path = :path")
    abstract suspend fun byPath(path: String): NoteEntity?

    @Query("SELECT path FROM notes WHERE type = :type")
    abstract suspend fun pathsOfType(type: String): List<String>

    @Query("SELECT tags FROM notes")
    abstract suspend fun allTags(): List<String>

    @Upsert
    abstract suspend fun upsertNote(note: NoteEntity)

    @Query("DELETE FROM notes WHERE id = :id")
    abstract suspend fun deleteNote(id: String)

    @Query("DELETE FROM notes_fts WHERE note_id = :id")
    abstract suspend fun deleteFts(id: String)

    @androidx.room.Insert
    abstract suspend fun insertFts(row: NoteFtsEntity)

    @Query("DELETE FROM notes")
    abstract suspend fun clearNotes()

    @Query("DELETE FROM notes_fts")
    abstract suspend fun clearFts()

    @Query(
        "SELECT notes.* FROM notes JOIN notes_fts ON notes.id = notes_fts.note_id " +
            "WHERE notes_fts MATCH :match ORDER BY notes.updated DESC LIMIT :limit",
    )
    abstract suspend fun search(match: String, limit: Int): List<NoteEntity>

    /** パスが変わったとき（同じ id で別の場所）に古い行を消してから入れる。 */
    @Transaction
    open suspend fun put(note: NoteEntity, fts: NoteFtsEntity) {
        byPath(note.path)?.takeIf { it.id != note.id }?.let { deleteNote(it.id); deleteFts(it.id) }
        upsertNote(note)
        deleteFts(note.id)
        insertFts(fts)
    }

    @Transaction
    open suspend fun remove(id: String) {
        deleteNote(id)
        deleteFts(id)
    }
}

@Database(entities = [NoteEntity::class, NoteFtsEntity::class], version = 1, exportSchema = true)
abstract class AppDatabase : RoomDatabase() {
    abstract fun noteDao(): NoteDao
}
