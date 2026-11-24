<#
.SYNOPSIS
Validates and separates installer downloads from the fixed updates-win-x64 release payload without uploading anything.
.PARAMETER Version
Three-part version of the installer and newest full package being exported.
.PARAMETER ReleaseDirectory
Velopack build output containing Setup.exe, the channel feed and every package referenced by it.
.PARAMETER OutputDirectory
New or empty directory receiving installers, updates and local verification records; defaults to artifacts/distribution/Version.
.PARAMETER MsixPackage
Optional explicitly selected MSIX matching Version. Unsigned development packages are never added automatically.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot '../artifacts/releases'),
    [string]$OutputDirectory,
    [string]$MsixPackage
)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "../artifacts/distribution/$Version" }
$ReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).ProviderPath
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$parent = [IO.Path]::GetDirectoryName($OutputDirectory)
if (-not $parent -or $OutputDirectory -eq $ReleaseDirectory) { throw 'Choose a separate distribution output directory.' }

function Assert-ExportPath {
    <# .SYNOPSIS Rejects reparse points in an existing file or directory and all of its ancestors. .PARAMETER Path The absolute path to inspect. #>
    param([string]$Path)
    $current = $Path
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked distribution path is not allowed: $current" }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Assert-EmptyExport {
    <# .SYNOPSIS Refuses nonempty or non-directory destinations without changing their contents. .PARAMETER Path The final distribution directory. #>
    param([string]$Path)
    Assert-ExportPath $Path
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -LiteralPath $Path -Force).Count) {
            throw 'Distribution output must be absent or empty; existing exports are never overwritten.'
        }
    }
}

Assert-ExportPath $ReleaseDirectory
Assert-EmptyExport $OutputDirectory
$feedPath = Join-Path $ReleaseDirectory 'releases.win-x64.json'
Assert-ExportPath $feedPath
if ((Get-Item -LiteralPath $feedPath).Length -gt 4MB) { throw 'Update feed exceeds 4 MiB.' }
$feedContent = Get-Content -LiteralPath $feedPath -Raw
$feed = $feedContent | ConvertFrom-Json
$assets = @($feed.Assets)
if ($assets.Count -eq 0 -or $assets.Count -gt 4096) { throw 'Update feed must contain between 1 and 4096 packages.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packageFiles = [Collections.Generic.List[IO.FileInfo]]::new()
$hasCurrentFull = $false
foreach ($asset in $assets) {
    if ($asset.PackageId -cne 'LiveLingo' -or $asset.Version -notmatch '^\d+\.\d+\.\d+$' -or
        $asset.Type -cnotin @('Full', 'Delta') -or $asset.SHA256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $asset.SHA1 -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid update feed identity, type, version or checksum.' }
    if ([version]$asset.Version -gt [version]$Version) { throw 'The installer version must be the newest version in the update feed.' }
    $expected = "LiveLingo-$($asset.Version)-win-x64-$($asset.Type.ToLowerInvariant()).nupkg"
    if ($asset.FileName -cne $expected -or -not $names.Add($asset.FileName)) { throw 'Unexpected path or duplicate file in update feed.' }
    $path = Join-Path $ReleaseDirectory $asset.FileName
    Assert-ExportPath $path
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.PSIsContainer -or ($asset.Size -isnot [long] -and $asset.Size -isnot [int]) -or
        $asset.Size -le 0 -or $file.Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.SHA256 -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash -ne $asset.SHA1) { throw "Update package size or checksum mismatch: $($asset.FileName)" }
    if ($asset.Version -ceq $Version -and $asset.Type -ceq 'Full') { $hasCurrentFull = $true }
    $packageFiles.Add($file)
}
if (-not $hasCurrentFull) { throw 'Update feed is missing the requested current full package.' }
$setup = Get-Item -LiteralPath (Join-Path $ReleaseDirectory 'LiveLingo-win-x64-Setup.exe') -ErrorAction Stop
Assert-ExportPath $setup.FullName
if ($setup.PSIsContainer -or $setup.Length -eq 0) { throw 'The installer is missing or empty.' }
$setupHash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash
$setupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup.FullName)
if ($setupVersion.ProductName -cne 'LiveLingo' -or $setupVersion.ProductVersion -cne $Version) {
    throw 'Setup product or version does not match this release.'
}
$msix = $null
$msixSigned = $null
$msixHash = $null
if ($MsixPackage) {
    $msix = Get-Item -LiteralPath $MsixPackage -ErrorAction Stop
    Assert-ExportPath $msix.FullName
    if ($msix.PSIsContainer -or $msix.Extension -ine '.msix') { throw 'MsixPackage must be a single MSIX file.' }
    $msixHash = (Get-FileHash -LiteralPath $msix.FullName -Algorithm SHA256).Hash
    $zip = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry -or $entry.Length -gt 1MB) { throw 'Invalid MSIX manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.Package.Identity.Version -ne "$Version.0" -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'MSIX version or architecture does not match this release.' }
        $msixSigned = $null -ne $zip.GetEntry('AppxSignature.p7x')
    } finally { $zip.Dispose() }
}

