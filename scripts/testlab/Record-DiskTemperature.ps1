#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Records one physical disk's temperature to a CSV while the ThroughputLab soak protocol runs.

.DESCRIPTION
    Reads only the drive's reliability counter (temperature). It does not read, list or open any files.
    Administrator rights are needed because Windows only exposes disk temperature to elevated sessions.
    Stop early with Ctrl+C; rows already written are kept.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Record-DiskTemperature.ps1 -DiskNumber 1 -DurationMinutes 110
#>
param(
    [int]$DiskNumber = 1,
    [int]$IntervalSeconds = 30,
    [int]$DurationMinutes = 110,
    [string]$OutputFolder = 'E:\LocalMediaAI-TestLab\measurements'
)

$ErrorActionPreference = 'Stop'
$disk = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$DiskNumber" }
if (-not $disk) { throw "Physical disk $DiskNumber was not found." }
Write-Host "Disk $DiskNumber : $($disk.FriendlyName) ($($disk.MediaType), $($disk.BusType))"

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$output = Join-Path $OutputFolder ("temperature-disk{0}-{1}.csv" -f $DiskNumber, (Get-Date -Format 'yyyyMMdd-HHmmss'))
'time,temperature_c,temperature_max_c' | Set-Content -LiteralPath $output -Encoding utf8
Write-Host "Writing $output every $IntervalSeconds s for $DurationMinutes min. Press Ctrl+C to stop early."

$deadline = (Get-Date).AddMinutes($DurationMinutes)
while ((Get-Date) -lt $deadline) {
    $counter = $disk | Get-StorageReliabilityCounter
    $line = '{0},{1},{2}' -f (Get-Date).ToString('o'), $counter.Temperature, $counter.TemperatureMax
    Add-Content -LiteralPath $output -Value $line -Encoding utf8
    Write-Host ("{0:HH:mm:ss}  {1} C" -f (Get-Date), $counter.Temperature)
    Start-Sleep -Seconds $IntervalSeconds
}
Write-Host "Done: $output"
