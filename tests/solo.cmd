@echo off
rem Solo lockstep smoke test: hosts over UDP with no guests, loads save slot 0, takes screenshots, quits.
for /f "usebackq delims=" %%g in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\game-dir.ps1"`) do set "GAME=%%g"
if not defined GAME exit /b 1
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" -screen-fullscreen 0 -screen-width 960 -screen-height 540 --sosonline-udp=27100 --sosonline-host --sosonline-guests=0 --sosonline-start=0 --sosonline-shots=1,300,900,1500 --sosonline-quit=1600 %*
