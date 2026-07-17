# インストール方法

## 友達向け（かんたん・.NET不要）

1. このリポジトリの **Releases** ページから最新の `claude-task-bar-win-x64.zip` をダウンロード
2. zip を右クリック →「すべて展開」
3. 展開フォルダの中の **`install.ps1`** を右クリック →「PowerShell で実行」
   - もし実行できないときは、展開フォルダで PowerShell を開いて:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .\install.ps1
     ```
4. トレイに常駐し、「起動したよ！」の演出が出れば成功
5. **Claude Code を再起動**するとフックが有効になり、セッション完了/質問待ちでマスコットが出ます

インストーラがやること:
- アプリを `%LOCALAPPDATA%\claude-task-bar` に配置
- Claude Code の Stop / Notification フックを `~/.claude/settings.json` に追加（既存設定は保持・バックアップあり）
- ログイン時の自動起動＋デスクトップにショートカット作成

### アンインストール
展開フォルダ（または `%LOCALAPPDATA%\claude-task-bar`）の `uninstall.ps1` を実行。

## 開発者向け（ソースからビルド）

- 必要: Windows 10/11、.NET 10 SDK
- ビルド＆起動:
  ```powershell
  cd app
  dotnet build -c Release
  Start-Process ".\bin\Release\net10.0-windows\ClaudeTaskBar.exe"
  ```
- 配布用パッケージ作成: リポジトリ直下で `powershell -ExecutionPolicy Bypass -File .\publish.ps1`
