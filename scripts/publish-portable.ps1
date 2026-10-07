[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\portable\LocalMediaAI')
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'publish-complete-portable.ps1') -Configuration $Configuration -RuntimeIdentifier $RuntimeIdentifier -Output $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
