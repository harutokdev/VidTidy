<#
.SYNOPSIS
    Exercises Install-VidTidy.ps1 against local release files: install, update, refused and failed updates, uninstall.

.DESCRIPTION
    Takes a zip made by Build-Release.ps1, derives two later versions from it (one file dropped, one added) and
    runs the installer in Windows PowerShell 5.1 with -ReleaseDir, so nothing is fetched from the VidTidy
    repository. FFmpeg is downloaded from its publisher unless -NoFfmpeg is given.
    Everything happens under -WorkRoot; shortcuts are not created.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\release\Test-Installer.ps1 -Zip artifacts\release\v1.0.0\VidTidy-1.0.0-win-x64.zip -WorkRoot C:\VidTidy-InstallerTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [switch]$NoFfmpeg
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$installer = Join-Path $PSScriptRoot '..\..\install\Install-VidTidy.ps1'
$Zip = [IO.Path]::GetFullPath($Zip)
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $WorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force }
$releases = Join-Path $WorkRoot 'releases'
# Spaces, an apostrophe, brackets and non-ASCII text, as in real user profile names.
$dir = Join-Path $WorkRoot "Vid Tidy's [test] テスト"
New-Item -ItemType Directory -Path $releases | Out-Null
$base = [regex]::Match([IO.Path]::GetFileName($Zip), '^VidTidy-(\d+\.\d+\.\d+)-win-x64\.zip$').Groups[1].Value
if (-not $base) { throw "Not a release zip: $Zip" }
$v = [version]$base
$v2 = "$($v.Major).$($v.Minor).$($v.Build + 1)"
$v3 = "$($v.Major).$($v.Minor).$($v.Build + 2)"
$failures = New-Object Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) } }

