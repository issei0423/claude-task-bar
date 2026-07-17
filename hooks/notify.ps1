# claude-task-bar : Stop hook notifier
# Called by Claude Code when a session finishes. Sends a "done" event to the
# local tray app (ClaudeTaskBar) with a session label and the terminal window handle.

$ErrorActionPreference = 'SilentlyContinue'

# Claude Code passes hook input as JSON on stdin (includes "cwd").
$cwd = ''
try {
  $raw = [Console]::In.ReadToEnd()
  if ($raw) { $cwd = ($raw | ConvertFrom-Json).cwd }
} catch {}
if (-not $cwd) { $cwd = (Get-Location).Path }
$source = Split-Path -Leaf $cwd
if (-not $source) { $source = 'Claude' }

# Best-effort: window handle of the terminal running this hook (for click-to-focus).
$hwnd = 0
try {
  Add-Type -Namespace CTB -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern System.IntPtr GetConsoleWindow();
'@
  $hwnd = [int64][CTB.Win]::GetConsoleWindow()
} catch {}

$src = [uri]::EscapeDataString($source)
$url = "http://127.0.0.1:8765/done?source=$src&hwnd=$hwnd"
try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 -Uri $url | Out-Null } catch {}
