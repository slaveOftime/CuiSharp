<#
.SYNOPSIS
    Builds CUI as a WebAssembly static archive (cui.a) for the browser sample.

.DESCRIPTION
    The browser sample links CUI the same way a NativeAOT desktop build does: a static
    archive handed to the linker, here through <NativeFileReference>. The archive must
    be named cui.a: the WASM SDK derives the P/Invoke module name from the basename,
    while CuiSharp imports "cui". Naming it libcui.a registers "libcui" and causes
    NativeLibrary resolution to fail. This script builds cui.a from the pinned
    submodule, using the Emscripten that ships
    with the .NET WebAssembly workload — never a separately installed emsdk, because
    the archive is linked against the runtime pack's native objects.

    Compiling CUI with a *newer* clang than the one the runtime pack was built with
    works right up to the final `wasm-opt` pass and then fails with
    `Unknown option --enable-bulk-memory-opt`: the newer clang records target features
    in the object's `target_features` section that the older binaryen cannot name. So
    pick the workload band that matches the app's target framework:

        net10.0 app  ->  Microsoft.NET.Runtime.Emscripten.3.1.56.*
        net11.0 app  ->  Microsoft.NET.Runtime.Emscripten.6.0.2.*

    Only the software renderer is compiled. The GL renderer, the window backends
    (`src/platform/*`) and the app layer are left out: a browser host owns its canvas
    and its event loop, so `CuiSurface` — the windowless `cui_ctx` plus renderer
    interface — is the whole surface CUI needs here.

    The submodule is compiled in place; only object files and the archive are written,
    and only under artifacts/. `native/CUI` is never modified.

.PARAMETER TargetFramework
    The framework band whose workload supplies Emscripten. Defaults to net10.0.

.PARAMETER EmscriptenSdkPath
    An explicit Emscripten SDK pack directory (the one containing tools/). Defaults to
    the newest installed pack matching TargetFramework.

.PARAMETER Optimization
    The emcc optimization flag for the archive. Defaults to -Oz; -O0 keeps debug info.

.PARAMETER Output
    Where to write cui.a (the basename is required). Defaults to artifacts/wasm/cui.a.

.EXAMPLE
    pwsh tools/build-cui-wasm.ps1
    pwsh tools/build-cui-wasm.ps1 -TargetFramework net11.0 -Output artifacts/wasm-net11/cui.a
