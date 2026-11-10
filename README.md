# revit-bridge-addin

Revit add-in for [revit-bridge](https://github.com/revitbridge/revit-bridge). It exposes a small JSON-RPC surface (26 commands, including dynamic C# execution) that the `revit-bridge` MCP server or a [revit-bridge-web](https://github.com/revitbridge/revit-bridge-web) host drives.

Revit 2020、2021、2023-2026 插件：本地 TCP 或远程 WebSocket 两种模式，供 `revit-bridge` MCP 服务器或演示宿主调用。每个 Revit 年份单独安装、单独配置、单独配对。

Fork of [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) (MIT). Only `plugin/` and `commandset/` were kept; see [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Supported Revit years

| Revit | .NET the add-in targets | Status |
|---|---|---|
| 2020 | .NET Framework 4.8 | built, not load-tested |
| 2021 | .NET Framework 4.8 | load-tested |
| 2022 | - | not supported (its build has deprecation warnings in code kept for 2022 only, and it cannot be tested here) |
| 2023 | .NET Framework 4.8 | load-tested |
| 2024 | .NET Framework 4.8 | load-tested |
| 2025 | .NET 8 | load-tested |
| 2026 | .NET 8 (also runs on 2026.5, which uses .NET 10) | load-tested on 2026.5 |
| 2027 | .NET 10 | not supported: the `Release R27` configuration builds, but no package is released and the installer refuses Revit 2027 |

"Load-tested" means the package of that year was installed with the installer and loaded in a real Revit of that year: the add-in started, Settings saved, the bridge answered over TCP, and a snippet compiled and ran. A year marked "built, not load-tested" compiles without warnings but has not been run in Revit; use it at your own risk and report what you find. The installer installs the supported years only (2020, 2021, 2023-2026); an installed Revit of another year is left out.

Every Revit year is its own install: its own folder under `%APPDATA%\Autodesk\Revit\Addins\<year>\`, its own settings, port and log, and - for remote mode - its own pairing. To the bridge server each Revit year is a separate device.

## Install

Requirements: Windows x64, one of the supported Revit years above, PowerShell 5.1 or later. Close the Revit of each year you install for; a Revit of a year that is open is skipped (the installer says so at the end), other years are installed.

One line, for every supported Revit installed on this machine (read from the registry; when no Revit at all is found, Revit 2026), local mode on a fresh install (Revit listens on `127.0.0.1:18080`, no token needed):

```powershell
irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1 | iex
```

One Revit year, or several:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -RevitVersion 2026
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -RevitVersion 2024,2026
```

`-RevitVersion installed` says "every supported Revit installed on this machine" explicitly (and fails when none is found instead of falling back to 2026). Installed Revit years that are not supported (2022, 2027, or 2019 and earlier) are left out with one line saying so, also while a Revit of such a year is open; `-RevitVersion` with such a year is refused; when Revit is installed only in such years, the installer stops with an error that names them and the supported years (the 2026 fallback is only for a machine with no Revit in the registry at all). At the end the installer lists each year as installed, skipped or not installed, with the reason; when any year was not installed it ends with an error (exit code 1) that names them. One year failing does not undo or stop the others.

Remote mode (Revit connects out to a bridge server; nothing is exposed on your machine). Click "Pair a Revit" on the site first to get a pairing code. A code pairs one Revit year (each Revit year is a separate device); for a second year, get a second code and run the command again with that year. The command the site gives you works as it is, without a year:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -Mode remote -Server https://<host> -Pair XXXX-XXXX
```

With one supported Revit year installed, that year is paired. With several, the installer lists them (and whether each already has revit-bridge, in local or remote mode) and asks which one year to pair; Enter takes the newest. An answer that is not one of the listed years is asked again, three times at most; after that it stops without having downloaded or redeemed anything, so the code is still unused. Where it cannot ask (a non-interactive session such as `powershell -NonInteractive`, or input from a pipe or file), it stops before anything is downloaded or redeemed and names the `-RevitVersion <year>` to add. To skip the question, name the year yourself:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -RevitVersion 2026 -Mode remote -Server https://<host> -Pair XXXX-XXXX
```

With `-Pair` and several years given (`-RevitVersion 2024,2026` or `-RevitVersion installed`) the installer stops before anything is downloaded or redeemed. The installer redeems the code at `<host>/api/v1/bridge/devices/redeem` and stores the device id, device token and WebSocket URL the server returns. Codes are single use and expire after 10 minutes. You can also install without `-Pair` and enter the code later under **Settings > Connection** of that Revit year. The code is redeemed before anything on disk changes: if the server refuses it ("refused (invalid, already used, or expired)") the existing install is left as it was; "the server address is probably wrong" means `-Server` answered 404 without a pairing-code error.

Pairing is what authorises that server to run ad-hoc code here, so it switches `allowRemoteCodeExecution` on; every such run still asks in Revit first (see [Use](#use)). To pair without it, add `-AllowRemoteCode:$false`. **Unpair** in the Settings window - or the server revoking this device - switches it back off.

To update, run the first one-line command again, for local and remote installs alike (no `-Mode` or `-Pair` needed). It updates every supported Revit installed on this machine - and installs for an installed year that did not have the add-in yet; add `-RevitVersion` to update only some years. An update keeps, per Revit year, the previous install's mode and pairing (`deviceId`, `token`, `wsUrl`), `allowRemoteCodeExecution`, `confirmEachRun`, `port` and `logLevel`. It does **not** keep your command choices: the command list is the package's again, every command enabled, and the installer names the commands that were not in your previous list so you can switch them off again under **Settings > Command sets**. (That page switches a command off by removing it from the list, which the installer cannot tell apart from a command that is new in the release.)

Pass `-Mode` only to switch. `-Mode local` over a paired remote install drops the device token, because in local mode a token locks the TCP port, and switches `allowRemoteCodeExecution` off with it (add `-AllowRemoteCode` to keep it on); to go back to remote, pair again. `-Mode remote` over a local install does not carry the local TCP secret over; the Revit is then not paired until you pair it. To re-pair an existing remote install, `-RevitVersion <year> -Server https://<host> -Pair XXXX-XXXX` is enough; on a fresh or local install `-Pair` needs `-Mode remote` as in the command above.

Ports: each Revit year in local mode listens on its own `port` setting, `18080` unless you change it. Two years on the same port work as long as only one of them has the bridge switched on at a time; to run two Revit years side by side, give one of them another port under **Settings > Connection** (and tell the MCP server, e.g. `REVIT_BRIDGE_PORT`). The installer does not change ports; it prints one line when two local installs share one.

The installer downloads the latest [Release](https://github.com/revitbridge/revit-bridge-addin/releases) - one package per Revit year, `revit-bridge-addin-<tag>-revit<year>.zip`, each downloaded once - verifies each package against `SHA256SUMS.txt`, and then, for each year, backs up any previous install to `revit-bridge.backup-<timestamp>` (`-2`, `-3`, ... appended when that name is taken) and writes the connection settings. If copying the new files of a year fails, or the run is stopped half way (Ctrl+C), it puts that year's previous install back and says so; years finished before that stay installed. Releases before v0.4.0 have a single package, for Revit 2026: `-Tag v0.3.1 -RevitVersion 2026` still installs it, another year on such a release is refused with a message. Revit shows a "Security - Unsigned Add-In" prompt the first time it loads each new build (so again after every upgrade, even when the previous build was on the Always Load list): releases are not code-signed yet (see [Signing](#signing)). Its default button is **Do Not Load**; choose **Always Load** to stop the prompt for that build, or **Load Once** to be asked again next start. The bridge does not listen until you click **Revit MCP Switch** after each Revit start (see [Use](#use)).

Installer parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Mode local\|remote` | previous install's mode, else `local` | TCP on localhost, or outbound WebSocket to `-Server` |
| `-Server <https url>` | | Site address of the bridge server (not the `wss://` URL), required with `-Pair` and with `-Mode remote` |
| `-Pair XXXX-XXXX` | | Pairing code from the site, for a remote install (an existing remote install, or `-Mode remote`) of exactly one Revit year; without `-RevitVersion` and with several years installed the installer asks which one; omitted, an existing pairing is kept |
| `-AllowRemoteCode` | on with `-Pair` | Allow `send_code_to_revit` / `manage_solidified_tools` from the paired server. Pairing switches it on; pass `-AllowRemoteCode:$false` to pair without it. Without `-Pair`, the previous setting is kept |
| `-Source release\|<dir>` | `release` | Install from GitHub Releases, or from a local directory: one year's add-in tree (a build output, see [Build](#build), or an extracted package; installed for that year only), or a folder with a release's downloaded files (the zips and `SHA256SUMS.txt`, verified the same way, for an offline install) |
| `-RevitVersion` | every supported Revit installed; `2026` when none is found | One year (`2026`), several (`2024,2026`), or `installed` |
| `-Repo`, `-Tag` | `revitbridge/revit-bridge-addin`, `latest` | Where to download from (forks point at their own repo) |

Manual install: unzip the package of your Revit year (`revit-bridge-addin-<tag>-revit<year>.zip`) into `%APPDATA%\Autodesk\Revit\Addins\<year>\` so that `revit-bridge.addin` and the `revit-bridge\` folder sit side by side, then edit `revit-bridge\Commands\commandRegistry.json` (see [Configure](#configure)).

## Use

1. Start Revit (any year you installed for). The **Revit MCP Plugin** panel appears on the **Add-Ins** ribbon tab.
2. Click **Revit MCP Switch** to start (or stop) the service. **Settings** opens the connection page.
3. Connect a client:
   - **Local mode**: run the MCP server on the same machine, e.g. `uvx revit-bridge` in Claude Desktop / Claude Code, and `revit-bridge check` to confirm the link. The add-in listens on TCP `127.0.0.1:18080` (JSON-RPC 2.0, loopback only).
   - **Remote mode**: the add-in dials `<wsUrl>/<deviceId>` on port 443 (the `wsUrl` the server returned when pairing) and sends `{"type":"auth","device_id":...,"token":...}` as the first message. The site then lists this Revit as online. Revoking the device on the site closes the connection (code 4003); the add-in stops reconnecting and shows "unpaired or revoked" until you pair again.

Ad-hoc code runs ask first: when the server marks a request for confirmation, Revit shows a Yes/No dialog with what is about to run, defaulting to No, and No answers the caller with `declined on device`. Capability packs, probes and reads never prompt. Turn the dialog off with the `confirmEachRun` checkbox if a run is unattended - the server's own confirmation step still applies.

Two read-only methods let a client find out what it can rely on before anything runs. Neither prompts, opens a transaction, needs an open document or is gated by `allowRemoteCodeExecution`; an older add-in answers both with `-32601 Method not found`.

- `get_bridge_capabilities` (no params) returns the protocol version, add-in version, registered methods, feature flags, the Revit version (`VersionNumber` / `VersionBuild` / `VersionName`) and the .NET runtime.
- `check_code_in_revit` (`code`, optional `parameters`) compiles a `send_code_to_revit` snippet with the same wrapper, reference whitelist and symbol blacklist without running it, and returns `compiled` plus diagnostics with 1-based user-code line/column (a blacklisted symbol is `RB0001`) and a `fingerprint` of code and environment.

A snippet that compiles but throws while it runs answers `success: false` with `errorMessage` `执行失败: <full exception type name>: <message>`, for example `执行失败: Autodesk.Revit.Exceptions.ArgumentException: ...` where older builds said `ArgumentException: ...`. The reflection wrapper (`TargetInvocationException`) and an `AggregateException` holding one exception are unwrapped to the exception the code threw; inner exceptions follow as ` ---> <type>: <message>`. Compile errors keep their `Line N: ...` text and come with `diagnostics`. `manage_solidified_tools` `run` reports the same way.

In remote mode, `send_code_to_revit` and `manage_solidified_tools` are rejected unless `allowRemoteCodeExecution` is `true`. Pairing switches it on and unpairing (or the server revoking the device) switches it off, so it follows the trust you granted rather than being a separate step. Local mode is not gated by this flag; the local MCP server's own confirmation step (`spec_confirmed`) applies instead.

Logs: `%APPDATA%\Autodesk\Revit\Addins\<year>\revit-bridge\Logs\mcp_YYYYMMDD.log`, one folder per Revit year.

## Build

Requirements: the .NET SDK pinned in [global.json](global.json) (10.0.403, `rollForward: latestFeature`; a .NET 10 SDK is needed for the `Release R27` configuration and builds the .NET Framework 4.8 and .NET 8 years too), Windows (WPF). No Revit SDK is needed; Revit API packages come from NuGet.

One configuration per Revit year, `Release R<yy>`: `R20`, `R21`, `R23`, `R24`, `R25`, `R26`, and `R27`, which builds but is not released or supported by the installer. For example Revit 2024:

```powershell
dotnet build plugin -c "Release R24"
dotnet build commandset -c "Release R24"
```

Both projects must be built, in that order. The result is a complete, installable tree in `plugin\bin\AddIn <year> Release R<yy>\`, here for 2024:

```
AddIn 2024 Release R24\
+-- revit-bridge.addin
+-- revit-bridge\
    +-- RevitMCPPlugin.dll, RevitMCPSDK.dll, Newtonsoft.Json.dll, ...
    +-- Commands\
        +-- commandRegistry.json
        +-- RevitMCPCommandSet\{command.json, 2024\RevitMCPCommandSet.dll + Roslyn}
```

Install your own build with `.\installer\install.ps1 -Source "plugin\bin\AddIn 2024 Release R24"` (it installs for the year the tree was built for). CI ([build.yml](.github/workflows/build.yml)) builds every released year (2020, 2021, 2023-2026) with the same two commands (job `build`), checks the set of packages and `SHA256SUMS.txt` (job `package`, read-only) and, on a `v*` tag only, publishes one `revit-bridge-addin-<tag>-revit<year>.zip` per year plus one `SHA256SUMS.txt` to Releases (job `publish`, the only one with write access). Official builds and self-builds produce the same tree.

`Debug R<yy>` additionally copies the output into `%APPDATA%\Autodesk\Revit\Addins\<year>\` (without touching an existing `commandRegistry.json`).

### Signing

Releases are currently unsigned (Revit shows the "Security - Unsigned Add-In" prompt once per new build). The workflow has an Authenticode step that runs only when the `SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD` secrets are set. Self-builds are always unsigned.

### Forking

Change the add-in identity before you redistribute a fork: see [FORKING.md](FORKING.md).

## Configure

Settings live in `<addin folder>\Commands\commandRegistry.json` under `settings`. The installer writes them; the **Settings** button in Revit edits the same file.

| Field | Default | Meaning |
|---|---|---|
| `logLevel` | `"Info"` | Not read yet: the log always records Info, Warning and Error. Kept across updates |
| `port` | `18080` | TCP port for local mode (bound to `127.0.0.1` only) |
| `mode` | `"tcp"` | `"tcp"` (local) or `"websocket"` (remote) |
| `wsUrl` | | WebSocket base URL, written by pairing (the server decides it); the add-in connects to `<wsUrl>/<deviceId>` |
| `deviceId` | `""` | This installation's device id, assigned when a pairing code is redeemed. Empty = not paired |
| `token` | `""` | Device token from the same reply, sent in the auth handshake. In local mode it is the optional pre-shared token instead |
| `confirmEachRun` | `true` | Show the Yes/No dialog for ad-hoc code runs the server marks for confirmation |
| `allowRemoteCodeExecution` | `false` until paired | Remote mode only: allow `send_code_to_revit` and `manage_solidified_tools`; rejected with an error when `false`. Set by pairing, cleared by unpairing or by the server revoking this device |

The `commands` array in the same file registers the 26 built-in commands from `RevitMCPCommandSet`; their `assemblyPath` names the year folder as `{VERSION}` (`RevitMCPCommandSet/{VERSION}/RevitMCPCommandSet.dll`), which the add-in replaces with the year of the Revit it runs in. `Commands\RevitMCPCommandSet\command.json` lists each command's name, description and assembly path. Saving the **Command sets** page in Settings rewrites only `commands`, and the **Connection** page only the settings it shows; everything else in the file, including properties this version does not know, stays. An empty or damaged file is reported and left as it is, not replaced by defaults.

## License

MIT, see [LICENSE](LICENSE). Upstream code is MIT (c) sparx-fire / mcp-servers-for-revit, see [LICENSE-upstream](LICENSE-upstream). Bundled third-party libraries: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
