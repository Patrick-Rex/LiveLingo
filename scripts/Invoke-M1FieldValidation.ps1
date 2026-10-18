param(
    [string]$ProbePath = (Join-Path $PSScriptRoot '../tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe'),
    [int]$DurationSeconds = 900
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
if (-not (Test-Path -LiteralPath $ProbePath)) { throw "Probe 不存在: $ProbePath" }
$candidates = @((& $ProbePath targets --json | ConvertFrom-Json) | Where-Object { $_.Name -eq 'msedge.exe' -and $_.IsApplicationRoot })
if ($LASTEXITCODE -ne 0) { throw '目标查询失败。' }
if ($candidates.Count -ne 1) { throw "检测到 $($candidates.Count) 个 Edge 根实例；请使用 targets 命令明确选择，不能自动猜测。" }
$selectedPid = $candidates[0].ProcessId
$runPath = Join-Path 'D:\Soft Ware\LiveLingo\data\capture' ('m1-field-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "Edge 根 PID=$selectedPid；范围包含其全部子孙及可能的多个标签页。"
Write-Host "录制 $DurationSeconds 秒，Ctrl+C 可提前结束。证据目录: $runPath"
& $ProbePath capture --pid $selectedPid --output $runPath --duration-seconds $DurationSeconds
$exitCode = $LASTEXITCODE
Write-Host "CaptureExit=$exitCode；证据目录: $runPath"
exit $exitCode
