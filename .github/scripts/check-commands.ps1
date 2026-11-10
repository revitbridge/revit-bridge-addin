# check-commands.ps1
#
# Checks that the three lists of command names agree:
#   command.json          repo root, "commands"[].commandName (deployed next to RevitMCPCommandSet.dll)
#   commandRegistry.json  repo root, "commands"[].commandName (deployed to revit-bridge\Commands\)
#   commandset\**\*.cs    each command's  ... string CommandName => "<name>";
# Prints the count and exits 0 when all three sets are equal. Otherwise prints, for each
# pair, the names found in one set and missing from the other, and exits 1. It also fails
# when a set comes out empty, a name is listed twice, or a CommandName declaration in
# commandset cannot be read.
#
# Usage: .github/scripts/check-commands.ps1 [-RepoRoot <dir>]
#   -RepoRoot defaults to the folder two levels above this script (the repository root).
#
# ASCII only. Runs under Windows PowerShell 5.1 and pwsh 7.

param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    Write-Host "check-commands: FAILED - repository root not found: $RepoRoot"
    exit 1
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

$problems = New-Object 'System.Collections.Generic.List[string]'

function Add-Name {
    param($Set, [string]$Name, [string]$Source, [string]$Where)
    if (-not $Set.Add($Name)) {
        $problems.Add("${Source}: command name '$Name' is listed more than once ($Where)")
    }
}

function Read-JsonNames {
    param([string]$FileName, $Set)
    $path = Join-Path $RepoRoot $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $problems.Add("${FileName}: file not found at $path")
        return
    }
    try {
        $doc = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    catch {
        $problems.Add("${FileName}: not valid JSON: $($_.Exception.Message)")
        return
    }
    $commandsProp = $null
    if ($null -ne $doc) {
        $commandsProp = $doc.PSObject.Properties['commands']
    }
    if ($null -eq $commandsProp -or $null -eq $commandsProp.Value) {
        $problems.Add("${FileName}: no ""commands"" array")
        return
    }
    $index = 0
    foreach ($entry in @($commandsProp.Value)) {
        $nameProp = $null
        if ($null -ne $entry) {
            $nameProp = $entry.PSObject.Properties['commandName']
        }
        if ($null -eq $nameProp -or -not ($nameProp.Value -is [string]) -or [string]::IsNullOrWhiteSpace($nameProp.Value)) {
            $problems.Add("${FileName}: commands[$index] has no commandName")
        }
        else {
            Add-Name -Set $Set -Name $nameProp.Value -Source $FileName -Where "commands[$index]"
        }
        $index++
    }
}

function Get-OnlyIn {
    param($From, $Other)
    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in $From) {
        if (-not $Other.Contains($name)) {
            $result.Add($name)
        }
    }
    $result.Sort([System.StringComparer]::Ordinal)
    return ,$result
}

$commandJsonNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$registryNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$sourceNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

Read-JsonNames -FileName 'command.json' -Set $commandJsonNames
Read-JsonNames -FileName 'commandRegistry.json' -Set $registryNames

# A line that starts a CommandName property declaration (any modifiers, any spelling of string).
$declPattern = '^\s*(?:(?:public|protected|internal|private|override|sealed|virtual|abstract|static|new)\s+)*(?:string|String|System\.String)\s+CommandName\b'
# The only form that is read: [public] [override|sealed] string CommandName => "<name>";
$parsePattern = '^\s*(?:(?:public|override|sealed)\s+)*string\s+CommandName\s*=>\s*"([^"\\]+)"\s*;\s*(?://.*)?$'
# A class whose base list names ExternalEventCommandBase or IRevitCommand.
$classPattern = '(?<mods>(?:\b\w+[ \t]+)*)\bclass\s+(?<name>\w+)\s*(?:<[^>{]*>)?\s*:(?<bases>[^{]*)\{'

$commandsetDir = Join-Path $RepoRoot 'commandset'
if (-not (Test-Path -LiteralPath $commandsetDir -PathType Container)) {
    $problems.Add("commandset: folder not found at $commandsetDir")
}
else {
    $csFiles = @(Get-ChildItem -LiteralPath $commandsetDir -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName.Substring($commandsetDir.Length) -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName)
    foreach ($file in $csFiles) {
        $rel = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
        $readHere = 0
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $trimmed = $lines[$i].Trim()
            if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('/*') -or $trimmed.StartsWith('*')) {
                continue
            }
            if ($lines[$i] -notmatch $declPattern) {
                continue
            }
            $m = [regex]::Match($lines[$i], $parsePattern)
            if ($m.Success) {
                Add-Name -Set $sourceNames -Name $m.Groups[1].Value -Source 'commandset' -Where "${rel}:$($i + 1)"
                $readHere++
            }
            else {
                $problems.Add("commandset: cannot read the command name at ${rel}:$($i + 1): $trimmed")
            }
        }
        $commandClasses = 0
        foreach ($cm in [regex]::Matches([string]::Join("`n", $lines), $classPattern)) {
            if ($cm.Groups['mods'].Value -match '\babstract\b') {
                continue
            }
            if ($cm.Groups['bases'].Value -match '\b(ExternalEventCommandBase|IRevitCommand)\b') {
                $commandClasses++
            }
        }
        if ($commandClasses -gt $readHere) {
            $problems.Add("commandset: $rel declares $commandClasses command class(es) but only $readHere readable CommandName => ""<name>"";")
        }
    }
}

$sets = @(
    @{ Label = 'command.json'; Names = $commandJsonNames },
    @{ Label = 'commandRegistry.json'; Names = $registryNames },
    @{ Label = 'commandset CommandName'; Names = $sourceNames }
)
foreach ($s in $sets) {
    if ($s.Names.Count -eq 0) {
        $problems.Add("$($s.Label): no command names found")
    }
}

$diffLines = New-Object 'System.Collections.Generic.List[string]'
$pairs = @(@(0, 1), @(0, 2), @(1, 2))
foreach ($pair in $pairs) {
    $a = $sets[$pair[0]]
    $b = $sets[$pair[1]]
    $onlyA = Get-OnlyIn -From $a.Names -Other $b.Names
    $onlyB = Get-OnlyIn -From $b.Names -Other $a.Names
    if ($onlyA.Count -eq 0 -and $onlyB.Count -eq 0) {
        continue
    }
    $diffLines.Add("  $($a.Label) vs $($b.Label):")
    foreach ($name in $onlyA) {
        $diffLines.Add("    in $($a.Label), missing from $($b.Label): $name")
    }
    foreach ($name in $onlyB) {
        $diffLines.Add("    in $($b.Label), missing from $($a.Label): $name")
    }
}

if ($problems.Count -eq 0 -and $diffLines.Count -eq 0) {
    Write-Host "check-commands: OK - command.json, commandRegistry.json and commandset CommandName list the same $($sourceNames.Count) commands."
    exit 0
}

Write-Host "check-commands: FAILED"
Write-Host ("  counts: command.json={0}, commandRegistry.json={1}, commandset CommandName={2}" -f $commandJsonNames.Count, $registryNames.Count, $sourceNames.Count)
foreach ($p in $problems) {
    Write-Host "  $p"
}
foreach ($line in $diffLines) {
    Write-Host $line
}
exit 1
