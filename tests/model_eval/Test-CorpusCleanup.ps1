$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cleanupScript = Join-Path $repoRoot 'scripts/Remove-EvaluationCorpus.ps1'
$parent = 'D:\Soft Ware\LiveLingo\data\evaluation\corpora'
$directory = Join-Path $parent ('cleanup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$asset = Join-Path $directory 'owned.txt'
[IO.File]::WriteAllText($asset, 'original')
$manifest = @{ schemaVersion=1; kind='disposable-evaluation-corpus'; root=$directory; files=@(@{path='owned.txt'; sha256=(Get-FileHash -LiteralPath $asset).Hash.ToLowerInvariant()}) }
[IO.File]::WriteAllText((Join-Path $directory 'assets.json'), ($manifest | ConvertTo-Json -Depth 5))
$unexpected = Join-Path $directory 'unregistered.txt'
[IO.File]::WriteAllText($unexpected, 'preserve')
$rejected = $false
try { & $cleanupScript -Directory $directory } catch { $rejected = $true }
if (-not $rejected -or -not (Test-Path -LiteralPath $asset) -or -not (Test-Path -LiteralPath $unexpected)) { throw 'Unknown-file guard failed.' }
# This exact file was created immediately above and is confined to our fixture directory.
if ((Split-Path ([IO.Path]::GetFullPath($unexpected)) -Parent) -ne $directory) { throw 'Fixture path escaped.' }
Remove-Item -LiteralPath $unexpected
[IO.File]::WriteAllText($asset, 'changed')
$rejected = $false
try { & $cleanupScript -Directory $directory } catch { $rejected = $true }
if (-not $rejected -or [IO.File]::ReadAllText($asset) -ne 'changed') { throw 'Changed-file guard failed.' }
[IO.File]::WriteAllText($asset, 'original')
& $cleanupScript -Directory $directory
if (Test-Path -LiteralPath $directory) { throw 'Owned corpus cleanup failed.' }
Write-Output 'CorpusCleanupChecks=3; Passed=3; Failed=0'
