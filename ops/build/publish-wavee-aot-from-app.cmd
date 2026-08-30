@ECHO OFF
SETLOCAL
REM NativeAOT publish for Wavee. Delegates to ops/build/publish-wavee-aot.ps1.
REM Output: src\apps\Wavee\bin\Release\net10.0\<rid>\publish\Wavee.exe
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-wavee-aot.ps1" %*
EXIT /B %ERRORLEVEL%
