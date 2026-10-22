<#
.SYNOPSIS
Proves the WFP engine (src/WebFilter.Wfp.Native) in a lab VM: network access per Windows account and per program,
persistence across a reboot, and a clean removal.

.DESCRIPTION
Runs on the host (Hyper-V administrator). Builds WebFilter.Wfp.Native.dll and wfpctl.exe (tests/WebFilter.Wfp.LabTool)
with Visual Studio's MSBuild, copies them and Wfp-Probe.ps1 into the VM and drives them over PowerShell Direct, signed
in as labadmin (C:\Lab\cred-<vm>.xml). PowerShell Direct runs over VMBus, not the network, so blocking labadmin's
network never cuts the session. The filters are applied to labadmin's own SID: they work for any account, admin or not.

  baseline  no filters: powershell.exe, curl.exe and an AppContainer process reach https://www.microsoft.com
  a         only curl.exe allowed (plus a program that is not installed): powershell.exe is blocked over IPv4 and IPv6
            (socket error 10013), curl.exe connects, a copy of curl.exe elsewhere is blocked, loopback still works,
            SYSTEM (a scheduled task) is unaffected, the missing program is skipped and reported; netsh lists our filters
  e         Store apps, with an AppContainer as the stand-in: blocked when not allowed; allowed by its package name
            (any case) while the same program outside the AppContainer stays blocked
  b         powershell.exe allowed too: it connects again
  c         back to curl.exe only, then a reboot of the VM: `list` still returns the SID and powershell.exe is still blocked
  d         RemoveAll: no provider, sublayer or filter of ours in the WFP API or in `netsh wfp show state` / `show filters`,
            and powershell.exe connects

Every run starts and ends with RemoveAll (also after a failure) and checks that nothing is left. The results are written
to tests/simulation/lab/results/wfp-engine.json. Never run against the development PC.

.EXAMPLE
.\Test-LabWfp.ps1 -VMName lab-kids
#>
[CmdletBinding()]
param(
    [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $VMName = 'lab-kids',
    [switch] $SkipBuild,
    [switch] $SkipReboot,
    [string] $ResultPath = (Join-Path $PSScriptRoot 'results\wfp-engine.json')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$guestDir = 'C:\Lab\wfp'
$providerGuid = 'f89a6e71-59e0-4c54-b98f-ceabf67463d0'
$sublayerGuid = '9710eafc-b30c-4e32-a731-50424230de15'
$container = 'WebFilter.LabTest'
$credential = Import-Clixml (Join-Path 'C:\Lab' "cred-$VMName.xml")

# ---- build and copy ----------------------------------------------------------------------------------------------------
$toolDir = Join-Path $repo 'tests\WebFilter.Wfp.LabTool\bin\x64\Release'
if (-not $SkipBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw "Visual Studio's MSBuild with the C++ workload was not found." }
    & $msbuild (Join-Path $repo 'tests\WebFilter.Wfp.LabTool\WebFilter.Wfp.LabTool.vcxproj') -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'The native build failed.' }
}
foreach ($file in @((Join-Path $toolDir 'wfpctl.exe'), (Join-Path $toolDir 'WebFilter.Wfp.Native.dll'), (Join-Path $PSScriptRoot 'Wfp-Probe.ps1'))) {
    Copy-VMFile -Name $VMName -SourcePath $file -DestinationPath (Join-Path $guestDir (Split-Path $file -Leaf)) -CreateFullPath -FileSource Host -Force
}

# The host's link-local IPv6 address on the VM's switch: a neighbor the VM can try over IPv6 (nothing listens on 443).
$switch = (Get-VMNetworkAdapter -VMName $VMName | Select-Object -First 1).SwitchName
$hostV6 = (Get-NetIPAddress -InterfaceAlias "vEthernet ($switch)" -AddressFamily IPv6 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like 'fe80*' } | Select-Object -First 1).IPAddress -replace '%.*$', ''

