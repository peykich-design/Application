# CLAUDE.md

このリポジトリ（`peykich-design/Application`）で作業するときの案内です。作業を始める前に、関係する知見ファイルを読んでください。

Android に限らず、Web・サーバーなど複数のアプリを**アプリごとのフォルダ**に分けて置くリポジトリです。
（2026年10月に `Android` から改名。旧 URL は GitHub が自動で新 URL に転送する）

## リポジトリの構成
| パス | 内容 |
|---|---|
| `android-template/` | Android アプリの**雛形**（Jetpack Compose + MVVM テンプレート）。直接は改造しない。決まりは `android-template/CLAUDE.md` |
| `amazon-to-ai/` | Amazon のページを AI 向け Markdown/JSON に変換する Web アプリと、URL 取得用の Node サーバー。決まりは `amazon-to-ai/CLAUDE.md` |
| `amazon-ai-reader-app/` | 上記の Android アプリ版（WebView でページを開いて変換。共有メニュー対応）。決まりは `amazon-ai-reader-app/CLAUDE.md` |
| `switchbot-panel/` | SwitchBot の機器とシーンをソファ横の常設スマホから操作するパネルアプリ（Android）。決まりは `switchbot-panel/CLAUDE.md` |
| `knowledge-vault/` | 仕事・趣味の知見と日記を1タップで記録し、Gemini で整形して Obsidian 互換の md に保存するアプリ（Android）。決まりは `knowledge-vault/CLAUDE.md` |
| `padmapper/` | Steam 入力の代わりに、コントローラー（Vader 4 Pro）の入力をキーボード/マウスに変換する Windows 用常駐ソフト（C# / WPF）。決まりは `padmapper/CLAUDE.md` |
| `.github/workflows/padmapper.yml` | PadMapper を Windows でビルド・単体テスト・起動確認 → exe をリリースに配置 |
| `.github/workflows/knowledge-vault.yml` | ナレッジ記録アプリのビルドと単体テスト → APK をリリースに配置 |
| `.github/workflows/switchbot-panel.yml` | パネルアプリのビルドと単体テスト → APK をリリースに配置 |
| `.github/workflows/amazon-ai-reader-app.yml` | Android アプリ版のビルドと単体テスト → APK をリリースに配置（エミュレーター確認は指定したときだけ） |
| `.github/workflows/*-build-and-test-workflow.yml` | 雛形（`android-template/`）のビルドと単体テスト（master への push / PR 時） |
| `.github/workflows/gemini.yml` | Issue やコメントに `@gemini-cli` と書くと Gemini CLI が応答する（リポジトリの Secret `GEMINI_API_KEY` を使用）。アプリのビルドとは無関係 |

## 知見ファイルの目次
| ファイル | 内容 | 読むタイミング |
|---|---|---|
| [build.md](build.md) | 雛形のバージョン、Gradle 設定、固定署名鍵、GitHub Actions、APK をリリースで配る方法、CI 結果の取り方、Windows 向け .NET（WPF）のビルド | アプリのビルド設定やワークフローを触るとき。サンドボックスで APK や Windows アプリが作れないと気づいたとき |
| [testing.md](testing.md) | CI のエミュレーターで画面・ログを取る方法と注意点、Playwright での Web 確認、ダミー応答でのサーバー確認 | 動作確認をするとき。アプリが起動しない・落ちる原因を調べるとき |
| [android.md](android.md) | Compose と WebView の落とし穴（白い画面）、WebView からの読み取り、共有メニュー対応、インストール時のトラブル | Android アプリの画面や機能を実装するとき |
| [amazon.md](amazon.md) | Amazon ページの取得方法の比較、URL の正規化と許可ドメイン、CAPTCHA の見分け方、HTML 構造、評価表記などの落とし穴 | 変換ロジック（extractor）や取得処理を直すとき |
| [ai-api.md](ai-api.md) | Claude（Java SDK）・Gemini（REST）の呼び出し方、Android での注意点、プロンプトの書き方 | AI の API を使う機能を作る・直すとき |
| [environment.md](environment.md) | サンドボックスで繋がる/繋がらない先、Artifact の制限、ユーザーの端末環境（Termux + Ubuntu） | 環境が原因っぽいエラーのとき。ユーザーに操作手順を案内するとき |

新しい知見が得られたら、該当するファイルに追記する。いつ・何を・どう書くかは [knowledge.md](knowledge.md) に従う。

