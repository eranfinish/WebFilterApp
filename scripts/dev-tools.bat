@echo off
REM URL Filter Development Tools Script
REM Provides easy access to development and testing functionality

setlocal enabledelayedexpansion

echo ========================================
echo URL Filter Development Tools
echo ========================================
echo.

REM Set paths
set SCRIPT_DIR=%~dp0
set DEVTOOLS_EXE=%SCRIPT_DIR%..\windows\DevTools\DevCommandLine.exe
set SERVICE_EXE=%SCRIPT_DIR%..\windows\UrlFilterService\UrlFilterService.exe

REM Check if development tools exist
if not exist "%DEVTOOLS_EXE%" (
    echo ERROR: Development tools not found at: %DEVTOOLS_EXE%
    echo Please build the DevTools project first.
    echo.
    echo Build command: dotnet build ..\windows\DevTools\
    pause
    exit /b 1
)

REM Parse command line arguments
set COMMAND=%1
set SUBCOMMAND=%2
set PARAM3=%3
set PARAM4=%4

if "%COMMAND%"=="" (
    echo No command specified. Showing interactive menu...
    goto :menu
)

echo Command: %COMMAND% %SUBCOMMAND% %PARAM3% %PARAM4%
echo.

if /i "%COMMAND%"=="test" goto :test
if /i "%COMMAND%"=="debug" goto :debug
if /i "%COMMAND%"=="logs" goto :logs
if /i "%COMMAND%"=="reset" goto :reset
if /i "%COMMAND%"=="simulate" goto :simulate
if /i "%COMMAND%"=="benchmark" goto :benchmark
if /i "%COMMAND%"=="config" goto :config
if /i "%COMMAND%"=="database" goto :database
if /i "%COMMAND%"=="monitor" goto :monitor
if /i "%COMMAND%"=="export" goto :export
if /i "%COMMAND%"=="import" goto :import
if /i "%COMMAND%"=="reverse" goto :reverse
if /i "%COMMAND%"=="service" goto :service
if /i "%COMMAND%"=="menu" goto :menu
if /i "%COMMAND%"=="help" goto :help

echo ERROR: Unknown command: %COMMAND%
goto :help

:menu
echo Interactive Development Menu
echo ============================
echo.
echo Select an option:
echo 1. Run Tests
echo 2. Start Debug Mode  
echo 3. View Logs
echo 4. Reset System
echo 5. Simulate Traffic
echo 6. Run Benchmarks
echo 7. Monitor System
echo 8. Export Data
echo 9. Service Control
echo 0. Help
echo X. Exit
echo.
set /p CHOICE="Enter choice: "

if "%CHOICE%"=="1" goto :test_menu
if "%CHOICE%"=="2" goto :debug
if "%CHOICE%"=="3" goto :logs_menu
if "%CHOICE%"=="4" goto :reset_menu
if "%CHOICE%"=="5" goto :simulate_menu
if "%CHOICE%"=="6" goto :benchmark
if "%CHOICE%"=="7" goto :monitor
if "%CHOICE%"=="8" goto :export_menu
if "%CHOICE%"=="9" goto :service_menu
if "%CHOICE%"=="0" goto :help
if /i "%CHOICE%"=="x" goto :end
echo Invalid choice. Please try again.
goto :menu

:test_menu
echo.
echo Test Menu
echo =========
echo 1. All Tests
echo 2. Service Tests
echo 3. Filtering Tests
echo 4. Database Tests
echo 5. Authentication Tests
echo 6. WFP Tests
echo 0. Back to Main Menu
echo.
set /p TEST_CHOICE="Select test type: "

if "%TEST_CHOICE%"=="1" set SUBCOMMAND=all
if "%TEST_CHOICE%"=="2" set SUBCOMMAND=service
if "%TEST_CHOICE%"=="3" set SUBCOMMAND=filtering
if "%TEST_CHOICE%"=="4" set SUBCOMMAND=database
if "%TEST_CHOICE%"=="5" set SUBCOMMAND=auth
if "%TEST_CHOICE%"=="6" set SUBCOMMAND=wfp
if "%TEST_CHOICE%"=="0" goto :menu

if not "%SUBCOMMAND%"=="" goto :test
echo Invalid choice.
goto :test_menu

:logs_menu
echo.
echo Logs Menu
echo =========
echo 1. Recent Logs (100 lines)
echo 2. Error Logs
echo 3. Audit Logs
echo 4. Performance Logs
echo 5. Live Log Monitoring
echo 0. Back to Main Menu
echo.
set /p LOG_CHOICE="Select log type: "

if "%LOG_CHOICE%"=="1" set SUBCOMMAND=recent& set PARAM3=100
if "%LOG_CHOICE%"=="2" set SUBCOMMAND=errors& set PARAM3=50
if "%LOG_CHOICE%"=="3" set SUBCOMMAND=audit& set PARAM3=50
if "%LOG_CHOICE%"=="4" set SUBCOMMAND=performance& set PARAM3=50
if "%LOG_CHOICE%"=="5" set SUBCOMMAND=live
if "%LOG_CHOICE%"=="0" goto :menu

