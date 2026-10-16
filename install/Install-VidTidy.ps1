<#
.SYNOPSIS
    Installs, updates or removes VidTidy from its GitHub releases.

.DESCRIPTION
    One script for all three jobs; VidTidy-Setup.cmd (download and double-click) and the
    Update-VidTidy / Uninstall-VidTidy shortcuts all run it.

    - Install / update: downloads the newest release zip, checks its SHA-256 against the
      .sha256 file published with it (this catches a damaged download; trust in the content
      comes from the GitHub release itself), and swaps the application files. Only files a
      previous run installed are replaced; the old ones are kept in "<InstallDir>.previous"
      until the next update, and everything is put back if the swap fails.
    - FFmpeg is not part of VidTidy. When runtime\ffmpeg is missing, the LGPL build of
      FFmpeg is downloaded from its publisher (github.com/BtbN/FFmpeg-Builds) and checked
      against that release's checksums.sha256. An FFmpeg placed there by hand is left alone.
    - Never touched: the library and settings (%LOCALAPPDATA%\LocalMediaAI, or the folder
      chosen in the app), runtime\ and models\ beyond the FFmpeg it installed, portable data.
    - Works with Windows PowerShell 5.1 (built into Windows 10/11); no administrator rights.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-VidTidy.ps1
    powershell -ExecutionPolicy Bypass -File Install-VidTidy.ps1 -InstallDir D:\Apps\VidTidy
    powershell -ExecutionPolicy Bypass -File Install-VidTidy.ps1 -CheckOnly
    powershell -ExecutionPolicy Bypass -File Install-VidTidy.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Where the app goes. Default: %LOCALAPPDATA%\Programs\VidTidy
    [string]$InstallDir,
    # A release tag such as v1.0.0. Default: the latest release. Naming an older one allows a downgrade.
    [string]$Version,
    # A folder holding release files (VidTidy-<version>-win-x64.zip and its .sha256) instead of GitHub.
    [string]$ReleaseDir,
    # Reinstall even when the installed version is the same.
    [switch]$Force,
    # Only report whether an update exists.
    [switch]$CheckOnly,
    # Remove the app, its FFmpeg and shortcuts. The library and settings are kept.
    [switch]$Uninstall,
    # Do not download FFmpeg.
    [switch]$NoFfmpeg,
    # Download FFmpeg again (only when this installer put it there).
    [switch]$UpdateFfmpeg,
    [switch]$NoShortcuts,
    [switch]$NoLaunch,
    # Never wait for a key press (for scripts).
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Repo = 'harutokdev/VidTidy'
$ExeName = 'LocalMediaAI.App.exe'
$ProcessName = 'LocalMediaAI.App'
$StateName = '.vidtidy-install.json'
$WorkMarker = '.vidtidy-installer-folder'
$ZipPattern = '^VidTidy-(\d+\.\d+\.\d+)-win-x64\.zip$'
$FfmpegRepo = 'BtbN/FFmpeg-Builds'
$FfmpegPattern = '^ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-\d+\.\d+\.zip$'
$FfmpegPreferred = 'ffmpeg-n8.1-latest-win64-lgpl-8.1.zip'
$FfmpegFiles = @('runtime\ffmpeg\ffmpeg.exe', 'runtime\ffmpeg\ffprobe.exe', 'runtime\ffmpeg\LICENSE.txt')
# Folders and files next to the exe that belong to the user, never to a release.
$UserOwned = @('runtime', 'models', 'data', 'cache', 'temp', 'portable.flag', 'portable.flag.relocated', 'testlab.flag',
    'data-location.json', 'data-location.json.disabled', $StateName)

$Ja = (Get-UICulture).Name -like 'ja*'
function T([string]$ja, [string]$en) { if ($Ja) { $ja } else { $en } }
function Say([string]$ja, [string]$en) { Write-Host (T $ja $en) }

