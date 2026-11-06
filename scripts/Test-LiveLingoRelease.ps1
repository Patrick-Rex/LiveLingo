<#
.SYNOPSIS
Verifies payload exclusions and optionally reconstructs a delta without installing or launching the app.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FullPackage,
    [string]$PreviousPackage,
    [string]$DeltaPackage
)
$ErrorActionPreference = 'Stop'
$FullPackage = (Resolve-Path -LiteralPath $FullPackage).ProviderPath
if ([bool]$PreviousPackage -ne [bool]$DeltaPackage) { throw 'Specify both PreviousPackage and DeltaPackage.' }
$repo = Split-Path $PSScriptRoot -Parent
function Get-PackageFiles {
    <# .SYNOPSIS Reads each entry with a bounded buffer and returns normalized content hashes. #>
    param([string]$Path)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $result = @{}
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if ($result.ContainsKey($entry.FullName)) { throw 'Duplicate package entry.' }
            $stream = $entry.Open()
            try { $result[$entry.FullName] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        }
        return $result
    } finally { $zip.Dispose() }
}
$files = Get-PackageFiles $FullPackage
foreach ($name in $files.Keys) {
    if ($name -match '(?i)(^|/)(models|runtimes|cache|data|tests|__pycache__)/|\.(safetensors|gguf|wav|mp3|pdb|pyc)$|(^|/)(python|ollama|nvidia-smi)\.exe$|(^|/)(coreclr|hostfxr|hostpolicy)\.dll$|System.Private.CoreLib.dll$') { throw "External/development payload found: $name" }
}
foreach ($required in @('LiveLingo.Desktop.Windows.exe', 'scripts/Install-LiveLingoDependencies.ps1', 'workers/qwen_asr/service.py', 'runtime-profiles/win-x64-cu130/requirements.lock')) {
    if (-not @($files.Keys | Where-Object { $_.EndsWith('/' + $required, [StringComparison]::Ordinal) }).Count) { throw "Missing payload: $required" }
}
Write-Output "PASS: full package exclusions and required files ($($files.Count) entries)."
if ($PreviousPackage) {
    $PreviousPackage = (Resolve-Path -LiteralPath $PreviousPackage).ProviderPath
    $DeltaPackage = (Resolve-Path -LiteralPath $DeltaPackage).ProviderPath
    $scratchRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release-tests'))
    $scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    Push-Location $repo
    try {
        $patched = Join-Path $scratch 'patched.nupkg'
        dotnet tool run vpk -- delta patch --base $PreviousPackage --patch $DeltaPackage --output $patched
        if ($LASTEXITCODE -ne 0) { throw 'Delta reconstruction failed.' }
        $actual = Get-PackageFiles $patched
        if ($actual.Count -ne $files.Count) { throw 'Reconstructed package entry count differs.' }
        foreach ($name in $files.Keys) { if ($actual[$name] -ne $files[$name]) { throw "Reconstructed content differs: $name" } }
        Write-Output "PASS: reconstructed contents match target; FullBytes=$((Get-Item $FullPackage).Length); DeltaBytes=$((Get-Item $DeltaPackage).Length)."
    } finally {
        Pop-Location
        $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne $scratchRoot) { throw 'Unsafe delta cleanup target.' }
        if (@(Get-Item $resolved; Get-ChildItem $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked delta staging entry; cleanup stopped.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
