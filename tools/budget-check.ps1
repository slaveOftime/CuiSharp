<#
.SYNOPSIS
    Fails when a frame, a shape's binary or the gallery's binary went over its budget.

.DESCRIPTION
    Three kinds of number decide whether a change is a regression, and each ceiling lives in the
    document a person reads rather than in a number that exists only in CI — a budget nobody can
    see is a budget nobody trusts:

      docs/PERFORMANCE.md  the mean frame time of the `--fps 900` harness, and the gallery executable
      docs/SIZE.md         the executable of each app shape in tools/size-matrix

    The shape budgets are checked against the CSV `tools/size-report.ps1 -Csv` writes, so the app
    that is measured and the app that is gated are the same one. A shape in the table with no
    measurement is skipped unless -RequireAll: the per-pull-request run measures the floor only,
    and asking for every shape is a deliberate, slower job.

.EXAMPLE
    pwsh tools/budget-check.ps1 -Benchmark artifacts/benchmark.txt -AotBytes 2862592

.EXAMPLE
    pwsh tools/budget-check.ps1 -AotBytes 2862592     # gallery size only, no benchmark run

.EXAMPLE
    pwsh tools/size-report.ps1 -All -Widths shipped -Csv artifacts/size.csv
    pwsh tools/budget-check.ps1 -SizeCsv artifacts/size.csv -RequireAll

.EXAMPLE
    pwsh tools/budget-check.ps1 -SizeCsv artifacts/size.csv    # whatever shapes were measured
#>
[CmdletBinding()]
param(
    # Captured stdout of `dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900`.
    [string] $Benchmark,

    # Size in bytes of the published gallery executable.
    [long] $AotBytes = 0,

    # The CSV `tools/size-report.ps1 -Csv` wrote: one row per app shape.
    [string] $SizeCsv,

    # Fail when a shape named in the size table was not measured.
    [switch] $RequireAll,

    # Where the frame and gallery budgets are written down.
    [string] $Performance,

    # Where the size-profile budgets are written down.
    [string] $Size
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Performance) { $Performance = Join-Path $root 'docs/PERFORMANCE.md' }
if (-not $Size) { $Size = Join-Path $root 'docs/SIZE.md' }

# A gate that checks nothing reports the same thing as a gate that passed, which is how a job
# with a typo in its flags stays green for a year.
if (-not $Benchmark -and $AotBytes -le 0 -and -not $SizeCsv) {
    throw 'budget: nothing to check. Pass -Benchmark, -AotBytes or -SizeCsv.'
}
if (-not (Test-Path $Performance)) { throw "budget: no $Performance" }
if (-not (Test-Path $Size)) { throw "budget: no $Size" }

$text = Get-Content -Raw -Path $Performance
$failures = @()

function Get-Ceiling([string] $metric) {
    # The table states the ceiling as "budget <number> <unit>" in the row's last column: plain
    # ASCII, so nothing between the doc and this regex can mangle the sign.
    $row = [regex]::Match($text, "(?m)^\| $metric[^|]*\|[^|]*\|[^|]*budget\s+([\d\.]+)\s*(ms|KB|MB|B)")
    if (-not $row.Success) { throw "budget: docs/PERFORMANCE.md has no 'budget <value> <unit>' in the row for '$metric'" }

    $value = [double]$row.Groups[1].Value
    $unit = $row.Groups[2].Value
    $scale = switch ($unit) { 'KB' { 1KB } 'MB' { 1MB } default { 1 } }
    return @{ Value = ($value * $scale); Label = "$value $unit" }
}

if ($Benchmark) {
    if (-not (Test-Path $Benchmark)) { throw "budget: no benchmark output at $Benchmark" }
    $output = Get-Content -Raw -Path $Benchmark

    $measured = [regex]::Match($output, 'build \+ software raster\s+mean\s+([\d\.]+)\s*ms')
    if (-not $measured.Success) { throw "budget: no frame time in $Benchmark (is this the --fps 900 harness?)" }

    $ceiling = Get-Ceiling 'Gallery frame'
    $mean = [double]$measured.Groups[1].Value
    Write-Host ("frame:  {0:0.00} ms measured, {1} allowed" -f $mean, $ceiling.Label)
    if ($mean -gt $ceiling.Value) { $failures += "frame time $mean ms is over the $($ceiling.Label) budget" }

    if ($output -notmatch 'managed allocation per frame:\s*0 B') {
        $failures += 'the harness did not report 0 B per frame'
    }
}