if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\VidTidy' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if ([IO.Path]::GetPathRoot($InstallDir).TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
    throw (T "ドライブの直下（$InstallDir）には入れられません。フォルダーを指定してください。" "Cannot install into a drive root ($InstallDir). Name a folder.")
}
$InstallDir = $InstallDir.TrimEnd('\')
$StatePath = Join-Path $InstallDir $StateName
$BackupDir = "$InstallDir.previous"
$StagingDir = "$InstallDir.staging"
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('VidTidy-Setup-' + [guid]::NewGuid().ToString('N'))

function Read-State {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { return $null }
    $state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    # A state written by an older installer may lack later fields.
    foreach ($field in @(@('files', @()), @('ffmpeg', $null), @('shortcuts', @()), @('shortcutsMade', $true))) {
        if (-not ($state.PSObject.Properties.Name -contains $field[0])) { $state | Add-Member -NotePropertyName $field[0] -NotePropertyValue $field[1] }
    }
    return $state
}

function Write-State($state) {
    # Written beside and then swapped in, so an interrupted write never leaves half a file.
    $json = $state | ConvertTo-Json -Depth 5
    $temp = "$StatePath.new"
    [IO.File]::WriteAllText($temp, $json, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $StatePath) { [IO.File]::Replace($temp, $StatePath, [NullString]::Value) } else { [IO.File]::Move($temp, $StatePath) }
}

function Test-UserOwned([string]$relative) {
    $first = ($relative -split '[\\/]')[0]
    foreach ($name in $UserOwned) { if ($first -ieq $name) { return $true } }
    return $false
}

function Get-InsidePath([string]$root, [string]$relative) {
    # Every path from a manifest or zip must stay under its root.
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Unsafe path: $relative" }
    $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path: $relative" }
    return $full
}

# The .staging and .previous folders are the installer's own; a folder of that name without the marker is someone else's.
function New-WorkFolder([string]$path) {
    Remove-WorkFolder $path
    if (Test-Path -LiteralPath $path) { throw (T "$path は VidTidy のものではないため使えません。移動するか名前を変えてください。" "$path is not VidTidy's, so it cannot be used. Move or rename it.") }
    New-Item -ItemType Directory -Path $path | Out-Null
    New-Item -ItemType File -Path (Join-Path $path $WorkMarker) | Out-Null
}

function Remove-WorkFolder([string]$path) {
    if ((Test-Path -LiteralPath $path) -and (Test-Path -LiteralPath (Join-Path $path $WorkMarker))) { Remove-Item -LiteralPath $path -Recurse -Force }
}

function Get-RemoteJson([string]$url) {
    return Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'VidTidy-Installer'; 'Accept' = 'application/vnd.github+json' }
}

function Save-RemoteFile([string]$url, [string]$path) {
    $request = [Net.HttpWebRequest]::Create($url)
    $request.UserAgent = 'VidTidy-Installer'
    $response = $request.GetResponse()
    try {
        $total = $response.ContentLength
        $stream = $response.GetResponseStream()
        $output = [IO.File]::Create($path)
        try {
            $buffer = New-Object byte[] 1048576
            $done = 0L
            $shown = 0
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $output.Write($buffer, 0, $read)
                $done += $read
                if ($total -gt 4MB) {
                    $percent = [int](100 * $done / $total)
                    if ($percent -ge $shown + 10) { Write-Host ('    {0,3}%  {1:N0} / {2:N0} MB' -f $percent, ($done / 1MB), ($total / 1MB)); $shown = $percent - ($percent % 10) }
                }
            }
        }
        finally { $output.Dispose(); $stream.Dispose() }
    }
    finally { $response.Dispose() }
}

