# Phase 3 vertical-slice: seed DB, run service, prove DNS filtering.
#
# Exit codes:
#   0 - both cases pass
#   1 - blocked domain did not return 0.0.0.0
#   2 - allowed domain did not resolve to non-zero
#   3 - service failed to start
#   4 - nslookup binary not found

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'windows\UrlFilterService\bin\Debug\net6.0-windows\UrlFilterService.exe'
$logDir = Join-Path $repo 'build-logs'
$log  = Join-Path $logDir 'phase3-dns-smoke.log'
$db   = Join-Path $env:LOCALAPPDATA 'UrlFilter\Database\urlfilter.db'

if (-not (Test-Path $exe)) { Write-Error "exe missing: $exe"; exit 3 }
if (-not (Get-Command nslookup -ErrorAction SilentlyContinue)) { Write-Error 'nslookup not on PATH'; exit 4 }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

function Stop-AllService {
    Get-Process -Name UrlFilterService -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000) } catch { }
    }
}

# 1. Clean slate
Stop-AllService
if (Test-Path $db) { Remove-Item $db -Force -ErrorAction SilentlyContinue }

# 2. Warm up: brief service run to create DB + apply schema.sql
$warmup = Start-Process -FilePath $exe -ArgumentList '-console' -PassThru `
    -RedirectStandardOutput "$log.warmup" -RedirectStandardError "$log.warmup.err" -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $db) {
        $content = Get-Content "$log.warmup" -Raw -ErrorAction SilentlyContinue
        if ($content -match 'DNS listen loop started') { break }
    }
    if ($warmup.HasExited) { break }
}
if (-not $warmup.HasExited) { $warmup.Kill(); $warmup.WaitForExit(3000) }
if (-not (Test-Path $db)) { Write-Error "warmup did not create DB"; exit 3 }

# 3. Seed DB with 1 blacklist entry via CLI
& $exe -block 'blocked-test.example' | Out-Host

# 4. Restart service so it picks up the seeded blacklist on init
Stop-AllService
$proc = Start-Process -FilePath $exe -ArgumentList '-console' -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds(30)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $log) {
        $content = Get-Content $log -Raw -ErrorAction SilentlyContinue
        if ($content -match 'URL Filter Service started successfully' -and
            $content -match 'DNS listen loop started') {
            $ready = $true; break
        }
    }
    if ($proc.HasExited) { break }
}

if (-not $ready) {
    Write-Host "SERVICE FAILED TO REACH READY STATE - dumping last 60 lines of log:"
    if (Test-Path $log) { Get-Content $log -Tail 60 | Out-Host }
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 3
}

$exitCode = 0
try {
    Write-Host "`n--- Query 1: blocked-test.example (expect 0.0.0.0) ---"
    $blockedOut = nslookup blocked-test.example 127.0.0.1 2>&1 | Out-String
    Write-Host $blockedOut
    if ($blockedOut -notmatch '0\.0\.0\.0') {
        Write-Host "FAIL: blocked domain did not return 0.0.0.0" -ForegroundColor Red
        $exitCode = 1
    } else {
        Write-Host "PASS: blocked domain returned 0.0.0.0" -ForegroundColor Green
    }

    Write-Host "`n--- Query 2: example.com (expect non-zero A record via upstream 8.8.8.8) ---"
    $allowedOut = nslookup example.com 127.0.0.1 2>&1 | Out-String
    Write-Host $allowedOut
    $ipv4Matches = [regex]::Matches($allowedOut, '\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b')
    $realIps = $ipv4Matches | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne '0.0.0.0' -and $_ -ne '127.0.0.1' }
    $hasReal = ($realIps | Measure-Object).Count -gt 0
    if (-not $hasReal) {
        Write-Host "FAIL: allowed domain did not resolve to a real address" -ForegroundColor Red
        if ($exitCode -eq 0) { $exitCode = 2 }
    } else {
        Write-Host "PASS: allowed domain resolved via upstream" -ForegroundColor Green
    }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(3000) }
}

Write-Host "`nFinal exit: $exitCode"
exit $exitCode
