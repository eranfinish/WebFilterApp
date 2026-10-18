@echo off
REM URL Filter System - Database Creation Script
REM Creates the SQLite database file from the SQL schema

setlocal enabledelayedexpansion

echo ============================================
echo URL Filter Database Generator
echo ============================================
echo.

REM Get the directory where this script is located
set "SCRIPT_DIR=%~dp0"
set "SQL_FILE=%SCRIPT_DIR%urlfilter.sql"
set "DB_FILE=%SCRIPT_DIR%urlfilter.db"
set "INSTALL_DIR=C:\ProgramData\UrlFilterSystem"

REM Check if SQL file exists
if not exist "%SQL_FILE%" (
    echo ERROR: SQL schema file not found: %SQL_FILE%
    exit /b 1
)

REM Check for sqlite3.exe in common locations
set "SQLITE_EXE="

REM Check current directory
if exist "%SCRIPT_DIR%sqlite3.exe" (
    set "SQLITE_EXE=%SCRIPT_DIR%sqlite3.exe"
    goto :found_sqlite
)

REM Check PATH
where sqlite3.exe >nul 2>&1
if %errorlevel% equ 0 (
    set "SQLITE_EXE=sqlite3"
    goto :found_sqlite
)

REM Check common install locations
if exist "C:\sqlite\sqlite3.exe" (
    set "SQLITE_EXE=C:\sqlite\sqlite3.exe"
    goto :found_sqlite
)

if exist "%ProgramFiles%\sqlite\sqlite3.exe" (
    set "SQLITE_EXE=%ProgramFiles%\sqlite\sqlite3.exe"
    goto :found_sqlite
)

if exist "%LOCALAPPDATA%\Programs\sqlite\sqlite3.exe" (
    set "SQLITE_EXE=%LOCALAPPDATA%\Programs\sqlite\sqlite3.exe"
    goto :found_sqlite
)

REM SQLite not found - use .NET alternative
echo SQLite CLI not found. Using .NET SQLite alternative...
goto :use_dotnet

:found_sqlite
echo Found SQLite at: %SQLITE_EXE%
echo.

REM Remove existing database if it exists
if exist "%DB_FILE%" (
    echo Removing existing database...
    del /f "%DB_FILE%"
)

echo Creating database from schema...
"%SQLITE_EXE%" "%DB_FILE%" < "%SQL_FILE%"

if %errorlevel% neq 0 (
    echo ERROR: Failed to create database
    exit /b 1
)

echo.
echo Database created successfully: %DB_FILE%

REM Verify the database
echo.
echo Verifying database tables...
"%SQLITE_EXE%" "%DB_FILE%" ".tables"

echo.
echo Table counts:
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'device_users: ' || COUNT(*) FROM device_users;"
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'admin_users: ' || COUNT(*) FROM admin_users;"
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'url_lists: ' || COUNT(*) FROM url_lists;"
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'youtube_channels: ' || COUNT(*) FROM youtube_channels;"
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'content_categories: ' || COUNT(*) FROM content_categories;"
"%SQLITE_EXE%" "%DB_FILE%" "SELECT 'admin_settings: ' || COUNT(*) FROM admin_settings;"

goto :copy_to_install

:use_dotnet
REM Create a simple C# script to create the database
set "CS_FILE=%SCRIPT_DIR%CreateDb.cs"
set "EXE_FILE=%SCRIPT_DIR%CreateDb.exe"

echo Creating database using .NET...

REM Check if we have dotnet
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Neither sqlite3.exe nor .NET SDK found.
    echo Please install one of the following:
    echo   1. SQLite CLI: https://www.sqlite.org/download.html
    echo   2. .NET SDK: https://dotnet.microsoft.com/download
    exit /b 1
)

REM Create a temporary .NET project
set "TEMP_PROJ=%SCRIPT_DIR%temp_dbcreator"
if exist "%TEMP_PROJ%" rmdir /s /q "%TEMP_PROJ%"
mkdir "%TEMP_PROJ%"

REM Create csproj
(
echo ^<Project Sdk="Microsoft.NET.Sdk"^>
echo   ^<PropertyGroup^>
echo     ^<OutputType^>Exe^</OutputType^>
echo     ^<TargetFramework^>net6.0^</TargetFramework^>
echo   ^</PropertyGroup^>
echo   ^<ItemGroup^>
echo     ^<PackageReference Include="Microsoft.Data.Sqlite" Version="7.0.0" /^>
echo   ^</ItemGroup^>
echo ^</Project^>
) > "%TEMP_PROJ%\CreateDb.csproj"

