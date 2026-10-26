#requires -Version 5.1
<#
.SYNOPSIS
Installs a verified LiveLingo payload into one desktop directory without modifying models or settings.
#>
[CmdletBinding()]
param(
    [string]$ApplicationRoot = 'D:\Soft Ware\LiveLingo\app',
    [switch]$ReplaceExisting,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoLinks([string]$Path) {
    # Reject links in both the ancestry and the contents before copying, moving or deleting.
    $ancestor = [IO.Path]::GetFullPath($Path)
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked path: $ancestor" }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $Path) {
        foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked entry: $($item.FullName)" }
        }
    }
}

function Assert-Payload([string]$Directory, $Manifest) {
    Assert-NoLinks $Directory
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Manifest.files) {
        $relative = [string]$entry.path
        if ($relative -notmatch '^[a-zA-Z0-9_. /-]+$' -or $relative.StartsWith('/') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw "Invalid package path: $relative" }
        $full = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($relative)) { throw "Invalid or duplicate entry: $relative" }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing file: $relative" }
        if ((Get-Item -LiteralPath $full).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Checksum mismatch: $relative" }
    }
    if (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File).Count -ne $seen.Count) { throw 'Unlisted files in payload.' }
    if (-not $seen.Contains('LiveLingo.Desktop.Windows.exe')) { throw 'Application executable missing from manifest.' }
}

function Remove-InstallScratch([string]$Path) {
    # Only exact transaction directories immediately below the verified application root are removable.
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($full) -ne $root -or [IO.Path]::GetFileName($full) -notin @('.desktop-install', '.desktop-backup')) { throw "Unsafe cleanup target: $full" }
    Assert-NoLinks $full
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.product -ne 'LiveLingo' -or $manifest.schemaVersion -ne 1 -or $manifest.runtime -ne 'win-x64') { throw 'Unsupported package.' }
$payload = Join-Path $PSScriptRoot 'desktop'
Assert-Payload $payload $manifest
if ($VerifyOnly) { Write-Output "Verified=$($manifest.version); Files=$($manifest.files.Count)"; return }
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'Windows 11 x64 and 64-bit PowerShell are required.' }
$dotnet = Get-Command dotnet -ErrorAction Stop
$frameworks = & $dotnet.Source --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($frameworks | Where-Object { $_ -match '^Microsoft.NETCore.App 10\.0\.\d+ ' })) { throw '.NET 10.0 x64 runtime is required; any stable servicing version is accepted.' }
if ($ApplicationRoot -notmatch '^[D-Zd-z]:\\') { throw 'ApplicationRoot must be an absolute local path on a non-C drive.' }
$root = [IO.Path]::GetFullPath($ApplicationRoot).TrimEnd('\')
if ([IO.Path]::GetFileName($root) -ne 'app') { throw 'ApplicationRoot must be named app; installation is always app\desktop.' }
$destination = Join-Path $root 'desktop'
$staging = Join-Path $root '.desktop-install'
$backup = Join-Path $root '.desktop-backup'
Assert-NoLinks $root
if ($payload.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase) -or $payload -eq $root) { throw 'Extract the package outside the installation tree.' }
if ((Test-Path -LiteralPath $staging) -or (Test-Path -LiteralPath $backup)) { throw 'A previous installation transaction remains. Inspect .desktop-install/.desktop-backup before retrying.' }
if (Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) }) { throw 'Close LiveLingo before installing.' }
$exists = Test-Path -LiteralPath $destination
if ($exists -and (-not $ReplaceExisting -or -not (Test-Path -LiteralPath (Join-Path $destination 'LiveLingo.Desktop.Windows.exe') -PathType Leaf))) { throw 'Existing installation: use -ReplaceExisting only for a LiveLingo application directory.' }
New-Item -ItemType Directory -Path $root -Force | Out-Null
$installed = $false
try {
    Copy-Item -LiteralPath $payload -Destination $staging -Recurse
    Assert-Payload $staging $manifest
    if ($exists) { Move-Item -LiteralPath $destination -Destination $backup }
    try {
        Move-Item -LiteralPath $staging -Destination $destination
        $installed = $true
    } catch {
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $destination }
        throw
    }
    if ($exists) { Remove-InstallScratch $backup }
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-InstallScratch $staging }
}
if ($installed) { Write-Output "Installed=$($manifest.version); Executable=$(Join-Path $destination 'LiveLingo.Desktop.Windows.exe')" }
