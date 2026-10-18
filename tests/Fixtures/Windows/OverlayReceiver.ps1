param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = 'LiveLingo independent input receiver'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(80, 100)
$form.ClientSize = New-Object System.Drawing.Size(1100, 500)
$button = New-Object System.Windows.Forms.Button
$button.Dock = 'Fill'
$button.BackColor = [System.Drawing.Color]::FromArgb(25, 95, 160)
$button.ForeColor = [System.Drawing.Color]::White
$button.Font = New-Object System.Drawing.Font('Segoe UI', 22)
$button.Text = 'Independent process: waiting for click through overlay'
$form.Controls.Add($button)
$script:clicks = 0
$script:started = [DateTime]::UtcNow
function Save-State {
    $state = @{ Pid = $PID; Handle = $form.Handle.ToInt64(); Clicks = $script:clicks; Utc = [DateTime]::UtcNow.ToString('o') }
    $path = Join-Path $Directory 'receiver.json'
    [IO.File]::WriteAllText($path + '.tmp', ($state | ConvertTo-Json))
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
$button.Add_Click({ $script:clicks++; $button.Text = "Received $script:clicks click(s) through overlay"; Save-State })
$form.Add_Shown({ Save-State })
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    if ((Test-Path -LiteralPath (Join-Path $Directory 'receiver-fullscreen.request')) -and $form.FormBorderStyle -ne 'None') { $form.FormBorderStyle = 'None'; $form.WindowState = 'Maximized' }
    if ((Test-Path -LiteralPath (Join-Path $Directory 'receiver-stop.request')) -or ([DateTime]::UtcNow - $script:started).TotalSeconds -gt 90) { $form.Close() }
})
$timer.Start()
try { [System.Windows.Forms.Application]::Run($form) }
finally { $timer.Stop(); $timer.Dispose(); $form.Dispose() }
