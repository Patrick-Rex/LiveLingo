<#
.SYNOPSIS
Checks local release separation and rejects invalid feeds without building, uploading or installing the application.
#>
#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release-export-tests'))
$scratch = Join-Path $root ([guid]::NewGuid().ToString('N'))
$source = Join-Path $scratch 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$script = Join-Path $PSScriptRoot 'Export-LiveLingoRelease.ps1'

function Write-TestFeed {
    <# .SYNOPSIS Writes a fixture feed with explicitly supplied assets. .PARAMETER Assets The feed records to validate. #>
    param([object[]]$Assets)
    [IO.File]::WriteAllText((Join-Path $source 'releases.win-x64.json'), (@{ Assets = $Assets } | ConvertTo-Json -Depth 5) + "`n", $utf8)
}

function Assert-RejectedExport {
    <# .SYNOPSIS Requires the current fixture feed to fail without leaving a completed or partial export. .PARAMETER Name The isolated test output name. .PARAMETER Pattern The expected failure text. #>
    param([string]$Name, [string]$Pattern)
    $output = Join-Path $scratch $Name
    $rejected = $false
    try { & $script -Version 0.2.6 -ReleaseDirectory $source -OutputDirectory $output | Out-Null }
    catch { if ($_.Exception.Message -notlike $Pattern) { throw }; $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $output)) { throw "Invalid feed did not fail cleanly: $Name" }
    Write-Output "PASS: $Name rejected without output."
}

function New-InstallerFixture {
    <#
    .SYNOPSIS
    Compiles a minimal, never-executed PE fixture carrying installer product/version metadata.
    .PARAMETER Path
    New output file path for the fixture; no existing file is overwritten.
    .PARAMETER Version
    Product version embedded into the Windows PE resource.
    .PARAMETER Product
    Product name embedded into the Windows PE resource.
    #>
    param([string]$Path, [string]$Version, [string]$Product = 'LiveLingo')
    $typeName = 'InstallerFixture_' + [guid]::NewGuid().ToString('N')
    $sourceCode = @"
using System.Reflection;
[assembly: AssemblyProduct("$Product")]
[assembly: AssemblyInformationalVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
/// <summary>Provides version resources for a release export fixture; never loaded or executed.</summary>
public sealed class $typeName { }
"@
    # Add-Type omits native version resources, so use its bundled Roslyn compiler explicitly.
    Add-Type -AssemblyName Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.CSharp
    $syntax = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceCode)
    $reference = [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($typeName,
        [Microsoft.CodeAnalysis.SyntaxTree[]]@($syntax), [Microsoft.CodeAnalysis.MetadataReference[]]@($reference), $options)
    $resources = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
    $output = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $result = $compilation.Emit($output, $null, $null, $resources, $null, $null, [Threading.CancellationToken]::None)
        if (-not $result.Success) { throw ($result.Diagnostics | Where-Object Severity -eq Error | Out-String) }
    } finally { $output.Dispose(); $resources.Dispose() }
}

