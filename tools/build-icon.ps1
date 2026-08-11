# 图标生成：原始手绘稿 → 透明裁边 PNG + 多尺寸 ICO
# 全部使用仓库内的 .NET 10 工具，不依赖 ImageMagick / Python。
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet run --project (Join-Path $root 'tools\Pu.IconBuilder') -- `
    (Join-Path $root 'assets\pu~.png') `
    (Join-Path $root 'assets\pu-logo.png') `
    (Join-Path $root 'assets\pu.ico')
