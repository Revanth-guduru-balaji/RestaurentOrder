@echo off
setlocal EnableDelayedExpansion

REM build.bat - Build a self-contained, single-file RestaurantOrder.exe.
REM
REM Usage:
REM     build.bat                    (defaults to win-x64)
REM     build.bat win-x64
REM     build.bat win-arm64
REM
REM Output: dist\RestaurantOrder.exe  (one file, no .NET install needed on target)

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "ROOT=%~dp0"
set "PROJECT=%ROOT%RestaurantOrder\RestaurantOrder.csproj"
set "PUBDIR=%ROOT%RestaurantOrder\bin\Release\net8.0-windows\%RID%\publish"
set "DIST=%ROOT%dist"

echo.
echo === RestaurantOrder build ===
echo   Runtime: %RID%
echo   Project: %PROJECT%
echo   Output:  %DIST%\RestaurantOrder.exe
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: 'dotnet' was not found on PATH.
    echo Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] Publishing self-contained single-file exe ...
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded
if errorlevel 1 (
    echo.
    echo ERROR: publish failed. See messages above.
    pause
    exit /b 1
)

if not exist "%PUBDIR%\RestaurantOrder.exe" (
    echo.
    echo ERROR: expected output file not found:
    echo   %PUBDIR%\RestaurantOrder.exe
    pause
    exit /b 1
)

echo.
echo [2/2] Copying exe to %DIST% ...
if not exist "%DIST%" mkdir "%DIST%"
copy /Y "%PUBDIR%\RestaurantOrder.exe" "%DIST%\RestaurantOrder.exe" >nul
if errorlevel 1 (
    echo ERROR: copy failed.
    pause
    exit /b 1
)

echo.
echo Done.
for %%I in ("%DIST%\RestaurantOrder.exe") do echo   %%~fI  ^(%%~zI bytes^)
echo.
echo Copy this single .exe to any Windows 10/11 PC and double-click to run.
echo No .NET runtime install required on the target machine.
echo.
endlocal
exit /b 0
