<#
.SYNOPSIS
    Lists what CUI's public API gained, lost or changed between two revisions, without checking either out.

.DESCRIPTION
    Compares declaration sets from complete header snapshots, not changed lines: moving a
    declaration or reformatting a typedef must not look like a new API. Reports functions,
    structs and header sections, plus the upstream changelog when requested.

.EXAMPLE
    pwsh skills/cui-sync/scripts/cui-api-diff.ps1                  # pinned commit -> newest tag

.EXAMPLE
    pwsh skills/cui-sync/scripts/cui-api-diff.ps1 -From 3d60199 -To v1.3.1 -Notes

.EXAMPLE
    pwsh skills/cui-sync/scripts/cui-api-diff.ps1 -To e8688b8       # a commit instead of a tag
#>
[CmdletBinding()]
param(
    [string] $From,
    [string] $To,
    [switch] $Notes,
    [switch] $Commits,
    [string] $Submodule
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $Submodule) { $Submodule = Join-Path $root 'native/CUI' }
if (-not (Test-Path (Join-Path $Submodule 'include/cui/cui.h'))) { throw "no CUI checkout at $Submodule" }

# Not named Git: PowerShell would resolve the git inside it to the function itself.
function Invoke-Git([string[]] $Arguments) {
    $output = @(& git -C $Submodule @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ("git " + ($Arguments -join ' ') + " failed: " + ($output -join ' ')) }
    return $output
}

function Show-List([string] $Title, $Items) {
    $items = @($Items)
    if ($items.Count -eq 0) { return }
    Write-Host "$Title ($($items.Count))" -ForegroundColor Yellow
    foreach ($item in $items) { Write-Host "  $item" }
    Write-Host ''
}

if (-not $From) { $From = (Invoke-Git @('rev-parse', '--short', 'HEAD')).Trim() }
if (-not $To) {
    $tags = @(Invoke-Git @('tag', '--sort=-v:refname')) | Where-Object { $_ -match '^v\d' }
    if ($tags.Count -eq 0) { throw 'the submodule has no v* tags: pass -To' }
    $To = $tags[0].Trim()
}

$headers = 'include/cui/cui.h', 'include/cui/cui_app.h', 'include/cui/cui_render.h'
function Read-Api([string] $Revision) {
    $text = ($headers | ForEach-Object { (Invoke-Git @('show', "${Revision}:$_")) -join "`n" }) -join "`n"
    $sections = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    # One-line rulers and the two multi-line title forms used by CUI.
    foreach ($match in [regex]::Matches($text, '/\*\s*-{4,}\s*([^\r\n]+?)\s*-{2,}\s*(?:\*/)?(?:\r?\n|$)|/\*\s*-{4,}\s*\*/\s*\r?\n/\*\s*([^\r\n]+?)\s*\*/')) {
        $title = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        $title = $title.Trim('-', ' ', '*')
        if ($title -match '[A-Za-z]') { [void]$sections.Add($title) }
    }
    # Comments may contain example calls and preprocessing contains function-like macros.
    $code = [regex]::Replace($text, '(?s)/\*.*?\*/|//[^\r\n]*', ' ')
    $code = [regex]::Replace($code, '(?m)^[ \t]*#(?:\\\r?\n|[^\r\n])*', '')
    $functions = [System.Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($code, '\bCUI_API\s+[^;{}]*?\b(cui_\w+)\s*\)?\s*\([^;{}]*\)\s*;')) {
        $name = $match.Groups[1].Value
        $declaration = [regex]::Replace($match.Value, "\(\s*$name\s*\)(?=\s*\()", $name)
        # Normalize spacing without merging C tokens (unsigned long is not unsignedlong).
        $functions[$name] = [regex]::Matches($declaration, '\w+|[^\s\w]') -join ' '
    }
    $structs = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($code, '\btypedef\s+struct\s+(cui_\w+)')) { [void]$structs.Add($match.Groups[1].Value) }
    return @{ Functions = $functions; Structs = $structs; Sections = $sections }
}

$range = "$From..$To"
$count = Invoke-Git @('rev-list', '--count', $range)
Write-Host "CUI $range ($count commits on the target side)" -ForegroundColor Cyan
if ($Commits) { (Invoke-Git @('log', '--oneline', $range)) | ForEach-Object { Write-Host "  $_" } }
Write-Host ''
$before = Read-Api $From
$after = Read-Api $To
$added = @($after.Functions.Keys | Where-Object { -not $before.Functions.ContainsKey($_) })
$removed = @($before.Functions.Keys | Where-Object { -not $after.Functions.ContainsKey($_) })
$changed = @($after.Functions.Keys | Where-Object { $before.Functions.ContainsKey($_) -and $before.Functions[$_] -cne $after.Functions[$_] })
$structsAdded = @($after.Structs | Where-Object { -not $before.Structs.Contains($_) })
$structsRemoved = @($before.Structs | Where-Object { -not $after.Structs.Contains($_) })
$sections = @($after.Sections | Where-Object { -not $before.Sections.Contains($_) })
Show-List 'functions added' $added
Show-List 'functions removed' $removed
Show-List 'function declarations changed (review signatures)' $changed
Show-List 'structs added' $structsAdded
Show-List 'structs removed' $structsRemoved
Show-List 'new header sections' $sections

if ($Notes) {
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

if ($added.Count -eq 0 -and $removed.Count -eq 0 -and $changed.Count -eq 0 -and $structsAdded.Count -eq 0 -and $structsRemoved.Count -eq 0 -and $sections.Count -eq 0) {
    Write-Host 'No declaration additions or removals; regenerate to check field and ABI changes.' -ForegroundColor Green
}
