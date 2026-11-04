# claude-task-bar : hook notifier (v2, pet-bar protocol).
# Reads the full hook JSON from stdin (session_id / cwd / hook_event_name),
# works out which session and window it belongs to, and POSTs the event as
# JSON to the app. Falls back to the legacy GET route if the POST fails,
# so a half-updated install still notifies.
# -Type is only a fallback for callers that do not pass hook_event_name.
param([string]$Type = "")
$ErrorActionPreference = 'SilentlyContinue'

$sessionId = ''
$cwd = ''
$hookEvent = ''
try {
  $raw = [Console]::In.ReadToEnd()
  if ($raw) {
    $hook = $raw | ConvertFrom-Json
    $cwd = $hook.cwd
    $sessionId = $hook.session_id
    $hookEvent = $hook.hook_event_name
  }
} catch {}
if (-not $cwd) { $cwd = (Get-Location).Path }
$source = Split-Path -Leaf $cwd
if (-not $source) { $source = 'Claude' }

# Event type: the hook's own name wins; -Type / default map the old calls.
$typeMap = @{
  'SessionStart'      = 'started'
  'UserPromptSubmit'  = 'running'
  'Stop'              = 'done'
  'Notification'      = 'ask'
  'SessionEnd'        = 'ended'
}
$type = $typeMap[$hookEvent]
if (-not $type) {
  if ($Type) { $type = $Type } else { $type = 'done' }
}

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

# Port comes from the same config.json the app reads.
$port = 8765
try {
  $cfgPath = Join-Path $env:LOCALAPPDATA 'claude-task-bar\config.json'
  if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    if ($cfg.port) { $port = [int]$cfg.port }
  }
} catch {}

$body = @{
  session_id = "$sessionId"
  type       = $type
  source     = $source
  cwd        = $cwd
  hwnd       = "$hwnd"
  surface    = $surface
} | ConvertTo-Json -Compress

$sent = $false
try {
  Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Method Post -ContentType 'application/json' -Body $body -Uri "http://127.0.0.1:$port/event" | Out-Null
  $sent = $true
} catch {}
if (-not $sent) {
  $src = [uri]::EscapeDataString($source)
  $t   = [uri]::EscapeDataString($type)
  $sf  = [uri]::EscapeDataString($surface)
  $sid = [uri]::EscapeDataString("$sessionId")
  $cw  = [uri]::EscapeDataString($cwd)
  $url = "http://127.0.0.1:$port/done?source=$src&hwnd=$hwnd&type=$t&surface=$sf&session_id=$sid&cwd=$cw"
  try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri $url | Out-Null } catch {}
}
