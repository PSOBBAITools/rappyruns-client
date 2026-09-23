@echo off
rem Builds pinshare-input.dll (x86, PsoBB.exe is 32-bit) with the VS2022 C++ tools.
rem Usage: build.cmd [output path]   (default: ..\..\data\pin-share\pinshare-input.dll)
setlocal
set HERE=%~dp0
set OUT=%~1
if "%OUT%"=="" set OUT=%HERE%..\..\data\pin-share\pinshare-input.dll
set VSDIR=
for %%r in ("%ProgramFiles%" "%ProgramFiles(x86)%") do for %%d in (Community Professional Enterprise BuildTools) do (
  if not defined VSDIR if exist "%%~r\Microsoft Visual Studio\2022\%%d\VC\Auxiliary\Build\vcvars32.bat" set "VSDIR=%%~r\Microsoft Visual Studio\2022\%%d"
)
if "%VSDIR%"=="" (echo Visual Studio 2022 C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul
if errorlevel 1 (echo vcvars32.bat failed & exit /b 1)
set OBJ=%TEMP%\pinshare-input-build
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /utf-8 /O2 /W3 /EHsc /MT /LD /Fo"%OBJ%\\" /Fe"%OUT%" "%HERE%pinshare-input.cpp" /link /SUBSYSTEM:WINDOWS /IMPLIB:"%OBJ%\pinshare-input.lib"
if errorlevel 1 (echo build failed & exit /b 1)
echo built %OUT%
