@echo off
rem Native menu smoke test: screenshots the title screen with the Online button and the Online popup, then quits.
for /f "usebackq delims=" %%g in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\game-dir.ps1"`) do set "GAME=%%g"
if not defined GAME exit /b 1
start "" /D "%GAME%" "%GAME%\SeaOfStars.exe" -screen-fullscreen 0 -screen-width 960 -screen-height 540 --sosonline-uitest %*
