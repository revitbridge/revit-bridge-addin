<#
.SYNOPSIS
    Installs the revit-bridge Revit add-in for the current user.

.DESCRIPTION
    Downloads the latest GitHub Release (or uses a local build output), verifies
    the SHA-256 checksum, backs up any existing install, copies the add-in into
    %APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\ and writes the connection
    settings into Commands\commandRegistry.json.

    One-line install (local TCP mode on a fresh install, nothing to configure;
    run again to update, keeping the previous mode and pairing):

        irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1 | iex

    With parameters (remote mode): get a pairing code from the site first.

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -Mode remote -Server https://example.com -Pair XXXX-XXXX

    From a local build:

        .\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"

.PARAMETER Mode
    local  - Revit listens on TCP 127.0.0.1:18080 for a local MCP server.
    remote - Revit connects out to a WebSocket bridge server (-Server required).
    Default: the mode of the install being updated, or local for a fresh
    install. Port, logLevel and the pairing are kept as well. Switching a paired
    install to local drops its device token (it would lock the TCP port).

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
    Remote mode: allow send_code_to_revit and manage_solidified_tools.
    Pairing with -Pair switches this on, because pairing is the act of
    authorising that server; each ad-hoc run still asks in Revit while
    confirmEachRun is on. Pass -AllowRemoteCode:$false together with -Pair to
    pair without it. Without -Pair the previous setting is kept, or off for a
    fresh install. Unpairing in Settings, or the server revoking the device,
    switches it off again.

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

# Run as "irm ... | iex" this script executes in the caller's own scope, so
# what it changes there is put back when it ends, also when it fails: see the
# try/finally around the main part below.
$callerErrorActionPreference = $ErrorActionPreference
$callerProgressPreference = $ProgressPreference
$callerSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
$addinClassName = "revit_mcp_plugin.Core.Application"
$userAgent = "revit-bridge-addin-installer"
# -Mode values of this script and the "mode" values in commandRegistry.json.
$registryModes = @{ "local" = "tcp"; "remote" = "websocket" }

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Enable-Tls12 {
    # Windows PowerShell 5.1 runs on .NET Framework, whose protocol list can lack
    # TLS 1.2 (GitHub requires it). Add it without removing anything; 0
    # (SystemDefault) already lets Windows choose, TLS 1.2 and 1.3 included.
    # PowerShell 7 always uses the Windows defaults.
    if ($PSVersionTable.PSEdition -eq "Core") { return }
    try {
        $current = [int][Net.ServicePointManager]::SecurityProtocol
        $tls12 = [int][Net.SecurityProtocolType]::Tls12
        if ($current -ne 0 -and ($current -band $tls12) -eq 0) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]($current -bor $tls12)
        }
    } catch {
        Write-Warning "Could not enable TLS 1.2: $($_.Exception.Message)"
    }
}

function Get-ProxyParameter {
    param([string]$Uri)
    # When Windows routes this address through a proxy, name it explicitly so the
    # logged-on user's credentials can be sent to it: -ProxyUseDefaultCredentials
    # is only accepted together with -Proxy, on 5.1 and on 7. Without a proxy (or
    # for localhost) nothing is added and the call is the same as before.
    $parameters = @{}
    try {
        $target = [Uri]$Uri
        if ($target.IsLoopback) { return $parameters }
        $systemProxy = [Net.WebRequest]::GetSystemWebProxy()
        if ($null -ne $systemProxy -and -not $systemProxy.IsBypassed($target)) {
            $proxyUri = $systemProxy.GetProxy($target)
            if ($null -ne $proxyUri -and $proxyUri.AbsoluteUri -ne $target.AbsoluteUri) {
                $parameters["Proxy"] = $proxyUri.AbsoluteUri
                $parameters["ProxyUseDefaultCredentials"] = $true
            }
        }
    } catch {
        Write-Warning "Could not read the system proxy setting for ${Uri}: $($_.Exception.Message)"
    }
    return $parameters
}

