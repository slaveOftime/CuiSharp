<#
.SYNOPSIS
    Fails when a frame or a binary went over the budget written in docs/PERFORMANCE.md.

.DESCRIPTION
    Two numbers decide whether a change is a performance regression: the mean frame time of the
    `--fps 900` harness and the size of the gallery's NativeAOT executable. Both ceilings live in the
    table in docs/PERFORMANCE.md, so this reads the budget from the same row a person reads rather
    than from a number that exists only in CI — a budget nobody can see is a budget nobody trusts.

.EXAMPLE
    pwsh tools/budget-check.ps1 -Benchmark artifacts/benchmark.txt -AotBytes 2134016

.EXAMPLE
    pwsh tools/budget-check.ps1 -AotBytes 2134016     # size only, no benchmark run
#>
[CmdletBinding()]
param(
    # Captured stdout of `dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900`.
    [string] $Benchmark,

    # Size in bytes of the published gallery executable.
    [long] $AotBytes = 0,

    # Where the budgets are written down.
    [string] $Performance
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Performance) { $Performance = Join-Path $root 'docs/PERFORMANCE.md' }
if (-not (Test-Path $Performance)) { throw "budget: no $Performance" }

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

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL $failure" }
    exit 1
}

Write-Host 'budget: OK'
