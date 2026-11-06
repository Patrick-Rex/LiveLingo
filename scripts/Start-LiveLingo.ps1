param([string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
if (-not $ApplicationDirectory) {
    $registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveLingo' -ErrorAction SilentlyContinue
    $ApplicationDirectory = if ($registered.InstallLocation -and (Test-Path -LiteralPath (Join-Path $registered.InstallLocation 'LiveLingo.exe') -PathType Leaf)) {
        $registered.InstallLocation
    } else { Join-Path $env:LOCALAPPDATA 'LiveLingo' }
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'LiveLingo.exe') -PathType Leaf)) { $ApplicationDirectory = 'D:\Soft Ware\LiveLingo\app\LiveLingo' }
}
$executable = Join-Path $ApplicationDirectory 'LiveLingo.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { $executable = Join-Path $ApplicationDirectory 'LiveLingo.Desktop.Windows.exe' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Desktop application not found. Run LiveLingo-win-x64-Setup.exe or set -ApplicationDirectory to a published directory.' }
$application = Start-Process -FilePath $executable -WorkingDirectory $ApplicationDirectory -PassThru -WindowStyle Hidden
Write-Output "LiveLingo PID=$($application.Id); closing the control window stops capture and its model processes."
