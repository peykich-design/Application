# padmapper/ の決まり

Steam 入力の「入力まわり」だけを切り出した Windows 用の常駐ソフト（仮称 PadMapper）。
主対象は Flydigi Vader 4 Pro（DInput モード）。仕様は `docs/SPEC.md`、機能の背景は `docs/feature-list.md`。

## 構成
| パス | 内容 |
|---|---|
| `src/PadMapper.Core/` | OS に依存しないロジック（net8.0）。Linux でもビルド・テストできる |
| `src/PadMapper.Core/Engine/` | バインディング判定（`BindingEngine`）、ジャイロ（`GyroProcessor`）、スティック、プロファイル選択、エンジン本体（`PadEngine`、専用スレッド1本） |
| `src/PadMapper.Core/Devices/` | 生レポート → `ControllerState` の変換定義（`DeviceDefinition`）。Vader 4 Pro の組み込み定義は `BuiltInDevices.cs` |
| `src/PadMapper.Core/Diagnostics/` | HEX ダンプ・差分、割り当てウィザードの判定（`MappingLearner`）、Markdown 書き出し |
| `src/PadMapper.Core/Profiles/` | プロファイル・設定の JSON、保存（5世代バックアップ・破損時の復元）、検証、マクロ |
| `src/PadMapper.App/` | WPF + WPF-UI の本体（net8.0-windows）。Win32 は `Native/Win32.cs` に集約 |
| `src/PadMapper.App/Input/` | Raw Input（専用スレッドのメッセージ専用ウィンドウ）、HID の列挙・直接読み取り、`ControllerManager` |
| `src/PadMapper.App/Resources/Strings.*.xaml` | 画面の文言（日本語/英語）。**キーは両方のファイルで揃える** |
| `tests/PadMapper.Core.Tests/` | Core の単体テスト（xUnit） |
| `docs/vader4pro-report.md` | Vader 4 Pro のレポート解析結果（Phase 0 の成果物） |

CI：`.github/workflows/padmapper.yml`（windows-latest でテスト → 単一 exe を発行 → `--smoke-test` で全ページを開いて画面を撮る）。
- exe：`https://github.com/peykich-design/Application/releases/download/padmapper-latest/PadMapper.exe`
- 起動確認の画面とログ：`https://github.com/peykich-design/Application/releases/download/padmapper-check/smoke.zip`

## 決まり
- NuGet は `WPF-UI` と `CommunityToolkit.Mvvm` のみ（テストは xUnit 一式）。追加するときは理由をコミットメッセージに書く。
- 純粋なロジックは Core に置き、単体テストを書く。ハードウェア依存は `IControllerSource` / `IOutputSink` の裏に隠す。
- 仮想コントローラーは出力しない（Phase 7 は `IOutputSink` の実装追加だけで足せる構造を保つ）。
- コントローラーには書き込まない。例外は設定「初期化コマンドを送る」（既定オフ）だけ。
- 押しっぱなしの出力は、プロファイル切替・一時停止・終了・切断・スリープ時に必ず全て離す（`BindingEngine.ReleaseAll`）。
- 画面の文言を足したら `Strings.ja.xaml` と `Strings.en.xaml` の両方に同じキーを足す。
  XAML の文字列は `{` で始めない（マークアップ拡張と解釈されるおそれがあるため）。
- `ImplicitUsings` は App では無効（WPF と WinForms の型名がぶつかるため）。`MessageBox` は `System.Windows.MessageBox` と書く。

## 確認のしかた
- Core のテスト：`dotnet test tests/PadMapper.Core.Tests`（サンドボックスでも動く）
- App のビルド：サンドボックスでもできる（手順はルートの `build.md`「Windows 向け .NET（WPF）をサンドボックスでビルドする」）
- 画面が開くか：CI のスモークテスト（`PadMapper.exe --smoke-test <出力先>`）。結果の画像は `padmapper-check` リリース
- 実機（コントローラー）での確認はユーザーに頼む。未確認のことは報告で明記する

## 実機で確かめること（未確認）
- Vader 4 Pro の独自レポート（`04 FE …`）が Raw Input で届くか。届かなければ設定の入力方式「HID の直接読み取り」
- SDL 由来のバイト位置（`BuiltInDevices.cs`）が実機と合うか。合わなければ診断のウィザードで取り直す
- 初期化コマンドなしでレポートが流れるか
