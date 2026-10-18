<#
.SYNOPSIS
Exercises package integrity, installation and replacement in a disposable non-C directory.
#>
param([Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
$tempRoot = (Resolve-Path -LiteralPath $env:TEMP).ProviderPath
$work = Join-Path $tempRoot ('package-test-' + [guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[string]]::new()

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Reason" }
    $checks.Add($Reason)
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $archive = (Resolve-Path -LiteralPath $Archive).ProviderPath
    $expected = (Get-Content -LiteralPath "$archive.sha256" -Raw).Split(' ')[0]
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'ZIP checksum mismatch.' }
    $checks.Add('archive-sha256')
    $extracted = Join-Path $work 'extracted'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $installer = Join-Path $extracted 'Install.ps1'
    & $installer -VerifyOnly
    $checks.Add('payload-sha256')
    $guide = Join-Path $extracted 'desktop/USER-GUIDE.md'
    $original = [IO.File]::ReadAllBytes($guide)
    try {
        [IO.File]::AppendAllText($guide, 'corruption')
        Assert-Rejected { & $installer -VerifyOnly } 'corrupt-payload-rejected'
    } finally { [IO.File]::WriteAllBytes($guide, $original) }
    $extra = Join-Path $extracted 'desktop/unlisted.txt'
    try {
        [IO.File]::WriteAllText($extra, 'unlisted')
        Assert-Rejected { & $installer -VerifyOnly } 'unlisted-payload-rejected'
    } finally { Remove-Item -LiteralPath $extra }
    $manifestPath = Join-Path $extracted 'manifest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest.files[0].path = '../../outside.txt'
        [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
        Assert-Rejected { & $installer -VerifyOnly } 'path-traversal-rejected'
    } finally { [IO.File]::WriteAllBytes($manifestPath, $manifestBytes) }
    Assert-Rejected { & $installer -ApplicationRoot 'C:\LiveLingo\app' } 'c-drive-rejected'
    $app = Join-Path $work 'installed/app'
    $data = Join-Path $work 'installed/data'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $sentinel = Join-Path $data 'settings.json'
    [IO.File]::WriteAllText($sentinel, '{"preserve":true}')
    & $installer -ApplicationRoot $app
    $checks.Add('fresh-install')
    Assert-Rejected { & $installer -ApplicationRoot $app } 'implicit-replacement-rejected'
    & $installer -ApplicationRoot $app -ReplaceExisting
    if ([IO.File]::ReadAllText($sentinel) -ne '{"preserve":true}') { throw 'User data changed.' }
    if (@(Get-ChildItem -LiteralPath $app -Force).Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $app 'desktop/LiveLingo.Desktop.Windows.exe'))) { throw 'Unexpected installation layout.' }
    $checks.Add('replace-single-directory-preserve-data')
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $app ('desktop/' + $file.path)) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Installed hash mismatch: $($file.path)" }
    }
    $checks.Add('installed-files-match')
} finally {
    if (Test-Path -LiteralPath $work) {
        $resolved = (Resolve-Path -LiteralPath $work).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne $tempRoot -or [IO.Path]::GetFileName($resolved) -notmatch '^package-test-[a-f0-9]{32}$') { throw 'Unsafe test cleanup path.' }
        if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked test entry; cleanup stopped.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
[pscustomobject]@{ Passed = $true; Count = $checks.Count; Checks = @($checks); TemporaryFilesRemoved = -not (Test-Path -LiteralPath $work) } | ConvertTo-Json -Depth 3
