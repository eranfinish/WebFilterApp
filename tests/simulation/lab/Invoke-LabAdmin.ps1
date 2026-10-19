<#
.SYNOPSIS
Sends admin-protocol messages to the WebFilter service inside a lab VM and returns the replies.

.DESCRIPTION
Runs over PowerShell Direct as labadmin (C:\Lab\cred-<vm>.xml, see Save-LabCredential.ps1), so the admin pipe sees an
elevated administrator. Framing is the service's: a 4-byte little-endian length and UTF-8 JSON whose first property is
"type" (src/WebFilter.Contracts/Admin/AdminMessages.cs). Each message gets an id; replies come back as objects.

.EXAMPLE
.\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"createPairingCode","v":1}'

.EXAMPLE
.\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"getStatus","v":1}', '{"type":"getDevices","v":1}'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $VMName,
    [Parameter(Mandatory)] [string[]] $Message,
    [string] $PipeName = 'WebFilter.Admin'
)
$ErrorActionPreference = 'Stop'
$credential = Import-Clixml (Join-Path 'C:\Lab' "cred-$VMName.xml")

$replies = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $PipeName, $Message -ScriptBlock {
    param($pipeName, [string[]] $messages)
    $ErrorActionPreference = 'Stop'
    function Read-Exactly($stream, [int] $count) {
        $buffer = New-Object byte[] $count
        $offset = 0
        while ($offset -lt $count) {
            $read = $stream.Read($buffer, $offset, $count - $offset)
            if ($read -le 0) { throw 'The service closed the pipe.' }
            $offset += $read
        }
        return , $buffer
    }
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $i = 0
        foreach ($json in $messages) {
            $i++
            # Insert the id right after the "type" property, which must stay first.
            $withId = $json -replace '^\s*\{\s*("type"\s*:\s*"[^"]*")\s*,?', ('{$1,"id":"lab-' + $i + '",')
            $withId = $withId -replace ',\s*\}$', '}'
            $body = [Text.Encoding]::UTF8.GetBytes($withId)
            $pipe.Write([BitConverter]::GetBytes([int] $body.Length), 0, 4)
            $pipe.Write($body, 0, $body.Length)
            $pipe.Flush()
            $length = [BitConverter]::ToInt32((Read-Exactly $pipe 4), 0)
            [Text.Encoding]::UTF8.GetString((Read-Exactly $pipe $length))
        }
    }
    finally {
        $pipe.Dispose()
    }
}
$replies | ForEach-Object { $_ | ConvertFrom-Json }
