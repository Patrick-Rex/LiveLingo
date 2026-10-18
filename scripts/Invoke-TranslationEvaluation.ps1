param(
    [Parameter(Mandatory,ParameterSetName='Text')][ValidateLength(1,12000)][string]$Text,
    [Parameter(Mandatory,ParameterSetName='Inputs')][string]$Inputs,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('translategemma:4b','translategemma:12b')][string[]]$Models = @('translategemma:12b'),
    [ValidateSet('official','faithful-v1','faithful-v2','faithful-v3')][string]$PromptPolicy = 'official',
    [ValidateRange(0,2)][int]$FidelityRetries = 0,
    [ValidateSet('source','draft')][string]$FidelityMode = 'source',
    [string]$Ollama = (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathFullyQualified($Output) -or [IO.Path]::GetPathRoot($Output) -notmatch '^[D-Z]:\\$') { throw 'Expected absolute local non-C output.' }
$outputPath = [IO.Path]::GetFullPath($Output).TrimEnd('\')
for ($ancestor = [IO.DirectoryInfo]::new($outputPath); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output path cannot traverse links.' }
}
$requestPath = $outputPath + '-request.json'
$inputPath = $outputPath + '-inputs.json'
foreach ($path in @($outputPath,$requestPath,$inputPath,($outputPath + '-supervisor'))) {
    if (Test-Path -LiteralPath $path) { throw "Refusing existing evidence: $path" }
}
$probe = Join-Path $repoRoot 'tools\LiveLingo.Probe\bin\Release\net10.0-windows10.0.26100.0\LiveLingo.Probe.exe'
$profile = Get-Content -LiteralPath (Join-Path $repoRoot 'runtime-profiles\win-x64-cu130\profile.json') -Raw | ConvertFrom-Json
foreach ($path in @($probe,$profile.interpreter,$Ollama)) {
    if (-not [IO.Path]::IsPathFullyQualified($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Prepare the required executable: $path" }
}
if ($PSCmdlet.ParameterSetName -eq 'Inputs') {
    if (-not [IO.Path]::IsPathFullyQualified($Inputs) -or -not (Test-Path -LiteralPath $Inputs -PathType Leaf)) { throw 'Expected an existing absolute input JSON path.' }
    $inputPath = [IO.Path]::GetFullPath($Inputs)
} elseif ([string]::IsNullOrWhiteSpace($Text)) { throw 'Source text cannot be blank.' }
New-Item -ItemType Directory -Force -Path (Split-Path $outputPath -Parent) | Out-Null
if ($PSCmdlet.ParameterSetName -eq 'Text') {
    $inputText = (@{items=@(@{id='local-text';text=$Text;origin='authored-test'})} | ConvertTo-Json -Depth 5) + "`n"
    $stream = [IO.File]::Open($inputPath,[IO.FileMode]::CreateNew)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($inputText); $stream.Write($bytes) } finally { $stream.Dispose() }
}
$request = [ordered]@{
    Executable=$profile.interpreter
    Arguments=@((Join-Path $repoRoot 'tools\model_eval\private_ollama.py'),'--ollama',$Ollama,'--output',$outputPath,'--inputs',$inputPath,'--prompt-policy',$PromptPolicy,'--fidelity-retries',"$FidelityRetries",'--fidelity-mode',$FidelityMode,'--models') + $Models
    WorkingDirectory=$repoRoot
    TimeoutSeconds=900
}
$requestText = ($request | ConvertTo-Json -Depth 5) + "`n"
$stream = [IO.File]::Open($requestPath,[IO.FileMode]::CreateNew)
try { $bytes=[Text.Encoding]::UTF8.GetBytes($requestText); $stream.Write($bytes) } finally { $stream.Dispose() }
& $probe model-eval --request $requestPath --output ($outputPath + '-supervisor')
$evaluationExit = $LASTEXITCODE
Write-Output "EvaluationExit=$evaluationExit; Evidence=$outputPath"
exit $evaluationExit
