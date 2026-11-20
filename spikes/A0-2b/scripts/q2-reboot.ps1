# Q2 after reboot. Precondition: lockdown on, VPN up. Captures the emulator NIC across the reboot.
. "$PSScriptRoot\common.ps1"
$pcap = Join-Path $global:Ev 'q2-reboot-nic.pcap'
Invoke-Adb @('logcat','-c') | Out-Null
& $global:Adb -s $global:Ser emu network capture start $pcap | Out-Null
Start-Sleep -Milliseconds 500
$t0 = Get-Date
Invoke-Adb @('reboot') | Out-Null
$bootAt = $null
while (((Get-Date) - $t0).TotalSeconds -lt 240) {
    Start-Sleep -Milliseconds 700
    $st = (Invoke-Adb @('shell','getprop','sys.boot_completed')).Trim()
    if ($st -eq '1') { $bootAt = Get-Date; break }
}
$bootSec = if ($bootAt) { [math]::Round(($bootAt - $t0).TotalSeconds, 1) } else { -1 }
Write-Output "boot_completed after $bootSec s (host clock)"
$vpnAt = $null; $vpnSec = -1
while (((Get-Date) - $t0).TotalSeconds -lt 240) {
    Start-Sleep -Milliseconds 700
    if (Get-VpnPid) { $vpnAt = Get-Date; $vpnSec = [math]::Round(($vpnAt - $t0).TotalSeconds, 1); break }
}
Write-Output "VPN pid seen after $vpnSec s (host clock)"
# Browse after boot, from the probe app in the foreground
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','https://example.org/') | Out-Null
Start-Sleep -Seconds 4
$okSec = -1
for ($k = 0; $k -lt 60; $k++) {
    Invoke-Probe 'raw' @('--es','ip','172.66.147.243','--ei','port','443') | Out-Null
    Start-Sleep -Milliseconds 1200
    if ($okSec -lt 0 -and ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match 'op=raw -> CONNECTED')) { $okSec = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1); break }
}
Write-Output "first raw CONNECTED seen by about $okSec s (host clock, polled)"
& $global:Adb -s $global:Ser emu network capture stop | Out-Null
$log = Invoke-Adb @('logcat','-d','-s','A02B:I','A02P:I','A02S:I','ActivityManager:I')
Save-Ev 'q2-reboot-logcat.txt' ("boot_completed_s=$bootSec vpn_pid_s=$vpnSec first_connect_s=$okSec`n" + $log)
