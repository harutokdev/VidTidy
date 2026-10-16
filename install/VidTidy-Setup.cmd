@echo off
rem VidTidy one-click setup. Downloads the latest installer from
rem https://github.com/harutokdev/VidTidy/releases and runs it.
rem Installs to %LOCALAPPDATA%\Programs\VidTidy; running it again updates VidTidy.
rem No administrator rights are needed. Details: Install-VidTidy.ps1
setlocal
set "VIDTIDY_DIR="
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; $p=Join-Path $env:TEMP ('Install-VidTidy-'+[guid]::NewGuid().ToString('N')+'.ps1'); $a=@{}; if ($env:VIDTIDY_DIR) { $a.InstallDir=$env:VIDTIDY_DIR }; try { Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/harutokdev/VidTidy/releases/latest/download/Install-VidTidy.ps1' -OutFile $p } catch { $l=if ($env:VIDTIDY_DIR) { Join-Path $env:VIDTIDY_DIR 'Install-VidTidy.ps1' }; if ($l -and (Test-Path -LiteralPath $l)) { Copy-Item -LiteralPath $l -Destination $p } else { Write-Host ('Could not download the installer: '+$_.Exception.Message) -ForegroundColor Red; [void](Read-Host 'Press Enter to close'); exit 1 } }; try { & $p @a } finally { Remove-Item -LiteralPath $p -ErrorAction SilentlyContinue }; exit $LASTEXITCODE"
