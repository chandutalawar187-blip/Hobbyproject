$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\LoqControl"
$outputDir = Join-Path $root "artifacts\release"

if (-not (Test-Path (Join-Path $publishDir "LoqControl.exe"))) {
    throw "Publish output is missing. Run dotnet publish first."
}

$sourceAnimation = Join-Path $root "src\LenovoLoqControl\Assets\Laptop_Control_Center.lottie"
$publishAssets = Join-Path $publishDir "Assets"
if (-not (Test-Path $sourceAnimation)) {
    throw "Dashboard animation is missing: $sourceAnimation"
}
New-Item -ItemType Directory -Force $publishAssets | Out-Null
Copy-Item $sourceAnimation (Join-Path $publishAssets "Laptop_Control_Center.lottie") -Force
Copy-Item (Join-Path $root "src\LenovoLoqControl\Assets\app_icon_black.ico") (Join-Path $publishDir "app_icon.ico") -Force
$smartmontoolsDir = Join-Path $publishDir "smartmontools"
New-Item -ItemType Directory -Force $smartmontoolsDir | Out-Null
Copy-Item (Join-Path $root "src\LenovoLoqControl\ThirdParty\smartmontools\smartctl.exe") $smartmontoolsDir -Force
Copy-Item (Join-Path $root "src\LenovoLoqControl\ThirdParty\smartmontools\drivedb.h") $smartmontoolsDir -Force
Copy-Item (Join-Path $root "src\LenovoLoqControl\ThirdParty\smartmontools\COPYING.txt") $smartmontoolsDir -Force
Copy-Item (Join-Path $root "src\LenovoLoqControl\ThirdParty\smartmontools\SOURCE-NOTICE.md") $smartmontoolsDir -Force

New-Item -ItemType Directory -Force $outputDir | Out-Null
wix build (Join-Path $PSScriptRoot "LoqControl.wxs") `
    -arch x64 `
    -d PublishDir=$publishDir `
    -o (Join-Path $outputDir "LOQ-Control-1.1.7-x64.msi")

if ($LASTEXITCODE -ne 0) {
    throw "WiX failed with exit code $LASTEXITCODE."
}

Write-Host "Created $(Join-Path $outputDir 'LOQ-Control-1.1.7-x64.msi')"