try {
    $setupPath = Join-Path $source 'LiveLingo-win-x64-Setup.exe'
    New-InstallerFixture -Path $setupPath -Version '0.2.6'
    [IO.File]::WriteAllText((Join-Path $source 'Install.ps1'), '# obsolete standalone script', $utf8)
    [IO.File]::WriteAllText((Join-Path $source 'USER-GUIDE.md'), '# obsolete standalone guide', $utf8)
    $assets = @(foreach ($item in @(@('0.2.5','Full'), @('0.2.6','Full'), @('0.2.6','Delta'))) {
        $name = "LiveLingo-$($item[0])-win-x64-$($item[1].ToLowerInvariant()).nupkg"
        $path = Join-Path $source $name
        [IO.File]::WriteAllText($path, "package fixture: $name", $utf8)
        [ordered]@{ PackageId='LiveLingo'; Version=$item[0]; Type=$item[1]; FileName=$name;
            Size=(Get-Item -LiteralPath $path).Length; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;
            SHA1=(Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash }
    })
    Write-TestFeed $assets
    $output = Join-Path $scratch 'valid'
    & $script -Version 0.2.6 -ReleaseDirectory $source -OutputDirectory $output | Out-Null
    $installers = @(Get-ChildItem -LiteralPath (Join-Path $output 'installers') -File)
    $updates = @(Get-ChildItem -LiteralPath (Join-Path $output 'updates') -File)
    if ($installers.Count -ne 1 -or $installers[0].Name -ne 'LiveLingo-win-x64-Setup.exe' -or $updates.Count -ne 4 -or
        @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Extension -in @('.ps1','.md') }).Count) { throw 'Installer/update separation failed.' }
    $exported = Get-Content -LiteralPath (Join-Path $output 'updates/releases.win-x64.json') -Raw | ConvertFrom-Json
    if ($exported.Assets.Count -ne 3 -or -not (Test-Path -LiteralPath (Join-Path $output 'updates/LiveLingo-0.2.5-win-x64-full.nupkg'))) { throw 'Historical update chain was dropped.' }
    foreach ($line in Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS')) {
        $parts = $line -split '\s+',2
        if ((Get-FileHash -LiteralPath (Join-Path $output $parts[1]) -Algorithm SHA256).Hash -ne $parts[0]) { throw 'Export checksum mismatch.' }
    }
    Write-Output 'PASS: only installer downloads; full update feed retained; scripts excluded; hashes match.'
    $rejected = $false
    try { & $script -Version 0.2.6 -ReleaseDirectory $source -OutputDirectory $output | Out-Null }
    catch { if ($_.Exception.Message -notlike 'Distribution output must be absent or empty*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Existing export was overwritten.' }
    Write-Output 'PASS: existing export preserved.'
    $empty = Join-Path $scratch 'empty'
    New-Item -ItemType Directory -Path $empty | Out-Null
    & $script -Version 0.2.6 -ReleaseDirectory $source -OutputDirectory $empty | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $empty 'distribution.json'))) { throw 'Empty destination not supported.' }
    Write-Output 'PASS: empty output directory accepted.'
    $saved = $assets[0].SHA256; $assets[0].SHA256 = '0' * 64
    Write-TestFeed $assets; Assert-RejectedExport 'bad-hash' 'Update package size or checksum mismatch*'
    $assets[0].SHA256 = $saved
    $saved = $assets[0].FileName; $assets[0].FileName = '../outside.nupkg'
    Write-TestFeed $assets; Assert-RejectedExport 'traversal' 'Unexpected path or duplicate file*'
    $assets[0].FileName = $saved
    Write-TestFeed @($assets + $assets[0]); Assert-RejectedExport 'duplicate' 'Unexpected path or duplicate file*'
    Write-TestFeed @($assets[0]); Assert-RejectedExport 'missing-current' 'Update feed is missing*'
    $saved = $assets[0].Version; $savedName = $assets[0].FileName
    $assets[0].Version = '0.2.3'; $assets[0].FileName = 'LiveLingo-0.2.3-win-x64-full.nupkg'
    Write-TestFeed $assets; Assert-RejectedExport 'missing-package' '*does not exist*'
    $assets[0].Version = $saved; $assets[0].FileName = $savedName
    Write-TestFeed $assets
    $oldSetup = Join-Path $scratch 'old-setup.exe'
    New-InstallerFixture -Path $oldSetup -Version '0.2.5'
    Copy-Item -LiteralPath $oldSetup -Destination $setupPath -Force
    Assert-RejectedExport 'stale-installer' 'Setup product or version does not match*'
    $otherSetup = Join-Path $scratch 'other-setup.exe'
    New-InstallerFixture -Path $otherSetup -Version '0.2.6' -Product 'DifferentProduct'
    Copy-Item -LiteralPath $otherSetup -Destination $setupPath -Force
    Assert-RejectedExport 'wrong-product' 'Setup product or version does not match*'
    if (@(Get-ChildItem -LiteralPath $scratch -Directory -Filter '.distribution-*').Count) { throw 'Export staging was not cleaned.' }
    Write-Output 'PASS: all release export checks completed (10 cases).'
} finally {
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -ne $root) { throw 'Unsafe release export test cleanup.' }
    if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked export test path; cleanup stopped.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
