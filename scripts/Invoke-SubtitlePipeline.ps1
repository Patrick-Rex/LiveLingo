<#
.PARAMETER LivePriority
按桌面直播的短段识别和积压续接策略回放已有 WAV。进程采集自动使用该策略。
#>
[CmdletBinding(DefaultParameterSetName = 'Replay')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Replay')][string]$InputWave,
    [Parameter(ParameterSetName = 'Replay')][switch]$LivePriority,
    [Parameter(Mandatory, ParameterSetName = 'Capture')][ValidateRange(1, 2147483647)][int]$TargetPid,
    [ValidateRange(0, 86400)][int]$DurationSeconds = 0,
    [ValidateSet('Chinese','English','Japanese','Korean', IgnoreCase = $false)][string]$AsrLanguage = 'Japanese',
    [ValidateSet('translategemma:4b', 'translategemma:12b')][string]$TranslationModel = 'translategemma:12b',
    [ValidateSet(0, 60, 120)][int]$AlignmentRecoverySeconds = 120,
    [switch]$PreserveObservedRepetitions,
    [ValidateSet('win-x64-cpu','win-x64-rocm','win-x64-cu128','win-x64-cu130')][string]$RuntimeProfile = 'win-x64-cpu',
    [string]$PythonPath = (Join-Path $env:LOCALAPPDATA "LiveLingoData\runtimes\$RuntimeProfile\Scripts\python.exe"),
    [string]$Model = (Join-Path $env:LOCALAPPDATA 'LiveLingoData\models\Qwen3-ASR-1.7B-7278e1e'),
    [string]$Ollama = (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
    [string]$CacheRoot = (Join-Path $env:LOCALAPPDATA 'LiveLingoData\cache'),
    [string]$OutputDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1') -CacheRoot $CacheRoot
$probePath = Join-Path $repository 'tools\LiveLingo.Probe\bin\Release\net10.0-windows10.0.26100.0\LiveLingo.Probe.exe'
if (-not $SkipBuild) {
    $buildLog = Join-Path $env:TEMP ('pipeline-build-' + [Guid]::NewGuid().ToString('N') + '.log')
    & dotnet build (Join-Path $repository 'tools\LiveLingo.Probe\LiveLingo.Probe.csproj') -c Release --no-restore -v:q *> $buildLog
    $buildExit = $LASTEXITCODE
    Select-String -LiteralPath $buildLog -Pattern '\berror\b' | ForEach-Object { Write-Host $_.Line }
    if ($buildExit -ne 0) { throw "BuildExit=$buildExit；完整日志: $buildLog。首次准备先执行 dotnet restore --locked-mode。" }
}
if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) { throw "Probe 不存在: $probePath" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Join-Path $env:LOCALAPPDATA 'LiveLingoData\data\evaluation') ('pipeline-' + $PSCmdlet.ParameterSetName.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$profile = Join-Path $repository "runtime-profiles\$RuntimeProfile\profile.json"
$arguments = @('--repository', $repository, '--profile', $profile, '--python', $PythonPath, '--model', $model, '--ollama', $ollama,
    '--output', $OutputDirectory, '--asr-language', $AsrLanguage, '--translation-model', $TranslationModel, '--alignment-recovery-seconds', $AlignmentRecoverySeconds.ToString())
if ($PreserveObservedRepetitions) { $arguments += '--preserve-observed-repetitions' }
if ($PSCmdlet.ParameterSetName -eq 'Capture') {
    if ($DurationSeconds -eq 0) { $DurationSeconds = 180 }
    $command = 'pipeline-capture'
    $arguments += @('--pid', $TargetPid.ToString())
    Write-Host "采集 PID=$TargetPid 的整个进程树。模型就绪后开始计时，不接管目标应用。"
} else {
    $InputWave = (Resolve-Path -LiteralPath $InputWave).ProviderPath
    $command = 'pipeline-replay'
    $arguments += @('--input', $InputWave)
    if ($LivePriority) { $arguments += '--live-priority' }
    Write-Host '按真实音频时间回放；DurationSeconds=0 只播放文件一次，正数显式循环至指定时长。'
}
$arguments += @('--duration-seconds', $DurationSeconds.ToString())
Write-Host "本次显式评测会保存原文、译文及版本；可在证据目录创建 stop.request 正常停止，交互终端也支持 Ctrl+C。证据目录: $OutputDirectory"
& $probePath $command @arguments
$pipelineExit = $LASTEXITCODE
Write-Host "PipelineExit=$pipelineExit；证据目录: $OutputDirectory"
exit $pipelineExit
