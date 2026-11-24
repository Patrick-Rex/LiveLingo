<#
.SYNOPSIS
Builds the Windows x64 delivery ZIP from production outputs and an explicit setup file list.
.PARAMETER SelfContained
Includes the .NET runtime in an intermediate ZIP for MSIX; omit for legacy ZIP and Velopack delivery.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/packages'),
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$name = "LiveLingo-$Version-win-x64"
$archive = Join-Path $OutputDirectory "$name.zip"
$checksum = "$archive.sha256"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $checksum)) { throw 'Package already exists; use a new version or explicitly remove the previous archive.' }
$workRoot = Join-Path $repo 'artifacts/packaging'
New-Item -ItemType Directory -Path $workRoot, $OutputDirectory -Force | Out-Null
$workRoot = (Resolve-Path -LiteralPath $workRoot).ProviderPath
$work = Join-Path $workRoot ([guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'desktop'
try {
    & (Join-Path $PSScriptRoot 'Publish-LiveLingo.ps1') -OutputDirectory $payload -Version $Version -SelfContained:$SelfContained
    $setup = Join-Path $payload 'scripts'
    New-Item -ItemType Directory -Path $setup -Force | Out-Null
    foreach ($file in @('Enter-Development.ps1', 'Enter-ModelEvaluation.ps1', 'Initialize-ModelRuntime.ps1', 'Install-LiveLingoDependencies.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $setup
    }
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/USER-GUIDE.md') -Destination (Join-Path $payload 'USER-GUIDE.md')
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/Install.ps1') -Destination (Join-Path $work 'Install.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/USER-GUIDE.md') -Destination (Join-Path $work 'README.md')
    foreach ($notice in @('LICENSE', 'NOTICE')) {
        Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $payload
        Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $work
    }

    # Preserve third-party license files and original NuGet metadata for shipped dependencies.
    $assets = Get-Content -LiteralPath (Join-Path $repo 'src/LiveLingo.Desktop.Windows/obj/project.assets.json') -Raw | ConvertFrom-Json
    $notices = Join-Path $payload 'licenses'
    New-Item -ItemType Directory -Path $notices | Out-Null
    $index = [Collections.Generic.List[string]]::new()
    $index.Add('# Third-party packages')
    foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $package = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
        }
        if (-not $package) { throw "Restored package not found: $($library.Name)" }
        $nuspec = @(Get-ChildItem -LiteralPath $package -Filter '*.nuspec')[0]
        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $index.Add("- $($library.Name): $($metadata.package.metadata.license.InnerText) $($metadata.package.metadata.licenseUrl)")
        $licenseFolder = Join-Path $notices ($library.Name.Replace('/', '-'))
        New-Item -ItemType Directory -Path $licenseFolder | Out-Null
        # Velopack treats the first .nuspec anywhere in a bundle as the application manifest.
        # Preserve dependency metadata verbatim with an XML suffix so delta ZIP order is harmless.
        Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $licenseFolder ($nuspec.Name + '.xml'))
        foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -match '(?i)^(license|licence|copying|notice|third.party)' }) {
            $relative = $file.FullName.Substring($package.TrimEnd('\').Length + 1)
            $licenseTarget = Join-Path $licenseFolder $relative
            New-Item -ItemType Directory -Path (Split-Path $licenseTarget -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $licenseTarget
        }
    }
    if ($SelfContained) {
        # Runtime packs are download dependencies, not entries in assets.libraries.
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'LiveLingo.Desktop.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json
        $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        if ($frameworks.Count -ne 1 -or $frameworks[0].name -ne 'Microsoft.NETCore.App') { throw 'Only the .NETCore runtime is expected for self-contained desktop delivery.' }
        $runtimeVersion = $frameworks[0].version
        $runtimeId = 'microsoft.netcore.app.runtime.win-x64'
        $runtimePackage = $assets.packageFolders.PSObject.Properties.Name |
            ForEach-Object { Join-Path $_ "$runtimeId/$runtimeVersion" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $runtimePackage) { throw "Restored .NET runtime pack not found: $runtimeVersion" }
        $runtimeNotices = Join-Path $notices "$runtimeId-$runtimeVersion"
        New-Item -ItemType Directory -Path $runtimeNotices | Out-Null
        foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            Copy-Item -LiteralPath (Join-Path $runtimePackage $notice) -Destination $runtimeNotices -ErrorAction Stop
        }
        Copy-Item -LiteralPath (Join-Path $runtimePackage "$runtimeId.nuspec") -Destination (Join-Path $runtimeNotices "$runtimeId.nuspec.xml")
        $index.Add("- Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion`: MIT; see licenses/$runtimeId-$runtimeVersion/LICENSE.TXT and THIRD-PARTY-NOTICES.TXT.")
    }
    [IO.File]::WriteAllText((Join-Path $payload 'THIRD-PARTY-NOTICES.md'), ($index -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(tests|specs|data|__pycache__|bin|obj)/|\.(pdb|wav|mp3|log|pyc)$') { throw "Development artifact in package: $relative" }
        [ordered]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{ schemaVersion = 1; product = 'LiveLingo'; version = $Version; runtime = 'win-x64'; files = $files }
    [IO.File]::WriteAllText((Join-Path $work 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
    & (Join-Path $work 'Install.ps1') -VerifyOnly
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($work, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksum, "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Package=$archive; Bytes=$((Get-Item -LiteralPath $archive).Length); SHA256=$hash"
} finally {
    # Delete only the unique staging directory created by this invocation.
    if (Test-Path -LiteralPath $work) {
        $resolved = (Resolve-Path -LiteralPath $work).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne $workRoot) { throw 'Unsafe package cleanup path.' }
        if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked staging entry; cleanup stopped.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
