<#
.SYNOPSIS
Builds a framework-dependent Velopack release with optional deltas from the previous full package.
.PARAMETER DownloadPrevious
Downloads the latest public release before packing. Omit for the first release or when the previous full package is already in OutputDirectory.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/releases'),
    [switch]$DownloadPrevious
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (Get-ChildItem -LiteralPath $OutputDirectory -Filter "LiveLingo-$Version-*-full.nupkg") { throw 'Release version already exists; choose a new version.' }
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release-staging'))
$scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Push-Location $repo
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Pinned release tool restore failed.' }
    if ($DownloadPrevious) {
        dotnet tool run vpk -- download github --repoUrl https://github.com/Patrick-Rex/LiveLingo --channel win-x64 --outputDir $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Previous release download failed. For the first release, omit DownloadPrevious.' }
    }
    & (Join-Path $PSScriptRoot 'New-LiveLingoPackage.ps1') -Version $Version -OutputDirectory $scratch
    $archive = Join-Path $scratch "LiveLingo-$Version-win-x64.zip"
    $expanded = Join-Path $scratch 'expanded'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
    $payload = Join-Path $expanded 'desktop'
    dotnet tool run vpk -- pack --packId LiveLingo --packTitle LiveLingo --packAuthors Patrick-Rex --packVersion $Version --packDir $payload --mainExe LiveLingo.Desktop.Windows.exe --runtime win-x64 --channel win-x64 --framework net10.0-x64-runtime --noPortable --outputDir $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
    # Users download only Setup.exe. Dependency scripts and the guide are inside its payload.
    $lines = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Extension -in @('.exe', '.nupkg', '.json', '.ps1', '.md') } | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Release=$OutputDirectory; Version=$Version; uploaded=False"
} finally {
    Pop-Location
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -ne $scratchRoot) { throw 'Unsafe release staging cleanup.' }
    if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked release staging entry; cleanup stopped.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
