@echo off
title UltraPuck - Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\cp99-setup.ps1" -Mode Uninstall
echo.
pause
