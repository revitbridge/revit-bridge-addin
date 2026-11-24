# revit-bridge-addin

Revit add-in for [revit-bridge](https://github.com/revitbridge/revit-bridge). It exposes a small JSON-RPC surface (30 commands, including dynamic C# execution) that the `revit-bridge` MCP server or a [revit-bridge-web](https://github.com/revitbridge/revit-bridge-web) host drives.

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

With `-Pair` and several years given (`-RevitVersion 2024,2026` or `-RevitVersion installed`) the installer stops before anything is downloaded or redeemed. The installer redeems the code at `<host>/api/v1/bridge/devices/redeem` and stores the device id, device token and WebSocket URL the server returns. Codes are single use and expire after 10 minutes. You can also install without `-Pair` and enter the code later in the **Revit Bridge** panel of that Revit year (Shift+click the **Revit Bridge** button, **Connection**). The code is redeemed before anything on disk changes: if the server refuses it ("refused (invalid, already used, or expired)") the existing install is left as it was; "the server address is probably wrong" means `-Server` answered 404 without a pairing-code error.

Pairing is what authorises that server to run ad-hoc code here, so it switches `allowRemoteCodeExecution` on; every such run still asks in Revit first (see [Use](#use)). To pair without it, add `-AllowRemoteCode:$false`. **Unpair** in the Revit Bridge panel - or the server revoking this device - switches it back off.

To update, run the first one-line command again, for local and remote installs alike (no `-Mode` or `-Pair` needed). It updates every supported Revit installed on this machine - and installs for an installed year that did not have the add-in yet; add `-RevitVersion` to update only some years. An update keeps, per Revit year, the previous install's mode and pairing (`deviceId`, `token`, `wsUrl`), `allowRemoteCodeExecution`, `confirmEachRun`, `port` and `logLevel`, and `localTools`, `autoStart` and `panelShown` when the previous install had them. It does **not** keep your command choices: the command list is the package's again, every command enabled, and the installer names the commands that were not in your previous list so you can switch them off again under **Advanced: commands** in the Revit Bridge panel. (That page switches a command off by removing it from the list, which the installer cannot tell apart from a command that is new in the release.)

Pass `-Mode` only to switch. `-Mode local` over a paired remote install drops the device token, because in local mode a token locks the TCP port, and switches `allowRemoteCodeExecution` off with it (add `-AllowRemoteCode` to keep it on); to go back to remote, pair again. `-Mode remote` over a local install does not carry the local TCP secret over; the Revit is then not paired until you pair it. To re-pair an existing remote install, `-RevitVersion <year> -Server https://<host> -Pair XXXX-XXXX` is enough; on a fresh or local install `-Pair` needs `-Mode remote` as in the command above.

Ports: each Revit year in local mode listens on its own `port` setting, `18080` unless you change it. Two years on the same port work as long as only one of them has the bridge switched on at a time; to run two Revit years side by side, give one of them another port in the Revit Bridge panel under **Connection > Advanced** (and tell the MCP server, e.g. `REVIT_BRIDGE_PORT`). The installer does not change ports; it prints one line when two local installs share one.

The installer downloads the latest [Release](https://github.com/revitbridge/revit-bridge-addin/releases) - one package per Revit year, `revit-bridge-addin-<tag>-revit<year>.zip`, each downloaded once - verifies each package against `SHA256SUMS.txt`, and then, for each year, backs up any previous install to `revit-bridge.backup-<timestamp>` (`-2`, `-3`, ... appended when that name is taken) and writes the connection settings. If copying the new files of a year fails, or the run is stopped half way (Ctrl+C), it puts that year's previous install back and says so; years finished before that stay installed. Releases before v0.4.0 have a single package, for Revit 2026: `-Tag v0.3.1 -RevitVersion 2026` still installs it, another year on such a release is refused with a message. Revit shows a "Security - Unsigned Add-In" prompt the first time it loads each new build (so again after every upgrade, even when the previous build was on the Always Load list): releases are not code-signed yet (see [Signing](#signing)). Its default button is **Do Not Load**; choose **Always Load** to stop the prompt for that build, or **Load Once** to be asked again next start. The bridge does not listen until you click **Revit Bridge** after each Revit start, unless you tick **Turn the bridge on when Revit starts** in its panel (see [Use](#use)).

Installer parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Mode local\|remote` | previous install's mode, else `local` | TCP on localhost, or outbound WebSocket to `-Server` |
| `-Server <https url>` | | Site address of the bridge server (not the `wss://` URL), required with `-Pair` and with `-Mode remote` |
| `-Pair XXXX-XXXX` | | Pairing code from the site, for a remote install (an existing remote install, or `-Mode remote`) of exactly one Revit year; without `-RevitVersion` and with several years installed the installer asks which one; omitted, an existing pairing is kept |
| `-AllowRemoteCode` | on with `-Pair` | Allow `send_code_to_revit` / `dry_run_code_in_revit` / `test_addin_command_in_revit` / `capture_view_in_revit` / `manage_solidified_tools` from the paired server. Pairing switches it on; pass `-AllowRemoteCode:$false` to pair without it. Without `-Pair`, the previous setting is kept |
| `-Source release\|<dir>` | `release` | Install from GitHub Releases, or from a local directory: one year's add-in tree (a build output, see [Build](#build), or an extracted package; installed for that year only), or a folder with a release's downloaded files (the zips and `SHA256SUMS.txt`, verified the same way, for an offline install) |
| `-RevitVersion` | every supported Revit installed; `2026` when none is found | One year (`2026`), several (`2024,2026`), or `installed` |
| `-Repo`, `-Tag` | `revitbridge/revit-bridge-addin`, `latest` | Where to download from (forks point at their own repo) |

Manual install: unzip the package of your Revit year (`revit-bridge-addin-<tag>-revit<year>.zip`) into `%APPDATA%\Autodesk\Revit\Addins\<year>\` so that `revit-bridge.addin` and the `revit-bridge\` folder sit side by side, then edit `revit-bridge\Commands\commandRegistry.json` (see [Configure](#configure)).

## Use

1. Start Revit (any year you installed for). The **Revit MCP Plugin** panel appears on the **Add-Ins** ribbon tab, with one button, **Revit Bridge**.
2. Click **Revit Bridge** to turn the bridge on or off. The button says **On** or **Off** (green or black plug), and its tooltip gives the mode, the port or server, whether **Allow my tools** is on and why the last start failed. Switching shows no dialog. A start that fails (the port is in use, `commandRegistry.json` exists but cannot be read, its `token` is not text) opens the Revit Bridge panel once with the reason, and the button stays **Off**. A second click within 0.8 s of the end of a switch is ignored. **Shift+click** opens the **Revit Bridge** panel: the switch and its live status, **Allow my tools**, **Turn the bridge on when Revit starts**, **Connection** (local or remote, server and pairing for remote, and under **Advanced** the port and "Ask before each ad-hoc code run") and **Advanced: commands** (the command list). The panel also opens by itself once, the first time the bridge is turned on after installing (`panelShown`).
3. Connect a client:
   - **Local mode**: run the MCP server on the same machine, e.g. `uvx revit-bridge` in Claude Desktop / Claude Code, and `revit-bridge check` to confirm the link. The add-in listens on TCP `127.0.0.1:18080` (JSON-RPC 2.0, loopback only).
   - **Remote mode**: the add-in dials `<wsUrl>/<deviceId>` on port 443 (the `wsUrl` the server returned when pairing) and sends `{"type":"auth","device_id":...,"token":...}` as the first message. The site then lists this Revit as online. Revoking the device on the site closes the connection (code 4003); the add-in stops reconnecting and shows "unpaired or revoked" until you pair again.

Ad-hoc code runs ask first: when the server marks a request for confirmation, Revit shows a Yes/No dialog with what is about to run, defaulting to No, and No answers the caller with `declined on device`. Capability packs, probes and reads never prompt. Turn the dialog off with the `confirmEachRun` checkbox if a run is unattended - the server's own confirmation step still applies.

Two read-only methods let a client find out what it can rely on before anything runs. Neither prompts, opens a transaction, needs an open document or is gated by `allowRemoteCodeExecution`; an older add-in answers both with `-32601 Method not found`.

- `get_bridge_capabilities` (no params) returns the protocol version, add-in version, registered methods, feature flags, the Revit version (`VersionNumber` / `VersionBuild` / `VersionName`), the .NET runtime and `user_tools` (see [Use your own tools](#use-your-own-tools)); `methods` lists the installed command set's methods only.
- `check_code_in_revit` (`code`, optional `parameters`) compiles a `send_code_to_revit` snippet with the same wrapper, reference whitelist and symbol blacklist without running it, and returns `compiled` plus diagnostics with 1-based user-code line/column (a blacklisted symbol is `RB0001`) and a `fingerprint` of code and environment. It also takes the optional `sources` described below.

`list_model_items` lists what a host offers for choosing, read-only: no transaction, no prompt, not gated by `allowRemoteCodeExecution`. `kind` is `levels` (`{Id, Name, ElevationMm}`, by elevation), `links` (one row per link instance: `{Id, Name, TypeId, TypeName, Loaded, DocumentTitle, Origin, Rotated}`, `Origin` `{X, Y, Z}` in mm, `DocumentTitle` null when the link is not loaded), `types` (`{FamilyTypeId, UniqueId, FamilyName, TypeName, Category, BuiltInCategory}`: `Category` is the display name, `BuiltInCategory` the `OST_*` name or null; needs `category`, an `OST_*` name, for every type of that category, or `ids`) or `type_categories` (`{Category, DisplayName, Count}`). Optional: `link_id` reads a loaded link's document instead (not with `links`); `ids`, at most 200 element ids (not with `type_categories`), keeps only the rows of those elements, and an id that does not exist or is of another class is simply missing from `Items`; `q` keeps the rows whose name contains it (any case); `limit` keeps the first rows. The reply is `{Success, Kind, Items, Total, Truncated, Warnings, Error}`. A bad request answers `Success: false` with `Error.Code` (`unknown_kind`, `bad_category`, `bad_params`, `unknown_link`, `link_not_loaded`, `no_document`, `busy`, `timeout`, `failed`) and `Error.Message`; a row that cannot be read is skipped with a line in `Warnings`. An older add-in answers `-32601 Method not found` and does not list it in `get_bridge_capabilities`' `methods`.

`dry_run_code_in_revit` and `check_code_in_revit` take an optional `sources`: an array of `{"name": "Core.cs", "text": "<complete C# file>"}` compiled in the same compilation as the snippet, so the snippet can call the types the files define and the same file can later be compiled into an add-in unchanged. The files are not wrapped: each needs its own `using` lines (the wrapper's `using System;`, `System.Linq`, `System.Collections.Generic`, `Autodesk.Revit.DB` and `Autodesk.Revit.UI` belong to the snippet only; a `global using` in a file applies to the snippet as well) and sees only the wrapper's references, a fixed set of .NET base libraries plus `RevitAPI` and `RevitAPIUI` (on the .NET 8 / .NET 10 years, for example, `System.Text.RegularExpressions` is not among them). Do not name a type `AIGeneratedCode.CodeExecutor`, the wrapper's own. The symbol blacklist applies to the files too. A diagnostic in a file has `file` (its name) and the line and column in that file; with `sources` sent, a snippet diagnostic has `file: ""`, and without them there is no `file` key. At most 20 files and 400 KB of UTF-8 text together, names unique ignoring case; a request over the limits is refused before compiling. The `fingerprint` covers the files in the order given. The code is C# 12 (Roslyn 4.8). `features.dry_run_sources` and `features.check_code_sources` say whether an add-in accepts them.

`dry_run_code_in_revit` (`code`, optional `parameters`, as for `send_code_to_revit`) runs a snippet and always rolls it back. The code runs in one transaction inside a transaction group; that transaction is committed so that Revit reports what changed and raises its warnings and errors, then the group is rolled back, so the model keeps nothing and the undo list gets no entry. It never shows the confirmation dialog. Warnings are recorded and dismissed; errors are recorded and the transaction rolls back without a dialog. The reply has `success`, `rolled_back` (always `true`; `false` is a defect), `result` (the return value as JSON), `error` (`<full exception type name>: <message>` when the code threw), `diagnostics` (compile failure only, same shape as above), `changes` (`added` and `modified`: `count`, `by_category` keyed by `BuiltInCategory` name with `(types)` for element types, `(none)` for elements without a category and `(other)` for a category that is not a `BuiltInCategory`, and the first 50 `ids`; `deleted`: `count` and the first 50 `ids`; the ids in `added` no longer exist after the rollback), `failures` (`severity` `Warning` or `Error`, `text`, `element_ids`) and `fingerprint`. The timeout is 60 s, as for `send_code_to_revit`; while a dry run that timed out is still running, the next one is refused until it ends. A rollback undoes model changes only: a snippet that posts a Revit command, shows a dialog or opens another document does so for real.

`test_addin_command_in_revit` runs a built add-in command in the open model without installing it, and restores the model. Parameters: `dll` (absolute path), `class`, optional `picks` (in order: `{"element_id": <id>, "fraction": 0.5}` for a point on the element's location line, `{"point_mm": [x, y, z]}`, `{"key": "Escape"}`), `input` (written as `<dll name>.input.json` next to the DLL the command runs), `readback_code` (a snippet run after the command returns and before the restore, with no transaction open), `zoom_to` (an element id), `timeout_seconds` (default 120) and `restore` (`"group_rollback"`, the default, rolls back a transaction group opened around the command; `"undo"` posts Undo after it, never more often than the test committed). The DLL is copied with the files next to it into a new folder under `%TEMP%\revit-bridge\addin-test\` and loaded from there, so the original stays free for the next build. The active view must be a plan view and every pick visible in it; the picks are clicked with the mouse once the command runs, so Revit has to be in the foreground. The reply has `loaded`, `load_error`, `returned`, `command_message`, `result_file`, `changes` and `failures` (as for the dry run), `readback`, `readback_error`, `picks_done`, `restore`, `rolled_back`, `undone`, `restored_clean` (true when the model is as it was, or nothing ran), `timed_out`, `foreground_ok`, `screenshot` (load failures only; empty for dialog timeouts), `dll_sha256` and `error`. One test at a time. The command is started through the test loader, the manifest's second entry (`Type="Command"`); it does nothing when run by hand. `zoom_view_in_revit` (`element_id` and `margin_mm`, or `fit: true`) zooms the active view; `capture_view_in_revit` (optional `element_id`, `margin_mm`, `path`) saves the view as PNG, by default under `Logs\captures\`.

On a timeout, the test closes a uniquely identified modal window created during that command through a bounded native `WM_CLOSE` call, then waits for the command and model restoration. It does not confirm dialogs automatically, send Escape as a timeout fallback, or use screenshots as evidence of completion. Existing or ambiguous windows are left alone and the reply reports that restoration is unconfirmed. Element picks pause while a dialog blocks the main window; pick activation uses native window calls, without keyboard or title-bar click fallbacks. An explicit Escape in `picks` still cancels an element pick when the guarded main window is active. A command that shows a modeless window while it waits for a pick is not supported: no pick is sent while a new window of the command is open. On a timeout the command's dialog is closed with `WM_CLOSE`, which an OK-only message box reports to the command as OK; the model is restored either way.

A snippet that compiles but throws while it runs answers `success: false` with `errorMessage` `执行失败: <full exception type name>: <message>`, for example `执行失败: Autodesk.Revit.Exceptions.ArgumentException: ...` where older builds said `ArgumentException: ...`. The reflection wrapper (`TargetInvocationException`) and an `AggregateException` holding one exception are unwrapped to the exception the code threw; inner exceptions follow as ` ---> <type>: <message>`. Compile errors keep their `Line N: ...` text and come with `diagnostics`. `manage_solidified_tools` `run` reports the same way.

In remote mode, `send_code_to_revit`, `dry_run_code_in_revit`, `test_addin_command_in_revit`, `capture_view_in_revit` and `manage_solidified_tools` are rejected unless `allowRemoteCodeExecution` is `true`. Pairing switches it on and unpairing (or the server revoking the device) switches it off, so it follows the trust you granted rather than being a separate step. Local mode is not gated by this flag; the local MCP server's own confirmation step (`spec_confirmed`) applies instead.

Logs: `%APPDATA%\Autodesk\Revit\Addins\<year>\revit-bridge\Logs\mcp_YYYYMMDD.log`, one folder per Revit year.

### Use your own tools

**Allow my tools** decides whether your own tools plug in: command sets other than the installed `RevitMCPCommandSet` (a command whose DLL is not `Commands\RevitMCPCommandSet\<year>\RevitMCPCommandSet.dll`; decided by path, not by name) and solidified tools (`manage_solidified_tools` `register` and `run`). Until you tick or untick it, it follows the mode: on in local mode, off in remote mode (`localTools` not set; an explicit `true` or `false` always wins, a value that cannot be read counts as off, and the panel says which applies). While it is off your command sets are not loaded, and a request for one of their methods, or for `manage_solidified_tools` `register` or `run`, answers JSON-RPC error `-32002` with a message that names the switch; `list` and `delete` still work, and a method whose DLL is missing answers `-32601` as before. Unticking takes effect at once; tools already loaded stay in memory until Revit restarts, and ticking again reuses them. Ticking while the bridge is on loads your tools without a restart, unless `token`, `mode`, `port` or `allowRemoteCodeExecution` in the file changed since the bridge was turned on (or the file cannot be read): then the panel says to turn the bridge off and on, and until then the running bridge keeps what it had. `get_bridge_capabilities` reports it as `"user_tools": {"allowed": true|false, "methods": [...]}`, `methods` being the user commands loaded. You can leave it off when you only use the cloud MCP.

To add a command set, put it in its own folder under `<addin folder>\Commands\`, next to `RevitMCPCommandSet`: a `command.json` (`name`, `description`, `commands`, each with `commandName`, `description` and `assemblyPath`, the DLL path relative to the year folder) and one folder per Revit year (`2026\MyTools.dll`). Each command is a public class implementing `RevitMCPSDK.API.Interfaces.IRevitCommand` (`CommandName`, `Execute(JObject parameters, string requestId)`); code that calls the Revit API runs through the SDK's external-event base class (`ExternalEventCommandBase`), never on the request thread. In Revit, Shift+click **Revit Bridge**, tick **Allow my tools**, open **Advanced: commands**, tick your commands and save, then turn the bridge off and on. An entry with the name of an installed official method is skipped: it cannot replace the official command.

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

Settings live in `<addin folder>\Commands\commandRegistry.json` under `settings`. The installer writes them; the Revit Bridge panel (Shift+click the **Revit Bridge** button) edits the same file.

| Field | Default | Meaning |
|---|---|---|
| `logLevel` | `"Info"` | Not read yet: the log always records Info, Warning and Error. Kept across updates |
| `port` | `18080` | TCP port for local mode (bound to `127.0.0.1` only) |
| `mode` | `"tcp"` | `"tcp"` (local) or `"websocket"` (remote) |
| `wsUrl` | | WebSocket base URL, written by pairing (the server decides it); the add-in connects to `<wsUrl>/<deviceId>` |
| `deviceId` | `""` | This installation's device id, assigned when a pairing code is redeemed. Empty = not paired |
| `token` | `""` | Device token from the same reply, sent in the auth handshake. In local mode it is the optional pre-shared token instead; a `token` that is not text (an object or an array) stops the local bridge from starting, with the reason, rather than leave the port open without it |
| `confirmEachRun` | `true` | Show the Yes/No dialog for ad-hoc code runs the server marks for confirmation |
| `allowRemoteCodeExecution` | `false` until paired | Remote mode only: allow `send_code_to_revit`, `dry_run_code_in_revit`, `test_addin_command_in_revit`, `capture_view_in_revit` and `manage_solidified_tools`; rejected with an error when `false`. Set by pairing, cleared by unpairing or by the server revoking this device |
| `localTools` | not set: on in local mode, off in remote mode | **Allow my tools** (see [Use your own tools](#use-your-own-tools)). Written only when you tick or untick it; a value that cannot be read counts as off |
| `autoStart` | `false` | **Turn the bridge on when Revit starts**; skipped while Revit replays a journal |
| `panelShown` | `false` | Set once the Revit Bridge panel has opened; it then never opens by itself on the first switch-on again |

Each setting is read on its own. A number or `true`/`false` where text is expected is that text (`"token": 12345` is the secret `12345`); numbers are read in plain decimal notation (`1.50` stays `1.50`, `1e2` becomes `100.0`). A port may be a numeric string (`"18090"`), and `"true"`/`"false"` (any case), `1` and `0` are booleans. `NaN`, `Infinity` and numbers too large for a decimal are their own text for a text setting and unreadable for any other. A value that cannot be read is left at its default (for `localTools`: off) without discarding the rest of the file, stays in the file as it is, and is listed in the panel and the button's tooltip; only settings you change are written back, and a setting you set is always written.

The `commands` array in the same file registers the 31 built-in commands from `RevitMCPCommandSet`; their `assemblyPath` names the year folder as `{VERSION}` (`RevitMCPCommandSet/{VERSION}/RevitMCPCommandSet.dll`), which the add-in replaces with the year of the Revit it runs in. `Commands\RevitMCPCommandSet\command.json` lists each command's name, description and assembly path. Saving **Advanced: commands** in the panel rewrites only `commands`, and the panel's other controls only the settings they change; everything else in the file, including properties this version does not know, stays. An empty or damaged file is reported and left as it is, not replaced by defaults, and the bridge does not turn on until it is repaired (its token is unknown); a missing file is a fresh install and starts on the defaults.

## License

MIT, see [LICENSE](LICENSE). Upstream code is MIT (c) sparx-fire / mcp-servers-for-revit, see [LICENSE-upstream](LICENSE-upstream). Bundled third-party libraries: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
