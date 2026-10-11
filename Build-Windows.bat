@echo off
setlocal
title Pet Da Dog - Build Windows Client
echo Building Pet Da Dog for Windows...
echo The validated package will be saved in the Builds folder.
echo Godot is found in this checkout's tools folder or on PATH.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Export-Windows.ps1" %*
set "PDD_BUILD_RESULT=%ERRORLEVEL%"
echo.
if "%PDD_BUILD_RESULT%"=="0" (
    echo Build complete. Use the validated package folder shown above.
    echo Launch PetDaDogCSharp.exe from that folder to play.
) else (
    echo Build failed. Your previous Builds package was preserved.
    echo Read the error above for the required fix or the detailed log location.
)
echo.
if not defined PDD_BUILD_NO_PAUSE pause
exit /b %PDD_BUILD_RESULT%
