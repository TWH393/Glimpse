# Creates a project-private ASR environment and downloads its model, without inference.
$ErrorActionPreference = 'Stop'
$speechRoot = Split-Path -Parent $PSScriptRoot
$env:UV_PYTHON_INSTALL_DIR = Join-Path $speechRoot '.runtime\python'
$speechEnvironment = Join-Path $speechRoot '.runtime\speech-env'
$speechPython = Join-Path $speechEnvironment 'Scripts\python.exe'
uv venv --python 3.12 --managed-python $speechEnvironment --allow-existing
if ($LASTEXITCODE -ne 0) { throw 'Could not create the speech environment.' }
uv pip install --python $speechPython --require-hashes -r (Join-Path $speechRoot 'worker\speech-requirements.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install speech dependencies.' }
& $speechPython (Join-Path $PSScriptRoot 'install_speech_model.py')
if ($LASTEXITCODE -ne 0) { throw 'Could not download the speech model.' }
Write-Host 'Speech environment and local model prepared. No audio capture, inference or tests were run.'
