@echo off
setlocal
set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if exist "%VCVARS%" goto found_vcvars
echo Visual Studio x64 build environment not found: %VCVARS%
exit /b 1
:found_vcvars
call "%VCVARS%"
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
exit /b %errorlevel%
