@echo off
cd /d "%~dp0..\"
if not exist "dump" mkdir "dump"
echo Dumping IL2CPP symbols, this takes a minute or two...
echo.
"%~dp0tools\il2cppdumper\Il2CppDumper.exe" "%~dp0..\..\GameAssembly.dll" "%~dp0..\..\BattleBit_Data\il2cpp_data\Metadata\global-metadata.dat" "%~dp0dump"
echo.
echo Done. Output written to %~dp0dump
pause
