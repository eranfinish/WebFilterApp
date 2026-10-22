<#
.SYNOPSIS
Sets up the lab family on the hub: noa (child) and yoav (teen), linked to their accounts on every paired computer.

.DESCRIPTION
Reads the hub's settings (getSettings), keeps its devices and accounts, and replaces the rest (replaceSettings with
ifVersion) with the role defaults of docs/PROTECTION-SPEC.md:
- noa: child, allowlist, YouTube restricted; blockImageSearch, jumpGuard and blockYouTubeShorts on; educational and
  school-tools allowed, plus the school sign-in prefix rules (section 11).
- yoav: teen, blocklist, YouTube restricted; jumpGuard on; adult, gambling, proxies, unfiltered-search and ads-popups
  blocked (ai-chat is offered but not checked).
The lists come from data/starter-lists. Accounts named noa and yoav are linked on every device; other non-admin accounts
stay unlinked (restricted). No schedules or limits, so time never runs out during a lab check.

.EXAMPLE
.\Set-LabFamily.ps1

.EXAMPLE
.\Set-LabFamily.ps1 -LogSearches
#>
[CmdletBinding()]
param(
    [ValidatePattern('^lab-[a-z0-9-]+$')] [string] $HubVM = 'lab-hub',
    # Turns on the search-term log for both members (the extension tells them it is on).
    [switch] $LogSearches
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$admin = Join-Path $PSScriptRoot 'Invoke-LabAdmin.ps1'

$current = & $admin -VMName $HubVM -Message '{"type":"getSettings","v":1}'
if (-not $current.settings) { $current | ConvertTo-Json -Depth 5; throw 'getSettings failed' }
$settings = $current.settings

function Read-List([string] $name) {
    $file = Join-Path $repo "data\starter-lists\$name.json"
    , @((Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json).domains)
}

$allowForChild = 'educational', 'school-tools'
$blockForTeen = 'adult', 'gambling', 'proxies', 'unfiltered-search', 'ads-popups'
$categories = [ordered]@{}
foreach ($name in $allowForChild + $blockForTeen) { $categories[$name] = Read-List $name }

$devices = @(foreach ($device in $settings.devices) {
    [ordered]@{
        id = $device.id; os = $device.os; role = $device.role
        accounts = @(foreach ($account in $device.accounts) {
            $entry = [ordered]@{ accountId = $account.accountId; name = $account.name; isAdmin = [bool] $account.isAdmin }
            if (-not $account.isAdmin -and $account.name -in 'noa', 'yoav') { $entry.member = $account.name }
            $entry
        })
    }
})

$bundle = [ordered]@{
    timeZone = $settings.timeZone
    members = @(
        [ordered]@{ id = 'noa'; displayName = (-join [char[]](0x05E0, 0x05D5, 0x05E2, 0x05D4)); role = 'child'; age = 9
            filterMode = 'allowlist'; youTubeMode = 'restricted'
            blockImageSearch = $true; jumpGuard = $true; blockYouTubeShorts = $true; logSearches = [bool] $LogSearches }
        [ordered]@{ id = 'yoav'; displayName = (-join [char[]](0x05D9, 0x05D5, 0x05D0, 0x05D1)); role = 'teen'; age = 14
            filterMode = 'blocklist'; youTubeMode = 'restricted'
            blockImageSearch = $false; jumpGuard = $true; blockYouTubeShorts = $false; logSearches = [bool] $LogSearches }
    )
    devices = $devices
    categories = $categories
    memberCategories = @(
        foreach ($name in $allowForChild) { [ordered]@{ member = 'noa'; category = $name; action = 'allow' } }
        foreach ($name in $blockForTeen) { [ordered]@{ member = 'yoav'; category = $name; action = 'block' } }
    )
    rules = @(
        [ordered]@{ id = 'builtin-school-signin-a'; pattern = 'www.google.com/a/'; type = 'prefix'; action = 'allow' }
        [ordered]@{ id = 'builtin-school-signin-recaptcha'; pattern = 'www.google.com/recaptcha/'; type = 'prefix'; action = 'allow' }
    )
    schedules = @(); dailyLimits = @(); youTubeChannels = @(); appRules = @()
    supportedBrowsers = @($settings.supportedBrowsers)
    windowsFeatures = $settings.windowsFeatures
}

$message = [ordered]@{ type = 'replaceSettings'; v = 1; settings = $bundle; ifVersion = $settings.version } | ConvertTo-Json -Depth 10 -Compress
$reply = & $admin -VMName $HubVM -Message $message
if ($reply.type -ne 'ok') { $reply | ConvertTo-Json -Depth 5; throw 'replaceSettings failed' }

$linked = foreach ($device in $devices) { foreach ($a in $device.accounts) { if ($a.member) { "$($device.id)\$($a.name) -> $($a.member)" } } }
"Family set on $HubVM (was version $($settings.version)). Linked: $($linked -join ', ')"
'Lists: ' + (($categories.Keys | ForEach-Object { "$_ $($categories[$_].Count)" }) -join ', ')
