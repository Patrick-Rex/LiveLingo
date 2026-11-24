<#
.SYNOPSIS
Builds one unsigned Windows 11 x64 MSIX with a self-contained .NET runtime; does not install or upload it.
.PARAMETER Version
Three-part release version; the MSIX revision is always zero for future Store submissions.
.PARAMETER IdentityName
Package identity assigned by Partner Center. Supply together with Publisher; omit both for an isolated development identity.
.PARAMETER Publisher
Exact publisher assigned by Partner Center or the signing certificate subject. No signing is performed.
.PARAMETER PublisherDisplayName
Publisher label displayed by Windows; defaults to the repository author.
.PARAMETER OutputDirectory
Directory receiving only the MSIX. Existing packages are never overwritten.
.PARAMETER MakeAppxPath
Optional Windows SDK MakeAppx executable path.
#>
#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Development')]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Identity')][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$IdentityName,
    [Parameter(Mandatory, ParameterSetName = 'Identity')][ValidateNotNullOrEmpty()][string]$Publisher,
    [ValidateNotNullOrEmpty()][string]$PublisherDisplayName = 'Patrick-Rex',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/msix'),
    [string]$MakeAppxPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'packaging/msix/Tools.ps1')
$makeappx = Resolve-MakeAppx $MakeAppxPath
$packageVersion = [version]"$Version.0"
if ($packageVersion.Major -gt 65535 -or $packageVersion.Minor -gt 65535 -or $packageVersion.Build -gt 65535) { throw 'MSIX version components must be between 0 and 65535.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $OutputDirectory "LiveLingo-$Version-win-x64-unsigned.msix"
if (Test-Path -LiteralPath $package) { throw 'MSIX already exists; choose a new version or output directory.' }
$scratchRoot = Join-Path $repo 'artifacts/msix-staging'
$scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch, $OutputDirectory -Force | Out-Null
try {
    & (Join-Path $PSScriptRoot 'New-LiveLingoPackage.ps1') -Version $Version -OutputDirectory $scratch -SelfContained
    $expanded = Join-Path $scratch 'expanded'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $scratch "LiveLingo-$Version-win-x64.zip"), $expanded)
    $payload = Join-Path $expanded 'desktop'
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'packaging/msix/AppxManifest.xml') -Raw
    $manifest.Package.Identity.Version = $packageVersion.ToString()
    if ($PSCmdlet.ParameterSetName -eq 'Identity') {
        $manifest.Package.Identity.Name = $IdentityName
        $manifest.Package.Identity.Publisher = $Publisher
    }
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $writer = [Xml.XmlWriter]::Create((Join-Path $payload 'AppxManifest.xml'), $settings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/msix/Assets') -Destination $payload -Recurse
    $stagedPackage = Join-Path $scratch 'LiveLingo.msix'
    $log = Join-Path $env:TEMP ('msix-pack-' + [guid]::NewGuid().ToString('N') + '.log')
    & $makeappx pack /d $payload /p $stagedPackage /o *> $log
    if ($LASTEXITCODE -ne 0) { Select-String -LiteralPath $log -Pattern '\berror\b'; throw "MakeAppx failed; log=$log" }
    & (Join-Path $PSScriptRoot 'Test-LiveLingoMsix.ps1') -Package $stagedPackage -MakeAppxPath $makeappx
    Copy-Item -LiteralPath $stagedPackage -Destination $package
    Write-Output "MSIX=$package; Version=$packageVersion; Signed=False; Identity=$($manifest.Package.Identity.Name); SHA256=$((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant())"
} finally {
    Remove-MsixScratch -Path $scratch -Root $scratchRoot
}
