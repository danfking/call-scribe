# Pull the local models the coach uses (via Ollama) and bring up the memory database.
# Run once before `call-scribe listen --coach`. Models match the AppConfig defaults;
# override with parameters if you've changed them in `call-scribe config`.
#
#   ./scripts/coach-pull-models.ps1
#   ./scripts/coach-pull-models.ps1 -FastModel gemma3:4b -ReasoningModel qwen3:14b
param(
    [string]$FastModel = "qwen3:4b",
    [string]$ReasoningModel = "llama3.1:8b",
    [string]$EmbedModel = "nomic-embed-text",
    [switch]$SkipDb
)

$ErrorActionPreference = "Stop"

foreach ($model in @($FastModel, $ReasoningModel, $EmbedModel)) {
    Write-Host "Pulling $model ..." -ForegroundColor Cyan
    ollama pull $model
}

if (-not $SkipDb) {
    $compose = Join-Path $PSScriptRoot "..\docker\coach-db.compose.yml"
    Write-Host "Starting memory database (Timescale + pgvector) ..." -ForegroundColor Cyan
    docker compose -f $compose up -d
}

Write-Host "Done. Run: call-scribe listen --coach" -ForegroundColor Green
