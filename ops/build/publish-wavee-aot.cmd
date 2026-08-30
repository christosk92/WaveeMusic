@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-wavee-aot.ps1" %*
exit /b %ERRORLEVEL%
