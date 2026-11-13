@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  QingJie - build ^& publish
echo ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Install .NET 10 SDK first:
    echo         https://dotnet.microsoft.com/download
    goto :error
)

echo [1/2] Restore ^& build ...
dotnet build src\QingJie -c Release --nologo
if errorlevel 1 goto :error

echo.
echo [2/2] Publish single-file exe ...
dotnet publish src\QingJie -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist --nologo
if errorlevel 1 goto :error

echo.
echo [OK] Output: %~dp0dist\QingJie.exe
echo      Run it, then press Ctrl+Alt+A to take a screenshot.
goto :eof

:error
echo.
echo [FAILED] Build did not finish. See messages above.
exit /b 1