function Get-ExpectedHash([string]$checksumText, [string]$fileName) {
    foreach ($line in ($checksumText -split "`r?`n")) {
        $parts = $line.Trim() -split '\s+\*?', 2
        if ($parts.Count -eq 2 -and $parts[1].Trim() -ieq $fileName -and $parts[0] -match '^[0-9a-fA-F]{64}$') { return $parts[0].ToUpperInvariant() }
        if ($parts.Count -eq 1 -and $parts[0] -match '^[0-9a-fA-F]{64}$') { return $parts[0].ToUpperInvariant() }
    }
    throw (T "チェックサムに $fileName がありません。" "No checksum for $fileName.")
}

function Assert-Hash([string]$path, [string]$expected) {
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw (T "ダウンロードしたファイルが壊れているか、公開されたものと違います（SHA-256 不一致）: $([IO.Path]::GetFileName($path))" "The downloaded file is damaged or differs from the published one (SHA-256 mismatch): $([IO.Path]::GetFileName($path))")
    }
}

# Returns @{ Version; ZipName; ZipUrl/ZipPath; HashUrl/HashPath }
function Find-Release {
    if ($ReleaseDir) {
        $dir = [IO.Path]::GetFullPath($ReleaseDir)
        $zips = @(Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -match $ZipPattern } |
            ForEach-Object { [pscustomobject]@{ File = $_; Version = [version]([regex]::Match($_.Name, $ZipPattern).Groups[1].Value) } } |
            Sort-Object Version -Descending)
        if ($Version) { $zips = @($zips | Where-Object { $_.Version -eq [version]$Version.TrimStart('v') }) }
        if (-not $zips) { throw (T "$dir に VidTidy の配布ファイルがありません。" "No VidTidy release file in $dir.") }
        $zip = $zips[0].File
        return @{ Version = $zips[0].Version; ZipName = $zip.Name; ZipPath = $zip.FullName; HashPath = "$($zip.FullName).sha256" }
    }
    $url = if ($Version) { "https://api.github.com/repos/$Repo/releases/tags/$Version" } else { "https://api.github.com/repos/$Repo/releases/latest" }
    try { $release = Get-RemoteJson $url }
    catch { throw (T "GitHub からリリース情報を取得できませんでした（インターネット接続を確認してください）: $($_.Exception.Message)" "Could not read the release from GitHub (check the internet connection): $($_.Exception.Message)") }
    $zip = @($release.assets | Where-Object { $_.name -match $ZipPattern })[0]
    if (-not $zip) { throw (T "リリース $($release.tag_name) に Windows 版の zip がありません。" "Release $($release.tag_name) has no Windows zip.") }
    $hash = @($release.assets | Where-Object { $_.name -eq "$($zip.name).sha256" })[0]
    if (-not $hash) { throw (T "リリース $($release.tag_name) にチェックサム（.sha256）がないため、インストールしません。" "Release $($release.tag_name) has no .sha256 checksum, so it is not installed.") }
    return @{ Version = [version]([regex]::Match($zip.name, $ZipPattern).Groups[1].Value); ZipName = $zip.name; ZipUrl = $zip.browser_download_url; HashUrl = $hash.browser_download_url }
}

