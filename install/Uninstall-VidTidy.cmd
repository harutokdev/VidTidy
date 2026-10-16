@rem Removes the VidTidy in this folder; the library and settings are kept. One line on purpose: "(goto)" ends the batch before this file is deleted, and the rest of the line still runs.
@cd /d "%TEMP%" & (goto) 2>nul & powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-VidTidy.ps1" -Uninstall -InstallDir "%~dp0."
