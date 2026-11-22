@echo off
setlocal
cd /d "%~dp0"
echo =========================================
echo   DockerManager - Lokale Build
echo =========================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FOUT] Build mislukt!
    pause
    exit /b %ERRORLEVEL%
)
echo.
pause