function Write-Sha([string]$path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$path.sha256", "$hash  $([IO.Path]::GetFileName($path))`n")
}

# A later version: same files minus $drop, plus $add.
function New-DerivedZip([string]$version, [string]$drop, [string]$add) {
    $target = Join-Path $releases "VidTidy-$version-win-x64.zip"
    Copy-Item -LiteralPath $Zip -Destination $target
    $archive = [IO.Compression.ZipFile]::Open($target, [IO.Compression.ZipArchiveMode]::Update)
    try {
        if ($drop) { $archive.GetEntry($drop).Delete() }
        if ($add) {
            $writer = New-Object IO.StreamWriter($archive.CreateEntry($add).Open())
            $writer.Write("added in $version"); $writer.Dispose()
        }
    }
    finally { $archive.Dispose() }
    Write-Sha $target
    return $target
}

function Invoke-Installer([string[]]$arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -InstallDir $dir -ReleaseDir $releases -Quiet -NoLaunch -NoShortcuts @arguments | Out-Host
    return $LASTEXITCODE
}
function Get-State { Get-Content -LiteralPath (Join-Path $dir '.vidtidy-install.json') -Raw | ConvertFrom-Json }

Copy-Item -LiteralPath $Zip -Destination $releases
Write-Sha (Join-Path $releases ([IO.Path]::GetFileName($Zip)))
$ffArgs = @(if ($NoFfmpeg) { '-NoFfmpeg' })

Write-Host "1. Install v$base"
Check ((Invoke-Installer $ffArgs) -eq 0) 'installer exits 0'
Check (Test-Path -LiteralPath (Join-Path $dir 'LocalMediaAI.App.exe')) 'exe installed'
Check ((Get-State).version -eq $base) "state says v$base"
if (-not $NoFfmpeg) {
    Check ((Test-Path -LiteralPath (Join-Path $dir 'runtime\ffmpeg\ffmpeg.exe')) -and (Test-Path -LiteralPath (Join-Path $dir 'runtime\ffmpeg\ffprobe.exe'))) 'FFmpeg downloaded'
    $filters = & (Join-Path $dir 'runtime\ffmpeg\ffmpeg.exe') -hide_banner -filters 2>$null | Out-String
    Check ($filters -match '\bzscale\b' -and $filters -match '\btonemap\b') 'FFmpeg has zscale and tonemap (HDR thumbnails)'
}
# Files the user owns must survive updates.
New-Item -ItemType Directory -Force -Path (Join-Path $dir 'models\keep'), (Join-Path $dir 'runtime\python') | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'models\keep\model.txt') -Value 'user model'
Set-Content -LiteralPath (Join-Path $dir 'runtime\python\python.txt') -Value 'user runtime'
Set-Content -LiteralPath (Join-Path $dir 'data-location.json') -Value '{}'

Write-Host "2. Up to date"
Check ((Invoke-Installer $ffArgs) -eq 0) 'second run exits 0 without changes'

Write-Host "3. Update to v$v2 (drops LICENSE.txt, adds added.txt)"
New-DerivedZip $v2 'LICENSE.txt' 'added.txt' | Out-Null
Check ((Invoke-Installer @('-CheckOnly')) -eq 0) 'check-only exits 0'
Check ((Get-State).version -eq $base) 'check-only changes nothing'
Check ((Invoke-Installer $ffArgs) -eq 0) 'update exits 0'
Check ((Get-State).version -eq $v2) "state says v$v2"
Check (-not (Test-Path -LiteralPath (Join-Path $dir 'LICENSE.txt'))) 'dropped file removed'
Check (Test-Path -LiteralPath (Join-Path $dir 'added.txt')) 'new file added'
Check (Test-Path -LiteralPath (Join-Path "$dir.previous" 'LICENSE.txt')) 'previous version kept aside'
Check ((Test-Path -LiteralPath (Join-Path $dir 'models\keep\model.txt')) -and (Test-Path -LiteralPath (Join-Path $dir 'runtime\python\python.txt')) -and (Test-Path -LiteralPath (Join-Path $dir 'data-location.json'))) 'user files kept'
if (-not $NoFfmpeg) { Check (Test-Path -LiteralPath (Join-Path $dir 'runtime\ffmpeg\ffmpeg.exe')) 'FFmpeg kept' }

Write-Host "4. Refuse a zip whose checksum does not match"
$bad = New-DerivedZip $v3 $null 'tampered.txt'
[IO.File]::WriteAllText("$bad.sha256", ('0' * 64) + "  $([IO.Path]::GetFileName($bad))`n")
Check ((Invoke-Installer $ffArgs) -ne 0) 'installer exits non-zero'
Check ((Get-State).version -eq $v2 -and -not (Test-Path -LiteralPath (Join-Path $dir 'tampered.txt'))) 'nothing changed'

Write-Host "5. Roll back when a file cannot be replaced"
Write-Sha $bad
$locked = [IO.File]::Open((Join-Path $dir 'added.txt'), 'Open', 'Read', 'None')
try { Check ((Invoke-Installer $ffArgs) -ne 0) 'installer exits non-zero while a file is locked' }
finally { $locked.Dispose() }
Check ((Get-State).version -eq $v2) "state still says v$v2"
Check ((Test-Path -LiteralPath (Join-Path $dir 'LocalMediaAI.App.exe')) -and (Test-Path -LiteralPath (Join-Path $dir 'added.txt')) -and -not (Test-Path -LiteralPath (Join-Path $dir 'tampered.txt'))) 'previous files restored'

Write-Host "6. Uninstall"
Check ((Invoke-Installer @('-Uninstall')) -eq 0) 'uninstall exits 0'
Check (-not (Test-Path -LiteralPath (Join-Path $dir 'LocalMediaAI.App.exe')) -and -not (Test-Path -LiteralPath (Join-Path $dir '.vidtidy-install.json'))) 'app removed'
Check (-not (Test-Path -LiteralPath (Join-Path $dir 'runtime\ffmpeg'))) 'FFmpeg removed'
Check ((Test-Path -LiteralPath (Join-Path $dir 'models\keep\model.txt')) -and (Test-Path -LiteralPath (Join-Path $dir 'runtime\python\python.txt'))) 'user files kept'
Check (-not (Test-Path -LiteralPath "$dir.previous")) 'backup removed'

Write-Host "7. Uninstall through Uninstall-VidTidy.cmd, started from inside the install folder"
Check ((Invoke-Installer @('-NoFfmpeg')) -eq 0) 'reinstall exits 0'
Push-Location -LiteralPath $dir
try { "y`n`n" | cmd /c (Join-Path $dir 'Uninstall-VidTidy.cmd') 2>&1 | Out-Host }
finally { Pop-Location }
Check (-not (Test-Path -LiteralPath (Join-Path $dir 'LocalMediaAI.App.exe')) -and -not (Test-Path -LiteralPath (Join-Path $dir '.vidtidy-install.json'))) 'app removed'
Check (Test-Path -LiteralPath (Join-Path $dir 'models\keep\model.txt')) 'user files kept'

if ($failures.Count) { Write-Host "$($failures.Count) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All installer checks passed.'
