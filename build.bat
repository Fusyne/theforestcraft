@echo off
title ForestCraft - build
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
echo.
pause