function Get-ReleaseSource {
    param([string]$Repo, [string]$Tag)

    Enable-Tls12

    if ($Tag -eq "latest") {
        $apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
    } else {
        $apiUrl = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    }

    Write-Host "Looking up release: $apiUrl"
    $apiProxy = Get-ProxyParameter -Uri $apiUrl
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = $userAgent } @apiProxy

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
    $zipProxy = Get-ProxyParameter -Uri $zipAsset.browser_download_url
    $sumsProxy = Get-ProxyParameter -Uri $sumsAsset.browser_download_url
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -Headers @{ "User-Agent" = $userAgent } -UseBasicParsing @zipProxy
    Invoke-WebRequest -Uri $sumsAsset.browser_download_url -OutFile $sumsPath -Headers @{ "User-Agent" = $userAgent } -UseBasicParsing @sumsProxy

    $expected = $null
    foreach ($line in Get-Content -LiteralPath $sumsPath -Encoding UTF8) {
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

    # XmlReader decodes the file by its own BOM / XML declaration. A text read
    # first would decode it in the ANSI code page under Windows PowerShell 5.1.
    $readerSettings = New-Object System.Xml.XmlReaderSettings
    $readerSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $xml = New-Object System.Xml.XmlDocument
    $reader = [System.Xml.XmlReader]::Create($manifest.FullName, $readerSettings)
    try {
        $xml.Load($reader)
    } finally {
        $reader.Close()
    }
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

    Enable-Tls12

    $url = $Server.TrimEnd('/') + "/api/v1/bridge/devices/redeem"
    $body = @{ code = $Code; addin_version = "installer" } | ConvertTo-Json -Compress

    Write-Host "Redeeming pairing code at $url"
    $redeemProxy = Get-ProxyParameter -Uri $url
    try {
        $reply = Invoke-RestMethod -Uri $url -Method Post -Body $body -ContentType "application/json" -Headers @{ "User-Agent" = $userAgent } @redeemProxy
    } catch {
        $failure = $_
        $response = $failure.Exception.Response
        $status = $null
        if ($null -ne $response) { $status = [int]$response.StatusCode }
        # The reply body: ErrorDetails holds it on 7, and on 5.1 when the reply had a
        # Content-Length. A chunked reply (common behind proxies and CDNs) leaves
        # ErrorDetails empty on 5.1, and the body is then still in the response stream.
        $text = ""
        if ($null -ne $failure.ErrorDetails -and $failure.ErrorDetails.Message) {
            $text = [string]$failure.ErrorDetails.Message
        } elseif ($null -ne $response -and $null -ne ($response | Get-Member -Name GetResponseStream -MemberType Method)) {
            try {
                $stream = $response.GetResponseStream()
                if ($stream.CanSeek) { [void]$stream.Seek(0, [IO.SeekOrigin]::Begin) }
                $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                $text = $reader.ReadToEnd()
                $reader.Close()
            } catch {
                Write-Verbose "Could not read the error reply from ${url}: $($_.Exception.Message)"
            }
        }
        $errorCode = ""
        $detail = ""
        if ($text) {
            try {
                $parsed = $text | ConvertFrom-Json
                if ($null -ne $parsed.error) { $errorCode = [string]$parsed.error }
                if ($null -ne $parsed.message) { $detail = [string]$parsed.message }
                elseif ($null -ne $parsed.detail) { $detail = [string]$parsed.detail }
                elseif ($errorCode) { $detail = $errorCode }
            } catch {
                Write-Verbose "The error reply from $url is not JSON."
            }
        }

        # Same rule as the add-in's PairingClient: only 404 with error
        # "invalid_code" means the code itself was refused.
        if ($status -eq 404 -and $errorCode -ceq "invalid_code") {
            throw "Pairing code $Code was refused (invalid, already used, or expired). Generate a new one on the site. $detail".Trim()
        }
        if ($status -eq 404) {
            throw "$url answered 404 Not Found without a pairing-code error, so the server address is probably wrong. -Server is the site address, e.g. https://bridge.example.com. $detail".Trim()
        }
        throw "Could not redeem the pairing code at $url : $($failure.Exception.Message) $detail".Trim()
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

function Restore-PreviousInstall {
    param([hashtable]$State)
    # Undo the file section: remove what this run wrote and put the previous folder and
    # manifest back under the names they had. Plain .NET calls and no output, so this also
    # works from a finally block while the run is being stopped (Ctrl+C). Problems are
    # collected in $State.Problems.
    $State.Problems = New-Object System.Collections.Generic.List[string]
    $plugin = $State.PluginDestination
    # The folder at the destination is this run's (partial) copy unless the previous
    # one could not be moved away.
    if (($State.MovedPlugin -or -not $State.HadPlugin) -and [IO.Directory]::Exists($plugin)) {
        try {
            [IO.Directory]::Delete($plugin, $true)
        } catch {
            $State.Problems.Add("could not remove the partly copied $plugin ($($_.Exception.Message))")
        }
    }
    if ($State.MovedPlugin) {
        if ([IO.Directory]::Exists($plugin)) {
            $State.Problems.Add("the previous add-in is still in $($State.BackupDestination)")
        } else {
            try {
                [IO.Directory]::Move($State.BackupDestination, $plugin)
            } catch {
                $State.Problems.Add("could not move $($State.BackupDestination) back to $plugin ($($_.Exception.Message))")
            }
        }
    }
    if ($State.BackedUpManifest) {
        try {
            [IO.File]::Copy($State.ManifestBackup, $State.AddinDestination, $true)
            [IO.File]::Delete($State.ManifestBackup)
        } catch {
            $State.Problems.Add("could not move $($State.ManifestBackup) back to $($State.AddinDestination) ($($_.Exception.Message))")
        }
    } elseif (-not $State.HadManifest -and [IO.File]::Exists($State.AddinDestination)) {
        try {
            [IO.File]::Delete($State.AddinDestination)
        } catch {
            $State.Problems.Add("could not remove the new $($State.AddinDestination) ($($_.Exception.Message))")
        }
    }
    $State.Restored = $true
}

function ConvertFrom-RegistryMode {
    param([string]$Value)
    # "websocket" -> "remote", "tcp" -> "local" (ignoring case, as the add-in
    # does); $null for anything else.
    foreach ($name in $registryModes.Keys) {
        if ($registryModes[$name] -eq $Value) {
            return $name
        }
    }
    return $null
}

function Get-RevitExeYear {
    param([string]$Path)
    # The Revit year of a Revit.exe: from its file version (26.x is 2026), or, when
    # the file cannot be read, from the last "20xx" in its path. $null if neither.
    try {
        $major = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileMajorPart
        if ($major -ge 15 -and $major -le 99) {
            return [string](2000 + $major)
        }
    } catch {
        Write-Verbose "Could not read the file version of ${Path}: $($_.Exception.Message)"
    }
    $years = [regex]::Matches($Path, '(?<!\d)20\d\d(?!\d)')
    if ($years.Count -gt 0) {
        return $years[$years.Count - 1].Value
    }
    return $null
}

function Get-BlockingRevitProcess {
    param([string]$RevitVersion, [object[]]$Processes)
    # Only a running Revit of the target year holds this add-in's files. A Revit
    # whose program path cannot be read (access denied, e.g. one running as
    # administrator) may be that year, so it blocks as well.
    $blocking = @()
    foreach ($process in $Processes) {
        $path = $null
        try {
            $path = [string]$process.Path
        } catch {
            $path = $null
        }
        if ([string]::IsNullOrWhiteSpace($path)) {
            $blocking += "a Revit process (id $($process.Id)) whose program path could not be read (access denied, for example when it runs as administrator), so it may be Revit $RevitVersion"
            continue
        }
        $year = Get-RevitExeYear -Path $path
        if ($null -eq $year) {
            $blocking += "a Revit process (id $($process.Id), $path) whose Revit year could not be determined"
        } elseif ($year -eq $RevitVersion) {
            $blocking += "Revit $year (process id $($process.Id), $path)"
        }
    }
    return $blocking
}

try {
    $ErrorActionPreference = "Stop"
    # The progress bar makes Invoke-WebRequest -OutFile and Expand-Archive very
    # slow on Windows PowerShell 5.1.
    $ProgressPreference = "SilentlyContinue"

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

    $blockingRevit = @(Get-BlockingRevitProcess -RevitVersion $RevitVersion -Processes @(Get-Process -Name Revit -ErrorAction SilentlyContinue))
    if ($blockingRevit.Count -gt 0) {
        throw "Close Revit $RevitVersion before installing or updating the add-in (Revit of other years can stay open). Still running: $($blockingRevit -join '; ')."
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
    # Work out the connection settings
    # ---------------------------------------------------------------------------
    # Everything here can fail (unreadable package, refused pairing code) and none
    # of it touches the existing install: no file is moved before it has succeeded.

    $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    $pluginDestination = Join-Path $addinsRoot $layout.FolderName
    $addinDestination = Join-Path $addinsRoot $layout.Manifest.Name
    $registryDestination = Join-Path $pluginDestination "Commands\commandRegistry.json"

    # Every text file is read as UTF-8 explicitly (a BOM is still recognised):
    # Windows PowerShell 5.1 otherwise decodes a file without BOM in the ANSI code
    # page, which on Chinese Windows (936) destroys every non-ASCII character.
    $config = Get-Content -LiteralPath (Join-Path $layout.PluginDir "Commands\commandRegistry.json") -Raw -Encoding UTF8 | ConvertFrom-Json

    # Carry the previous settings over an update: mode, deviceId, token and wsUrl
    # belong to this machine, not to the package, and so do port and logLevel.
    # Read from the install that is still in place.
    $existingMode = $null
    $existingWsUrl = ""
    $existingToken = ""
    $existingDeviceId = ""
    $existingConfirmEachRun = $true
    $existingAllowRemoteCode = $false
    $existingPort = 18080
    $existingLogLevel = "Info"
    $previousRegistry = $registryDestination
    if (Test-Path -LiteralPath $previousRegistry) {
        try {
            $previous = Get-Content -LiteralPath $previousRegistry -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -ne $previous.settings) {
                if ($null -ne $previous.settings.mode) { $existingMode = ConvertFrom-RegistryMode ([string]$previous.settings.mode) }
                if ($null -ne $previous.settings.token) { $existingToken = [string]$previous.settings.token }
                if ($null -ne $previous.settings.deviceId) { $existingDeviceId = [string]$previous.settings.deviceId }
                if ($null -ne $previous.settings.wsUrl) { $existingWsUrl = [string]$previous.settings.wsUrl }
                if ($null -ne $previous.settings.confirmEachRun) { $existingConfirmEachRun = [bool]$previous.settings.confirmEachRun }
                if ($null -ne $previous.settings.allowRemoteCodeExecution) { $existingAllowRemoteCode = [bool]$previous.settings.allowRemoteCodeExecution }
                if ($null -ne $previous.settings.port) {
                    $portNumber = 0
                    if ([int]::TryParse([string]$previous.settings.port, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$portNumber) -and
                        $portNumber -ge 1 -and $portNumber -le 65535) {
                        $existingPort = $portNumber
                    } else {
                        Write-Warning "Ignored the previous port '$($previous.settings.port)' (not a number from 1 to 65535); using $existingPort."
                    }
                }
                # The add-in does not read logLevel yet; an unknown value is replaced quietly.
                if (@("Debug", "Info", "Warning", "Error") -contains [string]$previous.settings.logLevel) {
                    $existingLogLevel = [string]$previous.settings.logLevel
                }
            }
        } catch {
            Write-Warning "Could not read settings from the previous install: $($_.Exception.Message)"
        }
    }

    # -Mode wins; without it an update keeps the previous install's mode, and a
    # fresh install is local.
    if ($PSBoundParameters.ContainsKey("Mode")) {
        $effectiveMode = $Mode
        $modeSource = ""
    } elseif ($null -ne $existingMode) {
        $effectiveMode = $existingMode
        $modeSource = " (kept from the previous install)"
    } else {
        $effectiveMode = $Mode
        $modeSource = ""
    }

    $wsUrl = $existingWsUrl
    $deviceId = $existingDeviceId
    $effectiveToken = $existingToken

    # In local mode a non-empty token is the TCP pre-shared secret that every local
    # call must carry, which a device token from pairing is not: carrying it over
    # would refuse every call from the local MCP server. A token from a previous
    # local install is the user's own TCP secret and stays.
    if ($effectiveMode -eq "local" -and $existingMode -eq "remote" -and -not [string]::IsNullOrEmpty($existingToken)) {
        $effectiveToken = ""
        Write-Warning "Switched from remote to local mode: the device token of the pairing ($deviceId) was not kept, because in local mode it would lock the TCP port. To use the bridge server again, pair again: re-run with -Mode remote -Server https://<host> -Pair XXXX-XXXX, or enter a new code in Settings > Connection."
    }

    if (-not [string]::IsNullOrWhiteSpace($Pair)) {
        $paired = Invoke-PairingRedeem -Server $Server -Code $Pair
        $deviceId = $paired.DeviceId
        $effectiveToken = $paired.Token
        $wsUrl = $paired.WsUrl
    } elseif ($effectiveMode -eq "remote") {
        if ([string]::IsNullOrWhiteSpace($deviceId) -or [string]::IsNullOrWhiteSpace($effectiveToken) -or [string]::IsNullOrWhiteSpace($wsUrl)) {
            Write-Warning "This Revit is not paired yet. Re-run with -Pair XXXX-XXXX, or enter a pairing code in Settings > Connection."
        } else {
            Write-Host "Kept the pairing from the previous install ($deviceId)."
        }
    }

    $modeValue = $registryModes[$effectiveMode]

    # Pairing is what authorises a server to run ad-hoc code on this machine, so
    # -Pair switches allowRemoteCodeExecution on unless the caller said otherwise.
    # An update without -Pair keeps whatever the previous install had.
    # ($effective... rather than $allowRemoteCode: PowerShell variable names are
    # case-insensitive, so that name would be the -AllowRemoteCode parameter itself.)
    if ($PSBoundParameters.ContainsKey("AllowRemoteCode")) {
        $effectiveAllowRemoteCode = [bool]$AllowRemoteCode
    } elseif (-not [string]::IsNullOrWhiteSpace($Pair)) {
        $effectiveAllowRemoteCode = $true
    } else {
        $effectiveAllowRemoteCode = $existingAllowRemoteCode
    }

    $settings = [pscustomobject]@{
        logLevel = $existingLogLevel
        port = $existingPort
        mode = $modeValue
        wsUrl = $wsUrl
        deviceId = $deviceId
        token = $effectiveToken
        confirmEachRun = $existingConfirmEachRun
        allowRemoteCodeExecution = $effectiveAllowRemoteCode
    }

    if ($null -eq $config.settings) {
        $config | Add-Member -NotePropertyName settings -NotePropertyValue $settings
    } else {
        $config.settings = $settings
    }
    $registryJson = $config | ConvertTo-Json -Depth 20

    # ---------------------------------------------------------------------------
    # Install files
    # ---------------------------------------------------------------------------
    # The previous add-in folder is renamed to a backup and the manifest copied to
    # one. If anything below fails - or the run is stopped half way (Ctrl+C) - what
    # this run wrote is removed and both are put back under their own names.

    # Backups are named by the second; an earlier run in the same second (or a clock
    # change) can have left that name already, and Move-Item into an existing folder
    # would nest the install inside it. Pick one suffix that is free for both names.
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $suffix = $stamp
    $n = 1
    while ((Test-Path -LiteralPath "$pluginDestination.backup-$suffix") -or (Test-Path -LiteralPath "$addinDestination.backup-$suffix")) {
        $n++
        $suffix = "$stamp-$n"
    }
    $fileState = @{
        PluginDestination = $pluginDestination
        AddinDestination  = $addinDestination
        BackupDestination = "$pluginDestination.backup-$suffix"
        ManifestBackup    = "$addinDestination.backup-$suffix"
        HadPlugin         = (Test-Path -LiteralPath $pluginDestination)
        HadManifest       = (Test-Path -LiteralPath $addinDestination)
        MovedPlugin       = $false
        BackedUpManifest  = $false
        Started           = $false
        Done              = $false
        Restored          = $false
        Problems          = $null
    }

    New-Item -ItemType Directory -Path $addinsRoot -Force | Out-Null

    try {
        $fileState.Started = $true
        if ($fileState.HadPlugin) {
            Move-Item -LiteralPath $pluginDestination -Destination $fileState.BackupDestination
            $fileState.MovedPlugin = $true
            Write-Host "Existing add-in backed up to: $($fileState.BackupDestination)"
        }
        if ($fileState.HadManifest) {
            Copy-Item -LiteralPath $addinDestination -Destination $fileState.ManifestBackup
            $fileState.BackedUpManifest = $true
        }

        Copy-Item -LiteralPath $layout.PluginDir -Destination $addinsRoot -Recurse -Force
        Copy-Item -LiteralPath $layout.Manifest.FullName -Destination $addinDestination -Force

        # UTF-8 without BOM on both 5.1 and 7, like the shipped file and the add-in's
        # own writes (Set-Content -Encoding UTF8 adds a BOM on 5.1 only).
        [IO.File]::WriteAllText($registryDestination, $registryJson, (New-Object System.Text.UTF8Encoding($false)))
        $fileState.Done = $true
    } catch {
        $installFailure = $_.Exception.Message
        Restore-PreviousInstall -State $fileState
        if ($fileState.Problems.Count -gt 0) {
            throw "Installing failed and the previous install could not be restored completely: $($fileState.Problems -join '; '). Rename the backup back by hand. Cause: $installFailure"
        }
        if ($fileState.HadPlugin -or $fileState.HadManifest) {
            throw "Installing failed, so the previous install was restored unchanged. Cause: $installFailure"
        }
        throw "Installing failed; the partly copied files were removed. Cause: $installFailure"
    } finally {
        # Started but neither finished nor restored by the catch above: the run was
        # stopped (Ctrl+C), which skips catch blocks but still runs finally.
        if ($fileState.Started -and -not $fileState.Done -and -not $fileState.Restored) {
            Restore-PreviousInstall -State $fileState
            if ($fileState.Problems.Count -gt 0) {
                $interruptMessage = "The install was interrupted and the previous install could not be restored completely: $($fileState.Problems -join '; '). Rename the backup back by hand."
            } else {
                $interruptMessage = "The install was interrupted; the previous install was restored unchanged."
            }
            try { Write-Warning $interruptMessage } catch { [Console]::Error.WriteLine("WARNING: " + $interruptMessage) }
        }
    }

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
            if (Select-String -LiteralPath $manifest.FullName -SimpleMatch $addinClassName -Encoding UTF8 -Quiet) {
                $otherManifests += $manifest.FullName
            }
        }
    }

    Write-Host ""
    Write-Host "Installed to:        $pluginDestination"
    Write-Host "Manifest:            $addinDestination"
    Write-Host "Source:              $sourceLabel"
    Write-Host "Mode:                $effectiveMode$modeSource"
    if ($effectiveMode -eq "remote") {
        if ([string]::IsNullOrWhiteSpace($deviceId) -or [string]::IsNullOrWhiteSpace($effectiveToken)) {
            Write-Host ("Server:              " + "$Server (not paired yet)".Trim())
        } else {
            Write-Host "Server:              $wsUrl"
            Write-Host "Device:              $deviceId"
        }
    } else {
        Write-Host "TCP endpoint:        127.0.0.1:$existingPort"
    }
    Write-Host "Confirm each run:    $existingConfirmEachRun"
    Write-Host "Remote code allowed: $effectiveAllowRemoteCode"
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
} finally {
    $ErrorActionPreference = $callerErrorActionPreference
    $ProgressPreference = $callerProgressPreference
    [Net.ServicePointManager]::SecurityProtocol = $callerSecurityProtocol
}
