@echo off
setlocal
REM Produces a self-contained LyllyPlayer (bundles .NET 8). Target PC needs Windows 10 x64 only — no .NET install.
REM Still requires ffmpeg and yt-dlp (configure paths in the app or put them on PATH).
cd /d "%~dp0"
dotnet publish "LyllyPlayer\LyllyPlayer.App\LyllyPlayer.App.csproj" -c Release -p:PublishProfile=Win10-SelfContained
if errorlevel 1 exit /b 1
echo.
echo Output: LyllyPlayer\LyllyPlayer.App\bin\Release\net8.0-windows\win-x64\publish\LyllyPlayer.exe
endlocal
