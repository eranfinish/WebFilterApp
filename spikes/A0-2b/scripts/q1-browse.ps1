# Q1: browsing under lockdown. Precondition: lockdown on, VPN up, probe activity started.
. "$PSScriptRoot\common.ps1"

function Shot([string]$name) {
    Invoke-Adb @('shell','screencap','-p','/sdcard/a02b.png') | Out-Null
    Invoke-Adb @('pull','/sdcard/a02b.png',(Join-Path $global:Ev $name)) | Out-Null
}

$out = New-Object System.Collections.Generic.List[string]
function Rec([string]$label, [string]$res) { $out.Add("$label :: $res") }

Invoke-Adb @('logcat','-c') | Out-Null
# Second app (probe, own uid): DNS and HTTP, allowed and blocked
Rec 'probe dns example.org'        (Invoke-Probe 'dns' @('--es','host','example.org') -Wait)
Rec 'probe dns blocked.example'    (Invoke-Probe 'dns' @('--es','host','blocked.example') -Wait)
Rec 'probe dns en.wikipedia.org'   (Invoke-Probe 'dns' @('--es','host','en.wikipedia.org') -Wait)
Rec 'probe http example.org'       (Invoke-Probe 'http' @('--es','url','https://example.org/') -Wait)
Rec 'probe http en.wikipedia.org'  (Invoke-Probe 'http' @('--es','url','https://en.wikipedia.org/') -Wait)
Rec 'probe raw 172.66.147.243:443' (Invoke-Probe 'raw' @('--es','ip','172.66.147.243','--ei','port','443') -Wait)
Rec 'probe raw 10.0.2.2:8080 (own origin)' (Invoke-Probe 'raw' @('--es','ip','10.0.2.2','--ei','port','8080') -Wait)
Start-Sleep -Seconds 6
# WebView (probe uid): allowed and blocked
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','https://example.org/') | Out-Null
Start-Sleep -Seconds 6
Invoke-Adb @('shell','am','start','-n',"$global:Probe/.WebProbeActivity",'--es','url','https://en.wikipedia.org/') | Out-Null
Start-Sleep -Seconds 8
# Chrome: allowed, then blocked (dismiss the first-run card first)
Invoke-Adb @('shell','am','start','-a','android.intent.action.VIEW','-d','https://example.org/','com.android.chrome') | Out-Null
Start-Sleep -Seconds 8
Invoke-Adb @('shell','input','tap','540','2274') | Out-Null
Start-Sleep -Seconds 2
Shot 'q1-chrome-example-org.png'
Invoke-Adb @('shell','am','start','-a','android.intent.action.VIEW','-d','https://en.wikipedia.org/','com.android.chrome') | Out-Null
Start-Sleep -Seconds 12
Shot 'q1-chrome-wikipedia-blocked.png'
$out.Add('--- A02P/A02B logcat ---')
$out.Add((Invoke-Adb @('logcat','-d','-s','A02P:I','A02B:I')))
Save-Ev 'q1-browsing-lockdown.txt' ($out -join "`n")
Write-Output 'q1 done'
