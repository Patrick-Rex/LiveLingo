<#
.SYNOPSIS
Unpacks an unsigned MSIX with the Windows SDK and verifies identity, self-contained runtime and payload boundaries without installing it.
.PARAMETER Package
Unsigned MSIX produced by New-LiveLingoMsix.ps1.
.PARAMETER MakeAppxPath
Optional Windows SDK MakeAppx executable path.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$MakeAppxPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'packaging/msix/Tools.ps1')
$makeappx = Resolve-MakeAppx $MakeAppxPath
$Package = (Resolve-Path -LiteralPath $Package).ProviderPath
$scratchRoot = Join-Path $repo 'artifacts/msix-tests'
$scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $payload = Join-Path $scratch 'payload'
    $log = Join-Path $scratch 'unpack.log'
    & $makeappx unpack /p $Package /d $payload /o *> $log
    if ($LASTEXITCODE -ne 0) { Select-String -LiteralPath $log -Pattern '\berror\b'; throw 'MakeAppx unpack failed.' }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Raw
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('r', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $identity = $manifest.Package.Identity
    if (-not $identity.Name -or -not $identity.Publisher -or $identity.ProcessorArchitecture -ne 'x64' -or
        ([version]$identity.Version).Revision -ne 0) { throw 'Invalid MSIX identity, architecture or revision.' }
    $application = $manifest.Package.Applications.Application
    if ($application.Executable -ne 'LiveLingo.Desktop.Windows.exe' -or $application.RuntimeBehavior -ne 'packagedClassicApp' -or
        $application.TrustLevel -ne 'mediumIL' -or
        -not $manifest.SelectSingleNode('/m:Package/m:Capabilities/r:Capability[@Name="runFullTrust"]', $ns)) { throw 'Invalid full-trust desktop application declaration.' }
    if ($manifest.Package.Dependencies.TargetDeviceFamily.MinVersion -ne '10.0.22000.0' -or
        $manifest.SelectNodes('/m:Package/m:Dependencies/m:PackageDependency', $ns).Count) { throw 'Unexpected platform or external framework dependency.' }
    Write-Output 'PASS: Windows 11 x64 manifest, full trust and package identity.'
    foreach ($required in @('LiveLingo.Desktop.Windows.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'licenses', 'scripts/Install-LiveLingoDependencies.ps1',
        'workers/qwen_asr/service.py', 'runtime-profiles/win-x64-cu130/requirements.lock',
        'Assets/StoreLogo.png', 'Assets/Square150x150Logo.png', 'Assets/Square44x44Logo.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing MSIX payload: $required" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $payload 'LiveLingo.Desktop.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or
        @($runtime.runtimeOptions.includedFrameworks).Count -ne 1 -or
        $runtime.runtimeOptions.includedFrameworks[0].name -ne 'Microsoft.NETCore.App') { throw 'MSIX must include only the required .NETCore runtime.' }
    $runtimeNotices = Join-Path $payload "licenses/microsoft.netcore.app.runtime.win-x64-$($runtime.runtimeOptions.includedFrameworks[0].version)"
    foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeNotices $notice) -PathType Leaf)) { throw "Missing .NET runtime notice: $notice" }
    }
    Write-Output 'PASS: self-contained .NETCore runtime, licenses and application payload.'
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
        if ($relative -match '(?i)(^|/)(models|runtimes|cache|data|tests|__pycache__)/|\.(safetensors|gguf|wav|mp3|pdb|pyc)$|(^|/)(python|ollama|nvidia-smi|Update|Squirrel)\.exe$|(^|/)(sq\.version|AppxSignature\.p7x)$') { throw "External, development or updater payload found: $relative" }
    }
    Write-Output "PASS: unsigned package excludes model runtimes, data and Velopack updater ($($files.Count) files)."
} finally {
    Remove-MsixScratch -Path $scratch -Root $scratchRoot
}
