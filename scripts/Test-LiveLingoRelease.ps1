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
    <# .SYNOPSIS Validates the unique application manifest and returns bounded-stream content hashes. #>
    param([string]$Path)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifests = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($manifests.Count -ne 1 -or $manifests[0].FullName -ne 'LiveLingo.nuspec') {
            throw 'The package must contain only the root LiveLingo.nuspec; dependency metadata must use .nuspec.xml.'
        }
        $reader = [IO.StreamReader]::new($manifests[0].Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.package.metadata.id -ne 'LiveLingo' -or $manifest.package.metadata.mainExe -ne 'LiveLingo.Desktop.Windows.exe' -or
            $manifest.package.metadata.channel -ne 'win-x64' -or -not $manifest.package.metadata.version) { throw 'Invalid application manifest.' }
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
        # Exercise the native patcher used by the installed app; the CLI repacks ZIPs differently.
        $nativeRoot = Join-Path $scratch 'native'
        $current = Join-Path $nativeRoot 'current'
        New-Item -ItemType Directory -Path $current, (Join-Path $nativeRoot 'packages') -Force | Out-Null
        $updater = Join-Path $nativeRoot 'Update.exe'
        $package = [IO.Compression.ZipFile]::OpenRead($FullPackage)
        try {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($package.GetEntry('lib/app/Squirrel.exe'), $updater)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($package.GetEntry('lib/app/sq.version'), (Join-Path $current 'sq.version'))
        } finally { $package.Dispose() }
        $log = Join-Path $scratch 'native-patch.log'
        $arguments = @('patch', '--old', ('"' + $PreviousPackage + '"'), '--delta', ('"' + $DeltaPackage + '"'),
            '--output', ('"' + $patched + '"'), '--rootDir', ('"' + $nativeRoot + '"'), '--log', ('"' + $log + '"'), '--silent')
        $process = Start-Process -FilePath $updater -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try {
            if (-not $process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw 'Native delta reconstruction timed out.' }
            if ($process.ExitCode -ne 0) { throw "Native delta reconstruction failed: $(Get-Content -LiteralPath $log -Raw)" }
        } finally { $process.Dispose() }
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
