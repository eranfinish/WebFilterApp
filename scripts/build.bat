@echo off
REM URL Filter System Build Script
REM Compiles all components and prepares for deployment

setlocal enabledelayedexpansion

echo ========================================
echo URL Filter System Build Script
echo ========================================
echo.

REM Set build configuration
set BUILD_CONFIG=Release
set DOTNET_VERSION=net6.0
set SCRIPT_DIR=%~dp0
set PROJECT_ROOT=%SCRIPT_DIR%..
set OUTPUT_DIR=%PROJECT_ROOT%\build
set LOGS_DIR=%OUTPUT_DIR%\logs

REM Parse command line arguments
if /i "%1"=="debug" set BUILD_CONFIG=Debug
if /i "%1"=="clean" goto :clean
if /i "%1"=="help" goto :help

echo Build Configuration: %BUILD_CONFIG%
echo Project Root: %PROJECT_ROOT%
echo Output Directory: %OUTPUT_DIR%
echo.

REM Create output directories
echo Creating output directories...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
if not exist "%LOGS_DIR%" mkdir "%LOGS_DIR%"

REM Initialize build log
set BUILD_LOG=%LOGS_DIR%\build_%date:~-4,4%%date:~-10,2%%date:~-7,2%_%time:~0,2%%time:~3,2%%time:~6,2%.log
set BUILD_LOG=%BUILD_LOG: =0%
echo Build started at %date% %time% > "%BUILD_LOG%"
echo Configuration: %BUILD_CONFIG% >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"

echo Build log: %BUILD_LOG%
echo.

REM Check prerequisites
call :check_prerequisites
if %errorlevel% neq 0 exit /b %errorlevel%

REM Build Windows components
call :build_windows_components
if %errorlevel% neq 0 exit /b %errorlevel%

REM Build Android components (if Android SDK available)
call :build_android_components

REM Copy shared components
call :copy_shared_components

REM Create deployment packages
call :create_deployment_packages

REM Run tests
if /i "%2"=="test" call :run_tests

echo.
echo ========================================
echo Build Summary
echo ========================================
call :show_build_summary

echo.
echo Build completed successfully!
echo Output directory: %OUTPUT_DIR%
echo Build log: %BUILD_LOG%
echo.
pause
goto :end

:check_prerequisites
echo Checking build prerequisites...
echo Checking build prerequisites... >> "%BUILD_LOG%"

REM Check for .NET SDK
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found. Please install .NET 6.0 SDK or later.
    echo ERROR: .NET SDK not found >> "%BUILD_LOG%"
    exit /b 1
)

for /f "tokens=*" %%i in ('dotnet --version') do set DOTNET_VERSION_INSTALLED=%%i
echo Found .NET SDK version: %DOTNET_VERSION_INSTALLED%
echo Found .NET SDK version: %DOTNET_VERSION_INSTALLED% >> "%BUILD_LOG%"

REM Check for MSBuild
where msbuild >nul 2>&1
if %errorlevel% neq 0 (
    echo WARNING: MSBuild not found in PATH. Some components may not build.
    echo WARNING: MSBuild not found in PATH >> "%BUILD_LOG%"
)

REM Check for Android SDK (optional)
where android >nul 2>&1
if %errorlevel% equ 0 (
    echo Found Android SDK
    echo Found Android SDK >> "%BUILD_LOG%"
    set ANDROID_SDK_AVAILABLE=1
) else (
    echo Android SDK not found - Android build will be skipped
    echo Android SDK not found >> "%BUILD_LOG%"
    set ANDROID_SDK_AVAILABLE=0
)

echo Prerequisites check completed.
echo Prerequisites check completed. >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"
exit /b 0

:build_windows_components
echo.
echo Building Windows components...
echo Building Windows components... >> "%BUILD_LOG%"

REM Build Common Library first
echo [0/5] Building Common Library...
echo [0/5] Building Common Library... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\Common"
dotnet build --configuration %BUILD_CONFIG% >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to build Common Library
    echo ERROR: Failed to build Common Library >> "%BUILD_LOG%"
    popd
    exit /b 1
)
popd
echo   ✓ Common Library built successfully

REM Build URL Filter Service
echo [1/5] Building URL Filter Service...
echo [1/5] Building URL Filter Service... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\UrlFilterService"
dotnet build --configuration %BUILD_CONFIG% --output "%OUTPUT_DIR%\Service" --verbosity detailed >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to build URL Filter Service
    echo ERROR: Failed to build URL Filter Service >> "%BUILD_LOG%"
    echo.
    echo Last 20 lines of build errors:
    powershell -Command "Get-Content '%BUILD_LOG%' | Select-Object -Last 20"
    popd
    exit /b 1
)
popd
echo   ✓ URL Filter Service built successfully