if not "%SUBCOMMAND%"=="" goto :logs
echo Invalid choice.
goto :logs_menu

:reset_menu
echo.
echo Reset Menu
echo ==========
echo WARNING: Reset operations will remove data!
echo.
echo 1. Reset All Data (DESTRUCTIVE)
echo 2. Reset Database Only
echo 3. Reset Configuration Only
echo 4. Reset Logs Only
echo 5. Reset Filters Only
echo 0. Back to Main Menu
echo.
set /p RESET_CHOICE="Select reset type: "

if "%RESET_CHOICE%"=="1" set SUBCOMMAND=all
if "%RESET_CHOICE%"=="2" set SUBCOMMAND=database
if "%RESET_CHOICE%"=="3" set SUBCOMMAND=config
if "%RESET_CHOICE%"=="4" set SUBCOMMAND=logs
if "%RESET_CHOICE%"=="5" set SUBCOMMAND=filters
if "%RESET_CHOICE%"=="0" goto :menu

if not "%SUBCOMMAND%"=="" goto :reset
echo Invalid choice.
goto :reset_menu

:simulate_menu
echo.
echo Traffic Simulation Menu
echo =======================
echo 1. Basic Traffic (60s)
echo 2. Burst Traffic (30s)
echo 3. YouTube Traffic (120s)
echo 4. Blocked Sites Traffic (60s)
echo 5. Custom Duration
echo 0. Back to Main Menu
echo.
set /p SIM_CHOICE="Select simulation type: "

if "%SIM_CHOICE%"=="1" set SUBCOMMAND=basic& set PARAM3=60
if "%SIM_CHOICE%"=="2" set SUBCOMMAND=burst& set PARAM3=30
if "%SIM_CHOICE%"=="3" set SUBCOMMAND=youtube& set PARAM3=120
if "%SIM_CHOICE%"=="4" set SUBCOMMAND=blocked& set PARAM3=60
if "%SIM_CHOICE%"=="5" goto :simulate_custom
if "%SIM_CHOICE%"=="0" goto :menu

if not "%SUBCOMMAND%"=="" goto :simulate
echo Invalid choice.
goto :simulate_menu

:simulate_custom
echo.
set /p SIM_TYPE="Enter simulation type (basic/burst/youtube/blocked): "
set /p SIM_DURATION="Enter duration in seconds: "
set SUBCOMMAND=%SIM_TYPE%
set PARAM3=%SIM_DURATION%
goto :simulate

:export_menu
echo.
echo Export Menu
echo ===========
echo 1. Export Database
echo 2. Export Configuration
echo 3. Export Logs
echo 4. Export All Data
echo 0. Back to Main Menu
echo.
set /p EXPORT_CHOICE="Select export type: "

if "%EXPORT_CHOICE%"=="1" set SUBCOMMAND=database
if "%EXPORT_CHOICE%"=="2" set SUBCOMMAND=config
if "%EXPORT_CHOICE%"=="3" set SUBCOMMAND=logs
if "%EXPORT_CHOICE%"=="4" set SUBCOMMAND=all
if "%EXPORT_CHOICE%"=="0" goto :menu

if not "%SUBCOMMAND%"=="" goto :export
echo Invalid choice.
goto :export_menu

:service_menu
echo.
echo Service Control Menu
echo ====================
echo 1. Start Service
echo 2. Stop Service
echo 3. Restart Service
echo 4. Service Status
echo 5. Console Mode (Testing)
echo 0. Back to Main Menu
echo.
set /p SERVICE_CHOICE="Select service operation: "

if "%SERVICE_CHOICE%"=="1" goto :service_start
if "%SERVICE_CHOICE%"=="2" goto :service_stop
if "%SERVICE_CHOICE%"=="3" goto :service_restart
if "%SERVICE_CHOICE%"=="4" goto :service_status
if "%SERVICE_CHOICE%"=="5" goto :service_console
if "%SERVICE_CHOICE%"=="0" goto :menu

echo Invalid choice.
goto :service_menu

REM Command implementations

:test
echo Running tests: %SUBCOMMAND%
echo.
if "%SUBCOMMAND%"=="" set SUBCOMMAND=all
"%DEVTOOLS_EXE%" test %SUBCOMMAND%
goto :end

:debug
echo Starting debug mode...
echo Press Ctrl+C to stop debug mode
echo.
"%DEVTOOLS_EXE%" debug
goto :end

:logs
echo Showing logs: %SUBCOMMAND%
echo.
if "%SUBCOMMAND%"=="" set SUBCOMMAND=recent
if "%PARAM3%"=="" set PARAM3=100
"%DEVTOOLS_EXE%" logs %SUBCOMMAND% %PARAM3%
if /i "%SUBCOMMAND%"=="live" (
    echo Press Ctrl+C to stop live monitoring
)
goto :end

:reset
echo Resetting system: %SUBCOMMAND%
echo.
echo WARNING: This operation will remove data!
set /p CONFIRM="Continue? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Reset cancelled.
    goto :end
)
if "%SUBCOMMAND%"=="" set SUBCOMMAND=all
"%DEVTOOLS_EXE%" reset %SUBCOMMAND%
goto :end