## ルール

### フォルダ固有の内容は、そのフォルダの CLAUDE.md に書く
特定のフォルダの中でだけ有効な決まりや情報（構成、ファイル間で同期が必要な箇所、そのフォルダ専用のビルド・公開手順など）は、
ルートの CLAUDE.md ではなく**そのフォルダ直下の `CLAUDE.md`** に書く。フォルダに無ければ新しく作り、上の「リポジトリの構成」の表から参照する。
ルートの CLAUDE.md と知見ファイル（build.md など）には、リポジトリ全体や複数のフォルダに共通する内容だけを書く。

### 新しいアプリは、新しいフォルダで始める
種類（Android・Web・サーバーなど）に関係なく、新しいアプリは `<アプリ名>/` フォルダを作ってその中で開発する。
ルート直下にはアプリのファイルを置かない（リポジトリ全体の知見ファイル・`CLAUDE.md`・`README.md`・`LICENSE` だけにする）。
フォルダを作ったら、そのフォルダの `CLAUDE.md` を作り、上の「リポジトリの構成」の表に1行追加する。
CI が必要なら `.github/workflows/<アプリ名>.yml` を作り、`paths: ["<アプリ名>/**"]` でそのフォルダの変更時だけ動かす。

### Android アプリは「雛形をコピーした新しいフォルダ」で作る
新しい Android アプリは、雛形（`android-template/`）を直接改造せず、`<アプリ名>/` フォルダを作って雛形の必要なファイルをコピーしてから始める。
手順は [build.md](build.md) の「新しいアプリを始める手順」、実例は `amazon-ai-reader-app/`。

### 作業の進め方
- 作業は作業ブランチで行い、プルリクエストを作って master に取り込む。master へ直接プッシュしない。
- 新しいセッションは master から始まる。`CLAUDE.md` や知見ファイルの更新も、master に取り込まれて初めて次回以降に読み込まれる。
- **プルリクエストは、次のタイミングで Claude が自分から作る**（ユーザーの指示を待たない）：
  1. 機能の追加や不具合の修正がひと区切りついたとき。CI のビルドとユニットテストが通ってから作る
  2. `CLAUDE.md` や知見ファイルを更新したとき（次回のセッションに引き継ぐため、早めに出す）
  3. ユーザーから「PRを作って」と言われたとき
  作業の途中や、確認が通っていない状態では作らない。1つのプルリクエストには1つの機能・修正だけを入れ、スマホで確認できる大きさに保つ。
- **Merge（master への取り込み）はユーザーが判断して行う**。Claude は「マージして」と指示されたときだけ Merge する。
  プルリクエストを作ったら、URL と「問題なければ Merge してください」をユーザーに伝える。
- マージ後に新しい作業を始めるときは、master の最新から作業ブランチを作り直す（マージ済みの履歴の上に積み足さない）。
- 作業ブランチにだけあるワークフローは手動実行（workflow_dispatch）できない。`push` トリガーで動かす。

### 動作確認のルール
- 通常はビルド成功とユニットテストで確認する。エミュレーターは使わない。
- 起動しない・画面が真っ白・クラッシュするなど、実行しないと原因が特定できない場合のみエミュレーターを使ってよい。
  動かし方（コミットメッセージに `[emulator]` を含める等）は [testing.md](testing.md)。
- エミュレーターを使ったら、確認した内容と原因を報告すること。
- 確認できなかったこと（API キーが必要な呼び出し、実ページでの変換、画面の見た目など）は、確認できていないと明記して報告する。

### 言語
- ユーザーとのやりとり、`CLAUDE.md`・知見ファイル・README などのドキュメント、アプリの画面の文言は日本語。
- コミットメッセージとプルリクエストのタイトルは英語。

### 秘密情報
- API キー・トークン・パスワードはリポジトリに入れない（コード、設定ファイル、ドキュメント、テスト用データを含む）。
  CI で必要なものは GitHub の Secrets に置く（例：`GEMINI_API_KEY`）。アプリでは利用者が画面から入力する。
- 例外は、公開して問題ないと決めた個人インストール用の署名鍵（`amazon-ai-reader-app/app/signing.keystore`、`switchbot-panel/app/signing.keystore`）だけ。

### その他
- ユーザーはスマホから操作していることが多い。手順は短く、スマホで実行できる形で案内する。
