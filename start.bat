@echo off
setlocal

cd /d "%~dp0"

wscript.exe "%~dp0start-main.vbs"

endlocal
exit /b 0
