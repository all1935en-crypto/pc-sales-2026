@echo off
setlocal

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] 找不到 dotnet，請先安裝 .NET SDK。
  pause
  exit /b 1
)

echo 啟動 Main 專案...
dotnet run --project Main\Main.csproj

if errorlevel 1 (
  echo.
  echo [ERROR] 程式啟動失敗，請檢查錯誤訊息。
  pause
  exit /b 1
)

endlocal
