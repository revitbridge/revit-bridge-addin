# Forking checklist

MIT lets you fork, modify and redistribute this add-in without asking. Before you ship a fork, give it its own identity so it can be installed next to `revit-bridge` (and next to upstream `mcp-servers-for-revit`) without the two fighting over the same manifest, folder or settings.

## 1. Add-in identity (`plugin/revit-bridge.addin`)

| Field | What to do |
|---|---|
| `ClientId` | Generate a fresh GUID: `[guid]::NewGuid().ToString().ToUpper()`. Never reuse `94B7DDE6-3238-488E-A668-B8C659155206` (revit-bridge) or `7FE2B868-5423-4D7A-A20D-5A6C9BA53F79` (upstream). Revit refuses to load two add-ins with the same `ClientId`. |
| `Name` | Your add-in name, e.g. `my-revit-bridge`. |
| `VendorId` | Your own ASCII vendor id (Autodesk asks for a unique one), e.g. `MyCompany`. |
| `VendorDescription` | Your URL or contact. |
| `Assembly` | `<folder>/RevitMCPPlugin.dll`; `<folder>` must equal `AddinFolderName` below. |

Rename the file to `<your-name>.addin` if you like; the installer picks up whatever single `*.addin` sits next to the folder.

## 2. Install folder name

`AddinFolderName` is set in two places and must match the `Assembly` folder in the manifest:

- `plugin/RevitMCPPlugin.csproj`
- `commandset/RevitMCPCommandSet.csproj`

Using your own folder keeps your `Commands\commandRegistry.json` (connection settings, token) and `Logs\` separate from other installs.

The C# namespace `revit_mcp_plugin` and `FullClassName` `revit_mcp_plugin.Core.Application` can stay; they are not what Revit uses to tell add-ins apart.

## 3. Assembly metadata

`plugin/Properties/AssemblyInfo.cs`: `AssemblyTitle`, `AssemblyProduct`, `AssemblyCompany`, `AssemblyCopyright`. Keep the upstream copyright line.

## 4. Licenses

Keep both license files in your repository and in any binary distribution:

- `LICENSE` (revit-bridge, MIT)
- `LICENSE-upstream` (sparx-fire / mcp-servers-for-revit, MIT)

Update `NOTICE` with your own copyright line on top and leave the existing attributions. Update `THIRD_PARTY_NOTICES.md` if you add or remove NuGet packages.

## 5. Installer and CI

- `installer/install.ps1`: change the default `-Repo` to your GitHub repository so `-Source release` downloads your releases. The zip name pattern `revit-bridge-addin-*.zip` is matched in `Get-ReleaseSource`; rename it together with the `Package` step in `.github/workflows/build.yml`.
- `.github/workflows/build.yml`: the sign step looks for `revit-bridge\RevitMCPPlugin.dll`; update the folder name there too.
- Releases from CI are unsigned unless you set the `SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD` secrets. Revit shows an "unsigned add-in" prompt for unsigned builds.

## 6. Defaults you may want to change

- `commandRegistry.json` (repo root): the shipped default `settings`. The server URL (`wsUrl`) has no built-in default in code; it lives only in this file and is written by the installer (`-Server`) or the Settings page.
- `plugin/Configuration/ServiceSettings.cs`: default port `18080`.
