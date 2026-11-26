<#
.SYNOPSIS
    Regenerates docs/API-SURFACE.txt from the built assembly.

.DESCRIPTION
    PublicSurfaceTests compares the assembly's exported types and members against
    docs/API-SURFACE.txt and fails on any difference. Run this after an intended API
    change, then review the diff before committing it. -Against summarizes removals by
    owner, so identical constructors on other types cannot hide a removal.

.EXAMPLE
    pwsh tools/api-surface.ps1
#>
[CmdletBinding()]
param([string] $Against)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:CUI_API_SURFACE = 'update'
    & dotnet test src/CuiSharp.Tests/CuiSharp.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicSurfaceTests'
    if ($LASTEXITCODE -ne 0) { throw 'updating the API surface failed' }
}
finally {
    Remove-Item Env:CUI_API_SURFACE -ErrorAction SilentlyContinue
    Pop-Location
}
Write-Host 'docs/API-SURFACE.txt updated' -ForegroundColor Green
if ($Against) {
    $before = @(& git -C $root show "${Against}:docs/API-SURFACE.txt")
    if ($LASTEXITCODE -ne 0) { throw "could not read the API surface at $Against" }

    function Owned-Surface([string[]] $lines) {
        $owner = ''
        foreach ($line in $lines) {
            if (-not $line.Trim() -or $line.StartsWith('#')) { continue }
            if ($line[0] -notin @(' ', "`t")) { $owner = $line; $owner }
            else { "$owner :: $($line.Trim())" }
        }
    }
    $after = Get-Content (Join-Path $root 'docs/API-SURFACE.txt')
    $delta = @(Compare-Object @(Owned-Surface $before) @(Owned-Surface $after) -CaseSensitive)
    $removed = @($delta | Where-Object SideIndicator -eq '<=')
    Write-Host "API against ${Against}: $($removed.Count) removed, $($delta.Count - $removed.Count) added"
    foreach ($entry in $removed) { Write-Host "  - $($entry.InputObject)" }
}
