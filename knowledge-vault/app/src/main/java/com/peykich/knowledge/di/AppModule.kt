package com.peykich.knowledge.di

import android.content.Context
import androidx.room.Room
import com.peykich.knowledge.ai.AiProvider
import com.peykich.knowledge.ai.GeminiConfig
import com.peykich.knowledge.ai.GeminiProvider
import com.peykich.knowledge.ai.OkHttpGeminiTransport
import com.peykich.knowledge.ai.PromptBuilder
import com.peykich.knowledge.data.AppDatabase
import com.peykich.knowledge.data.AppEvents
import com.peykich.knowledge.data.DraftStore
import com.peykich.knowledge.data.NoteRepository
import com.peykich.knowledge.data.SecretStore
import com.peykich.knowledge.data.SettingsStore
import com.peykich.knowledge.data.VaultStorage
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import okhttp3.OkHttpClient
import java.io.File
import java.util.concurrent.TimeUnit
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides @Singleton
    fun database(@ApplicationContext context: Context): AppDatabase =
        // DB はキャッシュ。形が変わったら作り直し、Vault から読み直す（NoteRepository.rebuildIfEmpty）
        Room.databaseBuilder(context, AppDatabase::class.java, "knowledge.db")
            .fallbackToDestructiveMigration(dropAllTables = true)
            .build()

    @Provides @Singleton
    fun vault(@ApplicationContext context: Context): VaultStorage = VaultStorage(File(context.filesDir, "vault"))

    @Provides @Singleton
    fun repository(vault: VaultStorage, db: AppDatabase): NoteRepository = NoteRepository(vault, db.noteDao())

    @Provides @Singleton
    fun settings(@ApplicationContext context: Context): SettingsStore = SettingsStore(context)

    @Provides @Singleton
    fun secrets(@ApplicationContext context: Context): SecretStore = SecretStore(context)

    @Provides @Singleton
    fun drafts(@ApplicationContext context: Context): DraftStore = DraftStore(context)

    @Provides @Singleton
    fun events(): AppEvents = AppEvents()

    @Provides @Singleton
    fun okHttp(): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    @Provides @Singleton
    fun ai(
        @ApplicationContext context: Context,
        client: OkHttpClient,
        settings: SettingsStore,
        secrets: SecretStore,
        repository: NoteRepository,
    ): AiProvider = GeminiProvider(
        transport = OkHttpGeminiTransport(client),
        prompts = PromptBuilder { name -> context.assets.open("prompts/$name").bufferedReader().use { it.readText() } },
        config = {
            val s = settings.current()
            GeminiConfig(secrets.geminiKey(), s.formatModel, s.selectModel, s.answerModel)
        },
        existingTags = { repository.existingTags() },
    )
}
