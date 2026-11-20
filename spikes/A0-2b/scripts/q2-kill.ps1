# Q2 fail-closed run. Usage: q2-kill.ps1 -Mode kill|forcestop|stopself -Run N
# Precondition: lockdown on, VPN up, probe activity in foreground.
param([string]$Mode = 'kill', [int]$Run = 1, [int]$PostSeconds = 90)
. "$PSScriptRoot\common.ps1"

function Mark([string]$m) { Invoke-Adb @('shell','log','-t','A02S',$m) | Out-Null }
function Tick([string]$label) {
    Mark "$label"
    Invoke-Probe 'raw' @('--es','ip','172.66.147.243','--ei','port','443') | Out-Null
    Invoke-Probe 'dns' @('--es','host','example.org') | Out-Null
}

$pid0 = Get-VpnPid
if (-not $pid0) { Save-Ev "q2-$Mode-run$Run-error.txt" 'no VPN pid before the run'; return }
Invoke-Adb @('logcat','-c') | Out-Null
for ($i = 1; $i -le 5; $i++) { Tick "PRE$i"; Start-Sleep -Milliseconds 800 }

Mark "KILL mode=$Mode pid=$pid0"
switch ($Mode) {
    'kill'      { Invoke-Cmd 'kill_vpn' | Out-Null }
    'forcestop' { Invoke-Adb @('shell','am','force-stop',$global:Pkg) | Out-Null }
    'stopself'  { Invoke-Cmd 'stop_self' | Out-Null }
}
$t0 = Get-Date
$recovered = $false
while (((Get-Date) - $t0).TotalSeconds -lt $PostSeconds) {
    Tick ("POST t=" + [int]((Get-Date) - $t0).TotalSeconds)
    Start-Sleep -Milliseconds 1500
}
$after = Get-VpnPid
Mark "AFTER_POST pidVpn=$after"
if (-not $after) {
    # No automatic restart: re-apply always-on lockdown from the DPC and measure.
    Mark "TRIGGER lockdown_on"
    $t1 = Get-Date
    Invoke-Cmd 'lockdown_on' | Out-Null
    for ($j = 0; $j -lt 40; $j++) {
        Tick ("RESTART t=" + [int]((Get-Date) - $t1).TotalSeconds)
        Start-Sleep -Milliseconds 1000
        if (Get-VpnPid) { Mark 'VPN_PID_BACK'; break }
    }
}
$log = Invoke-Adb @('logcat','-d','-s','A02S:I','A02P:I','A02B:I','ActivityManager:I')
Save-Ev "q2-$Mode-run$Run-logcat.txt" $log
Write-Output "done mode=$Mode run=$Run vpnPidAfterPost=$after"

