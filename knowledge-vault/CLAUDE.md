# knowledge-vault/CLAUDE.md

仕事の知見・趣味の知見・日記を「1タップで記録 → AI整形 → md保存 → 検索」まで行う Android アプリ（アプリ名「ナレッジ記録」）。
仕様は「ナレッジ記録アプリ 仕様書（MVP）」（2026-10-25）。保存先は Obsidian 互換の Vault（md ファイル）。
このフォルダでの作業に関する決まりです。

## 進み具合（仕様書 10.1 のマイルストーン）
- **M1 記録**：実装済み（ホーム、記録＝メモモード、Gemini 整形、プレビュー、原文保存、ローカル保存、frontmatter・index 生成、設定）。
  おまけで先に入れたもの：音声入力、ノート表示（共有のみ）、ブラウズ（日記カレンダーなし）、キーワード検索（FTS）。
- **M2 同期**（Google Drive）：未着手。Google Cloud の OAuth クライアントには `app/signing.keystore` の SHA-1 を登録する
  （`A7:8B:85:35:4D:3C:8C:62:76:5A:6F:3D:43:0A:28:1C:E3:07:C6:34`、パッケージ名 `com.peykich.knowledge`）。
- **M3 検索**（2段階の AI 検索）：未着手。`AiProvider.selectNotes` / `answer` とプロンプトは用意済み。
- **M4 閲覧・編集**：未着手。`AiProvider.editByInstruction` / `interviewTurn` とプロンプトは用意済み。

## 構成（`app/src/main/java/com/peykich/knowledge/`）
- `domain/`（Android に依存しない。単体テスト対象）
  - `Frontmatter.kt`：frontmatter の読み書き（YAML は一部の形だけ対応。知らないキーは `extra` に残して書き戻す）
  - `VaultLayout.kt`：フォルダ構成とファイル名（使えない文字は全角に、40文字で切る、重複は `_2`）
  - `NotePlanner.kt`：保存先と内容を決める（日記は1日1ファイルに `## HH:MM` で追記）。原文保存（`formatted: false`）
  - `IndexBuilder.kt`：`index.md` / `00_index.md` の生成
  - `SearchTokens.kt`：全文検索用の分かち書き（日本語は2文字ずつ）
- `ai/`：`AiProvider`（仕様書 9.1）、`GeminiProvider`（REST）、`GeminiProtocol`（要求・応答の JSON）、`PromptBuilder`
  - プロンプトの文面は `app/src/main/assets/prompts/*.md`（コードに書かない）
- `data/`：`NoteRepository`（保存・読み込み・index 再生成）、`VaultStorage`（`filesDir/vault/`、一時ファイル→リネームで書く）、
  `Database.kt`（Room：`notes` と `notes_fts`）、`SecretStore`（API キーを Keystore で暗号化）、`SettingsStore`（DataStore）、`DraftStore`（入力中の下書き）
- `di/AppModule.kt`：Hilt の提供元
- `ui/`：画面ごとのフォルダ（`home` `record` `note` `browse` `search` `settings`）と `AppRoot.kt`（画面遷移・下部ナビ・スナックバー）

## ルール
- **正本は Vault の md ファイル**。Room はキャッシュなので、スキーマを変えたら `version` を上げるだけでよい
  （作り直し → 起動時に `rebuildIfEmpty` で Vault から読み直す）。
- 画面は必ず `NoteRepository` を通して読み書きする（M2 で保存後に同期キューへ積む処理をここに足す）。
- `index.md` と `00_index.md` はアプリが生成する。手で直さない。ノートの保存のたびに作り直す。
- AI には本文と属性だけを JSON で返させ、frontmatter（id・日時）はアプリ側で組み立てる（仕様書 5章）。
- API キー・ノート本文をログに出さない。OkHttp にログ用の Interceptor を付けない。
- Vault・DB・API キーは自動バックアップの対象外（`res/xml/*`）。

## ビルドと確認
- プッシュすると `.github/workflows/knowledge-vault.yml` がビルドと単体テスト → リリース `knowledge-vault-latest` に APK を置く。
- 最新 APK：https://github.com/peykich-design/Application/releases/download/knowledge-vault-latest/knowledge-vault.apk
- `domain/` と `ai/`（`OkHttpGeminiTransport.kt` 以外）と単体テストは、サンドボックスでも Kotlin コンパイラだけで確認できる（`../build.md`）。
  単体テストは `assets/prompts` の本物の文面を読む。
- 実際の Gemini 呼び出しはユーザーの端末でしか確認できない（API キーが要る）。
