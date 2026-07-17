# claude-task-bar — MVP セットアップ / 実行手順

Windows 常駐アプリ（.NET / WPF）＋ Claude Code の Stop フックで、
セッション完了時にマスコットが画面端から出て知らせる、最小構成。

## 必要なもの

- Windows 10/11
- .NET 8 SDK（`dotnet --version` が 8.x）
- WebView2 ランタイム（Win11 は標準搭載。無ければ「Evergreen WebView2 Runtime」を入れる）
- Claude Code（フック設定に使用）

## 1. ビルドして起動

```
cd %USERPROFILE%\Desktop\claude-task-bar\app
dotnet build -c Release
dotnet run -c Release
```

起動するとタスクトレイに常駐する（ウィンドウは出ない）。
トレイアイコンを**ダブルクリック**するとテスト通知が出る（マスコットが右下からスライドイン）。
マスコットを**クリック**すると退場する（本番では対象ウィンドウを前面化）。

## 2. 動作確認（フックなしで）

アプリ起動中に、別のターミナルから：

```
powershell -Command "Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8765/done?source=IDE'"
```

→ マスコットが出れば受信OK。

## 3. Claude Code の Stop フックを設定

`hooks/settings-snippet.json` の内容を、Claude Code の設定ファイル
（ユーザー全体: `%USERPROFILE%\.claude\settings.json`／プロジェクト: `.claude\settings.json`）の
`hooks` にマージする。リポジトリを Desktop 以外に置いた場合は notify.ps1 のパスを直す。

設定後、Claude Code のセッションが応答を終えるたびに `notify.ps1` が走り、
`localhost:8765` に完了通知 → マスコットが出る。

## 仕組み（データの流れ）

```
Claude Code (応答完了)
  -> Stop フック: notify.ps1
  -> GET http://127.0.0.1:8765/done?source=<フォルダ名>&hwnd=<端末ウィンドウ>
  -> ClaudeTaskBar (トレイ常駐 / NotifyServer)
  -> OverlayWindow (透明・最前面) の WebView2 が mascot.html を表示
  -> クリック -> WindowFocus が hwnd を前面化
```

## この MVP の制限（今後）

- フォーカスは「端末ウィンドウ」単位。Windows Terminal など複数タブの中の
  特定タブへの切り替えは未対応（ウィンドウを前面化するのみ）。
- web / Claude in Chrome / Claude Desktop 用アダプタは未実装（次段階）。
- オーバーレイ窓は表示中、右下の領域でクリックを受ける（透明部クリックスルーは今後）。
- ポートは 8765 固定（設定化は今後）。

## 次のステップ

1. web / Claude in Chrome 用のブラウザ拡張アダプタ（DOM監視 → 同じ /done を叩く）
2. Claude Desktop 用 UserNotificationListener アダプタ
3. タブ単位のフォーカス、複数同時通知のスタック表示、設定UI
