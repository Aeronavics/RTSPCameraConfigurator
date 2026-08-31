@echo off
rem ---------------------------------------------------------------------------
rem  Prints the full path of an ffmpeg.exe to bundle, or nothing if none is found.
rem  Shared by the csproj publish step and the installer build.
rem
rem  Prefers an essentials build: it carries the H.264/H.265 decoders and the
rem  rawvideo muxer this app uses, at ~97 MB against ~212 MB for full_build.
rem  WinGet's Links\ffmpeg.exe is a zero-byte shim, so the real binary is resolved.
rem ---------------------------------------------------------------------------
setlocal
set "FOUND="

if exist "%LOCALAPPDATA%\Microsoft\WinGet\Packages" (
    for /f "delims=" %%F in ('dir /b /s "%LOCALAPPDATA%\Microsoft\WinGet\Packages\ffmpeg.exe" 2^>nul ^| findstr /i essentials') do (
        if not defined FOUND call :take "%%F"
    )
    for /f "delims=" %%F in ('dir /b /s "%LOCALAPPDATA%\Microsoft\WinGet\Packages\ffmpeg.exe" 2^>nul') do (
        if not defined FOUND call :take "%%F"
    )
)
if not defined FOUND for %%F in (ffmpeg.exe) do if exist "%%~$PATH:F" call :take "%%~$PATH:F"

if defined FOUND echo %FOUND%
endlocal
goto :eof

:take
rem A zero-byte file is the WinGet shim, not the real binary.
if %~z1 GTR 1000000 set "FOUND=%~f1"
goto :eof