# ---- guest helpers -----------------------------------------------------------------------------------------------------
$guestFunctions = {
    param($dir, $hostV6, $container, $providerGuid, $sublayerGuid)
    Set-Location $dir
    $global:WfDir = $dir
    $global:WfContainer = $container
    $global:WfGuids = @($providerGuid, $sublayerGuid)
    $global:WfV6Target = $null
    if ($hostV6) {
        $index = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceIndex
        $global:WfV6Target = "$hostV6%$index"
    }
    function global:Invoke-Wfpctl {
        $output = & "$global:WfDir\wfpctl.exe" @args 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = @($output) }
    }
    # A WFP block fails a connection at once (socket error 10013, curl exit 7). The HTTPS answer of www.microsoft.com is
    # sometimes slow or cut short from the lab network (curl 28, 18, 56), which says nothing about WFP: the probes ask
    # for the headers only (HEAD), count a received HTTP status as connected, and try again on those errors, up to 4 times.
    function global:Test-PowerShell {
        foreach ($try in 1..4) {
            $probe = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$global:WfDir\Wfp-Probe.ps1" -V6Target $global:WfV6Target | ConvertFrom-Json
            if ($probe.http -notlike '*timed out*') { break }
        }
        $probe
    }
    function global:Test-Curl([string] $exe = 'C:\Windows\System32\curl.exe') {
        foreach ($try in 1..4) {
            $output = & $exe -sS -I -o NUL -w '%{http_code}' --max-time 30 https://www.microsoft.com 2>&1 | ForEach-Object { "$_" }
            $exit = $LASTEXITCODE
            $status = "$($output | Select-Object -Last 1)"
            if ($status -eq '200' -or $exit -notin 18, 28, 56) { break }
        }
        # Status: the HTTP status received ("000" when there was no answer at all).
        [pscustomobject]@{ Exit = $exit; Status = $status; Tries = $try; Output = ($output -join ' ') }
    }
    function global:Test-AppContainer {
        foreach ($try in 1..4) {
            $result = Invoke-Wfpctl appcontainer-run $global:WfContainer 'C:\Windows\System32\curl.exe -s -I -o NUL --max-time 30 https://www.microsoft.com'
            $exit = [int](($result.Output | Where-Object { $_ -like 'exit=*' } | Select-Object -Last 1) -replace 'exit=', '')
            if ($exit -notin 18, 28, 56) { break }
        }
        [pscustomobject]@{ CurlExit = $exit; Tries = $try; Output = $result.Output -join ' ' }
    }
    function global:Test-AsSystem {
        $out = Join-Path $global:WfDir "probe-system-$(Get-Random).json"
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$global:WfDir\Wfp-Probe.ps1`" -Out `"$out`""
        Register-ScheduledTask -TaskName 'WebFilter WFP lab probe' -Action $action -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName 'WebFilter WFP lab probe'
            $deadline = (Get-Date).AddSeconds(90)
            while (-not (Test-Path $out) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
            Start-Sleep -Milliseconds 500
            if (-not (Test-Path $out)) { return [pscustomobject]@{ account = 'SYSTEM'; http = 'no result' } }
            Get-Content $out -Raw | ConvertFrom-Json
        }
        finally {
            Unregister-ScheduledTask -TaskName 'WebFilter WFP lab probe' -Confirm:$false
        }
    }
    function global:Get-NetshMentions {
        $state = Join-Path $global:WfDir "state-$(Get-Random).xml"
        $filters = Join-Path $global:WfDir "filters-$(Get-Random).xml"
        netsh wfp show state file=$state | Out-Null
        netsh wfp show filters file=$filters | Out-Null
        $stateText = Get-Content $state -Raw
        $filtersText = Get-Content $filters -Raw
        [pscustomobject]@{
            StateProvider  = ([regex]::Matches($stateText, $global:WfGuids[0], 'IgnoreCase')).Count
            StateSublayer  = ([regex]::Matches($stateText, $global:WfGuids[1], 'IgnoreCase')).Count
            FilterProvider = ([regex]::Matches($filtersText, $global:WfGuids[0], 'IgnoreCase')).Count
            # Filter names only: the dump also carries our provider's description.
            FilterNames    = ([regex]::Matches($filtersText, '<name>WebFilter: ')).Count
        }
    }
}

function New-GuestSession {
    $session = New-PSSession -VMName $VMName -Credential $credential
    Invoke-Command -Session $session -ScriptBlock $guestFunctions -ArgumentList $guestDir, $hostV6, $container, $providerGuid, $sublayerGuid
    $session
}

