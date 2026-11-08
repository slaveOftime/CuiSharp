<#
.SYNOPSIS
    Regenerates docs/API-SURFACE.txt from the built assembly.

.DESCRIPTION
    PublicSurfaceTests compares the assembly's exported types and members against
    docs/API-SURFACE.txt and fails on any difference. Run this after an intended API
    change, then read the diff before committing it: that diff is the review of the
    public surface.

.EXAMPLE
    pwsh tools/api-surface.ps1
#>
[CmdletBinding()]
param()

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
