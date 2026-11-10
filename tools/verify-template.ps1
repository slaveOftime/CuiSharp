<#
.SYNOPSIS
    Packs CuiSharp and the templates, generates an app from the package, and builds it.

.DESCRIPTION
    The template ships files that reference the published package, so the only honest
    check is the one a user performs: install the template, generate a solution, build
    it, test it, publish it. Nothing in the repository's own build does that, which is
    how a template can pin a version that predates the API it demonstrates.

    Steps:
      1. pack CuiSharp and CuiSharp.Templates into a temporary folder
      2. install the template, `dotnet new cui-app`, and point the app at that folder
      3. build, test, and (unless -NoPublish) publish a NativeAOT single file

    -Keep leaves the generated app and packages in place for inspection.

.EXAMPLE
    pwsh tools/verify-template.ps1
    pwsh tools/verify-template.ps1 -NoPublish -Keep
#>
[CmdletBinding()]
param(
    [switch] $NoPublish,
    [switch] $Keep,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("cui-template-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$packages = Join-Path $work 'packages'
$app = Join-Path $work 'TodoApp'
$templateInstalled = $false

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

try {
    New-Item -ItemType Directory -Path $packages -Force | Out-Null

    # A private package cache, so a released CuiSharp of the same version number
    # cannot be used instead of the one just built.
    $env:NUGET_PACKAGES = Join-Path $work 'nuget'

    # A version unique to this run: nuget.org already has the repository's version,
    # and the app must resolve the package built here or the check proves nothing.
    $version = "$(& { $v = (Select-String -Path Directory.Build.props -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value; $v })-verify$((Get-Random -Maximum 99999))"

    Step "packing CuiSharp $version and the templates"
    & dotnet pack src/CuiSharp/CuiSharp.csproj -c $Configuration -o $packages -p:Version=$version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'packing CuiSharp failed' }
    & dotnet pack src/CuiSharp.Templates/CuiSharp.Templates.csproj -c $Configuration -o $packages -p:Version=$version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'packing the templates failed' }

    $templatePackage = Get-ChildItem (Join-Path $packages 'CuiSharp.Templates.*.nupkg') | Select-Object -First 1
    if (-not $templatePackage) { throw 'the template package is missing from the pack output' }

    Step "generating an app from $($templatePackage.Name)"
    & dotnet new install $templatePackage.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'installing the template failed' }
    $templateInstalled = $true

    # Scaffolded at this build's version rather than the released default the template pins: every
    # later command, including `dotnet cui`, then restores the package this run just packed. That the
    # default tracks the release is TemplateVersionTests' job, not this gate's.
    & dotnet new cui-app -n TodoApp -o $app --CuiSharpVersion $version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'generating the app failed' }

    # A generated app has no NuGet.config; give it one that can see the local packages.
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $app 'NuGet.config')

    $solution = Join-Path $app 'TodoApp.slnx'
    $versionArg = "-p:CuiSharpVersion=$version"

    Step "building (CuiSharp $version)"
    & dotnet build $solution -c $Configuration -warnaserror $versionArg
    if ($LASTEXITCODE -ne 0) { throw 'the generated app does not build' }

    Step 'testing'
    & dotnet test $solution -c $Configuration --no-build $versionArg
    if ($LASTEXITCODE -ne 0) { throw 'the generated app has failing tests' }

    if (-not $NoPublish) {
        # The host's own RID, so this gate says something on Linux and macOS too: the
        # package's archive for that RID is what links, and a Windows-only run cannot
        # see an x11 or a Cocoa linker argument going wrong.
        $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
        $exeName = if ($IsWindows) { 'TodoApp.exe' } else { 'TodoApp' }

        Step "publishing a NativeAOT single file ($rid)"
        & dotnet publish (Join-Path $app 'TodoApp/TodoApp.csproj') -c $Configuration -r $rid $versionArg
        if ($LASTEXITCODE -ne 0) { throw 'the generated app does not publish' }

        $published = Join-Path $app "TodoApp/bin/Release/net10.0/$rid/publish"
        $exe = Join-Path $published $exeName
        if (-not (Test-Path $exe)) { throw "expected $exe" }
        $size = (Get-Item $exe).Length
        Write-Host ("    publish size: {0:N0} B" -f $size)

        # "One file" is the promise, so the shared library that shipped in the package
        # must not be sitting next to the executable that linked it in.
        foreach ($stray in 'cui.dll', 'cui.lib', 'libcui.so', 'libcui.dylib', 'libcui.a') {
            if (Test-Path (Join-Path $published $stray)) {
                throw "the AOT publish left $stray beside the executable"
            }
        }
        Write-Host '    no native library left beside the executable' -ForegroundColor Green
    }

    # The analyzers ship inside the package, and a package's analyzers are what an app
    # actually gets. Prove it: one discarded scope has to fail the generated app's build.
    Step 'checking that the package delivers the analyzers'
    $page = Join-Path $app 'TodoApp/TodoPageBridge.cs'
    Set-Content -Path $page -Encoding utf8 -Value @'
