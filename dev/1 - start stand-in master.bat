@echo off
echo NOTE: This is the old PowerShell stub. Use "1 - start master server.bat" instead.
cd /d "%~dp0..\"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stub_master.ps1"
pause
