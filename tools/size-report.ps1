<#
.SYNOPSIS
    Measures what a NativeAOT CuiSharp app costs, per app shape and per width.

.DESCRIPTION
    The numbers in docs/SIZE.md come from here. Each row is one published single-file exe built
    from tools/size-matrix: the same frame (a column, a heading, a button, a line of text) plus one
    widget family, so the difference between two rows is what that family costs.

    Two widths are measured: what the package ships — its size profile, on by default for a
    self-contained AOT publish — and what the repository did before that profile existed. MIN is
    measured on both, because the profile's worth is the difference between them; every other
    shape is measured on the shipped width, since a widget's cost is what it adds on top.

    A "hello world" NativeAOT exe with no CuiSharp reference is measured too: it is the floor
    every .NET app pays, and a CuiSharp app cannot go under it.

.EXAMPLE
    pwsh tools/size-report.ps1                       # MIN on both widths, plus the floor
    pwsh tools/size-report.ps1 -All                  # every shape
    pwsh tools/size-report.ps1 -All -Csv .tmp/size.csv
    pwsh tools/size-report.ps1 -Widths shipped       # the product only: 1 publish + the floor
    pwsh tools/size-report.ps1 -All -Widths shipped -Csv artifacts/size.csv   # what CI measures

.NOTES
    Needs native/cui.lib for the target RID: run native/build-native.ps1 first.
#>
[CmdletBinding()]
param(
    # Which app shapes to measure. MIN is always included.
    [ValidateSet('MIN', 'TABLE', 'CHART', 'FORM', 'RICH', 'ALL')]
    [string[]] $Shape = @('MIN'),

    # Measure every shape instead of the named ones.
    [switch] $All,

    # Which widths to measure. "shipped" is the product; "before" is what the repository did
    # before the size profile existed, and it is only there to show what the profile is worth.
    [ValidateSet('before', 'shipped')]
    [string[]] $Widths = @('before', 'shipped'),

    # Where to also write the measurements as CSV. Empty prints the table only.
    [string] $Csv = '',

    # Runtime identifier to publish for.
    [string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$matrix = Join-Path $root 'tools/size-matrix'
$native = Join-Path $root 'native'

if (-not $IsWindows -and ($PSVersionTable.PSVersion.Major -ge 6)) {
    throw 'size-report: this script publishes the Windows profile'
}
if ($Rid -notlike 'win-*') { throw "size-report: $Rid is not a Windows RID" }
if (-not (Test-Path (Join-Path $native 'cui.lib'))) {
    throw "size-report: no native/cui.lib. Run pwsh native/build-native.ps1 -Configuration Release first."
}

$shapes = if ($All -or $Shape -contains 'ALL') { @('MIN', 'TABLE', 'CHART', 'FORM', 'RICH') } else { @($Shape | Select-Object -Unique) }
if ($shapes -notcontains 'MIN') { $shapes = @('MIN') + $shapes }

# The width the package ships is the default properties; "before" is the two switches back on.
$beforeWidth = @{ CuiSharpSizeProfile = 'false'; CuiSharpHeadless = 'true' }
$widths = @($Widths)

function Publish-Shape([string] $name, [string] $width) {
    $publish = Join-Path $matrix 'bin/Release/net10.0'
    Remove-Item -Recurse -Force (Join-Path $matrix 'obj'), $publish -ErrorAction SilentlyContinue
    $arguments = @('publish', (Join-Path $matrix 'Shapes.csproj'), '-c', 'Release', '-r', $Rid, '--nologo', "-p:Shape=$name")
    if ($width -eq 'before') { $arguments += @($beforeWidth.GetEnumerator() | ForEach-Object { "-p:$($_.Key)=$($_.Value)" }) }
    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "size-report: shape $name ($width) does not build:`n$($output | Select-Object -Last 12)" }
    $exe = Join-Path $publish "$Rid/publish/Shape$name.exe"
    if (-not (Test-Path $exe)) { throw "size-report: no exe for shape $name at $exe" }
    return (Get-Item $exe).Length
}

# The floor: the same publish profile with no CuiSharp in the picture at all.
function Publish-Floor {
    $dir = Join-Path $root 'artifacts/size-floor'
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>WinExe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained>true</SelfContained>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <OptimizationPreference>Size</OptimizationPreference>
    <StripSymbols>true</StripSymbols>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <DebuggerSupport>false</DebuggerSupport>
    <IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
    <IlcDisableReflection>true</IlcDisableReflection>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $dir 'Floor.csproj') -Encoding utf8
    'System.Console.WriteLine("hi");' | Set-Content -Path (Join-Path $dir 'Program.cs') -Encoding utf8
    $output = & dotnet publish (Join-Path $dir 'Floor.csproj') -c Release -r $Rid --nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "size-report: the floor does not build:`n$($output | Select-Object -Last 12)" }
    return (Get-Item (Join-Path $dir "bin/Release/net10.0/$Rid/publish/Floor.exe")).Length
}

$rows = [System.Collections.Generic.List[object]]::new()

Write-Host '==> .NET floor (no CuiSharp)' -ForegroundColor Cyan
$floor = Publish-Floor
$rows.Add([pscustomobject]@{ Shape = 'hello'; Width = 'dotnet floor'; Bytes = $floor })

foreach ($name in $shapes) {
    # MIN on both widths; every other shape once, on the width the package ships.
    $forShape = if ($name -eq 'MIN') { $widths } else { @('shipped') }
    foreach ($width in $forShape) {
        Write-Host "==> $name / $width" -ForegroundColor Cyan
        $rows.Add([pscustomobject]@{ Shape = $name; Width = $width; Bytes = (Publish-Shape $name $width) })
    }
}

$min = @{}
$byKey = @{}
foreach ($row in $rows) {
    $byKey["$($row.Shape)/$($row.Width)"] = $row.Bytes
    if ($row.Shape -eq 'MIN') { $min[$row.Width] = $row.Bytes }
}

$rows | Select-Object Shape, Width,
    @{ Name = 'KB'; Expression = { [long][math]::Round($_.Bytes / 1KB) } },
    @{ Name = 'vs MIN on its width'; Expression = {
            $base = $min[$_.Width]
            if ($_.Shape -eq 'MIN' -or $_.Shape -eq 'hello' -or -not $base) { '' }
            else { '{0:+#,0;-#,0;0} B' -f ($_.Bytes - $base) }
        } } |
    Format-Table -AutoSize | Out-String -Width 110 | Write-Host

if ($min['before'] -and $min['shipped']) {
    Write-Host ("profile:  MIN {0:N0} B before, {1:N0} B shipped ({2:0.0}% smaller, {3:N0} B)" -f
        $min['before'], $min['shipped'], (100.0 * ($min['before'] - $min['shipped']) / $min['before']),
        ($min['before'] - $min['shipped'])) -ForegroundColor Green
}
if ($min['shipped']) {
    Write-Host ("floor:    {0:N0} B of MIN shipped is the .NET runtime ({1:0}%)" -f $floor, (100.0 * $floor / $min['shipped'])) -ForegroundColor Green
}

if ($Csv) {
    if (-not [System.IO.Path]::IsPathRooted($Csv)) { $Csv = Join-Path $root $Csv }
    $rows | Export-Csv -NoTypeInformation -Path $Csv
    Write-Host "wrote $Csv" -ForegroundColor Cyan
}
