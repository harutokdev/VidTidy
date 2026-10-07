#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Runs the whole HDD thermal measurement unattended: starts the ThroughputLab soak protocol on test-lab media
    and records the disk temperature until the soak ends.

.DESCRIPTION
    One command, no hand-off: the soak (idle, then continuous processing, then rest) and the temperature logger
    run together, and everything lands in the test lab's measurements folder:
        temperature-disk<N>-<stamp>.csv   temperature every IntervalSeconds
        soak-console-<stamp>.log          the soak's own output
        soak-activity-*.csv, soak-phases-*.json   written by the soak itself
        thermal-soak-<stamp>.done.json    written last; its presence means the run is finished
    The soak only processes the synthetic set inside the test lab. The temperature reading uses the drive's
    reliability counter and does not open any files. Administrator rights are needed only for the temperature.
    Ctrl+C stops both; rows already written are kept.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Start-ThermalSoak.ps1
#>
param(
    [int]$DiskNumber = 1,
    [int]$IntervalSeconds = 30,
    [int]$IdleMinutes = 10,
    [int]$WorkMinutes = 60,
    [int]$RestMinutes = 30,
    [string]$Set = 'standard-a',
    [string]$Lab = 'E:\LocalMediaAI-TestLab'
)

$ErrorActionPreference = 'Stop'
if ((Split-Path -Leaf $Lab) -ne 'LocalMediaAI-TestLab') { throw "The lab folder must be named LocalMediaAI-TestLab: $Lab" }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $repo 'artifacts\lab\bin\ThroughputLab\release\ThroughputLab.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "ThroughputLab is not built: $exe" }
$measurements = Join-Path $Lab 'measurements'
New-Item -ItemType Directory -Force -Path $measurements | Out-Null

$disk = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$DiskNumber" }
if (-not $disk) { throw "Physical disk $DiskNumber was not found." }
Write-Host "Disk $DiskNumber : $($disk.FriendlyName) ($($disk.MediaType), $($disk.BusType))"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$temperature = Join-Path $measurements "temperature-disk$DiskNumber-$stamp.csv"
$console = Join-Path $measurements "soak-console-$stamp.log"
$consoleErrors = Join-Path $measurements "soak-console-$stamp.err.log"
$done = Join-Path $measurements "thermal-soak-$stamp.done.json"
'time,temperature_c,temperature_max_c' | Set-Content -LiteralPath $temperature -Encoding utf8

$arguments = @('soak', '--lab', "`"$Lab`"", '--set', $Set,
    '--idle-minutes', $IdleMinutes, '--work-minutes', $WorkMinutes, '--rest-minutes', $RestMinutes)
$soak = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $repo -NoNewWindow -PassThru `
    -RedirectStandardOutput $console -RedirectStandardError $consoleErrors
$null = $soak.Handle  # keeps the process handle so ExitCode is available after exit
$total = $IdleMinutes + $WorkMinutes + $RestMinutes
Write-Host "Soak started ($total min: idle $IdleMinutes / work $WorkMinutes / rest $RestMinutes). You can leave this window open and walk away."
Write-Host "Results go to $measurements"

$started = Get-Date
try {
    while (-not $soak.HasExited) {
        $counter = $disk | Get-StorageReliabilityCounter
        Add-Content -LiteralPath $temperature -Value ('{0},{1},{2}' -f (Get-Date).ToString('o'), $counter.Temperature, $counter.TemperatureMax) -Encoding utf8
        Write-Host ("{0:HH:mm:ss}  {1} C  ({2:F0}/{3} min)" -f (Get-Date), $counter.Temperature, ((Get-Date) - $started).TotalMinutes, $total)
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    if (-not $soak.HasExited) {
        Write-Host 'Stopping the soak...'
        Stop-Process -Id $soak.Id -Force
        $soak.WaitForExit()
    }
    $summary = [ordered]@{
        finishedAt   = (Get-Date).ToString('o')
        minutes      = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        plannedMin   = $total
        soakExitCode = $soak.ExitCode
        temperature  = Split-Path -Leaf $temperature
        console      = Split-Path -Leaf $console
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath $done -Encoding utf8
    Write-Host "Done (soak exit code $($soak.ExitCode)). Summary: $done"
}
