param(
    [ValidateSet('win-x64-cpu', 'win-x64-rocm', 'win-x64-cu128', 'win-x64-cu130', 'win-x64-whisper')][string]$Profile = 'win-x64-cpu',
    [string]$BasePython,
    [string]$Uv = 'uv',
    [string]$RuntimeRoot = (Join-Path $env:LOCALAPPDATA "LiveLingoData\runtimes\$Profile"),
    [string]$CacheRoot = (Join-Path $env:LOCALAPPDATA 'LiveLingoData\cache')
)
$ErrorActionPreference = 'Stop'

function Assert-CompatiblePython {
    <# .SYNOPSIS Verifies the Python minor version and rejects Store runtimes that escape the Windows Job. #>
    param([string]$Executable, [string]$Version)
    $storeRoots = @((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'), (Join-Path $env:ProgramFiles 'WindowsApps'))
    $storeError = 'Microsoft Store Python cannot supervise model processes. Use uv-managed Python or a python.org installation.'
    $fullPath = [IO.Path]::GetFullPath($Executable)
    foreach ($root in $storeRoots) {
        if ($fullPath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw $storeError }
    }
    $identity = & $Executable -I -c 'import platform,sys; print(platform.python_version()); print(sys.base_prefix)'
    if ($LASTEXITCODE -ne 0 -or $identity.Count -ne 2) { throw 'Could not inspect the Python interpreter.' }
    foreach ($root in $storeRoots) {
        if ($identity[1].StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw $storeError }
    }
    if ($identity[0] -ne $Version -and -not $identity[0].StartsWith("$Version.")) { throw "Runtime requires Python $Version." }
}

. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1') -CacheRoot $CacheRoot
$repoRoot = Split-Path $PSScriptRoot -Parent
$profileDirectory = Join-Path $repoRoot "runtime-profiles\$Profile"
$profileData = Get-Content (Join-Path $profileDirectory 'profile.json') -Raw | ConvertFrom-Json
$lockPath = Join-Path $profileDirectory 'requirements.lock'
if ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $profileData.lockSha256) {
    throw 'Profile lock fingerprint does not match.'
}
if ($RuntimeRoot -notmatch '^[A-Za-z]:[\\/]') { throw 'Runtime requires a local absolute path.' }
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$expectedPython = Join-Path $runtimeRoot 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $runtimeRoot)) {
    $pythonArguments = @('--managed-python', '--python', $profileData.python)
    if ($BasePython) {
        Assert-CompatiblePython $BasePython $profileData.python
        $pythonArguments = @('--python', $BasePython)
    }
    & $Uv venv @pythonArguments $runtimeRoot
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create runtime.' }
} elseif (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'pyvenv.cfg'))) {
    throw 'Existing runtime directory is not an isolated environment.'
}
Assert-CompatiblePython $expectedPython $profileData.python
$existingTorch = @(Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'Lib\site-packages') -Filter 'torch-*.dist-info' -Directory)
if ($existingTorch.Count -and $profileData.torch -and $existingTorch.Name -notcontains "torch-$($profileData.torch).dist-info") {
    throw "The existing environment uses a different torch build. Choose a separate runtime path for $Profile."
}
$formatArguments = @('--only-binary=:all:')
if ($Profile -eq 'win-x64-rocm') { $formatArguments += @('--no-binary', 'rocm') }
& $Uv pip sync --python $expectedPython --require-hashes @formatArguments $lockPath
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency sync failed; runtime is not validated.' }
& $Uv pip check --python $expectedPython
if ($LASTEXITCODE -ne 0) { throw 'Installed dependencies are inconsistent.' }
Write-Output "RuntimeReady=$Profile; model validation still required."
