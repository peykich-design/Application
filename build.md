# ビルド・CI の知見

## 前提：このリポジトリのビルド環境
- `android-template/` は Android アプリの**雛形**（Jetpack Compose + MVVM テンプレート）。2026年10月まではリポジトリのルート直下にあった。
  バージョン：AGP 9.1.0 / Gradle 9.4.0 / Kotlin 2.3.10 / Compose BOM 2026.02.01 / JDK 21 / compileSdk・targetSdk 36。
- AGP 9 は Kotlin を内蔵している。`app/build.gradle.kts` に `kotlin-android` プラグインは不要で、
  `android { kotlin { compilerOptions { jvmTarget = JvmTarget.JVM_21 } } }` の形で設定する（雛形と同じ書き方にすると安全）。
- Claude Code のクラウド環境（サンドボックス）には **Android SDK がなく、`dl.google.com` にも接続できない**ため、APK はローカルでビルドできない。
  Maven Central も 429（レート制限）になることがある。→ **ビルド確認は GitHub Actions で行う**。

## 新しいアプリを始める手順（雛形をコピーする）
新しい Android アプリを開発するときは、雛形（`android-template/`）を直接改造せず、**新しいフォルダを作って雛形の必要なファイルをコピーしてから始める**。
`amazon-ai-reader-app/` がこの手順で作った実例。

1. `<アプリ名>/` フォルダを作る
2. 雛形（`android-template/`）からコピーする：
   - `gradlew` / `gradlew.bat` / `gradle/wrapper/*`
   - `gradle.properties`
   - `gradle/libs.versions.toml`（バージョンはそのまま。使うライブラリだけ残す）
   - `settings.gradle.kts` / `build.gradle.kts` / `app/build.gradle.kts` の書き方（`rootProject.name`・`namespace`・`applicationId` を変える）
   - `app/src/main/res/xml/*`（バックアップ設定）
3. フォルダ専用の `.gitignore` を置く
   あわせてフォルダ専用の `CLAUDE.md`（構成・そのアプリだけの決まり）を作り、ルート CLAUDE.md の「リポジトリの構成」に追加する
4. スマホにインストールするなら固定の署名鍵を作る（下の「署名」）
5. `.github/workflows/<アプリ名>.yml` を作り、`paths: ["<アプリ名>/**"]` でそのフォルダの変更時だけビルドする。
   エミュレーター確認のジョブは、必要なときだけ動く形で付ける（testing.md）
6. サンドボックスでは APK をビルドできないので、プッシュして GitHub Actions の結果で確認する

## 新しいアプリのビルド設定（amazon-ai-reader-app で実績あり）
- 雛形（`android-template/`）から `gradlew` / `gradlew.bat` / `gradle/wrapper/*` / `gradle.properties` / `app/src/main/res/xml/*` をコピーする。
- `gradle/libs.versions.toml` は雛形のバージョンをそのまま使い、必要なライブラリだけに絞る（Hilt/Room/KSP などを使わないなら外す）。
- 依存を最小にすると初回ビルドが速く、失敗要因も減る。
- フォルダごとに `.gitignore`（`.gradle/` `build/` `local.properties` `.kotlin/`）を置く。ルートの `.gitignore` は `/build` しか除外しない。

## 署名（スマホへ直接インストールする場合）
- CI で毎回デバッグ鍵が作り直されると署名が変わり、**上書きインストールできなくなる**。
- 対策：固定の鍵 `app/signing.keystore` をリポジトリに置き、`signingConfigs` で debug/release 両方に使う。
  作成：`keytool -genkeypair -keystore app/signing.keystore -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=..."`
- これは個人インストール用。Google Play 配布には使わない。
- minSdk 26 以上だと APK は v2 署名のみになる（正常）。

## GitHub Actions
- ワークフローは**リポジトリ直下の `.github/workflows/`** にしか置けない。アプリごとに `paths:` で対象フォルダを絞る。
  例：`.github/workflows/amazon-ai-reader-app.yml`（`amazon-ai-reader-app/**` の変更でビルド）。
- 作業ディレクトリは `defaults.run.working-directory` で指定。`chmod +x ./gradlew` を忘れない。
- `workflow_dispatch`（手動実行）は**デフォルトブランチ（master）にあるワークフローしか起動できない**。
  作業ブランチだけにあるワークフローを API で起動すると 404。→ 作業中は `push` トリガー（paths 指定）で動かす。
- 同じワークフローの多重実行は `concurrency: { group: ..., cancel-in-progress: true }` で止める。
- ビルド時間の目安：assembleDebug + 単体テスト 約 5 分、エミュレーター確認込みで約 15 分。

