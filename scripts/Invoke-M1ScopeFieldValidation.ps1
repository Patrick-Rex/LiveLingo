param(
    [string]$ProbePath = (Join-Path $PSScriptRoot '../tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe'),
    [string]$Fixtures = 'D:\Soft Ware\LiveLingo\data\fixtures\scope-tones',
    [int]$TargetProcessId = 0
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
if (-not (Test-Path -LiteralPath $ProbePath)) { throw "Probe 不存在: $ProbePath" }
$targets = @(& $ProbePath targets --json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw '目标查询失败。' }
$candidates = @($targets | Where-Object { $_.Name -eq 'msedge.exe' -and $_.IsApplicationRoot -and ($TargetProcessId -eq 0 -or $_.ProcessId -eq $TargetProcessId) })
if ($candidates.Count -ne 1) { throw "检测到 $($candidates.Count) 个匹配 Edge 根；请用 -TargetProcessId 指定已核实根。" }
$runPath = Join-Path 'D:\Soft Ware\LiveLingo\data\capture' ('m1-scope-field-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "使用 Edge 根 PID=$($candidates[0].ProcessId)，音频范围包括全部子孙与标签页。"
Write-Host '请将下面两个本地地址分别复制到该 Edge 的两个标签页；保留另一个空白标签页。'
Write-Host ([Uri](Join-Path $Fixtures 'target.html')).AbsoluteUri
Write-Host ([Uri](Join-Path $Fixtures 'second.html')).AbsoluteUri
Write-Host "证据目录: $runPath"
& $ProbePath field-scope-check --pid $candidates[0].ProcessId --fixtures $Fixtures --output $runPath
$runExit = $LASTEXITCODE
Write-Host "FieldScopeExit=$runExit；证据目录: $runPath"
if ($runExit -eq 0) {
    $python = 'D:\Soft Ware\LiveLingo\tools\spec-kit-1.1.3\Scripts\python.exe'
    if (Test-Path -LiteralPath $python) {
        & $python (Join-Path $PSScriptRoot '../tests/Fixtures/Audio/verify_scope.py') $runPath
        Write-Host "SpectrumExit=$LASTEXITCODE"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
exit $runExit
