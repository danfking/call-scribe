# Launcher for the speakers desktop shortcut: starts call-scribe live captions with far-side
# speaker identification AND the realtime coach panel (`listen --speakers --coach`). Brings
# up the local services best-effort first — Ollama (coach inference) and the Timescale +
# pgvector database (coach memory + voiceprints so named speakers persist) — then runs the
# session. Press Enter in the window to stop; the full-quality transcript is written, then
# the after-meeting pass attributes speakers and prompts to name any unknown ones. Both
# features degrade gracefully if a service or model is missing (no Ollama -> no advice; no
# database -> live "Speaker N" labels only, no enrollment), so nothing blocks the meeting.

$ErrorActionPreference = "Stop"

# The .NET 10 SDK is installed per-user (not registered machine-wide), so point the
# published app's host at it. Harmless if a machine-wide runtime is also present.
$dotnet10 = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path (Join-Path $dotnet10 "dotnet.exe")) { $env:DOTNET_ROOT = $dotnet10 }

$exe = Join-Path $env:LOCALAPPDATA "call-scribe\app\call-scribe.exe"
if (-not (Test-Path $exe)) {
    Write-Host "call-scribe is not published yet. Run scripts\publish-app.ps1 first." -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 1
}

# --- Ollama (local inference for the coach) -----------------------------------
try {
    $up = $false
    try { Invoke-WebRequest -Uri "http://localhost:11434/api/version" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true } catch { }
    if (-not $up) {
        $ollama = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
        if (Test-Path $ollama) {
            Write-Host "Starting Ollama..." -ForegroundColor Cyan
            Start-Process -FilePath $ollama -ArgumentList "serve" -WindowStyle Hidden
            for ($i = 0; $i -lt 10; $i++) {
                Start-Sleep -Seconds 1
                try { Invoke-WebRequest -Uri "http://localhost:11434/api/version" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { }
            }
        } else {
            Write-Host "Ollama not found; coach advice will be disabled." -ForegroundColor Yellow
        }
    }
} catch { Write-Host "Could not start Ollama; continuing without model advice." -ForegroundColor Yellow }

# --- Voiceprint + memory database (Timescale + pgvector) ----------------------
# Needed to enroll and recognise named speakers across meetings (and for coach memory).
# Without it the run still works, labelling the far side as "Speaker 1", "Speaker 2", ...
# for this call only.
try {
    $compose = Join-Path $PSScriptRoot "..\docker\coach-db.compose.yml"
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host "Starting voiceprint database..." -ForegroundColor Cyan
        docker compose -f $compose up -d | Out-Null
    } else {
        Write-Host "Docker not found; speakers will be labelled per-call without enrollment." -ForegroundColor Yellow
    }
} catch { Write-Host "Could not start the database; continuing without speaker enrollment." -ForegroundColor Yellow }

# Speaker models must be present (scripts\coach-pull-speaker-models.ps1). If they are not,
# the app falls back to the plain "Others" label rather than failing.
& $exe listen --speakers --coach

$transcripts = Join-Path $env:USERPROFILE "call-scribe\transcripts"
Write-Host ""
Write-Host "Transcripts are in: $transcripts" -ForegroundColor Green
Read-Host "Press Enter to close"
