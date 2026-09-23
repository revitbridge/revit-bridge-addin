<#
.SYNOPSIS
    Installs the revit-bridge Revit add-in for the current user.

.DESCRIPTION
    Downloads the latest GitHub Release (or uses a local build output), verifies
    the SHA-256 checksum, backs up any existing install, copies the add-in into
    %APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\ and writes the connection
    settings into Commands\commandRegistry.json.

    One-line install (local TCP mode, nothing to configure):

        irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1 | iex

    With parameters (remote mode): get a pairing code from the site first.

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -Mode remote -Server https://example.com -Pair XXXX-XXXX

    From a local build:

        .\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"

.PARAMETER Mode
    local  - Revit listens on TCP 127.0.0.1:18080 for a local MCP server (default).
    remote - Revit connects out to a WebSocket bridge server (-Server required).

.PARAMETER Server
    Site address of the bridge server, e.g. https://bridge.example.com.
    Remote mode only. The WebSocket URL is whatever the server returns when
    the pairing code is redeemed; it is never assembled here.

.PARAMETER Source
    "release" (default): download the release zip from GitHub and verify it
    against SHA256SUMS.txt. Otherwise a local directory: an extracted release
    zip or a build output such as "plugin\bin\AddIn 2026 Release R26".

.PARAMETER Pair
    Pairing code from the site, in the form XXXX-XXXX. Remote mode only.
    The installer redeems it at <Server>/api/v1/bridge/devices/redeem and
    stores the device id, device token and WebSocket URL it gets back. Codes
    are single use and expire after 10 minutes. Omit it to keep the pairing
    from a previous install, or pair later in Settings > Connection.

.PARAMETER AllowRemoteCode
    Remote mode: allow send_code_to_revit and manage_solidified_tools. When
    off (default) the add-in rejects those methods.

.PARAMETER RevitVersion
    Revit year to install into. Default 2026 (the only version the official
    Release is built for).

.PARAMETER Repo
    GitHub repository to download releases from. Forks set this to their own
    repository. Default revitbridge/revit-bridge-addin.

.PARAMETER Tag
    Release tag to install, e.g. v0.1.0. Default "latest".
#>
[CmdletBinding()]
param(
    [ValidateSet("local", "remote")]
    [string]$Mode = "local",
    [string]$Server = "",
    [string]$Source = "release",
    [string]$Pair = "",
    [switch]$AllowRemoteCode,
    [ValidatePattern('^20\d\d$')]
    [string]$RevitVersion = "2026",
    [string]$Repo = "revitbridge/revit-bridge-addin",
    [string]$Tag = "latest"
)

$ErrorActionPreference = "Stop"
$addinClassName = "revit_mcp_plugin.Core.Application"
$userAgent = "revit-bridge-addin-installer"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Get-ReleaseSource {
    param([string]$Repo, [string]$Tag)

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    if ($Tag -eq "latest") {
        $apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
    } else {
        $apiUrl = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    }

    Write-Host "Looking up release: $apiUrl"
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = $userAgent }

    $zipAsset = $release.assets | Where-Object { $_.name -like "revit-bridge-addin-*.zip" } | Select-Object -First 1
    $sumsAsset = $release.assets | Where-Object { $_.name -eq "SHA256SUMS.txt" } | Select-Object -First 1
    if ($null -eq $zipAsset) {
        throw "Release $($release.tag_name) has no revit-bridge-addin-*.zip asset."
    }
    if ($null -eq $sumsAsset) {
        throw "Release $($release.tag_name) has no SHA256SUMS.txt asset; refusing to install an unverified package."
    }

    $workDir = Join-Path $env:TEMP ("revit-bridge-addin-install-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    $zipPath = Join-Path $workDir $zipAsset.name
    $sumsPath = Join-Path $workDir $sumsAsset.name

    Write-Host "Downloading $($zipAsset.name) ($($release.tag_name))"
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -Headers @{ "User-Agent" = $userAgent } -UseBasicParsing
    Invoke-WebRequest -Uri $sumsAsset.browser_download_url -OutFile $sumsPath -Headers @{ "User-Agent" = $userAgent } -UseBasicParsing

    $expected = $null
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -match '^\s*([0-9A-Fa-f]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $zipAsset.name) {
            $expected = $Matches[1].ToLowerInvariant()
            break
        }
    }
    if ($null -eq $expected) {
        throw "SHA256SUMS.txt has no entry for $($zipAsset.name)."
    }
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Checksum mismatch for $($zipAsset.name). Expected $expected but got $actual."
    }
    Write-Host "Checksum verified: $actual"

    $extractDir = Join-Path $workDir "extracted"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    # Tolerate a zip that wraps everything in a single top-level folder.
    if (-not (Get-ChildItem -LiteralPath $extractDir -Filter "*.addin" -File)) {
        $children = @(Get-ChildItem -LiteralPath $extractDir)
        if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
            $extractDir = $children[0].FullName
        }
    }

    return [pscustomobject]@{ Path = $extractDir; Label = "release $($release.tag_name)" }
}

