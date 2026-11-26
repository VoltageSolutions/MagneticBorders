#Requires -Version 7
<#
.SYNOPSIS
  Arranges the published builds for local testing, in the same layout as the release zips:
  artifacts/local/<arch>/{cpp,rust,dotnet}/ next to artifacts/local/<arch>/settings/.
  Each build finds the settings app in ..\settings\, exactly as it does after unzipping a release.

.EXAMPLE
  pwsh tools/ci/stage-local.ps1            # x64
.EXAMPLE
  pwsh tools/ci/stage-local.ps1 -Arch arm64
#>
param([ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts'
$dest = Join-Path $artifacts "local\$Arch"
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
foreach ($part in 'cpp', 'rust', 'dotnet', 'settings') {
    $src = Join-Path $artifacts "$part\$Arch"
    if (-not (Test-Path $src)) { Write-Warning "$part has no $Arch publish yet (run tools/ci/run.ps1 -Target $part -Steps publish -Arch $Arch)"; continue }
    Copy-Item -Recurse $src (Join-Path $dest $part)
}
Write-Host "Ready: $dest" -ForegroundColor Green
Write-Host "  C++:  $dest\cpp\MagneticBorders.Cpp.exe"
Write-Host "  Rust: $dest\rust\magnetic-borders-rs.exe"
Write-Host "  C#:   $dest\dotnet\MagneticBorders.Net.exe"
