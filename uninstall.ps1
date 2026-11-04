# claude-task-bar uninstaller
$ErrorActionPreference = 'SilentlyContinue'
$installDir = Join-Path $env:LOCALAPPDATA 'claude-task-bar'

Get-Process ClaudeTaskBar | Stop-Process -Force

$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'
if (Test-Path $settingsPath) {
  Copy-Item $settingsPath "$settingsPath.bak" -Force
  try {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($json.hooks) {
      foreach ($evt in @('Stop','Notification','SessionStart','UserPromptSubmit','SessionEnd')) {
        if ($json.hooks.PSObject.Properties.Name -contains $evt) {
          $kept = @()
          foreach ($grp in @($json.hooks.$evt)) {
            $mine = $false
            foreach ($h in @($grp.hooks)) { if ("$($h.command)" -match 'claude-task-bar') { $mine = $true } }
            if (-not $mine) { $kept += $grp }
          }
          $json.hooks.$evt = $kept
        }
      }
      [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 20))
    }
  } catch {}
}

foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Desktop'))) {
  Remove-Item (Join-Path $folder 'claude-task-bar.lnk') -Force -ErrorAction SilentlyContinue
}

Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
Write-Host "Uninstalled."
