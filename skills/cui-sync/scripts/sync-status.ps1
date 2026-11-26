<#
.SYNOPSIS
    Prints where a CUI sync stands, and fails when a number in the docs has drifted.

.DESCRIPTION
    A sync used to start with six greps and two file reads to answer "what am I looking at": which
    revision is pinned, whether upstream has moved, which version the two files agree on, and whether
    the counts quoted in the READMEs still match coverage.txt. This answers all of it in one call, so
    an agent spends its context on the C rather than on arithmetic.

    Exit code 0 means the sync is in a state worth continuing from: no PENDING entry, and every number
    the docs quote matches coverage.txt. Exit code 1 means something printed under "stale" is wrong.

.EXAMPLE
    pwsh skills/cui-sync/scripts/sync-status.ps1

.EXAMPLE
    pwsh skills/cui-sync/scripts/sync-status.ps1 -Submodule ../CUI   # another checkout
#>
[CmdletBinding()]
param(
    # The CUI checkout to read; defaults to the one this repository pins.
    [string] $Submodule
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $Submodule) { $Submodule = Join-Path $root 'native/CUI' }

$problems = [System.Collections.Generic.List[string]]::new()

function Section([string] $Title) { Write-Host "`n$Title" -ForegroundColor Cyan }
function Note([string] $Text) { Write-Host "  $Text" }
function Problem([string] $Text) {
    Write-Host "  $Text" -ForegroundColor Red
    $problems.Add($Text)
}