REM Build WFP Integration
echo [2/5] Building WFP Integration...
echo [2/5] Building WFP Integration... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\WFPIntegration"
dotnet build --configuration %BUILD_CONFIG% --output "%OUTPUT_DIR%\WFP" --verbosity detailed >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to build WFP Integration
    echo ERROR: Failed to build WFP Integration >> "%BUILD_LOG%"
    echo.
    echo Last 20 lines of build errors:
    powershell -Command "Get-Content '%BUILD_LOG%' | Select-Object -Last 20"
    popd
    exit /b 1
)
popd
echo   ✓ WFP Integration built successfully

REM Build Admin Control Panel
echo [3/5] Building Admin Control Panel...
echo [3/5] Building Admin Control Panel... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\AdminControlPanel"
dotnet build --configuration %BUILD_CONFIG% --output "%OUTPUT_DIR%\AdminPanel" >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to build Admin Control Panel
    echo ERROR: Failed to build Admin Control Panel >> "%BUILD_LOG%"
    popd
    exit /b 1
)
popd
echo   ✓ Admin Control Panel built successfully

REM Build Installer
echo [4/5] Building Installer...
echo [4/5] Building Installer... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\Installer"
dotnet build --configuration %BUILD_CONFIG% --output "%OUTPUT_DIR%\Installer" >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to build Installer
    echo ERROR: Failed to build Installer >> "%BUILD_LOG%"
    popd
    exit /b 1
)
popd
echo   ✓ Installer built successfully

REM Build Development Tools
echo [5/5] Building Development Tools...
echo [5/5] Building Development Tools... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\windows\DevTools"
dotnet build --configuration %BUILD_CONFIG% --output "%OUTPUT_DIR%\DevTools" >> "%BUILD_LOG%" 2>&1
if %errorlevel% neq 0 (
    echo WARNING: Failed to build Development Tools
    echo WARNING: Failed to build Development Tools >> "%BUILD_LOG%"
) else (
    echo   ✓ Development Tools built successfully
)
popd

echo Windows components build completed.
echo Windows components build completed. >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"
exit /b 0

:build_android_components
echo.
if %ANDROID_SDK_AVAILABLE% neq 1 (
    echo Skipping Android build - SDK not available
    echo Skipping Android build - SDK not available >> "%BUILD_LOG%"
    exit /b 0
)

echo Building Android components...
echo Building Android components... >> "%BUILD_LOG%"

REM Build Android APK
echo [1/1] Building Android APK...
echo [1/1] Building Android APK... >> "%BUILD_LOG%"
pushd "%PROJECT_ROOT%\android"

REM Check for gradle wrapper
if exist "gradlew.bat" (
    call gradlew.bat assembleRelease >> "%BUILD_LOG%" 2>&1
) else (
    gradle assembleRelease >> "%BUILD_LOG%" 2>&1
)

if %errorlevel% neq 0 (
    echo WARNING: Failed to build Android APK
    echo WARNING: Failed to build Android APK >> "%BUILD_LOG%"
    popd
    exit /b 0
)

REM Copy APK to output directory
if not exist "%OUTPUT_DIR%\Android" mkdir "%OUTPUT_DIR%\Android"
if exist "app\build\outputs\apk\release\app-release.apk" (
    copy "app\build\outputs\apk\release\app-release.apk" "%OUTPUT_DIR%\Android\" >> "%BUILD_LOG%" 2>&1
    echo   ✓ Android APK built successfully
) else (
    echo WARNING: Android APK not found after build
    echo WARNING: Android APK not found after build >> "%BUILD_LOG%"
)

popd
echo Android components build completed.
echo Android components build completed. >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"
exit /b 0

:copy_shared_components
echo.
echo Copying shared components...
echo Copying shared components... >> "%BUILD_LOG%"

REM Copy database schema
if not exist "%OUTPUT_DIR%\Shared" mkdir "%OUTPUT_DIR%\Shared"
if not exist "%OUTPUT_DIR%\Shared\Database" mkdir "%OUTPUT_DIR%\Shared\Database"
copy "%PROJECT_ROOT%\shared\database-schema\*.sql" "%OUTPUT_DIR%\Shared\Database\" >> "%BUILD_LOG%" 2>&1

REM Copy configuration files
if not exist "%OUTPUT_DIR%\Shared\Config" mkdir "%OUTPUT_DIR%\Shared\Config"
if exist "%PROJECT_ROOT%\shared\configuration\*.json" (
    copy "%PROJECT_ROOT%\shared\configuration\*.json" "%OUTPUT_DIR%\Shared\Config\" >> "%BUILD_LOG%" 2>&1
)

REM Copy scripts
copy "%PROJECT_ROOT%\scripts\*.bat" "%OUTPUT_DIR%\" >> "%BUILD_LOG%" 2>&1

echo Shared components copied.
echo Shared components copied. >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"
exit /b 0

:create_deployment_packages
echo.
echo Creating deployment packages...
echo Creating deployment packages... >> "%BUILD_LOG%"

