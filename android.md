# Android アプリ実装の知見

## Compose と WebView
- **WebView を `verticalScroll` の中に置くと、白い背景が画面全体を覆って真っ白になる**（高さ 1dp にしても起きた）。
  画面の部品は存在する（uiautomator には出る）のに何も見えない、という症状になる。
  → WebView はスクロール領域の外に置き、使わないときは `update = { it.visibility = View.INVISIBLE }` にする。
  `Modifier.clipToBounds()` も付ける。
- `View.INVISIBLE` の WebView でもページの読み込みと JavaScript は動く（描画だけ止まる）。
- WebView は `remember { WebView(context) }` で作り、`DisposableEffect` の `onDispose` で `destroy()` する。
  初回生成は重く、起動時に 1〜2 秒のフレーム落ち（`Davey!`）が出るが動作には問題ない。

## WebView でページを読み取る
- `evaluateJavascript` の戻り値は **JSON の文字列リテラル**（`"\"{...}\""` や `null`）。
  `JSONTokener(raw).nextValue() as? String` で中身を取り出してから `JSONObject` にする。
- スクリプトは `(function(){ ...; return JSON.stringify(結果); })();` の形にすると受け渡しが簡単。
- `onPageFinished` は転送などで何度も呼ばれる。`Handler` で前回の予約を取り消してから少し待って実行する（デバウンス）。
- 後から読み込まれる部分（レビューなど）は、`window.scrollTo(0, document.body.scrollHeight)` してから約 2 秒待つ。
- サイトの PC 版と同じ HTML 構造が欲しいときは `userAgentString` を PC の Chrome にし、
  `useWideViewPort` / `loadWithOverviewMode` を true にする。
- `shouldOverrideUrlLoading` で許可ドメイン以外への移動や `intent://` などを止める。
- 変換ロジックは `assets/` に JS で置き、Web 版と同じコードを使い回すと二重実装を避けられる
  （ただしコピーなので、片方を直したらもう片方にも反映すること）。

## 共有メニューから受け取る
- マニフェストに `ACTION_SEND` + `text/plain` の intent-filter を追加する。
  intent-filter の `android:label` が共有メニューの下段に表示される（例：「AI Reader / AI用に変換」）。
- `launchMode="singleTop"` にして、起動中に共有されたら `onNewIntent` で受け取る。
- Amazon アプリの共有テキストは「商品名 https://amzn.asia/d/...」の形。正規表現で URL だけを取り出す。
- 受け取った値は `mutableStateOf` に入れ、画面側の `LaunchedEffect(sharedUrl)` で処理してから null に戻す。

## その他の UI
- クリップボードは Compose の `LocalClipboardManager`（非推奨化が進んでいる）より、
  `context.getSystemService(ClipboardManager::class.java)` を直接使う方が API の変化に強い。
- `enableEdgeToEdge()` + `Scaffold` の `innerPadding` + `imePadding()` でシステムバーとキーボードを避ける。
- テーマは `android:Theme.Material.Light.NoActionBar` で十分（Compose 側で MaterialTheme を当てる）。
- アイコンは minSdk 26 なら `mipmap-anydpi-v26` の adaptive-icon とベクター drawable だけで足りる。

## インストール時のトラブル（ユーザー端末）
- 「アプリがインストールされていません」は APK が正常でも出る。APK 側の確認方法：
  androguard で署名（v2）・minSdk・`testOnly` の有無、zip 内の `.so` が 16KB 境界に揃っているかを見る。
- 端末側の原因と対処：Chrome のダウンロード画面ではなく「ファイル」アプリから開く／
  Play プロテクトのスキャンを一時オフ／Galaxy の「自動ブロッカー」を一時オフ。
  今回は端末側の設定変更でインストールできた。

## Room の全文検索（FTS4）と日本語
- FTS4 の標準の区切り（simple）は日本語を単語に分けないため、文が丸ごと1語になり、部分一致で見つからない。
  → 漢字・かな・カナの連続を2文字ずつ（バイグラム）に分けて空白区切りで FTS に入れ、検索語も同じく分けてフレーズ検索（`"条件 件付 付き"`）にする。
  1文字の語は前方一致（`猫*`）。実例：`knowledge-vault/app/src/main/java/com/peykich/knowledge/domain/SearchTokens.kt`。
