$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\LoqControl"
$outputDir = Join-Path $root "artifacts\release"

if (-not (Test-Path (Join-Path $publishDir "LoqControl.exe"))) {
    throw "Publish output is missing. Run dotnet publish first."
}

New-Item -ItemType Directory -Force $outputDir | Out-Null
wix build (Join-Path $PSScriptRoot "LoqControl.wxs") `
    -arch x64 `
    -d PublishDir=$publishDir `
    -o (Join-Path $outputDir "LOQ-Control-1.0.1-x64.msi")

if ($LASTEXITCODE -ne 0) {
    throw "WiX failed with exit code $LASTEXITCODE."
}

Write-Host "Created $(Join-Path $outputDir 'LOQ-Control-1.0.1-x64.msi')"
