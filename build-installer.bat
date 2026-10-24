@echo off
REM Builds ClaudiusFileManager.exe, then wraps it in a Windows installer (setup.exe).
REM Requires:
REM   .NET 8 SDK     winget install Microsoft.DotNet.SDK.8
REM   Inno Setup 6   winget install JRSoftware.InnoSetup

cd /d "%~dp0"
setlocal

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.0.0"

REM ---- Find the project file (normally src\ClaudiusFileManager\ClaudiusFileManager.csproj) ----
set "PROJ="
if exist "src\ClaudiusFileManager\ClaudiusFileManager.csproj" set "PROJ=%CD%\src\ClaudiusFileManager\ClaudiusFileManager.csproj"
if not defined PROJ (
  for /r "%CD%" %%F in (ClaudiusFileManager.csproj) do (
    if not defined PROJ if exist "%%F" set "PROJ=%%F"
  )
)
if not defined PROJ (
  echo.
  echo Could not find ClaudiusFileManager.csproj in:
  echo   %CD%
  echo.
  echo This folder should contain:  src\ClaudiusFileManager\ClaudiusFileManager.csproj
  echo It currently contains:
  dir /b
  echo.
  echo Please download Claudius-File-Manager.zip again, right-click it, choose "Extract All...",
  echo and run build-installer.bat from the extracted folder that contains the "src" folder.
  goto :fail
)
for %%D in ("%PROJ%") do set "PROJDIR=%%~dpD"
echo Using project: %PROJ%

REM ---- Check the .NET SDK ----
where dotnet >nul 2>&1
if errorlevel 1 (
  echo The .NET 8 SDK is not installed. Install it with:
  echo   winget install Microsoft.DotNet.SDK.8
  echo then close this window and run build-installer.bat again.
  goto :fail
)

REM ---- Build the app ----
echo.
echo === Building ClaudiusFileManager.exe (version %VERSION%) ===
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:Version=%VERSION% -o "%CD%\publish"
if errorlevel 1 goto :fail
if not exist "publish\ClaudiusFileManager.exe" (
  echo The app build did not produce publish\ClaudiusFileManager.exe.
  goto :fail
)

REM ---- Find Inno Setup ----
set "ISCC="
for %%P in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do (
  if exist %%P set "ISCC=%%~P"
)
if not defined ISCC (
  where iscc >nul 2>&1 && set "ISCC=iscc"
)
if not defined ISCC (
  echo.
  echo The app was built: %CD%\publish\ClaudiusFileManager.exe
  echo but Inno Setup 6 was not found, so no installer was made. Install it with:
  echo   winget install JRSoftware.InnoSetup
  goto :fail
)

set "ISS=%CD%\installer\ClaudiusFileManager.iss"
if not exist "%ISS%" (
  echo.
  echo The app was built, but installer\ClaudiusFileManager.iss is missing from this folder.
  goto :fail
)

REM ---- Build the installer ----
echo.
echo === Building installer ===
"%ISCC%" /Qp "/DMyAppVersion=%VERSION%" "/DMyAppSource=%CD%\publish\ClaudiusFileManager.exe" "/DMyAppIcon=%PROJDIR%Assets\app.ico" "/DMyRoot=%CD%" "%ISS%"
if errorlevel 1 goto :fail

echo.
echo Done:  %CD%\installer-output\ClaudiusFileManager-Setup-%VERSION%.exe
start "" "%CD%\installer-output"
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
