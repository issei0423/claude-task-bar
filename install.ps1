# claude-task-bar installer
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path $env:LOCALAPPDATA 'claude-task-bar'

Write-Host "Installing claude-task-bar -> $installDir"

Get-Process ClaudeTaskBar -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $here '*') $installDir

$exe    = Join-Path $installDir 'ClaudeTaskBar.exe'
$notify = Join-Path $installDir 'hooks\notify.ps1'

# register Claude Code hooks (Stop / Notification) without clobbering existing ones
$claudeDir = Join-Path $env:USERPROFILE '.claude'
New-Item -ItemType Directory -Force -Path $claudeDir | Out-Null
$settingsPath = Join-Path $claudeDir 'settings.json'
if (Test-Path $settingsPath) { Copy-Item $settingsPath "$settingsPath.bak" -Force; $raw = Get-Content $settingsPath -Raw } else { $raw = '{}' }
try { $json = $raw | ConvertFrom-Json } catch { $json = [pscustomobject]@{} }
if ($null -eq $json) { $json = [pscustomobject]@{} }
if (-not ($json.PSObject.Properties.Name -contains 'hooks')) { $json | Add-Member hooks ([pscustomobject]@{}) }

$cmdDone = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$notify`""
$cmdAsk  = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$notify`" -Type ask"

function Add-CtbHook($evt, $cmd) {
  $hooks = $json.hooks
  if (-not ($hooks.PSObject.Properties.Name -contains $evt)) { $hooks | Add-Member $evt @() }
  $list = @($hooks.$evt)
  $has = $false
  foreach ($grp in $list) { if ($grp.hooks) { foreach ($h in @($grp.hooks)) { if ("$($h.command)" -match 'claude-task-bar') { $has = $true } } } }
  if (-not $has) {
    $entry = [pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = $cmd }) }
    $hooks.$evt = @($list + $entry)
  }
}
Add-CtbHook 'Stop' $cmdDone
Add-CtbHook 'Notification' $cmdAsk
# Pet-bar lifecycle events: notify.ps1 maps these from hook_event_name,
# so they use the plain command (no -Type needed).
Add-CtbHook 'SessionStart' $cmdDone
Add-CtbHook 'UserPromptSubmit' $cmdDone
Add-CtbHook 'SessionEnd' $cmdDone
[System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 20))

# shortcuts: Startup (auto-run at login) + Desktop
$ws = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Desktop'))) {
  $lnk = $ws.CreateShortcut((Join-Path $folder 'claude-task-bar.lnk'))
  $lnk.TargetPath = $exe
  $lnk.WorkingDirectory = $installDir
  $lnk.IconLocation = $exe
  $lnk.Save()
}

Start-Process $exe
Write-Host ""
Write-Host "Done. It runs in the tray. Restart Claude Code to activate the hooks."
Write-Host "To remove: run uninstall.ps1  (settings backup: settings.json.bak)"
