@echo off
cd /d "%~dp0..\"
echo Stopping anything still running...
taskkill /IM BattleBit.exe /F >nul 2>&1
taskkill /IM MasterServer.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

del /q master_log.txt client_log.txt server_log.txt >nul 2>&1

echo Building master server...
pushd MasterServer
dotnet build -c Release --nologo -v q
popd
if errorlevel 1 goto :fail

echo Starting master server in PROBE mode...
start "" /min "%~dp0..\MasterServer\bin\Release\net10.0\MasterServer.exe" --local-only --probe
timeout /t 4 /nobreak >nul

echo Starting game server...
start "" /min "%~dp0..\..\BattleBit.exe" -batchmode -nographics -Port=30000 -Hz=60 -AntiCheat=None -MaxPing=999 -VoxelMode=false -Name=RevivalTest -logFile "%~dp0..\server_log.txt"
timeout /t 14 /nobreak >nul

echo Starting game client...
start "" "%~dp0..\..\BattleBit.exe" -logFile "%~dp0..\client_log.txt"

echo.
echo Open the server browser and screenshot the server row.
echo The row carries labelled markers: S1name / S2map / S3mode,
echo numbers 11 22 33 44, 111111, 222222 and 123.
echo Whichever marker shows in which column tells us the real field order.
goto :eof

:fail
echo BUILD FAILED
pause
