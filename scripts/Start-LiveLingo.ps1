param([string]$ApplicationDirectory = 'D:\Soft Ware\LiveLingo\app\LiveLingo')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
$executable = Join-Path $ApplicationDirectory 'LiveLingo.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { $executable = Join-Path $ApplicationDirectory 'LiveLingo.Desktop.Windows.exe' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Desktop application not found. Run Install-LiveLingo.ps1 or set -ApplicationDirectory to a published directory.' }
$application = Start-Process -FilePath $executable -WorkingDirectory $ApplicationDirectory -PassThru -WindowStyle Hidden
Write-Output "LiveLingo PID=$($application.Id); closing the control window stops capture and its model processes."
