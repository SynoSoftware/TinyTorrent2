@echo off
setlocal
title Generate TinyTorrent installer
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Build.ps1" %*
if errorlevel 1 (
    echo.
    echo Installer generation failed. The error above explains what to fix.
    pause
    exit /b 1
)
start "" "%~dp0artifacts\release"
