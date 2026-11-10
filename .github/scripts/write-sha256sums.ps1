# write-sha256sums.ps1
#
# Writes <Dist>\SHA256SUMS.txt for every revit-bridge-addin-*.zip in <Dist>: one line
# "<sha256>  <file name>" per zip, sorted by name, LF line ends, ASCII. This is the file the
# installer verifies each package against.
#
# Usage: .github/scripts/write-sha256sums.ps1 [-Dist <dir>]
#   -Dist defaults to dist under the repository root
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [string]$Dist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($Dist)) {
    $Dist = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist'
}
$zips = @(Get-ChildItem -LiteralPath $Dist -File -Filter 'revit-bridge-addin-*.zip' | Sort-Object Name)
if ($zips.Count -eq 0) { throw "no revit-bridge-addin-*.zip in $Dist" }

$lines = foreach ($zip in $zips) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $zip.Name
}
$text = ($lines -join "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS.txt'), $text, (New-Object System.Text.ASCIIEncoding))
Write-Host $text.TrimEnd()
