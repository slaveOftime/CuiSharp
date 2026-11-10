<#
.SYNOPSIS
    Measures what a NativeAOT CuiSharp app costs, per shape and per profile.

.DESCRIPTION
    The numbers in docs/SIZE.md come from here. Each row is one published single-file exe built
    from tools/size-matrix: the same frame (a column, a heading, a button, a line of text) plus
    one widget family, so the difference between two rows is what that family costs.

    Three widths are measured, because the three levers differ by an order of magnitude and
    folding them together would hide which one did the work:

      plain   the template's publish profile, no switches
      tuned   + the NativeAOT switches that do not change behaviour an app relies on
      mini    tuned, with the headless channel trimmed out

    A "hello world" NativeAOT exe with no CuiSharp reference is measured too: it is the floor
    every .NET app pays, and a CuiSharp app cannot go under it.

.EXAMPLE
    pwsh tools/size-report.ps1                       # min shape, all three widths, plus the floor
    pwsh tools/size-report.ps1 -All                  # every shape, ~6 publishes per width
    pwsh tools/size-report.ps1 -All -Csv .tmp/size.csv

.NOTES
    Needs native/cui.lib for the target RID: run native/build-native.ps1 first. Windows only for
    now (the publish profile links the MSVC archive).
#>
[CmdletBinding()]
param(
    # Which app shapes to measure. MIN is always included.
    [ValidateSet('MIN', 'TABLE', 'CHART', 'FORM', 'RICH', 'ALL')]
    [string[]] $Shape = @('MIN'),

    # Measure every shape instead of the named ones.
    [switch] $All,

    # Skip the Windows publish profile row (-tuned).
    [switch] $PlainOnly,

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

$profiles = [ordered]@{ plain = @{}; tuned = @{ CuiSizeTuned = 'true' }; mini = @{ CuiSizeTuned = 'true'; CuiNoHeadless = 'true' } }
if ($PlainOnly) { $profiles = [ordered]@{ plain = @{} } }

function Publish-Shape([string] $name, [hashtable] $extra) {
    $publish = Join-Path $matrix 'bin/Release/net10.0'
    Remove-Item -Recurse -Force (Join-Path $matrix 'obj'), $publish -ErrorAction SilentlyContinue
    $arguments = @('publish', (Join-Path $matrix 'Shapes.csproj'), '-c', 'Release', '-r', $Rid, '--nologo')
    if ($extra.Count -gt 0) { $arguments += @($extra.GetEnumerator() | ForEach-Object { "-p:$($_.Key)=$($_.Value)" }) }
    $arguments += "-p:Shape=$name"
    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "size-report: shape $name does not build:`n$($output | Select-Object -Last 12)" }
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
Write-Host "==> .NET floor (no CuiSharp)" -ForegroundColor Cyan
$floor = Publish-Floor
$rows.Add([pscustomobject]@{ Shape = 'hello'; Profile = 'dotnet floor'; Bytes = $floor })

foreach ($profile in $profiles.Keys) {
    foreach ($name in $shapes) {
        Write-Host "==> $name / $profile" -ForegroundColor Cyan
        $bytes = Publish-Shape $name $profiles[$profile]
        $rows.Add([pscustomobject]@{ Shape = $name; Profile = $profile; Bytes = $bytes })
    }
}

$min = @{}
$byKey = @{}
foreach ($row in $rows) {
    $byKey["$($row.Shape)/$($row.Profile)"] = $row.Bytes
    if ($row.Shape -eq 'MIN' -and $row.Profile -ne 'dotnet floor') { $min[$row.Profile] = $row.Bytes }
}

$table = $rows | Select-Object Shape, Profile,
    @{ Name = 'KB'; Expression = { [long][math]::Round($_.Bytes / 1KB) } },
    @{ Name = 'vs MIN plain'; Expression = {
            $base = $min['plain']
            if ($_.Shape -eq 'hello' -or $_.Shape -eq 'MIN' -or -not $base) { '' }
            else { '{0:+#,0;-#,0;0} B' -f ($_.Bytes - $base) }
        } },
    @{ Name = 'tuned vs plain'; Expression = {
            $plain = $byKey["$($_.Shape)/plain"]
            if (-not $plain -or $_.Profile -eq 'plain') { '' }
            else { '{0:+#,0;-#,0;0} B' -f ($_.Bytes - $plain) }
        } }

$table | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

# The one comparison that matters per shape: what the tuning bought, and what the floor costs.
Write-Host 'cost of a feature, against MIN on the same width:' -ForegroundColor Cyan
foreach ($profile in $profiles.Keys) {
    $base = $min[$profile]
    if (-not $base) { continue }
    foreach ($row in $rows | Where-Object { $_.Profile -eq $profile -and $_.Shape -ne 'MIN' }) {
        $delta = $row.Bytes - $base
        Write-Host ("  {0,-6} {1,-6} {2,8:N0} B  ({3})" -f $row.Shape, $profile, $delta, ('{0:0.0}% of MIN' -f (100.0 * $delta / $base)))
    }
}
if ($min['plain'] -and $min['mini']) {
    Write-Host ("trimming: MIN plain {0:N0} B -> mini {1:N0} B  ({2:0.0}% smaller)" -f $min['plain'], $min['mini'], (100.0 * ($min['plain'] - $min['mini']) / $min['plain'])) -ForegroundColor Green
}
if ($min['plain']) {
    Write-Host ("floor:    {0:N0} B of MIN plain is the .NET runtime ({1:0}%)" -f $floor, (100.0 * $floor / $min['plain'])) -ForegroundColor Green
}

if ($Csv) {
    if (-not [System.IO.Path]::IsPathRooted($Csv)) { $Csv = Join-Path $root $Csv }
    $rows | Export-Csv -NoTypeInformation -Path $Csv
    Write-Host "wrote $Csv" -ForegroundColor Cyan
}
