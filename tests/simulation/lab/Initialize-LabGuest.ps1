<#
.SYNOPSIS
Prepares a Windows 11 lab VM after setup: computer name, a local automation admin, standard child accounts,
Private network profile, and optionally Google Chrome.

.DESCRIPTION
Run inside the VM from an elevated PowerShell. Passwords are prompted for and never written anywhere.
The child accounts are standard users (members of Users only), as the product requires.

.EXAMPLE
.\Initialize-LabGuest.ps1 -ComputerName lab-kids -InstallChrome
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $ComputerName,
    [string[]] $Children = @('noa', 'yoav'),
    [switch] $InstallChrome
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell inside the VM.'
}

$adminPassword = Read-Host -AsSecureString 'Password for the local automation admin "labadmin"'
$childPassword = Read-Host -AsSecureString 'Password for the child test accounts'

# Group names are localized; resolve them by well-known SID.
$administrators = (Get-LocalGroup -SID 'S-1-5-32-544').Name
$users = (Get-LocalGroup -SID 'S-1-5-32-545').Name

if (-not (Get-LocalUser -Name 'labadmin' -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name 'labadmin' -Password $adminPassword -FullName 'Lab automation' -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -Group $administrators -Member 'labadmin'
    Write-Host 'Created local admin: labadmin' -ForegroundColor Green
}

foreach ($child in $Children) {
    if (Get-LocalUser -Name $child -ErrorAction SilentlyContinue) {
        Write-Warning "$child already exists - skipped"
        continue
    }
    New-LocalUser -Name $child -Password $childPassword -FullName $child -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -Group $users -Member $child
    Write-Host "Created standard user: $child" -ForegroundColor Green
}

# The hub's inbound firewall rule and mDNS discovery are scoped to Private networks.
Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
Write-Host 'Network profile set to Private'

if ($InstallChrome) {
    winget install --id Google.Chrome --exact --silent --accept-package-agreements --accept-source-agreements
}

Write-Host ''
Write-Host "Members of ${administrators}:"
Get-LocalGroupMember -Group $administrators | ForEach-Object { "  $($_.Name)" }

if ($env:COMPUTERNAME -ne $ComputerName) {
    Rename-Computer -NewName $ComputerName -Force
    Write-Host "Computer renamed to $ComputerName - restart the VM to apply." -ForegroundColor Yellow
}