if ($AotBytes -gt 0) {
    $ceiling = Get-Ceiling 'Gallery AOT executable'
    Write-Host ("size:   {0:N0} B measured, {1} allowed" -f $AotBytes, $ceiling.Label)
    if ($AotBytes -gt $ceiling.Value) { $failures += "the gallery executable is $AotBytes B, over the $($ceiling.Label) budget" }
}

if ($SizeCsv) {
    if (-not (Test-Path $SizeCsv)) { throw "budget: no size report at $SizeCsv" }

    # The ceilings are the rows of docs/SIZE.md's own table. Numbers in these docs are written with
    # a thin space or a plain one between groups, so the value is read with its separators and
    # stripped before it becomes a number.
    $sizeText = Get-Content -Raw -Path $Size
    $ceilings = [ordered]@{}
    # Single quotes: a backtick is PowerShell's own escape character, and this pattern needs two.
    $budgetRow = '(?m)^\| `?(?<shape>[A-Za-z][A-Za-z ]*?)`?\s*\|[^|]*?budget\s+(?<value>\d[\d\s\u00A0\u202F]*?)\s*(?<unit>KB|MB|B)\b'
    foreach ($row in [regex]::Matches($sizeText, $budgetRow)) {
        $value = [double]($row.Groups['value'].Value -replace '[\s\u00A0\u202F]', '')
        $unit = $row.Groups['unit'].Value
        $scale = switch ($unit) { 'KB' { 1KB } 'MB' { 1MB } default { 1 } }
        $ceilings[$row.Groups['shape'].Value.Trim()] = @{ Value = $value * $scale; Label = "$($row.Groups['value'].Value.Trim()) $unit" }
    }
    if ($ceilings.Count -eq 0) { throw "budget: $Size has no 'budget <value> <unit>' rows to check the size report against" }

    # One CSV can hold two widths of MIN: the shipped one and what the same app measured before the
    # size profile. The budget is about the product, so a pre-profile row is not a number it can
    # compare — and taking whichever row came last made the verdict depend on row order.
    $measured = @{}
    $skipped = @()
    foreach ($row in Import-Csv -Path $SizeCsv) {
        if ($row.Width -eq 'before') { $skipped += "$($row.Shape)/$($row.Width)"; continue }
        $measured[$row.Shape] = [long]$row.Bytes
    }
    if ($skipped.Count -gt 0) {
        Write-Host ("note:   not the shipped width, so not checked: {0}" -f ($skipped -join ', '))
    }

    foreach ($shape in $ceilings.Keys) {
        $ceiling = $ceilings[$shape]
        if (-not $measured.ContainsKey($shape)) {
            if ($RequireAll) { $failures += "shape $shape has a budget in $Size but was not measured in $SizeCsv" }
            continue
        }

        $bytes = $measured[$shape]
        Write-Host ("shape:  {0,-6} {1,12:N0} B measured, {2} allowed" -f $shape, $bytes, $ceiling.Label)
        if ($bytes -gt $ceiling.Value) {
            $failures += ("shape {0} is {1:N0} B, over the {2} budget by {3:N0} B" -f
                $shape, $bytes, $ceiling.Label, ($bytes - $ceiling.Value))
        }
    }

    # The other direction: a shape that gets measured but never written into the table is a number
    # nobody defends, which is the state this gate exists to leave behind.
    if ($RequireAll) {
        foreach ($shape in $measured.Keys) {
            if (-not $ceilings.Contains($shape)) {
                $failures += "shape $shape was measured but has no budget in $Size"
            }
        }
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL $failure" }
    exit 1
}

Write-Host 'budget: OK'
