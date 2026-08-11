# NativeAOT 发布 + 打包（方案.md M4）
# 产物：publish/pu/pu.exe（单文件）+ publish/pu-windows-x64.zip（含使用说明）
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host '== NativeAOT 发布（win-x64，可能需要几分钟）=='
dotnet publish src/Pu.App -c Release -r win-x64 -o publish/pu

$exe = Join-Path $root 'publish\pu\pu.exe'
if (-not (Test-Path $exe)) { Write-Host '发布失败：找不到 pu.exe'; exit 1 }

$size = (Get-Item $exe).Length
Write-Host ("pu.exe: {0:N1} MB" -f ($size / 1MB))

$manual = Join-Path $root 'tools\使用说明.txt'
Copy-Item $manual (Join-Path $root 'publish\pu\')
$zip = Join-Path $root 'publish\pu-windows-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Force -Path (Join-Path $root 'publish\pu\pu.exe'), (Join-Path $root 'publish\pu\使用说明.txt') -DestinationPath $zip

Write-Host "打包完成：publish\pu-windows-x64.zip"
Write-Host '发给别人后：解压 → 运行 pu.exe --install → 按提示装 ffmpeg'
