param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory) -or $OutputDirectory.StartsWith('C:', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $OutputDirectory)) { throw '需要非 C 盘的新证据目录。' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$settings = Join-Path $OutputDirectory 'settings.json'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
/// <summary>Close only a verified application window owned by this test.</summary>
public static class DesktopCloseFixture {
    /// <summary>Post a normal close request to the owned control window.</summary><param name="window">Owned HWND.</param><param name="message">WM_CLOSE.</param><param name="w">Unused.</param><param name="l">Unused.</param><returns>Success.</returns>
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
}
'@
function Read-Json([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}
function Invoke-DesktopCase([string]$Name, [string]$Operation) {
    $directory = Join-Path $OutputDirectory $Name
    $arguments = @('--verify', ('"'+$directory+'"'), '--settings', ('"'+$settings+'"'), '--seconds', '5')
    if ($Operation -in @('missing','close-loading')) { $arguments += @('--target', "$PID") }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not (Test-Path -LiteralPath (Join-Path $directory 'ready.json')) -and -not $process.HasExited) {
            if ([DateTime]::UtcNow -gt $deadline) { throw '界面未就绪。' }; Start-Sleep -Milliseconds 100
        }
        if ($Operation -eq 'save') { [IO.File]::WriteAllText((Join-Path $directory 'command.txt'), 'save-settings') }
        if ($Operation -eq 'close-loading') {
            Start-Sleep -Milliseconds 1000
            $ready = Read-Json (Join-Path $directory 'ready.json')
            if ($ready.Pid -ne $process.Id) { throw '验证 HWND 所有者不匹配。' }
            [DesktopCloseFixture]::PostMessageW([IntPtr]$ready.Control, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        if (-not $process.WaitForExit(45000)) { throw '关闭未在预算内完成。' }
        $window = if (Test-Path -LiteralPath (Join-Path $directory 'window.json')) { Read-Json (Join-Path $directory 'window.json') } else { $null }
        return @{ ExitCode = $process.ExitCode; Report = Read-Json (Join-Path $directory 'report.json'); Window = $window }
    }
    finally {
        if (-not $process.HasExited) {
            [IO.File]::WriteAllText((Join-Path $directory 'stop.request'), '')
            if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit() }
        }
    }
}
$saved = Invoke-DesktopCase 'saved' 'save'
$original = [IO.File]::ReadAllText($settings)
$restarted = Invoke-DesktopCase 'restarted' 'none'
[IO.File]::WriteAllText($settings, '{broken')
$corrupt = Invoke-DesktopCase 'corrupt' 'none'
$corruptPreserved = [IO.File]::ReadAllText($settings) -eq '{broken'
$invalid = $original | ConvertFrom-Json
$invalid.PythonPath = 'D:\LiveLingo-missing-fixture\python.exe'
[IO.File]::WriteAllText($settings, ($invalid | ConvertTo-Json -Depth 8))
$missing = Invoke-DesktopCase 'missing' 'missing'
[IO.File]::WriteAllText($settings, $original)
$closing = Invoke-DesktopCase 'close-loading' 'close-loading'
$checks = [ordered]@{
    SavedFromUi = $saved.ExitCode -eq 0 -and ($original | ConvertFrom-Json).FontSize -eq 32
    NewProcessLoaded = $restarted.ExitCode -eq 0 -and $restarted.Window.Settings.FontSize -eq 32
    CorruptVisible = $corrupt.ExitCode -eq 0 -and $corrupt.Window.Error.Contains('设置未加载')
    CorruptPreserved = $corruptPreserved
    MissingDependencyVisible = $missing.ExitCode -eq 1 -and $missing.Report.Error.Contains('LiveLingo-missing-fixture')
    MissingDependencyNoWorkers = $missing.Report.LastOwned.Count -eq 0
    CloseDuringLoad = $closing.ExitCode -eq 0 -and $closing.Report.Final.Status -eq 'stopped'
    OwnedProcessActuallyStarted = $closing.Report.LastOwned.Count -gt 0
    CloseOwnedZero = $closing.Report.RemainingOwned.Count -eq 0
    CloseTemporaryZero = $closing.Report.RemainingTemporary.Count -eq 0
}
$result = @{ Passed = @($checks.Values | Where-Object { -not $_ }).Count -eq 0; Checks = $checks }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'settings-report.json') -Encoding utf8NoBOM
$result | ConvertTo-Json -Depth 8
if (-not $result.Passed) { exit 1 }