REM Create the C# program
(
echo using Microsoft.Data.Sqlite;
echo using System;
echo using System.IO;
echo.
echo class Program {
echo     static void Main^(string[] args^) {
echo         string scriptDir = Path.GetDirectoryName^(System.Reflection.Assembly.GetExecutingAssembly^(^).Location^);
echo         string sqlFile = Path.Combine^(scriptDir, "..", "..", "..", "urlfilter.sql"^);
echo         string dbFile = Path.Combine^(scriptDir, "..", "..", "..", "urlfilter.db"^);
echo.
echo         if ^(!File.Exists^(sqlFile^)^) {
echo             sqlFile = args.Length ^> 0 ? args[0] : "urlfilter.sql";
echo             dbFile = args.Length ^> 1 ? args[1] : "urlfilter.db";
echo         }
echo.
echo         Console.WriteLine^($"SQL File: {sqlFile}"^);
echo         Console.WriteLine^($"DB File: {dbFile}"^);
echo.
echo         if ^(File.Exists^(dbFile^)^) File.Delete^(dbFile^);
echo.
echo         string sql = File.ReadAllText^(sqlFile^);
echo         using var conn = new SqliteConnection^($"Data Source={dbFile}"^);
echo         conn.Open^(^);
echo.
echo         using var cmd = conn.CreateCommand^(^);
echo         cmd.CommandText = sql;
echo         cmd.ExecuteNonQuery^(^);
echo.
echo         Console.WriteLine^("Database created successfully!"^);
echo.
echo         cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
echo         using var reader = cmd.ExecuteReader^(^);
echo         Console.WriteLine^("Tables created:"^);
echo         while ^(reader.Read^(^)^) Console.WriteLine^($"  - {reader.GetString^(0^)}"^);
echo     }
echo }
) > "%TEMP_PROJ%\Program.cs"

REM Build and run
echo Building database creator...
dotnet build "%TEMP_PROJ%\CreateDb.csproj" -c Release -o "%TEMP_PROJ%\bin" >nul 2>&1

if %errorlevel% neq 0 (
    echo Build failed. Trying alternative method...
    goto :manual_install
)

echo Running database creator...
dotnet "%TEMP_PROJ%\bin\CreateDb.dll" "%SQL_FILE%" "%DB_FILE%"

REM Cleanup
rmdir /s /q "%TEMP_PROJ%" 2>nul

goto :copy_to_install

:manual_install
echo.
echo ============================================
echo MANUAL INSTALLATION REQUIRED
echo ============================================
echo.
echo Please install SQLite CLI tools:
echo   1. Download from: https://www.sqlite.org/download.html
echo   2. Extract sqlite3.exe to: %SCRIPT_DIR%
echo   3. Run this script again
echo.
echo Alternatively, the database will be created automatically
echo when the URL Filter Service starts for the first time.
echo.
exit /b 1

:copy_to_install
echo.
echo ============================================
echo Installation Options
echo ============================================
echo.
echo 1. Copy to service installation directory
echo 2. Keep in current location only
echo 3. Exit
echo.
set /p choice="Enter choice (1-3): "

if "%choice%"=="1" (
    echo.
    echo Creating installation directory...
    if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

    echo Copying database to %INSTALL_DIR%...
    copy /y "%DB_FILE%" "%INSTALL_DIR%\urlfilter.db"

    if %errorlevel% equ 0 (
        echo.
        echo SUCCESS: Database installed to %INSTALL_DIR%\urlfilter.db
    ) else (
        echo.
        echo WARNING: Could not copy to installation directory.
        echo You may need to run this script as Administrator.
    )
)

echo.
echo ============================================
echo Database Creation Complete!
echo ============================================
echo.
echo Database file: %DB_FILE%
echo.
echo Contents:
echo   - 20 content categories
echo   - 80+ blocked URLs (adult, social, gaming, VPN, etc.)
echo   - 30+ whitelisted educational sites
echo   - 25+ approved YouTube channels
echo   - Sample family profiles (Mom, Dad, Emma age 8, Jake age 15)
echo   - Time restrictions for children
echo   - Default admin settings
echo.

pause
