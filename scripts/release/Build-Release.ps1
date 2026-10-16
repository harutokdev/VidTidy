<#
.SYNOPSIS
    Builds the files for a VidTidy GitHub release (what Install-VidTidy.ps1 downloads).

.DESCRIPTION
    Publishes the app self-contained for win-x64 (no .NET install needed on the user's PC) and writes, in
    artifacts\release\v<Version>\:

      VidTidy-<Version>-win-x64.zip          the app, LICENSE, THIRD-PARTY-NOTICES.md and the update/uninstall launchers
      VidTidy-<Version>-win-x64.zip.sha256   its SHA-256 ("<hash>  <file name>"); the installer refuses a zip without it
      Install-VidTidy.ps1                    the installer/updater (the setup launcher downloads this one)
      VidTidy-Setup.cmd                      the one-click launcher users download and double-click

    Upload all four to a GitHub release tagged v<Version>. The zip holds no FFmpeg, Python runtime or model:
    the installer downloads FFmpeg from its publisher, and the user's runtime\ and models\ are never replaced.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\release\Build-Release.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Output) { $Output = Join-Path $repo "artifacts\release\v$Version" }
$Output = [IO.Path]::GetFullPath($Output)
$publish = Join-Path $Output 'publish'
$zipName = "VidTidy-$Version-win-x64.zip"
$zipPath = Join-Path $Output $zipName

# Only a folder holding an earlier build of these files is cleared; anything else stops the build.
if (Test-Path -LiteralPath $Output) {
    $known = @('publish', $zipName, "$zipName.sha256", 'Install-VidTidy.ps1', 'VidTidy-Setup.cmd')
    $other = @(Get-ChildItem -LiteralPath $Output -Force | Where-Object { $known -notcontains $_.Name })
    if ($other) { throw "$Output holds other files ($($other[0].Name), ...); choose an empty or new -Output folder." }
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$workspaceDotNet = Join-Path $repo '.tooling\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceDotNet -PathType Leaf) { $workspaceDotNet } else { 'dotnet' }
& $dotnet publish (Join-Path $repo 'src\LocalMediaAI.App\LocalMediaAI.App.csproj') --configuration Release --runtime win-x64 --self-contained true `
    --output $publish /p:Version=$Version /p:PublishSingleFile=false /p:PublishTrimmed=false /p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# A release carries only the app. These belong to the user's install and must never come from a zip.
foreach ($name in @('runtime', 'models', 'data', 'cache', 'temp', 'portable.flag', 'testlab.flag', 'data-location.json')) {
    if (Test-Path -LiteralPath (Join-Path $publish $name)) { throw "The published app contains '$name'; a release must not carry it." }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $publish 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $publish
foreach ($name in @('Install-VidTidy.ps1', 'Update-VidTidy.cmd', 'Uninstall-VidTidy.cmd')) { Copy-Item -LiteralPath (Join-Path $repo "install\$name") -Destination $publish }

# Entries use '/' so every unzip tool (and the installer) reads the same paths.
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName) {
        $entryName = $file.FullName.Substring($publish.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose(); $stream.Dispose() }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$zipPath.sha256", "$hash  $zipName`n", (New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath (Join-Path $repo 'install\Install-VidTidy.ps1') -Destination $Output
Copy-Item -LiteralPath (Join-Path $repo 'install\VidTidy-Setup.cmd') -Destination $Output
Remove-Item -LiteralPath $publish -Recurse -Force

Write-Host "Release files for v${Version}: $Output"
Get-ChildItem -LiteralPath $Output -File | ForEach-Object { Write-Host ('  {0,-40} {1,12:N0} bytes' -f $_.Name, $_.Length) }
Write-Host "SHA-256: $hash"
