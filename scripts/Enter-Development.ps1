# Set process-local caches; dot-source in the shell used for builds and tools.
$env:NUGET_PACKAGES = 'D:\Soft Ware\LiveLingo\cache\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = 'D:\Soft Ware\LiveLingo\cache\nuget\http'
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'D:\Soft Ware\LiveLingo\cache\dotnet-bundle'
$env:TEMP = 'D:\Soft Ware\LiveLingo\cache\tmp'
$env:TMP = $env:TEMP
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR, $env:TEMP | Out-Null
