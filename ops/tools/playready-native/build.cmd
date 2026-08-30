@echo off
REM Build FluentGpu.PlayReady.Native.dll ? in-process Win32 DLL (not a UWP sidecar).
REM FG_UWP enables the shared Media Foundation CDM/CENC implementation;
REM FG_WIN32_PMP selects the desktop PMP bridge; FG_DESKTOP_DLL exposes the in-process ABI.
setlocal
set VCVARS="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
set HERE=%~dp0
set ARCH=%~1
if "%ARCH%"=="" set ARCH=arm64
set OUT=out\%ARCH%

call %VCVARS% %ARCH% >nul
if errorlevel 1 ( echo vcvarsall failed & exit /b 1 )

pushd "%HERE%"
if not exist %OUT% mkdir %OUT%
if not exist %OUT%\linktmp mkdir %OUT%\linktmp
set TEMP=%HERE%%OUT%\linktmp
set TMP=%TEMP%

cl /nologo /std:c++20 /EHsc /MD /O2 /DWIN32 /D_UNICODE /DUNICODE /DFG_UWP /DFG_WIN32_PMP /DFG_DESKTOP_DLL ^
   /I "%HERE%generated" ^
   /Fo"%OUT%\\" /Fe"%OUT%\FluentGpu.PlayReady.Native.dll" /LD ^
   PlayReadyNative.cpp ^
   /link WindowsApp.lib
set RC=%errorlevel%
popd
endlocal & exit /b %RC%
