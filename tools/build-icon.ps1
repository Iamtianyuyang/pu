# 从 SVG 生成多尺寸 pu.ico。改完 assets\*.svg 重跑这个脚本即可。
# 依赖：Chrome 或 Edge（当 SVG 光栅化器用），无需 ImageMagick。
$ErrorActionPreference = 'Stop'

$root   = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
$work   = Join-Path ([IO.Path]::GetTempPath()) ("pu-icon-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $work | Out-Null

$browser = @(
  "C:\Program Files\Google\Chrome\Application\chrome.exe",
  "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "找不到 Chrome 或 Edge" }

# 小尺寸用简化版：16px 下高光和第三颗余韵只会变成噪点
$plan = @(
  @{ size = 16;  svg = 'pu-small.svg' }
  @{ size = 20;  svg = 'pu-small.svg' }
  @{ size = 24;  svg = 'pu-small.svg' }
  @{ size = 32;  svg = 'pu-small.svg' }
  @{ size = 40;  svg = 'pu.svg' }
  @{ size = 48;  svg = 'pu.svg' }
  @{ size = 64;  svg = 'pu.svg' }
  @{ size = 128; svg = 'pu.svg' }
  @{ size = 256; svg = 'pu.svg' }
)

$pngs = @()
foreach ($p in $plan) {
  $svg = Get-Content (Join-Path $assets $p.svg) -Raw

  # 内联进 HTML 并撑满视口，这样 --window-size 就精确等于输出像素
  $html = @"
<!doctype html><meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}
svg{display:block;width:$($p.size)px;height:$($p.size)px}</style>
$svg
"@
  $htmlPath = Join-Path $work "r$($p.size).html"
  $pngPath  = Join-Path $work "$($p.size).png"
  [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))

  & $browser --headless --disable-gpu --no-sandbox --hide-scrollbars `
             --force-device-scale-factor=1 --virtual-time-budget=2000 `
             --default-background-color=00000000 `
             --user-data-dir="$work\profile" `
             --window-size=$($p.size),$($p.size) `
             --screenshot="$pngPath" "file:///$($htmlPath -replace '\\','/')" 2>$null | Out-Null

  if (-not (Test-Path $pngPath)) { throw "渲染 $($p.size)px 失败" }
  $pngs += [pscustomobject]@{ Size = $p.size; Bytes = [IO.File]::ReadAllBytes($pngPath) }
  Write-Host ("  {0,3}px  {1,6:N0} bytes" -f $p.size, $pngs[-1].Bytes.Length)
}

# 组装 ICO。Vista 起 ICO 条目可以直接是 PNG 负载，不必转 BMP。
$icoPath = Join-Path $assets 'pu.ico'
$fs = [IO.File]::Create($icoPath)
$bw = [IO.BinaryWriter]::new($fs)

$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: 1 = icon
$bw.Write([uint16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
  $bw.Write([byte]($p.Size -band 0xFF))   # 256 写成 0，正好被 -band 0xFF 处理掉
  $bw.Write([byte]($p.Size -band 0xFF))
  $bw.Write([byte]0)                      # 调色板色数，真彩色填 0
  $bw.Write([byte]0)                      # reserved
  $bw.Write([uint16]1)                    # color planes
  $bw.Write([uint16]32)                   # bits per pixel
  $bw.Write([uint32]$p.Bytes.Length)
  $bw.Write([uint32]$offset)
  $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }

$bw.Close(); $fs.Close()
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("pu.ico  {0:N0} bytes  ({1} 个尺寸)" -f (Get-Item $icoPath).Length, $pngs.Count) -ForegroundColor Green
