# URL Filter System - Database Creation Script (PowerShell)
# Creates the SQLite database file from the SQL schema
# Usage: .\Create-Database.ps1 [-OutputPath <path>] [-Force]

param(
    [string]$OutputPath = "",
    [switch]$Force,
    [switch]$InstallToService
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "URL Filter Database Generator (PowerShell)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SqlFile = Join-Path $ScriptDir "urlfilter.sql"
$DefaultDbFile = Join-Path $ScriptDir "urlfilter.db"
$ServiceInstallDir = "C:\ProgramData\UrlFilterSystem"

# Determine output path
if ($OutputPath -eq "") {
    $DbFile = $DefaultDbFile
} else {
    $DbFile = $OutputPath
}

Write-Host "SQL Schema: $SqlFile" -ForegroundColor Gray
Write-Host "Output DB:  $DbFile" -ForegroundColor Gray
Write-Host ""

# Check if SQL file exists
if (-not (Test-Path $SqlFile)) {
    Write-Host "ERROR: SQL schema file not found: $SqlFile" -ForegroundColor Red
    exit 1
}

# Check if database already exists
if ((Test-Path $DbFile) -and -not $Force) {
    Write-Host "Database already exists: $DbFile" -ForegroundColor Yellow
    $response = Read-Host "Overwrite? (y/N)"
    if ($response -ne "y" -and $response -ne "Y") {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }
}

# Remove existing database
if (Test-Path $DbFile) {
    Write-Host "Removing existing database..." -ForegroundColor Yellow
    Remove-Item $DbFile -Force
}

# Try to find or load SQLite
$sqliteLoaded = $false

# Method 1: Try Microsoft.Data.Sqlite from NuGet
Write-Host "Loading SQLite library..." -ForegroundColor Gray

# Check if assembly is already loaded
$sqliteAssembly = [System.AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq "Microsoft.Data.Sqlite" }

if ($sqliteAssembly) {
    $sqliteLoaded = $true
    Write-Host "  Using pre-loaded Microsoft.Data.Sqlite" -ForegroundColor Green
}

# Try loading from project build output
if (-not $sqliteLoaded) {
    $possiblePaths = @(
        (Join-Path $ScriptDir "..\..\windows\Common\bin\Release\net6.0\Microsoft.Data.Sqlite.dll"),
        (Join-Path $ScriptDir "..\..\windows\Common\bin\Debug\net6.0\Microsoft.Data.Sqlite.dll"),
        (Join-Path $ScriptDir "..\..\build\Service\Microsoft.Data.Sqlite.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.data.sqlite\7.0.0\lib\net6.0\Microsoft.Data.Sqlite.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.data.sqlite\6.0.0\lib\net6.0\Microsoft.Data.Sqlite.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.data.sqlite.core\7.0.0\lib\net6.0\Microsoft.Data.Sqlite.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.data.sqlite.core\6.0.0\lib\net6.0\Microsoft.Data.Sqlite.dll")
    )

    foreach ($path in $possiblePaths) {
        $resolvedPath = [System.IO.Path]::GetFullPath($path)
        if (Test-Path $resolvedPath) {
            try {
                Add-Type -Path $resolvedPath
                $sqliteLoaded = $true
                Write-Host "  Loaded: $resolvedPath" -ForegroundColor Green
                break
            } catch {
                # Try next path
            }
        }
    }
}

# Method 2: Try System.Data.SQLite
if (-not $sqliteLoaded) {
    $systemDataSqlitePaths = @(
        (Join-Path $ScriptDir "..\..\build\Service\System.Data.SQLite.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\system.data.sqlite.core\1.0.118.0\lib\net46\System.Data.SQLite.dll")
    )

    foreach ($path in $systemDataSqlitePaths) {
        $resolvedPath = [System.IO.Path]::GetFullPath($path)
        if (Test-Path $resolvedPath) {
            try {
                Add-Type -Path $resolvedPath
                $sqliteLoaded = $true
                $useSystemData = $true
                Write-Host "  Loaded: $resolvedPath" -ForegroundColor Green
                break
            } catch {
                # Try next path
            }
        }
    }
}

# Method 3: Use sqlite3.exe CLI
$sqlite3Exe = $null
if (-not $sqliteLoaded) {
    Write-Host "  Searching for sqlite3.exe..." -ForegroundColor Gray

    $sqlite3Paths = @(
        (Join-Path $ScriptDir "sqlite3.exe"),
        "C:\sqlite\sqlite3.exe",
        "$env:ProgramFiles\sqlite\sqlite3.exe",
        "$env:LOCALAPPDATA\Programs\sqlite\sqlite3.exe"
    )

    # Check PATH
    $pathResult = Get-Command sqlite3.exe -ErrorAction SilentlyContinue
    if ($pathResult) {
        $sqlite3Exe = $pathResult.Source
    } else {
        foreach ($path in $sqlite3Paths) {
            if (Test-Path $path) {
                $sqlite3Exe = $path
                break
            }
        }
    }

    if ($sqlite3Exe) {
        Write-Host "  Found sqlite3.exe: $sqlite3Exe" -ForegroundColor Green
    }
}

# Create the database
Write-Host ""
Write-Host "Creating database..." -ForegroundColor Cyan

$sql = Get-Content $SqlFile -Raw

if ($sqliteLoaded) {
    # Use .NET SQLite library
    try {
        if ($useSystemData) {
            # System.Data.SQLite
            $connectionString = "Data Source=$DbFile;Version=3;"
            $connection = New-Object System.Data.SQLite.SQLiteConnection($connectionString)
        } else {
            # Microsoft.Data.Sqlite
            $connectionString = "Data Source=$DbFile"
            $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection($connectionString)
        }

        $connection.Open()

        # Split SQL into individual statements (SQLite doesn't support multiple statements in one command easily)
        # We'll execute the whole script - SQLite should handle it
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $null = $command.ExecuteNonQuery()

        Write-Host "Database created successfully!" -ForegroundColor Green
        Write-Host ""

        # Verify tables
        Write-Host "Verifying tables..." -ForegroundColor Gray
        $command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
        $reader = $command.ExecuteReader()

        $tables = @()
        while ($reader.Read()) {
            $tables += $reader.GetString(0)
        }
        $reader.Close()

        Write-Host "Tables created: $($tables.Count)" -ForegroundColor Green
        foreach ($table in $tables) {
            Write-Host "  - $table" -ForegroundColor Gray
        }

        # Get counts for key tables
        Write-Host ""
        Write-Host "Record counts:" -ForegroundColor Gray

        $countTables = @("device_users", "admin_users", "url_lists", "youtube_channels", "content_categories", "admin_settings")
        foreach ($table in $countTables) {
            $command.CommandText = "SELECT COUNT(*) FROM $table;"
            $count = $command.ExecuteScalar()
            Write-Host "  $($table): $count" -ForegroundColor Gray
        }

        $connection.Close()

    } catch {
        Write-Host "ERROR: Failed to create database" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }

} elseif ($sqlite3Exe) {
    # Use sqlite3.exe CLI
    try {
        $sql | & $sqlite3Exe $DbFile

        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 returned error code $LASTEXITCODE"
        }

        Write-Host "Database created successfully!" -ForegroundColor Green
        Write-Host ""

        # Verify
        Write-Host "Tables created:" -ForegroundColor Gray
        & $sqlite3Exe $DbFile ".tables"

    } catch {
        Write-Host "ERROR: Failed to create database" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }

} else {
    Write-Host "ERROR: No SQLite library or CLI tool found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install one of the following:" -ForegroundColor Yellow
    Write-Host "  1. SQLite CLI: https://www.sqlite.org/download.html" -ForegroundColor Gray
    Write-Host "     Extract sqlite3.exe to: $ScriptDir" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. Run 'dotnet restore' in the Windows projects to get NuGet packages" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

# Copy to service directory if requested
if ($InstallToService) {
    Write-Host ""
    Write-Host "Installing to service directory..." -ForegroundColor Cyan

    if (-not (Test-Path $ServiceInstallDir)) {
        New-Item -ItemType Directory -Path $ServiceInstallDir -Force | Out-Null
    }

    $serviceDbPath = Join-Path $ServiceInstallDir "urlfilter.db"
    Copy-Item $DbFile $serviceDbPath -Force

    Write-Host "Installed to: $serviceDbPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Database Creation Complete!" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Database file: $DbFile" -ForegroundColor White
Write-Host ""
Write-Host "Contents:" -ForegroundColor Gray
Write-Host "  - 20 content categories" -ForegroundColor Gray
Write-Host "  - 80+ blocked URLs (adult, social, gaming, VPN, etc.)" -ForegroundColor Gray
Write-Host "  - 30+ whitelisted educational sites" -ForegroundColor Gray
Write-Host "  - 25+ approved YouTube channels" -ForegroundColor Gray
Write-Host "  - Sample family profiles (Mom, Dad, Emma 8yo, Jake 15yo)" -ForegroundColor Gray
Write-Host "  - Time restrictions for children" -ForegroundColor Gray
Write-Host "  - Default admin settings" -ForegroundColor Gray
Write-Host ""
