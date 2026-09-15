$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\LoqControl"
$servicePublishDir = Join-Path $root "artifacts\LoqControlService"
$serviceBuildDir = Join-Path $root "artifacts\service-build"
$appBuildDir = Join-Path $root "artifacts\app-build"
$outputDir = Join-Path $root "artifacts\release"
$releaseVersion = "1.2.4"
$generatedWxs = Join-Path $outputDir "PublishedFiles.generated.wxs"
$generatedServiceWxs = Join-Path $outputDir "HardwareServiceFiles.generated.wxs"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force $publishDir | Out-Null

dotnet publish (Join-Path $root "src\LenovoLoqControl\LenovoLoqControl.csproj") `
    -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:BaseOutputPath=$appBuildDir -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Desktop publish failed with exit code $LASTEXITCODE."
}

if (Test-Path $servicePublishDir) {
    Remove-Item $servicePublishDir -Recurse -Force
}
New-Item -ItemType Directory -Force $servicePublishDir | Out-Null

dotnet publish (Join-Path $root "src\LenovoLoqControlService\LenovoLoqControlService.csproj") `
    -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:BaseOutputPath=$serviceBuildDir -o $servicePublishDir
if ($LASTEXITCODE -ne 0) {
    throw "Service publish failed with exit code $LASTEXITCODE."
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

$serviceWriter = [System.Xml.XmlWriter]::Create($generatedServiceWxs, $xml)
$serviceWriter.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")
$serviceWriter.WriteStartElement("Fragment")
$serviceWriter.WriteStartElement("DirectoryRef")
$serviceWriter.WriteAttributeString("Id", "HardwareServiceFolder")
$serviceFiles = @(Get-ChildItem $servicePublishDir -Recurse -File |
    Where-Object { $_.Name -ne "LenovoLoqControlService.exe" } |
    ForEach-Object {
        [pscustomobject]@{
            File = $_
            Relative = [System.IO.Path]::GetRelativePath($servicePublishDir, $_.FullName)
        }
    })
$serviceDirectoryIds = @{}
foreach ($entry in $serviceFiles) {
    $directory = [System.IO.Path]::GetDirectoryName($entry.Relative)
    if ([string]::IsNullOrEmpty($directory) -or $serviceDirectoryIds.ContainsKey($directory)) { continue }
    $directoryId = "ServicePublishedDirectory" + ($directory -replace '[^A-Za-z0-9]', '')
    $serviceDirectoryIds[$directory] = $directoryId
    $serviceWriter.WriteStartElement("Directory")
    $serviceWriter.WriteAttributeString("Id", $directoryId)
    $serviceWriter.WriteAttributeString("Name", [System.IO.Path]::GetFileName($directory))
    $serviceWriter.WriteEndElement()
}
$serviceWriter.WriteEndElement()
$serviceWriter.WriteStartElement("ComponentGroup")
$serviceWriter.WriteAttributeString("Id", "HardwareServiceFiles")
$serviceIndex = 0
foreach ($entry in $serviceFiles) {
    $serviceIndex++
    $directory = [System.IO.Path]::GetDirectoryName($entry.Relative)
    $directoryId = if ([string]::IsNullOrEmpty($directory)) { "HardwareServiceFolder" } else { $serviceDirectoryIds[$directory] }
    $serviceWriter.WriteStartElement("Component")
    $serviceWriter.WriteAttributeString("Id", "ServicePublishedFile$serviceIndex")
    $serviceWriter.WriteAttributeString("Guid", "*")
    $serviceWriter.WriteAttributeString("Directory", $directoryId)
    $serviceWriter.WriteStartElement("File")
    $serviceWriter.WriteAttributeString("Id", "ServicePublishedFileEntry$serviceIndex")
    $serviceWriter.WriteAttributeString("Source", (Join-Path '$(var.ServicePublishDir)' $entry.Relative))
    $serviceWriter.WriteAttributeString("KeyPath", "yes")
    $serviceWriter.WriteEndElement()
    $serviceWriter.WriteEndElement()
}
$serviceWriter.WriteEndElement()
$serviceWriter.WriteEndElement()
$serviceWriter.WriteEndDocument()
$serviceWriter.Dispose()

wix build (Join-Path $PSScriptRoot "LoqControl.wxs") `
    $generatedWxs `
    $generatedServiceWxs `
    -arch x64 `
    -d PublishDir=$publishDir `
    -d ServicePublishDir=$servicePublishDir `
    -d SourceDir=$PSScriptRoot `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -o (Join-Path $outputDir "LOQ-Control-$releaseVersion-x64.msi")

if ($LASTEXITCODE -ne 0) {
    throw "WiX failed with exit code $LASTEXITCODE."
}

Remove-Item $generatedWxs -Force
Remove-Item $generatedServiceWxs -Force
$msiPath = Join-Path $outputDir "LOQ-Control-$releaseVersion-x64.msi"
Write-Host "Created $msiPath"
