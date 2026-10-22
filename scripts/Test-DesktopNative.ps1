param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-ModelEvaluation.ps1')
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory) -or $OutputDirectory.StartsWith('C:', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $OutputDirectory)) { throw '需要非 C 盘的新证据目录。' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$fixtureSettings = $OutputDirectory + '.settings.json'
if (Test-Path -LiteralPath $fixtureSettings) { throw '测试设置文件必须是新的独占路径。' }
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
/// <summary>Only used by the explicit native window verification harness.</summary>
public static class DesktopInputFixture {
    /// <summary>Show our receiver after hidden process startup.</summary><param name="window">Owned receiver.</param><param name="command">Show command.</param><returns>Previous visibility.</returns>
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    /// <summary>Check which actual window receives hit testing at our fixture point.</summary><param name="point">Packed signed coordinates.</param><returns>Hit window.</returns>
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long point);
    /// <summary>Read the process owning a fixture hit window.</summary><param name="window">Hit window.</param><param name="pid">Owning PID.</param><returns>Thread ID.</returns>
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    /// <summary>Read foreground identity.</summary><returns>Foreground HWND.</returns>
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    /// <summary>Activate our independently launched receiver.</summary><param name="window">Owned receiver.</param><returns>Success.</returns>
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    /// <summary>Position pointer over verified receiver bounds.</summary><param name="x">Pixel x.</param><param name="y">Pixel y.</param><returns>Success.</returns>
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    /// <summary>Send a mouse event at the verified receiver.</summary><param name="flags">Flags.</param><param name="x">Unused.</param><param name="y">Unused.</param><param name="data">Unused.</param><param name="extra">Unused.</param>
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    /// <summary>Send fixture hotkey input.</summary><param name="key">Virtual key.</param><param name="scan">Scan.</param><param name="flags">Up/down.</param><param name="extra">Unused.</param>
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    /// <summary>Probe actual global registration conflict.</summary><param name="window">Null thread window.</param><param name="id">Fixture ID.</param><param name="modifiers">Modifiers.</param><param name="key">Virtual key.</param><returns>Success.</returns>
    [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    /// <summary>Release fixture hotkey only.</summary><param name="window">Thread window.</param><param name="id">Fixture ID.</param><returns>Success.</returns>
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
}
'@
function Read-Json([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}
function Read-Window { Read-Json (Join-Path $OutputDirectory 'window.json') }
function Send-FixtureHotkey([byte]$Key) {
    if ([DesktopInputFixture]::GetForegroundWindow().ToInt64() -notin $script:ownedWindows) { throw '前台不是自有测试窗口，拒绝发送按键。' }
    [DesktopInputFixture]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [DesktopInputFixture]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    try { [DesktopInputFixture]::keybd_event($Key, 0, 0, [UIntPtr]::Zero); [DesktopInputFixture]::keybd_event($Key, 0, 2, [UIntPtr]::Zero) }
    finally { [DesktopInputFixture]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [DesktopInputFixture]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 500
}
$app = $null; $receiver = $null; $failure = $null; $checks = [ordered]@{}
try {
    $app = Start-Process -FilePath $Executable -ArgumentList @('--verify', ('"' + $OutputDirectory + '"'), '--settings', ('"' + $fixtureSettings + '"'), '--seconds', '70') -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'ready.json'))) { if ($app.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw '桌面未就绪。' }; Start-Sleep -Milliseconds 200 }
    $fixture = Join-Path $PSScriptRoot '../tests/Fixtures/Windows/OverlayReceiver.ps1'
    $receiver = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-STA','-File',('"'+$fixture+'"'),'-Directory',('"'+$OutputDirectory+'"')) -PassThru -WindowStyle Hidden
    while (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'receiver.json'))) { if ($receiver.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw '接收器未就绪。' }; Start-Sleep -Milliseconds 200 }
    $receiverState = Read-Json (Join-Path $OutputDirectory 'receiver.json')
    $readyState = Read-Json (Join-Path $OutputDirectory 'ready.json')
    $script:receiverHandle = [long]$receiverState.Handle
    $script:ownedWindows = @($script:receiverHandle, [long]$readyState.Control, [long]$readyState.Overlay, [long]$readyState.LayoutLock)
    [DesktopInputFixture]::ShowWindow([IntPtr]$script:receiverHandle, 5) | Out-Null
    [DesktopInputFixture]::SetForegroundWindow([IntPtr]$script:receiverHandle) | Out-Null
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'command.txt'), 'position:120,250,950,230')
    Start-Sleep -Milliseconds 900
    $before = Read-Window
    $checks.Transparent = $before.Transparency -eq 'Transparent'
    $checks.RequiredStyles = ($before.Styles -band 0x08080020) -eq 0x08080020
    if ([DesktopInputFixture]::GetForegroundWindow().ToInt64() -ne $script:receiverHandle) { throw '未能激活自有接收器。' }
    [uint32]$hitPid = 0
    $hit = [DesktopInputFixture]::WindowFromPoint(([long]350 -shl 32) -bor 500)
    [DesktopInputFixture]::GetWindowThreadProcessId($hit, [ref]$hitPid) | Out-Null
    $checks.HitReceiverProcess = $hitPid -eq $receiver.Id
    $bitmap = New-Object System.Drawing.Bitmap(1000, 400)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen(100, 180, 0, 0, $bitmap.Size); $bitmap.Save((Join-Path $OutputDirectory 'native-composition.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    if (-not $checks.HitReceiverProcess) { throw "点击位置的实际进程不是接收器：PID=$hitPid HWND=$hit" }
    [DesktopInputFixture]::SetCursorPos(500, 350) | Out-Null
    [DesktopInputFixture]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [DesktopInputFixture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    $afterClick = Read-Json (Join-Path $OutputDirectory 'receiver.json')
    $checks.CrossProcessClick = $afterClick.Clicks -eq 1
    $checks.ClickFocusPreserved = [DesktopInputFixture]::GetForegroundWindow().ToInt64() -eq $script:receiverHandle
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'command.txt'), 'update:1'); Start-Sleep -Milliseconds 400
    $checks.UpdateFocusPreserved = [DesktopInputFixture]::GetForegroundWindow().ToInt64() -eq $script:receiverHandle
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'command.txt'), 'reshow:1'); Start-Sleep -Milliseconds 500
    $checks.ReshowFocusPreserved = [DesktopInputFixture]::GetForegroundWindow().ToInt64() -eq $script:receiverHandle
    $locked = Read-Window
    $checks.LockButtonStyles = ($locked.LayoutLock.Styles -band 0x08080020) -eq 0x08080000
    foreach ($expectedLayout in @($true, $false)) {
        $buttonState = (Read-Window).LayoutLock
        $buttonX = [int]($buttonState.Position.X + $buttonState.Width * $buttonState.RenderScaling / 2)
        $buttonY = [int]($buttonState.Position.Y + $buttonState.Height * $buttonState.RenderScaling / 2)
        $buttonHit = [DesktopInputFixture]::WindowFromPoint(([long]$buttonY -shl 32) -bor [uint32]$buttonX)
        if ($buttonHit.ToInt64() -ne [long]$readyState.LayoutLock) { throw '字幕锁定按钮不在预期命中位置，拒绝点击。' }
        [DesktopInputFixture]::SetCursorPos($buttonX, $buttonY) | Out-Null
        [DesktopInputFixture]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [DesktopInputFixture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        $buttonResult = Read-Window
        $checks["LockButtonLayout$expectedLayout"] = $buttonResult.LayoutMode -eq $expectedLayout
        $checks["LockButtonFocus$expectedLayout"] = [DesktopInputFixture]::GetForegroundWindow().ToInt64() -eq $script:receiverHandle
    }
    $checks.BodyPassthroughAfterButtonLock = ((Read-Window).Styles -band 0x08080020) -eq 0x08080020
    Send-FixtureHotkey 0x77; $checks.HideHotkey = -not (Read-Window).OverlayVisible
    Send-FixtureHotkey 0x77; $checks.ShowHotkey = (Read-Window).OverlayVisible
    Send-FixtureHotkey 0x78; $layout = Read-Window; $checks.LayoutHotkey = $layout.LayoutMode -and (($layout.Styles -band 0x08000020) -eq 0)
    [DesktopInputFixture]::SetCursorPos(400, 265) | Out-Null
    [DesktopInputFixture]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150
    [DesktopInputFixture]::SetCursorPos(520, 325) | Out-Null; Start-Sleep -Milliseconds 150
    [DesktopInputFixture]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 400
    $moved = Read-Window
    $checks.LayoutDrag = $moved.Position.X -ne $layout.Position.X -and $moved.Position.Y -ne $layout.Position.Y
    foreach ($edge in @('SouthEast', 'South', 'East', 'NorthWest', 'North', 'West', 'NorthEast', 'SouthWest')) {
        $sizing = Read-Window
        $west = $edge.Contains('West'); $east = $edge.Contains('East')
        $north = $edge.Contains('North'); $south = $edge.Contains('South')
        $localX = if ($west) { 4 } elseif ($east) { $sizing.Width - 4 } else { $sizing.Width / 2 }
        $localY = if ($north) { 4 } elseif ($south) { $sizing.Height - 4 } else { $sizing.Height / 2 }
        $edgeX = [int]($sizing.Position.X + $localX * $sizing.RenderScaling)
        $edgeY = [int]($sizing.Position.Y + $localY * $sizing.RenderScaling)
        [uint32]$hitPid = 0
        [DesktopInputFixture]::GetWindowThreadProcessId([DesktopInputFixture]::WindowFromPoint(([long]$edgeY -shl 32) -bor ([long]$edgeX -band 0xffffffffL)), [ref]$hitPid) | Out-Null
        if ($hitPid -ne $app.Id) { throw "缩放边缘不属于自有字幕窗口：$edge" }
        $deltaX = if ($west) { -20 } elseif ($east) { 20 } else { 0 }
        $deltaY = if ($north) { -20 } elseif ($south) { 20 } else { 0 }
        [DesktopInputFixture]::SetCursorPos($edgeX, $edgeY) | Out-Null
        [DesktopInputFixture]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150
        try { [DesktopInputFixture]::SetCursorPos(($edgeX + $deltaX), ($edgeY + $deltaY)) | Out-Null; Start-Sleep -Milliseconds 150 }
        finally { [DesktopInputFixture]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 400
        $resized = Read-Window
        $checks["Resize$edge"] = (($deltaX -eq 0 -or $resized.Width -gt $sizing.Width) -and ($deltaY -eq 0 -or $resized.Height -gt $sizing.Height))
    }
    $zoomBefore = Read-Window
    $zoomX = [int]($zoomBefore.Position.X + 100 * $zoomBefore.RenderScaling)
    $zoomY = [int]($zoomBefore.Position.Y + 100 * $zoomBefore.RenderScaling)
    [uint32]$hitPid = 0
    [DesktopInputFixture]::GetWindowThreadProcessId([DesktopInputFixture]::WindowFromPoint(([long]$zoomY -shl 32) -bor ([long]$zoomX -band 0xffffffffL)), [ref]$hitPid) | Out-Null
    if ($hitPid -ne $app.Id -or [DesktopInputFixture]::GetForegroundWindow().ToInt64() -notin $script:ownedWindows) { throw '缩放文字输入目标不是自有窗口。' }
    [DesktopInputFixture]::SetCursorPos($zoomX, $zoomY) | Out-Null
    [DesktopInputFixture]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    try { [DesktopInputFixture]::mouse_event(0x0800,0,0,120,[UIntPtr]::Zero) }
    finally { [DesktopInputFixture]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 500
    $zoomAfter = Read-Window
    $checks.CtrlWheelZoom = $zoomAfter.Settings.FontSize -eq ($zoomBefore.Settings.FontSize + 2)
    $checks.ZoomPreservesBounds = $zoomAfter.Width -eq $zoomBefore.Width -and $zoomAfter.Height -eq $zoomBefore.Height -and $zoomAfter.Position.X -eq $zoomBefore.Position.X -and $zoomAfter.Position.Y -eq $zoomBefore.Position.Y
    [DesktopInputFixture]::SetForegroundWindow([IntPtr]$script:receiverHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    Send-FixtureHotkey 0x78; $checks.RestoreClickThrough = -not (Read-Window).LayoutMode
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'receiver-fullscreen.request'), '')
    Start-Sleep -Milliseconds 600
    [DesktopInputFixture]::SetForegroundWindow([IntPtr]$script:receiverHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [uint32]$hitPid = 0
    [DesktopInputFixture]::GetWindowThreadProcessId([DesktopInputFixture]::WindowFromPoint(([long]350 -shl 32) -bor 500), [ref]$hitPid) | Out-Null
    if ($hitPid -ne $receiver.Id) { throw '无边框全屏测试点击位置不属于自有接收器。' }
    [DesktopInputFixture]::SetCursorPos(500,350) | Out-Null
    [DesktopInputFixture]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [DesktopInputFixture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    $checks.BorderlessFullscreenClick = (Read-Json (Join-Path $OutputDirectory 'receiver.json')).Clicks -eq 2
    $checks.BorderlessFullscreenFocus = [DesktopInputFixture]::GetForegroundWindow().ToInt64() -eq $script:receiverHandle
    $registered = [DesktopInputFixture]::RegisterHotKey([IntPtr]::Zero, 6789, 0x4003, 0x77)
    $checks.ConflictDetected = -not $registered
    if ($registered) { [DesktopInputFixture]::UnregisterHotKey([IntPtr]::Zero, 6789) | Out-Null }
}
catch { $failure = $_.Exception.ToString() }
finally {
    if (Test-Path -LiteralPath $OutputDirectory) {
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'stop.request'), '')
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'receiver-stop.request'), '')
    }
    foreach ($process in @($app, $receiver)) { if ($null -ne $process -and -not $process.WaitForExit(20000)) { $failure = "$failure; 自有测试进程未正常退出 PID=$($process.Id)"; $process.Kill(); $process.WaitForExit() } }
}
$registeredAfter = [DesktopInputFixture]::RegisterHotKey([IntPtr]::Zero, 6789, 0x4003, 0x77)
$checks.HotkeyReleased = $registeredAfter
if ($registeredAfter) { [DesktopInputFixture]::UnregisterHotKey([IntPtr]::Zero, 6789) | Out-Null }
$result = @{ Passed = ($null -eq $failure -and $app.ExitCode -eq 0 -and $receiver.ExitCode -eq 0 -and @($checks.Values | Where-Object { -not $_ }).Count -eq 0); Error = $failure; Checks = $checks; AppExit = $app.ExitCode; ReceiverExit = $receiver.ExitCode }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-report.json') -Encoding utf8NoBOM
$result | ConvertTo-Json -Depth 8
if (-not $result.Passed) { exit 1 }
