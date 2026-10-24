@echo off
setlocal enabledelayedexpansion

title PC Monitoring System - ASP.NET Core .NET 8
cd /d "%~dp0"
if exist "%~dp0src\PCMonitoringCPURam.csproj" cd /d "%~dp0src"

echo ==========================================================
echo        PC System Resource Monitor (CPU, RAM, Disk, Net)
echo                  ASP.NET Core .NET 8
echo ==========================================================
echo.

echo [1/2] Checking .NET SDK...
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET SDK is not installed or not found in system PATH.
    echo Please install .NET 8 SDK from https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

echo [2/2] Starting application (port dibaca dari port.conf, default 5055)...
echo.
echo Press Ctrl+C in this window to stop the server.
echo.

dotnet run
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Application terminated with exit code: %ERRORLEVEL%
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Application stopped normally.
pause
exit /b 0