REM Create Windows deployment package
echo Creating Windows deployment package...
if exist "%OUTPUT_DIR%\UrlFilter-Windows.zip" del "%OUTPUT_DIR%\UrlFilter-Windows.zip"

pushd "%OUTPUT_DIR%"
powershell -Command "Compress-Archive -Path Service, AdminPanel, Installer, DevTools, Shared, *.bat -DestinationPath UrlFilter-Windows.zip" >> "%BUILD_LOG%" 2>&1
if %errorlevel% equ 0 (
    echo   ✓ Windows deployment package created: UrlFilter-Windows.zip
) else (
    echo WARNING: Failed to create Windows deployment package
    echo WARNING: Failed to create Windows deployment package >> "%BUILD_LOG%"
)
popd

REM Create Android deployment package (if built)
if exist "%OUTPUT_DIR%\Android" (
    echo Creating Android deployment package...
    pushd "%OUTPUT_DIR%"
    powershell -Command "Compress-Archive -Path Android -DestinationPath UrlFilter-Android.zip" >> "%BUILD_LOG%" 2>&1
    if %errorlevel% equ 0 (
        echo   ✓ Android deployment package created: UrlFilter-Android.zip
    ) else (
        echo WARNING: Failed to create Android deployment package
    )
    popd
)

echo Deployment packages created.
echo Deployment packages created. >> "%BUILD_LOG%"
echo. >> "%BUILD_LOG%"
exit /b 0

:run_tests
echo.
echo Running tests...
echo Running tests... >> "%BUILD_LOG%"

if exist "%OUTPUT_DIR%\DevTools\DevCommandLine.exe" (
    echo Running comprehensive system tests...
    "%OUTPUT_DIR%\DevTools\DevCommandLine.exe" test all >> "%BUILD_LOG%" 2>&1
    if %errorlevel% equ 0 (
        echo   ✓ All tests passed
    ) else (
        echo WARNING: Some tests failed - check build log for details
        echo WARNING: Some tests failed >> "%BUILD_LOG%"
    )
) else (
    echo WARNING: Development tools not available - skipping tests
    echo WARNING: Development tools not available - skipping tests >> "%BUILD_LOG%"
)

echo. >> "%BUILD_LOG%"
exit /b 0

:show_build_summary
echo Components built:
if exist "%OUTPUT_DIR%\Service" echo   ✓ URL Filter Service
if exist "%OUTPUT_DIR%\AdminPanel" echo   ✓ Admin Control Panel  
if exist "%OUTPUT_DIR%\Installer" echo   ✓ System Installer
if exist "%OUTPUT_DIR%\DevTools" echo   ✓ Development Tools
if exist "%OUTPUT_DIR%\Android" echo   ✓ Android Application
echo.
echo Deployment packages:
if exist "%OUTPUT_DIR%\UrlFilter-Windows.zip" echo   ✓ Windows Package (UrlFilter-Windows.zip)
if exist "%OUTPUT_DIR%\UrlFilter-Android.zip" echo   ✓ Android Package (UrlFilter-Android.zip)
echo.
echo Output files location: %OUTPUT_DIR%

REM Calculate total size
for /f "tokens=3" %%a in ('dir "%OUTPUT_DIR%" /-c ^| findstr "File(s)"') do set TOTAL_SIZE=%%a
if defined TOTAL_SIZE (
    echo Total build size: %TOTAL_SIZE% bytes
)

exit /b 0

:clean
echo Cleaning build outputs...
if exist "%OUTPUT_DIR%" (
    rmdir /s /q "%OUTPUT_DIR%"
    echo Build directory cleaned.
) else (
    echo Build directory already clean.
)

REM Clean intermediate files
pushd "%PROJECT_ROOT%"
for /d /r . %%d in (bin,obj) do @if exist "%%d" rmdir /s /q "%%d"
echo Intermediate files cleaned.
popd

echo Clean completed.
goto :end

:help
echo.
echo URL Filter System Build Script
echo ==============================
echo.
echo Usage: build.bat [options]
echo.
echo Options:
echo   (none)    - Build in Release configuration
echo   debug     - Build in Debug configuration  
echo   clean     - Clean all build outputs
echo   help      - Show this help
echo.
echo Additional Parameters:
echo   test      - Run tests after build (e.g., build.bat release test)
echo.
echo Examples:
echo   build.bat              - Standard release build
echo   build.bat debug        - Debug build
echo   build.bat release test - Release build with tests
echo   build.bat clean        - Clean build outputs
echo.
echo Prerequisites:
echo   - .NET 6.0 SDK or later
echo   - MSBuild (optional, for enhanced building)
echo   - Android SDK (optional, for Android build)
echo   - PowerShell (for package creation)
echo.
echo Output:
echo   All build outputs are placed in the 'build' directory
echo   Deployment packages are created as ZIP files
echo   Build logs are saved in 'build\logs'
echo.
goto :end

:end
endlocal