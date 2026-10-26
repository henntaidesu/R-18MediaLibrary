@echo off
rem Build a distributable R-18MediaLibrary.exe: Release, framework-dependent (no .NET runtime bundled), single file.
rem The target machine needs the .NET 10 Desktop Runtime installed; if it's missing, double-clicking
rem R-18MediaLibrary.exe triggers the OS's built-in "install .NET runtime" prompt automatically (no extra code needed).
setlocal

cd /d "%~dp0"
set "OUT=%~dp0publish"

if exist "%OUT%" (
    echo Cleaning previous publish folder: %OUT%
    rmdir /s /q "%OUT%"
)

echo Publishing R-18MediaLibrary.exe ...
dotnet publish src\R-18MediaLibrary.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:VlcWindowsX86Enabled=false ^
    -o "%OUT%"

if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

rem The .lib files under libvlc\win-x64 are C/C++ link-time import libs, unused at runtime - drop them.
if exist "%OUT%\libvlc\win-x64\*.lib" del /q "%OUT%\libvlc\win-x64\*.lib"

echo.
echo Done: %OUT%\R-18MediaLibrary.exe
echo.
echo Notes:
echo   - All managed dependencies (HtmlAgilityPack, NAudio, SharpCompress, Sqlite, etc.) are bundled into the single exe.
echo   - The LibVLC engine's DLLs (libvlc, libvlccore, all codec/demux plugins) are bundled into the exe too,
echo     and self-extracted to a temp cache folder the first time the app runs.
echo   - A small libvlc\win-x64 folder remains next to the exe (hrtfs spatial-audio data + lua playlist/interface
echo     scripts, a few MB total) - LibVLC needs these as real files, keep them alongside R-18MediaLibrary.exe.
echo   - The .NET runtime itself is NOT bundled. If the target machine lacks the .NET 10 Desktop Runtime,
echo     running R-18MediaLibrary.exe shows the official Windows/.NET prompt guiding the user to download and install it.
