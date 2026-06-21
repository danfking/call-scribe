# Creates a Desktop shortcut that launches call-scribe live captions with far-side
# speaker identification (`listen --speakers`). Uses the Desktop folder via the shell so
# it respects OneDrive redirection.

$ErrorActionPreference = "Stop"

$launcher = Join-Path $PSScriptRoot "start-listen-speakers.ps1"
$desktop = [Environment]::GetFolderPath("Desktop")
$lnkPath = Join-Path $desktop "Call Scribe (Speakers).lnk"

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = (Get-Command powershell.exe).Source
$lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
$lnk.WorkingDirectory = Split-Path $launcher
$lnk.IconLocation = "$env:SystemRoot\System32\SndVol.exe,0"
$lnk.Description = "call-scribe live captions with far-side speaker identification"
$lnk.Save()

Write-Host "Created shortcut: $lnkPath" -ForegroundColor Green
