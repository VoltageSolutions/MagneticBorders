#Requires -Version 7
<#
.SYNOPSIS
  Single entry point for building, checking and packaging one part of Magnetic Borders.
  The local build watcher and the GitHub workflows both call this script, so a step that
  passes locally passes in CI.

.EXAMPLE
  pwsh tools/ci/run.ps1 -Target cpp -Steps lint,build,test,coverage
.EXAMPLE
  pwsh tools/ci/run.ps1 -Target rust -Steps build,publish -Arch arm64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('cpp', 'rust', 'dotnet', 'settings', 'docs')][string]$Target,
    # Not a ValidateSet: `pwsh run.ps1 -Steps restore,lint` (how the workflows call it) passes one string, "restore,lint".
    [string[]]$Steps = @('restore', 'lint', 'build', 'test'),
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [switch]$Ci
)
$ErrorActionPreference = 'Stop'
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$known = 'restore', 'lint', 'build', 'test', 'coverage', 'publish'
foreach ($s in $Steps) { if ($s -notin $known) { throw "Unknown step '$s'. Use one or more of: $($known -join ', ')." } }
. (Join-Path $PSScriptRoot 'common.ps1')

$ctx = New-CiContext -Target $Target -Arch $Arch -Ci:$Ci
$targetScript = Join-Path $PSScriptRoot "targets/$Target.ps1"
if (-not (Test-Path $targetScript)) { throw "No target script: $targetScript" }
. $targetScript

foreach ($step in $Steps) {
    $fn = "Invoke-$((Get-Culture).TextInfo.ToTitleCase($step))"
    if (-not (Get-Command $fn -ErrorAction SilentlyContinue)) {
        Write-Host "[$Target/$Arch] ${step}: not applicable" -ForegroundColor DarkGray
        continue
    }
    Write-Host "[$Target/$Arch] $step" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $ctx.SourceDir
    try { & $fn $ctx } finally { Pop-Location }
    Write-Host ("[$Target/$Arch] $step done in {0:n1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
}
