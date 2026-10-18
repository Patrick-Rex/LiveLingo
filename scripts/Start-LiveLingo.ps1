param([string]$ApplicationDirectory = 'D:\Soft Ware\LiveLingo\app\desktop')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
$executable = Join-Path $ApplicationDirectory 'LiveLingo.Desktop.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '未找到桌面程序。先运行 ./scripts/Publish-LiveLingo.ps1，或指定 -ApplicationDirectory。' }
$application = Start-Process -FilePath $executable -WorkingDirectory $ApplicationDirectory -PassThru -WindowStyle Hidden
Write-Output "LiveLingo PID=$($application.Id)；关闭控制窗会停止采集并回收自有模型进程。"
