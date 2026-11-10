<#
.SYNOPSIS
    Lists what CUI's public API gained or lost between two revisions, without checking either out.

.DESCRIPTION
    A sync starts by reading a 15 000-line diff, which is the most expensive part of the job and
    mostly says nothing: upstream restructures its C files constantly. This prints only what CuiSharp
    has to answer for — public functions, structs and header sections — plus the upstream changelog
    section for the target, which already says in prose which components are new.

.EXAMPLE
    pwsh skills/sync-cui/scripts/cui-api-diff.ps1                  # pinned commit -> newest tag

.EXAMPLE
    pwsh skills/sync-cui/scripts/cui-api-diff.ps1 -From 3d60199 -To v1.3.1 -Notes

.EXAMPLE
    pwsh skills/sync-cui/scripts/cui-api-diff.ps1 -To e8688b8       # a commit instead of a tag
#>
[CmdletBinding()]
param(
    # What the binding was generated from. Defaults to the submodule's checked-out commit, which is
    # the pinned one until the bump.
    [string] $From,

    # What to compare against. Defaults to the newest tag in the submodule.
    [string] $To,

    # Print the upstream CHANGELOG.md section for $To: it names the new components in prose.
    [switch] $Notes,

    # The submodule to read; defaults to the one this repository pins.
    [string] $Submodule
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $Submodule) { $Submodule = Join-Path $root 'native/CUI' }
if (-not (Test-Path (Join-Path $Submodule 'include/cui/cui.h'))) { throw "no CUI checkout at $Submodule" }

# Not named `Git`: PowerShell resolves the `git` inside it to the function itself, and recurse.
function Invoke-Git([string[]] $Arguments) {
    $output = @(& git -C $Submodule @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ("git " + ($Arguments -join ' ') + " failed: " + ($output -join ' ')) }
    return $output
}

function Show-List([string] $Title, $Items) {
    if ($null -eq $Items -or $Items.Count -eq 0) { return }
    Write-Host "$Title ($($Items.Count))" -ForegroundColor Yellow
    foreach ($item in $Items) { Write-Host "  $item" }
    Write-Host ''
}

if (-not $From) { $From = (Invoke-Git @('rev-parse', '--short', 'HEAD')).Trim() }
if (-not $To) {
    $tags = @(Invoke-Git @('tag', '--sort=-v:refname')) | Where-Object { $_ -match '^v\d' }
    if ($tags.Count -eq 0) { throw 'the submodule has no v* tags: pass -To' }
    $To = $tags[0].Trim()
}

$range = "$From..$To"
Write-Host "CUI $range" -ForegroundColor Cyan
(Invoke-Git @('log', '--oneline', $range)) | ForEach-Object { Write-Host "  $_" }
Write-Host ''

# The headers, not the sources: a declaration is what a wrapper answers for, and a function split
# into phases inside src/ is invisible to the binding.
$headers = 'include/cui/cui.h', 'include/cui/cui_app.h', 'include/cui/cui_render.h'
$diff = Invoke-Git (@('diff', '--unified=0', $range, '--') + $headers)

# One declaration may wrap over several lines; the name is in the first few.
function Get-Declared([string] $Marker, [string] $Pattern) {
    $found = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $diff.Count; $i++) {
        if (-not $diff[$i].StartsWith($Marker)) { continue }
        for ($look = 0; $look -lt 3 -and ($i + $look) -lt $diff.Count; $look++) {
            $line = $diff[$i + $look]
            if (-not $line.StartsWith($Marker)) { break }
            if (($line.Substring(1)) -match $Pattern) { [void]$found.Add($Matches[1]); break }
        }
    }
    return , $found
}

$added = Get-Declared '+' 'CUI_API\s+[\w \*]+?\b(cui_\w+)\s*\('
$removed = Get-Declared '-' 'CUI_API\s+[\w \*]+?\b(cui_\w+)\s*\('
foreach ($name in @($added)) { if ($removed.Contains($name)) { [void]$removed.Remove($name) } }

Show-List 'functions added' $added
Show-List 'functions removed' $removed
Show-List 'structs added' (Get-Declared '+' 'typedef\s+struct\s+(cui_\w+)')
Show-List 'structs removed' (Get-Declared '-' 'typedef\s+struct\s+(cui_\w+)')

# A section title is the file the wrapper lands in: one extension class per section.
$sections = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in $diff) {
    if ($line -notmatch '^\+\s*/\*\s*-{4,}\s*(.+?)\s*-{2,}\s*(\*/)?\s*$') { continue }
    # A bare rule is not a title; only a rule with words in it names a section.
    $title = $Matches[1].Trim('-', ' ', '*')
    if ($title.Length -gt 0) { [void]$sections.Add($title) }
}
Show-List 'new header sections' $sections

if ($Notes) {
    # Not `$notes`: PowerShell variable names are case-insensitive, so it would be the switch itself.
    $section = [System.Collections.Generic.List[string]]::new()
    $started = $false
    foreach ($line in (Invoke-Git @('show', "${To}:CHANGELOG.md"))) {
        if ($line -match '^## ') {
            if ($started) { break }
            $started = $true
        }
        if ($started) { $section.Add($line) }
    }
    if ($section.Count -gt 0) {
        Write-Host "upstream notes for ${To}" -ForegroundColor Yellow
        $section | ForEach-Object { Write-Host "  $_" }
    }
}

if ($added.Count -eq 0 -and $removed.Count -eq 0 -and $sections.Count -eq 0) {
    Write-Host 'No public API change: the bump is a rebuild and a regenerate.' -ForegroundColor Green
}
