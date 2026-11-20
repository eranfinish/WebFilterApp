# Q4 with the VPN up (lockdown on). Usage: q4-vpn.ps1 -Label vpn
# Records RTT (100 small GETs through the tunnel) and 3 x 50 MB downloads, and the VPN process CPU ticks before/after.
param([string]$Label = 'vpn')
. "$PSScriptRoot\common.ps1"
function Get-Cpu { $p = (Get-VpnPid); if (-not $p) { return 'no-vpn-pid' }; $s = (Invoke-Adb @('shell',"cat /proc/$p/stat")).Trim(); $f = $s -split ' '; "pid=$p utime=$($f[13]) stime=$($f[14]) (clock ticks, CLK_TCK=100)" }
function Wait-Log([string]$pat, [int]$maxSec) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $maxSec) {
        if ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match $pat) { return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}
Invoke-Adb @('logcat','-c') | Out-Null
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','http://10.0.2.2:8080/small.txt') | Out-Null
Start-Sleep -Seconds 4
$cpu0 = Get-Cpu
Invoke-Probe 'rtt' @('--es','url','http://10.0.2.2:8080/small.txt','--ei','n','100') | Out-Null
$rttOk = Wait-Log 'op=rtt' 300
$cpu1 = Get-Cpu
Invoke-Probe 'bench' @('--es','url','http://10.0.2.2:8080/big.bin','--ei','n','3') | Out-Null
$benchOk = Wait-Log 'op=bench' 1500
$cpu2 = Get-Cpu
$log = Invoke-Adb @('logcat','-d','-s','A02P:I','A02B:I')
Save-Ev "q4-$Label.txt" ("label=$Label`ncpu_before_rtt=$cpu0`ncpu_after_rtt=$cpu1`ncpu_after_bench=$cpu2`nrtt_done=$rttOk bench_done=$benchOk`n" + $log)
Write-Output "q4 $Label done"
