# Launcher for the coach desktop shortcut: starts call-scribe live captions with the
# realtime coach panel on. Brings up the local services the coach needs (Ollama for
# inference, the Timescale+pgvector container for memory) best-effort first, then runs
# `listen --coach`. Press Enter in the window to stop; the full-quality transcript is
# written and its location shown. The coach degrades gracefully if a service is down
# (no model -> no advice; no database -> no memory), so a missing service never blocks
# the meeting.

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

# --- Ollama (local inference) -------------------------------------------------
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

# --- Memory database (Timescale + pgvector) -----------------------------------
try {
    $compose = Join-Path $PSScriptRoot "..\docker\coach-db.compose.yml"
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host "Starting coach memory database..." -ForegroundColor Cyan
        docker compose -f $compose up -d | Out-Null
    } else {
        Write-Host "Docker not found; coach will run without cross-meeting memory." -ForegroundColor Yellow
    }
} catch { Write-Host "Could not start the memory database; continuing without memory." -ForegroundColor Yellow }

& $exe listen --coach

$transcripts = Join-Path $env:USERPROFILE "call-scribe\transcripts"
Write-Host ""
Write-Host "Transcripts are in: $transcripts" -ForegroundColor Green
Read-Host "Press Enter to close"
