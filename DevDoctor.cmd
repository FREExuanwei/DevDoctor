@echo off
setlocal
if exist "%~dp0artifacts\DevDoctor-v1.2.0-win-x64-portable\DevDoctor.exe" (
  start "" "%~dp0artifacts\DevDoctor-v1.2.0-win-x64-portable\DevDoctor.exe"
  exit /b 0
)
if exist "%~dp0artifacts\DevDoctor-v1.1.0-win-x64-portable\DevDoctor.exe" (
  start "" "%~dp0artifacts\DevDoctor-v1.1.0-win-x64-portable\DevDoctor.exe"
  exit /b 0
)
if exist "%~dp0artifacts\DevDoctor-v1.0.0-win-x64-portable\DevDoctor.exe" (
  start "" "%~dp0artifacts\DevDoctor-v1.0.0-win-x64-portable\DevDoctor.exe"
  exit /b 0
)
if exist "%~dp0.tools\dotnet\dotnet.exe" set "DOTNET_ROOT=%~dp0.tools\dotnet"
if not exist "%~dp0src\DevDoctor.App\bin\Debug\net10.0-windows\DevDoctor.exe" (
  echo Build DevDoctor first. See README.md.
  pause
  exit /b 1
)
start "" "%~dp0src\DevDoctor.App\bin\Debug\net10.0-windows\DevDoctor.exe"
endlocal
