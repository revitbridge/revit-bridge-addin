# check-version.ps1
#
# For a release tag: checks that the tag without its leading "v" equals
#   - the first three components of AssemblyVersion in plugin\Properties\AssemblyInfo.cs, and
#   - the version of the topmost released "## <version> ..." heading in CHANGELOG.md
#     (an "Unreleased" heading on top is skipped).
# Exits 0 when all three agree, 1 otherwise.
#
# Usage: .github/scripts/check-version.ps1 -Tag v0.3.0 [-RepoRoot <dir>]
#   -RepoRoot defaults to the folder two levels above this script (the repository root).
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [string]$Tag,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$problems = New-Object 'System.Collections.Generic.List[string]'

# Tag
$tagVersion = $null
if ([string]::IsNullOrEmpty($Tag)) {
    $problems.Add('no tag given (-Tag v<major>.<minor>.<build>)')
}
elseif ($Tag -cmatch '^v(.+)$') {
    $tagVersion = $Matches[1]
    Write-Host "  tag:             $Tag -> $tagVersion"
}
else {
    $problems.Add("tag '$Tag' does not start with 'v'")
}

# AssemblyVersion
$asmRel = 'plugin\Properties\AssemblyInfo.cs'
$asmPath = Join-Path $RepoRoot $asmRel
$asmVersion = $null
if (-not (Test-Path -LiteralPath $asmPath -PathType Leaf)) {
    $problems.Add("$asmRel not found at $asmPath")
}
else {
    $found = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in [System.IO.File]::ReadAllLines($asmPath, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^\s*\[assembly:\s*AssemblyVersion\s*\(\s*"([^"]*)"\s*\)\s*\]') {
            $found.Add($Matches[1])
        }
    }
    if ($found.Count -ne 1) {
        $problems.Add("expected one [assembly: AssemblyVersion(...)] in $asmRel, found $($found.Count)")
    }
    elseif ($found[0] -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
        $asmVersion = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]
        Write-Host "  AssemblyVersion: $($found[0]) -> $asmVersion ($asmRel)"
    }
    else {
        $problems.Add("AssemblyVersion '$($found[0])' in $asmRel is not <major>.<minor>.<build>[.<revision>]")
    }
}

# CHANGELOG.md
$clPath = Join-Path $RepoRoot 'CHANGELOG.md'
$clVersion = $null
if (-not (Test-Path -LiteralPath $clPath -PathType Leaf)) {
    $problems.Add("CHANGELOG.md not found at $clPath")
}
else {
    $heading = $null
    $headingText = $null
    foreach ($line in [System.IO.File]::ReadAllLines($clPath, [System.Text.Encoding]::UTF8)) {
        if ($line -notmatch '^##(?!#)\s*(.*?)\s*$') {
            continue
        }
        $text = $Matches[1]
        if ($text -match '^\[?unreleased\]?(?:\s|$)') {
            continue
        }
        $heading = $line
        $headingText = $text
        break
    }
    if ($null -eq $heading) {
        $problems.Add('CHANGELOG.md has no released "## <version>" heading')
    }
    elseif ($headingText -match '^\[?(\d+\.\d+\.\d+)\]?(?=\s|$)') {
        $clVersion = $Matches[1]
        Write-Host "  CHANGELOG.md:    '$heading' -> $clVersion"
    }
    else {
        $problems.Add("cannot read a version from the topmost released heading in CHANGELOG.md: '$heading'")
    }
}

if ($null -ne $tagVersion -and $null -ne $asmVersion -and $tagVersion -cne $asmVersion) {
    $problems.Add("tag $Tag ($tagVersion) does not match AssemblyVersion $asmVersion")
}
if ($null -ne $tagVersion -and $null -ne $clVersion -and $tagVersion -cne $clVersion) {
    $problems.Add("tag $Tag ($tagVersion) does not match the topmost CHANGELOG.md release $clVersion")
}

if ($problems.Count -eq 0) {
    Write-Host "check-version: OK - tag, AssemblyVersion and CHANGELOG.md all say $tagVersion"
    exit 0
}

Write-Host 'check-version: FAILED'
foreach ($p in $problems) {
    Write-Host "  $p"
}
exit 1
