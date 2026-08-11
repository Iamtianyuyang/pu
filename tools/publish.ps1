# .NET 10 WPF self-contained single-file publish and packaging.
# Output: publish/pu/pu.exe and publish/pu-windows-x64.zip.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host '== Publishing .NET 10 WPF (win-x64, self-contained, single-file) =='
# PublishTrimmed=false: WPF relies on reflection and cannot be trimmed.
# EnableCompressionInSingleFile: compress bundled assemblies (168 MB -> 74 MB).
dotnet publish src/Pu.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:EnableCompressionInSingleFile=true -o publish/pu

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

# == Vendored ffmpeg for the -full packages (downloaded once into tools/vendor) ==
$vendorDir = Join-Path $root 'tools\vendor\ffmpeg'
$ffExe = Join-Path $vendorDir 'ffmpeg.exe'
$ffProbe = Join-Path $vendorDir 'ffprobe.exe'
if (-not ((Test-Path $ffExe) -and (Test-Path $ffProbe))) {
    Write-Host '== Downloading ffmpeg (gyan.dev release-essentials, one-time) =='
    $ProgressPreference = 'SilentlyContinue'
    $ffZip = Join-Path $root 'tmp\ffmpeg-release-essentials.zip'
    New-Item -ItemType Directory -Force (Split-Path $ffZip) | Out-Null
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $ffZip
    $expand = Join-Path $root 'tmp\ffmpeg-vendor'
    if (Test-Path $expand) { Remove-Item $expand -Recurse -Force }
    Expand-Archive $ffZip -DestinationPath $expand
    $found = Get-ChildItem $expand -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if ($null -eq $found) { throw 'ffmpeg.exe not found in downloaded archive.' }
    New-Item -ItemType Directory -Force $vendorDir | Out-Null
    Copy-Item $found.FullName $ffExe
    Copy-Item (Join-Path $found.DirectoryName 'ffprobe.exe') $ffProbe
    Remove-Item $expand -Recurse -Force
    Remove-Item $ffZip -Force
}

# Full portable zip: pu.exe + manual + ffmpeg/
$stage = Join-Path $root 'tmp\pu-full-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'ffmpeg') | Out-Null
Copy-Item $exe $stage
Copy-Item $manualCopy $stage
Copy-Item $ffExe (Join-Path $stage 'ffmpeg')
Copy-Item $ffProbe (Join-Path $stage 'ffmpeg')
$fullZip = Join-Path $root 'publish\pu-windows-x64-full.zip'
if (Test-Path $fullZip) { Remove-Item $fullZip }
Compress-Archive -Force -Path (Join-Path $stage '*') -DestinationPath $fullZip
Remove-Item $stage -Recurse -Force

Write-Host 'Package ready: publish\pu-windows-x64-full.zip (ffmpeg included)'

# Optional installer: compile tools/setup.iss when Inno Setup (ISCC) is available.
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($null -ne $iscc) {
    Write-Host '== Building installer (Inno Setup) =='
    & $iscc /Q (Join-Path $PSScriptRoot 'setup.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed with exit code $LASTEXITCODE." }
    Write-Host 'Installer ready: publish\pu-setup.exe'
    Write-Host '== Building full installer (ffmpeg included) =='
    & $iscc /Q /DFull (Join-Path $PSScriptRoot 'setup.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile (full) failed with exit code $LASTEXITCODE." }
    Write-Host 'Installer ready: publish\pu-setup-full.exe'
} else {
    Write-Host 'Inno Setup not found; skipping installer (portable zips only).'
}