function Invoke-Guest([scriptblock] $block, [object[]] $arguments = @()) {
    Invoke-Command -Session $script:session -ScriptBlock $block -ArgumentList $arguments
}

# ---- recording ---------------------------------------------------------------------------------------------------------
$steps = [System.Collections.Generic.List[object]]::new()
$current = $null
function Start-Step([string] $id, [string] $title) {
    $script:current = [ordered]@{ id = $id; title = $title; checks = [System.Collections.Generic.List[object]]::new() }
    $steps.Add($script:current)
    Write-Host "`n== $id. $title" -ForegroundColor Cyan
}
function Assert-Check([string] $what, $condition, $evidence) {
    $pass = [System.Management.Automation.LanguagePrimitives]::IsTrue($condition)
    # Objects from the VM carry PowerShell remoting properties; keep only their own.
    if ($evidence -is [psobject] -and $evidence.PSObject.Properties['PSComputerName']) {
        $evidence = $evidence | Select-Object * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName
    }
    $text = if ($evidence -is [string]) { [string]$evidence } else { $evidence | ConvertTo-Json -Compress -Depth 5 }
    $script:current.checks.Add([ordered]@{ check = $what; pass = $pass; evidence = $text })
    $color = if ($pass) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}: {2}" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $what, $text) -ForegroundColor $color
}
function Test-Blocked($probe) { $probe.tcp4 -like '*10013*' -and $probe.http -like 'error*' }
function Test-Connected($probe) { $probe.tcp4 -eq 'connected' -and $probe.http -eq '200' }

$session = New-GuestSession
$sid = [string](Invoke-Guest { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value })
$os = [string](Invoke-Guest { (Get-CimInstance Win32_OperatingSystem) | ForEach-Object { "$($_.Caption) $($_.Version)" } })
$curl = 'C:\Windows\System32\curl.exe'
$powershell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$missing = 'C:\Lab\wfp\not-installed\game.exe'
Write-Host "VM $VMName ($os), account SID $sid, IPv6 target $hostV6"

