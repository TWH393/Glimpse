# Installs private CPU/GPU OCR environments without running recognition or tests.
param([switch]$Gpu)
$ErrorActionPreference = 'Stop'
$glimpseRoot = Split-Path -Parent $PSScriptRoot
$env:UV_PYTHON_INSTALL_DIR = Join-Path $glimpseRoot '.runtime\python'
if ($Gpu -and !(Test-Path -LiteralPath (Join-Path $glimpseRoot '.runtime\ocr-env\Scripts\python.exe'))) {
    & $PSCommandPath
}
$ocrEnvironment = Join-Path $glimpseRoot $(if ($Gpu) { '.runtime\ocr-gpu-env' } else { '.runtime\ocr-env' })
$ocrPython = Join-Path $ocrEnvironment 'Scripts\python.exe'
uv venv --python 3.12 --managed-python $ocrEnvironment --allow-existing
if ($LASTEXITCODE -ne 0) { throw 'Could not create the OCR environment.' }
if ($Gpu) {
    # The full lock replaces RapidOCR's CPU-only ORT dependency with GPU ORT;
    # both distributions share files and must never be installed together.
    uv pip install --python $ocrPython --no-deps --require-hashes -r (Join-Path $glimpseRoot 'worker\ocr-gpu-requirements.lock.txt')
} else {
    uv pip install --python $ocrPython --require-hashes -r (Join-Path $glimpseRoot 'worker\ocr-requirements.lock.txt')
}
if ($LASTEXITCODE -ne 0) { throw 'Could not install OCR dependencies.' }

$ocrPackage = Join-Path $ocrEnvironment 'Lib\site-packages\rapidocr_onnxruntime'
$ocrManifest = foreach ($name in @('ch_PP-OCRv4_det_infer.onnx', 'ch_PP-OCRv4_rec_infer.onnx', 'ch_ppocr_mobile_v2.0_cls_infer.onnx')) {
    $model = Join-Path $ocrPackage "models\$name"
    if (!(Test-Path -LiteralPath $model)) { throw "Bundled OCR model is missing: $name" }
    [ordered]@{ file = $name; bytes = (Get-Item -LiteralPath $model).Length; sha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant() }
}
ConvertTo-Json -InputObject @($ocrManifest) | Set-Content -LiteralPath (Join-Path $ocrEnvironment 'model-manifest.json') -Encoding utf8NoBOM
$configPath = Join-Path $env:LOCALAPPDATA 'Glimpse\settings.json'
$config = @{}
if (Test-Path -LiteralPath $configPath) {
    Copy-Item -LiteralPath $configPath -Destination ($configPath + '.before-ocr') -Force
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
}
$config['OcrEngine'] = 0
if ($Gpu) {
    $config['OcrGpuPython'] = $ocrPython
    $config['OcrDeviceMode'] = 1
} else {
    $config['OcrPython'] = $ocrPython
    if (!$config.ContainsKey('OcrDeviceMode')) { $config['OcrDeviceMode'] = 2 }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configPath) | Out-Null
$temporary = $configPath + '.ocr.tmp'
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
Move-Item -LiteralPath $temporary -Destination $configPath -Force
Write-Host "Installed RapidOCR $(if ($Gpu) { 'GPU with private CUDA/cuDNN dependencies' } else { 'CPU' }) and its bundled models. No recognition or tests were run."
