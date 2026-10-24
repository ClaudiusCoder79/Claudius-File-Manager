@echo off
REM Builds a single, self-contained ClaudiusFileManager.exe for Windows 11 x64.
REM Requires the .NET 8 SDK:  winget install Microsoft.DotNet.SDK.8

cd /d "%~dp0"
setlocal

set "PROJ="
if exist "src\ClaudiusFileManager\ClaudiusFileManager.csproj" set "PROJ=%CD%\src\ClaudiusFileManager\ClaudiusFileManager.csproj"
if not defined PROJ (
  for /r "%CD%" %%F in (ClaudiusFileManager.csproj) do (
    if not defined PROJ if exist "%%F" set "PROJ=%%F"
  )
)
if not defined PROJ (
  echo Could not find ClaudiusFileManager.csproj in %CD%
  echo This folder should contain:  src\ClaudiusFileManager\ClaudiusFileManager.csproj
  echo It currently contains:
  dir /b
  goto :fail
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo The .NET 8 SDK is not installed. Install it with:
  echo   winget install Microsoft.DotNet.SDK.8
  goto :fail
)

dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o "%CD%\publish"
if errorlevel 1 goto :fail

echo.
echo Done:  %CD%\publish\ClaudiusFileManager.exe
start "" "%CD%\publish"
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