try {
    Start-Step 'baseline' 'No WebFilter objects; everything connects'
    $result = Invoke-Guest { Invoke-Wfpctl remove-all; Invoke-Wfpctl status }
    Assert-Check 'clean start: no provider, sublayer or filters' (($result[1].Output -join '') -eq 'provider=no sublayer=no filters=0') $result[1].Output
    $probe = Invoke-Guest { Test-PowerShell }
    Assert-Check 'powershell.exe connects' (Test-Connected $probe) $probe
    Assert-Check 'IPv6 to the host is not refused by WFP (timeout or refusal)' ($probe.tcp6 -notlike '*10013*') $probe.tcp6
    $probe = Invoke-Guest { Test-Curl }
    Assert-Check 'curl.exe connects' ($probe.Status -eq '200') $probe
    # A new AppContainer profile can take a little while before Windows lets it on the network: a few tries.
    $probe = Invoke-Guest { foreach ($try in 1..5) { $r = Test-AppContainer; if ($r.CurlExit -eq 0) { break }; Start-Sleep 10 }; $r }
    Assert-Check 'curl.exe in an AppContainer connects' ($probe.CurlExit -eq 0) $probe

    Start-Step 'a' 'Only curl.exe allowed for the account'
    $result = Invoke-Guest { param($sid, $curl, $missing) Invoke-Wfpctl set $sid --app $curl --app $missing } $sid, $curl, $missing
    Assert-Check 'set succeeds' ($result.Exit -eq 0) $result.Output
    Assert-Check 'curl.exe got its filter' (($result.Output -like "app $curl*: allowed (0)").Count -eq 1) ($result.Output -like "app $curl*")
    Assert-Check 'the program that is not installed is skipped and reported (error 2 or 3)' (($result.Output -like "app $missing*: skipped (*)") -match 'skipped \((2|3)\)') ($result.Output -like "app $missing*")
    $result = Invoke-Guest { Invoke-Wfpctl list; Invoke-Wfpctl status }
    Assert-Check 'list returns the SID' (@($result[0].Output) -contains $sid -and @($result[0].Output).Count -eq 1) $result[0].Output
    Assert-Check 'status: provider, sublayer, 6 filters (2 layers x block, loopback, curl.exe)' (($result[1].Output -join '') -eq 'provider=yes sublayer=yes filters=6') $result[1].Output
    $probe = Invoke-Guest { Test-PowerShell }
    Assert-Check 'powershell.exe is blocked over IPv4 (socket error 10013)' (Test-Blocked $probe) $probe
    Assert-Check 'powershell.exe is blocked over IPv6 (socket error 10013)' ($probe.tcp6 -like '*10013*') $probe.tcp6
    Assert-Check 'loopback still works (127.0.0.1 and ::1)' ($probe.loopback4 -eq 'connected' -and $probe.loopback6 -eq 'connected') $probe
    $probe = Invoke-Guest { Test-Curl }
    Assert-Check 'curl.exe connects' ($probe.Status -eq '200') $probe
    $probe = Invoke-Guest { Copy-Item C:\Windows\System32\curl.exe C:\Lab\wfp\curl-copy.exe -Force; Test-Curl C:\Lab\wfp\curl-copy.exe }
    Assert-Check 'a copy of curl.exe in another folder is blocked (curl exit 7)' ($probe.Exit -eq 7) $probe
    $probe = Invoke-Guest { Test-AsSystem }
    Assert-Check 'SYSTEM is unaffected (powershell.exe from a scheduled task)' ($probe.account -like '*SYSTEM*' -and (Test-Connected $probe)) $probe
    $mentions = Invoke-Guest { Get-NetshMentions }
    Assert-Check 'netsh wfp show state / show filters list our provider, sublayer and filters' ($mentions.StateProvider -gt 0 -and $mentions.StateSublayer -gt 0 -and $mentions.FilterProvider -gt 0 -and $mentions.FilterNames -eq 6) $mentions

    Start-Step 'e' 'Store apps (an AppContainer process of the account)'
    $result = Invoke-Guest { param($sid) Invoke-Wfpctl set $sid --app C:\Windows\System32\PING.EXE } $sid
    $probe = Invoke-Guest { Test-AppContainer }
    Assert-Check 'not allowed: blocked (curl exit 7)' ($result.Exit -eq 0 -and $probe.CurlExit -eq 7) $probe
    $result = Invoke-Guest { param($sid, $name) Invoke-Wfpctl set $sid --package $name } $sid, $container
    $probe = Invoke-Guest { Test-AppContainer }
    Assert-Check "package $container allowed: it connects" ($result.Exit -eq 0 -and $probe.CurlExit -eq 0) @($result.Output + $probe.Output)
    $probe = Invoke-Guest { Test-Curl }
    Assert-Check 'the same curl.exe outside the AppContainer stays blocked (curl exit 7)' ($probe.Exit -eq 7) $probe
    $result = Invoke-Guest { param($sid, $name) Invoke-Wfpctl set $sid --package $name.ToLowerInvariant() } $sid, $container
    $probe = Invoke-Guest { Test-AppContainer }
    Assert-Check 'the package name matches in any case' ($result.Exit -eq 0 -and $probe.CurlExit -eq 0) @($result.Output + $probe.Output)

    Start-Step 'b' 'powershell.exe allowed too'
    $result = Invoke-Guest { param($sid, $curl, $ps) Invoke-Wfpctl set $sid --app $curl --app $ps } $sid, $curl, $powershell
    Assert-Check 'set succeeds' ($result.Exit -eq 0) $result.Output
    $probe = Invoke-Guest { Test-PowerShell }
    Assert-Check 'powershell.exe connects again' (Test-Connected $probe) $probe
    Assert-Check 'and over IPv6 is no longer refused by WFP' ($probe.tcp6 -notlike '*10013*') $probe.tcp6

    Start-Step 'c' 'Persistence: curl.exe only, then a reboot'
    $result = Invoke-Guest { param($sid, $curl) Invoke-Wfpctl set $sid --app $curl; Invoke-Wfpctl status } $sid, $curl
    Assert-Check 'set before the reboot' ($result[0].Exit -eq 0 -and ($result[1].Output -join '') -eq 'provider=yes sublayer=yes filters=6') $result[1].Output
    if ($SkipReboot) {
        Assert-Check 'reboot' $false 'skipped (-SkipReboot)'
    }
    else {
        $bootBefore = Invoke-Guest { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
        try { Invoke-Guest { Restart-Computer -Force } } catch { Write-Verbose "The session ended with the restart: $_" }
        Remove-PSSession $session -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddMinutes(10)
        $bootAfter = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 10
            try {
                $bootAfter = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime } -ErrorAction Stop
                if ($bootAfter -gt $bootBefore) { break }
            }
            catch { }
        }
        Assert-Check 'the VM rebooted' ($bootAfter -gt $bootBefore) "boot before: $bootBefore, after: $bootAfter"
        $session = New-GuestSession
    }
    $result = Invoke-Guest { Invoke-Wfpctl list; Invoke-Wfpctl status }
    Assert-Check 'after the reboot list returns the SID' (@($result[0].Output) -contains $sid) $result[0].Output
    Assert-Check 'after the reboot the 6 filters are there' (($result[1].Output -join '') -eq 'provider=yes sublayer=yes filters=6') $result[1].Output
    $probe = Invoke-Guest { Test-PowerShell }
    Assert-Check 'after the reboot powershell.exe is still blocked' (Test-Blocked $probe) $probe
    # The network may still be coming up right after a boot: give curl.exe a few tries.
    $probe = Invoke-Guest { foreach ($try in 1..6) { $r = Test-Curl; if ($r.Status -eq '200') { break }; Start-Sleep 10 }; $r }
    Assert-Check 'after the reboot curl.exe connects' ($probe.Status -eq '200') $probe

    Start-Step 'd' 'RemoveAll restores everything'
    $result = Invoke-Guest { Invoke-Wfpctl remove-all; Invoke-Wfpctl status; Invoke-Wfpctl list }
    Assert-Check 'remove-all succeeds' ($result[0].Exit -eq 0) $result[0].Output
    Assert-Check 'status: no provider, no sublayer, no filters' (($result[1].Output -join '') -eq 'provider=no sublayer=no filters=0') $result[1].Output
    Assert-Check 'list is empty' (@($result[2].Output | Where-Object { $_ }).Count -eq 0) $result[2].Output
    $mentions = Invoke-Guest { Get-NetshMentions }
    Assert-Check 'netsh wfp show state / show filters: nothing of ours' ($mentions.StateProvider + $mentions.StateSublayer + $mentions.FilterProvider + $mentions.FilterNames -eq 0) $mentions
    $probe = Invoke-Guest { Test-PowerShell }
    Assert-Check 'powershell.exe connects' (Test-Connected $probe) $probe
    $result = Invoke-Guest { Invoke-Wfpctl remove-all }
    Assert-Check 'remove-all again (nothing to remove) succeeds' ($result.Exit -eq 0) $result.Output
}
finally {
    # Whatever happened above: leave the VM without any WebFilter WFP object, and say so.
    if (-not $session -or $session.State -ne 'Opened') { $session = New-GuestSession }
    $final = Invoke-Guest { Invoke-Wfpctl remove-all | Out-Null; Invoke-Wfpctl status; Invoke-Wfpctl appcontainer-delete $global:WfContainer | Out-Null }
    Write-Host "`nFinal WFP state in $VMName`: $($final.Output)" -ForegroundColor Yellow
    Remove-PSSession $session

    $all = @($steps | ForEach-Object { $_.checks })
    $report = [ordered]@{
        date      = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        vm        = $VMName
        os        = $os
        account   = $sid
        dll       = (Get-FileHash (Join-Path $toolDir 'WebFilter.Wfp.Native.dll') -Algorithm SHA256).Hash
        finalWfp  = ($final.Output -join ' ')
        passed    = @($all | Where-Object { $_.pass }).Count
        failed    = @($all | Where-Object { -not $_.pass }).Count
        steps     = $steps
    }
    New-Item -ItemType Directory -Force (Split-Path $ResultPath) | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $ResultPath -Encoding UTF8
    Write-Host ("{0} passed, {1} failed. Results: {2}" -f $report.passed, $report.failed, $ResultPath)
}
