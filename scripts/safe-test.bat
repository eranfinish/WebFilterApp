@echo off
REM Safe Testing Script - Test URL Filter without blocking internet
REM This script provides safe testing modes that won't disrupt your connection

setlocal enabledelayedexpansion

echo ========================================
echo URL Filter Safe Testing Mode
echo ========================================
echo.

echo This script allows you to test URL Filter functionality
echo WITHOUT blocking your actual internet connection.
echo.

set SCRIPT_DIR=%~dp0
set DEVTOOLS_EXE=%SCRIPT_DIR%..\windows\DevTools\DevCommandLine.exe

if not exist "%DEVTOOLS_EXE%" (
    echo ERROR: Development tools not found. Please build the project first.
    echo Run: scripts\build.bat
    pause
    exit /b 1
)

:menu
echo Safe Testing Options:
echo =====================
echo.
echo 1. Monitor Mode Only (No Blocking)
echo 2. Log-Only Mode (Capture but don't block)
echo 3. Test Mode (Block test domains only)
echo 4. Simulation Mode (Generate fake traffic)
echo 5. Database Testing (No network impact)
echo 6. Service Testing (Dry run mode)
echo 7. View Test Results
echo 8. Reset Test Environment
echo 9. Exit
echo.
set /p CHOICE="Select testing mode (1-9): "

if "%CHOICE%"=="1" goto :monitor_mode
if "%CHOICE%"=="2" goto :log_mode
if "%CHOICE%"=="3" goto :test_mode
if "%CHOICE%"=="4" goto :simulation_mode
if "%CHOICE%"=="5" goto :database_test
if "%CHOICE%"=="6" goto :service_test
if "%CHOICE%"=="7" goto :view_results
if "%CHOICE%"=="8" goto :reset_test
if "%CHOICE%"=="9" goto :end
echo Invalid choice. Please try again.
goto :menu

:monitor_mode
echo.
echo ========================================
echo Monitor Mode (No Blocking)
echo ========================================
echo.
echo This mode will monitor network traffic but NOT block anything.
echo Your internet connection will work normally.
echo.
echo Starting monitor mode...
echo Press Ctrl+C to stop monitoring
echo.

REM Set environment variable to enable monitor-only mode
set URLFILTER_MODE=MONITOR_ONLY
"%DEVTOOLS_EXE%" config set test_mode monitor_only
"%DEVTOOLS_EXE%" monitor

goto :menu

:log_mode
echo.
echo ========================================
echo Log-Only Mode (Capture but don't block)
echo ========================================
echo.
echo This mode will log all traffic for analysis but won't block anything.
echo Perfect for understanding traffic patterns without disruption.
echo.
set /p DURATION="Enter monitoring duration in seconds (default 60): "
if "%DURATION%"=="" set DURATION=60

echo Starting log-only mode for %DURATION% seconds...
echo Your internet will work normally while logging traffic.
echo.

REM Configure log-only mode
"%DEVTOOLS_EXE%" config set test_mode log_only
"%DEVTOOLS_EXE%" config set test_duration %DURATION%

REM Start monitoring
"%DEVTOOLS_EXE%" debug

echo.
echo Log-only mode completed. Check logs for captured traffic data.
pause
goto :menu

:test_mode
echo.
echo ========================================
echo Safe Test Mode (Test domains only)
echo ========================================
echo.
echo This mode only blocks safe test domains that won't affect your browsing:
echo - test.example.com
echo - blocked.test.local  
echo - safe-test-domain.com
echo.
echo Your normal internet browsing will be UNAFFECTED.
echo.
set /p CONFIRM="Continue with safe test mode? (y/N): "
if /i not "%CONFIRM%"=="y" goto :menu

echo Configuring safe test domains...

REM Add safe test domains to database
"%DEVTOOLS_EXE%" config set test_mode safe_domains
"%DEVTOOLS_EXE%" database exec "INSERT OR IGNORE INTO url_lists (url_pattern, list_type, category, description) VALUES ('test.example.com', 'blacklist', 'testing', 'Safe test domain')"
"%DEVTOOLS_EXE%" database exec "INSERT OR IGNORE INTO url_lists (url_pattern, list_type, category, description) VALUES ('blocked.test.local', 'blacklist', 'testing', 'Safe test domain')"  
"%DEVTOOLS_EXE%" database exec "INSERT OR IGNORE INTO url_lists (url_pattern, list_type, category, description) VALUES ('safe-test-domain.com', 'blacklist', 'testing', 'Safe test domain')"

echo Safe test mode configured. Only test domains will be blocked.
echo Test by visiting: http://test.example.com (should be blocked)
echo Normal sites like google.com will work fine.
echo.
echo Starting service in test mode...
"%DEVTOOLS_EXE%" service start

echo.
echo Test mode active. Your normal internet is safe!
echo Press any key to stop test mode...
pause

echo Stopping test mode...
"%DEVTOOLS_EXE%" service stop
"%DEVTOOLS_EXE%" database exec "DELETE FROM url_lists WHERE category = 'testing'"

goto :menu

:simulation_mode
echo.
echo ========================================
echo Traffic Simulation Mode
echo ========================================
echo.
echo This generates simulated network traffic for testing
echo WITHOUT affecting your real internet connection.
echo.
echo Simulation types:
echo 1. Basic web traffic
echo 2. YouTube traffic patterns  
echo 3. Social media traffic
echo 4. Mixed traffic simulation
echo.
set /p SIM_TYPE="Select simulation type (1-4): "
set /p SIM_DURATION="Duration in seconds (default 30): "
if "%SIM_DURATION%"=="" set SIM_DURATION=30

if "%SIM_TYPE%"=="1" set SIM_NAME=basic
if "%SIM_TYPE%"=="2" set SIM_NAME=youtube
if "%SIM_TYPE%"=="3" set SIM_NAME=social
if "%SIM_TYPE%"=="4" set SIM_NAME=mixed

echo.
echo Generating %SIM_NAME% traffic simulation for %SIM_DURATION% seconds...
echo This creates fake traffic data for testing - no real network impact.
echo.

"%DEVTOOLS_EXE%" simulate %SIM_NAME% %SIM_DURATION%

echo.
echo Simulation completed. Check logs for generated traffic data.
pause
goto :menu

:database_test
echo.
echo ========================================
echo Database Testing (No Network Impact)
echo ========================================
echo.
echo Testing database functionality without any network changes.
echo This is completely safe and won't affect internet connectivity.
echo.

echo Running database tests...
"%DEVTOOLS_EXE%" test database

echo.
echo Testing URL pattern matching (offline)...
"%DEVTOOLS_EXE%" test filtering

echo.
echo Testing user management...
"%DEVTOOLS_EXE%" test auth

echo Database testing completed.
pause
goto :menu

:service_test
echo.
echo ========================================
echo Service Testing (Dry Run Mode)
echo ========================================
echo.
echo Testing service functionality without actually filtering traffic.
echo Service will start in dry-run mode - no blocking will occur.
echo.

echo Starting service in dry-run mode...
"%DEVTOOLS_EXE%" config set dry_run_mode true
"%DEVTOOLS_EXE%" test service

echo.
echo Service testing completed in dry-run mode.
echo No actual traffic filtering occurred.
"%DEVTOOLS_EXE%" config set dry_run_mode false
pause
goto :menu

:view_results
echo.
echo ========================================
echo Test Results
echo ========================================
echo.

echo Recent test logs:
"%DEVTOOLS_EXE%" logs recent 50

echo.
echo Test statistics:
"%DEVTOOLS_EXE%" database analyze

echo.
echo System status:
"%DEVTOOLS_EXE%" monitor

pause
goto :menu

:reset_test
echo.
echo ========================================
echo Reset Test Environment
echo ========================================
echo.
echo This will reset the test environment to defaults
echo without affecting your system configuration.
echo.
set /p CONFIRM="Reset test environment? (y/N): "
if /i not "%CONFIRM%"=="y" goto :menu

echo Resetting test environment...
"%DEVTOOLS_EXE%" config set test_mode normal
"%DEVTOOLS_EXE%" config set dry_run_mode false
"%DEVTOOLS_EXE%" database exec "DELETE FROM url_lists WHERE category = 'testing'"
"%DEVTOOLS_EXE%" reset logs

echo Test environment reset completed.
pause
goto :menu

:end
echo.
echo Safe testing session ended.
echo Your internet connection was protected throughout testing.
echo.
pause
endlocal