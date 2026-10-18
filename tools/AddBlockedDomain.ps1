#Requires -Version 5.0
<#
.SYNOPSIS
Adds a domain to the blacklist of the URL Filter service.

.EXAMPLE
.\AddBlockedDomain.ps1 facebook.com
.\AddBlockedDomain.ps1 -Domain facebook.com -Remove
.\AddBlockedDomain.ps1 -List
#>
param(
    [Parameter(Position=0)]
    [string]$Domain,

    [switch]$Remove,
    [switch]$List
)

$ErrorActionPreference = "Stop"

$sqliteDll = "C:\Users\finis\source\Projects\WebFilterApp\windows\UrlFilterService\bin\Debug\net6.0-windows\System.Data.SQLite.dll"
$dbPath    = "C:\ProgramData\UrlFilter\Database\urlfilter.db"

if (-not (Test-Path $sqliteDll)) { throw "Missing $sqliteDll - build the service first." }
if (-not (Test-Path $dbPath))    { throw "Missing $dbPath - run the service at least once first." }

Add-Type -Path $sqliteDll
$conn = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$dbPath")
$conn.Open()

try {
    $cmd = $conn.CreateCommand()

    if ($List) {
        $cmd.CommandText = "SELECT id, url_pattern, list_type, is_active FROM url_lists ORDER BY list_type, url_pattern"
        $r = $cmd.ExecuteReader()
        Write-Host ""
        Write-Host "Current URL lists:" -ForegroundColor Cyan
        Write-Host "ID | Active | Type      | Pattern"
        Write-Host "---+--------+-----------+--------------------------------"
        while ($r.Read()) {
            $id     = $r.GetInt32(0)
            $url    = $r.GetString(1)
            $type   = $r.GetString(2)
            $active = if ($r.GetBoolean(3)) { "YES" } else { "no " }
            "{0,3} | {1,-6} | {2,-9} | {3}" -f $id, $active, $type, $url | Write-Host
        }
        $r.Close()
        return
    }

    if (-not $Domain) { throw "Usage: .\AddBlockedDomain.ps1 <domain>  or  -List  or  <domain> -Remove" }

    if ($Remove) {
        $cmd.CommandText = "DELETE FROM url_lists WHERE url_pattern = @d AND list_type = 'blacklist'"
        [void]$cmd.Parameters.AddWithValue("@d", $Domain)
        $n = $cmd.ExecuteNonQuery()
        Write-Host "Removed $n row(s) for '$Domain'" -ForegroundColor Yellow
    }
    else {
        $cmd.CommandText = "INSERT INTO url_lists (url_pattern, list_type, is_active, match_type) VALUES (@d, 'blacklist', 1, 'domain')"
        [void]$cmd.Parameters.AddWithValue("@d", $Domain)
        $n = $cmd.ExecuteNonQuery()
        Write-Host "Added '$Domain' to blacklist ($n row)" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "NOTE: Restart the service (Ctrl+C and re-run), or wait up to 5 minutes for auto-refresh." -ForegroundColor Cyan
}
finally {
    $conn.Close()
}