# Not named `Git`: PowerShell resolves the `git` inside it to the function itself, and recurse.
function Invoke-Git([string[]] $Arguments) {
    $output = @(& git -C $Submodule @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { return @() }
    return $output
}

# --- the submodule -----------------------------------------------------------------------------

Section 'submodule'
$pinned = @(Invoke-Git @('describe', '--tags', '--always')) -join ''
$head = @(Invoke-Git @('rev-parse', '--short', 'HEAD')) -join ''
if (-not $pinned) {
    Problem "no CUI checkout at $Submodule (git submodule update --init --recursive)"
} else {
    Note "pinned      $pinned ($head)"
    $tags = @(Invoke-Git @('tag', '--sort=-v:refname')) | Where-Object { $_ -match '^v\d' }
    if ($tags.Count -gt 0) {
        $newest = $tags[0].Trim()
        $behind = @(Invoke-Git @('rev-list', '--count', "HEAD..$newest")) -join ''
        Note "newest      $newest ($behind commits ahead)"
        if ($pinned -ne $newest -and $behind -ne '0') { Note 'status      a bump is available: steps 1-3 of SKILL.md' }
        else { Note 'status      up to date with the newest tag' }
    }
}

# --- versions ----------------------------------------------------------------------------------

Section 'version'
$props = Join-Path $root 'Directory.Build.props'
$version = ([regex]::Match((Get-Content $props -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
$template = Join-Path $root 'src/CuiSharp.Templates/CuiSharp.App/.template.config/template.json'
$templateVersion = ([regex]::Match((Get-Content $template -Raw), '"defaultValue":\s*"([0-9][^"]*)"')).Groups[1].Value
$changelog = Join-Path $root 'CHANGELOG.md'
$logged = ([regex]::Match((Get-Content $changelog -Raw), '(?m)^## \[?([0-9][0-9.]*)')).Groups[1].Value
$app = Get-Content (Join-Path $root 'samples/CuiSharp.Gallery/GalleryApp.cs') -Raw
$badgeVersion = ([regex]::Match($app, 'ReleaseVersion = "([^"]+)"')).Groups[1].Value
$catalog = Get-Content (Join-Path $root 'skills/cui-dotnet/api/catalog.json') -Raw | ConvertFrom-Json -AsHashtable
Note "Directory.Build.props  $version"
if (-not $version) { Problem 'Directory.Build.props has no Version' }
foreach ($copy in @(
    @{ File = 'template.json'; Value = $templateVersion },
    @{ File = 'CHANGELOG.md newest'; Value = $logged },
    @{ File = 'GalleryApp.cs badge'; Value = $badgeVersion },
    @{ File = 'api/catalog.json'; Value = $catalog.version })) {
    Note "$($copy.File): $($copy.Value)"
    if ($copy.Value -cne $version) { Problem "$($copy.File) disagrees with build version $version" }
}

# --- coverage ----------------------------------------------------------------------------------

Section 'coverage'
$coveragePath = Join-Path $root 'bindings/coverage.txt'
$coverage = Get-Content $coveragePath
$functions = ([regex]::Match((@($coverage) -match 'raw functions covered including scope ends')[-1], '\d+/(\d+)')).Groups[1].Value
$options = ([regex]::Match((@($coverage) -match '^Generated: ') -join '', 'Generated: (\d+)')).Groups[1].Value
$optionsPending = ([regex]::Match((@($coverage) -match '^Generated: ') -join '', 'semantics: (\d+)')).Groups[1].Value
$records = ([regex]::Match((@($coverage) -match 'Data records generated as array items'), 'items: (\d+)')).Groups[1].Value
$wrappers = ([regex]::Match((@($coverage) -match '^Generated wrappers: '), 'wrappers: (\d+)')).Groups[1].Value

# The report's record list can grow; split on its section marker, never a fixed line number.
$start = [array]::IndexOf($coverage, 'WIDGETS')
if ($start -lt 0) { throw 'coverage.txt has no WIDGETS section; run tools/regen.ps1' }
$widgets = @($coverage | Select-Object -Skip ($start + 1))
function Count-Status([string] $Status) {
    @($widgets | Where-Object { $_.StartsWith("$Status ", [StringComparison]::Ordinal) }).Count
}

$generated = Count-Status 'GENERATED'
$manual = Count-Status 'MANUAL'
$excluded = Count-Status 'EXCLUDED'
$skipped = @($widgets | Where-Object { $_ -cmatch '^RAW-SKIP function \w+: variadic$' }).Count
$pending = Count-Status 'PENDING'

Note "functions   $functions non-variadic (generated $generated, manual $manual, excluded $excluded, pending $pending); $skipped variadic skipped"
Note "options     $options ($optionsPending pending)"
Note "records     $records"
Note "wrappers    $wrappers generated"
if ($pending -ne 0) { Problem "$pending PENDING entries: a sync is unfinished, see references/bindings.md" }
if ($optionsPending -ne 0) { Problem "$optionsPending option structs are pending: see references/bindings.md" }

# Scopes can wrap two C functions, but each function still has exactly one entry.
$total = $generated + $manual + $excluded + $pending
if (-not $functions -or $total -ne [int]$functions) {
    Problem "coverage.txt has $total function statuses, expected $functions (excluding RAW-SKIP)"
}

# --- the numbers the docs quote ----------------------------------------------------------------

# The prose repeats the same list in two places. Rather than trust a human to keep it, read the
# numbers out of the sentence and compare them with coverage.txt: a stale sync is then a failed
# command, not a claim someone notices a release later.
$checks = @(
    @{ File = 'README.md'; Pattern = '(\d+)(?: non-variadic)? CUI (?:[\d.]+ )?functions'; Value = $functions; What = 'C functions' },
    @{ File = 'README.md'; Pattern = '(\d+) option'; Value = $options; What = 'options' },
    @{ File = 'README.md'; Pattern = '(\d+) records'; Value = $records; What = 'records' },
    @{ File = 'README.md'; Pattern = '(\d+) generated wrappers'; Value = $wrappers; What = 'generated wrappers' },
    @{ File = 'README.md'; Pattern = '(\d+)\s+deliberate\s+hand-written'; Value = $manual; What = 'manual wrappers' },
    @{ File = 'README.md'; Pattern = '(\d+)\s+excluded'; Value = $excluded; What = 'excluded' },
    @{ File = 'README.md'; Pattern = '(\d+)\s+variadic skipped'; Value = $skipped; What = 'variadic skipped' },
    @{ File = 'README.md'; Pattern = '(\d+) pending'; Value = $pending; What = 'pending' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\* option structs'; Value = $options; What = 'options' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\* data records'; Value = $records; What = 'records' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\* generated wrappers'; Value = $wrappers; What = 'generated wrappers' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\*\s*deliberate'; Value = $manual; What = 'manual wrappers' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\* excluded'; Value = $excluded; What = 'excluded' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\*\s*variadic'; Value = $skipped; What = 'variadic skipped' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\*\*\s*pending'; Value = $pending; What = 'pending' },
    @{ File = 'bindings/README.md'; Pattern = '(\d+)\s+(?:non-variadic\s+)?C\s+functions'; Value = $functions; What = 'C functions' }
)

Section 'numbers in the docs'
foreach ($check in $checks) {
    $path = Join-Path $root $check.File
    $text = Get-Content $path -Raw
    $found = [regex]::Match($text, $check.Pattern)
    if (-not $found.Success) { Problem "$($check.File): no '$($check.What)' sentence to compare" ; continue }
    $quoted = $found.Groups[1].Value
    if ($quoted -ne $check.Value) {
        Problem "$($check.File) says $quoted $($check.What), coverage.txt says $($check.Value)"
    } else {
        Note "$($check.File): $($check.What) $quoted ok"
    }
}

# The gallery's own count: `PageCount` is what the app draws, `PageNames` is what the shell shows, and
# the two disagreeing means a page is unreachable rather than missing.
$pageCount = ([regex]::Match($app, 'PageCount = (\d+)')).Groups[1].Value
$names = [regex]::Match($app, 'PageNames\s*=\s*\{(?<body>.*?)\}', 'Singleline').Groups['body'].Value
$named = [regex]::Matches($names, '"[^"]+"').Count
Note "gallery     PageCount $pageCount, $named names"
if ($pageCount -ne "$named") { Problem "GalleryApp says $pageCount pages but PageNames lists $named" }

# --- verdict -----------------------------------------------------------------------------------

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Host "$($problems.Count) thing(s) to fix before or after the sync." -ForegroundColor Yellow
    exit 1
}

Write-Host 'consistent: no PENDING, and every quoted number matches coverage.txt.' -ForegroundColor Green
