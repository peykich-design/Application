# 開発環境・ユーザー環境の知見

## Claude Code のクラウド環境（サンドボックス）
- 接続できないもの：Amazon（`www.amazon.co.jp`）、`dl.google.com`（Android SDK）。Maven Central は 429 になることがある。
- 接続できるもの：GitHub（API・リリースのダウンロード）、`maven.google.com`、`services.gradle.org`、npm、PyPI。
- `gh` CLI は使わず、GitHub の操作は MCP ツール（`mcp__github__*`）か公開 API への `curl` で行う。
- Python で外部ファイル（APK など）を解析するときは、スクラッチパッド内の専用フォルダに置く。
  `pip install` したパッケージは `~/.local` に入るので、`python3 -I` だと読めないことがある。

- 自動許可モードでは、**ダウンロードした外部ライブラリのコードを実行すると拒否される**（例：PyPI のパッケージを import して中の protobuf 定義を表示しようとした）。
  → 実行せずにデータとして読む。`*_pb2.py` の `AddSerializedFile(b'...')` は `ast.literal_eval` で取り出して、自作の小さなパーサーで
  FileDescriptorProto（message=4, field=2: name=1/number=3/type=5, enum=5）を読めばフィールド番号が分かる。

## claude.ai の Artifact（公開ページ）
- 外部への通信は CSP で全て遮断される。URL から取得する機能は Artifact 上では動かない（案内メッセージを出す）。
- `javascript:` リンクやファイルのダウンロードも無効。ブックマークレットはコピーボタンで渡す。
- Artifact 用のファイルは `<!doctype>` などを除いた形にする必要があるので、
  リポジトリの `index.html` から `tail -n +4` で先頭3行を落としてスクラッチパッドに書き出して公開している。

## ユーザーの環境
- Android 端末（タブレット／スマホ）。Chrome で GitHub のリリースから APK を入れる。
- Termux（F-Droid 版）の中に Ubuntu（proot-distro）があり、Node.js v24 が入っている。
  - Ubuntu に入る：`proot-distro login ubuntu`
  - Ubuntu 内のサーバーには Android の Chrome から `http://localhost:<port>` で接続できる。
  - Termux がバックグラウンドで止まるときは通知の「Acquire wakelock」。サーバー停止は CTRL → c。
- リポジトリは公開（`git clone` に認証不要）。2026年10月に `peykich-design/Android` から `peykich-design/Application` に改名した。
  旧 URL（クローン・リリースのダウンロード）は GitHub が転送するので動くが、新しく書くものは新しい名前を使う。
  ユーザーの Termux 内のクローンは `~/Android` フォルダのまま（`git remote set-url origin https://github.com/peykich-design/Application` で切り替え可能）。
- リポジトリを改名したら、Claude Code のセッションは旧名で紐づいたままになる。`add_repo` で新しい名前を追加し、
  作業フォルダで `git remote set-url origin <新URL>` すれば、同じフォルダのままプッシュできる。

## ユーザーへの案内のコツ
- 操作手順はスマホで実行できる形（コピーしやすい短いコマンド、画面上のボタン名）で書く。
- コマンドの実行結果やエラーはスクリーンショットで送ってもらうと早い。
