# claude-task-bar — セットアップ / 実行手順

Windows 常駐アプリ（.NET / WPF）。Claude Code のフックでセッションの
開始・実行・質問待ち・完了・終了を受け取り、セッションごとにペットが
タスクバー（画面右下のバー）に住む。再設計 v2（ペット方式）の実装。

設計の確定内容は `DESIGN.md`（リポジトリ直下）を参照。

## 必要なもの

- Windows 10/11
- .NET 10 SDK（`dotnet --version` が 10.x）
- Claude Code（フック設定に使用）

## 1. ビルドして起動

```
cd app
dotnet build -c Release
dotnet run -c Release
```

起動するとタスクトレイに常駐する。
トレイアイコンを**ダブルクリック**するとテスト用セッションのペットが増える。
トレイメニューから「完了にする」「質問待ちにする」「7件まとめて追加（あふれ確認）」も試せる。

## 2. 動作確認（フックなしで）

アプリ起動中に、別のターミナルから：

```
powershell -Command "Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body '{\"session_id\":\"manual-1\",\"type\":\"done\",\"source\":\"IDE\",\"surface\":\"ide\"}' 'http://127.0.0.1:8765/event'"
```

→ ペットバーにペットが出て、完了の吹き出しとオーバーレイが出れば受信OK。
旧形式の `GET /done?source=..&hwnd=..&type=..` も互換のため動き続ける。

## 3. Claude Code のフックを設定

`hooks/settings-snippet.json` の内容を、Claude Code の設定ファイル
（ユーザー全体: `%USERPROFILE%\.claude\settings.json`／プロジェクト: `.claude\settings.json`）の
`hooks` にマージする。インストーラ（install.ps1）経由なら自動で入る。

入るフックは5種類：SessionStart / UserPromptSubmit / Stop / Notification / SessionEnd。
`notify.ps1` は stdin の hook JSON から `hook_event_name` と `session_id` を読み、
イベント種別を判別する（呼び出し側の引数は不要）。

## 仕組み（データの流れ）

```
Claude Code の各フック
  -> notify.ps1（session_id / cwd / hook_event_name / hwnd / surface を収集）
  -> POST http://127.0.0.1:<port>/event  (JSON)
       失敗時は旧 GET /done?... にフォールバック
  -> ClaudeTaskBar (トレイ常駐 / NotifyServer)
  -> SessionRegistry（セッション台帳）に状態を記録
  -> PetBarWindow: セッションごとにペット1体（表示は最大5体、
       あふれは「+N」表示、優先順位は 質問待ち → 完了(未確認) → 実行中）
  -> ペットをクリック -> WindowFocus がそのセッションのウィンドウを前面化
     ペットの × -> ペットだけ消す（ジャンプしない）
  -> 完了/質問待ちの瞬間は従来の OverlayWindow もスライドインする（移行期間中）
```

## 設定

`%LOCALAPPDATA%\claude-task-bar\config.json`：

```json
{ "port": 8765 }
```

ポートだけ変更できる。アプリと notify.ps1 の両方が同じファイルを読む。
無い・壊れている場合は 8765 で起動する。

## テスト

コアロジック（台帳・表示選択・イベント解析・設定・受信パース）は
Windows 非依存に切り出してあるので、どこでも回せる：

```
cd tests\CoreTests
dotnet run
```

## 現在の制限

- フォーカスは「端末ウィンドウ」単位。Windows Terminal など複数タブの中の
  特定タブへの切り替えは未対応（ウィンドウを前面化するのみ）。
- web / Claude in Chrome / Claude Desktop 用アダプタは未実装（次段階）。
  ただしイベント形式は共通なので、同じ POST /event に送ればペットになる。
- 設定はポートのみ。設定UIは今後。

## 次のステップ

1. web / Claude in Chrome 用のブラウザ拡張アダプタ（DOM監視 → 同じ /event を叩く）
2. Claude Desktop 用 UserNotificationListener アダプタ
3. タブ単位のフォーカス、設定UI、OverlayWindow の段階的な置き換え完了
