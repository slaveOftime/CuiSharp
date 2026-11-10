<#
.SYNOPSIS
    Prints the CHANGELOG section for one version — the body the GitHub release publishes.

.DESCRIPTION
    The changelog is the only place release notes are written. This pulls the section for a tag out
    of it, so the release page and the repository cannot say different things, and fails when the
    section is missing or still sitting under [Unreleased] — the two ways a release ships without
    notes.

.EXAMPLE
    pwsh tools/release-notes.ps1 -Version v0.1.0

.EXAMPLE
    pwsh tools/release-notes.ps1 -Version 0.2.0 -OutFile release-notes.md
#>
[CmdletBinding()]
param(
    # The version or tag to publish: `0.1.0` and `v0.1.0` are the same release.
    [Parameter(Mandatory)][string] $Version,

    # Write here instead of the pipeline.
    [string] $OutFile,

    # The file to read; the changelog beside this script's repository.
    [string] $Path
)

$ErrorActionPreference = 'Stop'

if (-not $Path) { $Path = Join-Path $PSScriptRoot '../CHANGELOG.md' }
if (-not (Test-Path $Path)) { throw "release-notes: no changelog at $Path" }

$wanted = $Version.TrimStart('v', 'V')
$lines = (Get-Content -Raw -Path $Path).Replace("`r`n", "`n").Split("`n")

# A section starts at `## [` and runs to the next one.
$start = -1
$end = $lines.Count
for ($i = 0; $i -lt $lines.Count; $i++) {
    if (-not $lines[$i].StartsWith('## [')) { continue }

    if ($start -lt 0) {
        if ($lines[$i] -match '^## \[([^\]]+)\]') {
            if ($Matches[1] -eq $wanted) { $start = $i }
            elseif ($Matches[1] -match '^Unreleased$' -and $wanted -eq 'Unreleased') { $start = $i }
        }
        continue
    }

    $end = $i
    break
}

if ($start -lt 0) {
    throw "release-notes: CHANGELOG.md has no section for $wanted. Add '## [$wanted] — <date>' with what changed, then release."
}

$body = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
if ($body.Length -eq 0) { throw "release-notes: the section for $wanted is empty." }
if ($body -match '(?im)^- (nothing yet|tbd)') { throw "release-notes: the section for $wanted says nothing yet." }

if ($OutFile) {
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $OutFile), $body + "`n")
    Write-Host "release-notes: wrote $wanted to $OutFile"
} else {
    $body
}