#>
[CmdletBinding()]
param(
    [string] $TargetFramework = 'net10.0',
    [string] $EmscriptenSdkPath = '',
    [string] $Optimization = '-Oz',
    [string] $Output = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cui = Join-Path $repoRoot 'native/CUI'
$isWindowsHost = $IsWindows -or $env:OS -eq 'Windows_NT'
if (-not $isWindowsHost) {
    throw 'The workload-pack resolver in this script currently supports Windows x64. Use the Windows Pages runner.'
}
$exe = '.exe'
$rid = 'win-x64'

if (-not (Test-Path (Join-Path $cui 'CMakeLists.txt'))) {
    throw "No CUI checkout at '$cui'. Run: git submodule update --init --recursive"
}

function Get-DotnetRoot {
    if ($env:DOTNET_ROOT) { return $env:DOTNET_ROOT }
    $command = Get-Command dotnet -ErrorAction Stop
    # <root>/dotnet(.exe); the packs directory sits beside it.
    return Split-Path -Parent $command.Source
}

function Resolve-EmscriptenPack {
    # The band is the TFM's major.minor: the workload a net10.0 app resolves is the
    # 10.0.* one, and its Emscripten is what the runtime pack was compiled with.
    $band = $TargetFramework -replace '^net', ''
    $packs = Join-Path (Get-DotnetRoot) 'packs'
    if (-not (Test-Path $packs)) { throw "No workload packs at '$packs'. Install the wasm-tools workload." }

    $candidates = Get-ChildItem $packs -Directory -Filter "Microsoft.NET.Runtime.Emscripten.*.Sdk.$rid" |
        ForEach-Object {
            Get-ChildItem $_.FullName -Directory |
                Where-Object { $_.Name -like "$band.*" } |
                ForEach-Object { [pscustomobject]@{ Pack = $_.FullName; Version = $_.Name; } }
        } |
        Sort-Object Version

    if (-not $candidates) {
        throw "No Emscripten SDK pack for '$rid' in the $band band under '$packs'. Install the wasm-tools workload for $TargetFramework."
    }
    return $candidates[-1].Pack
}

if (-not $EmscriptenSdkPath) { $EmscriptenSdkPath = Resolve-EmscriptenPack }
if (-not (Test-Path (Join-Path $EmscriptenSdkPath "tools/bin/clang$exe"))) {
    throw "'$EmscriptenSdkPath' is not an Emscripten SDK pack (no tools/bin/clang)."
}

# Emscripten needs its own LLVM, binaryen, node and the prebuilt cache. The workload
# packs them as siblings at the same version, so take the siblings of *this* pack; the
# .NET targets hand emcc the same four variables.
$packName = Split-Path -Leaf (Split-Path -Parent $EmscriptenSdkPath)   # ...Emscripten.3.1.56.Sdk.win-x64
$toolkitRoot = Split-Path -Parent (Split-Path -Parent $EmscriptenSdkPath)
if ($packName -notmatch '^Microsoft\.NET\.Runtime\.Emscripten\.([0-9][0-9.]*)\.Sdk\.') {
    throw "'$packName' is not an Emscripten SDK pack name."
}
$flavour = $Matches[1]
$version = Split-Path -Leaf $EmscriptenSdkPath

function Get-SiblingToolPath([string] $kind, [string] $relative) {
    $pack = Join-Path $toolkitRoot "Microsoft.NET.Runtime.Emscripten.$flavour.$kind.$rid/$version"
    if (-not (Test-Path $pack)) { throw "No Emscripten $kind pack beside '$EmscriptenSdkPath' (looked for '$pack')." }
    return Join-Path $pack $relative
}

$node = Get-SiblingToolPath 'Node' "tools/bin/node$exe"
$python = Get-SiblingToolPath 'Python' 'tools'
$cache = Get-SiblingToolPath 'Cache' 'tools/emscripten/cache'
foreach ($path in $node, $python, $cache) {
    if (-not (Test-Path $path)) { throw "Missing Emscripten component '$path'." }
}

$tools = Join-Path $EmscriptenSdkPath 'tools'
$emccDir = Join-Path $tools 'emscripten'
$env:DOTNET_EMSCRIPTEN_LLVM_ROOT = Join-Path $tools 'bin'
$env:DOTNET_EMSCRIPTEN_BINARYEN_ROOT = $tools
$env:DOTNET_EMSCRIPTEN_NODE_JS = $node
$env:EM_CACHE = $cache
$env:EM_FROZEN_CACHE = '1'
$env:EMSCRIPTEN_ROOT = $emccDir
$env:EMSDK_PYTHON = Join-Path $python "python$exe"
$env:PYTHONPATH = $python
$env:PYTHONUTF8 = '1'
$env:EM_WORKAROUND_PYTHON_BUG_34780 = '1'

$emcc = Join-Path $emccDir "emcc.bat"
$emar = Join-Path $emccDir "emar.bat"
$env:PATH = "$python$([IO.Path]::PathSeparator)$env:PATH"

if (-not $Output) { $Output = Join-Path $repoRoot 'artifacts/wasm/cui.a' }
$Output = [IO.Path]::GetFullPath($Output)
if ([IO.Path]::GetFileName($Output) -cne 'cui.a') {
    throw 'The archive filename must be cui.a so its registered P/Invoke module matches LibraryImport("cui").'
}
$objectDir = Join-Path (Split-Path -Parent $Output) 'obj'

$emccVersion = (Get-Content (Join-Path $emccDir 'emscripten-version.txt') -Raw).Trim().Trim('"')
Write-Host "==> emscripten $flavour ($emccVersion) from $EmscriptenSdkPath" -ForegroundColor Cyan
Write-Host "==> sources   $cui"
Write-Host "==> archive   $Output"

# Everything the windowless context needs. src/platform is the desktop app layer
# (windows, events, native file dialogs) and cui_render_gl.c is the GL renderer;
# a browser host supplies both, and neither links without a GL loader.
$sources = @(
    Get-ChildItem (Join-Path $cui 'src') -Filter '*.c' -File |
        Where-Object { $_.Name -ne 'cui_app.c' } |
        ForEach-Object { $_.FullName }
    (Join-Path $cui 'src/render/cui_render_sw.c')
)

Remove-Item -Recurse -Force $objectDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $objectDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null

$cflags = @(
    $Optimization
    '-std=c99'
    '-DNDEBUG'
    # One function and one datum per section: the browser link dead-strips whole
    # widgets, which is most of why the archive is 1.4 MB rather than 1.6 MB.
    '-ffunction-sections'
    '-fdata-sections'
    '-Wall'
    '-I', (Join-Path $cui 'include')
    '-I', (Join-Path $cui 'src')
)

$objects = & {
    foreach ($source in $sources) {
        $name = [IO.Path]::GetFileNameWithoutExtension($source)
        $object = Join-Path $objectDir "$name.o"
        & $emcc -c @cflags $source -o $object
        if ($LASTEXITCODE) { throw "emcc failed for $name" }
        $object
    }
}

Remove-Item -Force $Output -ErrorAction SilentlyContinue
& $emar rcs $Output @objects
if ($LASTEXITCODE) { throw 'emar failed' }

$size = (Get-Item $Output).Length
# A quick sanity check: the archive must define the functions the bindings call, so a
# truncated or partially archived build fails here rather than in the browser.
$exports = 0
& (Join-Path $tools "bin/llvm-nm$exe") --defined-only --extern-only $Output |
    ForEach-Object { if ($_ -match '\sT\s+_?(cui_\w+)$') { $exports++ } }

Write-Host "==> $([IO.Path]::GetFileName($Output)): $size bytes from $($sources.Count) sources, $exports cui_* functions" -ForegroundColor Green
if ($exports -lt 400) { throw "Only $exports cui_* functions defined; the archive is incomplete." }
