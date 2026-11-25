# Regenerates every gallery snapshot one page at a time: the batched nav dance is flaky
# (a click can land while the nav is still gliding), a single page per session is not.
. (Join-Path $PSScriptRoot 'gallery-pages.ps1')
$failed = @()
foreach ($theme in @(@('light', ''), @('dark', '-dark'))) {
    $themeName = $theme[0]; $suffix = $theme[1]
    foreach ($page in $GalleryPages) {
        $ok = $false
        foreach ($attempt in 1..2) {
            pwsh -NoProfile -File (Join-Path $PSScriptRoot 'snapshots.ps1') -Pages $page.Key -Theme $themeName -Suffix $suffix *> $null
            if ($LASTEXITCODE -eq 0) { $ok = $true; break }
        }
        if (-not $ok) { $failed += "$($page.Key)$suffix" }
        Write-Host "$($page.Number) $($page.Key)$suffix -> $(if ($ok) { 'ok' } else { 'FAILED' })"
    }
}
if ($failed.Count -gt 0) { Write-Host "FAILED: $($failed -join ', ')"; exit 1 }
Write-Host 'all snapshots regenerated'
