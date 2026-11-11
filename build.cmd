@echo off
setlocal
cd /d "%~dp0"
echo.
echo  Building installer and screenshots. Please wait 3-5 minutes...
echo.
call :run > build_log.txt 2>&1
echo  Finished. Log: build_log.txt
findstr /c:"error" /c:"EXIT" build_log.txt
echo.
pause
exit /b
:run
taskkill /f /im ETS2TwitchBot.exe
dotnet publish ETS2TwitchBot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "%~dp0out_full"
echo PUBLISH_FULL_EXIT=%errorlevel%
if exist "%~dp0out_full\ETS2TwitchBot.exe" start /wait "" "%~dp0out_full\ETS2TwitchBot.exe" --selftest
echo SELFTEST_EXIT=%errorlevel%
if exist "%~dp0out_full\selftest_log.txt" copy /y "%~dp0out_full\selftest_log.txt" "%~dp0selftest_result.txt"
set "DEST=%USERPROFILE%\Desktop\ETS2_Twitch_Bot_Installer"
mkdir "%DEST%"
copy /y "%~dp0out_full\ETS2TwitchBot.exe" "%DEST%\ETS2_Twitch_Bot_Setup.exe"
copy /y "%~dp0README_RU.txt" "%DEST%\README_RU.txt"
echo COPY_EXIT=%errorlevel%
set "TMPD=%TEMP%\ets2shots"
if exist "%TMPD%" rmdir /s /q "%TMPD%"
mkdir "%TMPD%"
copy /y "%~dp0out_full\ETS2TwitchBot.exe" "%TMPD%\ETS2TwitchBot.exe"
if exist "%~dp0shots" rmdir /s /q "%~dp0shots"
mkdir "%~dp0shots"
start /wait "" "%TMPD%\ETS2TwitchBot.exe" --shots "%~dp0shots"
echo SHOTS_EXIT=%errorlevel%
dir "%~dp0shots"
if exist "%LocalAppData%\Programs\ETS2 Twitch Bot\ETS2TwitchBot.exe" start "" "%LocalAppData%\Programs\ETS2 Twitch Bot\ETS2TwitchBot.exe"
exit /b
