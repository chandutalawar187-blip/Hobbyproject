$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\LoqControl"
$outputDir = Join-Path $root "artifacts\release"
$generatedWxs = Join-Path $outputDir "PublishedFiles.generated.wxs"

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

New-Item -ItemType Directory -Force $outputDir | Out-Null

$xml = [System.Xml.XmlWriterSettings]::new()
$xml.Indent = $true
$writer = [System.Xml.XmlWriter]::Create($generatedWxs, $xml)
$writer.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")
$writer.WriteStartElement("Fragment")

$directoryIds = @{
    "" = "INSTALLFOLDER"
    "Assets" = "AssetsFolder"
    "Assets\i" = "AssetImagesFolder"
    "ThirdParty" = "ThirdPartyFolder"
    "ThirdParty\LibreHardwareMonitor" = "LibreHardwareMonitorFolder"
}
$publishFiles = @(Get-ChildItem $publishDir -Recurse -File |
    Where-Object { $_.FullName -ne (Join-Path $publishDir "LoqControl.exe") } |
    ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($publishDir, $_.FullName)
    $relativeDirectory = [System.IO.Path]::GetDirectoryName($relative)
    if ($null -eq $relativeDirectory) { $relativeDirectory = "" }
    [pscustomobject]@{
        File = $_
        Relative = $relative
        Directory = $relativeDirectory
    }
})
$dynamicDirectories = @{}
foreach ($entry in $publishFiles) {
    if ($directoryIds.ContainsKey($entry.Directory)) { continue }
    $directoryName = [System.IO.Path]::GetFileName($entry.Directory)
    $directoryId = "PublishedDirectory" + ($directoryName -replace '[^A-Za-z0-9]', '')
    $directoryIds[$entry.Directory] = $directoryId
    $dynamicDirectories[$entry.Directory] = $directoryName
}
$writer.WriteStartElement("DirectoryRef")
$writer.WriteAttributeString("Id", "INSTALLFOLDER")
foreach ($entry in $dynamicDirectories.GetEnumerator()) {
    $writer.WriteStartElement("Directory")
    $writer.WriteAttributeString("Id", $directoryIds[$entry.Key])
    $writer.WriteAttributeString("Name", $entry.Value)
    $writer.WriteEndElement()
}
$writer.WriteEndElement()
$writer.WriteStartElement("ComponentGroup")
$writer.WriteAttributeString("Id", "PublishedFiles")
$componentIndex = 0
foreach ($entry in $publishFiles) {
    $relative = $entry.Relative
    $relativeDirectory = $entry.Directory
    $directoryId = $directoryIds[$relativeDirectory]
    $componentIndex++
    $writer.WriteStartElement("Component")
    $writer.WriteAttributeString("Id", "PublishedFile$componentIndex")
    $writer.WriteAttributeString("Guid", "*")
    $writer.WriteAttributeString("Directory", $directoryId)
    $writer.WriteStartElement("File")
    $writer.WriteAttributeString("Id", "PublishedFileEntry$componentIndex")
    $writer.WriteAttributeString("Source", (Join-Path '$(var.PublishDir)' $relative))
    $writer.WriteAttributeString("KeyPath", "yes")
    $writer.WriteEndElement()
    $writer.WriteEndElement()
}
$writer.WriteEndElement()
$writer.WriteEndElement()
$writer.WriteEndDocument()
$writer.Dispose()

wix build (Join-Path $PSScriptRoot "LoqControl.wxs") `
    $generatedWxs `
    -arch x64 `
    -d PublishDir=$publishDir `
    -o (Join-Path $outputDir "LOQ-Control-1.2.0-x64.msi")

if ($LASTEXITCODE -ne 0) {
    throw "WiX failed with exit code $LASTEXITCODE."
}

Remove-Item $generatedWxs -Force
Write-Host "Created $(Join-Path $outputDir 'LOQ-Control-1.2.0-x64.msi')"
