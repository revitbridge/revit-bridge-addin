# Third-party notices

Libraries that ship inside the built add-in (`AddIn 2026 Release R26\revit-bridge\`). All are pulled from NuGet at build time; no source is vendored.

| Component | Version (R26 build) | License | Source |
|---|---|---|---|
| RevitMCPSDK | 2026.0.0.5 | MIT, (c) Duong Tran Quang - DTDucas | https://github.com/DTDucas/RevitMCPSDK |
| Newtonsoft.Json | 13.0.3 | MIT, (c) James Newton-King | https://www.newtonsoft.com/json |
| Microsoft.CodeAnalysis (Roslyn), Microsoft.CodeAnalysis.CSharp | 4.8.0 | MIT, (c) Microsoft Corporation | https://github.com/dotnet/roslyn |
| Nice3point.Revit.Toolkit | 2026.x | MIT, (c) Nice3point | https://github.com/Nice3point/RevitToolkit |
| Nice3point.Revit.Extensions | 2026.x | MIT, (c) Nice3point | https://github.com/Nice3point/RevitExtensions |
| Microsoft.Windows.SDK.NET, WinRT.Runtime (C#/WinRT projection) | 10.0.19041.x | MIT, (c) Microsoft Corporation | https://github.com/microsoft/CsWinRT |

Build-time only (not redistributed):

| Component | License | Source |
|---|---|---|
| Nice3point.Revit.Api.RevitAPI / RevitAPIUI (reference assemblies for the Autodesk Revit API; the Revit API itself is (c) Autodesk and licensed with Revit) | MIT (package), Autodesk terms (API) | https://github.com/Nice3point/RevitApi |
| Nice3point.Revit.Build.Tasks | MIT, (c) Nice3point | https://github.com/Nice3point/Revit.Build.Tasks |
| Microsoft.CSharp, System.Data.DataSetExtensions | MIT, (c) .NET Foundation | https://github.com/dotnet/runtime |

Upstream project this add-in is forked from:

| Component | License | Source |
|---|---|---|
| mcp-servers-for-revit (`plugin/`, `commandset/`) | MIT, (c) 2026 sparx-fire, (c) 2026 mcp-servers-for-revit, see [LICENSE-upstream](LICENSE-upstream) | https://github.com/mcp-servers-for-revit/mcp-servers-for-revit |

Full license texts are available from the linked repositories and inside the NuGet packages (`~/.nuget/packages/<id>/<version>/`).
