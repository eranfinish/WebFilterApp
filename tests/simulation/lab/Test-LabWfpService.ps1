<#
.SYNOPSIS
End to end in a lab VM: the real WebFilter.Service with ManageNetwork=true keeps WFP equal to the family settings,
through NativeWfpEngine (P/Invoke) and WebFilter.Wfp.Native.dll.

.DESCRIPTION
Runs on the host (Hyper-V administrator). Publishes the service (Release, self-contained), adds the native DLL, copies
both into the VM and starts the service there as a console process (not as a Windows service, next to any installed
one: its own data folder, pipe names and a free hub port), in the hub role with a bootstrap family:
  noa       linked to a restricted child, with one allowed program that is not installed yet (always allowed)
  labadmin  linked to an adult (and an administrator)
The hub also reports the computer's other accounts, so the remaining non-admin accounts appear unlinked. Checks:
  1  filters for every non-admin account (noa linked, the others unlinked) and none for the administrators
  2  the program that is not installed is logged as skipped; once it exists it gets its filters on the next tick
  3  filters someone deleted are put back on the next tick, with a warning
  4  a settings change (pausing noa through the admin pipe) lifts her filters at once; resuming puts them back
  5  the filters stay after the service stops (fail closed)
Ends with RemoveAll (wfpctl) and checks that nothing is left; the service's own `cleanup` is not run here, because it
would also remove the installed WebFilter's data and pairing. Results: tests/simulation/lab/results/wfp-service.json.
Run Test-LabWfp.ps1 first (it copies wfpctl.exe into the VM).

