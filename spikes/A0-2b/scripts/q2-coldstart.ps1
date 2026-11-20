# Cold start after emu kill (replaces the adb-reboot test). Start the emulator first with the documented command.
# Measures, from the moment the device is seen in adb: boot_completed, VPN pid, upstream DNS resolved,
# first 'dns example.org -> OK' (through the VPN), first 'http https://example.org/ -> OK'.
. "$PSScriptRoot\common.ps1"
$pcap = Join-Path $global:Ev 'q2-coldstart-nic.pcap'
$t0 = Get-Date
$devAt = $null
while (((Get-Date) - $t0).TotalSeconds -lt 400) {
    if ((& $global:Adb devices 2>&1 | Out-String) -match "$($global:Ser)\s+device") { $devAt = Get-Date; break }
    Start-Sleep -Milliseconds 500
}
if (-not $devAt) { Save-Ev 'q2-coldstart-error.txt' 'device never appeared in adb within 400 s'; return }
& $global:Adb -s $global:Ser emu network capture start $pcap | Out-Null
$bootAt = $null
while (((Get-Date) - $devAt).TotalSeconds -lt 300) {
    if ((Invoke-Adb @('shell','getprop','sys.boot_completed')).Trim() -eq '1') { $bootAt = Get-Date; break }
    Start-Sleep -Milliseconds 700
}
$vpnAt = $null
while (((Get-Date) - $devAt).TotalSeconds -lt 360) {
    if (Get-VpnPid) { $vpnAt = Get-Date; break }
    Start-Sleep -Milliseconds 700
}
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','https://example.org/') | Out-Null
Start-Sleep -Seconds 4
$dnsAt = $null; $httpAt = $null
while (((Get-Date) - $devAt).TotalSeconds -lt 420) {
    if (-not $dnsAt) {
        Invoke-Probe 'dns' @('--es','host','example.org') | Out-Null
        Start-Sleep -Milliseconds 1500
        if ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match 'op=dns -> OK') { $dnsAt = Get-Date }
    } elseif (-not $httpAt) {
        Invoke-Probe 'http' @('--es','url','https://example.org/') | Out-Null
        Start-Sleep -Milliseconds 2500
        if ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match 'op=http -> OK') { $httpAt = Get-Date; break }
    }
}
& $global:Adb -s $global:Ser emu network capture stop | Out-Null
$f = { param($d) if ($d) { [math]::Round(($d - $devAt).TotalSeconds, 1) } else { -1 } }
$summary = "device_seen=0.0 boot_completed=$(& $f $bootAt) vpn_pid_seen=$(& $f $vpnAt) first_dns_example_org_OK=$(& $f $dnsAt) first_http_example_org_OK=$(& $f $httpAt) (seconds after device seen in adb, host clock, polled)"
$log = Invoke-Adb @('logcat','-d')
Save-Ev 'q2-coldstart-timing.txt' ($summary + "`n" + (($log -split "`n") | Select-String -Pattern 'A02B|A02P|A02S' | ForEach-Object { $_.Line } | Out-String))
Write-Output $summary
