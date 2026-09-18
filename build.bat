@echo off
setlocal
echo =========================================
echo   DockerManager - Release Build
echo =========================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FOUT] Build mislukt!
    pause
    exit /b %ERRORLEVEL%
)
echo.
pause
