# revit-bridge-addin

Revit add-in for [revit-bridge](https://github.com/revitbridge/revit-bridge). It exposes a small JSON-RPC surface (24 commands plus dynamic C# execution) that the `revit-bridge` MCP server or a [revit-bridge-web](https://github.com/revitbridge/revit-bridge-web) host drives.

Revit 2026 插件：本地 TCP 或远程 WebSocket 两种模式，供 `revit-bridge` MCP 服务器或演示宿主调用。

Fork of [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) (MIT). Only `plugin/` and `commandset/` were kept; see [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Install

Requirements: Windows x64, Autodesk Revit 2026, PowerShell 5.1 or later. Close Revit first.

One line, local mode (Revit listens on `127.0.0.1:18080`, no token needed):

```powershell
irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1 | iex
```

Remote mode (Revit connects out to a bridge server; nothing is exposed on your machine):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/revitbridge/revit-bridge-addin/main/installer/install.ps1))) -Mode remote -Server wss://<host>/api/v1/bridge/ws -Slot 1
```

The installer downloads the latest [Release](https://github.com/revitbridge/revit-bridge-addin/releases), verifies it against `SHA256SUMS.txt`, backs up any previous install to `revit-bridge.backup-<timestamp>`, and writes the connection settings. Revit shows an "unsigned add-in" prompt on first load: releases are not code-signed yet (see [Signing](#signing)).

Installer parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Mode local\|remote` | `local` | TCP on localhost, or outbound WebSocket to `-Server` |
| `-Server <wss url>` | | Bridge server base URL, required for `remote` |
| `-Slot 1..5` | `1` | Slot on the bridge server (`remote`) |
| `-Token <value>` | | Optional slot token (`remote`); a token from a previous install is kept when omitted |
| `-AllowRemoteCode` | off | Remote mode: allow `send_code_to_revit` / `manage_solidified_tools` (rejected otherwise) |
| `-Source release\|<dir>` | `release` | Install from GitHub Releases, or from a local directory (see [Build](#build)) |
| `-RevitVersion` | `2026` | Target Revit year |
| `-Repo`, `-Tag` | `revitbridge/revit-bridge-addin`, `latest` | Where to download from (forks point at their own repo) |

Manual install: unzip the release into `%APPDATA%\Autodesk\Revit\Addins\2026\` so that `revit-bridge.addin` and the `revit-bridge\` folder sit side by side, then edit `revit-bridge\Commands\commandRegistry.json` (see [Configure](#configure)).

## Use

1. Start Revit 2026. The **Revit MCP Plugin** panel appears on the ribbon.
2. Click **Revit MCP Switch** to start (or stop) the service. **Settings** opens the connection page.
3. Connect a client:
   - **Local mode**: run the MCP server on the same machine, e.g. `uvx revit-bridge` in Claude Desktop / Claude Code, and `revit-bridge check` to confirm the link. The add-in listens on TCP `127.0.0.1:18080` (JSON-RPC 2.0, loopback only).
   - **Remote mode**: the add-in dials `wss://<host>/api/v1/bridge/ws/<slot>` on port 443, sends `User-Agent: RevitMCPPlugin/0.3` and, if configured, the slot token as the first message. Open the web host, pick the same slot, and paste the token there.

In remote mode, `send_code_to_revit` and `manage_solidified_tools` are rejected unless `allowRemoteCodeExecution` is `true` (`-AllowRemoteCode`). Keep it off outside a dedicated test model. Local mode is not gated by this flag; the local MCP server's own confirmation step (`spec_confirmed`) applies instead. Per-call confirmation dialogs and pairing codes are planned for a later release.

Logs: `%APPDATA%\Autodesk\Revit\Addins\2026\revit-bridge\Logs\mcp_YYYYMMDD.log`.

## Build

Requirements: .NET SDK pinned in [global.json](global.json) (9.0.x, `rollForward: latestFeature`), Windows (WPF). No Revit SDK is needed; Revit API packages come from NuGet.

```powershell
dotnet build plugin -c "Release R26"
dotnet build commandset -c "Release R26"
```

Both projects must be built, in that order. The result is a complete, installable tree in `plugin\bin\AddIn 2026 Release R26\`:

```
AddIn 2026 Release R26\
+-- revit-bridge.addin
+-- revit-bridge\
    +-- RevitMCPPlugin.dll, RevitMCPSDK.dll, Newtonsoft.Json.dll, ...
    +-- Commands\
        +-- commandRegistry.json
        +-- RevitMCPCommandSet\{command.json, 2026\RevitMCPCommandSet.dll + Roslyn}
```

Install your own build with `.\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"`. CI ([build.yml](.github/workflows/build.yml)) runs the same two commands and, on a `v*` tag, publishes `revit-bridge-addin-<tag>.zip` + `SHA256SUMS.txt` to Releases. Official builds and self-builds produce the same tree.

`Debug R26` additionally copies the output into `%APPDATA%\Autodesk\Revit\Addins\2026\` (without touching an existing `commandRegistry.json`).

### Signing

Releases are currently unsigned (Revit shows a one-time "unsigned add-in" prompt). The workflow has an Authenticode step that runs only when the `SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD` secrets are set. Self-builds are always unsigned.

### Forking

Change the add-in identity before you redistribute a fork: see [FORKING.md](FORKING.md).

## Configure

Settings live in `<addin folder>\Commands\commandRegistry.json` under `settings`. The installer writes them; the **Settings** button in Revit edits the same file.

| Field | Default | Meaning |
|---|---|---|
| `logLevel` | `"Info"` | Log verbosity |
| `port` | `18080` | TCP port for local mode (bound to `127.0.0.1` only) |
| `mode` | `"tcp"` | `"tcp"` (local) or `"websocket"` (remote) |
| `wsUrl` | | Bridge server base URL for `websocket` mode, e.g. `wss://host/api/v1/bridge/ws` |
| `slotId` | `"1"` | Slot `1`-`5` on the bridge server |
| `token` | `""` | Optional pre-shared slot token, sent after the WebSocket handshake. Empty = not sent |
| `allowRemoteCodeExecution` | `false` | Remote mode only: allow `send_code_to_revit` and `manage_solidified_tools`; rejected with an error when `false` |

The `commands` array in the same file registers the 24 built-in commands from `RevitMCPCommandSet`; `command.json` next to the DLL holds their parameter schemas.

## License

MIT, see [LICENSE](LICENSE). Upstream code is MIT (c) sparx-fire / mcp-servers-for-revit, see [LICENSE-upstream](LICENSE-upstream). Bundled third-party libraries: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
