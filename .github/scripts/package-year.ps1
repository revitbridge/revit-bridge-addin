# package-year.ps1
#
# Zips the build output of one Revit year into
#   <Dist>\revit-bridge-addin-<Version>-revit<Year>.zip
# with revit-bridge.addin and the revit-bridge\ folder at the root of the zip (the tree an
# install copies), and prints the zip's SHA-256. SHA256SUMS.txt over all years is written
# later, once, by write-sha256sums.ps1.
#
# Usage: .github/scripts/package-year.ps1 -Year <year> -Version <version> [-OutputDir <dir>] [-Dist <dir>]
#   -Version   v0.4.0 for a tag build, dev-<sha7> otherwise
#   -OutputDir defaults to plugin\bin\AddIn <year> Release R<yy> under the repository root
#   -Dist      defaults to dist under the repository root
# Prints "zip_name=<name>" as its last line.
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7 with the same result: the zip is
# written with System.IO.Compression and "/" in entry names (5.1's Compress-Archive would
# write "\"), files in ordinal order of their path.

param(
    [Parameter(Mandatory = $true)]
    [string]$Year,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputDir,
    [string]$Dist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($Year -notmatch '^20\d\d$') { throw "-Year must be a Revit year such as 2026, not '$Year'" }
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') { throw "-Version '$Version' is not usable in a file name" }

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $repoRoot ("plugin\bin\AddIn {0} Release R{1}" -f $Year, $Year.Substring(2))
}
if ([string]::IsNullOrEmpty($Dist)) {
    $Dist = Join-Path $repoRoot 'dist'
}
if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) { throw "build output not found: $OutputDir" }

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$zipName = "revit-bridge-addin-$Version-revit$Year.zip"
$zipPath = Join-Path $Dist $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Every file below the output folder, by its path relative to it: the folder's children sit
# at the root of the zip.
$root = (Resolve-Path -LiteralPath $OutputDir).ProviderPath.TrimEnd('\') + '\'
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { $_.FullName })
[Array]::Sort($files, [StringComparer]::Ordinal)
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $entryName = $file.Substring($root.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entryName, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "$hash  $zipName"
Write-Output "zip_name=$zipName"