.EXAMPLE
.\Test-LabWfpService.ps1 -VMName lab-kids
#>
[CmdletBinding()]
param(
    [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $VMName = 'lab-kids',
    [switch] $SkipPublish,
    [string] $ResultPath = (Join-Path $PSScriptRoot 'results\wfp-service.json')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$work = Join-Path $env:TEMP 'webfilter-lab-wfp-service'
$zip = Join-Path $work 'service.zip'
$guestDir = 'C:\Lab\wfp-svc'
$adminPipe = 'WebFilter.Lab.Admin'
$credential = Import-Clixml (Join-Path 'C:\Lab' "cred-$VMName.xml")

# ---- publish and copy --------------------------------------------------------------------------------------------------
if (-not $SkipPublish) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    & $msbuild (Join-Path $repo 'src\WebFilter.Wfp.Native\WebFilter.Wfp.Native.vcxproj') -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'The native build failed.' }
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    dotnet publish (Join-Path $repo 'src\WebFilter.Service\WebFilter.Service.csproj') -c Release -r win-x64 --self-contained --nologo -v:minimal `
        --artifacts-path (Join-Path $work 'artifacts') -o (Join-Path $work 'app')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    Copy-Item (Join-Path $repo 'src\WebFilter.Wfp.Native\bin\x64\Release\WebFilter.Wfp.Native.dll') (Join-Path $work 'app')
    Compress-Archive -Path (Join-Path $work 'app\*') -DestinationPath $zip -Force
}
Copy-VMFile -Name $VMName -SourcePath $zip -DestinationPath "$guestDir\service.zip" -CreateFullPath -FileSource Host -Force

$session = New-PSSession -VMName $VMName -Credential $credential
$setup = Invoke-Command -Session $session -ArgumentList $guestDir -ScriptBlock {
    param($dir)
    $ErrorActionPreference = 'Stop'
    if (Test-Path "$dir\app") { Remove-Item -LiteralPath "$dir\app" -Recurse -Force }
    Expand-Archive -LiteralPath "$dir\service.zip" -DestinationPath "$dir\app"
    foreach ($leftover in "$dir\data", "$dir\profiles", "$dir\later") { if (Test-Path $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force } }
    New-Item -ItemType Directory -Force "$dir\later" | Out-Null
    $accounts = Get-LocalUser | Where-Object Enabled
    $admins = (Get-LocalGroupMember -SID S-1-5-32-544).SID.Value
    $noa = ($accounts | Where-Object Name -eq 'noa').SID.Value
    $labadmin = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $bundle = [ordered]@{
        timezone          = 'Asia/Jerusalem'
        members           = @(
            [ordered]@{ id = 'noa'; displayName = 'Noa'; role = 'child'; age = 8; filterMode = 'allowlist'; youtubeMode = 'allowlist' },
            [ordered]@{ id = 'parent'; displayName = 'Parent'; role = 'adult'; age = 40; filterMode = 'unrestricted'; youtubeMode = 'unrestricted' })
        devices           = @([ordered]@{ id = 'lab-kids'; os = 'windows'; role = 'hub'; accounts = @(
                    [ordered]@{ accountId = $noa; name = 'noa'; member = 'noa'; isAdmin = $false },
                    [ordered]@{ accountId = $labadmin; name = 'labadmin'; member = 'parent'; isAdmin = $true }) })
        # Every collection is written out: the bundle reader leaves a missing one null.
        categories        = [ordered]@{}
        memberCategories  = @()
        rules             = @()
        schedules         = @()
        dailyLimits       = @()
        youtubeChannels   = @()
        supportedBrowsers = @('C:\Program Files\Google\Chrome\Application\chrome.exe', 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
        appRules          = @([ordered]@{ id = 'a-later'; member = 'noa'; match = 'path'; value = "$dir\later\app.exe"; action = 'allow'; alwaysAllowed = $true })
    }
    $bundle | ConvertTo-Json -Depth 6 | Set-Content -Path "$dir\family.json" -Encoding UTF8
    [pscustomobject]@{
        Noa         = $noa
        Labadmin    = $labadmin
        Admins      = @($admins)
        NonAdmins   = @($accounts | Where-Object { $admins -notcontains $_.SID.Value } | ForEach-Object { $_.SID.Value })
        Os          = (Get-CimInstance Win32_OperatingSystem) | ForEach-Object { "$($_.Caption) $($_.Version)" }
    }
}
$noa = [string]$setup.Noa
$nonAdmins = @($setup.NonAdmins | ForEach-Object { [string]$_ })
$admins = @($setup.Admins | ForEach-Object { [string]$_ })
Write-Host "VM $VMName ($($setup.Os)); non-admin accounts: $($nonAdmins -join ', '); administrators: $($admins -join ', ')"

# ---- recording ---------------------------------------------------------------------------------------------------------
$checks = [System.Collections.Generic.List[object]]::new()
function Assert-Check([string] $what, $condition, $evidence) {
    $pass = [System.Management.Automation.LanguagePrimitives]::IsTrue($condition)
    # [string] drops the remoting properties a string from the VM carries.
    $text = if ($evidence -is [string]) { [string]$evidence } else { ($evidence | ForEach-Object { [string]$_ }) -join ' | ' }
    $checks.Add([ordered]@{ check = $what; pass = $pass; evidence = $text })
    Write-Host ("  [{0}] {1}: {2}" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $what, $text) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}

$guest = {
    function global:Get-WfpUsers { @(& C:\Lab\wfp\wfpctl.exe list | Where-Object { $_ }) }
    function global:Get-WfpStatus { (& C:\Lab\wfp\wfpctl.exe status) -join '' }
    function global:Get-ServiceLog { if (Test-Path C:\Lab\wfp-svc\out.log) { Get-Content C:\Lab\wfp-svc\out.log } }
    function global:Wait-Until([scriptblock] $condition, [int] $seconds = 60) {
        $deadline = (Get-Date).AddSeconds($seconds)
        while ((Get-Date) -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
        return $false
    }
}
Invoke-Command -Session $session -ScriptBlock $guest

function Send-Admin([string] $message) {
    try { & (Join-Path $PSScriptRoot 'Invoke-LabAdmin.ps1') -VMName $VMName -PipeName $adminPipe -Message $message }
    catch { [pscustomobject]@{ type = "exception: $($_.Exception.Message)" } }
}

function Get-Count([string] $status) { if ($status -match 'filters=(\d+)') { [int]$Matches[1] } else { -1 } }

try {
    $status = Invoke-Command -Session $session { & C:\Lab\wfp\wfpctl.exe remove-all | Out-Null; Get-WfpStatus }
    Assert-Check 'clean start' ($status -eq 'provider=no sublayer=no filters=0') $status

    $process = Invoke-Command -Session $session -ArgumentList $guestDir, $adminPipe -ScriptBlock {
        param($dir, $adminPipe)
        $arguments = @(
            '--WebFilter:Role=hub', '--WebFilter:DeviceId=lab-kids', '--WebFilter:ManageNetwork=true',
            "--WebFilter:DataDirectory=$dir\data", "--WebFilter:ProfileDirectory=$dir\profiles", "--WebFilter:BootstrapBundlePath=$dir\family.json",
            '--WebFilter:AgentPipeName=WebFilter.Lab.Agent', "--WebFilter:AdminPipeName=$adminPipe", '--WebFilter:HubPort=0',
            '--WebFilter:TickInterval=00:00:05')
        $global:WfService = Start-Process -FilePath "$dir\app\WebFilter.Service.exe" -ArgumentList $arguments -WorkingDirectory "$dir\app" `
            -RedirectStandardOutput "$dir\out.log" -RedirectStandardError "$dir\err.log" -WindowStyle Hidden -PassThru
        $global:WfService.Id
    }
    Write-Host "Service started in $VMName (process $process)"

    Write-Host "`n== 1. Filters for the non-admin accounts only"
    $ready = Invoke-Command -Session $session -ArgumentList (, $nonAdmins) -ScriptBlock {
        param($expected)
        Wait-Until { $users = Get-WfpUsers; @($expected | Where-Object { $users -notcontains $_ }).Count -eq 0 } 60
    }
    $users = @(Invoke-Command -Session $session { Get-WfpUsers } | ForEach-Object { [string]$_ })
    Assert-Check 'every non-admin account has filters (noa linked, the others unlinked)' ($ready -and @($nonAdmins | Where-Object { $users -notcontains $_ }).Count -eq 0) $users
    Assert-Check 'no administrator has filters' (@($admins | Where-Object { $users -contains $_ }).Count -eq 0) "administrators: $($admins -join ', ')"
    $status = Invoke-Command -Session $session { Get-WfpStatus }
    # Each account: 2 layers x (block, loopback, Chrome, Edge); noa's extra program is not installed yet.
    $expected = $nonAdmins.Count * 8
    Assert-Check "status: $expected filters" ((Get-Count $status) -eq $expected) $status
    $log = Invoke-Command -Session $session { Get-ServiceLog }
    Assert-Check 'the service logged the pass' (@($log -match 'Network filters: \d+ accounts set').Count -gt 0) ($log -match 'Network filters:')

    Write-Host "`n== 2. A program that is not installed yet"
    Assert-Check 'logged as skipped, staying blocked' (@($log -match 'app\.exe, allowed for .* got no network filter and stays blocked: not installed').Count -gt 0) ($log -match 'got no network filter')
    $after = Invoke-Command -Session $session -ArgumentList $expected -ScriptBlock {
        param($before)
        Copy-Item C:\Windows\System32\curl.exe C:\Lab\wfp-svc\later\app.exe
        Wait-Until { (Get-WfpStatus) -match "filters=$($before + 2)$" } 30 | Out-Null
        Get-WfpStatus
    }
    Assert-Check 'once installed it gets its filters on the next tick (+2)' ((Get-Count $after) -eq $expected + 2) $after
    $expected += 2

    Write-Host "`n== 3. Deleted filters are put back"
    $victim = $nonAdmins | Where-Object { $_ -ne $noa } | Select-Object -First 1
    $restored = Invoke-Command -Session $session -ArgumentList $victim, $expected -ScriptBlock {
        param($sid, $count)
        & C:\Lab\wfp\wfpctl.exe remove $sid | Out-Null
        $gone = (Get-WfpUsers) -notcontains $sid
        $back = Wait-Until { (Get-WfpUsers) -contains $sid -and (Get-WfpStatus) -match "filters=$count$" } 30
        [pscustomobject]@{ Gone = $gone; Back = $back; Log = @(Get-ServiceLog | Where-Object { $_ -match 'had been removed; restored' }) }
    }
    Assert-Check "the filters of $victim, deleted with wfpctl, are back within a tick" ($restored.Gone -and $restored.Back) "removed: $($restored.Gone), back: $($restored.Back)"
    Assert-Check 'with a warning in the log' (@($restored.Log | Where-Object { $_ -match [regex]::Escape($victim) }).Count -gt 0) $restored.Log

    Write-Host "`n== 4. A settings change applies at once"
    $until = (Get-Date).AddHours(1).ToString('yyyy-MM-ddTHH:mm:sszzz')
    $reply = Send-Admin "{`"type`":`"pause`",`"v`":1,`"memberId`":`"noa`",`"until`":`"$until`"}"
    $paused = Invoke-Command -Session $session -ArgumentList $noa -ScriptBlock { param($sid) Wait-Until { (Get-WfpUsers) -notcontains $sid } 3 }
    Assert-Check 'pausing noa (admin pipe) lifts her filters within 3 seconds' ($reply.type -eq 'ok' -and $paused) "reply: $($reply.type), lifted: $paused"
    $reply = Send-Admin '{"type":"resume","v":1,"memberId":"noa"}'
    $resumed = Invoke-Command -Session $session -ArgumentList $noa, $expected -ScriptBlock { param($sid, $count) Wait-Until { (Get-WfpUsers) -contains $sid -and (Get-WfpStatus) -match "filters=$count$" } 3 }
    Assert-Check 'resuming puts them back within 3 seconds' ($reply.type -eq 'ok' -and $resumed) "reply: $($reply.type), back: $resumed"

    Write-Host "`n== 5. Fail closed: the filters stay when the service stops"
    $stopped = Invoke-Command -Session $session { Stop-Process -Id $global:WfService.Id -Force; $global:WfService.WaitForExit(10000) | Out-Null; [pscustomobject]@{ Exited = $global:WfService.HasExited; Status = Get-WfpStatus } }
    Assert-Check 'after the service process ended, the filters are all still there' ($stopped.Exited -and (Get-Count $stopped.Status) -eq $expected) $stopped.Status
    $errors = Invoke-Command -Session $session { @(Get-ServiceLog | Where-Object { $_ -match '^fail:|Could not apply the network filters' }) }
    Assert-Check 'no error in the service log' ($errors.Count -eq 0) $errors
}
finally {
    $final = Invoke-Command -Session $session {
        if ($global:WfService -and -not $global:WfService.HasExited) { Stop-Process -Id $global:WfService.Id -Force }
        & C:\Lab\wfp\wfpctl.exe remove-all | Out-Null
        Get-WfpStatus
    }
    Write-Host "`nFinal WFP state in $VMName`: $final" -ForegroundColor Yellow
    $log = Invoke-Command -Session $session { Get-ServiceLog }
    Remove-PSSession $session
    $report = [ordered]@{
        date     = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        vm       = $VMName
        os       = [string]$setup.Os
        finalWfp = [string]$final
        passed   = @($checks | Where-Object { $_.pass }).Count
        failed   = @($checks | Where-Object { -not $_.pass }).Count
        checks   = $checks
        log      = @($log | Where-Object { $_ -match 'Network filters|network filter|WebFilter.Service.Network' } | ForEach-Object { "$_".Trim() })
    }
    New-Item -ItemType Directory -Force (Split-Path $ResultPath) | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -Path $ResultPath -Encoding UTF8
    Write-Host ("{0} passed, {1} failed. Results: {2}" -f $report.passed, $report.failed, $ResultPath)
}
