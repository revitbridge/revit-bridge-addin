# check-build-output.ps1
#
# Checks that the Release R26 build produced the files an install needs, before
# anything hashes, signs or packages them:
#   revit-bridge.addin
#   revit-bridge\RevitMCPPlugin.dll
#   revit-bridge\Commands\commandRegistry.json
#   revit-bridge\Commands\RevitMCPCommandSet\command.json
#   revit-bridge\Commands\RevitMCPCommandSet\2026\RevitMCPCommandSet.dll
# Exits 0 when all are present; otherwise lists every missing path and exits 1.
#
# Usage: .github/scripts/check-build-output.ps1 [-OutputDir <dir>]
#   -OutputDir defaults to plugin\bin\AddIn 2026 Release R26 under the repository root
#   (two levels above this script).
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($OutputDir)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $OutputDir = Join-Path $repoRoot 'plugin\bin\AddIn 2026 Release R26'
}

$expected = @(
    'revit-bridge.addin',
    'revit-bridge\RevitMCPPlugin.dll',
    'revit-bridge\Commands\commandRegistry.json',
    'revit-bridge\Commands\RevitMCPCommandSet\command.json',
    'revit-bridge\Commands\RevitMCPCommandSet\2026\RevitMCPCommandSet.dll'
)

$missing = New-Object 'System.Collections.Generic.List[string]'
foreach ($rel in $expected) {
    $path = Join-Path $OutputDir $rel
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Write-Host "  found:   $path"
    }
    else {
        $missing.Add($path)
    }
}

if ($missing.Count -eq 0) {
    Write-Host "check-build-output: OK - all $($expected.Count) expected files are present under $OutputDir"
    exit 0
}

Write-Host "check-build-output: FAILED - $($missing.Count) of $($expected.Count) expected files are missing under ${OutputDir}:"
foreach ($path in $missing) {
    Write-Host "  missing: $path"
}
exit 1
