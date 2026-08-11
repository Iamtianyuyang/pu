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
} else {
    Write-Host 'Inno Setup not found; skipping installer (portable zip only).'
}
