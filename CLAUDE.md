# revit-bridge-addin

Revit add-in (C#, WPF) for Revit 2020, 2021 and 2023-2027: .NET Framework 4.8 for 2020-2024, .NET 8 for 2025-2026, .NET 10 for 2027. One code base, one build configuration per year. Fork of mcp-servers-for-revit; only `plugin/` and `commandset/` are kept.

## Build

Requires the .NET SDK pinned in `global.json` (10.0.403, `rollForward: latestFeature`; a .NET 10 SDK, which also builds the net48 and net8 years). CI uses the same file.

```powershell
dotnet build plugin -c "Release R26"
dotnet build commandset -c "Release R26"
```

Configurations: `Release R<yy>` and `Debug R<yy>` for R20, R21, R23, R24, R25, R26, R27 (no R22). Build order matters: `commandset` copies its output into `plugin\bin\AddIn <year> Release R<yy>\revit-bridge\Commands\`. `Debug R<yy>` also copies into `%APPDATA%\Autodesk\Revit\Addins\<year>\`; point `APPDATA` elsewhere to avoid that.

The years built and released are listed once in `.github/workflows/build.yml` (`REVIT_YEARS`); a year there needs its configuration in both projects and the solution. README's "Supported Revit years" says which were load-tested; `$notLoadTestedYears` in `installer/install.ps1` must name the same not-load-tested years.

## Test

No unit tests. Smoke, per year: both builds succeed with 0 warnings and `.github/scripts/check-build-output.ps1 -Year <year>` passes (`revit-bridge.addin`, `revit-bridge\RevitMCPPlugin.dll`, `revit-bridge\Commands\commandRegistry.json`, the command set's `command.json` and `<year>\RevitMCPCommandSet.dll`). Release set: `package-year.ps1` per year, `write-sha256sums.ps1`, then `check-release.ps1 -Version <v> -Years <years>`. Installer dry run without touching the real profile: set `$env:APPDATA` and `$env:TEMP` to a scratch folder, then `.\installer\install.ps1 -Source "plugin\bin\AddIn 2026 Release R26"` (one year's tree) or `-Source <folder with the zips and SHA256SUMS.txt> -RevitVersion 2024,2026`.

## Hard constraints

- `plugin/revit-bridge.addin`: `ClientId` is this project's own GUID; never reuse the upstream GUID. `Name` `revit-bridge`, `VendorId` `RevitBridge`. One manifest for every year.
- `AddinFolderName` (both csproj) must equal the folder in the manifest's `<Assembly>` path. Do not rename the C# namespace `revit_mcp_plugin`.
- Year-specific Revit API goes through `#if REVIT<year>_OR_GREATER` with a comment naming the API and the release; the symbols are defined only in `Directory.Build.targets`. Prefer one helper per differing API (e.g. `ElementIdCompat.FromInt`) to `#if` in commands. `LangVersion` is set once in `Directory.Build.props`.
- `commandRegistry.json` names the command set's year folder as `{VERSION}`; never write a year into it.
- `installer/install.ps1`, workflow scripts and any `.ps1`/`.sh`/`.py` here are ASCII only and run on Windows PowerShell 5.1 and pwsh 7. The installer must not require a token file; `-AllowRemoteCode` stays off by default; `-Pair` pairs exactly one Revit year.
- Pairing codes, server-side execution policy and per-call confirmation belong to a later phase; do not add them here without a plan.
- Public repo: only `README.md`, `CHANGELOG.md`, `CLAUDE.md`, license/notice files and `FORKING.md` as documentation. No design notes, research or logs.
- No secrets: `.env*`, `.secrets/`, `*.token` are ignored and must never be committed.
