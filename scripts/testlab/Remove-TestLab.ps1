<#
.SYNOPSIS
    Deletes every ThroughputLab test folder: generated and supplied test media, test catalogs, caches and measurements.

.DESCRIPTION
    Run before publishing the repository. Only folders named exactly "LocalMediaAI-TestLab" are touched.
    Junctions and symbolic links found inside are removed as links only; their targets are never entered.
    Use -WhatIf to see what would be removed. The repository is not affected: these folders live outside it
    and are never tracked by git.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Remove-TestLab.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$LabRoot = 'E:\LocalMediaAI-TestLab',
    # Only remove -LabRoot, not the SSD-side lab folders (used to test this script).
    [switch]$OnlyLabRoot
)

$ErrorActionPreference = 'Stop'

function Test-Link([string]$Path) {
    return (([IO.File]::GetAttributes($Path)) -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

# Walks the tree with .NET calls so that no link is ever followed (Remove-Item -Recurse in Windows PowerShell 5.1 can).
function Remove-TreeWithoutFollowingLinks([string]$Folder) {
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Folder)) {
        $isDirectory = [IO.Directory]::Exists($entry)
        if (Test-Link $entry) {
            if ($isDirectory) { [IO.Directory]::Delete($entry, $false) } else { [IO.File]::Delete($entry) }
        }
        elseif ($isDirectory) {
            Remove-TreeWithoutFollowingLinks $entry
            [IO.Directory]::Delete($entry, $false)
        }
        else {
            [IO.File]::SetAttributes($entry, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($entry)
        }
    }
}

function Measure-TreeWithoutFollowingLinks([string]$Folder) {
    $total = 0L
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Folder)) {
        if (Test-Link $entry) { continue }
        if ([IO.Directory]::Exists($entry)) { $total += Measure-TreeWithoutFollowingLinks $entry }
        else { $total += (New-Object IO.FileInfo $entry).Length }
    }
    return $total
}

$targets = if ($OnlyLabRoot) { @($LabRoot) } else { @($LabRoot, (Join-Path $env:SystemDrive 'LocalMediaAI-TestLab'), (Join-Path $env:LOCALAPPDATA 'LocalMediaAI-TestLab')) }
foreach ($target in $targets) {
    if ((Split-Path -Leaf $target) -ne 'LocalMediaAI-TestLab') { throw "Refusing to remove a folder not named LocalMediaAI-TestLab: $target" }
    if (-not [IO.Directory]::Exists($target)) { Write-Host "Not present: $target"; continue }
    if (Test-Link $target) { throw "Refusing to follow a junction or link: $target" }
    $bytes = Measure-TreeWithoutFollowingLinks $target
    if ($PSCmdlet.ShouldProcess($target, ('Remove {0:N1} GiB of test data' -f ($bytes / 1GB)))) {
        Remove-TreeWithoutFollowingLinks $target
        [IO.Directory]::Delete($target, $false)
        Write-Host "Removed $target"
    }
}
