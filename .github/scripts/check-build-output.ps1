# check-build-output.ps1
#
# Checks that the Release build of one Revit year produced the files an install needs,
# before anything hashes, signs or packages them:
#   revit-bridge.addin
#   revit-bridge\RevitMCPPlugin.dll
#   revit-bridge\Commands\commandRegistry.json
#   revit-bridge\Commands\RevitMCPCommandSet\command.json
#   revit-bridge\Commands\RevitMCPCommandSet\<year>\RevitMCPCommandSet.dll
# and that no other year's command set is in the tree (each package holds one year).
# Exits 0 when all is well; otherwise lists every problem and exits 1.
#
# Usage: .github/scripts/check-build-output.ps1 -Year <year> [-OutputDir <dir>]
#   -OutputDir defaults to plugin\bin\AddIn <year> Release R<yy> under the repository root
#   (two levels above this script).
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [Parameter(Mandatory = $true)]
    [string]$Year,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($Year -notmatch '^20\d\d$') {
    Write-Host "check-build-output: FAILED - -Year must be a Revit year such as 2026, not '$Year'"
    exit 1
}

if ([string]::IsNullOrEmpty($OutputDir)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $OutputDir = Join-Path $repoRoot ("plugin\bin\AddIn {0} Release R{1}" -f $Year, $Year.Substring(2))
}

$expected = @(
    'revit-bridge.addin',
    'revit-bridge\RevitMCPPlugin.dll',
    'revit-bridge\Commands\commandRegistry.json',
    'revit-bridge\Commands\RevitMCPCommandSet\command.json',
    "revit-bridge\Commands\RevitMCPCommandSet\$Year\RevitMCPCommandSet.dll"
)

$problems = New-Object 'System.Collections.Generic.List[string]'
foreach ($rel in $expected) {
    $path = Join-Path $OutputDir $rel
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Write-Host "  found:   $path"
    }
    else {
        $problems.Add("missing: $path")
    }
}

$setDir = Join-Path $OutputDir 'revit-bridge\Commands\RevitMCPCommandSet'
if (Test-Path -LiteralPath $setDir -PathType Container) {
    foreach ($dir in @(Get-ChildItem -LiteralPath $setDir -Directory)) {
        if ($dir.Name -ne $Year) {
            $problems.Add("another year's folder in the tree: $($dir.FullName)")
        }
    }
}

if ($problems.Count -eq 0) {
    Write-Host "check-build-output: OK - all $($expected.Count) expected files for Revit $Year are present under $OutputDir"
    exit 0
}

Write-Host "check-build-output: FAILED - $($problems.Count) problem(s) under ${OutputDir}:"
foreach ($p in $problems) {
    Write-Host "  $p"
}
exit 1
