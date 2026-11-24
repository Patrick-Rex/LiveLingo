function Resolve-MakeAppx {
    <#
    .SYNOPSIS
    Resolves an explicit MakeAppx executable or the newest installed Windows SDK x64 tool.
    .PARAMETER Path
    Optional executable path; no tools are downloaded or installed.
    .OUTPUTS
    The full path of an existing MakeAppx executable.
    #>
    param([string]$Path)
    if ($Path) { return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $tool = Get-ChildItem -LiteralPath $sdk -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.0\.\d+\.0$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $tool) { throw 'Windows SDK MakeAppx.exe is required. Install the SDK or specify -MakeAppxPath.' }
    return $tool
}

function Remove-MsixScratch {
    <#
    .SYNOPSIS
    Removes only a unique staging directory directly inside the expected workspace root, refusing links.
    .PARAMETER Path
    The temporary directory created by the calling script.
    .PARAMETER Root
    Its expected parent directory under the repository artifacts directory.
    #>
    param([string]$Path, [string]$Root)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($Root)) { throw 'Unsafe MSIX staging cleanup path.' }
    $ancestor = $resolved
    while ($ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked MSIX staging path; cleanup stopped.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked MSIX staging entry; cleanup stopped.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
