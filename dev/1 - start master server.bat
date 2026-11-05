@echo off
cd /d "%~dp0..\MasterServer"
echo Building Revival Master Server...
dotnet build -c Release --nologo -v q 2>&1
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Starting Revival Master Server (HTTP 29300 + WebSocket 29301)
echo Close this window to stop it.
echo.
dotnet run -c Release --no-build
pause
