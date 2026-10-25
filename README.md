# Application

個人で開発しているアプリをまとめたリポジトリです。アプリごとにフォルダを分けています。

| フォルダ | 内容 |
|---|---|
| [`amazon-to-ai/`](amazon-to-ai/) | Amazon の商品ページ・検索結果を、AI が読みやすい Markdown / JSON に変換する Web アプリ（URL 取得用の Node サーバー付き） |
| [`amazon-ai-reader-app/`](amazon-ai-reader-app/) | 上記の Android アプリ版。共有メニューから変換し、複数の商品を AI（Claude / Gemini）で比較できる |
| [`padmapper/`](padmapper/) | Steam 入力の代わりに、コントローラー（Flydigi Vader 4 Pro）の入力をキーボード・マウスに変換する Windows 用の常駐ソフト |
| [`android-template/`](android-template/) | 新しい Android アプリを作るときの雛形（Jetpack Compose + MVVM） |

## Android アプリのインストール
最新の APK：https://github.com/peykich-design/Application/releases/download/amazon-ai-reader-latest/amazon-ai-reader.apk

## Windows ソフトのダウンロード
PadMapper：https://github.com/peykich-design/Application/releases/download/padmapper-latest/PadMapper.exe

## 開発メモ
開発で得た知見は、ルートの `build.md`・`testing.md`・`android.md` などにまとめています。目次は [`CLAUDE.md`](CLAUDE.md)。
