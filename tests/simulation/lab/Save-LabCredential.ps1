<#
.SYNOPSIS
Saves a lab VM's labadmin credential for the automated lab tests, after checking that it works.

.DESCRIPTION
Reads the password in this console (not in the Get-Credential pop-up, which may keep a different keyboard language),
refuses non-English characters, tries it over PowerShell Direct, and only then writes C:\Lab\cred-<vm>.xml. The file is
encrypted with DPAPI: only your Windows account on this computer can read it. Nothing is printed or logged.

.EXAMPLE
.\Save-LabCredential.ps1 -VMName lab-kids
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $VMName,
    [string] $UserName = 'labadmin',
    [string] $Folder = 'C:\Lab'
)
$ErrorActionPreference = 'Stop'

$password = Read-Host -AsSecureString "Password of $VMName\$UserName (keyboard in English)"
$credential = [pscredential]::new("$VMName\$UserName", $password)
if ($credential.GetNetworkCredential().Password -match '[^\x20-\x7E]') {
    throw 'The password contains non-English characters: switch the keyboard to English (Alt+Shift) and run again. Nothing was saved.'
}

try {
    $who = Invoke-Command -VMName $VMName -Credential $credential -ErrorAction Stop -ScriptBlock { whoami }
}
catch {
    throw "The VM did not accept the password ($($_.Exception.Message)). Nothing was saved."
}

$path = Join-Path $Folder "cred-$VMName.xml"
$credential | Export-Clixml -Path $path
Write-Host "Saved $path (checked: logged in to $VMName as $who)." -ForegroundColor Green
