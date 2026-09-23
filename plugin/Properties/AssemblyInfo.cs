using System.Reflection;
using System.Runtime.InteropServices;

// General information about this assembly.
[assembly: AssemblyTitle("revit-bridge-addin")]
[assembly: AssemblyDescription("Revit add-in for revit-bridge (fork of mcp-servers-for-revit)")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("revitbridge")]
[assembly: AssemblyProduct("revit-bridge-addin")]
[assembly: AssemblyCopyright("Copyright (c) 2026 revitbridge; portions Copyright (c) 2026 sparx-fire / mcp-servers-for-revit")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM.
// It is unrelated to the Revit add-in ClientId in revit-bridge.addin.
[assembly: Guid("43cd0fd7-df41-4f64-92be-a0f78666d86f")]

// Version information. Keep in sync with CHANGELOG.md.
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

#if NET5_0_OR_GREATER || NETCOREAPP3_1_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
#endif
