#Requires -Version 7
<#
.SYNOPSIS
  Quick comparison of the three engines: runs tools/bench/bench.ps1 with short defaults and prints the summary.

.DESCRIPTION
  The build watcher calls this for the "compare" target. It expects the published builds (tools/ci/run.ps1 -Steps
  publish, then tools/ci/stage-local.ps1). Parameters override the quick defaults, for example -IdleSeconds 60. For a full run, call tools/bench/bench.ps1 directly.

.EXAMPLE
  pwsh tools/compare.ps1
.EXAMPLE
  pwsh tools/compare.ps1 -Runs 3 -Engines cpp,rust
#>
[CmdletBinding()]
param(
    [int]$Runs = 2,
    [int]$IdleSeconds = 20,
    [int]$Drags = 5,
    [int]$SoakDrags = 100,
    [ValidateSet('cpp', 'rust', 'dotnet')][string[]]$Engines = @('cpp', 'rust', 'dotnet')
)
$ErrorActionPreference = 'Stop'
$bench = Join-Path $PSScriptRoot 'bench/bench.ps1'
& $bench -Runs $Runs -IdleSeconds $IdleSeconds -Drags $Drags -SoakDrags $SoakDrags -Engines $Engines
exit $LASTEXITCODE
