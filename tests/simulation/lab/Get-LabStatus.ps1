<#
.SYNOPSIS
Starts the lab VMs (if they are off) and writes what is installed and running in each to C:\Lab\status.txt.

.DESCRIPTION
Run on the host from an elevated PowerShell, or as a member of "Hyper-V Administrators", with the credentials saved by
Save-LabCredential.ps1 (C:\Lab\cred-<vm>.xml, readable only by the account that saved them). Read-only inside the
VMs, except one thing: a network still classified Public is set to Private (the hub's firewall rule is Private only;
see LAB-INSTALLER-CHECKLIST.md). It never restores a checkpoint.

.EXAMPLE
.\Get-LabStatus.ps1
#>
[CmdletBinding()]
param(
    [string[]] $VMName = @('lab-hub', 'lab-kids'),
    [string] $OutFile = 'C:\Lab\status.txt',
    [int] $BootTimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]]::new()
function Out-Line([string] $text = '') { $lines.Add($text); Write-Host $text }

Out-Line "Lab status $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') on $env:COMPUTERNAME as $env:USERNAME"
Out-Line ''
Out-Line '== Host'
Get-VM -Name $VMName | ForEach-Object { Out-Line ("{0}: {1}, uptime {2}" -f $_.Name, $_.State, $_.Uptime) }
Get-VMSnapshot -VMName $VMName | ForEach-Object { Out-Line ("checkpoint {0}/{1} ({2:yyyy-MM-dd HH:mm})" -f $_.VMName, $_.Name, $_.CreationTime) }

foreach ($vm in $VMName) {
    if ((Get-VM -Name $vm).State -ne 'Running') {
        Out-Line "Starting $vm"
        Start-VM -Name $vm
    }
}

foreach ($vm in $VMName) {
    $deadline = (Get-Date).AddSeconds($BootTimeoutSeconds)
    while ((Get-VMIntegrationService -VMName $vm -Name Heartbeat).PrimaryStatusDescription -ne 'OK' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
    }
}

$probe = {
    param([string[]] $others)
    $ErrorActionPreference = 'Continue'
    $o = [System.Collections.Generic.List[string]]::new()
    $os = Get-CimInstance Win32_OperatingSystem
    $o.Add("OS: $($os.Caption) $($os.Version), booted $($os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm'))")

    foreach ($p in Get-NetConnectionProfile) {
        $line = "Network '$($p.Name)': $($p.NetworkCategory)"
        if ($p.NetworkCategory -eq 'Public') {
            Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
            $line += ' -> set to Private'
        }
        $o.Add($line)
    }
    foreach ($other in $others) {
        $ok = Test-Connection -ComputerName "$other.local" -Count 1 -Quiet -ErrorAction SilentlyContinue
        $o.Add("Ping $other.local: $ok")
    }

    $product = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object DisplayName -like 'WebFilter*' | Select-Object -First 1
    $o.Add('Installed: ' + $(if ($product) { "$($product.DisplayName) $($product.DisplayVersion)" } else { 'none' }))

    $svc = Get-CimInstance Win32_Service -Filter "Name='WebFilter'" -ErrorAction SilentlyContinue
    $o.Add('Service: ' + $(if ($svc) { "$($svc.State), $($svc.StartMode), $($svc.StartName)" } else { 'none' }))

    $settingsPath = 'C:\Program Files\WebFilter\appsettings.json'
    if (Test-Path $settingsPath) {
        $wf = (Get-Content $settingsPath -Raw | ConvertFrom-Json).WebFilter
        $o.Add("Settings: Role=$($wf.Role) ManageBrowserPolicies=$($wf.ManageBrowserPolicies) ManageNetwork=$($wf.ManageNetwork) ExtensionGuard=$($wf.ExtensionGuard)")
    }

    foreach ($b in @(
            @{ Name = 'Edge'; Exe = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'; Ext = 'Microsoft\Edge\User Data\Default\Extensions\labgagpndiahmbeohjikogfdigebeidl' },
            @{ Name = 'Chrome'; Exe = 'C:\Program Files\Google\Chrome\Application\chrome.exe'; Ext = 'Google\Chrome\User Data\Default\Extensions\lkbhmgdhaalikcmpmefffohinafbjfgh' })) {
        if (-not (Test-Path $b.Exe)) { $o.Add("$($b.Name): not installed"); continue }
        $o.Add("$($b.Name): $((Get-Item $b.Exe).VersionInfo.ProductVersion)")
        foreach ($u in Get-ChildItem C:\Users -Directory) {
            $dir = Join-Path $u.FullName "AppData\Local\$($b.Ext)"
            if (Test-Path $dir) {
                $versions = (Get-ChildItem $dir -Directory | ForEach-Object { $_.Name -replace '_0$', '' }) -join ', '
                $o.Add("  extension for $($u.Name): $versions")
            }
        }
    }

    $users = Get-LocalUser | Where-Object Enabled | ForEach-Object {
        $admin = [bool](Get-LocalGroupMember -SID S-1-5-32-544 -ErrorAction SilentlyContinue | Where-Object Name -like "*\$($_.Name)")
        "$($_.Name)$(if ($admin) { ' (admin)' })"
    }
    $o.Add("Local accounts: $($users -join ', ')")
    # query.exe is missing on Home; the owners of explorer.exe are the users with a desktop session.
    $signedIn = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" |
        ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User } | Sort-Object -Unique
    $o.Add("Signed in: $($signedIn -join ', ')")

    foreach ($f in Get-ChildItem C:\ProgramData\WebFilter -Recurse -File -Filter *.json -ErrorAction SilentlyContinue) {
        $first = [IO.File]::ReadAllBytes($f.FullName) | Select-Object -First 1
        if ($first -ne 0x7B -and $first -ne 0x5B) { $o.Add("Damaged state file: $($f.FullName) ($($f.Length) bytes, first byte $first)") }
    }

    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'WebFilter.Service'; Level = 2, 3 } -MaxEvents 8 -ErrorAction SilentlyContinue
    $o.Add("Recent service warnings/errors: $(@($events).Count)")
    foreach ($e in $events) {
        $o.Add("  $($e.TimeCreated.ToString('MM-dd HH:mm')) $($e.LevelDisplayName): $(($e.Message -split "`r?`n")[0])")
    }
    $o
}

foreach ($vm in $VMName) {
    Out-Line ''
    Out-Line "== $vm"
    try {
        $credential = Import-Clixml "C:\Lab\cred-$vm.xml"
        $others = $VMName | Where-Object { $_ -ne $vm }
        Invoke-Command -VMName $vm -Credential $credential -ScriptBlock $probe -ArgumentList (, [string[]] $others) |
            ForEach-Object { Out-Line $_ }
    }
    catch {
        Out-Line "Could not query ${vm}: $($_.Exception.Message)"
    }
}

$hubScript = Join-Path $PSScriptRoot 'Invoke-LabAdmin.ps1'
if ($VMName -contains 'lab-hub') {
    Out-Line ''
    Out-Line '== Hub (admin pipe)'
    try {
        & $hubScript -VMName lab-hub -Message '{"type":"getStatus","v":1}', '{"type":"getDevices","v":1}' |
            ForEach-Object { Out-Line ($_ | ConvertTo-Json -Depth 6 -Compress) }
    }
    catch {
        Out-Line "Admin pipe: $($_.Exception.Message)"
    }
}

$lines | Out-File -FilePath $OutFile -Encoding utf8
Write-Host "Saved $OutFile" -ForegroundColor Green
