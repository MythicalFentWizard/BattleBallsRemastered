@echo off
cd /d "%~dp0..\.."
echo Starting a test server on port 30000. Close this window to stop it.
"%~dp0..\..\BattleBit.exe" -batchmode -nographics -Port=30000 -Hz=60 -AntiCheat=None -MaxPing=999 -VoxelMode=false -Name=RevivalTest -logFile "%~dp0..\server_log.txt"
pause
