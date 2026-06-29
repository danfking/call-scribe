# Launcher for the desktop shortcut: starts call-scribe live captions. Press Enter
# in the window to stop; the full-quality transcript is then written and the
# location is shown.

$ErrorActionPreference = "Stop"

$exe = Join-Path $env:LOCALAPPDATA "call-scribe\app\call-scribe.exe"
if (-not (Test-Path $exe)) {
    Write-Host "call-scribe is not published yet. Run scripts\publish-app.ps1 first." -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 1
}

# listen captures the mic over WASAPI and the system audio, and suppresses far-side
# bleed in the text layer. For the cleanest track separation, use headphones.
& $exe listen

$transcripts = Join-Path $env:USERPROFILE "call-scribe\transcripts"
Write-Host ""
Write-Host "Transcripts are in: $transcripts" -ForegroundColor Green
Read-Host "Press Enter to close"
