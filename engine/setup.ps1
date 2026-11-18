# One-time setup for the Jarvis voice engine (openWakeWord + faster-whisper + Kokoro).
# Needs Python 3.10+ on PATH. Creates engine\.venv and downloads ~260 MB of models into engine\models.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File engine\setup.ps1
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$models = Join-Path $here "models"
$py = Join-Path $here ".venv\Scripts\python.exe"
$env:PYTHONUTF8 = "1"

if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw "Python 3.10+ is required: https://www.python.org/downloads/" }

Write-Host "Creating Python environment..."
if (-not (Test-Path $py)) { python -m venv (Join-Path $here ".venv") }
& $py -m pip install -q --upgrade pip
& $py -m pip install -q -r (Join-Path $here "requirements.txt")

New-Item -ItemType Directory -Force $models | Out-Null
Write-Host "Downloading wake word model (hey jarvis)..."
& $py -c "import openwakeword.utils as u; u.download_models(['hey_jarvis'], target_directory=r'$models')"

Write-Host "Downloading Kokoro voice..."
$r = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0"
foreach ($f in "kokoro-v1.0.int8.onnx", "voices-v1.0.bin") {
    if (-not (Test-Path (Join-Path $models $f))) { Invoke-WebRequest "$r/$f" -OutFile (Join-Path $models $f) }
}

Write-Host "Downloading Whisper (base.en)..."
& $py -c "from faster_whisper import WhisperModel; WhisperModel('base.en', device='cpu', compute_type='int8', download_root=r'$models\whisper')"

Write-Host "Done. Restart Dynamic Island and say 'Hey Jarvis'."
