param(
    [Parameter(Mandatory)][ValidateSet('win-x64-cu128', 'win-x64-cu130', 'win-x64-whisper')][string]$Profile,
    [string]$BasePython = (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
$profileDirectory = Join-Path $repoRoot "runtime-profiles\$Profile"
$profileData = Get-Content (Join-Path $profileDirectory 'profile.json') -Raw | ConvertFrom-Json
$lockPath = Join-Path $profileDirectory 'requirements.lock'
if ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $profileData.lockSha256) {
    throw 'Profile lock fingerprint does not match.'
}
$runtimeRoot = [IO.Path]::GetFullPath("D:\Soft Ware\LiveLingo\runtimes\$Profile")
$expectedPython = Join-Path $runtimeRoot 'Scripts\python.exe'
if ($profileData.interpreter -ne $expectedPython) { throw 'Profile interpreter is outside its dedicated runtime.' }
$actualVersion = & $BasePython -c 'import platform; print(platform.python_version())'
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $profileData.python) { throw 'Unexpected base Python version.' }
if (-not (Test-Path -LiteralPath $runtimeRoot)) {
    & uv venv --python $BasePython $runtimeRoot
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create runtime.' }
} elseif (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'pyvenv.cfg'))) {
    throw 'Existing runtime directory is not an isolated environment.'
}
$runtimeVersion = & $expectedPython -c 'import platform; print(platform.python_version())'
if ($LASTEXITCODE -ne 0 -or $runtimeVersion -ne $profileData.python) { throw 'Unexpected runtime Python version.' }
& uv pip sync --python $expectedPython --require-hashes --only-binary=:all: $lockPath
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency sync failed; runtime is not validated.' }
& uv pip check --python $expectedPython
if ($LASTEXITCODE -ne 0) { throw 'Installed dependencies are inconsistent.' }
Write-Output "RuntimeReady=$Profile; GPU and model validation still required."
