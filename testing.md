# テスト・動作確認の知見

## Android アプリ：エミュレーターで確認する（GitHub Actions）
サンドボックスには SDK もエミュレーターもないので、CI 上のエミュレーターで実機確認の代わりをする。
**使うのは CLAUDE.md の「動作確認のルール」で許された場合だけ**（起動しない・真っ白・クラッシュなど、実行しないと原因が分からないとき）。
動かし方：コミットメッセージに `[emulator]` を含めてプッシュする。master にある `amazon-ai-reader-app.yml` なら、
Actions の手動実行（`workflow_dispatch`、入力 `emulator: true`）で作業ブランチを指定しても動かせる。
- 判定はコミットメッセージ全体（本文を含む）に対して行われる。本文で説明のために `[emulator]` と書いても動いてしまう（実際に起きた）。
  使わないときは、コミットメッセージのどこにもこの文字列を書かない。
実装例：`.github/workflows/amazon-ai-reader-app.yml` の `device-check` ジョブと `amazon-ai-reader-app/tools/device-check.sh`。

- ビルドジョブの後に `needs: build` で実行し、APK は `actions/upload-artifact` → `download-artifact` で受け渡す。
  （別ワークフローにすると、リリース上の古い APK を先に取ってしまう競合が起きる）
- KVM を有効にしてから `reactivecircus/android-emulator-runner@v2`（api-level 34 / x86_64 / google_apis）を使う。
- `script:` は1行ずつ別シェルで実行されるので、処理はシェルスクリプトにまとめて `bash xxx.sh` で呼ぶ。
- 集める情報：
  - `adb exec-out screencap -p > shot.png`（画面）
  - `adb shell uiautomator dump`（画面の部品とテキスト。描画されていなくても部品があるかが分かる）
  - `adb logcat -d`（`FATAL EXCEPTION` / `Killing ... <package>` を探す）
- 結果はリリース（例 `amazon-ai-reader-check`）に置き、サンドボックスから `curl -L` で取得して画像を Read で見る。
- **起動直後のエミュレーターは不安定**：Google Play 開発者サービス（gms.persistent）が再起動し、
  そのプロバイダ（FontsProvider など）に依存するアプリが巻き添えで kill される
  （ログ：`Killing ... depends on provider com.google.android.gms/... in dying proc`）。アプリの不具合ではない。
  → テスト前に `sleep 120` で落ち着かせる。
- `adb shell am start ... --es android.intent.extra.TEXT` に空白を含む文字列を渡すときは
  `"'テスト https://...'"` のように二重に引用する（しないと端末側で分割され、共有先の選択画面が出てしまう）。
- 画面の文字をタップするには、uiautomator の画面構造から `text="..."` の `bounds` を探して中心を `adb shell input tap` する（`tap_text` 関数。実例：`amazon-ai-reader-app/tools/device-check.sh`）。タブを切り替えて各画面を撮影できる。
- 共有インテントのテスト：`am start -a android.intent.action.SEND -t text/plain --es android.intent.extra.TEXT "'...'" -n <pkg>/.MainActivity`
- GitHub のサーバーから Amazon を開くと必ず確認画面（CAPTCHA）になるので、実際の変換結果はエミュレーターでは確認できない。

## Windows アプリ：CI のスモークテストで画面を確認する
- サンドボックスでは WPF をビルドできても実行はできない。`windows-latest` で exe に `--smoke-test <出力先>` を付けて起動し、
  全ページ（日本語・英語）とオーバーレイを PNG に保存して終了させる（実例：`padmapper/src/PadMapper.App/SmokeTest.cs`）。
- 画像・ログ・結果（`smoke-result.txt`）は zip にしてリリース `padmapper-check` に置き、サンドボックスから `curl -L` で取って Read で見る。
- スモークテストでは実際のキー入力を送らない（出力先を記録用に差し替える）。CI のランナーに入力が飛ぶのを防ぐため。

## Web アプリ：Playwright で確認する（サンドボックス内）
- Chromium は `/opt/pw-browsers/chromium` にある。`chromium.launch({ executablePath: '/opt/pw-browsers/chromium' })`。
- playwright はグローバルにしか入っていないので、ESM では `import { chromium } from '<npm root -g>/playwright/index.mjs'` と絶対パスで読み込む。
- テストスクリプトはスクラッチパッドに置き、リポジトリには残さない。
- 外部サイト（Amazon）はサンドボックスから接続できない。サーバーの確認は
  `node -r mock.cjs server.js` で `global.fetch` を差し替えたダミー応答で行う（短縮URLの転送・CAPTCHA・許可外ドメインへの転送を再現）。
- スマホ幅（390px）で `document.documentElement.scrollWidth` を見て横はみ出しを確認する。

## テストを書くときの落とし穴
- JS の `String.prototype.replace(文字列, 置換文字列)` は置換文字列中の `$$` `$&` などを特殊扱いする。
  コードを埋め込むときは `replace(x, () => code)` と関数で渡す。
- 自作のHTMLサンプルで通っても、本物の Amazon のページでは取れないことがある（実際にレビュー本文で起きた）。
  ユーザーに実ページで試してもらい、出力を見せてもらうのが最終確認。
