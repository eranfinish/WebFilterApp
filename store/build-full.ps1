<#
.SYNOPSIS
    Builds the full extension from extension/ and packages it into store/dist/webfilter-<version>.zip, the update for
    the existing Chrome Web Store and Edge Add-ons items.

.DESCRIPTION
    - Runs a fresh production build (`npm run build` after deleting extension/dist), so the zip never contains the
      _metadata/ folder a browser writes into an unpacked extension, nor the test hooks of dist-test/.
    - Fails if:
        * the version is not higher than the version already in the stores (-PublishedVersion);
        * the manifest has a "key" (the stores assign the key; a key would change nothing but is rejected);
        * the permissions, host permissions or content-script matches differ from the reviewed list below. A new
          permission changes what reviewers and parents see, so it must be added here on purpose, together with its
          justification in store/listing/full-product.md;
        * a file the manifest references is missing, the ad ruleset's notice is missing, or a locale's description is
          over 132 characters (the Chrome Web Store summary limit);
        * the bundle contains eval, new Function, fetch, XMLHttpRequest or WebSocket (the extension sends nothing over
          the network and runs no remote code; the store answers say so).
    - Entry names use forward slashes (Compress-Archive in Windows PowerShell 5.1 writes backslashes, which the stores
      can reject). Skips dotfiles.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File store\build-full.ps1
#>
[CmdletBinding()]
param(
    # The version both stores have now (S1). The upload must be higher.
    [string] $PublishedVersion = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$storeDir = $PSScriptRoot
$extensionDir = (Resolve-Path (Join-Path $storeDir '..\extension')).Path
$sourceDir = Join-Path $extensionDir 'dist'
$distDir = Join-Path $storeDir 'dist'

# What the published package may ask for. Each entry has a justification in store/listing/full-product.md.
$expectedPermissions = @('alarms', 'declarativeNetRequest', 'idle', 'nativeMessaging', 'notifications', 'storage', 'tabs', 'webNavigation')
$expectedHostPermissions = @('<all_urls>')
$expectedContentScriptMatches = @('*://*.youtube-nocookie.com/*', '*://*.youtube.com/*')

function Fail([string] $message) {
    throw "build-full: $message"
}

function Test-Property($object, [string] $name) {
    return ($null -ne $object) -and ($object.PSObject.Properties.Name -contains $name)
}

function ConvertTo-Version([string] $value) {
    $parts = @($value.Split('.'))
    while ($parts.Count -lt 4) { $parts += '0' }
    return [version] ($parts[0..3] -join '.')
}

function Same([object[]] $actual, [object[]] $expected) {
    $a = @($actual | ForEach-Object { [string] $_ } | Sort-Object -Unique)
    $e = @($expected | Sort-Object -Unique)
    return ($a.Count -eq $e.Count) -and (@(Compare-Object $a $e).Count -eq 0)
}

# --- Fresh production build --------------------------------------------------------------------------------
if (Test-Path -LiteralPath $sourceDir) { Remove-Item -LiteralPath $sourceDir -Recurse -Force }
Push-Location $extensionDir
try {
    & npm run build
    if ($LASTEXITCODE -ne 0) { Fail "npm run build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

# --- Manifest checks ---------------------------------------------------------------------------------------
$manifest = Get-Content -Raw -Encoding UTF8 (Join-Path $sourceDir 'manifest.json') | ConvertFrom-Json

if ($manifest.manifest_version -ne 3) { Fail 'manifest_version must be 3' }
$version = [string] $manifest.version
if ($version -notmatch '^\d+(\.\d+){0,3}$') { Fail "invalid version '$version'" }
if ((ConvertTo-Version $version) -le (ConvertTo-Version $PublishedVersion)) {
    Fail "version $version is not higher than the published $PublishedVersion"
}
if (Test-Property $manifest 'key') { Fail 'the manifest must not have a "key"' }

if (-not (Same $manifest.permissions $expectedPermissions)) {
    Fail ("permissions changed: {0} (expected {1}); update the list here and the justifications" -f (($manifest.permissions | Sort-Object) -join ', '), ($expectedPermissions -join ', '))
}
if (Test-Property $manifest 'optional_permissions') { Fail 'unexpected optional_permissions' }
if (-not (Same $manifest.host_permissions $expectedHostPermissions)) { Fail "host_permissions changed: $($manifest.host_permissions -join ', ')" }
$scriptMatches = @($manifest.content_scripts | ForEach-Object { $_.matches } | ForEach-Object { $_ })
if (-not (Same $scriptMatches $expectedContentScriptMatches)) { Fail "content script matches changed: $($scriptMatches -join ', ')" }

$referenced = New-Object System.Collections.Generic.List[string]
foreach ($p in $manifest.icons.PSObject.Properties) { $referenced.Add([string] $p.Value) }
$referenced.Add([string] $manifest.action.default_popup)
foreach ($p in $manifest.action.default_icon.PSObject.Properties) { $referenced.Add([string] $p.Value) }
$referenced.Add([string] $manifest.background.service_worker)
foreach ($script in $manifest.content_scripts) { foreach ($js in $script.js) { $referenced.Add([string] $js) } }
foreach ($ruleset in $manifest.declarative_net_request.rule_resources) { $referenced.Add([string] $ruleset.path) }
foreach ($resource in $manifest.web_accessible_resources) {
    foreach ($r in $resource.resources) { if ($r -notmatch '\*') { $referenced.Add([string] $r) } }
}
$referenced.Add("_locales/$($manifest.default_locale)/messages.json")
$referenced.Add('rulesets/ads-notice.txt')   # EasyList attribution (CC BY-SA) must ship with the ruleset
foreach ($relative in $referenced) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $relative) -PathType Leaf)) {
        Fail "missing file '$relative'"
    }
}

foreach ($localeDir in Get-ChildItem -LiteralPath (Join-Path $sourceDir '_locales') -Directory) {
    $messages = Get-Content -Raw -Encoding UTF8 (Join-Path $localeDir.FullName 'messages.json') | ConvertFrom-Json
    if (-not (Test-Property $messages 'extDescription')) { Fail "_locales/$($localeDir.Name) has no extDescription" }
    $length = ([string] $messages.extDescription.message).Length
    if ($length -gt 132) { Fail "_locales/$($localeDir.Name) description is $length characters (max 132)" }
    Write-Host ("  description [{0}]: {1} characters" -f $localeDir.Name, $length)
}

foreach ($js in Get-ChildItem -LiteralPath $sourceDir -Filter *.js -File) {
    $text = Get-Content -Raw -Encoding UTF8 $js.FullName
    foreach ($pattern in 'eval\(', 'new Function\(', '\bfetch\(', 'XMLHttpRequest', 'WebSocket', 'wfTest') {
        if ($text -match $pattern) { Fail "$($js.Name) contains '$pattern'" }
    }
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
$zipPath = Join-Path $distDir "webfilter-$version.zip"
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
if (@($entries | Where-Object { $_ -match '^_metadata/' }).Count -gt 0) { Fail 'the zip contains _metadata/' }

$entries | ForEach-Object { Write-Host "  $_" }
$size = (Get-Item -LiteralPath $zipPath).Length
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Created $zipPath"
Write-Host "  version $version (stores have $PublishedVersion), $($entries.Count) files, $size bytes"
Write-Host "  permissions: $($expectedPermissions -join ', '); host: $($expectedHostPermissions -join ', ')"
Write-Host "  SHA256 $hash"
