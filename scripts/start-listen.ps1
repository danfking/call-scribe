# Launcher for the desktop shortcut: opens the call-scribe home screen (arrow-key menu
# plus a typed command palette). Pick "Start" to record with live captions; Enter stops
# and saves the transcript. The menu also covers transcribe, devices, config, and coach.
# To skip the menu, run a command directly, e.g. "call-scribe start --full".

$ErrorActionPreference = "Stop"

$exe = Join-Path $env:LOCALAPPDATA "call-scribe\app\call-scribe.exe"
if (-not (Test-Path $exe)) {
    Write-Host "call-scribe is not published yet. Run scripts\publish-app.ps1 first." -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 1
}

# No subcommand: open the home screen. Recording (via the Start item) captures the mic
# over WASAPI and the system audio, and suppresses far-side bleed in the text layer. For
# the cleanest track separation, use headphones.
& $exe

$transcripts = Join-Path $env:USERPROFILE "call-scribe\transcripts"
Write-Host ""
Write-Host "Transcripts are in: $transcripts" -ForegroundColor Green
Read-Host "Press Enter to close"
