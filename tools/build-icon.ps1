# 图标生成：assets/pu~.png → assets/pu.ico（多尺寸 16→256，PNG-in-ICO）
# 依赖：dotnet（用 tmp 下的小工具完成缩放与封装，不依赖 ImageMagick）
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$tool = Join-Path $root 'tmp\iconconv'
if (-not (Test-Path (Join-Path $tool 'Program.cs'))) {
    Write-Host '缺少 tmp/iconconv 转换工具'; exit 1
}

dotnet run --project $tool -- (Join-Path $root 'assets\pu~.png') (Join-Path $root 'assets\pu.ico')
Write-Host '图标已生成：assets/pu.ico（编辑 assets/pu~.png 后重跑本脚本）'
