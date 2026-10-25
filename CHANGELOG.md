# Changelog

## [Unreleased] (0.2.0)

Pairing replaces the fixed slots and the pre-shared slot token. Existing remote installs must pair again: get a code from the site and re-run the installer with `-Pair`, or enter it under Settings > Connection. Local (TCP) installs are unaffected.

- Pairing replaces slots: `-Mode remote -Server https://<host> -Pair XXXX-XXXX` redeems the code at `/api/v1/bridge/devices/redeem` and stores `deviceId`, `token` and the `wsUrl` the server returns; **`-Slot` and `-Token` are gone**. The Settings window does the same from a pairing-code field. A 0.1.x config still loads and counts as not paired.
- The WebSocket handshake always sends `{"type":"auth","device_id","token"}` to `<wsUrl>/<deviceId>`. Close 4003 (unpaired or revoked) stops reconnecting and shows that state on the ribbon switch and in the Settings window; close 4002 keeps the retry.
- Pairing switches `allowRemoteCodeExecution` on: the installer's `-Pair` and the Settings window both set it (pass `-AllowRemoteCode:$false` to pair without it), and **Unpair** in the Settings window or a close 4003 from the server clears it again. An update without `-Pair` keeps the previous value.
- Ad-hoc code runs the server marks with a `confirm` object show a Yes/No dialog in Revit (default No, body cut at 2000 characters) before running; No answers `-32001 "declined on device"`. Capability packs, probes and reads never prompt. The `confirmEachRun` setting (default on) turns the dialog off.
- The log file is written as UTF-8 with a BOM. The content was already UTF-8, but without the BOM Windows tools (PowerShell 5.1's `Get-Content`, Notepad) read it in the ANSI code page and showed the Chinese half as mojibake, which made the log useless for diagnosing the confirmation dialog. Existing log files are appended to, not rewritten.
- Reproducible CI build: `Directory.Build.props` sets `Deterministic` and, on GitHub Actions only, `ContinuousIntegrationBuild` (source paths mapped to `/_/`); the workflow prints the SHA-256 of both assemblies so runs can be compared. Local builds keep real paths in the PDB.
- `commandset` builds with 0 warnings: `GeometryUtils.FindIntersection` uses `Curve.Intersect(Curve, CurveIntersectResultOption.Detailed)` on Revit 2026 (old overload kept for older configurations), an unused `catch` variable is dropped, and the meaningless `System.Net.Http` reference (net8) is removed.
- New `check_code_in_revit` (`code`, optional `parameters`, which is never evaluated): compiles a snippet with the same wrapper, reference whitelist and symbol blacklist as `send_code_to_revit` - one shared compile step, not a copy - but only emits to memory: nothing is loaded or run, no transaction, no ExternalEvent, no dialog, no active document needed. It runs on the socket thread with a 30 s timeout and returns `compiled`, `diagnostics` (`id`, `severity` error/warning/info, 1-based user-code `line`/`column` or `null` with `in_wrapper: true`, English `message`, `symbol` for CS1061/CS0117/CS0246/CS0103/CS1501/CS1503/CS7036), `truncated` (errors first, at most 50 returned), `fingerprint` (`sha256:` over code, `wrapper_version`, the referenced assembly names and versions, and the add-in version), `wrapper_version` (1) and `errorMessage` (compiler exceptions and timeouts only). A blacklisted symbol is an `RB0001` diagnostic, not an exception.
- New `get_bridge_capabilities`: `protocol` 2, `addin_version`, the registered `methods`, `features` (`check_code`, `structured_diagnostics`, `kit: false`), `revit` (`VersionNumber` / `VersionBuild` / `VersionName`, read once when commands load and cached, so the call never touches Revit objects off the API thread), `runtime` (framework description, CLR version) and `kit: null`. Add-ins without it answer `-32601 Method not found`. To list the registered methods, `CommandManager` now prefers a command constructor taking `(UIApplication, ICommandRegistry)`.
- Neither new method carries `confirm` or is gated by `allowRemoteCodeExecution` in remote mode; both are registered in `commandRegistry.json` and `command.json`.
- `send_code_to_revit` compiles through the same step. Compile errors in `errorMessage` now give the user-code line (they were the 0-based line of the wrapped text, 13 too high) and English messages; a failed compile adds `diagnostics` (same shape as above) and every compile adds `fingerprint`, while `success` / `result` / `errorMessage` are unchanged. A failed compile no longer opens and rolls back a transaction, and a missing active document is reported in `errorMessage` instead of failing inside the error handler.

## 0.1.1 (2026-09-17)

- No built-in WebSocket server URL: `wsUrl` comes only from `commandRegistry.json` (installer `-Server` or the Settings page). The Settings page no longer pre-fills a host; the switch shows a message instead of connecting when `wsUrl` is empty in `websocket` mode.

## 0.1.0 (2026-09-17)

First release under the revitbridge organization. History before this point lives in `imkcrevit/revit-api-rag` (`revit_plugin/`), imported with `git subtree split`.

- New add-in identity: `Name` `revit-bridge`, `VendorId` `RevitBridge`, new `ClientId`; installs into `Addins\2026\revit-bridge\` so it can coexist with upstream `mcp-servers-for-revit`.
- `global.json` pins the .NET SDK (9.0.313, `rollForward: latestFeature`); GitHub Actions builds `Release R26`, packages `revit-bridge-addin-<tag>.zip` + `SHA256SUMS.txt` and publishes them on `v*` tags. Optional Authenticode step, off by default.
- `installer/install.ps1`: one-line install from GitHub Releases with checksum verification, `-Mode local|remote`, `-Source release|<dir>`, no token file required, `-AllowRemoteCode` off by default.
- Build output `plugin\bin\AddIn 2026 Release R26\` is now a complete installable tree (manifest + folder + default `commandRegistry.json`).
- Shipped `commandRegistry.json` defaults to `mode: "tcp"`.
- Added `NOTICE`, `THIRD_PARTY_NOTICES.md`, `FORKING.md`; removed the demo-kit README and `install-revit-demo.ps1`.
