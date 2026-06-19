# Publishes call-scribe to a stable local folder so the desktop shortcut can run
# it without rebuilding. Framework-dependent: uses the installed .NET runtime.
# Re-run this after pulling changes to update the shortcut's binary.

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot
$proj = Join-Path $repo "src/CallScribe/CallScribe.csproj"
$dest = Join-Path $env:LOCALAPPDATA "call-scribe\app"

dotnet publish $proj -c Release -o $dest

Write-Host ""
Write-Host "Published to $dest" -ForegroundColor Green
