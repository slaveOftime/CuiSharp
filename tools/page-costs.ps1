<#
.SYNOPSIS
    Walks every gallery page and measures what the app costs while it shows each one.

.DESCRIPTION
    One headless process, every page in turn: the tool clicks the page, lets it settle, parks
    the pointer off the content, and dwells — keeping the app drawing for -Each seconds with
    the silent `pulse` command, so the measurement does not pay for serialising the tree it is
    measuring. Per page it reports the CPU the process burned, the frames it got through, and
    the memory it holds (managed heap, working set, private bytes). The numbers go into
    docs/PERFORMANCE.md, where the frame-time budget also lives.

    The dwell answers what a page costs to LOOK at; the --fps 900 harness still answers what a
    frame costs to BUILD. The two are not the same number: a dwell includes the headless
    round trips around the drawing, so treat its ms/frame as an upper bound of the frame's own.

.EXAMPLE
    pwsh tools/page-costs.ps1

.EXAMPLE
    pwsh tools/page-costs.ps1 -Each 5 -Csv .tmp/page-costs.csv
#>
[CmdletBinding()]
param(
    # Seconds each page stays on screen, drawing.
    [double] $Each = 3,

    # Frames per silent pulse round trip; the dwell sends these until the window is through.
    [int] $Pulse = 240,

    # Where to also write the measurements as CSV. Empty prints the table only.
    [string] $Csv = '',

    # light or dark: what CUI_THEME the app runs with.
    [ValidateSet('light', 'dark')]
    [string] $Theme = 'light'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gallery = Join-Path $root 'samples/CuiSharp.Gallery'
. (Join-Path $PSScriptRoot 'gallery-pages.ps1')

Write-Host '==> building' -ForegroundColor Cyan
foreach ($project in @(
    (Join-Path $gallery 'CuiSharp.Gallery.csproj'),
    (Join-Path $root 'src/CuiSharp.Tools/CuiSharp.Tools.csproj'))) {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "$project does not build" }
}
$cui = Join-Path $root 'src/CuiSharp.Tools/bin/Release/net10.0/CuiSharp.Tools.exe'

# Same arrival ritual as snapshots.ps1: park the nav scroll, wheel down for the pages that
# live below its fold, click, prove the heading says the page opened, park the pointer so the
# dwell is not measuring a hover, then dwell. The STATS answers come back in click order.
$script = [System.Text.StringBuilder]::new()
foreach ($page in $GalleryPages) {
    [void]$script.AppendLine('wheel 120 400 400')
    [void]$script.AppendLine('frame 25')
    if ($page.Scrolled) {
        [void]$script.AppendLine('wheel 120 400 -40')
        [void]$script.AppendLine('frame 25')
    }
    [void]$script.AppendLine("click NavItem `"$($page.Label)`"")
    [void]$script.AppendLine('frame 4')
    [void]$script.AppendLine("find Heading `"$($page.Heading)`"")
    [void]$script.AppendLine('move 5 5')
    [void]$script.AppendLine('frame 2')
    [void]$script.AppendLine("dwell $Each")
}
[void]$script.AppendLine('faults')
[void]$script.AppendLine('quit')

Write-Host "==> dwelling on $($GalleryPages.Count) page(s), ${Each}s each, $Theme" -ForegroundColor Cyan
$env:CUI_THEME = $Theme
try {
    $output = $script.ToString() | & $cui session --no-build --project $gallery
    if ($LASTEXITCODE -ne 0) { throw 'dotnet cui failed' }
}
finally {
    Remove-Item Env:CUI_THEME -ErrorAction SilentlyContinue
}

$stats = @($output | Where-Object { $_ -like 'STATS *' })
if ($stats.Count -ne $GalleryPages.Count) {
    throw "expected $($GalleryPages.Count) STATS lines, got $($stats.Count)"
}

# The script's last two commands are `faults` and `quit`, and quit's answer is empty: the
# lines after the final STATS are the fault list, or nothing. Everything before is the trees
# a click and a find print, which is noise here.
$lastStats = [array]::LastIndexOf($output, $stats[-1])
$tailLines = @($output | Select-Object -Skip ($lastStats + 1)) | Where-Object { $_.Trim().Length -gt 0 }
if ($tailLines.Count -gt 0) {
    throw "the app threw while walking the pages:$([Environment]::NewLine)$($tailLines -join [Environment]::NewLine)"
}

$rows = foreach ($i in 0..$($GalleryPages.Count - 1)) {
    if ($stats[$i] -notmatch
        'STATS wall (\d+) cpu (\d+) frames (\d+) fps ([\d\.]+) heap (\d+) ws (\d+) private (\d+)') {
        throw "unparsable STATS line: $($stats[$i])"
    }

    [pscustomobject]@{
        Number      = $GalleryPages[$i].Number
        Page        = $GalleryPages[$i].Label
        'cpu ms'    = [int]$Matches[2]
        'wall ms'   = [int]$Matches[1]
        'fps'       = [double]$Matches[4]
        'ms/frame'  = [math]::Round(1000 / [double]$Matches[4], 2)
        'heap KB'   = [math]::Round([long]$Matches[5] / 1KB)
        'ws MB'     = [math]::Round([long]$Matches[6] / 1MB, 1)
        'priv MB'   = [math]::Round([long]$Matches[7] / 1MB, 1)
    }
}

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

if ($Csv) {
    if (-not [System.IO.Path]::IsPathRooted($Csv)) { $Csv = Join-Path $root $Csv }
    $rows | Export-Csv -NoTypeInformation -Path $Csv
    Write-Host "wrote $Csv" -ForegroundColor Cyan
}

$cpu = ($rows | Measure-Object -Property 'cpu ms' -Sum).Sum
$fps = ($rows | Measure-Object -Property 'fps' -Average).Average
Write-Host ("total cpu {0} ms over {1} dwells; mean fps {2:0.0}" -f $cpu, $GalleryPages.Count, $fps) -ForegroundColor Cyan
