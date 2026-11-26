<#
.SYNOPSIS
    Takes the gallery's screenshots, one PNG per page, straight from the app.

.DESCRIPTION
    The images in docs/images/gallery are not drawn by a mock-up: this script runs the gallery
    headless with the requested page selected and checks its heading before capturing.
    It builds once and stages the PNGs; a failed page leaves existing images untouched.

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

    # Frames drawn before capture, so a page that animates its entrance has settled.
    [ValidateRange(1, 600)]
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
$cui = Join-Path $root 'src/CuiSharp.Tools/bin/Release/net10.0/CuiSharp.Tools.dll'

Write-Host "==> drawing $($chosen.Count) page(s), $Theme" -ForegroundColor Cyan
$stage = Join-Path $out ('.snapshots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    $total = 0L
    foreach ($page in $chosen) {
        $index = [int]$page.Number - 1
        $path = Join-Path $stage ('{0}-{1}{2}.png' -f $page.Number, $page.Key, $Suffix)
        $commands = @("pulse $Settle", "find Heading `"$($page.Heading)`"", "png $path", 'faults', 'quit')
        $output = @($commands | dotnet $cui session --no-build --project $gallery --env "CUI_THEME=$Theme" -- "--page=$index")
        if ($LASTEXITCODE -ne 0) { throw "dotnet cui failed for $($page.Key) ($Theme)" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "no PNG for $($page.Key) ($Theme)" }
        # `faults` and `quit` are silent on success; anything after the PNG response is a fault.
        $pngAnswer = [array]::FindIndex([string[]]$output, [Predicate[string]]{ param($line) $line.Trim() -eq $path })
        if ($pngAnswer -lt 0) { throw "no PNG reply for $($page.Key) ($Theme)" }
        $faults = @($output | Select-Object -Skip ($pngAnswer + 1) | Where-Object { $_.Trim() })
        if ($faults.Count) { throw "$($page.Key) reported faults: $($faults -join '; ')" }
        $total += (Get-Item -LiteralPath $path).Length
    }
    Get-ChildItem -LiteralPath $stage -Filter '*.png' | Move-Item -Destination $out -Force
    Write-Host ('    wrote {0} PNG(s), {1:N0} KB' -f $chosen.Count, ($total / 1KB)) -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
