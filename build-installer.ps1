# Builds Windows Installer packages for Windows Patch Manager:
#   - WindowsPatchManager.msi
#   - WindowsPatchManager-Setup.exe  (WiX Burn bootstrapper)
#
# Requires: .NET SDK 8+ (WiX Toolset is pulled via NuGet as WixToolset.Sdk)

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj = Join-Path $Root 'src\ApplicationUpdater\ApplicationUpdater.csproj'
$MsiProj = Join-Path $Root 'installer\wix\ApplicationUpdater.Installer\ApplicationUpdater.Installer.wixproj'
$BundleProj = Join-Path $Root 'installer\wix\ApplicationUpdater.Bundle\ApplicationUpdater.Bundle.wixproj'
$PayloadDir = Join-Path $Root 'artifacts\payload'
$DistDir = Join-Path $Root 'dist'
$IconSrc = Join-Path $Root 'assets\installer.ico'
$AppIcon = Join-Path $Root 'src\ApplicationUpdater\Assets\app.ico'

Write-Host '=== Windows Patch Manager — Windows Installer (WiX / MSI) ===' -ForegroundColor Cyan
Write-Host "Root: $Root"
Write-Host 'Publisher: Skywere Industries'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK (dotnet) is required to build the WiX installer.'
}

if (-not (Test-Path $IconSrc)) {
    throw "Production icon missing: $IconSrc"
}

# Sync icon BEFORE publish so ApplicationIcon is baked into the EXE
New-Item -ItemType Directory -Path (Split-Path $AppIcon) -Force | Out-Null
Copy-Item $IconSrc $AppIcon -Force
Write-Host "Icon synced: assets\installer.ico -> src\...\Assets\app.ico ($((Get-Item $AppIcon).Length) bytes)" -ForegroundColor Green

if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# Clean obj icons / force rebuild of app host with new icon
$objDir = Join-Path $Root 'src\ApplicationUpdater\obj'
if (Test-Path $objDir) {
    Get-ChildItem $objDir -Recurse -Filter 'apphost.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "`n[1/3] Publishing Windows Patch Manager ($Runtime)..." -ForegroundColor Yellow
dotnet publish $AppProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PayloadDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$exe = Join-Path $PayloadDir 'WindowsPatchManager.exe'
if (-not (Test-Path $exe)) { throw "Missing $exe after publish." }

Get-ChildItem $PayloadDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Ship loose icon next to EXE (shortcuts / About fallback)
$iconDstDir = Join-Path $PayloadDir 'Assets'
New-Item -ItemType Directory -Path $iconDstDir -Force | Out-Null
Copy-Item $IconSrc (Join-Path $iconDstDir 'app.ico') -Force
Copy-Item $IconSrc (Join-Path $PayloadDir 'app.ico') -Force
Write-Host "  Icon: $IconSrc"

Write-Host ("  Payload: {0:N1} MB" -f ((Get-ChildItem $PayloadDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

Write-Host "`n[2/3] Building MSI (Windows Installer)..." -ForegroundColor Yellow
# Clean prior MSI outputs so icon table is rebuilt
Get-ChildItem (Join-Path $Root 'installer\wix\ApplicationUpdater.Installer\bin') -Recurse -Filter '*.msi' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

dotnet build $MsiProj -c $Configuration -p:PayloadDir="$PayloadDir" --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$msi = Get-ChildItem (Join-Path $Root 'installer\wix\ApplicationUpdater.Installer') -Recurse -Filter 'WindowsPatchManager.msi' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msi) { throw 'WindowsPatchManager.msi was not produced.' }
Copy-Item $msi.FullName -Destination (Join-Path $DistDir 'WindowsPatchManager.msi') -Force
Write-Host ("  MSI: {0:N1} MB -> dist\WindowsPatchManager.msi" -f ($msi.Length / 1MB))

Write-Host "`n[3/3] Building Setup.exe (WiX Burn bootstrapper)..." -ForegroundColor Yellow
Get-ChildItem (Join-Path $Root 'installer\wix\ApplicationUpdater.Bundle\bin') -Recurse -Filter '*.exe' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

dotnet build $BundleProj -c $Configuration -p:PayloadDir="$PayloadDir" --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Bundle build failed.' }

$setup = Get-ChildItem (Join-Path $Root 'installer\wix\ApplicationUpdater.Bundle') -Recurse -Filter 'WindowsPatchManager-Setup.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $setup) { throw 'WindowsPatchManager-Setup.exe was not produced.' }
Copy-Item $setup.FullName -Destination (Join-Path $DistDir 'WindowsPatchManager-Setup.exe') -Force
Copy-Item $setup.FullName -Destination (Join-Path $Root 'installer\WindowsPatchManager-Setup.exe') -Force
Copy-Item $msi.FullName -Destination (Join-Path $Root 'installer\WindowsPatchManager.msi') -Force

@(
    (Join-Path $DistDir 'ApplicationUpdater.msi'),
    (Join-Path $DistDir 'ApplicationUpdater-Setup.exe'),
    (Join-Path $Root 'installer\ApplicationUpdater.msi'),
    (Join-Path $Root 'installer\ApplicationUpdater-Setup.exe')
) | ForEach-Object { if (Test-Path $_) { Remove-Item $_ -Force } }

Write-Host ''
Write-Host '=== Build succeeded ===' -ForegroundColor Green
Write-Host "  dist\WindowsPatchManager.msi"
Write-Host "  dist\WindowsPatchManager-Setup.exe"
Write-Host "  Icon source: assets\installer.ico"
Write-Host ''
Write-Host 'Install (full wizard with folder Browse):'
Write-Host '  msiexec /i .\dist\WindowsPatchManager.msi'
Write-Host ''
Write-Host 'Install (Setup.exe; Options = install folder):'
Write-Host '  .\dist\WindowsPatchManager-Setup.exe'
Write-Host ''
