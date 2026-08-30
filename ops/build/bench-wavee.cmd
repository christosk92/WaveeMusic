@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bench-wavee.ps1" %*
exit /b %ERRORLEVEL%
