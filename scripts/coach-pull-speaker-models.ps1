# Pull the ONNX models the speaker-identification feature needs (listen --speakers and
# coach diarize/enroll). They download into the call-scribe models directory under
# LocalAppData, matching the AppConfig defaults. Run once.
#
#   ./scripts/coach-pull-speaker-models.ps1
#
# Models (from the sherpa-onnx releases):
#   - pyannote speaker segmentation 3.0  -> sherpa-onnx-pyannote-segmentation-3-0.onnx
#   - NeMo TitaNet small (English) embed -> nemo_en_titanet_small.onnx
# Override the filenames in `call-scribe config` if you change these.
param(
    [string]$ModelsDir = (Join-Path $env:LOCALAPPDATA "call-scribe\models")
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

# The segmentation model ships inside a tar.bz2; the embedding model is a bare .onnx.
$segArchiveUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2"
$embUrl        = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/nemo_en_titanet_small.onnx"

$segOut = Join-Path $ModelsDir "sherpa-onnx-pyannote-segmentation-3-0.onnx"
$embOut = Join-Path $ModelsDir "nemo_en_titanet_small.onnx"

if (-not (Test-Path $embOut)) {
    Write-Host "Downloading speaker embedding model (TitaNet small) ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $embUrl -OutFile $embOut
} else {
    Write-Host "Embedding model already present." -ForegroundColor Green
}

if (-not (Test-Path $segOut)) {
    Write-Host "Downloading speaker segmentation model (pyannote) ..." -ForegroundColor Cyan
    $tmp = Join-Path $env:TEMP "sherpa-seg.tar.bz2"
    Invoke-WebRequest -Uri $segArchiveUrl -OutFile $tmp

    # tar on Windows 10+ handles .tar.bz2. Extract to a temp dir, then lift out model.onnx.
    $extract = Join-Path $env:TEMP "sherpa-seg-extract"
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar -xjf $tmp -C $extract
    $modelOnnx = Get-ChildItem -Recurse -Path $extract -Filter "model.onnx" | Select-Object -First 1
    if (-not $modelOnnx) { throw "model.onnx not found in the segmentation archive." }
    Copy-Item $modelOnnx.FullName $segOut -Force
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    Remove-Item -Force $tmp -ErrorAction SilentlyContinue
} else {
    Write-Host "Segmentation model already present." -ForegroundColor Green
}

Write-Host "Done. Speaker models are in $ModelsDir" -ForegroundColor Green
Write-Host "Enable with: call-scribe listen --speakers   (or set speakerIdEnabled in config)" -ForegroundColor Green