function Resolve-SourceLayout {
    param([string]$Path)

    $manifests = @(Get-ChildItem -LiteralPath $Path -Filter "*.addin" -File)
    if ($manifests.Count -ne 1) {
        throw "Expected exactly one .addin manifest in $Path but found $($manifests.Count)."
    }
    $manifest = $manifests[0]

    [xml]$xml = Get-Content -LiteralPath $manifest.FullName -Raw
    $addIn = $xml.RevitAddIns.AddIn
    if ($null -eq $addIn) {
        throw "$($manifest.Name) is not a Revit add-in manifest."
    }
    $assemblyRel = [string]$addIn.Assembly
    $folderName = Split-Path -Parent $assemblyRel
    if ([string]::IsNullOrWhiteSpace($folderName)) {
        throw "$($manifest.Name): <Assembly> must be a relative path like <folder>/RevitMCPPlugin.dll."
    }

    $pluginDir = Join-Path $Path $folderName
    $mainDll = Join-Path $Path ($assemblyRel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $mainDll)) {
        throw "Source is missing $assemblyRel. Build both projects first (see README > Build)."
    }
    $registry = Join-Path $pluginDir "Commands\commandRegistry.json"
    if (-not (Test-Path -LiteralPath $registry)) {
        throw "Source is missing $folderName\Commands\commandRegistry.json."
    }

    return [pscustomobject]@{
        Manifest   = $manifest
        FolderName = $folderName
        PluginDir  = $pluginDir
        ClientId   = [string]$addIn.ClientId
        Name       = [string]$addIn.Name
    }
}

function Invoke-PairingRedeem {
    param([string]$Server, [string]$Code)

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    $url = $Server.TrimEnd('/') + "/api/v1/bridge/devices/redeem"
    $body = @{ code = $Code; addin_version = "installer" } | ConvertTo-Json -Compress

    Write-Host "Redeeming pairing code at $url"
    try {
        $reply = Invoke-RestMethod -Uri $url -Method Post -Body $body -ContentType "application/json" -Headers @{ "User-Agent" = $userAgent }
    } catch {
        $response = $_.Exception.Response
        $status = $null
        if ($null -ne $response) { $status = [int]$response.StatusCode }
        $detail = ""
        try {
            if ($null -ne $response) {
                $reader = New-Object IO.StreamReader($response.GetResponseStream())
                $text = $reader.ReadToEnd()
                $reader.Close()
                if ($text) {
                    $parsed = $text | ConvertFrom-Json
                    if ($null -ne $parsed.message) { $detail = [string]$parsed.message }
                    elseif ($null -ne $parsed.error) { $detail = [string]$parsed.error }
                }
            }
        } catch { }

        if ($status -eq 404) {
            throw "Pairing code $Code was refused (invalid, already used, or expired). Generate a new one on the site. $detail".Trim()
        }
        throw "Could not redeem the pairing code at $url : $($_.Exception.Message) $detail".Trim()
    }

    if ([string]::IsNullOrWhiteSpace($reply.device_id) -or
        [string]::IsNullOrWhiteSpace($reply.device_token) -or
        [string]::IsNullOrWhiteSpace($reply.ws_url)) {
        throw "The server reply is missing device_id, device_token or ws_url."
    }
    if ([string]$reply.ws_url -notmatch '^wss?://') {
        throw "The server returned an unexpected ws_url: $($reply.ws_url)"
    }

    Write-Host "Paired as $($reply.device_id)"
    return [pscustomobject]@{
        DeviceId = [string]$reply.device_id
        Token    = [string]$reply.device_token
        WsUrl    = ([string]$reply.ws_url).TrimEnd('/')
    }
}

