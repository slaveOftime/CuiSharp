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
      4. generate cui-mini, run its named .cs file and check its button through the headless protocol
      5. on Windows, observe the file-based app's native window and close it

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
$miniApp = Join-Path $work 'mini'
$templateInstalled = $false
$elapsed = [System.Diagnostics.Stopwatch]::StartNew()

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

try {
    New-Item -ItemType Directory -Path $packages -Force | Out-Null

    # A private package cache, unless the caller supplies one (CI caches a stable folder). Isolation
    # still holds either way: the version below is unique to this run, so no cached CuiSharp can
    # satisfy it and only the third-party packages are reused.
    if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $work 'nuget' }
    $ownPackageCache = $env:NUGET_PACKAGES -eq (Join-Path $work 'nuget')

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
        # PowerShell may be emulated on ARM64; the SDK's RID is what NuGet and our targets use.
        $rid = (& dotnet msbuild src/CuiSharp/CuiSharp.csproj -nologo -getProperty:NETCoreSdkRuntimeIdentifier | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $rid -notmatch '^(win|linux|osx)-(x64|arm64)$') {
            throw "could not determine the .NET SDK's native RID: $rid"
        }
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
using CuiSharp;

namespace TodoApp;

internal static class AnalyzerProbe
{
    public static void Draw(Ui ui)
    {
        ui.Card(new Card());
    }
}
'@
    $analysisOutput = (& dotnet build $solution -c $Configuration $versionArg 2>&1 | Out-String)
    $analyzed = $LASTEXITCODE -ne 0 -and $analysisOutput -match '\bCUI002\b'
    Remove-Item $page -Force
    if (-not $analyzed) { throw "the package did not diagnose the discarded scope with CUI002: $analysisOutput" }
    Write-Host '    CUI002 fired inside the generated app' -ForegroundColor Green

    # `dotnet cui` has to work on the app a person just generated, with no code added: the
    # framework serves the headless protocol, so the tool drives the app itself.
    Step 'driving the generated app with dotnet cui'
    # .exe only on Windows: the apphost on Linux and macOS carries no extension,
    # and a path that does not exist would send the launch after a rebuild that
    # cannot produce it.
    $toolName = if ($IsWindows) { 'CuiSharp.Tools.exe' } else { 'CuiSharp.Tools' }
    $cui = Join-Path $PSScriptRoot ("../src/CuiSharp.Tools/bin/Release/net10.0/$toolName")
    if (-not (Test-Path $cui)) { & dotnet build (Join-Path $PSScriptRoot '../src/CuiSharp.Tools/CuiSharp.Tools.csproj') -c Release | Out-Null }
    # Read the tool's stdout as UTF-8, explicitly: a native command's output is otherwise decoded
    # with this console's code page, and the check below is about the tool's bytes, not about pwsh.
    $psi = [System.Diagnostics.ProcessStartInfo]::new($cui)
    # The successful JIT build above is still present after the intentionally failing probe;
    # do not restore/rebuild it a third time just to read its already-tested frame.
    foreach ($argument in @('tree', '--project', (Join-Path $app 'TodoApp'), '--no-build', '--frames', '2')) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    # strict: a byte that is not UTF-8 raises instead of becoming a replacement character
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
    $tool = [System.Diagnostics.Process]::Start($psi)
    try {
        # Drain stderr concurrently too; a failed restore/launch must not fill its pipe and hang.
        $stdout = $tool.StandardOutput.ReadToEndAsync()
        $stderr = $tool.StandardError.ReadToEndAsync()
        if (-not $tool.WaitForExit(60000)) { throw 'dotnet cui tree timed out after 60 seconds' }
        $tree = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($tool.ExitCode -ne 0) { throw "dotnet cui tree failed: $errors" }
    }
    finally {
        if (-not $tool.HasExited) { $tool.Kill($true); $tool.WaitForExit() }
        $tool.Dispose()
    }
    if ($tree -notmatch 'Frame') { throw "dotnet cui tree printed no tree: $tree" }

    # The footer's label has a middle dot in it, and a tool that wrote its own code page would lose
    # it — that is the first thing an agent reading a frame would hit.
    if ($tree.Contains([char]0x00B7)) { Write-Host '    labels round-trip as UTF-8' -ForegroundColor Green }
    else { Write-Host '    no non-ASCII label in the tree; UTF-8 round trip not checked' }

    $lines = ($tree -split "`n" | Select-Object -Skip 1 -First 4 | ForEach-Object { $_.Trim() })
    Write-Host ('    ' + ($lines -join ' / ')) -ForegroundColor Green

    Step 'generating and running a file-based app'
    $defaultMini = Join-Path $work 'demo1'
    & dotnet new cui-mini -o $defaultMini | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'generating the default mini app failed' }
    $defaultFiles = @(Get-ChildItem $defaultMini -Recurse -File)
    if ($defaultFiles.Count -ne 1 -or $defaultFiles[0].Name -cne 'demo1.cs' -or
        (Get-Content $defaultFiles[0].FullName -First 1) -ne '#:package CuiSharp@*') {
        throw 'dotnet new cui-mini -o demo1 must generate only demo1.cs referencing CuiSharp@*'
    }
    # An explicit -n also names the file and gives the native window a unique title.
    $miniName = 'CuiMini' + [guid]::NewGuid().ToString('N')
    $miniFile = "$miniName.cs"
    & dotnet new cui-mini -n $miniName -o $miniApp --CuiSharpVersion $version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'generating the mini app failed' }
    $generated = @(Get-ChildItem $miniApp -Recurse -File)
    if ($generated.Count -ne 1 -or $generated[0].Name -cne $miniFile) {
        throw "cui-mini must generate exactly one file named $miniFile"
    }
    Copy-Item (Join-Path $app 'NuGet.config') (Join-Path $miniApp 'NuGet.config')

    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $psi.WorkingDirectory = $miniApp
    foreach ($argument in @('run', $miniFile, '--no-launch-profile')) { $psi.ArgumentList.Add($argument) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
    $psi.Environment['CUISHARP_HEADLESS'] = '1'
    $mini = [System.Diagnostics.Process]::Start($psi)
    try {
        # Drain both streams while building/restoring; neither pipe may block the child.
        $stdout = $mini.StandardOutput.ReadToEndAsync()
        $stderr = $mini.StandardError.ReadToEndAsync()
        $mini.StandardInput.WriteLine("tree`nclick Increment`ntree`nquit")
        $mini.StandardInput.Close()
        if (-not $mini.WaitForExit(180000)) { throw "dotnet run $miniFile timed out" }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($mini.ExitCode -ne 0) { throw "dotnet run $miniFile failed: $errors $output" }
        if ($output -notmatch 'CUI-READY 480 320' -or $output -match 'CUI-ERR' -or
            ([regex]::Matches($output, 'CUI-OK ').Count -ne 4) -or
            $output -notmatch 'Clicked 0 times' -or $output -notmatch 'Clicked 1 times') {
            throw "the file-based app did not draw and react to its button: $output"
        }
        Write-Host "    $miniFile drew a frame and Increment changed its state" -ForegroundColor Green
    }
    finally {
        if (-not $mini.HasExited) { $mini.Kill($true); $mini.WaitForExit() }
        $mini.Dispose()
    }

    if ($IsWindows) {
        Step 'observing the file-based app window'
        if (-not ('CuiMiniWindowProbe' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CuiMiniWindowProbe
{
    // IntPtr.Zero is a real NULL class name; PowerShell converts $null to an empty string.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(IntPtr className, string title);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
        }
        $psi.Environment['CUISHARP_HEADLESS'] = '0'
        $psi.ArgumentList.Add('--no-build')
        $mini = [System.Diagnostics.Process]::Start($psi)
        try {
            $stdout = $mini.StandardOutput.ReadToEndAsync()
            $stderr = $mini.StandardError.ReadToEndAsync()
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $window = [IntPtr]::Zero
            while (-not $mini.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                $window = [CuiMiniWindowProbe]::FindWindow([IntPtr]::Zero, $miniName)
                if ($window -ne [IntPtr]::Zero -and [CuiMiniWindowProbe]::IsWindowVisible($window)) { break }
                Start-Sleep -Milliseconds 50
            }
            if ($window -eq [IntPtr]::Zero -or -not [CuiMiniWindowProbe]::IsWindowVisible($window)) {
                if (-not $mini.HasExited) { $mini.Kill($true); $mini.WaitForExit() }
                throw "dotnet run $miniFile did not open a visible native window: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())"
            }
            if (-not [CuiMiniWindowProbe]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'could not send WM_CLOSE to the mini app window'
            }
            if (-not $mini.WaitForExit(30000)) { throw 'the mini app did not exit after its window closed' }
            if ($mini.ExitCode -ne 0) { throw "the mini app window failed: $($stderr.GetAwaiter().GetResult())" }
            Write-Host '    visible native window opened and closed cleanly' -ForegroundColor Green
        }
        finally {
            if (-not $mini.HasExited) { $mini.Kill($true); $mini.WaitForExit() }
            $mini.Dispose()
        }
    }
    else { Write-Host '    native window smoke skipped (no portable desktop assumed); headless interaction passed' }

    Write-Host ('templates verified in {0:N1} seconds' -f $elapsed.Elapsed.TotalSeconds) -ForegroundColor Green
}
finally {
    if ($templateInstalled) { & dotnet new uninstall CuiSharp.Templates | Out-Null }
    if ($ownPackageCache) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
    Pop-Location
    if ($Keep) { Write-Host "kept: $work" } else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
