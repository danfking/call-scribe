# Creates a single Desktop shortcut, "Call Scribe", that launches live captions with both
# the realtime coach and far-side speaker identification (start-call-scribe.ps1). Removes any
# older Call Scribe shortcut variants so the Desktop has just the one. Uses the Desktop folder
# via the shell so it respects OneDrive redirection.

$ErrorActionPreference = "Stop"

$launcher = Join-Path $PSScriptRoot "start-call-scribe.ps1"
$desktop = [Environment]::GetFolderPath("Desktop")

# Remove older/variant shortcuts so only the unified one remains.
foreach ($name in @("Call Scribe.lnk", "Call Scribe (Coach).lnk", "Call Scribe (Speakers).lnk", "Call Scribe (AEC).lnk")) {
    $path = Join-Path $desktop $name
    if (Test-Path $path) { Remove-Item $path -Force }
}

$lnkPath = Join-Path $desktop "Call Scribe.lnk"
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = (Get-Command powershell.exe).Source
$lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
$lnk.WorkingDirectory = Split-Path $launcher
$lnk.IconLocation = "$env:SystemRoot\System32\SndVol.exe,0"
$lnk.Description = "call-scribe live captions with the realtime coach and far-side speaker identification"
$lnk.Save()

Write-Host "Created shortcut: $lnkPath" -ForegroundColor Green
