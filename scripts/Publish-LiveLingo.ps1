param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/publish/desktop'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) { throw '发布暂存目录非空。正式安装请使用 New-LiveLingoPackage.ps1 和包内 Install.ps1，固定安装名称 desktop。' }
$log = Join-Path $env:TEMP ('desktop-publish-' + [guid]::NewGuid().ToString('N') + '.log')
dotnet restore (Join-Path $repo 'src/LiveLingo.Desktop.Windows/LiveLingo.Desktop.Windows.csproj') --locked-mode --disable-build-servers -r win-x64 *> $log
if ($LASTEXITCODE -ne 0) { Select-String -LiteralPath $log -Pattern '\berror\b'; throw "Locked restore failed; log=$log" }
dotnet publish (Join-Path $repo 'src/LiveLingo.Desktop.Windows/LiveLingo.Desktop.Windows.csproj') -c Release -r win-x64 --no-restore --disable-build-servers --self-contained false -p:CopyOutputSymbolsToPublishDirectory=false -p:PublishDocumentationFiles=false -o $OutputDirectory -v:q *> $log
$result = $LASTEXITCODE
Select-String -LiteralPath $log -Pattern '\berror\b'
if ($result -ne 0) { throw "PublishExit=$result; log=$log" }
$required = @('LiveLingo.Desktop.Windows.exe', 'workers/qwen_asr/service.py', 'tools/model_eval/common.py', 'tools/model_eval/prepare_model.py', 'tools/model_eval/download.py', 'runtime-profiles/win-x64-cu130/profile.json', 'runtime-profiles/win-x64-cu130/requirements.lock')
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $relative) -PathType Leaf)) { throw "发布依赖缺失：$relative" } }
Write-Output "PublishExit=0; Executable=$(Join-Path $OutputDirectory 'LiveLingo.Desktop.Windows.exe')"