function Wait-AppClosed {
    while ($true) {
        $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object {
                try { $_.Path -and $_.Path.StartsWith($InstallDir + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
        if (-not $running) { return }
        if ($Quiet) { throw (T 'VidTidy が起動中です。閉じてからやり直してください。' 'VidTidy is running. Close it and try again.') }
        $answer = Read-Host (T 'VidTidy が起動中です。閉じてから Enter を押してください（中止は q）' 'VidTidy is running. Close it, then press Enter (q to cancel)')
        if ($answer -eq 'q') { throw (T '中止しました。' 'Cancelled.') }
    }
}

function Assert-InstallDirUsable($state) {
    if ($state -or -not (Test-Path -LiteralPath $InstallDir)) { return }
    $foreign = @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { -not (Test-UserOwned $_.Name) })
    if ($foreign) {
        throw (T "$InstallDir には VidTidy 以外のファイルがあります。空のフォルダーか、別の場所を -InstallDir で指定してください。" "$InstallDir holds files that are not VidTidy's. Choose an empty folder with -InstallDir.")
    }
}

function Expand-Release([string]$zipPath) {
    New-WorkFolder $StagingDir
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }
            $relative = $entry.FullName.Replace('/', '\')
            if ((Test-UserOwned $relative) -or $relative -ieq $WorkMarker) { throw (T "配布ファイルにユーザーのフォルダー名が含まれています: $relative" "The release contains a user-owned path: $relative") }
            $target = Get-InsidePath $StagingDir $relative
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally { $archive.Dispose() }
    if (-not (Test-Path -LiteralPath (Join-Path $StagingDir $ExeName) -PathType Leaf)) { throw (T "配布ファイルに $ExeName がありません。" "The release has no $ExeName.") }
    return @(Get-ChildItem -LiteralPath $StagingDir -File -Recurse -Force | Where-Object { $_.Name -ne $WorkMarker } |
        ForEach-Object { $_.FullName.Substring($StagingDir.Length + 1) } | Sort-Object)
}

# Removes the folders that held the given files once they are empty; other folders, including empty ones the user made, stay.
function Remove-EmptyAppFolders([string[]]$files) {
    $folders = @{}
    foreach ($relative in $files) {
        $parent = [IO.Path]::GetDirectoryName($relative)
        while ($parent -and -not (Test-UserOwned $parent)) { $folders[$parent] = $true; $parent = [IO.Path]::GetDirectoryName($parent) }
    }
    foreach ($relative in @($folders.Keys | Sort-Object Length -Descending)) {
        $path = Join-Path $InstallDir $relative
        if ((Test-Path -LiteralPath $path -PathType Container) -and -not (Get-ChildItem -LiteralPath $path -Force)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}

# Moves the old release's files aside and the staged ones in; restores everything on failure.
function Switch-AppFiles([string[]]$oldFiles, [string[]]$newFiles) {
    New-WorkFolder $BackupDir
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $moved = New-Object Collections.Generic.List[string]
    $placed = New-Object Collections.Generic.List[string]
    try {
        foreach ($relative in @($oldFiles + $newFiles | Select-Object -Unique)) {
            if (Test-UserOwned $relative) { continue }
            $current = Get-InsidePath $InstallDir $relative
            if (-not (Test-Path -LiteralPath $current -PathType Leaf)) { continue }
            $aside = Get-InsidePath $BackupDir $relative
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($aside)) | Out-Null
            Move-Item -LiteralPath $current -Destination $aside
            $moved.Add($relative)
        }
        foreach ($relative in $newFiles) {
            $target = Get-InsidePath $InstallDir $relative
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
            Move-Item -LiteralPath (Get-InsidePath $StagingDir $relative) -Destination $target
            $placed.Add($relative)
        }
    }
    catch {
        $failure = $_
        Say '失敗したため、元に戻しています…' 'Failed; restoring the previous files...'
        $stuck = New-Object Collections.Generic.List[string]
        foreach ($relative in $placed) { Remove-Item -LiteralPath (Get-InsidePath $InstallDir $relative) -Force -ErrorAction SilentlyContinue }
        foreach ($relative in $moved) {
            try { Move-Item -LiteralPath (Get-InsidePath $BackupDir $relative) -Destination (Get-InsidePath $InstallDir $relative) -Force }
            catch { $stuck.Add($relative) }
        }
        if ($stuck.Count) {
            Say "次のファイルを戻せませんでした。$BackupDir から手で戻してください:" "These files could not be restored; copy them back from ${BackupDir}:"
            $stuck | ForEach-Object { Write-Host "    $_" }
        }
        throw $failure
    }
}

function Install-Ffmpeg($state) {
    $target = Join-Path $InstallDir 'runtime\ffmpeg'
    $exists = (Test-Path -LiteralPath (Join-Path $target 'ffmpeg.exe')) -or (Test-Path -LiteralPath (Join-Path $target 'ffprobe.exe'))
    if ($exists -and -not $state.ffmpeg) {
        Say 'runtime\ffmpeg は手動で置かれたものなので、そのままにします。' 'runtime\ffmpeg was placed by hand, so it is left as is.'
        return $null
    }
    $complete = (Test-Path -LiteralPath (Join-Path $target 'ffmpeg.exe')) -and (Test-Path -LiteralPath (Join-Path $target 'ffprobe.exe'))
    if ($complete -and -not $UpdateFfmpeg) { return $state.ffmpeg }

    Say 'FFmpeg（LGPL 版）を配布元からダウンロードしています…' 'Downloading FFmpeg (LGPL build) from its publisher...'
    $release = Get-RemoteJson "https://api.github.com/repos/$FfmpegRepo/releases/latest"
    $asset = @($release.assets | Where-Object { $_.name -eq $FfmpegPreferred })[0]
    if (-not $asset) {
        $asset = @($release.assets | Where-Object { $_.name -match $FfmpegPattern } |
            Sort-Object { $m = [regex]::Match($_.name, $FfmpegPattern); [version]"$($m.Groups[1].Value).$($m.Groups[2].Value)" } -Descending)[0]
    }
    $sums = @($release.assets | Where-Object { $_.name -eq 'checksums.sha256' })[0]
    if (-not $asset -or -not $sums) { throw (T 'FFmpeg の配布ファイルが見つかりません。' 'No FFmpeg download was found.') }
    $zip = Join-Path $WorkDir $asset.name
    $sumsPath = Join-Path $WorkDir 'ffmpeg-checksums.sha256'
    Save-RemoteFile $sums.browser_download_url $sumsPath
    Save-RemoteFile $asset.browser_download_url $zip
    $expected = Get-ExpectedHash (Get-Content -LiteralPath $sumsPath -Raw) $asset.name
    Assert-Hash $zip $expected

    $staged = Join-Path $WorkDir 'ffmpeg'
    New-Item -ItemType Directory -Path $staged | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($name in @('bin/ffmpeg.exe', 'bin/ffprobe.exe', 'LICENSE.txt')) {
            $entry = @($archive.Entries | Where-Object { $_.FullName -match ('^[^/]+/' + [regex]::Escape($name) + '$') })[0]
            if (-not $entry) { throw "FFmpeg zip has no $name" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staged ([IO.Path]::GetFileName($name))), $true)
        }
    }
    finally { $archive.Dispose() }
    & (Join-Path $staged 'ffmpeg.exe') -hide_banner -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw (T 'ダウンロードした FFmpeg が起動しません。' 'The downloaded FFmpeg does not start.') }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $staged -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force }
    return [pscustomobject]@{ asset = $asset.name; sha256 = $expected; files = $FfmpegFiles }
}

