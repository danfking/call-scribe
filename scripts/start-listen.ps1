# Launcher for the desktop shortcut: starts call-scribe live captions with
# acoustic echo cancellation on (for speaker use). Press Enter in the window to
# stop; the full-quality transcript is then written and the location is shown.

$ErrorActionPreference = "Stop"

$exe = Join-Path $env:LOCALAPPDATA "call-scribe\app\call-scribe.exe"
if (-not (Test-Path $exe)) {
    Write-Host "call-scribe is not published yet. Run scripts\publish-app.ps1 first." -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 1
}

# --aec cancels far-side speaker bleed; the suppressor level defaults to 1, which
# preserved the near-end voice in testing. Pass --aes 0 if you ever hear your own
# voice getting clipped while the other side talks.
& $exe listen --aec

$transcripts = Join-Path $env:USERPROFILE "call-scribe\transcripts"
Write-Host ""
Write-Host "Transcripts are in: $transcripts" -ForegroundColor Green
Read-Host "Press Enter to close"
