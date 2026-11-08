<#
.SYNOPSIS
    Builds CUI (https://github.com/yappologistic/CUI) into the artifacts CuiSharp needs.

.DESCRIPTION
    Produces two files next to this script:

      cui.dll   runtime library, loaded by JIT builds (dotnet run / dotnet watch)
      cui.lib   static archive, linked into NativeAOT single-file builds

    Both are compiled with /MT so the resulting binaries need no VC++
    redistributable.

.PARAMETER CuiSource
    Path to a CUI checkout. Defaults to the CUI submodule next to this script
    (git submodule update --init --recursive).

.PARAMETER Configuration
    Release (default) or Debug.

.NOTES
    The submodule is never modified: it is staged into artifacts/native-src and
    native/patches/*.patch is applied there in order (docs/PLAN.md section 4.3).
#>
[CmdletBinding()]
param(
    [string] $CuiSource = (Join-Path $PSScriptRoot 'CUI'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

if (-not (Test-Path (Join-Path $CuiSource 'CMakeLists.txt'))) {
    throw "No CUI checkout at '$CuiSource'. Clone https://github.com/yappologistic/CUI or pass -CuiSource."
}

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
        "$env:ProgramFiles\CMake\bin\cmake.exe",
        "${env:ProgramFiles(x86)}\CMake\bin\cmake.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw 'cmake not found. Install CMake or put it on PATH.'
}

function Enter-MsvcEnvironment {
    if ($env:VSCMD_ARG_TGT_ARCH) { return }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw 'Visual Studio with the C++ toolset was not found.' }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'Visual Studio with the C++ toolset was not found.' }
    $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    # Re-run in a shell that has the MSVC environment, then exit.
    cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($matches[1])" -Value $matches[2] }
    }
}

$cmake = Find-CMake
Enter-MsvcEnvironment

function Invoke-NativePatches {
    # The pinned submodule stays untouched, so the build runs against a patched
    # copy. tools/bindgen.cs reads the same staged headers.
    $repoRoot = Split-Path -Parent $here
    $stage = Join-Path $repoRoot 'artifacts\native-src'
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $exclude = @(
        (Join-Path $CuiSource 'build-shared'),
        (Join-Path $CuiSource 'build-static'),
        (Join-Path $CuiSource '.git'))
    $null = robocopy $CuiSource $stage /E /NFL /NDL /NJH /NJS /NP /XD @exclude
    if ($LASTEXITCODE -ge 8) { throw "could not stage the CUI checkout (robocopy $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0

    $patchDir = Join-Path $here 'patches'
    if (Test-Path $patchDir) {
        foreach ($patch in Get-ChildItem $patchDir -Filter '*.patch' | Sort-Object Name) {
            Write-Host "==> patch $($patch.Name)" -ForegroundColor Cyan
            Push-Location $stage
            try { & git apply $patch.FullName } finally { Pop-Location }
            if ($LASTEXITCODE) { throw "native patch failed to apply: $($patch.Name)" }
        }
    }
    return $stage
}

$flags = if ($Configuration -eq 'Release') { '/MT /O2 /Ob2 /DNDEBUG' } else { '/MTd /Od /Zi /DDEBUG' }

$CuiSource = Invoke-NativePatches

Push-Location $CuiSource
try {
    # A cached build tree records absolute paths; a moved or copied checkout
    # makes cmake refuse to reuse it, so always start clean.
    Remove-Item -Recurse -Force build-shared, build-static -ErrorAction SilentlyContinue

    Write-Host "==> cui.dll (shared, runtime)" -ForegroundColor Cyan
    & $cmake -B build-shared -G 'NMake Makefiles' `
        "-DCMAKE_BUILD_TYPE=$Configuration" `
        -DCUI_BUILD_TESTS=OFF -DCUI_BUILD_EXAMPLES=OFF `
        -DCUI_BUILD_SHARED=ON -DCUI_WITH_APP=ON `
        "-DCMAKE_C_FLAGS_$($Configuration.ToUpper())=$flags"
    if ($LASTEXITCODE) { throw 'cmake configure (shared) failed' }
    & $cmake --build build-shared
    if ($LASTEXITCODE) { throw 'cmake build (shared) failed' }

    Write-Host "==> cui.lib (static, NativeAOT)" -ForegroundColor Cyan
    & $cmake -B build-static -G 'NMake Makefiles' `
        "-DCMAKE_BUILD_TYPE=$Configuration" `
        -DCUI_BUILD_TESTS=OFF -DCUI_BUILD_EXAMPLES=OFF `
        -DCUI_WITH_APP=ON `
        "-DCMAKE_C_FLAGS_$($Configuration.ToUpper())=$flags"
    if ($LASTEXITCODE) { throw 'cmake configure (static) failed' }
    & $cmake --build build-static
    if ($LASTEXITCODE) { throw 'cmake build (static) failed' }

    Copy-Item (Join-Path $CuiSource "build-shared\cui.dll") $here -Force
    Copy-Item (Join-Path $CuiSource "build-static\cui.lib") $here -Force
}
finally {
    Pop-Location
}

Get-ChildItem $here -Filter 'cui.*' | Where-Object { $_.Extension -in '.dll', '.lib' } |
    ForEach-Object { Write-Host ("    {0,-10} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) -ForegroundColor Green }
Write-Host 'Native artifacts ready.' -ForegroundColor Green