function Get-FileSha256 {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return "(missing)"
}

# ---------------------------------------------------------------------------
# Validate input
# ---------------------------------------------------------------------------

if ($Mode -eq "remote") {
    if ([string]::IsNullOrWhiteSpace($Server)) {
        throw "-Server is required in remote mode, e.g. -Server https://bridge.example.com"
    }
    if ($Server -match '^wss?://') {
        throw "-Server is the site address (https://host), not the WebSocket URL. The server returns the WebSocket URL when the pairing code is redeemed."
    }
    if ($Server -notmatch '^https://' -and $Server -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?/?$') {
        throw "-Server must start with https:// (http:// is only allowed for localhost)."
    }
}

if (-not [string]::IsNullOrWhiteSpace($Pair)) {
    if ($Mode -ne "remote") {
        throw "-Pair only applies to -Mode remote."
    }
    $Pair = $Pair.Trim().ToUpperInvariant()
    if ($Pair -match '^[A-Z0-9]{8}$') {
        $Pair = $Pair.Substring(0, 4) + "-" + $Pair.Substring(4)
    }
    if ($Pair -notmatch '^[A-Z0-9]{4}-[A-Z0-9]{4}$') {
        throw "-Pair must be a pairing code in the form XXXX-XXXX."
    }
}

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw "Close Revit before installing or updating the add-in."
}

# ---------------------------------------------------------------------------
# Resolve source
# ---------------------------------------------------------------------------

if ($Source -eq "release") {
    $resolved = Get-ReleaseSource -Repo $Repo -Tag $Tag
    $sourcePath = $resolved.Path
    $sourceLabel = $resolved.Label
} else {
    $sourcePath = (Resolve-Path -LiteralPath $Source).Path
    $sourceLabel = "local directory"
}

$layout = Resolve-SourceLayout -Path $sourcePath
Write-Host "Source: $sourceLabel ($sourcePath)"
Write-Host "Add-in: $($layout.Name), ClientId $($layout.ClientId), folder $($layout.FolderName)"

# ---------------------------------------------------------------------------
# Install files
# ---------------------------------------------------------------------------

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$pluginDestination = Join-Path $addinsRoot $layout.FolderName
$addinDestination = Join-Path $addinsRoot $layout.Manifest.Name
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

New-Item -ItemType Directory -Path $addinsRoot -Force | Out-Null

$previousRegistry = $null
if (Test-Path -LiteralPath $pluginDestination) {
    $backupDestination = "$pluginDestination.backup-$stamp"
    Move-Item -LiteralPath $pluginDestination -Destination $backupDestination
    Write-Host "Existing add-in backed up to: $backupDestination"
    $candidate = Join-Path $backupDestination "Commands\commandRegistry.json"
    if (Test-Path -LiteralPath $candidate) {
        $previousRegistry = $candidate
    }
}
if (Test-Path -LiteralPath $addinDestination) {
    Copy-Item -LiteralPath $addinDestination -Destination "$addinDestination.backup-$stamp" -Force
}

Copy-Item -LiteralPath $layout.PluginDir -Destination $addinsRoot -Recurse -Force
Copy-Item -LiteralPath $layout.Manifest.FullName -Destination $addinDestination -Force

# ---------------------------------------------------------------------------
# Write connection settings
# ---------------------------------------------------------------------------

$registryDestination = Join-Path $pluginDestination "Commands\commandRegistry.json"
$config = Get-Content -LiteralPath $registryDestination -Raw | ConvertFrom-Json

# Carry the previous pairing over an update: deviceId, token and wsUrl belong
# to this machine, not to the package.
$existingWsUrl = ""
$existingToken = ""
$existingDeviceId = ""
$existingConfirmEachRun = $true
if ($null -ne $previousRegistry) {
    try {
        $previous = Get-Content -LiteralPath $previousRegistry -Raw | ConvertFrom-Json
        if ($null -ne $previous.settings) {
            if ($null -ne $previous.settings.token) { $existingToken = [string]$previous.settings.token }
            if ($null -ne $previous.settings.deviceId) { $existingDeviceId = [string]$previous.settings.deviceId }
            if ($null -ne $previous.settings.wsUrl) { $existingWsUrl = [string]$previous.settings.wsUrl }
            if ($null -ne $previous.settings.confirmEachRun) { $existingConfirmEachRun = [bool]$previous.settings.confirmEachRun }
        }
    } catch {
        Write-Warning "Could not read settings from the previous install: $($_.Exception.Message)"
    }
}

