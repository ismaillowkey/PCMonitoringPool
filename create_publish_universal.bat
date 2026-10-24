@echo off
setlocal

title Publish PC System Resource Monitor - Universal (Release)

echo ======================================================================
echo        PC System Resource Monitor - Publish Script (Universal)
echo ======================================================================
echo.

:: 1. Detect directory structure and locate project file
set "SCRIPT_DIR=%~dp0"
set "PROJECT_FILE="
set "OUTPUT_DIR="

:: Case 1: Script is in workspace root, csproj is in src subfolder
if exist "%SCRIPT_DIR%src\PCMonitoringCPURam.csproj" (
    set "PROJECT_FILE=%SCRIPT_DIR%src\PCMonitoringCPURam.csproj"
    set "OUTPUT_DIR=%SCRIPT_DIR%publish"
    goto :CHECK_PROJECT
)

:: Case 2: Script is in the same folder as csproj
if exist "%SCRIPT_DIR%PCMonitoringCPURam.csproj" (
    set "PROJECT_FILE=%SCRIPT_DIR%PCMonitoringCPURam.csproj"
    set "OUTPUT_DIR=%SCRIPT_DIR%publish"
    goto :CHECK_PROJECT
)

:: Case 3: Script is in publish folder, csproj is in parent's src subfolder
if exist "%SCRIPT_DIR%..\src\PCMonitoringCPURam.csproj" (
    set "PROJECT_FILE=%SCRIPT_DIR%..\src\PCMonitoringCPURam.csproj"
    set "OUTPUT_DIR=%SCRIPT_DIR%"
    goto :CHECK_PROJECT
)

:: Case 4: Script is in publish folder, csproj is in parent folder
if exist "%SCRIPT_DIR%..\PCMonitoringCPURam.csproj" (
    set "PROJECT_FILE=%SCRIPT_DIR%..\PCMonitoringCPURam.csproj"
    set "OUTPUT_DIR=%SCRIPT_DIR%"
    goto :CHECK_PROJECT
)

:CHECK_PROJECT
if not defined PROJECT_FILE (
    echo [ERROR] Project file PCMonitoringCPURam.csproj could not be found.
    echo Searched locations:
    echo   - "%SCRIPT_DIR%src\PCMonitoringCPURam.csproj"
    echo   - "%SCRIPT_DIR%PCMonitoringCPURam.csproj"
    echo   - "%SCRIPT_DIR%..\src\PCMonitoringCPURam.csproj"
    echo.
    goto :ERROR_EXIT
)

echo [1/3] Checking .NET SDK availability...
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET SDK is not installed or not found in system PATH.
    echo Please install .NET 8 SDK or higher from https://dotnet.microsoft.com/download
    echo.
    goto :ERROR_EXIT
)

for /f "tokens=*" %%i in ('dotnet --version') do set "DOTNET_VER=%%i"
echo       Detected .NET SDK Version: %DOTNET_VER%
echo.

echo [2/3] Preparing output directory...
echo       Project file  : "%PROJECT_FILE%"
echo       Output folder : "%OUTPUT_DIR%"
echo.

echo [3/3] Publishing universal application (Release)...
echo       Command: dotnet publish "%PROJECT_FILE%" -c Release -o "%OUTPUT_DIR%"
echo.

dotnet publish "%PROJECT_FILE%" -c Release -o "%OUTPUT_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Publish process failed with exit code: %ERRORLEVEL%
    echo.
    goto :ERROR_EXIT
)

:: Salin file konfigurasi port (port.conf) ke folder hasil publish
:: agar port HTTP bisa diubah tanpa perlu build ulang
set "PORT_CONF_SRC="
if exist "%SCRIPT_DIR%port.conf" set "PORT_CONF_SRC=%SCRIPT_DIR%port.conf"
if not defined PORT_CONF_SRC if exist "%SCRIPT_DIR%..\port.conf" set "PORT_CONF_SRC=%SCRIPT_DIR%..\port.conf"
if not defined PORT_CONF_SRC if exist "%OUTPUT_DIR%\port.conf" set "PORT_CONF_SRC=%OUTPUT_DIR%\port.conf"

if defined PORT_CONF_SRC (
    if /i not "%PORT_CONF_SRC%"=="%OUTPUT_DIR%\port.conf" copy /y "%PORT_CONF_SRC%" "%OUTPUT_DIR%\port.conf" >nul
    echo.
    echo Port config : "%OUTPUT_DIR%\port.conf"
) else (
    echo.
    echo [WARN] port.conf tidak ditemukan. Aplikasi akan memakai port default 5055.
)

echo.
echo ======================================================================
echo                   PUBLISH COMPLETED SUCCESSFULLY!
echo ======================================================================
echo.
echo Target Mode: Universal / Framework-Dependent (Release)
echo Output Directory:
echo "%OUTPUT_DIR%"
echo.
echo Primary Output:
echo   - Executable : "%OUTPUT_DIR%\PCMonitoringCPURam.exe"
echo   - Assembly   : "%OUTPUT_DIR%\PCMonitoringCPURam.dll"
echo.
echo How to run:
echo   - Windows : Double-click PCMonitoringCPURam.exe or run: dotnet "%OUTPUT_DIR%\PCMonitoringCPURam.dll"
echo   - Linux   : dotnet "%OUTPUT_DIR%\PCMonitoringCPURam.dll"
echo.
pause
exit /b 0

:ERROR_EXIT
echo ======================================================================
echo                       PUBLISH FAILED!
echo ======================================================================
echo.
pause
exit /b 1
