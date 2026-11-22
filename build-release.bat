@echo off
setlocal
cd /d "%~dp0"
echo =========================================
echo   DockerManager - Officiele Release Build
echo =========================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Release %*
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FOUT] Release build mislukt!
    pause
    exit /b %ERRORLEVEL%
)
echo.
pause
