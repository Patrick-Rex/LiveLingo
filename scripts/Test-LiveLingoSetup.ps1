<#
.SYNOPSIS
Checks setup guardrails with isolated files, without installing software or downloading assets.
#>
#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$scratchParent = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/setup-tests'))
$scratch = Join-Path $scratchParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$passed = 0
$environmentNames = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'TEMP', 'TMP', 'TESTINGPLATFORM_TELEMETRY_OPTOUT', 'LIVELINGO_CACHE_ROOT', 'UV_CACHE_DIR', 'PIP_CACHE_DIR', 'HF_HOME', 'HF_HUB_CACHE', 'TORCH_HOME', 'TORCHINDUCTOR_CACHE_DIR', 'CUDA_CACHE_PATH', 'PYTHONPYCACHEPREFIX', 'PYTHONUTF8', 'PYTHONIOENCODING')
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
function Assert-SetupRejected {
    <# .SYNOPSIS Requires an invalid setup request to fail before installation. #>
    param([scriptblock]$Action, [string]$Expected)
    $rejected = $false
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notlike "*$Expected*") { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Invalid setup request was accepted.' }
}
try {
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/Install-LiveLingo.ps1') -Destination $scratch
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-LiveLingoDependencies.ps1') -Destination $scratch
    # The dummy installer must never be executed by CheckOnly or rejected requests.
    [IO.File]::WriteAllText((Join-Path $scratch 'LiveLingo-win-x64-Setup.exe'), 'not executable')
    $installer = Join-Path $scratch 'Install-LiveLingo.ps1'
    $dependencies = Join-Path $scratch 'Install-LiveLingoDependencies.ps1'
    $destination = Join-Path $scratch 'managed'
    $result = & $installer -InstallDirectory $destination -CheckOnly
    if (-not ($result -match 'Installer=') -or (Test-Path -LiteralPath $destination)) { throw 'CheckOnly changed the installation target.' }
    $passed++
    Assert-SetupRejected { & $installer -InstallDirectory 'relative\LiveLingo' -CheckOnly } 'local absolute'
    $systemPath = Join-Path $env:SystemDrive ('LiveLingo-setup-check-' + [guid]::NewGuid().ToString('N'))
    $systemCheck = & $installer -InstallDirectory $systemPath -CheckOnly
    if (-not ($systemCheck -match 'Installer=') -or (Test-Path -LiteralPath $systemPath)) { throw 'System drive rejected or modified by CheckOnly.' }
    $passed++
    Assert-SetupRejected { & $installer -InstallDirectory (Join-Path $scratch 'desktop') -CheckOnly } 'old ZIP'
    $passed++
    New-Item -ItemType Directory -Path $destination | Out-Null
    [IO.File]::WriteAllText((Join-Path $destination 'user.txt'), 'keep')
    Assert-SetupRejected { & $installer -InstallDirectory $destination -CheckOnly } 'unrelated files'
    if ([IO.File]::ReadAllText((Join-Path $destination 'user.txt')) -ne 'keep') { throw 'Existing file changed.' }
    $passed++
    $settings = Join-Path $scratch 'settings.json'
    $fresh = & $dependencies -SettingsPath $settings -Component Check
    if ($fresh -notcontains 'RuntimeProfile=win-x64-cpu') { throw 'Fresh installation must default to CPU.' }
    [IO.File]::WriteAllText($settings, '{"PythonPath":"relative\\python.exe"}')
    Assert-SetupRejected { & $dependencies -SettingsPath $settings -Component Runtime } 'local absolute'
    $old = & $dependencies -SettingsPath $settings -Component Check
    if ($old -notcontains 'RuntimeProfile=win-x64-cu130') { throw 'Legacy profile was lost.' }
    foreach ($profile in @('win-x64-cpu', 'win-x64-rocm', 'win-x64-cu128', 'win-x64-cu130')) {
        [IO.File]::WriteAllText($settings, (@{ RuntimeProfile=$profile } | ConvertTo-Json))
        if ((& $dependencies -SettingsPath $settings -Component Check) -notcontains "RuntimeProfile=$profile") { throw "Selected profile not honored: $profile" }
    }
    [IO.File]::WriteAllText($settings, '{"RuntimeProfile":"win-x64-cpu"}')
    if ((& $dependencies -SettingsPath $settings -Component Driver) -notmatch 'does not need') { throw 'CPU requested a driver.' }
    $passed++
    $modelCache = Join-Path $scratch 'custom model cache'
    . (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1') -CacheRoot $modelCache
    if ($env:LIVELINGO_CACHE_ROOT -ne $modelCache -or $env:HF_HOME -ne (Join-Path $modelCache 'huggingface')) { throw 'Custom model cache was not propagated to Python preparation.' }
    $passed++
    $unusedRuntime = Join-Path $scratch 'store-python-test'
    $storePython = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\python.exe'
    Assert-SetupRejected {
        & (Join-Path $PSScriptRoot 'Initialize-ModelRuntime.ps1') -BasePython $storePython -RuntimeRoot $unusedRuntime -CacheRoot $modelCache
    } 'Microsoft Store Python'
    if (Test-Path -LiteralPath $unusedRuntime) { throw 'Rejected base interpreter created a runtime.' }
    $passed++
    Write-Output "SetupTestsPassed=$passed; softwareInstalled=False; downloads=False"
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -ne $scratchParent) { throw 'Unsafe setup-test cleanup target.' }
    if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked test entry; cleanup stopped.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
