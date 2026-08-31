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
set "PROJECT=%ROOT%\src\RtspCameraSetup\RtspCameraSetup.csproj"
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
if not exist "%PAYLOAD%\CameraSetup.exe" (
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

rem ---------------------------------------------------------------------------
rem  ffmpeg. It is the preview engine, bundled so the good engine is the default
rem  with nothing for the user to do. It is not committed to the repo (~212 MB),
rem  so it is located on this machine at build time.
rem
rem  The WinGet "Links" entry is a zero-byte shim, so resolve the real binary.
rem  If none is found the installer still builds; it just warns at the end.
rem ---------------------------------------------------------------------------
set "FFMPEG="
set "FFDEFS="
set "FFLIC="

rem Prefer the essentials build. It carries the same H.264/H.265 decoders and the
rem rawvideo muxer this app actually uses, at ~97 MB against ~212 MB for full_build.
if exist "%LOCALAPPDATA%\Microsoft\WinGet\Packages" (
    for /f "delims=" %%F in ('dir /b /s "%LOCALAPPDATA%\Microsoft\WinGet\Packages\ffmpeg.exe" 2^>nul ^| findstr /i essentials') do (
        if not defined FFMPEG call :usereal "%%F"
    )
    for /f "delims=" %%F in ('dir /b /s "%LOCALAPPDATA%\Microsoft\WinGet\Packages\ffmpeg.exe" 2^>nul') do (
        if not defined FFMPEG call :usereal "%%F"
    )
)
if not defined FFMPEG for %%F in (ffmpeg.exe) do if exist "%%~$PATH:F" call :usereal "%%~$PATH:F"

if defined FFMPEG (
    echo Bundling ffmpeg from "%FFMPEG%"
    call :addlicence
) else (
    echo ffmpeg not found on this machine - building without it.
    echo The installer will tell the user how to add it.
)

if not exist "%ROOT%\build" mkdir "%ROOT%\build"

echo Compiling installer with "%ISCC%"...
"%ISCC%" /DPayload="%PAYLOAD%" %FFDEFS% "%~dp0CameraSetup.iss"
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
:usereal
if %~z1 GTR 1000000 set "FFMPEG=%~f1"
goto :eof

rem This is a GPL build, so its licence text has to travel with it.
:addlicence
for %%D in ("%FFMPEG%") do set "FFPARENT=%%~dpD.."
set "FFDEFS=/DFfmpeg=%FFMPEG%"
if exist "%FFPARENT%\LICENSE" set "FFDEFS=%FFDEFS% /DFfmpegLicense=%FFPARENT%\LICENSE"
if exist "%FFPARENT%\README.txt" set "FFDEFS=%FFDEFS% /DFfmpegReadme=%FFPARENT%\README.txt"
goto :eof
