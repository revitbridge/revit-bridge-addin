# Changelog

## [Unreleased]

- Reproducible CI build: `Directory.Build.props` sets `Deterministic` and, on GitHub Actions only, `ContinuousIntegrationBuild` (source paths mapped to `/_/`); the workflow prints the SHA-256 of both assemblies so runs can be compared. Local builds keep real paths in the PDB.

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
