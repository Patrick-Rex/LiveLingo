<#
.SYNOPSIS
Checks or explicitly prepares external dependencies; never bundles models or drivers.
.PARAMETER Component
Check only reports paths. DotNet, Ollama, Runtime and Models perform the selected installation; Driver opens NVIDIA's installer page.
.PARAMETER SettingsPath
Existing applied settings to use for resource locations. This script never rewrites settings.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Check', 'DotNet', 'Ollama', 'Runtime', 'Models', 'Driver')][string]$Component = 'Check',
    [string]$SettingsPath = 'D:\Soft Ware\LiveLingo\data\settings.json',
    [string]$BasePython = (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
$settings = [pscustomobject]@{
    PythonPath = 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe'
    ModelPath = 'D:\Soft Ware\LiveLingo\models\Qwen3-ASR-1.7B-7278e1e'
    OllamaPath = (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe')
    OllamaModelsPath = 'D:\Soft Ware\Ollama'
    CachePath = 'D:\Soft Ware\LiveLingo\cache'
    TranslationModel = 'translategemma:12b'
}
if ($Component -ne 'DotNet' -and (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    $saved = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in @('PythonPath', 'ModelPath', 'OllamaPath', 'OllamaModelsPath', 'CachePath', 'TranslationModel')) {
        if ($saved.PSObject.Properties.Name -contains $name -and $saved.$name) { $settings.$name = $saved.$name }
    }
}

function Test-DotNetRuntime {
    <# .SYNOPSIS Tests the x64 shared .NET 10 runtime without requiring an SDK. #>
    $exe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $false }
    $runtimes = & $exe --list-runtimes
    return $LASTEXITCODE -eq 0 -and [bool]($runtimes -match '^Microsoft.NETCore.App 10\.0\.\d+ \[')
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
    <# .SYNOPSIS Rejects system-drive and remote model/cache locations. #>
    param([string]$Path)
    if ($Path -notmatch '^[D-Zd-z]:[\\/]' -or [IO.Path]::GetFullPath($Path).StartsWith('C:', [StringComparison]::OrdinalIgnoreCase)) { throw "Use a non-C local absolute resource path: $Path" }
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if ((Test-Path -LiteralPath $itemPath) -and ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked resource path is not supported: $itemPath" }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}
function Find-Uv {
    <# .SYNOPSIS Resolves an existing uv executable after a winget installation. #>
    $command = Get-Command uv -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe'), (Join-Path $env:USERPROFILE '.local\bin\uv.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'uv was not found. Reopen PowerShell after installing uv and retry.'
}

if ($Component -eq 'Check') {
    [pscustomobject]@{ Component = '.NET 10 x64'; Present = Test-DotNetRuntime }
    foreach ($name in @('PythonPath', 'ModelPath', 'OllamaPath', 'OllamaModelsPath')) {
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
if ($Component -eq 'Ollama') {
    if (-not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf)) { Install-WingetPackage 'Ollama.Ollama' }
    if (-not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf)) { throw 'Update the Ollama executable path in LiveLingo settings to the installed location, then retry.' }
    Write-Output 'OllamaInstalled=True; models are prepared separately.'; return
}
if ($Component -eq 'Driver') {
    Start-Process 'https://www.nvidia.com/Download/index.aspx'
    Write-Output 'Manual action required: choose the driver for your GPU, install r580 or newer for CUDA 13.x, and reboot if requested. CUDA Toolkit is not required.'
    return
}
foreach ($path in @($settings.PythonPath, $settings.ModelPath, $settings.OllamaModelsPath, $settings.CachePath)) { Assert-ResourcePath $path }
$contentRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1') -CacheRoot $settings.CachePath
if ($Component -eq 'Runtime') {
    if (-not (Test-Path -LiteralPath $BasePython -PathType Leaf)) { Install-WingetPackage 'Python.Python.3.13' '3.13.2' }
    if (-not (Test-Path -LiteralPath $BasePython -PathType Leaf)) { throw 'Specify -BasePython with your Python 3.13.2 executable.' }
    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { Install-WingetPackage 'astral-sh.uv' }
    $uv = Find-Uv
    $env:PATH = (Split-Path $uv -Parent) + [IO.Path]::PathSeparator + $env:PATH
    $runtimeRoot = Split-Path (Split-Path $settings.PythonPath -Parent) -Parent
    if ((Join-Path $runtimeRoot 'Scripts\python.exe') -ne $settings.PythonPath) { throw 'PythonPath must point to an isolated runtime Scripts\python.exe.' }
    & (Join-Path $PSScriptRoot 'Initialize-ModelRuntime.ps1') -Profile win-x64-cu130 -BasePython $BasePython -RuntimeRoot $runtimeRoot -CacheRoot $settings.CachePath
    & $settings.PythonPath -c "import torch; x=torch.ones(16,device='cuda'); print(torch.__version__,torch.cuda.get_device_name(),(x+x).sum().item())"
    if ($LASTEXITCODE -ne 0) { throw 'Runtime installed, but GPU validation failed. Check the NVIDIA driver and GPU compatibility.' }
    return
}
if ($settings.TranslationModel -notin @('translategemma:12b', 'translategemma:4b')) { throw 'Unsupported translation model.' }
if (-not (Test-Path -LiteralPath $settings.PythonPath -PathType Leaf) -or -not (Test-Path -LiteralPath $settings.OllamaPath -PathType Leaf)) { throw 'Prepare Runtime and Ollama before Models.' }
$prepare = Join-Path $contentRoot 'tools\model_eval\prepare_model.py'
if (Test-Path -LiteralPath (Join-Path $settings.ModelPath 'asset-manifest.json')) {
    & $settings.PythonPath -c 'import sys; sys.path.insert(0,sys.argv[1]); from prepare_model import verify_model; verify_model(sys.argv[2])' (Split-Path $prepare -Parent) $settings.ModelPath
} else {
    $prepareArguments = @($prepare, '--output', $settings.ModelPath)
    if (Test-Path -LiteralPath (Join-Path $settings.ModelPath 'preparation.json')) { $prepareArguments += '--resume' }
    & $settings.PythonPath @prepareArguments
}
if ($LASTEXITCODE -ne 0) { throw 'Qwen preparation failed; existing assets were preserved. Resolve the reported error before retrying.' }

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
