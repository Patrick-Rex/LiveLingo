param(
    [Parameter(Mandatory)][ValidateSet('win-x64-cu128','win-x64-cu130')][string]$Profile,
    [Parameter(Mandatory)][string]$Audio,
    [Parameter(Mandatory)][string]$Output,
    [string]$Model = 'D:\Soft Ware\LiveLingo\models\Qwen3-ASR-1.7B-7278e1e',
    [ValidateRange(1,100)][int]$Repeats = 3,
    [ValidateRange(1,4096)][int]$MaxNewTokens = 512,
    [ValidateRange(1,1200)][int]$MaxWindowSeconds = 30,
    [ValidateSet('auto','Japanese','English','Chinese')][string]$Language = 'auto'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathFullyQualified($Output) -or [IO.Path]::GetPathRoot($Output) -notmatch '^[D-Z]:\\$') { throw 'Expected absolute local non-C output.' }
$outputPath = [IO.Path]::GetFullPath($Output).TrimEnd('\')
for ($ancestor = [IO.DirectoryInfo]::new($outputPath); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output path cannot traverse links.' }
}
if (Test-Path -LiteralPath $outputPath) { throw 'Refusing existing output directory.' }
$requestPath = $outputPath + '-request.json'
if (Test-Path -LiteralPath $requestPath) { throw 'Refusing existing request file.' }
$probe = Join-Path $repoRoot 'tools\LiveLingo.Probe\bin\Release\net10.0-windows10.0.26100.0\LiveLingo.Probe.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw 'Build the Release Probe before running model evaluation.' }
New-Item -ItemType Directory -Force -Path (Split-Path $outputPath -Parent) | Out-Null
$profilePath = Join-Path $repoRoot "runtime-profiles\$Profile\profile.json"
$profileData = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$request = [ordered]@{
    Executable=$profileData.interpreter
    Arguments=@((Join-Path $repoRoot 'tools\model_eval\asr_benchmark.py'),'--profile',$profilePath,'--model',$Model,'--audio',$Audio,'--output',$outputPath,'--repeats',"$Repeats",'--max-new-tokens',"$MaxNewTokens",'--max-window-seconds',"$MaxWindowSeconds",'--language',$Language)
    WorkingDirectory=$repoRoot
    TimeoutSeconds=900
}
$requestText = ($request | ConvertTo-Json -Depth 5) + "`n"
$stream = [IO.File]::Open($requestPath, [IO.FileMode]::CreateNew)
try { $bytes=[Text.Encoding]::UTF8.GetBytes($requestText); $stream.Write($bytes) } finally { $stream.Dispose() }
& $probe model-eval --request $requestPath --output ($outputPath + '-supervisor')
Write-Output "EvaluationExit=$LASTEXITCODE; Evidence=$outputPath"
exit $LASTEXITCODE
