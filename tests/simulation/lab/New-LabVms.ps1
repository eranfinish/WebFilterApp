<#
.SYNOPSIS
Creates the WebFilter Hyper-V lab VMs (Generation 2, Secure Boot, virtual TPM), ready for a Windows 11 install.

.DESCRIPTION
Run on the host from an elevated PowerShell, or as a member of "Hyper-V Administrators".
Existing VMs with the same name are left untouched.

.EXAMPLE
.\New-LabVms.ps1 -IsoPath C:\Lab\iso\Win11_x64.iso

.EXAMPLE
.\New-LabVms.ps1 -IsoPath C:\Lab\iso\Win11_x64.iso -Name lab-laptop
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $IsoPath,
    [string[]] $Name = @('lab-hub', 'lab-kids'),
    [string] $Root = 'C:\Lab\vms',
    [int] $MemoryGB = 4,
    [int] $DiskGB = 80,
    [int] $Cpu = 2,
    [string] $SwitchName = 'Default Switch'
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$hyperVAdmins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-578')
if (-not ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or $principal.IsInRole($hyperVAdmins))) {
    throw 'Run from an elevated PowerShell, or add your account to "Hyper-V Administrators" and sign in again.'
}
if (-not (Test-Path $IsoPath)) { throw "ISO not found: $IsoPath" }
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
    throw "Virtual switch '$SwitchName' not found. Run Get-VMSwitch to see the available switches."
}

foreach ($vmName in $Name) {
    if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) {
        Write-Warning "$vmName already exists - skipped"
        continue
    }
    $vhd = Join-Path $Root "$vmName\$vmName.vhdx"
    New-VM -Name $vmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath $vhd -NewVHDSizeBytes ($DiskGB * 1GB) `
        -SwitchName $SwitchName -Path $Root | Out-Null
    Set-VM -Name $vmName -ProcessorCount $Cpu -DynamicMemory -MemoryMinimumBytes 2GB -MemoryMaximumBytes (($MemoryGB + 2) * 1GB) `
        -AutomaticCheckpointsEnabled $false -CheckpointType Standard
    # Windows 11 requires TPM 2.0 and Secure Boot.
    Set-VMKeyProtector -VMName $vmName -NewLocalKeyProtector
    Enable-VMTPM -VMName $vmName
    Set-VMFirmware -VMName $vmName -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    $dvd = Add-VMDvdDrive -VMName $vmName -Path $IsoPath -Passthru
    Set-VMFirmware -VMName $vmName -FirstBootDevice $dvd
    # Lets the host copy files in with Copy-VMFile.
    Enable-VMIntegrationService -VMName $vmName -Name 'Guest Service Interface'
    Write-Host "Created $vmName ($MemoryGB GB RAM, $Cpu vCPU, $DiskGB GB disk, vTPM, Secure Boot)" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Next, for each VM:  Start-VM -Name <vm>;  vmconnect localhost <vm>  - then press a key to boot from the ISO.'
