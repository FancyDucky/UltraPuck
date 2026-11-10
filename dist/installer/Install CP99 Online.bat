@echo off
title CP99 Online - Install
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\cp99-setup.ps1" -Mode Install
echo.
pause
