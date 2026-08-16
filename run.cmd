@echo off
rem ---------------------------------------------------------------------------
rem  Camera Setup launcher.
rem
rem  Double-click this, or run it from a terminal. It launches the published app
rem  if it is already built, and builds it first if it is not.
rem
rem  Pass /rebuild to force a fresh publish.
rem ---------------------------------------------------------------------------
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\RtspCameraSetup\RtspCameraSetup.csproj"
set "PUBLISH=%ROOT%src\RtspCameraSetup\bin\Release\net9.0-windows\win-x64\publish"
set "APP=%PUBLISH%\CameraSetup.exe"

set "FORCE="
if /i "%~1"=="/rebuild" set "FORCE=1"

rem Deliberately NOT deleting the publish folder to force a rebuild: presets live
rem in publish\presets, and wiping it would destroy saved parameter files.
rem dotnet publish overwrites the build output on its own.
if defined FORCE (
    echo Rebuilding...
    dotnet publish "%PROJECT%" -c Release --nologo
    if errorlevel 1 (
        echo.
        echo ERROR: the build failed. See the messages above.
        pause
        exit /b 1
    )
    echo.
)

if not exist "%APP%" (
    echo Building Camera Setup ^(first run^)...
    echo.

    where dotnet >nul 2>&1
    if errorlevel 1 (
        echo ERROR: the .NET SDK was not found on PATH.
        echo.
        echo Install it with:
        echo     winget install Microsoft.DotNet.SDK.9
        echo.
        echo Then run this again.
        pause
        exit /b 1
    )

    dotnet publish "%PROJECT%" -c Release --nologo
    if errorlevel 1 (
        echo.
        echo ERROR: the build failed. See the messages above.
        pause
        exit /b 1
    )
    echo.
)

if not exist "%APP%" (
    echo ERROR: expected the app at:
    echo     %APP%
    echo but it is not there. Try: run.cmd /rebuild
    pause
    exit /b 1
)

rem The whole publish folder matters, not just the exe: libvlc loads its native
rem DLLs and plugins from libvlc\win-x64 next to the executable.
echo Starting Camera Setup...
start "" "%APP%"

endlocal

