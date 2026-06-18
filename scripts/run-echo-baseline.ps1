# Builds and runs the echo-bleed baseline harness over a speaker-volume sweep.
#
# IMPORTANT: use SPEAKERS, not headphones (acoustic bleed is the whole point),
# and keep the room quiet and do not speak during the run. Any Me caption the
# harness reports is far-side speech that leaked into your track.
#
# Generate a clip first with: ./generate-echo-clips.ps1

param(
    [string]$Clip = "$PSScriptRoot/../artifacts/echo-clips/farside.mp3",
    [string]$Volumes = "0.1,0.25,0.5,0.75,1.0",
    [double]$Tail = 12,
    [string]$LiveModel = "base.en"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Clip)) {
    Write-Error "Clip not found: $Clip. Generate one first with ./generate-echo-clips.ps1"
}

$proj = "$PSScriptRoot/../tools/EchoHarness/EchoHarness.csproj"

dotnet run --project $proj -c Release -- `
    --clip $Clip --volumes $Volumes --tail $Tail --live-model $LiveModel
