# Run from an elevated PowerShell prompt after publishing the x64 service.
$serviceName = "LoqControlHardwareService"
$binaryPath = Join-Path $PSScriptRoot "..\src\LenovoLoqControlService\bin\x64\Release\net8.0-windows\LenovoLoqControlService.exe"
$binaryPath = [IO.Path]::GetFullPath($binaryPath)

if (-not (Test-Path $binaryPath)) {
    throw "Service executable was not found: $binaryPath"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

& sc.exe create $serviceName binPath= "`"$binaryPath`"" start= auto DisplayName= "LOQ Control Hardware Service" | Out-Null
& sc.exe description $serviceName "Maintains verified Lenovo LOQ thermal mode state after the desktop UI closes." | Out-Null
Start-Service -Name $serviceName
Get-Service -Name $serviceName
