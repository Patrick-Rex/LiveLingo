<#
.SYNOPSIS
Checks or explicitly prepares external dependencies; never bundles models or drivers.
.PARAMETER Component
Check reports paths. Driver opens the selected GPU vendor's driver page.
.PARAMETER SettingsPath
Existing applied settings to use for resource locations. This script never rewrites settings.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Check', 'DotNet', 'Ollama', 'Runtime', 'Models', 'QwenModel', 'TranslationModel', 'Driver')][string]$Component = 'Check',
    [string]$SettingsPath = (Join-Path $env:LOCALAPPDATA 'LiveLingoData\data\settings.json'),
    [string]$BasePython
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
$resourceRoot = Join-Path $env:LOCALAPPDATA 'LiveLingoData'
if (-not $PSBoundParameters.ContainsKey('SettingsPath') -and -not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    $legacySettingsPath = 'D:\Soft Ware\LiveLingo\data\settings.json'
    if (Test-Path -LiteralPath $legacySettingsPath -PathType Leaf) { $SettingsPath = $legacySettingsPath }
}
$ollamaCommand = Get-Command ollama.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$settings = [pscustomobject]@{
    RuntimeProfile = 'win-x64-cpu'
    PythonPath = ''
    ModelPath = (Join-Path $resourceRoot 'models\Qwen3-ASR-1.7B-7278e1e')
    OllamaPath = if ($ollamaCommand) { $ollamaCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
    OllamaModelsPath = if ($env:OLLAMA_MODELS) { $env:OLLAMA_MODELS } else { Join-Path $env:USERPROFILE '.ollama\models' }
    CachePath = (Join-Path $resourceRoot 'cache')
    TranslationModel = 'translategemma:12b'
    AsrBackend = 0
    TranslationBackend = 0
}
$saved = $null
if ($Component -ne 'DotNet' -and (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    $saved = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in @('RuntimeProfile', 'PythonPath', 'ModelPath', 'OllamaPath', 'OllamaModelsPath', 'CachePath', 'TranslationModel')) {
        if ($saved.PSObject.Properties.Name -contains $name -and $saved.$name) { $settings.$name = $saved.$name }
    }
    if ($saved.PythonPath -and -not $saved.RuntimeProfile) { $settings.RuntimeProfile = 'win-x64-cu130' }
}
if ($settings.RuntimeProfile -notin @('win-x64-cpu', 'win-x64-rocm', 'win-x64-cu128', 'win-x64-cu130')) { throw 'Unknown ASR runtime profile.' }
if (-not $settings.PythonPath) { $settings.PythonPath = Join-Path $resourceRoot "runtimes\$($settings.RuntimeProfile)\Scripts\python.exe" }
foreach ($name in @('AsrBackend', 'TranslationBackend')) {
    if ($saved -and $saved.PSObject.Properties.Name -contains $name) { $settings.$name = $saved.$name }
    if ($settings.$name -notin @(0, 1)) { throw 'Unknown model backend; expected 0 (local) or 1 (OpenAI compatible API).' }
}
$localAsr = $settings.AsrBackend -eq 0
$localTranslation = $settings.TranslationBackend -eq 0

function Test-DotNetRuntime {
    <# .SYNOPSIS Tests the x64 shared .NET 10 runtime without requiring an SDK. #>
    $registered = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -ErrorAction SilentlyContinue
    foreach ($root in @($env:DOTNET_ROOT_X64, $registered.InstallLocation, (Join-Path $env:ProgramFiles 'dotnet'))) {
        if (-not $root) { continue }
        $exe = Join-Path $root 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
        $runtimes = & $exe --list-runtimes
        if ($LASTEXITCODE -eq 0 -and ($runtimes -match '^Microsoft.NETCore.App 10\.0\.\d+ \[')) { return $true }
    }
    return $false
}
function Install-WingetPackage {
    <# .SYNOPSIS Installs an explicitly selected package through the official winget source. #>
    param([string]$Id, [string]$Version = '')
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'Install Microsoft App Installer (winget), then retry. Alternatively install the dependency from its official website.' }
    $arguments = @('install', '--id', $Id, '--exact', '--architecture', 'x64', '--source', 'winget', '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')
    if ($Version) { $arguments += @('--version', $Version) }
    & winget @arguments
    if ($LASTEXITCODE -ne 0) { throw "winget failed for $Id (exit $LASTEXITCODE)." }
}
function Assert-ResourcePath {
    <# .SYNOPSIS Requires local absolute paths and rejects links before writing assets. #>
    param([string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]') { throw "Use a local absolute resource path: $Path" }
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if ((Test-Path -LiteralPath $itemPath) -and ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked resource path is not supported: $itemPath" }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}
function Find-Uv {
    <# .SYNOPSIS Resolves an existing uv executable after a winget installation. #>
    $command = Get-Command uv.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($candidate in @((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe'), (Join-Path $env:USERPROFILE '.local\bin\uv.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

if ($Component -eq 'Check') {
    [pscustomobject]@{ Component = '.NET 10 x64'; Present = Test-DotNetRuntime }
    if ($localAsr) { Write-Output "RuntimeProfile=$($settings.RuntimeProfile)" }
    $paths = @()
    if ($localAsr) { $paths += @('PythonPath', 'ModelPath') }
    if ($localTranslation) { $paths += @('OllamaPath', 'OllamaModelsPath') }
    foreach ($name in $paths) {
        [pscustomobject]@{ Component = $name; Present = Test-Path -LiteralPath $settings.$name }
    }
    Write-Output 'Path checks are not model/GPU validation. Run again with -Component DotNet, Ollama, Runtime, Models or Driver.'
    return
}
if ($Component -eq 'DotNet') {
    if (-not (Test-DotNetRuntime)) { Install-WingetPackage 'Microsoft.DotNet.Runtime.10' }
    if (-not (Test-DotNetRuntime)) { throw '.NET 10 x64 Runtime is still missing. Install from https://dotnet.microsoft.com/download/dotnet/10.0' }
    Write-Output 'DotNetReady=True'; return
}
if (($Component -in @('Runtime', 'QwenModel', 'Driver') -and -not $localAsr) -or
    ($Component -in @('Ollama', 'TranslationModel') -and -not $localTranslation) -or
    ($Component -eq 'Models' -and -not $localAsr -and -not $localTranslation)) {
    Write-Output 'No local dependency required for the selected API source.'; return
}
if ($Component -eq 'Ollama') {
    if (-not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf)) { Install-WingetPackage 'Ollama.Ollama' }
    if (-not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe') -PathType Leaf) -and
        -not (Get-Command ollama -ErrorAction SilentlyContinue)) { throw 'Ollama was not found after installation. Check the installer result.' }
    Write-Output 'OllamaInstalled=True; models are prepared separately.'; return
}
if ($Component -eq 'Driver') {
    switch ($settings.RuntimeProfile) {
        'win-x64-cpu' { Write-Output 'The CPU profile does not need a GPU driver.' }
        'win-x64-rocm' { Start-Process 'https://www.amd.com/en/support/download/drivers.html' }
        default { Start-Process 'https://www.nvidia.com/Download/index.aspx' }
    }
    return
}
Assert-ResourcePath $settings.CachePath
if ($localAsr -and $Component -in @('Runtime', 'Models', 'QwenModel')) {
    Assert-ResourcePath $settings.PythonPath; Assert-ResourcePath $settings.ModelPath
}
if ($localTranslation -and $Component -in @('Models', 'TranslationModel')) { Assert-ResourcePath $settings.OllamaModelsPath }
$contentRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1') -CacheRoot $settings.CachePath
if ($Component -eq 'Runtime') {
    $runtimeRoot = Split-Path (Split-Path $settings.PythonPath -Parent) -Parent
    if (-not $runtimeRoot -or (Join-Path $runtimeRoot 'Scripts\python.exe') -ne $settings.PythonPath) { throw 'PythonPath must point to an isolated runtime Scripts\python.exe.' }
    $uv = Find-Uv
    if (-not $uv) {
        Install-WingetPackage 'astral-sh.uv'
        $uv = Find-Uv
        if (-not $uv) { throw 'uv was not found after installation. Install uv or reopen PowerShell and retry.' }
    }
    & (Join-Path $PSScriptRoot 'Initialize-ModelRuntime.ps1') -Profile $settings.RuntimeProfile -BasePython $BasePython -RuntimeRoot $runtimeRoot -CacheRoot $settings.CachePath -Uv $uv
    $profilePath = Join-Path $contentRoot "runtime-profiles\$($settings.RuntimeProfile)\profile.json"
    $smoke = "import json,sys; sys.path.insert(0,sys.argv[1]); import torch; from workers.qwen_asr.inference import select_device; p=json.load(open(sys.argv[2], encoding='utf-8')); d=select_device(torch,p); x=torch.ones(16,device=d); assert (x+x).sum().item()==32; print(torch.__version__,d)"
    & $settings.PythonPath -c $smoke $contentRoot $profilePath
    if ($LASTEXITCODE -ne 0) { throw "Runtime installed, but the $($settings.RuntimeProfile) device check failed. Verify driver compatibility or select the CPU profile." }
    return
}
if ($localTranslation -and $Component -in @('Models', 'TranslationModel') -and $settings.TranslationModel -notin @('translategemma:12b', 'translategemma:4b')) { throw 'Unsupported translation model.' }
if ($localAsr -and $Component -in @('Models', 'QwenModel')) {
if (-not (Test-Path -LiteralPath $settings.PythonPath -PathType Leaf)) { throw 'Install the Python runtime before the Qwen model.' }
$prepare = Join-Path $contentRoot 'tools\model_eval\prepare_model.py'
if (Test-Path -LiteralPath (Join-Path $settings.ModelPath 'asset-manifest.json')) {
    & $settings.PythonPath -c 'import sys; sys.path.insert(0,sys.argv[1]); from prepare_model import verify_model; verify_model(sys.argv[2])' (Split-Path $prepare -Parent) $settings.ModelPath
} else {
    $prepareArguments = @($prepare, '--output', $settings.ModelPath)
    if (Test-Path -LiteralPath (Join-Path $settings.ModelPath 'preparation.json')) { $prepareArguments += '--resume' }
    & $settings.PythonPath @prepareArguments
}
if ($LASTEXITCODE -ne 0) { throw 'Qwen preparation failed; existing assets were preserved. Resolve the reported error before retrying.' }
if ($Component -eq 'QwenModel') { Write-Output 'QwenReady=True'; return }
}

if (-not $localTranslation) { Write-Output 'SelectedModelsReady=True'; return }
if (-not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf)) { throw 'Install Ollama before the translation model.' }

# Use only a private, newly created Ollama service; never stop an existing service.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$oldHost = $env:OLLAMA_HOST; $oldModels = $env:OLLAMA_MODELS
$service = $null
try {
    $env:OLLAMA_HOST = "127.0.0.1:$port"; $env:OLLAMA_MODELS = $settings.OllamaModelsPath
    $logId = [guid]::NewGuid().ToString('N')
    $service = Start-Process -FilePath $settings.OllamaPath -ArgumentList 'serve' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $settings.CachePath "$logId-ollama.out.log") -RedirectStandardError (Join-Path $settings.CachePath "$logId-ollama.err.log")
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($service.HasExited) { throw 'Private Ollama service exited before it was ready.' }
        try { $null = Invoke-RestMethod "http://127.0.0.1:$port/api/version" -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Private Ollama service did not become ready.' }
    $expected = if ($settings.TranslationModel -eq 'translategemma:12b') { 'c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252' } else { 'c49d986b0764f5881c476eb21435bb62b7abc62347aab3d4a6071e811be510a1' }
    $models = Invoke-RestMethod "http://127.0.0.1:$port/api/tags" -TimeoutSec 10
    $existing = @($models.models | Where-Object { $_.name -eq $settings.TranslationModel })
    if ($existing.Count -gt 0 -and $existing[0].digest -ne $expected) { throw 'Existing TranslateGemma fingerprint differs. It was not overwritten; check the approved model version.' }
    if ($existing.Count -eq 0) {
        & $settings.OllamaPath pull $settings.TranslationModel
        if ($LASTEXITCODE -ne 0) { throw 'TranslateGemma download failed; retry Models to resume.' }
    }
    $models = Invoke-RestMethod "http://127.0.0.1:$port/api/tags" -TimeoutSec 10
    if (-not @($models.models | Where-Object { $_.name -eq $settings.TranslationModel -and $_.digest -eq $expected }).Count) { throw 'Downloaded model does not match the approved fingerprint; do not start translation with this asset.' }
    Write-Output 'ModelsReady=True; reopen LiveLingo and recheck the environment.'
} finally {
    if ($service -and -not $service.HasExited) { Stop-Process -Id $service.Id -Force; $service.WaitForExit() }
    if ($service) { $service.Dispose() }
    $env:OLLAMA_HOST = $oldHost; $env:OLLAMA_MODELS = $oldModels
}
