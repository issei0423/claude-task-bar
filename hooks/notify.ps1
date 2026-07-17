# claude-task-bar : hook notifier. -Type done (Stop) / -Type ask (Notification)
param([string]$Type = "done")
$ErrorActionPreference = 'SilentlyContinue'

$cwd = ''
try {
  $raw = [Console]::In.ReadToEnd()
  if ($raw) { $cwd = ($raw | ConvertFrom-Json).cwd }
} catch {}
if (-not $cwd) { $cwd = (Get-Location).Path }
$source = Split-Path -Leaf $cwd
if (-not $source) { $source = 'Claude' }

# Walk the process tree: find the owning window handle + detect IDE vs terminal.
$hwnd = 0
$surface = 'terminal'
$ideNames = @('code','cursor','devenv','idea','idea64','pycharm','pycharm64','rider','rider64','webstorm','webstorm64','goland','clion','windsurf','fleet')
try {
  $cpid = $PID
  for ($i = 0; $i -lt 12; $i++) {
    $p = Get-Process -Id $cpid -ErrorAction Stop
    if ($ideNames -contains $p.ProcessName.ToLower()) { $surface = 'ide' }
    if ($hwnd -eq 0 -and $p.MainWindowHandle -ne 0) { $hwnd = [int64]$p.MainWindowHandle }
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$cpid" -ErrorAction Stop).ParentProcessId
    if (-not $parent -or $parent -eq 0) { break }
    $cpid = $parent
  }
} catch {}

$src = [uri]::EscapeDataString($source)
$t   = [uri]::EscapeDataString($Type)
$sf  = [uri]::EscapeDataString($surface)
$url = "http://127.0.0.1:8765/done?source=$src&hwnd=$hwnd&type=$t&surface=$sf"
try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri $url | Out-Null } catch {}
