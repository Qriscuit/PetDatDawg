@echo off
setlocal
title Pet Da Dog - Build Windows Client
echo Building Pet Da Dog for Windows...
echo The validated package will be saved in the Builds folder.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Export-Windows.ps1" %*
set "PDD_BUILD_RESULT=%ERRORLEVEL%"
echo.
if "%PDD_BUILD_RESULT%"=="0" (
    echo Build complete. Open "%~dp0Builds\PetDaDogCSharp.exe" to play.
) else (
    echo Build failed. Your previous Builds package was preserved.
    echo Read the error above for the required fix or the detailed log location.
)
echo.
if not defined PDD_BUILD_NO_PAUSE pause
exit /b %PDD_BUILD_RESULT%