namespace TodoApp;

internal static class AnalyzerProbe
{
    public static void Draw(CuiSharp.Ui ui) => ui.Card(new CuiSharp.Card());
}
'@
    & dotnet build $solution -c $Configuration $versionArg 2>&1 | Out-Null
    $analyzed = $LASTEXITCODE -ne 0
    Remove-Item $page -Force
    if (-not $analyzed) { throw 'the generated app built a discarded scope, so the package is not delivering CUI002' }
    Write-Host '    CUI002 fired inside the generated app' -ForegroundColor Green

    # `dotnet cui` has to work on the app a person just generated, with no code added: the
    # framework serves the headless protocol, so the tool drives the app itself.
    Step 'driving the generated app with dotnet cui'
    $cui = Join-Path $PSScriptRoot '../src/CuiSharp.Tools/bin/Release/net10.0/CuiSharp.Tools.exe'
    if (-not (Test-Path $cui)) { & dotnet build (Join-Path $PSScriptRoot '../src/CuiSharp.Tools/CuiSharp.Tools.csproj') -c Release | Out-Null }
    # Read the tool's stdout as UTF-8, explicitly: a native command's output is otherwise decoded
    # with this console's code page, and the check below is about the tool's bytes, not about pwsh.
    $psi = [System.Diagnostics.ProcessStartInfo]::new($cui)
    foreach ($argument in @('tree', '--project', (Join-Path $app 'TodoApp'), '--frames', '2')) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    # strict: a byte that is not UTF-8 raises instead of becoming a replacement character
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
    $tool = [System.Diagnostics.Process]::Start($psi)
    try { $tree = $tool.StandardOutput.ReadToEnd() }
    catch { throw "dotnet cui did not write UTF-8 to a redirected stdout: $($_.Exception.Message)" }
    $tool.WaitForExit()
    if ($tool.ExitCode -ne 0) { throw "dotnet cui tree failed: $($tool.StandardError.ReadToEnd())" }
    if ($tree -notmatch 'Frame') { throw "dotnet cui tree printed no tree: $tree" }

    # The footer's label has a middle dot in it, and a tool that wrote its own code page would lose
    # it — that is the first thing an agent reading a frame would hit.
    if ($tree.Contains([char]0x00B7)) { Write-Host '    labels round-trip as UTF-8' -ForegroundColor Green }
    else { Write-Host '    no non-ASCII label in the tree; UTF-8 round trip not checked' }

    $lines = ($tree -split "`n" | Select-Object -Skip 1 -First 4 | ForEach-Object { $_.Trim() })
    Write-Host ('    ' + ($lines -join ' / ')) -ForegroundColor Green

    Write-Host 'template verified' -ForegroundColor Green
}
finally {
    if ($templateInstalled) { & dotnet new uninstall CuiSharp.Templates | Out-Null }
    Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    Pop-Location
    if ($Keep) { Write-Host "kept: $work" } else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
