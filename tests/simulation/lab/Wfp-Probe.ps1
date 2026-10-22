<#
.SYNOPSIS
Inside a lab VM: what this powershell.exe process, running as whoever started it, can reach. Used by Test-LabWfp.ps1.

.DESCRIPTION
Prints (or writes to -Out) one JSON object:
  account, program   who ran it, and which executable (the WFP app id is the executable's path)
  http               the status of a HEAD request to https://www.microsoft.com, or the error
  tcp4               a TCP connection to www.microsoft.com:443 over IPv4: "connected", "timeout" or "socket error <code> <name>"
  tcp6               the same to -V6Target port 443 (a link-local address on the VM's network). Nothing listens there, so an
                     allowed process gets a refusal or a timeout; WFP's block shows as socket error 10013 (AccessDenied).
  loopback4/6        a connection to a listener of this process on 127.0.0.1 / ::1
#>
param(
    [string] $V6Target,
    [string] $Out
)
$ErrorActionPreference = 'Stop'

function Get-Inner($exception) {
    while ($exception.InnerException) { $exception = $exception.InnerException }
    $exception
}

function Test-Tcp([string] $address, [int] $port) {
    $ip = [Net.IPAddress]::Parse($address)
    $client = New-Object Net.Sockets.TcpClient($ip.AddressFamily)
    try {
        $task = $client.ConnectAsync($ip, $port)
        if (-not $task.Wait(5000)) { return 'timeout' }
        return 'connected'
    }
    catch {
        $inner = Get-Inner $_.Exception
        if ($inner -is [Net.Sockets.SocketException]) { return "socket error $($inner.ErrorCode) $($inner.SocketErrorCode)" }
        return "error $($inner.Message)"
    }
    finally {
        $client.Close()
    }
}

function Test-Loopback([Net.IPAddress] $address) {
    $listener = New-Object Net.Sockets.TcpListener($address, 0)
    $listener.Start()
    try { Test-Tcp $address.ToString() $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

$result = [ordered]@{
    account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    program = (Get-Process -Id $PID).Path
}
try { $result.http = [string](Invoke-WebRequest https://www.microsoft.com -Method Head -UseBasicParsing -TimeoutSec 20).StatusCode }
catch { $result.http = 'error: ' + (Get-Inner $_.Exception).Message }
# Name resolution goes through the DNS Client service (NETWORK SERVICE), which WFP filters of this account do not touch.
$ip4 = [Net.Dns]::GetHostAddresses('www.microsoft.com') | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
$result.tcp4 = Test-Tcp $ip4.IPAddressToString 443
if ($V6Target) { $result.tcp6 = Test-Tcp $V6Target 443 }
$result.loopback4 = Test-Loopback ([Net.IPAddress]::Loopback)
$result.loopback6 = Test-Loopback ([Net.IPAddress]::IPv6Loopback)

$json = $result | ConvertTo-Json -Compress
if ($Out) { Set-Content -Path $Out -Value $json -Encoding UTF8 } else { $json }
