#Requires -Version 7
<#
.SYNOPSIS
  Assembles the work-in-progress tester build of a pull request from the published CI artifacts.

.DESCRIPTION
  Reads publish-cpp-<arch> and publish-settings-<arch> folders (as download-artifact creates them) from InputDir and
  writes, for x64 and ARM64, <OutDir>/<arch>/ with:
    engine/MagneticBorders.Cpp.exe   the app (it finds the settings app in ..\settings)
    settings/                        the settings app (no .pdb files)
    LICENSE, README.md
  The name MagneticBorders-<version>-pr<number>-<short sha> goes to the step output `name` on GitHub Actions.

.EXAMPLE
  pwsh tools/ci/tester-build.ps1 -InputDir artifacts -OutDir tester -PullRequest 2 -Sha 0123456789abcdef
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDir,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][int]$PullRequest,
    [Parameter(Mandatory)][string]$Sha
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

$global:LASTEXITCODE = 0
$version = (& (Join-Path $PSScriptRoot 'check-version.ps1') -RepoRoot $root -SkipBranchCheck | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'The version sources disagree; see check-version.ps1.' }
$name = "MagneticBorders-$version-pr$PullRequest-$($Sha.Substring(0, 7))"

foreach ($arch in 'x64', 'arm64') {
    $engine = Join-Path $InputDir "publish-cpp-$arch"
    $settings = Join-Path $InputDir "publish-settings-$arch"
    foreach ($d in $engine, $settings) { if (-not (Test-Path $d)) { throw "Missing $d" } }
    $stage = Join-Path $OutDir $arch
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'engine'), (Join-Path $stage 'settings') | Out-Null
    Copy-Item (Join-Path $engine 'MagneticBorders.Cpp.exe') (Join-Path $stage 'engine')
    Copy-Item (Join-Path $settings '*') (Join-Path $stage 'settings') -Recurse
    Get-ChildItem (Join-Path $stage 'settings') -Recurse -Filter '*.pdb' | Remove-Item -Force
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $stage
    Write-Host "Assembled $name-$arch in $stage"
}
if ($env:GITHUB_OUTPUT) { "name=$name" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
