@echo off
rem Fabrique pc\dist\secondscreen.exe (mets ffmpeg.exe à côté après).
cd /d "%~dp0"
pip install pyinstaller
pyinstaller --onefile --noconsole --name secondscreen secondscreen.py
echo.
echo Termine : dist\secondscreen.exe  (copie ffmpeg.exe dans le meme dossier)
pause
