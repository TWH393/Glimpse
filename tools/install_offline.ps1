# Installs the local English -> Chinese engine. Does not run translation tests.
$ErrorActionPreference = 'Stop'
$glimpseRoot = Split-Path -Parent $PSScriptRoot
$env:UV_PYTHON_INSTALL_DIR = Join-Path $glimpseRoot '.runtime\python'
$glimpseEnvironment = Join-Path $glimpseRoot '.venv'
$glimpsePython = Join-Path $glimpseEnvironment 'Scripts\python.exe'
uv venv --python 3.12 --managed-python $glimpseEnvironment --allow-existing
if ($LASTEXITCODE -ne 0) { throw 'Could not create the Python environment.' }
uv pip install --python $glimpsePython -r (Join-Path $glimpseRoot 'worker\requirements.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install translation dependencies.' }

$glimpseDownloads = Join-Path $glimpseRoot 'models\downloads'
New-Item -ItemType Directory -Force -Path $glimpseDownloads | Out-Null
$glimpseModel = Join-Path $glimpseDownloads 'translate-en_zh-1_9.argosmodel'
$glimpseExpectedHash = '433E7C4F034D87FBE2353161E05F18646D7999452F801A4E1F0378522B9850AB'
if (-not (Test-Path -LiteralPath $glimpseModel)) {
    Invoke-WebRequest -Uri 'https://data.argosopentech.com/argospm/v1/translate-en_zh-1_9.argosmodel' -OutFile $glimpseModel
}
if ((Get-FileHash -LiteralPath $glimpseModel -Algorithm SHA256).Hash -ne $glimpseExpectedHash) {
    throw 'Model download hash does not match the recorded package. Installation stopped.'
}
$glimpseRequest = @{ action = 'install'; path = $glimpseModel } | ConvertTo-Json -Compress
$glimpseReply = $glimpseRequest | & $glimpsePython -X utf8 (Join-Path $glimpseRoot 'worker\translate.py')
if ($LASTEXITCODE -ne 0) { throw 'Model installation process failed.' }
$glimpseResult = $glimpseReply | ConvertFrom-Json
if (-not $glimpseResult.ok) { throw $glimpseResult.error }

$glimpseConfigPath = Join-Path $env:LOCALAPPDATA 'Glimpse\settings.json'
$glimpseConfig = @{}
if (Test-Path -LiteralPath $glimpseConfigPath) {
    Copy-Item -LiteralPath $glimpseConfigPath -Destination ($glimpseConfigPath + '.before-offline') -Force
    $glimpseConfig = Get-Content -LiteralPath $glimpseConfigPath -Raw | ConvertFrom-Json -AsHashtable
}
$glimpseConfig['Python'] = $glimpsePython
$glimpseConfig['Engine'] = 2
$glimpseConfig['Offline'] = $true
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $glimpseConfigPath) | Out-Null
$glimpseConfig | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $glimpseConfigPath -Encoding utf8NoBOM
Write-Output 'Installed English -> Chinese offline model and selected the offline engine. No translation tests were run.'