function Get-ShortcutPaths {
    $programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'VidTidy'
    return [pscustomobject]@{
        Folder = $programs
        App = Join-Path $programs 'VidTidy.lnk'
        Update = Join-Path $programs (T 'VidTidy を更新.lnk' 'Update VidTidy.lnk')
        Uninstall = Join-Path $programs (T 'VidTidy をアンインストール.lnk' 'Uninstall VidTidy.lnk')
        Desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'VidTidy.lnk'
    }
}

function Test-OurShortcut([string]$path) {
    if (-not $path -or $path -notlike '*.lnk') { return $false }
    $paths = Get-ShortcutPaths
    return ([IO.Path]::GetDirectoryName($path) -ieq $paths.Folder) -or ($path -ieq $paths.Desktop)
}

function New-Shortcuts {
    $paths = Get-ShortcutPaths
    New-Item -ItemType Directory -Force -Path $paths.Folder | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $exe = Join-Path $InstallDir $ExeName
    foreach ($item in @(
            @{ Path = $paths.App; Target = $exe; Icon = $exe; Dir = $InstallDir },
            @{ Path = $paths.Desktop; Target = $exe; Icon = $exe; Dir = $InstallDir },
            @{ Path = $paths.Update; Target = (Join-Path $InstallDir 'Update-VidTidy.cmd'); Icon = $exe; Dir = $env:TEMP },
            @{ Path = $paths.Uninstall; Target = (Join-Path $InstallDir 'Uninstall-VidTidy.cmd'); Icon = "$env:SystemRoot\System32\shell32.dll,31"; Dir = $env:TEMP })) {
        if (-not (Test-Path -LiteralPath $item.Target)) { continue }
        $link = $shell.CreateShortcut($item.Path)
        $link.TargetPath = $item.Target
        $link.WorkingDirectory = $item.Dir
        $link.IconLocation = $item.Icon
        $link.Save()
    }
    return @($paths.App, $paths.Desktop, $paths.Update, $paths.Uninstall | Where-Object { Test-Path -LiteralPath $_ })
}

