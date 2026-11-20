# Cold start with -no-snapshot-load. Polls ip rule from the first adb contact until the VPN is up (no pcap assumed).
. "$PSScriptRoot\common.ps1"
$t0 = Get-Date
$lines = New-Object System.Collections.Generic.List[string]
$devAt = $null
while (((Get-Date) - $t0).TotalSeconds -lt 400) {
    if ((& $global:Adb devices 2>&1 | Out-String) -match "$($global:Ser)\s+device") { $devAt = Get-Date; break }
    Start-Sleep -Milliseconds 500
}
if (-not $devAt) { Save-Ev 'q2-coldstart2-error.txt' 'device never appeared within 400 s'; return }
$up0 = (Invoke-Adb @('shell','cat','/proc/uptime')).Trim()
$lines.Add("first adb contact host=$($devAt.ToString('HH:mm:ss')) device /proc/uptime=$up0")
$cap = & $global:Adb -s $global:Ser emu network capture start q2coldstart2.pcap 2>&1 | Out-String
$lines.Add("capture start reply: $($cap.Trim())")
$bootAt = $null; $vpnAt = $null
$prevProh = -1; $prevPg = 'x'
while (((Get-Date) - $devAt).TotalSeconds -lt 420) {
    $up = (Invoke-Adb @('shell','cat','/proc/uptime')).Trim().Split(' ')[0]
    $bc = (Invoke-Adb @('shell','getprop','sys.boot_completed')).Trim()
    $ipr = Invoke-Adb @('shell','ip','rule')
    $prohib = ($ipr -split "`n" | Select-String -Pattern 'prohibit').Count
    $pg = (Invoke-Adb @('shell','ping -c 1 -W 1 8.8.8.8 > /dev/null 2>&1; echo $?')).Trim()
    $vpid = Get-VpnPid
    if ($bc -eq '1' -and -not $bootAt) { $bootAt = Get-Date; $lines.Add("boot_completed=1 at uptime $up") }
    if ($prohib -ne $prevProh -or $pg -ne $prevPg) { $lines.Add("uptime=$up prohibit_lines=$prohib vpn_pid='$vpid' shell_ping_8.8.8.8_exit=$pg"); $prevProh = $prohib; $prevPg = $pg }
    if ($vpid -and -not $vpnAt) { $vpnAt = Get-Date; $lines.Add("VPN process up at uptime $up"); break }
    Start-Sleep -Milliseconds 900
}
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','https://example.org/') | Out-Null
Start-Sleep -Seconds 4
$dnsAt = $null; $httpAt = $null
while (((Get-Date) - $devAt).TotalSeconds -lt 480) {
    if (-not $dnsAt) {
        Invoke-Probe 'dns' @('--es','host','example.org') | Out-Null
        Start-Sleep -Milliseconds 1500
        if ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match 'op=dns -> OK') { $dnsAt = Get-Date; $lines.Add("first dns example.org OK at host +$([math]::Round(($dnsAt-$devAt).TotalSeconds,1))s") }
    } else {
        Invoke-Probe 'http' @('--es','url','https://example.org/') | Out-Null
        Start-Sleep -Milliseconds 2500
        if ((Invoke-Adb @('logcat','-d','-s','A02P:I')) -match 'op=http -> OK') { $httpAt = Get-Date; $lines.Add("first http example.org OK at host +$([math]::Round(($httpAt-$devAt).TotalSeconds,1))s"); break }
    }
}
$stop = & $global:Adb -s $global:Ser emu network capture stop 2>&1 | Out-String
$lines.Add("capture stop reply: $($stop.Trim())")
$log = (Invoke-Adb @('logcat','-d')) -split "`n" | Select-String -Pattern 'A02B|A02P|A02S' | ForEach-Object { $_.Line }
Save-Ev 'q2-coldstart2-timing.txt' (($lines -join "`n") + "`n--- logcat (A02B/A02P/A02S)`n" + ($log -join "`n"))
Write-Output ($lines -join "`n")