:simulate
echo Simulating traffic: %SUBCOMMAND%
echo.
if "%SUBCOMMAND%"=="" set SUBCOMMAND=basic
if "%PARAM3%"=="" set PARAM3=60
echo Simulating %SUBCOMMAND% traffic for %PARAM3% seconds...
"%DEVTOOLS_EXE%" simulate %SUBCOMMAND% %PARAM3%
goto :end

:benchmark
echo Running performance benchmarks...
echo This may take several minutes...
echo.
"%DEVTOOLS_EXE%" benchmark
goto :end

:config
echo Configuration management: %SUBCOMMAND%
echo.
"%DEVTOOLS_EXE%" config %SUBCOMMAND% %PARAM3% %PARAM4%
goto :end

:database
echo Database operations: %SUBCOMMAND%
echo.
"%DEVTOOLS_EXE%" database %SUBCOMMAND% %PARAM3%
goto :end

:monitor
echo Starting system monitor...
echo Press Ctrl+C to stop monitoring
echo.
"%DEVTOOLS_EXE%" monitor
goto :end

:export
echo Exporting data: %SUBCOMMAND%
echo.
if "%SUBCOMMAND%"=="" set SUBCOMMAND=all
if "%PARAM3%"=="" (
    set PARAM3=urlfilter-export-%date:~-4,4%%date:~-10,2%%date:~-7,2%-%time:~0,2%%time:~3,2%%time:~6,2%
    set PARAM3=!PARAM3: =0!
    set PARAM3=!PARAM3:.zip=!.zip
)
"%DEVTOOLS_EXE%" export %SUBCOMMAND% "%PARAM3%"
goto :end

:import
echo Importing data from: %SUBCOMMAND%
echo.
if "%SUBCOMMAND%"=="" (
    echo ERROR: No import file specified
    goto :end
)
"%DEVTOOLS_EXE%" import "%SUBCOMMAND%"
goto :end

:reverse
echo ========================================
echo COMPLETE SYSTEM REVERSAL
echo ========================================
echo.
echo WARNING: This will PERMANENTLY REMOVE the entire URL Filter System!
echo.
set /p CONFIRM1="Type YES to confirm: "
if not "%CONFIRM1%"=="YES" (
    echo Reversal cancelled.
    goto :end
)
echo.
echo Final confirmation required...
set /p CONFIRM2="Type REVERSE to proceed: "
if not "%CONFIRM2%"=="REVERSE" (
    echo Reversal cancelled.
    goto :end
)
echo.
"%DEVTOOLS_EXE%" reverse
goto :end

:service_start
echo Starting URL Filter Service...
net start UrlFilterService
goto :end

:service_stop
echo Stopping URL Filter Service...
net stop UrlFilterService
goto :end

:service_restart
echo Restarting URL Filter Service...
net stop UrlFilterService
timeout /t 3 /nobreak >nul
net start UrlFilterService
goto :end

:service_status
echo URL Filter Service Status:
sc query UrlFilterService
goto :end

:service_console
echo Starting service in console mode...
echo Press Ctrl+C to stop
echo.
if exist "%SERVICE_EXE%" (
    "%SERVICE_EXE%" /console
) else (
    echo ERROR: Service executable not found at: %SERVICE_EXE%
    echo Please build the service project first.
)
goto :end

:help
echo.
echo URL Filter Development Tools
echo ============================
echo.
echo Usage: dev-tools.bat [command] [options]
echo.
echo Commands:
echo   test [type]           - Run tests (all, service, filtering, database, auth, wfp)
echo   debug                 - Start debug mode with verbose logging
echo   logs [type] [lines]   - Show logs (recent, errors, audit, performance, live)
echo   reset [type]          - Reset system (all, database, config, logs, filters)
echo   simulate [type] [sec] - Simulate traffic (basic, burst, youtube, blocked)
echo   benchmark             - Run performance benchmarks
echo   config [op] [args]    - Configuration management
echo   database [op] [args]  - Database operations
echo   monitor               - Real-time system monitoring
echo   export [type] [file]  - Export data
echo   import [file]         - Import data
echo   service [op]          - Service control (start, stop, restart, status)
echo   reverse               - Complete system removal (DESTRUCTIVE)
echo   menu                  - Show interactive menu
echo   help                  - Show this help
echo.
echo Examples:
echo   dev-tools.bat                    - Show interactive menu
echo   dev-tools.bat test all           - Run all tests
echo   dev-tools.bat logs live          - Monitor logs in real-time
echo   dev-tools.bat simulate youtube   - Simulate YouTube traffic
echo   dev-tools.bat monitor            - Monitor system performance
echo   dev-tools.bat export database    - Export database
echo.
echo Interactive Menu:
echo   Run without arguments to access the interactive menu system
echo   which provides guided access to all functionality.
echo.
goto :end

:end
if not "%COMMAND%"=="menu" if not "%COMMAND%"=="help" (
    echo.
    echo Run "dev-tools.bat menu" for interactive access
    echo Run "dev-tools.bat help" for command reference
)
echo.
if "%COMMAND%"=="menu" (
    goto :menu
) else (
    pause
)
endlocal