$wsUrl = $existingWsUrl
$deviceId = $existingDeviceId
$effectiveToken = $existingToken

if (-not [string]::IsNullOrWhiteSpace($Pair)) {
    $paired = Invoke-PairingRedeem -Server $Server -Code $Pair
    $deviceId = $paired.DeviceId
    $effectiveToken = $paired.Token
    $wsUrl = $paired.WsUrl
} elseif ($Mode -eq "remote") {
    if ([string]::IsNullOrWhiteSpace($deviceId) -or [string]::IsNullOrWhiteSpace($wsUrl)) {
        Write-Warning "This Revit is not paired yet. Re-run with -Pair XXXX-XXXX, or enter a pairing code in Settings > Connection."
    } else {
        Write-Host "Kept the pairing from the previous install ($deviceId)."
    }
}

if ($Mode -eq "remote") {
    $modeValue = "websocket"
} else {
    $modeValue = "tcp"
}

$settings = [pscustomobject]@{
    logLevel = "Info"
    port = 18080
    mode = $modeValue
    wsUrl = $wsUrl
    deviceId = $deviceId
    token = $effectiveToken
    confirmEachRun = $existingConfirmEachRun
    allowRemoteCodeExecution = [bool]$AllowRemoteCode
}

if ($null -eq $config.settings) {
    $config | Add-Member -NotePropertyName settings -NotePropertyValue $settings
} else {
    $config.settings = $settings
}
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $registryDestination -Encoding UTF8

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

$mainDllDestination = Join-Path $pluginDestination "RevitMCPPlugin.dll"
$commandDllDestination = Join-Path $pluginDestination "Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll"
if (-not (Test-Path -LiteralPath $commandDllDestination)) {
    Write-Warning "Command set for Revit $RevitVersion not found at $commandDllDestination. Only the add-in shell was installed."
}

# Other manifests that load the same add-in class: upstream mcp-servers-for-revit
# or an older copy of this add-in. They use a different ClientId and folder, so
# they can coexist, but the user should know they are there.
$manifestRoots = @(
    $addinsRoot,
    (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion")
) | Select-Object -Unique
$otherManifests = @()
foreach ($manifestRoot in $manifestRoots) {
    if (-not (Test-Path -LiteralPath $manifestRoot)) { continue }
    foreach ($manifest in Get-ChildItem -LiteralPath $manifestRoot -Filter "*.addin" -File -ErrorAction SilentlyContinue) {
        if ($manifest.FullName -eq $addinDestination) { continue }
        if (Select-String -LiteralPath $manifest.FullName -SimpleMatch $addinClassName -Quiet) {
            $otherManifests += $manifest.FullName
        }
    }
}

Write-Host ""
Write-Host "Installed to:        $pluginDestination"
Write-Host "Manifest:            $addinDestination"
Write-Host "Source:              $sourceLabel"
Write-Host "Mode:                $Mode"
if ($Mode -eq "remote") {
    if ([string]::IsNullOrWhiteSpace($deviceId)) {
        Write-Host "Server:              $Server (not paired yet)"
    } else {
        Write-Host "Server:              $wsUrl"
        Write-Host "Device:              $deviceId"
    }
} else {
    Write-Host "TCP endpoint:        127.0.0.1:18080"
}
Write-Host "Confirm each run:    $existingConfirmEachRun"
Write-Host "Remote code allowed: $([bool]$AllowRemoteCode)"
Write-Host "RevitMCPPlugin.dll SHA-256:     $(Get-FileSha256 $mainDllDestination)"
Write-Host "RevitMCPCommandSet.dll SHA-256: $(Get-FileSha256 $commandDllDestination)"
if ($otherManifests.Count -gt 0) {
    Write-Warning "Other manifests load the same add-in class ($addinClassName):"
    foreach ($manifestPath in $otherManifests) {
        Write-Warning "  $manifestPath"
    }
    Write-Warning "They can coexist with revit-bridge (different ClientId and folder). If the 'Revit MCP Plugin' ribbon panel is missing or duplicated, remove one of them."
}
Write-Host ""
Write-Host "Start Revit $RevitVersion, then click 'Revit MCP Switch' on the 'Revit MCP Plugin' ribbon panel."
