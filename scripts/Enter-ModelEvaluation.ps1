# Process-local model caches; never change the user's persistent environment.
. (Join-Path $PSScriptRoot 'Enter-Development.ps1')
$modelCacheRoot = 'D:\Soft Ware\LiveLingo\cache'
$env:UV_CACHE_DIR = Join-Path $modelCacheRoot 'uv'
$env:PIP_CACHE_DIR = Join-Path $modelCacheRoot 'pip'
$env:HF_HOME = Join-Path $modelCacheRoot 'huggingface'
$env:HF_HUB_CACHE = Join-Path $env:HF_HOME 'hub'
$env:TORCH_HOME = Join-Path $modelCacheRoot 'torch'
$env:TORCHINDUCTOR_CACHE_DIR = Join-Path $modelCacheRoot 'torchinductor'
$env:CUDA_CACHE_PATH = Join-Path $modelCacheRoot 'cuda'
$env:PYTHONPYCACHEPREFIX = Join-Path $modelCacheRoot 'pycache'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
New-Item -ItemType Directory -Force -Path $env:UV_CACHE_DIR, $env:PIP_CACHE_DIR, $env:HF_HUB_CACHE, $env:TORCH_HOME, $env:TORCHINDUCTOR_CACHE_DIR, $env:CUDA_CACHE_PATH, $env:PYTHONPYCACHEPREFIX | Out-Null
