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

New-Item -ItemType Directory -Force $outputDir | Out-Null
wix build (Join-Path $PSScriptRoot "LoqControl.wxs") `
    -arch x64 `
    -d PublishDir=$publishDir `
    -o (Join-Path $outputDir "LOQ-Control-1.1.2-x64.msi")

if ($LASTEXITCODE -ne 0) {
    throw "WiX failed with exit code $LASTEXITCODE."
}

Write-Host "Created $(Join-Path $outputDir 'LOQ-Control-1.1.2-x64.msi')"
