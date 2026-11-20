# Shared helpers for the A0.2b device runs. Only ever talks to emulator-5580.
$global:Adb = 'C:\Users\Eran Finish\AppData\Local\Android\Sdk\platform-tools\adb.exe'
$global:Ser = 'emulator-5580'
$global:Ev  = 'C:\Projests\WebFilterApp\WebFilterApp\spikes\A0-2b\evidence'
$global:Pkg = 'com.webfilter.spike.a02b'
$global:Probe = 'com.webfilter.spike.a02b.probe'

function Invoke-Adb([string[]]$Args2) { (& $global:Adb -s $global:Ser @Args2 2>&1 | Out-String).TrimEnd() }

function Invoke-Cmd([string]$Op, [string[]]$Extra = @()) {
    Invoke-Adb (@('shell','am','broadcast','-W','-n',"$($global:Pkg)/.CmdReceiver",'--es','op',$Op) + $Extra)
}

function Invoke-Probe([string]$Op, [string[]]$Extra = @(), [switch]$Wait) {
    $w = @(); if ($Wait) { $w = @('-W') }
    Invoke-Adb (@('shell','am','broadcast') + $w + @('-n',"$($global:Probe)/.ProbeReceiver",'--es','op',$Op) + $Extra)
}

function Get-VpnPid { (Invoke-Adb @('shell','pidof',"$($global:Pkg):vpn")).Trim() }

function Save-Ev([string]$Name, [string]$Text) { Set-Content -Path (Join-Path $global:Ev $Name) -Value $Text -Encoding UTF8 }