New-Item -ItemType Directory -Path $parent -Force | Out-Null
Assert-ExportPath $parent
$scratch = Join-Path $parent ('.distribution-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $installers = Join-Path $scratch 'installers'
    $updates = Join-Path $scratch 'updates'
    New-Item -ItemType Directory -Path $installers, $updates | Out-Null
    Copy-Item -LiteralPath $setup.FullName -Destination $installers
    if ($msix) { Copy-Item -LiteralPath $msix.FullName -Destination $installers }
    if ((Get-FileHash -LiteralPath (Join-Path $installers $setup.Name) -Algorithm SHA256).Hash -ne $setupHash) {
        throw 'Copied installer differs from the validated Setup.'
    }
    if ($msix -and (Get-FileHash -LiteralPath (Join-Path $installers $msix.Name) -Algorithm SHA256).Hash -ne $msixHash) {
        throw 'Copied MSIX differs from the validated package.'
    }
    foreach ($file in $packageFiles) { Copy-Item -LiteralPath $file.FullName -Destination $updates }
    [IO.File]::WriteAllText((Join-Path $updates 'releases.win-x64.json'), $feedContent.Replace("`r`n", "`n").TrimEnd("`n") + "`n", [Text.UTF8Encoding]::new($false))
    # Recheck copied packages so a concurrent source change cannot publish a stale feed.
    foreach ($asset in $assets) {
        $copied = Get-Item -LiteralPath (Join-Path $updates $asset.FileName)
        if ($copied.Length -ne $asset.Size -or (Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256).Hash -ne $asset.SHA256) {
            throw "Copied update package differs from its validated feed: $($asset.FileName)"
        }
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    $checksums = Get-ChildItem -LiteralPath $installers, $updates -File | Sort-Object FullName | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($scratch, $_.FullName).Replace('\', '/')
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'SHA256SUMS'), ($checksums -join "`n") + "`n", $utf8)
    $report = [ordered]@{
        version = $Version; updateReleaseTag = 'updates-win-x64'; uploaded = $false
        installerAssets = @(Get-ChildItem -LiteralPath $installers -File | Sort-Object Name | Select-Object -ExpandProperty Name)
        updateAssets = @(Get-ChildItem -LiteralPath $updates -File | Sort-Object Name | Select-Object -ExpandProperty Name)
        msixContainsSignature = $msixSigned
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'distribution.json'), ($report | ConvertTo-Json -Depth 4) + "`n", $utf8)
    Assert-EmptyExport $OutputDirectory
    if (Test-Path -LiteralPath $OutputDirectory) { [IO.Directory]::Delete($OutputDirectory, $false) }
    [IO.Directory]::Move($scratch, $OutputDirectory)
    Write-Output "Distribution=$OutputDirectory; Installers=$($report.installerAssets.Count); Updates=$($report.updateAssets.Count); Uploaded=False"
    if ($msix -and -not $msixSigned) { Write-Warning 'The explicitly selected MSIX is unsigned and is for development testing, not normal installer distribution.' }
} finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne $parent) { throw 'Unsafe distribution cleanup path.' }
        Assert-ExportPath $resolved
        if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked distribution staging entry; cleanup stopped.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
