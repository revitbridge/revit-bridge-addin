# revit-bridge-addin

Revit 2026 add-in (C#, .NET 8 target, WPF). Fork of mcp-servers-for-revit; only `plugin/` and `commandset/` are kept.

## Build

```powershell
dotnet build plugin -c "Release R26"
dotnet build commandset -c "Release R26"
```

Build order matters: `commandset` copies its output into `plugin\bin\AddIn 2026 Release R26\revit-bridge\Commands\`. The SDK version is pinned in `global.json`; CI uses the same file.

## Test

No unit tests. Smoke = both builds succeed and `plugin\bin\AddIn 2026 Release R26\` contains `revit-bridge.addin`, `revit-bridge\RevitMCPPlugin.dll` and `revit-bridge\Commands\commandRegistry.json`. Installer dry run without touching the real profile: set `$env:APPDATA` to a scratch folder, then `.\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"`.

## Hard constraints

- `plugin/revit-bridge.addin`: `ClientId` is this project's own GUID; never reuse the upstream GUID. `Name` `revit-bridge`, `VendorId` `RevitBridge`.
- `AddinFolderName` (both csproj) must equal the folder in the manifest's `<Assembly>` path. Do not rename the C# namespace `revit_mcp_plugin`.
- `installer/install.ps1`, workflow scripts and any `.ps1`/`.sh`/`.py` here are ASCII only. The installer must not require a token file; `-AllowRemoteCode` stays off by default.
- Pairing codes, server-side execution policy and per-call confirmation belong to a later phase; do not add them here without a plan.
- Public repo: only `README.md`, `CHANGELOG.md`, `CLAUDE.md`, license/notice files and `FORKING.md` as documentation. No design notes, research or logs.
- No secrets: `.env*`, `.secrets/`, `*.token` are ignored and must never be committed.
