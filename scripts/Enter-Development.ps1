<#
.SYNOPSIS
Prepares the current shell for development, preserving machine cache settings by default.
.PARAMETER CacheRoot
Optional absolute cache root. Explicitly redirects caches for this process and its children.
#>
[CmdletBinding()]
param([string]$CacheRoot)

if ($PSBoundParameters.ContainsKey('CacheRoot')) {
    if ([string]::IsNullOrWhiteSpace($CacheRoot) -or -not [IO.Path]::IsPathFullyQualified($CacheRoot)) {
        throw 'CacheRoot must be an absolute path on a writable drive.'
    }
    $developmentCacheRoot = [IO.Path]::GetFullPath($CacheRoot)
    $developmentCaches = @{
        NUGET_PACKAGES = Join-Path $developmentCacheRoot 'nuget/packages'
        NUGET_HTTP_CACHE_PATH = Join-Path $developmentCacheRoot 'nuget/http'
        DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $developmentCacheRoot 'dotnet-bundle'
        TEMP = Join-Path $developmentCacheRoot 'tmp'
        TMP = Join-Path $developmentCacheRoot 'tmp'
    }
    foreach ($directory in $developmentCaches.Values | Select-Object -Unique) {
        New-Item -ItemType Directory -Force -Path $directory -ErrorAction Stop | Out-Null
    }
    foreach ($entry in $developmentCaches.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