## スマホへの配布（Releases を使う）
- Actions の artifact は zip でログイン必須なので、スマホからは扱いにくい。
- `gh release delete <tag> --yes --cleanup-tag || true` → `gh release create <tag> file.apk --prerelease --target $GITHUB_SHA` で
  **固定タグのリリースに毎回上書き**すると、常に同じ URL で最新 APK を配れる。
  `permissions: contents: write` と `GH_TOKEN: ${{ github.token }}` が必要。
- 例：`https://github.com/peykich-design/Application/releases/download/amazon-ai-reader-latest/amazon-ai-reader.apk`
- prerelease は `releases/latest` API には出てこない。確認は `releases/tags/<tag>` で行う。

## サンドボックスで Kotlin の一部をコンパイル・テストする
- Android SDK は無いが、**Android に依存しないコード（API の署名・解析、ルール判定など）と単体テスト**はサンドボックスで確認できる。
  CI の往復（約5分）を減らせる。実例：`switchbot-panel/` の `api/` と `domain/` の一部。
- Kotlin コンパイラは GitHub のリリースから取れる（Maven Central が 429 でも取れる）：
  `curl -sSL -O https://github.com/JetBrains/kotlin/releases/download/v2.3.10/kotlin-compiler-2.3.10.zip && unzip -q kotlin-compiler-2.3.10.zip`
- kotlinx.serialization を使うなら `-Xplugin=kotlinc/lib/kotlinx-serialization-compiler-plugin.jar` を付け、
  実行時ライブラリ（`kotlinx-serialization-core-jvm` / `-json-jvm`）と `junit` / `hamcrest-core` の jar を Maven Central から `curl` で取る。
- 例：`kotlinc/bin/kotlinc -Xplugin=... -cp <jar...> -d out <main の .kt> <test の .kt>` → `java -cp out:<jar...>:kotlinc/lib/kotlin-stdlib.jar org.junit.runner.JUnitCore <テストクラス>`
- 作業はスクラッチパッドで行い、ダウンロードしたものはリポジトリに入れない。

## CI の結果をサンドボックスから見る方法
- リポジトリは公開なので、`curl https://api.github.com/repos/<owner>/<repo>/actions/runs/<id>` で認証なしに状態を取れる。
  Bash の `run_in_background` で `until` ループを回して完了を待つ（`sleep` の直接実行は不可）。
- ジョブの成果物（スクリーンショット・ログ）はリリースに置けば `curl -L` で取得できる（artifact は認証が要る）。
- 失敗ログは GitHub MCP の `get_job_logs` で読める。

## Windows 向け .NET（WPF）をサンドボックスでビルドする
実例：`padmapper/`（WPF + WPF-UI）。
- サンドボックス（Ubuntu 24.04）には .NET が無い。`dot.net` / `builds.dotnet.microsoft.com` は 403 で、公式のインストールスクリプトは使えない。
  → `apt-get install -y dotnet-sdk-8.0`（Ubuntu の公式パッケージ）で入る。NuGet（`api.nuget.org`）には接続できる。
- **症状**：`dotnet build` で `Microsoft.NET.Sdk.WindowsDesktop.targets was not found`。
  原因：Ubuntu 版の SDK には WPF/WinForms 用の SDK（WindowsDesktop）が入っていない。
  対処：Microsoft のパッケージ（`packages.microsoft.com` は接続できる）から SDK の deb を取り、そのフォルダだけコピーする：
  ```
  curl -sSL -O https://packages.microsoft.com/ubuntu/22.04/prod/pool/main/d/dotnet-sdk-8.0/dotnet-sdk-8.0_8.0.426-1_amd64.deb
  dpkg-deb -x dotnet-sdk-8.0_8.0.426-1_amd64.deb x
  cp -r x/usr/share/dotnet/sdk/*/Sdks/Microsoft.NET.Sdk.WindowsDesktop /usr/lib/dotnet/sdk/8.0.*/Sdks/
  ```
  csproj に `<EnableWindowsTargeting>true</EnableWindowsTargeting>` を書けば、XAML を含めて Linux でビルドできる（実行はできない）。
- `SupportedOSPlatformVersion` を書くと Linux では `NETSDK1135`（TargetPlatformVersion 7.0 より高い）になるので書かない。
- OS に依存しないロジックは `net8.0` の別プロジェクト（`padmapper/src/PadMapper.Core`）に分けると、Linux で単体テストまで回せる。
- 実行の確認は GitHub Actions の `windows-latest` で行う。exe に `--smoke-test <出力先>` を付けると全ページを開いて画像に保存して終了する作りにし、
  結果をリリース（`padmapper-check`）に置いてサンドボックスから `curl -L` で取って見る（`.github/workflows/padmapper.yml`）。
- WPF-UI（4.0.3）のアイコン名（`SymbolRegular`）が存在するかは、`strings` で dll から名前を抜き出し、**末尾一致**で探す（`grep "Home24$"`）。
  メタデータの文字列は他の名前と末尾を共有して格納されるので、完全一致（`grep -x`）だと見つからないことがある。
