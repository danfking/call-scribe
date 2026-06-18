# Generates a far-side speech clip for the echo-bleed harness using edge-tts.
#
# edge-tts is Microsoft Edge's free neural TTS (no API key). Install once with:
#   pip install edge-tts
#
# Output is a single mp3 the harness plays through the speakers. Use a voice that
# is clearly not your own so leaked captions are unambiguously the far side.

param(
    [string]$Text = "This is the other side of the call speaking. The integration catalogue needs a performance pass before the release goes out. Let me know whether the crossfire reels are ready to ship, and we can line up the rollout for next week.",
    [string]$Voice = "en-US-GuyNeural",
    [string]$OutDir = "$PSScriptRoot/../artifacts/echo-clips",
    [string]$Name = "farside"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command edge-tts -ErrorAction SilentlyContinue)) {
    Write-Error "edge-tts not found on PATH. Install it with: pip install edge-tts"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path $OutDir "$Name.mp3"

edge-tts --voice $Voice --text $Text --write-media $out

Write-Host "Wrote $out"
