<#
.SYNOPSIS
    Takes the gallery's screenshots, one PNG per page, straight from the app.

.DESCRIPTION
    The images in docs/images/gallery are not drawn by a mock-up: this script runs the gallery
    headless, clicks each page in its navigation and saves the frame the app drew. Regenerate
    them after changing a page, and the README cannot go stale.

    One process draws the whole set, so a run is a couple of seconds.

.EXAMPLE
    pwsh tools/snapshots.ps1

.EXAMPLE
    pwsh tools/snapshots.ps1 -Theme dark -Suffix -dark -Pages overview,charts
#>
[CmdletBinding()]
param(
    # light or dark: what CUI_THEME the app runs with.
    [ValidateSet('light', 'dark')]
    [string] $Theme = 'light',

    # Appended to each file name, e.g. -dark for 01-overview-dark.png.
    [string] $Suffix = '',

    # Frames drawn after a click, so a page that animates its entrance has settled.
    [int] $Settle = 6,

    # Where the PNGs go: relative to the repository root, or absolute.
    [string] $Out = 'docs/images/gallery',

    # Which pages, by key, e.g. -Pages overview,charts. Empty means all of them.
    [string[]] $Pages = @()
)

$ErrorActionPreference = 'Stop'
# PowerShell variable names are case-insensitive, so nothing below may be called `$Pages`
# again - that is the parameter. `pwsh -File` also hands a comma list over as one string.
$wantedPages = @($Pages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$root = Split-Path -Parent $PSScriptRoot
$gallery = Join-Path $root 'samples/CuiSharp.Gallery'
$out = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Force $out | Out-Null

# The page table is shared with the other tools that walk the gallery.
. (Join-Path $PSScriptRoot 'gallery-pages.ps1')
$all = $GalleryPages

$chosen = if ($wantedPages.Count -eq 0) { $all } else {
    foreach ($wanted in $wantedPages) {
        if (-not ($all | Where-Object Key -EQ $wanted)) { throw "no page called $wanted" }
    }

    @($all | Where-Object { $wantedPages -contains $_.Key })
}

Write-Host '==> building' -ForegroundColor Cyan
foreach ($project in @(
    (Join-Path $gallery 'CuiSharp.Gallery.csproj'),
    (Join-Path $root 'src/CuiSharp.Tools/CuiSharp.Tools.csproj'))) {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "$project does not build" }
}
$cui = Join-Path $root 'src/CuiSharp.Tools/bin/Release/net10.0/CuiSharp.Tools.exe'

# One process for the whole set: one startup, then a click and a save per page.
$script = [System.Text.StringBuilder]::new()
$written = [System.Collections.Generic.List[string]]::new()
foreach ($page in $chosen) {
    $path = Join-Path $out ('{0}-{1}{2}.png' -f $page.Number, $page.Key, $Suffix)
    if (Test-Path $path) { Remove-Item $path -Force }
    $written.Add($path)

    # The nav list scrolls, and a click on an item below its fold hits whatever is drawn
    # there. Park the scroll at the top first, and wheel down for the items that live there.
    [void]$script.AppendLine('wheel 120 400 400')
    [void]$script.AppendLine('frame 25')
    if ($page.Scrolled) {
        [void]$script.AppendLine('wheel 120 400 -40')
        [void]$script.AppendLine('frame 25')
    }

    # Click first, then save: the frame a click produces is the page it opened.
    [void]$script.AppendLine("click NavItem `"$($page.Label)`"")
    [void]$script.AppendLine("frame $Settle")
    # Prove the click landed where it was aimed: the shell draws the page's name as the
    # heading, and a find that misses fails the session, not just the picture.
    [void]$script.AppendLine("find Heading `"$($page.Heading)`"")
    # Off the navigation, or the picture is of a hover.
    [void]$script.AppendLine('move 5 5')
    [void]$script.AppendLine('frame 2')
    [void]$script.AppendLine("png $path")
}
[void]$script.AppendLine('quit')

Write-Host "==> drawing $($chosen.Count) page(s), $Theme" -ForegroundColor Cyan
$env:CUI_THEME = $Theme
try {
    $script.ToString() | & $cui --stdin --no-build --project $gallery | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet cui failed' }
}
finally {
    Remove-Item Env:CUI_THEME -ErrorAction SilentlyContinue
}

$missing = @($written | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) { throw "the app wrote no PNG for: $($missing -join ', ')" }

$total = ($written | ForEach-Object { (Get-Item $_).Length } | Measure-Object -Sum).Sum
Write-Host ('    wrote {0} PNG(s), {1:N0} KB' -f $written.Count, ($total / 1KB)) -ForegroundColor Green
