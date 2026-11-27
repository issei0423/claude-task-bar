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

→ ペットバーにペットが出て、完了の吹き出しが出れば受信OK。
（画面の端から大きいマスコットが滑り出る旧通知は 2026-11-04 にやめた。知らせるのはペットバーだけ）
そのペットを押すとそのセッションへ飛ぶ。ペットは消えず、席へ戻って立つ（完了 → 確認済み）。
右クリックで消せる。消えたペットは、同じコマンドをもう一度送ると、また出る。
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
  -> notify.ps1（session_id / cwd / hook_event_name / hwnd / surface を収集。
       親プロセスをさかのぼって、最初の claude / node の PID を pid として足す（無ければ 0）。
       Orca の中なら環境変数 ORCA_TERMINAL_HANDLE を orca_terminal として足す。
       形 term_[A-Za-z0-9_-]{1,128} に合わない値は送らない）
  -> POST http://127.0.0.1:<port>/event  (JSON)
       失敗時は旧 GET /done?... にフォールバック
  -> ClaudeTaskBar (トレイ常駐 / NotifyServer)
  -> SessionRegistry（セッション台帳）に状態を記録
       （アプリの起動時と5秒おきに、Claude Code の動いているセッションの一覧
       %USERPROFILE%\.claude\sessions\<pid>.json（読むだけ）も読み、台帳に無い
       対話セッションのペットを足す。アプリを起こし直しても、開いているセッションの
       ペットがすぐ出る。状態を動かすのはフックで、一覧は居るかどうかと最初の状態
       （busy → 作業中、ほか → 席で立つ）にだけ使う。読めなければフックだけで動く）
  -> PetBarWindow: セッションごとにペット1体（表示は最大5体、
       あふれは「+N」表示、優先順位は 質問待ち → 完了(未確認) → 実行中）
  -> ペットをクリック -> WindowFocus がそのセッションのウィンドウを前面化
       （窓は、そのセッションのコンソール（pid の AttachConsole → GetConsoleWindow →
       大元の持ち主）に聞く。聞けなければフックの hwnd。explorer の窓には飛ばない。
       Windows Terminal では、コンソールの題と同じ名前のタブがちょうど1つあれば、そのタブも選ぶ。
       押すたびに %TEMP%\ctb.log に「jump: <名前> pid=.. window=.. (console|fallback|none) tab=..」の行が出る。
       題を手で変えたタブ・同じ題のタブが2つあるときは、窓だけ前に出る）
       orca_terminal があれば、続けて OrcaJump が
       orca.exe terminal switch --terminal <orca_terminal> --json を呼び、
       Orca の中のそのタブに切り替える（上限5秒。失敗しても窓は出たまま、
       理由は %TEMP%\ctb.log に1行）
       ペットは消えない。知らせを引っこめて席へ戻る
       （完了(未確認) → 完了(確認済み)で立つ、質問待ち → 実行中でノートPCを叩く。実行中はそのまま）
     ペットが居なくなるとき：
       ・SessionEnd のイベントが来た
       ・pid のプロセスが無くなった（5秒おきに確認。pid が使い回されたときも）。
         pid が無いセッションは、hwnd の窓が無くなったとき。どちらも無いもの（トレイのテスト用）は消えない
       ・ペットを右クリック -> ペットだけ消す（ジャンプしない）
     右クリックで消したあとも、そのセッションの次のイベントでまた出る
  （旧 OverlayWindow のスライドイン通知は呼ばない。完了・質問待ちもペットバーで知らせる）
```

## 設定

`%LOCALAPPDATA%\claude-task-bar\config.json`：

```json
{ "port": 8765, "orcaPath": "D:\\Tools\\orca\\resources\\bin\\orca.exe", "petScale": 3 }
```

- `port`：受信ポート。アプリと notify.ps1 の両方が同じファイルを読む。
  無い・壊れている場合は 8765 で起動する。
- `orcaPath`（省略可）：Orca のタブ切り替えに使う `orca.exe` のフルパス。
  `.exe` で終わるフルパスのときだけ使う。省略時は
  `%LOCALAPPDATA%\Programs\orca\resources\bin\orca.exe` → PATH の各フォルダの
  `orca.exe` の順で探す（`orca.cmd` は使わない）。見つからなければタブは切り替えず、
  窓を前面化するだけ。設定はアプリの起動時に1回だけ読む。
- `petScale`（省略可）：ペットの絵の1ドットを画面の何ピクセルで描くか。整数の 1〜6、
  省略・範囲の外・数でない値は 2。絵は 32×32 ドットなので、2 なら 64×64 ピクセル。
  大きい画面や高い解像度では 3 や 4 にすると見やすい。

## ペットの絵を作り直したとき

絵の正は `tools/sprites/sprite-book.html`。絵を変えたら、リポジトリ直下で次を回して
`app/assets/pet`（アニメごとの横1列 PNG と `pet.json`）に書き出し、アプリを入れ直す：

```
node tools\sprites\export.js
```

（Node.js が要る。npm のパッケージは要らない。）

## テスト

コアロジック（台帳・表示選択・イベント解析・設定・受信パース）は
Windows 非依存に切り出してあるので、どこでも回せる：

```
cd tests\CoreTests
dotnet run
```

## 現在の制限

- フォーカスは「端末ウィンドウ」単位。タブまで切り替えるのは Orca の中だけ。
  Windows Terminal など、ほかの複数タブの端末の特定タブへの切り替えは未対応
  （ウィンドウを前面化するのみ）。
- web / Claude in Chrome / Claude Desktop 用アダプタは未実装（次段階）。
  ただしイベント形式は共通なので、同じ POST /event に送ればペットになる。
- 設定はポートと orcaPath のみ。設定UIは今後。

## 次のステップ

1. web / Claude in Chrome 用のブラウザ拡張アダプタ（DOM監視 → 同じ /event を叩く）
2. Claude Desktop 用 UserNotificationListener アダプタ
3. Orca 以外のタブ単位のフォーカス、設定UI
