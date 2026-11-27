# claude-task-bar : hook notifier (v2, pet-bar protocol).
# Reads the full hook JSON from stdin (session_id / cwd / hook_event_name),
# works out which session and window it belongs to, and POSTs the event as
# JSON to the app. Falls back to the legacy GET route if the POST fails,
# so a half-updated install still notifies.
# -Type is only a fallback for callers that do not pass hook_event_name.
param([string]$Type = "")
$ErrorActionPreference = 'SilentlyContinue'
# When the hook fired (Unix ms). The app compares this, not the arrival time:
# powershell takes about 3 seconds to get here and to post.
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

$sessionId = ''
$cwd = ''
$hookEvent = ''
$raw = ''
try {
  # Read stdin as UTF-8 bytes. [Console]::In decodes with the console code
  # page (Shift_JIS on Japanese Windows), which breaks the JSON whenever the
  # hook input carries Japanese text - and every Stop does (the last reply).
  # A broken parse lost session_id, so the event was filed under the folder
  # instead and the same session showed up as a second pet.
  $stdin = [Console]::OpenStandardInput()
  $buf = New-Object System.IO.MemoryStream
  $stdin.CopyTo($buf)
  $raw = [System.Text.Encoding]::UTF8.GetString($buf.ToArray())
} catch {}
if ($raw) {
  try {
    $hook = $raw | ConvertFrom-Json
    $cwd = $hook.cwd
    $sessionId = $hook.session_id
    $hookEvent = $hook.hook_event_name
  } catch {}
  # Should the JSON as a whole still not parse, pick the plain fields out of
  # the text, so the event stays with its own session.
  if (-not $sessionId -and $raw -match '"session_id"\s*:\s*"([^"\\]+)"') { $sessionId = $Matches[1] }
  if (-not $hookEvent -and $raw -match '"hook_event_name"\s*:\s*"([A-Za-z]+)"') { $hookEvent = $Matches[1] }
  if (-not $cwd -and $raw -match '"cwd"\s*:\s*("(?:[^"\\]|\\.)*")') { try { $cwd = $Matches[1] | ConvertFrom-Json } catch {} }
}
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
$claudePid = 0   # nearest ancestor named claude / node: the app watches it to see the session is gone
$surface = 'terminal'
$ideNames = @('code','cursor','devenv','idea','idea64','pycharm','pycharm64','rider','rider64','webstorm','webstorm64','goland','clion','windsurf','fleet')
try {
  $cpid = $PID
  for ($i = 0; $i -lt 12; $i++) {
    $p = Get-Process -Id $cpid -ErrorAction Stop
    if ($ideNames -contains $p.ProcessName.ToLower()) { $surface = 'ide' }
    if ($claudePid -eq 0 -and $i -gt 0 -and @('claude','node') -contains $p.ProcessName.ToLower()) { $claudePid = [int]$p.Id }
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

# Orca sets ORCA_TERMINAL_HANDLE in each of its terminal tabs; the app uses
# it to switch to that exact tab. Only the documented shape is forwarded
# (case-sensitive, \z so a trailing newline does not slip through).
$orcaTerminal = "$env:ORCA_TERMINAL_HANDLE"
if ($orcaTerminal -cnotmatch '^term_[A-Za-z0-9_-]{1,128}\z') { $orcaTerminal = '' }

$body = @{
  session_id    = "$sessionId"
  type          = $type
  source        = $source
  cwd           = $cwd
  hwnd          = "$hwnd"
  surface       = $surface
  orca_terminal = $orcaTerminal
  pid           = $claudePid
  ts            = $ts
} | ConvertTo-Json -Compress

$sent = $false
try {
  # Send bytes: given a string, Windows PowerShell 5.1 encodes the body as
  # Latin-1, which turns a Japanese folder name into question marks.
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
  Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Method Post -ContentType 'application/json; charset=utf-8' -Body $bytes -Uri "http://127.0.0.1:$port/event" | Out-Null
  $sent = $true
} catch {}
if (-not $sent) {
  $src = [uri]::EscapeDataString($source)
  $t   = [uri]::EscapeDataString($type)
  $sf  = [uri]::EscapeDataString($surface)
  $sid = [uri]::EscapeDataString("$sessionId")
  $cw  = [uri]::EscapeDataString($cwd)
  $ot  = [uri]::EscapeDataString($orcaTerminal)
  $url = "http://127.0.0.1:$port/done?source=$src&hwnd=$hwnd&type=$t&surface=$sf&session_id=$sid&cwd=$cw&orca_terminal=$ot"
  try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri $url | Out-Null } catch {}
}
