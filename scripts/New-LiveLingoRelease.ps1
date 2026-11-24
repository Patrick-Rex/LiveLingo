<#
.SYNOPSIS
Builds a framework-dependent Velopack release with optional deltas from the previous full package.
.PARAMETER DownloadPrevious
Downloads the latest full package from the fixed updates-win-x64 release before packing. Omit for the first release or when the previous full package is already in OutputDirectory.
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
# A reused output directory must not silently reintroduce scripts or guides as release assets.
$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object {
    $_.PSIsContainer -or $_.Name -notmatch '^(LiveLingo-\d+\.\d+\.\d+-win-x64-(full|delta)\.nupkg|LiveLingo-win-x64-Setup\.exe|(releases|assets)\.win-x64\.json|RELEASES-win-x64|SHA256SUMS)$'
})
if ($unexpected.Count) { throw "Unexpected release files; use a clean output directory: $($unexpected.Name -join ', ')" }
if (Get-ChildItem -LiteralPath $OutputDirectory -Filter "LiveLingo-$Version-*-full.nupkg") { throw 'Release version already exists; choose a new version.' }
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release-staging'))
$scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Push-Location $repo
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Pinned release tool restore failed.' }
    if ($DownloadPrevious) {
        dotnet tool run vpk -- download http --url https://github.com/Patrick-Rex/LiveLingo/releases/download/updates-win-x64/ --channel win-x64 --outputDir $OutputDirectory
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
    $assetNames = @('LiveLingo-win-x64-Setup.exe', 'releases.win-x64.json', 'RELEASES-win-x64',
        "LiveLingo-$Version-win-x64-full.nupkg")
    $deltaName = "LiveLingo-$Version-win-x64-delta.nupkg"
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory $deltaName)) { $assetNames += $deltaName }
    $lines = $assetNames | Sort-Object | ForEach-Object {
        $asset = Get-Item -LiteralPath (Join-Path $OutputDirectory $_) -ErrorAction Stop
        '{0}  {1}' -f (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $asset.Name
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
