<#
.SYNOPSIS
    Imports the MSVC x64 or ARM64 toolchain (cl, link, lib, dumpbin) into the current shell.

.DESCRIPTION
    Dot-source this from scripts and CI steps that invoke the compiler directly:
    a plain PowerShell session has no MSVC environment, and the toolchain is only
    reachable through vcvarsall.bat. Defaults to the host OS architecture and does
    nothing when the requested target architecture is already imported.

.EXAMPLE
    . ./native/msvc-env.ps1
    cl /nologo /MT probe.c /Fe:probe.exe
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
)

if ($env:VSCMD_ARG_TGT_ARCH -eq $Architecture) { return }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Visual Studio with the C++ toolset was not found.' }
$component = if ($Architecture -eq 'arm64') {
    'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
} else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
$install = & $vswhere -latest -products * -requires $component -property installationPath
if (-not $install) { throw "Visual Studio with the $Architecture C++ toolset was not found." }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$toolchain = if ($hostArch -eq $Architecture) { $Architecture } else { "${hostArch}_$Architecture" }
cmd /c "`"$vcvars`" $toolchain >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($matches[1])" -Value $matches[2] }
}
if ($LASTEXITCODE -ne 0 -or $env:VSCMD_ARG_TGT_ARCH -ne $Architecture) {
    throw "could not import the MSVC $toolchain toolchain"
}
