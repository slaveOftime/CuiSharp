<#
.SYNOPSIS
    Regenerates the binding from the CUI headers and verifies the result.

.DESCRIPTION
    The three steps that must always happen together after a CUI submodule bump:

      1. build the native library (cui.dll / cui.lib) from the submodule
      2. regenerate every *.g.cs file and bindings/api.json from the headers
      3. build and test, which pins the struct layout against libclang

    Pass -NoNative to skip step 1 when the native artifacts are already current,
    -NoDiff to fail if regeneration changed anything (what CI runs), and -NoTest to
    stop after the build when the suite is run elsewhere (it needs the native library).

.EXAMPLE
    pwsh tools/regen.ps1
    pwsh tools/regen.ps1 -NoDiff
#>
[CmdletBinding()]
param(
    [switch] $NoNative,
    [switch] $NoDiff,
    [switch] $NoTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $NoNative) {
        if ($IsWindows) {
            & pwsh -File (Join-Path $root 'native/build-native.ps1')
        } else {
            & bash (Join-Path $root 'native/build-native.sh')
        }
        if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
    }

    # The folder holds only what has no other home: the hand-written guide, the generated reference
    # and the generated API pages. Everything else a reader needs stays where it is written.
    $bundle = Join-Path $root 'skills/cui-dotnet'

    $generated = @(
        'bindings/api.json'
        'bindings/coverage.txt'
        'src/CuiSharp/Interop/Native.g.cs'
        'src/CuiSharp/Interop/Structs.g.cs'
        'src/CuiSharp/Interop/Enums.g.cs'
        'src/CuiSharp/Interop/Constants.g.cs'
        'src/CuiSharp/Icons.g.cs'
        'src/CuiSharp.Tests/Abi.g.cs'
        'src/CuiSharp.Tests/OptionMappings.g.cs'
        'skills/cui-dotnet/llms.txt'
    )

    function Get-GeneratedHash {
        $sb = [System.Text.StringBuilder]::new()
        # Discover UI outputs on each invocation, so adding/removing a generated
        # module also fails -NoDiff, not just changing already-known file bytes.
        $uiFiles = Get-ChildItem (Join-Path $root 'src/CuiSharp/Ui') -Filter '*.g.cs' |
            ForEach-Object { 'src/CuiSharp/Ui/' + $_.Name }
        # Generated documentation too, so a stale reference fails -NoDiff like stale code.
        $apiDocs = Join-Path $bundle 'api'
        $docFiles = if (Test-Path $apiDocs) {
            Get-ChildItem $apiDocs -Filter '*.md' | ForEach-Object { 'skills/cui-dotnet/api/' + $_.Name }
        } else { @() }
        $paths = @($generated) + @($uiFiles) + @($docFiles)
        foreach ($f in ($paths | Sort-Object -Unique)) {
            $path = Join-Path $root $f
            if (Test-Path $path) {
                [void]$sb.Append($f).Append(':').AppendLine(
                    (Get-FileHash -Path $path -Algorithm SHA256).Hash)
            } else {
                [void]$sb.Append($f).AppendLine(':missing')
            }
        }
        return $sb.ToString()
    }

    $before = Get-GeneratedHash

    Write-Host 'bindgen: reading headers'
    & dotnet run (Join-Path $root 'tools/bindgen.cs')
    if ($LASTEXITCODE -ne 0) { throw 'bindgen failed' }

    if ($NoDiff -and (Get-GeneratedHash) -ne $before) {
        throw 'bindgen output changed: the generated files were stale or the generator is not deterministic'
    }
    if ($NoDiff) { Write-Host 'bindgen: no diff' }

    Write-Host 'bindgen: contract checks (isolated IR/config copies)'
    & dotnet run (Join-Path $root 'tools/bindgen.cs') -- --self-test
    if ($LASTEXITCODE -ne 0) { throw 'bindgen contract checks failed' }

    Write-Host 'build + test'
    & dotnet build (Join-Path $root 'CuiSharp.slnx') -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    if ($NoTest) {
        Write-Host 'tests: skipped (-NoTest)'
        return
    }

    & dotnet test (Join-Path $root 'CuiSharp.slnx') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}
finally {
    Pop-Location
}
