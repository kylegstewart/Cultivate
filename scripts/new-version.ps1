#requires -Version 5.1
<#
.SYNOPSIS
    Bumps Cultivate's version and turns CHANGELOG.md's "Unreleased" section into that release.
.DESCRIPTION
    The Release workflow runs this before building. Run it locally only to preview; the workflow commits the result.
    Prints the new version and, with -NotesPath, writes the release notes there.
.EXAMPLE
    .\scripts\new-version.ps1 -Bump minor -NotesPath notes.md
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [string]$Version,     # an explicit X.Y.Z instead of -Bump
    [string]$NotesPath,
    [string]$Date
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\Cultivate\Cultivate.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'
if (-not $Date) { $Date = Get-Date -Format 'yyyy-MM-dd' }

$xml = [IO.File]::ReadAllText($project)
$match = [regex]::Match($xml, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) { throw "No <Version>X.Y.Z</Version> found in $project." }
$current = [version]::new([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value)
if ($Version) {
    $next = [version]$Version
    if ($next.Revision -ge 0 -or $next.Build -lt 0) { throw "Use a three-part version like 1.2.0, not '$Version'." }
} else {
    $next = switch ($Bump) {
        'major' { [version]::new($current.Major + 1, 0, 0) }
        'minor' { [version]::new($current.Major, $current.Minor + 1, 0) }
        default { [version]::new($current.Major, $current.Minor, $current.Build + 1) }
    }
}
if ($next -le $current) { throw "The new version $next must be greater than $current." }
[IO.File]::WriteAllText($project, $xml.Remove($match.Index, $match.Length).Insert($match.Index, "<Version>$next</Version>"))

$text = [IO.File]::ReadAllText($changelog)
$heading = [regex]::Match($text, '(?m)^## Unreleased[ \t]*\r?$')
if (-not $heading.Success) { throw "CHANGELOG.md needs a '## Unreleased' section." }
$bodyStart = $heading.Index + $heading.Length
$following = [regex]::Match($text.Substring($bodyStart), '(?m)^## ')
$bodyEnd = if ($following.Success) { $bodyStart + $following.Index } else { $text.Length }
$notes = $text.Substring($bodyStart, $bodyEnd - $bodyStart).Trim()
$dash = [char]0x2014
$section = "## $next $dash $Date`n`n" + $(if ($notes) { "$notes`n`n" } else { "Maintenance release.`n`n" })
[IO.File]::WriteAllText($changelog, $text.Substring(0, $heading.Index) + "## Unreleased`n`n" + $section + $text.Substring($bodyEnd))
if ($NotesPath) { [IO.File]::WriteAllText($NotesPath, $notes) }
Write-Output $next.ToString()
