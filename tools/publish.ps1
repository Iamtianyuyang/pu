# .NET 10 WPF self-contained single-file publish and packaging.
# Output: publish/pu/pu.exe and publish/pu-windows-x64.zip.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host '== Publishing .NET 10 WPF (win-x64, self-contained, single-file) =='
dotnet publish src/Pu.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/pu

$exe = Join-Path $root 'publish\pu\pu.exe'
if (-not (Test-Path $exe)) { throw 'Publish failed: pu.exe was not produced.' }

$size = (Get-Item $exe).Length
Write-Host ("pu.exe: {0:N1} MB" -f ($size / 1MB))

$manual = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.txt' -File | Select-Object -First 1
if ($null -eq $manual) { throw 'Package manual was not found under tools/.' }
$manualCopy = Join-Path $root (Join-Path 'publish\pu' $manual.Name)
Copy-Item -LiteralPath $manual.FullName -Destination $manualCopy -Force

$zip = Join-Path $root 'publish\pu-windows-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Force -Path $exe, $manualCopy -DestinationPath $zip

Write-Host 'Package ready: publish\pu-windows-x64.zip'
