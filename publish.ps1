# Build a self-contained release package (no .NET required on the target PC).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'app\ClaudeTaskBar.csproj'
$dist = Join-Path $root 'dist\claude-task-bar'

if (Test-Path (Join-Path $root 'dist')) { Remove-Item -Recurse -Force (Join-Path $root 'dist') }

dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $dist

Copy-Item -Recurse -Force (Join-Path $root 'hooks')         $dist
Copy-Item -Force          (Join-Path $root 'install.ps1')   $dist
Copy-Item -Force          (Join-Path $root 'uninstall.ps1') $dist
Copy-Item -Force          (Join-Path $root 'INSTALL.md')    $dist

$zip = Join-Path $root 'dist\claude-task-bar-win-x64.zip'
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force
Write-Host "Release package: $zip"
Write-Host "Upload this zip to the GitHub Releases page."
