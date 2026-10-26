<#
.SYNOPSIS
Checks that development setup preserves machine defaults and supports explicit cache relocation.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$developmentScript = Join-Path $PSScriptRoot 'Enter-Development.ps1'
$repository = Split-Path $PSScriptRoot -Parent
$names = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'TEMP', 'TMP', 'TESTINGPLATFORM_TELEMETRY_OPTOUT')
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$scratchParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$scratch = Join-Path $scratchParent ('development-test-' + [guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[string]]::new()
try {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    . $developmentScript
    foreach ($name in $names | Where-Object { $_ -ne 'TESTINGPLATFORM_TELEMETRY_OPTOUT' }) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process')) { throw "Default setup replaced the machine default: $name" }
    }
    $checks.Add('machine-defaults-preserved')

    foreach ($name in $names | Where-Object { $_ -ne 'TESTINGPLATFORM_TELEMETRY_OPTOUT' }) {
        [Environment]::SetEnvironmentVariable($name, (Join-Path $scratch "existing/$name"), 'Process')
    }
    . $developmentScript
    foreach ($name in $names | Where-Object { $_ -ne 'TESTINGPLATFORM_TELEMETRY_OPTOUT' }) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -ne (Join-Path $scratch "existing/$name")) { throw "Existing environment was overwritten: $name" }
    }
    $checks.Add('explicit-environment-preserved')

    $cacheDirectory = Join-Path $scratch 'cache with spaces'
    . $developmentScript -CacheRoot $cacheDirectory
    $expected = @{
        NUGET_PACKAGES = 'nuget/packages'
        NUGET_HTTP_CACHE_PATH = 'nuget/http'
        DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'dotnet-bundle'
        TEMP = 'tmp'
        TMP = 'tmp'
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $actual = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        if ($actual -ne (Join-Path $cacheDirectory $entry.Value) -or -not (Test-Path -LiteralPath $actual -PathType Container)) { throw "Cache override failed: $($entry.Key)" }
    }
    . $developmentScript
    if ($env:NUGET_PACKAGES -ne (Join-Path $cacheDirectory 'nuget/packages')) { throw 'Nested setup lost the cache override.' }
    $checks.Add('cache-relocation-and-nested-setup')

    $rejected = $false
    try { . $developmentScript -CacheRoot 'relative-cache' } catch { $rejected = $true }
    if (-not $rejected) { throw 'Relative cache roots must be rejected.' }
    if ($env:NUGET_PACKAGES -ne (Join-Path $cacheDirectory 'nuget/packages')) { throw 'Invalid cache root changed the environment.' }
    $checks.Add('invalid-root-leaves-environment-intact')

    [xml]$nuget = Get-Content -LiteralPath (Join-Path $repository 'nuget.config') -Raw
    if ($nuget.SelectSingleNode('/configuration/config/add[@key="globalPackagesFolder"]')) { throw 'Repository overrides the machine package cache.' }
    $sdk = (Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk
    if ($sdk.version -ne '10.0.100' -or $sdk.rollForward -ne 'latestFeature' -or $sdk.allowPrerelease -ne $false) { throw 'Expected stable .NET 10 SDK feature-band compatibility.' }
    $checks.Add('portable-nuget-and-sdk-contract')
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne $scratchParent -or [IO.Path]::GetFileName($resolved) -notmatch '^development-test-[a-f0-9]{32}$') { throw 'Unsafe development test cleanup path.' }
        if (@(Get-Item -LiteralPath $resolved; Get-ChildItem -LiteralPath $resolved -Force -Recurse) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked test entry; cleanup stopped.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
[pscustomobject]@{ Passed = $true; Count = $checks.Count; Checks = @($checks) } | ConvertTo-Json
