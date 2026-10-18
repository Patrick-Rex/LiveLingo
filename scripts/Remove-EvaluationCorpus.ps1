param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$allowedParent = [IO.Path]::GetFullPath('D:\Soft Ware\LiveLingo\data\evaluation\corpora')
$target = (Get-Item -LiteralPath $Directory -Force).FullName
if ((Split-Path $target -Parent) -ne $allowedParent) { throw 'Only dedicated evaluation corpus directories are allowed.' }
if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked corpus directories are not allowed.' }
$manifestPath = Join-Path $target 'assets.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.kind -ne 'disposable-evaluation-corpus' -or $manifest.schemaVersion -ne 1 -or $manifest.root -ne $target) { throw 'Corpus ownership manifest mismatch.' }
$files = @()
foreach ($entry in $manifest.files) {
    if ([IO.Path]::GetFileName($entry.path) -ne $entry.path -or $entry.path -eq 'assets.json') { throw 'Unsafe manifest entry.' }
    $file = Join-Path $target $entry.path
    if (Test-Path -LiteralPath $file) {
        $item = Get-Item -LiteralPath $file -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected an ordinary owned file.' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Changed file preserved: $file" }
        $files += $file
    }
}
# Validate the entire inventory before deleting any item. No recursive deletion.
$known = @($files) + @($manifestPath)
$unknown = @(Get-ChildItem -LiteralPath $target -Force | Where-Object { $_.FullName -notin $known })
if ($unknown.Count -gt 0) { throw 'Unregistered files preserved; add verified derived assets to the manifest before cleanup.' }
foreach ($file in $files) { Remove-Item -LiteralPath $file -Force }
Remove-Item -LiteralPath $manifestPath -Force
Remove-Item -LiteralPath $target -Force
Write-Output "Deleted only the registered evaluation corpus: $target"
