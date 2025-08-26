@echo off
REM URL Filter System Installation Script
REM Provides easy installation and management of the URL Filter System

setlocal enabledelayedexpansion

echo ========================================
echo URL Filter System Installation Script
echo ========================================
echo.

REM Check for administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator.
    echo Please right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Set paths
set SCRIPT_DIR=%~dp0
set INSTALLER_EXE=%SCRIPT_DIR%..\windows\Installer\UrlFilterInstaller.exe
set SERVICE_EXE=%SCRIPT_DIR%..\windows\UrlFilterService\UrlFilterService.exe
set DEVTOOLS_EXE=%SCRIPT_DIR%..\windows\DevTools\DevCommandLine.exe

echo Script directory: %SCRIPT_DIR%
echo.

REM Check if installer exists
if not exist "%INSTALLER_EXE%" (
    echo ERROR: Installer not found at: %INSTALLER_EXE%
    echo Please build the project first.
    pause
    exit /b 1
)

REM Parse command line arguments
set COMMAND=%1
if "%COMMAND%"=="" set COMMAND=install

echo Command: %COMMAND%
echo.

if /i "%COMMAND%"=="install" goto :install
if /i "%COMMAND%"=="uninstall" goto :uninstall
if /i "%COMMAND%"=="repair" goto :repair
if /i "%COMMAND%"=="status" goto :status
if /i "%COMMAND%"=="reset" goto :reset
if /i "%COMMAND%"=="test" goto :test
if /i "%COMMAND%"=="debug" goto :debug
if /i "%COMMAND%"=="logs" goto :logs
if /i "%COMMAND%"=="reverse" goto :reverse
if /i "%COMMAND%"=="help" goto :help

echo ERROR: Unknown command: %COMMAND%
goto :help

:install
echo Installing URL Filter System...
echo.
"%INSTALLER_EXE%" install
if %errorLevel% equ 0 (
    echo.
    echo ========================================
    echo Installation completed successfully!
    echo ========================================
    echo.
    echo The URL Filter Service is now running and monitoring network traffic.
    echo.
    echo Next steps:
    echo 1. Configure admin users
    echo 2. Set up URL filtering rules
    echo 3. Configure user time restrictions
    echo.
    echo Use "install.bat status" to check system status
    echo Use "install.bat help" to see all available commands
) else (
    echo.
    echo ========================================
    echo Installation FAILED!
    echo ========================================
    echo Please check the error messages above and try again.
    echo.
    echo Common issues:
    echo - Make sure you're running as Administrator
    echo - Check that .NET Framework 4.7.2+ is installed
    echo - Verify Windows version compatibility
    echo.
    echo Use "install.bat help" for more options
)
goto :end

:uninstall
echo Uninstalling URL Filter System...
echo.
echo WARNING: This will remove the URL Filter System completely!
set /p CONFIRM="Continue? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Uninstall cancelled.
    goto :end
)
echo.
"%INSTALLER_EXE%" uninstall
if %errorLevel% equ 0 (
    echo.
    echo ========================================
    echo Uninstallation completed successfully!
    echo ========================================
    echo.
    echo The URL Filter System has been completely removed.
    echo User data has been preserved in C:\ProgramData\UrlFilter
) else (
    echo.
    echo Uninstallation encountered errors. Please check the messages above.
)
goto :end

:repair
echo Repairing URL Filter System...
echo.
"%INSTALLER_EXE%" repair
goto :end

:status
echo Checking URL Filter System status...
echo.
"%INSTALLER_EXE%" status
echo.
if exist "%DEVTOOLS_EXE%" (
    echo Getting detailed system information...
    echo.
    "%DEVTOOLS_EXE%" monitor
)
goto :end

:reset
echo Resetting URL Filter System to defaults...
echo.
echo WARNING: This will remove all configuration and user data!
set /p CONFIRM="Continue? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Reset cancelled.
    goto :end
)
echo.
if exist "%DEVTOOLS_EXE%" (
    "%DEVTOOLS_EXE%" reset all
) else (
    "%INSTALLER_EXE%" reset
)
goto :end

:test
echo Running URL Filter System tests...
echo.
if exist "%DEVTOOLS_EXE%" (
    "%DEVTOOLS_EXE%" test all
) else (
    echo ERROR: Development tools not found. Please build the DevTools project.
    echo Test functionality requires DevCommandLine.exe
)
goto :end

:debug
echo Starting debug mode...
echo.
if exist "%DEVTOOLS_EXE%" (
    "%DEVTOOLS_EXE%" debug
) else (
    echo ERROR: Development tools not found. Please build the DevTools project.
    echo Debug functionality requires DevCommandLine.exe
)
goto :end

:logs
echo Showing recent logs...
echo.
if exist "%DEVTOOLS_EXE%" (
    "%DEVTOOLS_EXE%" logs recent 100
) else (
    echo Opening log directory...
    if exist "C:\ProgramData\UrlFilter\Logs" (
        explorer "C:\ProgramData\UrlFilter\Logs"
    ) else (
        echo Log directory not found. System may not be installed.
    )
)
goto :end

:reverse
echo ========================================
echo SYSTEM REVERSAL WARNING
echo ========================================
echo.
echo This operation will COMPLETELY REMOVE the URL Filter System
echo and restore your system to its original state.
echo.
echo This includes:
echo - Stopping and removing the Windows service
echo - Removing all WFP network filters  
echo - Removing all firewall rules
echo - Removing all registry entries
echo - Removing all files and directories
echo - Removing all certificates
echo.
echo THIS OPERATION CANNOT BE UNDONE!
echo.
set /p CONFIRM1="Are you absolutely sure? (type YES): "
if not "%CONFIRM1%"=="YES" (
    echo Reversal cancelled.
    goto :end
)
echo.
set /p CONFIRM2="Final confirmation - type REVERSE to proceed: "
if not "%CONFIRM2%"=="REVERSE" (
    echo Reversal cancelled.
    goto :end
)
echo.
echo Starting complete system reversal...
if exist "%DEVTOOLS_EXE%" (
    "%DEVTOOLS_EXE%" reverse
) else (
    echo WARNING: Development tools not found.
    echo Attempting basic uninstall instead...
    "%INSTALLER_EXE%" uninstall
)
goto :end

:help
echo.
echo URL Filter System Management Script
echo ===================================
echo.
echo Usage: install.bat [command]
echo.
echo Commands:
echo   install     - Install the URL Filter System (default)
echo   uninstall   - Uninstall the URL Filter System
echo   repair      - Repair the installation
echo   status      - Show system status and health
echo   reset       - Reset system to defaults (removes user data)
echo   test        - Run system tests
echo   debug       - Start debug mode with verbose logging
echo   logs        - Show recent system logs
echo   reverse     - DESTRUCTIVE: Complete system removal
echo   help        - Show this help message
echo.
echo Examples:
echo   install.bat                 - Install the system
echo   install.bat status          - Check system status  
echo   install.bat test            - Run comprehensive tests
echo   install.bat logs            - View recent logs
echo   install.bat reset           - Reset to factory defaults
echo   install.bat reverse         - Complete system removal
echo.
echo Notes:
echo - This script must be run as Administrator
echo - The "reverse" command is for development/testing only
echo - User data is preserved during normal uninstall
echo - Some commands require the DevTools to be built
echo.
goto :end

:end
echo.
if not "%COMMAND%"=="help" (
    echo Run "install.bat help" for more options.
)
echo.
pause
endlocal