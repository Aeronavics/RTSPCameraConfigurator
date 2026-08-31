@echo off
rem ---------------------------------------------------------------------------
rem  Builds the Camera Setup installer.
rem
rem    build-installer.cmd            publish, then compile the installer
rem    build-installer.cmd /nopublish compile from the existing publish output
rem
rem  Output lands in build\CameraSetup-Setup-<version>.exe
rem ---------------------------------------------------------------------------
setlocal

set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\src\RTSPCameraConfigurator\RTSPCameraConfigurator.csproj"
set "PAYLOAD=%ROOT%\dist"

if /i "%~1"=="/nopublish" goto :compile

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: the .NET SDK was not found on PATH.
    echo     winget install Microsoft.DotNet.SDK.9
    exit /b 1
)

echo Publishing...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%PAYLOAD%" --nologo
if errorlevel 1 (
    echo.
    echo ERROR: the build failed. See the messages above.
    exit /b 1
)
echo.

:compile
if not exist "%PAYLOAD%\RTSPCameraConfigurator.exe" (
    echo ERROR: no payload at "%PAYLOAD%".
    echo Run this without /nopublish, or publish first.
    exit /b 1
)

rem Inno Setup 6. ISCC is not added to PATH by its own installer, so look where
rem it actually lands before giving up.
set "ISCC="
where ISCC.exe >nul 2>&1 && set "ISCC=ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe"      set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
rem winget installs per-user when it is not run elevated, which lands it here.
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo ERROR: Inno Setup 6 was not found.
    echo.
    echo Install it with:
    echo     winget install JRSoftware.InnoSetup
    echo.
    echo Then run this again.
    exit /b 1
)

if not exist "%ROOT%\build" mkdir "%ROOT%\build"

echo Compiling installer with "%ISCC%"...
"%ISCC%" /DPayload="%PAYLOAD%" "%~dp0RTSPCameraConfigurator.iss"
if errorlevel 1 (
    echo.
    echo ERROR: the installer failed to compile.
    exit /b 1
)

echo.
echo Done. Installer is in "%ROOT%\build".
endlocal
goto :eof

rem A zero-byte file is the WinGet shim, not the real binary. Skip it.

rem This is a GPL build, so its licence text has to travel with it.
