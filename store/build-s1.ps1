<#
.SYNOPSIS
    Packages store/extension-s1 into store/dist/webfilter-s1-<version>.zip for the Chrome Web Store and Edge Add-ons.

.DESCRIPTION
    - manifest.json is at the zip root; entry names use forward slashes
      (Compress-Archive in Windows PowerShell 5.1 writes backslashes, which the stores can reject).
    - Fails if the manifest has a "key", any permission, host permission or content script: the S1 build must
      request nothing, so that review is quick. The full product is a later version of the same store item.
    - Fails if a file the manifest references is missing, or a locale's description is over 132 characters
      (the Chrome Web Store summary limit).
    - Skips _metadata/ (created by the browser in unpacked extensions) and dotfiles.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File store\build-s1.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$storeDir = $PSScriptRoot
$sourceDir = (Resolve-Path (Join-Path $storeDir 'extension-s1')).Path
$distDir = Join-Path $storeDir 'dist'

function Fail([string] $message) {
    throw "build-s1: $message"
}

function Test-Property($object, [string] $name) {
    return ($null -ne $object) -and ($object.PSObject.Properties.Name -contains $name)
}

# --- Manifest checks ---------------------------------------------------------------------------------------
$manifest = Get-Content -Raw -Encoding UTF8 (Join-Path $sourceDir 'manifest.json') | ConvertFrom-Json

if ($manifest.manifest_version -ne 3) { Fail 'manifest_version must be 3' }
$version = [string] $manifest.version
if ($version -notmatch '^\d+(\.\d+){0,3}$') { Fail "invalid version '$version'" }

$forbidden = @('key', 'permissions', 'optional_permissions', 'host_permissions', 'optional_host_permissions',
    'content_scripts', 'externally_connectable', 'web_accessible_resources')
foreach ($name in $forbidden) {
    if (Test-Property $manifest $name) { Fail "the S1 build must not declare '$name'" }
}

$referenced = New-Object System.Collections.Generic.List[string]
foreach ($p in $manifest.icons.PSObject.Properties) { $referenced.Add([string] $p.Value) }
if (Test-Property $manifest 'action') {
    if (Test-Property $manifest.action 'default_popup') { $referenced.Add([string] $manifest.action.default_popup) }
    if (Test-Property $manifest.action 'default_icon') {
        foreach ($p in $manifest.action.default_icon.PSObject.Properties) { $referenced.Add([string] $p.Value) }
    }
}
$defaultLocale = [string] $manifest.default_locale
$referenced.Add("_locales/$defaultLocale/messages.json")
foreach ($relative in $referenced) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $relative) -PathType Leaf)) {
        Fail "manifest references missing file '$relative'"
    }
}

foreach ($localeDir in Get-ChildItem -LiteralPath (Join-Path $sourceDir '_locales') -Directory) {
    $messages = Get-Content -Raw -Encoding UTF8 (Join-Path $localeDir.FullName 'messages.json') | ConvertFrom-Json
    if (-not (Test-Property $messages 'extDescription')) { Fail "_locales/$($localeDir.Name) has no extDescription" }
    $length = ([string] $messages.extDescription.message).Length
    if ($length -gt 132) { Fail "_locales/$($localeDir.Name) description is $length characters (max 132)" }
    Write-Host ("  description [{0}]: {1} characters" -f $localeDir.Name, $length)
}

# --- Package -----------------------------------------------------------------------------------------------
$files = Get-ChildItem -LiteralPath $sourceDir -Recurse -File -Force |
    ForEach-Object {
        $relative = $_.FullName.Substring($sourceDir.Length).TrimStart('\', '/') -replace '\\', '/'
        [pscustomobject] @{ Path = $_.FullName; Entry = $relative }
    } |
    Where-Object { $_.Entry -notmatch '^_metadata/' -and $_.Entry -notmatch '(^|/)\.' } |
    Sort-Object Entry

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$zipPath = Join-Path $distDir "webfilter-s1-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.Path, $file.Entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

# --- Verify ------------------------------------------------------------------------------------------------
$check = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($check.Entries | ForEach-Object { $_.FullName })
}
finally {
    $check.Dispose()
}
if ($entries -notcontains 'manifest.json') { Fail 'manifest.json is not at the zip root' }
if (@($entries | Where-Object { $_ -match '\\' }).Count -gt 0) { Fail 'zip entry names contain backslashes' }

$entries | ForEach-Object { Write-Host "  $_" }
$size = (Get-Item -LiteralPath $zipPath).Length
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Created $zipPath"
Write-Host "  version $version, $($entries.Count) files, $size bytes"
Write-Host "  SHA256 $hash"
