# check-release.ps1
#
# Checks a set of release files before they are published (or after they were downloaded):
#   - <Dist> holds exactly one revit-bridge-addin-<Version>-revit<year>.zip per year in -Years
#     and no other revit-bridge-addin-*.zip;
#   - SHA256SUMS.txt lists every zip exactly once, nothing else, and each hash matches;
#   - each zip has revit-bridge.addin and the revit-bridge/ folder at its root and nothing
#     else there, uses "/" in entry names, holds RevitMCPPlugin.dll, commandRegistry.json,
#     the command set's command.json and <year>/RevitMCPCommandSet.dll, no other year's
#     command set, and a registry whose assemblyPath entries name no other year.
# Prints one line per check and exits 0 when all pass, 1 otherwise.
#
# Usage: .github/scripts/check-release.ps1 -Version <version> -Years <years> [-Dist <dir>]
#   -Years  e.g. 2020,2021,2023 or "2020 2021 2023"
#   -Dist   defaults to dist under the repository root
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string[]]$Years,
    [string]$Dist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrEmpty($Dist)) {
    $Dist = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist'
}
$yearList = @($Years | ForEach-Object { [string]$_ -split '[,;\s]+' } | Where-Object { $_ -ne '' } | Sort-Object -Unique)

$problems = New-Object 'System.Collections.Generic.List[string]'
foreach ($y in $yearList) {
    if ($y -notmatch '^20\d\d$') { $problems.Add("-Years: '$y' is not a Revit year") }
}

# Zip names
$expectedNames = @($yearList | ForEach-Object { "revit-bridge-addin-$Version-revit$_.zip" })
$zips = @(Get-ChildItem -LiteralPath $Dist -File -Filter 'revit-bridge-addin-*.zip' | Sort-Object Name)
$zipNames = @($zips | ForEach-Object { $_.Name })
foreach ($name in $expectedNames) {
    if ($zipNames -notcontains $name) { $problems.Add("missing zip: $name") }
}
foreach ($name in $zipNames) {
    if ($expectedNames -notcontains $name) { $problems.Add("unexpected zip: $name") }
}
Write-Host "  zips: $($zipNames.Count) found, $($expectedNames.Count) expected ($($yearList -join ', '))"

# SHA256SUMS.txt
$sumsPath = Join-Path $Dist 'SHA256SUMS.txt'
$listed = @{}
if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
    $problems.Add("missing: SHA256SUMS.txt")
}
else {
    $bytes = [IO.File]::ReadAllBytes($sumsPath)
    if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) { $problems.Add("SHA256SUMS.txt is not ASCII") }
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
        $lineNo++
        if ($line -eq '') { continue }
        if ($line -notmatch '^([0-9a-f]{64})  (\S+)$') {
            $problems.Add("SHA256SUMS.txt line ${lineNo} is not '<lower-case sha256>  <name>': $line")
            continue
        }
        $hash = $Matches[1]
        $name = $Matches[2]
        if ($listed.ContainsKey($name)) { $problems.Add("SHA256SUMS.txt lists $name twice") ; continue }
        $listed[$name] = $hash
        $file = Join-Path $Dist $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            $problems.Add("SHA256SUMS.txt lists $name, which is not in $Dist")
            continue
        }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $hash) { $problems.Add("hash mismatch for ${name}: listed $hash, file $actual") }
        else { Write-Host "  hash ok: $name" }
    }
    foreach ($name in $zipNames) {
        if (-not $listed.ContainsKey($name)) { $problems.Add("SHA256SUMS.txt has no entry for $name") }
    }
}

# Layout of each zip
foreach ($zip in $zips) {
    if ($zip.Name -notmatch '-revit(20\d\d)\.zip$') { continue }
    $year = $Matches[1]
    $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        $where = $zip.Name
        foreach ($entry in $entries) {
            if ($entry.Contains('\')) { $problems.Add("${where}: entry name with a backslash: $entry"); break }
        }
        $roots = @($entries | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        $rootFiles = @($entries | Where-Object { $_ -notmatch '/' })
        $manifests = @($rootFiles | Where-Object { $_ -like '*.addin' })
        if ($manifests.Count -ne 1 -or $manifests[0] -ne 'revit-bridge.addin') { $problems.Add("${where}: expected revit-bridge.addin at the root, found [$($manifests -join ', ')]") }
        foreach ($root in $roots) {
            if ($root -ne 'revit-bridge.addin' -and $root -ne 'revit-bridge') { $problems.Add("${where}: unexpected entry at the root: $root") }
        }
        foreach ($rel in @('revit-bridge/RevitMCPPlugin.dll', 'revit-bridge/Commands/commandRegistry.json', 'revit-bridge/Commands/RevitMCPCommandSet/command.json', "revit-bridge/Commands/RevitMCPCommandSet/$year/RevitMCPCommandSet.dll")) {
            if ($entries -notcontains $rel) { $problems.Add("${where}: missing $rel") }
        }
        $setYears = @($entries | Where-Object { $_ -match '^revit-bridge/Commands/RevitMCPCommandSet/(20\d\d)/' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
        if ($setYears.Count -ne 1 -or $setYears[0] -ne $year) { $problems.Add("${where}: command set folders for [$($setYears -join ', ')], expected $year only") }
        $registryEntry = $archive.GetEntry('revit-bridge/Commands/commandRegistry.json')
        if ($null -ne $registryEntry) {
            $reader = New-Object IO.StreamReader($registryEntry.Open(), [Text.Encoding]::UTF8)
            try { $registry = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Close() }
            foreach ($command in @($registry.commands)) {
                $path = [string]$command.assemblyPath
                if ($path -match '(?<!\d)(20\d\d)(?!\d)' -and $Matches[1] -ne $year) { $problems.Add("${where}: $($command.commandName) assemblyPath names Revit $($Matches[1]): $path") }
            }
        }
        Write-Host "  layout checked: $where ($($entries.Count) entries)"
    }
    finally {
        $archive.Dispose()
    }
}

if ($problems.Count -eq 0) {
    Write-Host "check-release: OK - $($zipNames.Count) packages for Revit $($yearList -join ', ') and SHA256SUMS.txt agree"
    exit 0
}
Write-Host "check-release: FAILED - $($problems.Count) problem(s):"
foreach ($p in $problems) { Write-Host "  $p" }
exit 1
