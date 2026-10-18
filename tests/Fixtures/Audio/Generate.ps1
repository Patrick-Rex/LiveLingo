param([string]$Output = 'D:\Soft Ware\LiveLingo\data\fixtures\m1-tones-v1', [switch]$IncludeSecondTab)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Output) -or $Output.StartsWith('C:', [StringComparison]::OrdinalIgnoreCase)) { throw '夹具必须为非 C 盘绝对目录。' }
if (Test-Path -LiteralPath $Output) { throw '为保留指纹，拒绝覆盖已有夹具目录。' }
New-Item -ItemType Directory -Path $Output | Out-Null
$fixtures = @()
$tones = @(@{Name='target'; Frequency=440}, @{Name='independent'; Frequency=997})
if ($IncludeSecondTab) { $tones += @{Name='second'; Frequency=660} }
foreach ($tone in $tones) {
    $wavePath = Join-Path $Output ($tone.Name + '.wav')
    & ffmpeg -hide_banner -loglevel error -n -f lavfi -i "sine=frequency=$($tone.Frequency):sample_rate=44100:duration=10" -af volume=0.2 -ac 2 -c:a pcm_s16le $wavePath
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg 失败: $LASTEXITCODE" }
    $fixtures += [ordered]@{ file=($tone.Name + '.wav'); frequencyHz=$tone.Frequency; sampleRate=44100; channels=2; bits=16; seconds=10; volumeFilter='0.2'; sha256=(Get-FileHash -LiteralPath $wavePath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$page = '<!doctype html><html lang="en"><meta charset="utf-8"><title>LiveLingo controlled 440 Hz</title><audio src="target.wav" autoplay loop controls></audio></html>'
[IO.File]::WriteAllText((Join-Path $Output 'target.html'), $page + "`n", [Text.UTF8Encoding]::new($false))
if ($IncludeSecondTab) {
    [IO.File]::WriteAllText((Join-Path $Output 'second.html'), $page.Replace('440 Hz','660 Hz').Replace('target.wav','second.wav') + "`n", [Text.UTF8Encoding]::new($false))
}
$manifest = [ordered]@{ schemaVersion=1; kind='generated-tone-not-speech'; generator=((& ffmpeg -version | Select-Object -First 1)); fixtures=$fixtures }
[IO.File]::WriteAllText((Join-Path $Output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n","`n") + "`n", [Text.UTF8Encoding]::new($false))
$manifest | ConvertTo-Json -Depth 5
