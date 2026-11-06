<#
.SYNOPSIS
Installs the external .NET runtime, then launches the adjacent Velopack installer.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory = 'D:\Soft Ware\LiveLingo\app\LiveLingo',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
if ($InstallDirectory -notmatch '^[D-Zd-z]:[\\/]' -or $InstallDirectory.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) { throw 'Choose a valid non-C local absolute install directory.' }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
if ($InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory)) { throw 'Do not install into a drive root.' }
$ancestor = $InstallDirectory
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked install directory is not supported.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if ((Test-Path -LiteralPath $InstallDirectory) -and @(Get-ChildItem -LiteralPath $InstallDirectory -Force).Count -gt 0 -and
    (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Update.exe')) -or -not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'current\LiveLingo.Desktop.Windows.exe')))) { throw 'Install directory contains unrelated files; choose an empty directory.' }
if ((Split-Path $InstallDirectory -Leaf) -eq 'desktop') { throw 'Choose a new directory for the managed install; do not mix it with the old ZIP desktop directory.' }
$installer = Join-Path $PSScriptRoot 'LiveLingo-win-x64-Setup.exe'
$dependencies = Join-Path $PSScriptRoot 'Install-LiveLingoDependencies.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer missing: $installer" }
if ($CheckOnly) { & $dependencies -Component Check; Write-Output "Installer=$installer; Target=$InstallDirectory"; return }
& $dependencies -Component DotNet
# Setup performs its own runtime check as well; install and app launch are interactive.
$process = Start-Process -FilePath $installer -ArgumentList @('--installto', ('"' + $InstallDirectory + '"')) -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Installation failed: $($process.ExitCode)" }
Write-Output "Installed=$InstallDirectory; use the Start Menu shortcut."
