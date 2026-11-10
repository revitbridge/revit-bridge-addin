<#
.SYNOPSIS
    Installs the revit-bridge Revit add-in for the current user.

.DESCRIPTION
    Downloads the latest GitHub Release (or uses a local build output), verifies
    the SHA-256 checksum of every package it uses, backs up any existing install,
    copies the add-in into %APPDATA%\Autodesk\Revit\Addins\<year>\ and writes the
    connection settings into Commands\commandRegistry.json.

    Every Revit year is its own install: its own settings and pairing, its own
    backup, its own check for a running Revit. A Revit of one year that is open
    skips that year only, and one year that fails does not undo or stop the
    others; the run then ends with an error that names them.

    One-line install for every supported Revit installed on this machine (local
    TCP mode on a fresh install, nothing to configure; run again to update,
    keeping each year's mode and pairing):

        irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1 | iex

    With parameters, e.g. two Revit years:

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -RevitVersion 2024,2026

    Remote mode, one Revit year per pairing code (get the code from the site first):

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -RevitVersion 2026 -Mode remote -Server https://example.com -Pair XXXX-XXXX

    From a local build (installs for the year it was built for):

        .\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"

.PARAMETER Mode
    local  - Revit listens on TCP 127.0.0.1:18080 for a local MCP server.
    remote - Revit connects out to a WebSocket bridge server (-Server required).
    Default: per Revit year, the mode of the install being updated, or local for
    a fresh install. Port, logLevel and the pairing are kept as well; the command
    list is the package's, every command enabled. Switching a paired install to
    local drops its device token (it would lock the TCP port) and, unless
    -AllowRemoteCode is given, allowRemoteCodeExecution; switching a local install
    to remote does not carry its TCP secret over.

.PARAMETER Server
    Site address of the bridge server, e.g. https://bridge.example.com.
    Remote mode only. The WebSocket URL is whatever the server returns when
    the pairing code is redeemed; it is never assembled here.

.PARAMETER Source
    "release" (default): download the release from GitHub (-Repo, -Tag) and
    verify each package against SHA256SUMS.txt. Otherwise a local directory,
    either
    - one Revit year's add-in tree: an extracted package or a build output such
      as "plugin\bin\AddIn 2026 Release R26" (installed for that year only), or
    - a folder holding a release's files as downloaded: the
      revit-bridge-addin-*.zip packages and SHA256SUMS.txt, verified like a
      download (for an offline install).

.PARAMETER Pair
    Pairing code from the site, in the form XXXX-XXXX, with -Server. For a
    remote install: an existing remote install takes it without -Mode, a fresh
    or local install needs -Mode remote. A code pairs one Revit year (each Revit
    year is a separate device), so -Pair needs exactly one target year.
    The installer redeems it at <Server>/api/v1/bridge/devices/redeem and
    stores the device id, device token and WebSocket URL it gets back, before
    any file is touched. Codes are single use and expire after 10 minutes. Omit
    it to keep the pairing from a previous install, or pair later in
    Settings > Connection.

.PARAMETER AllowRemoteCode
    Remote mode: allow send_code_to_revit and manage_solidified_tools.
    Pairing with -Pair switches this on, because pairing is the act of
    authorising that server; each ad-hoc run still asks in Revit while
    confirmEachRun is on. Pass -AllowRemoteCode:$false together with -Pair to
    pair without it. Without -Pair the previous setting is kept, or off for a
    fresh install. Unpairing in Settings, or the server revoking the device,
    switches it off again.

.PARAMETER RevitVersion
    Revit year(s) to install for: one year (-RevitVersion 2026), several
    (-RevitVersion 2024,2026), or "installed": every Revit installed on this
    machine (read from HKLM\SOFTWARE\Autodesk\Revit) in a year this installer
    supports (2020, 2021, 2023-2027) that the release has a package for.
    Default: the same as "installed", except that when no Revit at all is found
    in the registry it installs for 2026, as earlier installers did; Revit
    installed only in unsupported years is an error. With -Source <add-in tree>
    the default is that tree's own year.

.PARAMETER Repo
    GitHub repository to download releases from. Forks set this to their own
    repository. Default revitbridge/revit-bridge-addin.

.PARAMETER Tag
    Release tag to install, e.g. v0.4.0. Default "latest". Releases before
    v0.4.0 carry one package, for Revit 2026 only.
#>
[CmdletBinding()]
param(
    [ValidateSet("local", "remote")]
    [string]$Mode = "local",
    [string]$Server = "",
    [string]$Source = "release",
    [string]$Pair = "",
    [switch]$AllowRemoteCode,
    [string[]]$RevitVersion = @(),
    [string]$Repo = "revitbridge/revit-bridge-addin",
    [string]$Tag = "latest"
)

# Everything below runs in a child scope. Run as "irm ... | iex" the script
# executes in the caller's own scope, and its variables (the device token among
# them), helper functions and preferences would stay in that PowerShell session.
# $PSBoundParameters inside the block would be the block's own, so the script's
# bound parameters are passed in.
& {
    param($installerBound)
    # Preferences set below stay in this child scope; the TLS setting is process-wide,
    # so it is put back when the script ends, also when it fails (finally below).
    $callerSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
    $addinClassName = "revit_mcp_plugin.Core.Application"
    $userAgent = "revit-bridge-addin-installer"
    # -Mode values of this script and the "mode" values in commandRegistry.json.
    $registryModes = @{ "local" = "tcp"; "remote" = "websocket" }
    # Releases before v0.4.0 carry a single revit-bridge-addin-<tag>.zip, built for this
    # year only. It is also the year installed when no Revit is found in the registry.
    $singlePackageYear = "2026"
    # The Revit years this add-in is built for (REVIT_YEARS in .github/workflows/build.yml,
    # README "Supported Revit years"). Years found in the registry are filtered to these.
    $supportedYears = @("2020", "2021", "2023", "2024", "2025", "2026", "2027")
    # Years whose package is compiled but has not been loaded in a real Revit (README,
    # "Supported Revit years"). Keep both lists the same.
    $notLoadTestedYears = @("2020", "2027")
    # State shared with the helpers below (a hashtable, so they can change it).
    $run = @{ WorkDir = $null; Finished = $false; Completed = (New-Object System.Collections.Generic.List[string]) }

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

    function ConvertTo-RevitYearRequest {
        param([string[]]$Values)
        # -RevitVersion as given: years (also "2024,2026" in one string, as a command
        # line passed through powershell.exe -File delivers it), or the word "installed".
        $years = @()
        $installed = $false
        foreach ($value in @($Values)) {
            foreach ($item in @([string]$value -split '[,;\s]+')) {
                if ($item -eq "") { continue }
                if ($item -eq "installed") {
                    $installed = $true
                } elseif ($item -match '^20\d\d$') {
                    if ($years -notcontains $item) { $years += $item }
                } else {
                    throw "-RevitVersion takes Revit years, e.g. -RevitVersion 2026 or -RevitVersion 2024,2026, or the word installed (every supported Revit installed on this machine). '$item' is neither."
                }
            }
        }
        if ($installed -and $years.Count -gt 0) {
            throw "-RevitVersion installed already means every supported Revit installed on this machine; give either that or the years, not both."
        }
        return [pscustomobject]@{ Installed = $installed; Years = @($years | Sort-Object) }
    }

    function Get-InstalledRevitYears {
        # The Revit years installed on this machine, as Revit's installer registers them:
        # HKLM\SOFTWARE\Autodesk\Revit\<year>\REVIT-<nn>:<language> with an
        # InstallationLocation that holds Revit.exe. Revit is 64-bit only, so the 64-bit
        # view is read (also from a 32-bit PowerShell). Revit LT (REVITLT-...) does not load
        # add-ins and is not counted. Nothing is guessed from folders.
        $years = @()
        $machine = $null
        try {
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
            $revitKey = $machine.OpenSubKey("SOFTWARE\Autodesk\Revit")
            if ($null -eq $revitKey) { return @() }
            foreach ($yearName in $revitKey.GetSubKeyNames()) {
                if ($yearName -notmatch '^20\d\d$') { continue }
                $yearKey = $revitKey.OpenSubKey($yearName)
                if ($null -eq $yearKey) { continue }
                foreach ($productName in $yearKey.GetSubKeyNames()) {
                    if ($productName -notmatch '^REVIT-\d+:') { continue }
                    $productKey = $yearKey.OpenSubKey($productName)
                    if ($null -eq $productKey) { continue }
                    $location = [string]$productKey.GetValue("InstallationLocation")
                    if (-not [string]::IsNullOrWhiteSpace($location) -and (Test-Path -LiteralPath (Join-Path $location "Revit.exe") -PathType Leaf)) {
                        $years += $yearName
                        break
                    }
                }
            }
        } catch {
            Write-Warning "Could not read the installed Revit versions from the registry: $($_.Exception.Message)"
        } finally {
            if ($null -ne $machine) { $machine.Close() }
        }
        return @($years | Sort-Object -Unique)
    }

    function Get-WorkDir {
        # One temporary folder per run for downloads and extracted packages, removed at
        # the end of the run (finally below).
        if ($null -eq $run.WorkDir) {
            $path = Join-Path $env:TEMP ("revit-bridge-addin-install-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            $run.WorkDir = $path
        }
        return $run.WorkDir
    }

    function Get-GitHubRelease {
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
        $assets = @($release.assets | ForEach-Object { [pscustomobject]@{ Name = [string]$_.name; Url = [string]$_.browser_download_url; Path = $null } })
        return [pscustomobject]@{ Label = "release $($release.tag_name)"; Assets = $assets }
    }

    function Get-ReleaseFolder {
        param([string]$Path)
        # A release's files saved in a local folder: the same selection and checks as a
        # download, without the download.
        $assets = @(Get-ChildItem -LiteralPath $Path -File | Where-Object { $_.Name -like "*.zip" -or $_.Name -eq "SHA256SUMS.txt" } |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Url = $null; Path = $_.FullName } })
        return [pscustomobject]@{ Label = "release folder $Path"; Assets = $assets }
    }

    function Select-ReleasePackages {
        param($Release)
        # One package per Revit year: revit-bridge-addin-<tag>-revit<year>.zip (v0.4.0 and
        # later). An older release has one revit-bridge-addin-<tag>.zip, for Revit 2026.
        $sums = @($Release.Assets | Where-Object { $_.Name -eq "SHA256SUMS.txt" } | Select-Object -First 1)
        if ($sums.Count -eq 0) {
            throw "$($Release.Label) has no SHA256SUMS.txt; refusing to install an unverified package."
        }
        $packages = @{}
        foreach ($asset in $Release.Assets) {
            if ($asset.Name -match '^revit-bridge-addin-.+-revit(20\d\d)\.zip$') {
                if ($packages.ContainsKey($Matches[1])) {
                    throw "$($Release.Label) has two packages for Revit $($Matches[1]): $($packages[$Matches[1]].Name) and $($asset.Name)."
                }
                $packages[$Matches[1]] = $asset
            }
        }
        $singlePackage = $false
        if ($packages.Count -eq 0) {
            $single = @($Release.Assets | Where-Object { $_.Name -like "revit-bridge-addin-*.zip" } | Select-Object -First 1)
            if ($single.Count -eq 0) {
                throw "$($Release.Label) has no revit-bridge-addin-*.zip asset."
            }
            $packages[$singlePackageYear] = $single[0]
            $singlePackage = $true
        }
        return [pscustomobject]@{ Sums = $sums[0]; Packages = $packages; SinglePackage = $singlePackage }
    }

    function Get-AssetFile {
        param($Asset, [string]$Destination)
        # The asset's file on disk: downloaded once into the run's folder, or the file in
        # the release folder as it is.
        if ($null -ne $Asset.Path) { return $Asset.Path }
        Write-Host "Downloading $($Asset.Name)"
        $proxy = Get-ProxyParameter -Uri $Asset.Url
        Invoke-WebRequest -Uri $Asset.Url -OutFile $Destination -Headers @{ "User-Agent" = $userAgent } -UseBasicParsing @proxy
        return $Destination
    }

    function Read-Checksums {
        param([string]$Path)
        # name -> lower-case SHA-256, first entry per name.
        $sums = @{}
        foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
            if ($line -match '^\s*([0-9A-Fa-f]{64})\s+\*?(.+?)\s*$' -and -not $sums.ContainsKey($Matches[2])) {
                $sums[$Matches[2]] = $Matches[1].ToLowerInvariant()
            }
        }
        return $sums
    }

    function Expand-VerifiedPackage {
        param($Asset, [hashtable]$Sums, [string]$Year)
        # Download (once) and verify one package, then extract it into the run's folder.
        if (-not $Sums.ContainsKey($Asset.Name)) {
            throw "SHA256SUMS.txt has no entry for $($Asset.Name)."
        }
        $workDir = Get-WorkDir
        $zipPath = Get-AssetFile -Asset $Asset -Destination (Join-Path $workDir $Asset.Name)
        $expected = $Sums[$Asset.Name]
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Checksum mismatch for $($Asset.Name). Expected $expected but got $actual."
        }
        Write-Host "Checksum verified: $($Asset.Name) $actual"

        $extractDir = Join-Path $workDir "revit$Year"
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

        # Tolerate a zip that wraps everything in a single top-level folder.
        if (-not (Get-ChildItem -LiteralPath $extractDir -Filter "*.addin" -File)) {
            $children = @(Get-ChildItem -LiteralPath $extractDir)
            if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
                $extractDir = $children[0].FullName
            }
        }
        return $extractDir
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

    function Get-TreeRevitYears {
        param([string]$PluginDir)
        # The Revit year an add-in tree is built for: its command set sits in
        # Commands\<command set>\<year>\. A tree holds the add-in of one year.
        $years = @()
        $commands = Join-Path $PluginDir "Commands"
        foreach ($set in @(Get-ChildItem -LiteralPath $commands -Directory -ErrorAction SilentlyContinue)) {
            foreach ($yearDir in @(Get-ChildItem -LiteralPath $set.FullName -Directory -ErrorAction SilentlyContinue)) {
                if ($yearDir.Name -match '^20\d\d$' -and @(Get-ChildItem -LiteralPath $yearDir.FullName -Filter "*.dll" -File -ErrorAction SilentlyContinue).Count -gt 0) {
                    $years += $yearDir.Name
                }
            }
        }
        return @($years | Sort-Object -Unique)
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
                throw "The pairing code was refused (invalid, already used, or expired). Generate a new one on the site. $detail".Trim()
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

    function Get-RevitProcesses {
        return @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
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

    function Get-RunningRevitMessage {
        param([string]$Year, [string[]]$Blocking)
        return "Close Revit $Year before installing or updating the add-in (Revit of other years can stay open). Still running: $($Blocking -join '; ')."
    }

    function New-YearResult {
        param([string]$Year, [string]$Status, [bool]$Failed, [string]$Detail)
        return [pscustomobject]@{ Year = $Year; Status = $Status; Failed = $Failed; Detail = $Detail }
    }

    function Install-RevitYear {
        param([string]$Year, [string]$SourcePath, [string]$SourceLabel)
        # One Revit year, start to end: read the package and the previous settings, redeem
        # -Pair, then the file section with its backup and restore, then the report.
        # Throws when this year cannot be installed; the previous install of this year is
        # then as it was.

        # Checked again here: downloading the other years' packages can take a while.
        $blockingRevit = @(Get-BlockingRevitProcess -RevitVersion $Year -Processes @(Get-RevitProcesses))
        if ($blockingRevit.Count -gt 0) {
            throw (Get-RunningRevitMessage -Year $Year -Blocking $blockingRevit)
        }

        $layout = Resolve-SourceLayout -Path $SourcePath
        Write-Host "Source: $SourceLabel ($SourcePath)"
        Write-Host "Add-in: $($layout.Name), ClientId $($layout.ClientId), folder $($layout.FolderName)"
        $packageYears = @(Get-TreeRevitYears -PluginDir $layout.PluginDir)
        if ($packageYears.Count -ne 1 -or $packageYears[0] -ne $Year) {
            throw "The package for Revit $Year holds the add-in for Revit $(if ($packageYears.Count -eq 0) { 'no year' } else { $packageYears -join ', ' }) ($($layout.FolderName)\Commands\<command set>\<year>\)."
        }

        # ---------------------------------------------------------------------------
        # Work out the connection settings
        # ---------------------------------------------------------------------------
        # Everything here can fail (unreadable package, refused pairing code) and none
        # of it touches the existing install: no file is moved before it has succeeded.

        $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
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
        $previousCommandNames = $null
        $previousDisabledNames = @()
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
                if ($null -ne $previous.commands) {
                    $previousCommandNames = @($previous.commands | ForEach-Object { [string]$_.commandName })
                    $previousDisabledNames = @($previous.commands | Where-Object { $_.enabled -eq $false } | ForEach-Object { [string]$_.commandName })
                }
            } catch {
                Write-Warning "Could not read settings from the previous install: $($_.Exception.Message)"
            }
        }

        # -Mode wins; without it an update keeps the previous install's mode, and a
        # fresh install is local.
        if ($installerBound.ContainsKey("Mode")) {
            $effectiveMode = $Mode
            $modeSource = ""
        } elseif ($null -ne $existingMode) {
            $effectiveMode = $existingMode
            $modeSource = " (kept from the previous install)"
        } else {
            $effectiveMode = $Mode
            $modeSource = ""
        }

        # -Pair pairs a remote install: an existing remote install takes it without
        # -Mode; a fresh or local install needs -Mode remote (switching is what -Mode is
        # for). Checked before the code is redeemed, so a refused run does not use it up.
        if ($pairGiven -and $effectiveMode -ne "remote") {
            throw "-Pair only applies to a remote install, and this one would be local (a fresh install, a local install, or -Mode local). Add -Mode remote, e.g. -Mode remote -Server https://<host> -Pair XXXX-XXXX."
        }

        $wsUrl = $existingWsUrl
        $deviceId = $existingDeviceId
        $effectiveToken = $existingToken
        $notPairedWarned = $false

        # In local mode a non-empty token is the TCP pre-shared secret that every local
        # call must carry, which a device token from pairing is not: carrying it over
        # would refuse every call from the local MCP server. A token from a previous
        # local install is the user's own TCP secret and stays. Without the token there
        # is no pairing behind allowRemoteCodeExecution any more, so it goes off too
        # unless -AllowRemoteCode says otherwise.
        if ($effectiveMode -eq "local" -and $existingMode -eq "remote" -and -not [string]::IsNullOrEmpty($existingToken)) {
            $effectiveToken = ""
            $remoteCodeNote = ""
            if (-not $installerBound.ContainsKey("AllowRemoteCode")) {
                $existingAllowRemoteCode = $false
                $remoteCodeNote = " Remote code execution (allowRemoteCodeExecution) was switched off with it."
            }
            Write-Warning "Switched from remote to local mode: the device token of the pairing ($deviceId) was not kept, because in local mode it would lock the TCP port.$remoteCodeNote To use the bridge server again, pair again: re-run with -RevitVersion $Year -Mode remote -Server https://<host> -Pair XXXX-XXXX, or enter a new code in Settings > Connection."
        }

        # The other way round: a local install's token is its TCP secret, not a device
        # token, so it is not carried into remote mode; without -Pair the Revit is then
        # simply not paired yet.
        if ($effectiveMode -eq "remote" -and $existingMode -ne "remote" -and -not [string]::IsNullOrEmpty($existingToken) -and -not $pairGiven) {
            $effectiveToken = ""
            $notPairedWarned = $true
            Write-Warning "Switched from local to remote mode: the local TCP secret (token) was not kept, because it is not a device token. This Revit is not paired yet: re-run with -RevitVersion $Year -Server https://<host> -Pair XXXX-XXXX, or enter a pairing code in Settings > Connection."
        }

        if ($pairGiven) {
            $paired = Invoke-PairingRedeem -Server $Server -Code $Pair
            $deviceId = $paired.DeviceId
            $effectiveToken = $paired.Token
            $wsUrl = $paired.WsUrl
        } elseif ($effectiveMode -eq "remote" -and -not $notPairedWarned) {
            if ([string]::IsNullOrWhiteSpace($deviceId) -or [string]::IsNullOrWhiteSpace($effectiveToken) -or [string]::IsNullOrWhiteSpace($wsUrl)) {
                Write-Warning "This Revit is not paired yet. Re-run with -RevitVersion $Year -Pair XXXX-XXXX, or enter a pairing code in Settings > Connection."
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
        if ($installerBound.ContainsKey("AllowRemoteCode")) {
            $effectiveAllowRemoteCode = [bool]$AllowRemoteCode
        } elseif ($pairGiven) {
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
                    $interruptMessage = "The install for Revit $Year was interrupted and the previous install could not be restored completely: $($fileState.Problems -join '; '). Rename the backup back by hand."
                } else {
                    $interruptMessage = "The install for Revit $Year was interrupted; the previous install was restored unchanged."
                }
                try { Write-Warning $interruptMessage } catch { [Console]::Error.WriteLine("WARNING: " + $interruptMessage) }
            }
        }
        $run.Completed.Add($Year)

        # ---------------------------------------------------------------------------
        # Report
        # ---------------------------------------------------------------------------

        # The add-in is installed at this point. Nothing below may turn that into a
        # failure: a problem with the summary is a warning.
        try {
            $mainDllDestination = Join-Path $pluginDestination "RevitMCPPlugin.dll"
            $commandDllDestination = Join-Path $pluginDestination "Commands\RevitMCPCommandSet\$Year\RevitMCPCommandSet.dll"
            if (-not (Test-Path -LiteralPath $commandDllDestination)) {
                Write-Warning "Command set for Revit $Year not found at $commandDllDestination. Only the add-in shell was installed."
            }

            # Other manifests that load the same add-in class: upstream mcp-servers-for-revit
            # or an older copy of this add-in. They use a different ClientId and folder, so
            # they can coexist, but the user should know they are there.
            $manifestRoots = @(
                $addinsRoot,
                (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$Year")
            ) | Select-Object -Unique
            $otherManifests = @()
            foreach ($manifestRoot in $manifestRoots) {
                if (-not (Test-Path -LiteralPath $manifestRoot)) { continue }
                foreach ($manifest in Get-ChildItem -LiteralPath $manifestRoot -Filter "*.addin" -File -ErrorAction SilentlyContinue) {
                    if ($manifest.FullName -eq $addinDestination) { continue }
                    try {
                        if (Select-String -LiteralPath $manifest.FullName -SimpleMatch $addinClassName -Encoding UTF8 -Quiet) {
                            $otherManifests += $manifest.FullName
                        }
                    } catch {
                        Write-Warning "Could not read $($manifest.FullName) to check whether it loads the same add-in: $($_.Exception.Message)"
                    }
                }
            }

            Write-Host ""
            Write-Host "Installed to:        $pluginDestination"
            Write-Host "Manifest:            $addinDestination"
            Write-Host "Source:              $SourceLabel"
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
            # The Command sets page switches a command off by removing it from the list, which
            # cannot be told apart from a command that is new in this package, so the
            # package's list is installed as it is, every command enabled. Name the commands
            # the user may want to switch off again.
            if ($null -ne $previousCommandNames) {
                $packageNames = @($config.commands | ForEach-Object { [string]$_.commandName })
                $notBefore = @($packageNames | Where-Object { $previousCommandNames -notcontains $_ })
                $offBefore = @($packageNames | Where-Object { $previousDisabledNames -contains $_ })
                if ($notBefore.Count -gt 0 -or $offBefore.Count -gt 0) {
                    Write-Host "Commands:            the package's list was installed with every command enabled."
                    if ($notBefore.Count -gt 0) { Write-Host "  Not in the previous list (new, or switched off in Settings > Command sets): $($notBefore -join ', ')" }
                    if ($offBefore.Count -gt 0) { Write-Host "  Switched off in the previous list (enabled: false): $($offBefore -join ', ')" }
                    Write-Host "  Switch them off again in Settings > Command sets if you do not want them."
                }
            }
            Write-Host "RevitMCPPlugin.dll SHA-256:     $(Get-FileSha256 $mainDllDestination)"
            Write-Host "RevitMCPCommandSet.dll SHA-256: $(Get-FileSha256 $commandDllDestination)"
            if ($otherManifests.Count -gt 0) {
                Write-Warning "Other manifests load the same add-in class ($addinClassName):"
                foreach ($manifestPath in $otherManifests) {
                    Write-Warning "  $manifestPath"
                }
                Write-Warning "They can coexist with revit-bridge (different ClientId and folder). If the 'Revit MCP Plugin' ribbon panel is missing or duplicated, remove one of them."
            }
            if ($notLoadTestedYears -contains $Year) {
                Write-Warning "The Revit $Year build is compiled but has not been tested in a real Revit $Year yet."
            }
            Write-Host ""
            Write-Host "Start Revit $Year, then click 'Revit MCP Switch' on the 'Revit MCP Plugin' ribbon panel."
        } catch {
            Write-Warning "The add-in was installed, but the summary could not be completed: $($_.Exception.Message)"
        }

        $detail = $effectiveMode
        if ($effectiveMode -eq "local") { $detail = "local, 127.0.0.1:$existingPort" }
        if (-not $fileState.HadPlugin) { $detail += ", new install" }
        return New-YearResult -Year $Year -Status "installed" -Failed $false -Detail $detail
    }

    function Write-SharedPortNote {
        param([string]$FolderName)
        # Two years in local mode on the same port are a conflict only while both Revits
        # have the bridge switched on; ports are not changed here, only named. Covers every
        # install of this add-in in this profile, not only the years of this run.
        $byPort = @{}
        $root = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
        foreach ($yearDir in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^20\d\d$' })) {
            $registry = Join-Path $yearDir.FullName "$FolderName\Commands\commandRegistry.json"
            if (-not (Test-Path -LiteralPath $registry -PathType Leaf)) { continue }
            try {
                $settings = (Get-Content -LiteralPath $registry -Raw -Encoding UTF8 | ConvertFrom-Json).settings
            } catch {
                continue
            }
            if ($null -eq $settings -or (ConvertFrom-RegistryMode ([string]$settings.mode)) -ne "local") { continue }
            $port = "18080"
            if ($null -ne $settings.port) { $port = [string]$settings.port }
            if (-not $byPort.ContainsKey($port)) { $byPort[$port] = @() }
            $byPort[$port] += $yearDir.Name
        }
        foreach ($port in @($byPort.Keys | Sort-Object)) {
            if ($byPort[$port].Count -gt 1) {
                Write-Host "Port $port is used by the local installs for Revit $($byPort[$port] -join ', '): only one of them can have the bridge switched on at a time (give one another port under Settings > Connection to run them side by side)."
            }
        }
    }

    try {
        $ErrorActionPreference = "Stop"
        # The progress bar makes Invoke-WebRequest -OutFile and Expand-Archive very
        # slow on Windows PowerShell 5.1.
        $ProgressPreference = "SilentlyContinue"

        # ---------------------------------------------------------------------------
        # Validate input
        # ---------------------------------------------------------------------------

        # Only the form of the parameters here. Whether -Pair fits is decided per year
        # once the mode is known (the previous install's mode when -Mode is not given).
        $explicitRemote = $installerBound.ContainsKey("Mode") -and $Mode -eq "remote"
        $pairGiven = -not [string]::IsNullOrWhiteSpace($Pair)
        if ([string]::IsNullOrWhiteSpace($Server) -and ($explicitRemote -or $pairGiven)) {
            throw "-Server is required in remote mode, e.g. -Server https://bridge.example.com"
        }
        if (-not [string]::IsNullOrWhiteSpace($Server)) {
            if ($Server -match '^wss?://') {
                throw "-Server is the site address (https://host), not the WebSocket URL. The server returns the WebSocket URL when the pairing code is redeemed."
            }
            if ($Server -notmatch '^https://' -and $Server -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?/?$') {
                throw "-Server must start with https:// (http:// is only allowed for localhost)."
            }
        }

        if ($pairGiven) {
            $Pair = $Pair.Trim().ToUpperInvariant()
            if ($Pair -match '^[A-Z0-9]{8}$') {
                $Pair = $Pair.Substring(0, 4) + "-" + $Pair.Substring(4)
            }
            if ($Pair -notmatch '^[A-Z0-9]{4}-[A-Z0-9]{4}$') {
                throw "-Pair must be a pairing code in the form XXXX-XXXX."
            }
        }

        $request = ConvertTo-RevitYearRequest -Values $RevitVersion

        # ---------------------------------------------------------------------------
        # What kind of source, and which Revit years
        # ---------------------------------------------------------------------------

        $sourceKind = "release"
        $sourcePath = $null
        $treeYear = $null
        if ($Source -ne "release") {
            if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
                throw "-Source is ""release"" or an existing directory; $Source is neither."
            }
            $sourcePath = (Resolve-Path -LiteralPath $Source).ProviderPath
            if (@(Get-ChildItem -LiteralPath $sourcePath -Filter "*.addin" -File).Count -gt 0) {
                $sourceKind = "tree"
                $treeLayout = Resolve-SourceLayout -Path $sourcePath
                $treeYears = @(Get-TreeRevitYears -PluginDir $treeLayout.PluginDir)
                if ($treeYears.Count -ne 1) {
                    throw "Cannot tell which Revit year the add-in in $sourcePath is for: expected one year folder in $($treeLayout.FolderName)\Commands\<command set>\, found $(if ($treeYears.Count -eq 0) { 'none' } else { $treeYears -join ', ' })."
                }
                $treeYear = $treeYears[0]
            } elseif (Test-Path -LiteralPath (Join-Path $sourcePath "SHA256SUMS.txt") -PathType Leaf) {
                $sourceKind = "folder"
            } else {
                throw "$sourcePath holds neither an add-in (a .addin manifest next to its folder) nor a release's files (revit-bridge-addin-*.zip and SHA256SUMS.txt)."
            }
        }

        # "given": the years in -RevitVersion; "installed": found in the registry (default,
        # or -RevitVersion installed); "fallback": nothing found, 2026 as before; "source":
        # the one year of an add-in tree.
        if ($sourceKind -eq "tree") {
            if ($request.Installed) {
                throw "-RevitVersion installed needs a release with a package per year (-Source release or a release folder); $sourcePath is the add-in for Revit $treeYear only."
            }
            if ($request.Years.Count -gt 0 -and ($request.Years.Count -ne 1 -or $request.Years[0] -ne $treeYear)) {
                throw "$sourcePath is the add-in for Revit $treeYear; it cannot be installed for Revit $($request.Years -join ', '). Use the build or package for that year, or -Source release."
            }
            $targets = @($treeYear)
            $targetsFrom = "source"
        } elseif ($request.Years.Count -gt 0) {
            $targets = @($request.Years)
            $targetsFrom = "given"
        } else {
            # Only the years this add-in is built for are targets; another installed year (2022,
            # 2019 and earlier) is named once and plays no further part.
            $detectedAll = @(Get-InstalledRevitYears)
            $detected = @($detectedAll | Where-Object { $supportedYears -contains $_ })
            $unsupported = @($detectedAll | Where-Object { $supportedYears -notcontains $_ })
            if ($detected.Count -gt 0) {
                Write-Host "Revit installed on this machine: $($detected -join ', ')"
            }
            if ($unsupported.Count -gt 0) {
                Write-Host "Left out, this add-in is not built for them: Revit $($unsupported -join ', ') (supported: $($supportedYears -join ', '))."
            }
            if ($detected.Count -gt 0) {
                $targets = $detected
                $targetsFrom = "installed"
            } elseif ($unsupported.Count -gt 0) {
                throw "Revit is installed here only in years this add-in is not built for: Revit $($unsupported -join ', '). Supported Revit years: $($supportedYears -join ', '). Nothing was installed."
            } elseif ($request.Installed) {
                throw "No Revit installation was found in the registry (HKLM\SOFTWARE\Autodesk\Revit). Name the Revit year instead, e.g. -RevitVersion 2026."
            } else {
                $targets = @($singlePackageYear)
                $targetsFrom = "fallback"
                Write-Host "No Revit installation was found in the registry (HKLM\SOFTWARE\Autodesk\Revit), so the add-in is installed for Revit $singlePackageYear. Use -RevitVersion to choose the Revit year(s)."
            }
        }

        # A pairing code makes one device, and every Revit year is a device of its own.
        # Checked before anything is downloaded or redeemed.
        if ($pairGiven -and $targets.Count -ne 1) {
            throw "-Pair pairs one Revit year (each Revit year is a separate device with its own pairing code), and this run would install for Revit $($targets -join ', '). Pair one year at a time: add -RevitVersion <year>, e.g. -RevitVersion $($targets[$targets.Count - 1]) -Pair XXXX-XXXX, and get a new code on the site for each further year."
        }

        $results = New-Object System.Collections.Generic.List[object]
        $pending = New-Object System.Collections.Generic.List[string]
        foreach ($year in $targets) { $pending.Add($year) }

        # One Revit year: refuse before anything is looked up or downloaded, as before.
        if ($targets.Count -eq 1) {
            $blocking = @(Get-BlockingRevitProcess -RevitVersion $targets[0] -Processes @(Get-RevitProcesses))
            if ($blocking.Count -gt 0) {
                throw (Get-RunningRevitMessage -Year $targets[0] -Blocking $blocking)
            }
        }

        # ---------------------------------------------------------------------------
        # Resolve the package of each year: one download each, verified
        # ---------------------------------------------------------------------------

        $packageOf = @{}
        if ($sourceKind -eq "tree") {
            foreach ($year in $pending) {
                $packageOf[$year] = [pscustomobject]@{ Path = $sourcePath; Label = "local directory" }
            }
        } else {
            if ($sourceKind -eq "release") {
                $release = Get-GitHubRelease -Repo $Repo -Tag $Tag
            } else {
                $release = Get-ReleaseFolder -Path $sourcePath
            }
            $selection = Select-ReleasePackages -Release $release

            # Which years the release covers, first: a year it has no package for is not
            # held up by a Revit of that year being open.
            foreach ($year in @($pending)) {
                if ($selection.Packages.ContainsKey($year)) { continue }
                if ($selection.SinglePackage) {
                    $why = "$($release.Label) has a package for Revit $singlePackageYear only (releases before v0.4.0 carry one package). Install Revit $year from a newer release: leave out -Tag for the latest."
                } else {
                    $why = "$($release.Label) has no package for Revit $year (revit-bridge-addin-<tag>-revit$year.zip)."
                }
                # Years found in the registry that the release does not cover are skipped
                # quietly; a year that was asked for by name is a failure.
                if ($targets.Count -eq 1 -and $targetsFrom -ne "installed") { throw $why }
                $results.Add((New-YearResult -Year $year -Status "skipped" -Failed ($targetsFrom -ne "installed") -Detail $why))
                [void]$pending.Remove($year)
            }

            # A Revit of a year that is open skips that year only. Checked before its package
            # is downloaded, and again just before its files are touched.
            if ($targets.Count -gt 1) {
                $processes = @(Get-RevitProcesses)
                foreach ($year in @($pending)) {
                    $blocking = @(Get-BlockingRevitProcess -RevitVersion $year -Processes $processes)
                    if ($blocking.Count -gt 0) {
                        $results.Add((New-YearResult -Year $year -Status "not installed" -Failed $true -Detail (Get-RunningRevitMessage -Year $year -Blocking $blocking)))
                        [void]$pending.Remove($year)
                    }
                }
            }

            $sums = @{}
            if ($pending.Count -gt 0) {
                $sumsFile = Get-AssetFile -Asset $selection.Sums -Destination (Join-Path (Get-WorkDir) $selection.Sums.Name)
                $sums = Read-Checksums -Path $sumsFile
            }
            foreach ($year in @($pending)) {
                try {
                    $extracted = Expand-VerifiedPackage -Asset $selection.Packages[$year] -Sums $sums -Year $year
                    $packageOf[$year] = [pscustomobject]@{ Path = $extracted; Label = $release.Label }
                } catch {
                    if ($targets.Count -eq 1) { throw }
                    $results.Add((New-YearResult -Year $year -Status "not installed" -Failed $true -Detail $_.Exception.Message))
                    [void]$pending.Remove($year)
                }
            }
        }

        # ---------------------------------------------------------------------------
        # Install, one Revit year after the other
        # ---------------------------------------------------------------------------

        $folderName = "revit-bridge"
        foreach ($year in $pending) {
            if ($targets.Count -gt 1) {
                Write-Host ""
                Write-Host "=== Revit $year ==="
            }
            try {
                $results.Add((Install-RevitYear -Year $year -SourcePath $packageOf[$year].Path -SourceLabel $packageOf[$year].Label))
                $folderName = (Resolve-SourceLayout -Path $packageOf[$year].Path).FolderName
            } catch {
                if ($targets.Count -eq 1) { throw }
                Write-Warning "Revit ${year}: $($_.Exception.Message)"
                $results.Add((New-YearResult -Year $year -Status "not installed" -Failed $true -Detail $_.Exception.Message))
            }
        }
        $run.Finished = $true

        # ---------------------------------------------------------------------------
        # Summary over all years
        # ---------------------------------------------------------------------------

        $failed = @($results | Where-Object { $_.Failed })
        $installed = @($results | Where-Object { $_.Status -eq "installed" })
        try {
            if ($targets.Count -gt 1 -or $results.Count -gt $installed.Count) {
                Write-Host ""
                Write-Host "Summary:"
                foreach ($result in @($results | Sort-Object Year)) {
                    Write-Host "  Revit $($result.Year): $($result.Status) - $($result.Detail)"
                }
            }
            Write-SharedPortNote -FolderName $folderName
        } catch {
            Write-Warning "The summary could not be completed: $($_.Exception.Message)"
        }

        if ($failed.Count -gt 0) {
            throw "Not every Revit year was installed: $((@($failed | Sort-Object Year | ForEach-Object { "Revit $($_.Year) $($_.Status)" })) -join ', '). See the summary above."
        }
        if ($installed.Count -eq 0) {
            throw "Nothing was installed: the release has no package for the Revit years found on this machine ($($targets -join ', ')). Name a year with -RevitVersion."
        }
    } finally {
        # Stopped (Ctrl+C) after some years were installed completely: say which ones stay.
        if (-not $run.Finished -and $run.Completed.Count -gt 0) {
            $stoppedMessage = "The run ended early. Installed completely before that: Revit $($run.Completed -join ', '). The year being installed was put back as it was, the remaining ones were not touched."
            try { Write-Warning $stoppedMessage } catch { [Console]::Error.WriteLine("WARNING: " + $stoppedMessage) }
        }
        if ($null -ne $run.WorkDir) {
            try {
                [IO.Directory]::Delete($run.WorkDir, $true)
            } catch {
                try { Write-Warning "Could not remove the temporary folder $($run.WorkDir): $($_.Exception.Message)" } catch { [Console]::Error.WriteLine("WARNING: could not remove " + $run.WorkDir) }
            }
        }
        [Net.ServicePointManager]::SecurityProtocol = $callerSecurityProtocol
    }
} $PSBoundParameters
