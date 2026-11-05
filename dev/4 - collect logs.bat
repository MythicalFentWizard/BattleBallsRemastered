@echo off
copy /y "%USERPROFILE%\AppData\LocalLow\BattleBitDevTeam\BattleBit\Player.log" "%~dp0..\client_Player.log"
copy /y "%USERPROFILE%\AppData\LocalLow\BattleBitDevTeam\BattleBit\Player-prev.log" "%~dp0..\client_Player-prev.log"
echo Logs copied into the _revival folder. Tell Claude you're done.
pause
