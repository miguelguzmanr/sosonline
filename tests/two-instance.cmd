@echo off
rem Two game instances on one PC over loopback UDP: 27100 hosts (starts from save slot 0), 27101 joins.
rem Each writes BepInEx\SosOnline\state-<port>.log; matching lines mean both simulations agree.
rem Optional: set SCRIPT_HOST / SCRIPT_GUEST to scripted input files.
if "%SHOTS%"=="" set SHOTS=600,1200,1800,2350
for /f "usebackq delims=" %%g in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\game-dir.ps1"`) do set "GAME=%%g"
if not defined GAME exit /b 1
set COMMON=-screen-fullscreen 0 -screen-width 800 -screen-height 450 --sosonline-delay=3 --sosonline-quit=%QUIT%
if "%QUIT%"=="" set COMMON=-screen-fullscreen 0 -screen-width 800 -screen-height 450 --sosonline-delay=3 --sosonline-quit=2400
set SH=
if not "%SCRIPT_HOST%"=="" set SH=--sosonline-script=%SCRIPT_HOST%
set SG=
if not "%SCRIPT_GUEST%"=="" set SG=--sosonline-script=%SCRIPT_GUEST%
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" %COMMON% --sosonline-udp=27100 --sosonline-host --sosonline-guests=1 --sosonline-start=0 --sosonline-shots=%SHOTS% %SH%
timeout /t 3 /nobreak >nul
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" %COMMON% --sosonline-udp=27101 --sosonline-join=27100 --sosonline-shots=%SHOTS% %SG%