function Invoke-Uninstall {
    $state = Read-State
    if (-not $state) { throw (T "$InstallDir にこのスクリプトで入れた VidTidy がありません。" "No VidTidy installed by this script in $InstallDir.") }
    if (-not $Quiet) {
        $answer = Read-Host (T "VidTidy を $InstallDir から削除します。ライブラリと設定は残ります。続けますか？ (y/N)" "Remove VidTidy from $InstallDir? The library and settings are kept. (y/N)")
        if ($answer -notmatch '^[yY]') { throw (T '中止しました。' 'Cancelled.') }
    }
    Wait-AppClosed
    # A folder that is anyone's current directory cannot be removed, this process's included.
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
    $files = @($state.files) + @(if ($state.ffmpeg) { $state.ffmpeg.files })
    foreach ($relative in $files) {
        $path = Get-InsidePath $InstallDir $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($link in @($state.shortcuts)) { if ((Test-OurShortcut $link) -and (Test-Path -LiteralPath $link)) { Remove-Item -LiteralPath $link -Force } }
    $folder = (Get-ShortcutPaths).Folder
    if ((Test-Path -LiteralPath $folder) -and -not (Get-ChildItem -LiteralPath $folder -Force)) { Remove-Item -LiteralPath $folder -Force }
    Remove-Item -LiteralPath $StatePath -Force
    Remove-WorkFolder $BackupDir
    Remove-WorkFolder $StagingDir
    Remove-EmptyAppFolders $files
    foreach ($dir in @('runtime\ffmpeg', 'runtime')) {
        $path = Join-Path $InstallDir $dir
        if ((Test-Path -LiteralPath $path) -and -not (Get-ChildItem -LiteralPath $path -Force)) { Remove-Item -LiteralPath $path -Force }
    }
    Say "VidTidy を削除しました。ライブラリと設定（$env:LOCALAPPDATA\LocalMediaAI など）は残しています。" "VidTidy was removed. The library and settings ($env:LOCALAPPDATA\LocalMediaAI etc.) were kept."
    if (-not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
        try { Remove-Item -LiteralPath $InstallDir -Force }
        catch { Say "空のフォルダー $InstallDir が残りました。手で削除できます。" "The empty folder $InstallDir is left; you can delete it." }
    }
    else { Say "残したもの（AI ランタイムやモデルなど）: $InstallDir" "Kept (AI runtime, models, ...): $InstallDir" }
}

function Invoke-Install {
    $state = Read-State
    $installed = if ($state) { [version]$state.version } else { $null }
    Assert-InstallDirUsable $state
    $release = Find-Release
    if ($installed) { Say "インストール済み: v$installed / 最新: v$($release.Version)" "Installed: v$installed / latest: v$($release.Version)" }
    else { Say "最新: v$($release.Version)" "Latest: v$($release.Version)" }

    $needApp = (-not $installed) -or $Force -or ($release.Version -gt $installed) -or ($Version -and $release.Version -ne $installed)
    if ($CheckOnly) {
        if ($needApp) { Say '更新があります。' 'An update is available.' } else { Say '最新の状態です。' 'Up to date.' }
        return $false
    }
    $needFfmpeg = -not $NoFfmpeg -and ($UpdateFfmpeg -or -not (Test-Path -LiteralPath (Join-Path $InstallDir 'runtime\ffmpeg\ffmpeg.exe')))
    $needShortcuts = -not $NoShortcuts -and -not ($state -and $state.shortcutsMade)
    if (-not $needApp -and -not $needFfmpeg -and -not $needShortcuts) { Say '最新の状態です。' 'Up to date.'; return $false }

    New-Item -ItemType Directory -Path $WorkDir | Out-Null
    $newState = if ($state) { $state } else { [pscustomobject]@{ version = $null; installedAtUtc = $null; files = @(); ffmpeg = $null; shortcuts = @(); shortcutsMade = $false } }
    if ($needApp) {
        Wait-AppClosed
        if ($release.ZipPath) { $zip = $release.ZipPath; $hashText = Get-Content -LiteralPath $release.HashPath -Raw }
        else {
            Say "v$($release.Version) をダウンロードしています…" "Downloading v$($release.Version)..."
            $zip = Join-Path $WorkDir $release.ZipName
            $hashPath = "$zip.sha256"
            Save-RemoteFile $release.HashUrl $hashPath
            Save-RemoteFile $release.ZipUrl $zip
            $hashText = Get-Content -LiteralPath $hashPath -Raw
        }
        Assert-Hash $zip (Get-ExpectedHash $hashText $release.ZipName)
        Say '展開して入れ替えています…' 'Extracting and replacing the app files...'
        $oldFiles = @($newState.files)
        $newFiles = Expand-Release $zip
        Switch-AppFiles $oldFiles $newFiles
        # Record the new files first: everything after this is tidying up.
        $newState.version = $release.Version.ToString()
        $newState.installedAtUtc = [DateTime]::UtcNow.ToString('o')
        $newState.files = $newFiles
        Write-State $newState
        Remove-WorkFolder $StagingDir
        Remove-EmptyAppFolders $oldFiles
    }
    # Shortcuts come before FFmpeg so a failed FFmpeg download does not leave the app without them.
    if ($needShortcuts) {
        $newState.shortcuts = New-Shortcuts
        $newState.shortcutsMade = $true
        Write-State $newState
    }
    elseif (-not $newState.shortcutsMade) { $newState.shortcutsMade = $true; Write-State $newState }
    if (-not $NoFfmpeg) {
        $newState.ffmpeg = Install-Ffmpeg $newState
        Write-State $newState
    }
    Say "VidTidy v$($newState.version) を $InstallDir に入れました。" "VidTidy v$($newState.version) is installed in $InstallDir."
    if ($state -and $needApp) { Say "前の版は $BackupDir に残しています（次の更新で消えます）。" "The previous version is kept in $BackupDir until the next update." }
    return $true
}

$exitCode = 0
$launch = $false
try {
    if ($Uninstall) { Invoke-Uninstall }
    else { $launch = (Invoke-Install) -and -not $NoLaunch -and -not $CheckOnly }
}
catch {
    $exitCode = 1
    Write-Host ''
    Write-Host ((T 'エラー: ' 'Error: ') + $_.Exception.Message) -ForegroundColor Red
}
finally {
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
    try { Remove-WorkFolder $StagingDir } catch { }
}

if ($launch) {
    # ProcessStartInfo, not Start-Process: the latter reads [ ] in the folder name as wildcards.
    $start = New-Object Diagnostics.ProcessStartInfo((Join-Path $InstallDir $ExeName))
    $start.WorkingDirectory = $InstallDir
    $start.UseShellExecute = $true
    [void][Diagnostics.Process]::Start($start)
}
elseif (-not $Quiet) { [void](Read-Host (T 'Enter で閉じます' 'Press Enter to close')) }
exit $exitCode
