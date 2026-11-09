<#
.SYNOPSIS
    Imports the MSVC x64 toolchain (cl, link, lib, dumpbin) into the current shell.

.DESCRIPTION
    Dot-source this from scripts and CI steps that invoke the compiler directly:
    a plain PowerShell session has no MSVC environment, and the toolchain is only
    reachable through vcvars64.bat. Does nothing when the environment is already
    imported (VSCMD_ARG_TGT_ARCH is set).

.EXAMPLE
    . ./native/msvc-env.ps1
    cl /nologo /MT probe.c /Fe:probe.exe
#>
if ($env:VSCMD_ARG_TGT_ARCH) { return }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Visual Studio with the C++ toolset was not found.' }
$install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $install) { throw 'Visual Studio with the C++ toolset was not found.' }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($matches[1])" -Value $matches[2] }
}
