# claude-task-bar : hook notifier. -Type done (Stop) / -Type ask (Notification)
param([string]$Type = "done")
$ErrorActionPreference = 'SilentlyContinue'

# --- session label = project folder name (hook stdin cwd, fallback current dir) ---
$cwd = ''
try {
  $raw = [Console]::In.ReadToEnd()
  if ($raw) { $cwd = ($raw | ConvertFrom-Json).cwd }
} catch {}
if (-not $cwd) { $cwd = (Get-Location).Path }
$source = Split-Path -Leaf $cwd
if (-not $source) { $source = 'Claude' }

# --- window handle: walk up the process tree to the first ancestor that owns a
#     visible top-level window (Windows Terminal / VS Code / conhost host, etc.) ---
$hwnd = 0
try {
  $cpid = $PID
  for ($i = 0; $i -lt 12; $i++) {
    $p = Get-Process -Id $cpid -ErrorAction Stop
    if ($p.MainWindowHandle -ne 0) { $hwnd = [int64]$p.MainWindowHandle; break }
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$cpid" -ErrorAction Stop).ParentProcessId
    if (-not $parent -or $parent -eq 0) { break }
    $cpid = $parent
  }
} catch {}

# fallback: console window
if ($hwnd -eq 0) {
  try {
    Add-Type -Namespace CTB -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern System.IntPtr GetConsoleWindow();
'@
    $hwnd = [int64][CTB.Win]::GetConsoleWindow()
  } catch {}
}

$src = [uri]::EscapeDataString($source)
$t   = [uri]::EscapeDataString($Type)
$url = "http://127.0.0.1:8765/done?source=$src&hwnd=$hwnd&type=$t"
try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri $url | Out-Null } catch {}
