# switchbot-panel/CLAUDE.md

ソファ横に常設した旧 Android スマホで、SwitchBot（Hub 2 配下の機器とシーン）を操作する専用パネルアプリ（アプリ名「ソファパネル」）。
仕様は「SwitchBotソファ操作パネル 仕様書」（2026-10-24）。MVP は「シーン・マクロ実行、マクロ編集、個別操作、常時表示、充電管理」。
このフォルダでの作業に関する決まりです。

## 構成（`app/src/main/java/com/peykich/sofapanel/`）
- `api/`：SwitchBot Open API v1.1
  - `Signer.kt`：署名（token + t + nonce を secret で HMAC-SHA256 → base64。大文字化はしない）
  - `SwitchBotClient.kt`：HTTP（`HttpURLConnection`、タイムアウト10秒）。`Transport` を差し替えてテストする
  - `ApiParser.kt` / `ApiError.kt`：応答の読み取りと、上部バーに出すエラー文（401 は「認証エラー（上限超過の可能性もあり）」）
- `domain/`
  - `CommandCatalog.kt`：機器の種類ごとに使えるコマンド（赤外線はカスタムボタンも可）
  - `Macro.kt`：マクロの形式（JSON）と規則（循環の拒否、操作50ステップまで、待ち1秒〜10分、展開）
  - `MacroRunner.kt`：実行（通信エラーは1回だけ再送、失敗は飛ばして続行、別マクロで中断・切替）
  - `ChargeController.kt`（判断だけ・テスト対象）と `ChargeManager.kt`（API 呼び出し）：充電管理
  - `TimeRules.kt`：夜間の時間帯、焼き付き対策のずらし量
- `data/BackgroundStore.kt`：背景画像（選んだ画像を長辺1920pxに縮めて `filesDir/background.jpg` に保存）。設定の `backgroundVersion` が変わると読み直す
- `data/`：`SecretStore.kt`（Keystore の AES-GCM で暗号化）、`SettingsStore.kt`（DataStore）、`JsonFiles.kt`（マクロと一覧キャッシュ）、`PanelRepository.kt`
- `googletv/`：Google TV を同じ LAN で直接操作（SwitchBot を通さない）。スマホの Google TV アプリのリモコンと同じ「Android TV Remote v2」
  - `Proto.kt`：必要最小限の protobuf（varint・長さ付きフィールド）と、長さ（varint）付きの送受信
  - `Identity.kt`：クライアント証明書（RSA 2048 の自己署名を DER で手組み）とペアリングの値（`PairingSecret`）
  - `GoogleTvClient.kt`：ペアリング（6467）とキー送信（6466）。TLS。TV の証明書はペアリング時の SHA-256 で照合
  - `GoogleTvManager.kt`：証明書と接続先の暗号化保存（`SecretStore.saveText`）、ペアリングの状態、NSD での検索（`_androidtvremote2._tcp.`）
  - 機器としては `Device(id="googletv", type="Google TV")`、コマンドは `commandType="googletv"`・`parameter=キーコード`。
    `PanelRepository.sendCommand()` が SwitchBot と振り分ける。画面やマクロで機器一覧を使うときは `vm.devices`（SwitchBot＋Google TV）を使う
- `service/PanelService.kt`：フォアグラウンドサービス（`specialUse`）。電池の監視、1分ごとの充電判定、温湿度の10分ごとの取得
- `ui/`：`MainScreen` / `StandbyScreen`（待機・夜間）/ `MacroEditorScreen` / `SettingsScreen`、`PanelViewModel`、`Theme.kt`（配色）、`PanelIcons.kt`（線画アイコン）
- `MainActivity.kt`：全画面・画面常時点灯・待機時の輝度・「戻すためのタップ」を画面に渡さない処理（`dispatchTouchEvent`）
- `app/src/main/res/font/outfit.ttf`：数字用フォント Outfit（可変フォント、OFL。ライセンスは `OFL-Outfit.txt`）
- `app/signing.keystore`：上書きインストール用の固定署名鍵（パスワード `android`。Google Play 用ではない）

## ルール
- トークンとシークレットは**ログ・例外メッセージ・画面に出さない**。保存は `SecretStore` だけ。
- API の呼び出し回数を増やす変更をするときは、受け入れ基準「24時間で通常利用500回以下」を意識する。
  一覧取得は起動時と手動のみ、温湿度は10分に1回、充電は切り替えの瞬間だけ。設定画面に「今日の API 呼び出し回数」が出る。
- 上部バーの一行は2種類：`repo.post()` は送信結果（次の結果まで残す）、`repo.notice()` は設定の保存などのお知らせ（4秒、エラーは10秒で消えて直前の送信結果に戻る）。
  設定画面の操作の通知に `post()` を使うと、メイン画面にずっと残ってしまう（実際に起きた）。
- 赤外線リモコンは状態を持たない（「送信しました」だけを出す）。
- 日本語フォントは端末標準（Android の日本語フォントは Noto Sans CJK で、Noto Sans JP と同じ字形）。ファイルを同梱すると APK が大きくなるため。
- カード・パネル・ボタン地の色は `PanelColors.Card` / `Button` に `LocalSurfaceAlpha` を掛けて透過させる（メイン画面だけ設定値、他の画面は不透明）。新しくメイン画面に面を足すときも同じようにする。
- 配色は `ui/Theme.kt` の `PanelColors`。アクセントは1色だけ（設定でアンバー・青・紫・緑）。

## Google TV の通信手順（Android TV Remote v2）
- 参照実装：Python の `androidtvremote2`（`pairing.py` / `remote.py`）。メッセージは「長さ varint ＋ protobuf」。
- ペアリング（6467）：`OuterMessage{protocol_version=2,status=200}` に pairing_request(10) → ack(11) → options(20) → options(20) → configuration(30) → ack(31) でテレビにコード表示 → secret(40) → secret_ack(41)。
- secret = SHA-256(クライアントの法, 指数, TV の法, 指数, コード後ろ4桁)。各数値は符号なしのバイト列。ハッシュの先頭1バイト＝コードの先頭2桁。
- 操作（6466）：TV から remote_configure(1) → 同じ番号で機能ビット（611）と device_info を返す → remote_set_active(2) を返す → ping(8) には pong(9) → remote_start(40) で操作可能 → remote_key_inject(10){key_code, direction=3(SHORT)}。
- キー：スリープ 223、起動 224、電源 26、ホーム 3、音量 24/25、消音 164。
- 単体テスト `GoogleTvTest` は、localhost に同じ手順の偽 TV（TLS・クライアント証明書必須）を立てて、ペアリングとキー送信を通しで確認している。
- 実機で未確認：スリープ中のテレビが 6466 に応答するか（機種と「ネットワーク待機」設定による）。

## ビルドと確認
- プッシュすると `.github/workflows/switchbot-panel.yml` がビルドと単体テスト → リリース `sofa-panel-latest` に APK を置く。
- 最新 APK：https://github.com/peykich-design/Application/releases/download/sofa-panel-latest/sofa-panel.apk
- `api/` と `domain/` の Android に依存しない部分と単体テストは、サンドボックスでも Kotlin コンパイラだけで確認できる（`../build.md`）。
- 実機の SwitchBot アカウントでの動作（API 呼び出し、赤外線の送信、プラグの切替）はユーザーの端末でしか確認できない。
