#Requires -Version 7
<#
.SYNOPSIS
  Assembles the work-in-progress tester build of a pull request from the published CI artifacts.

.DESCRIPTION
  Calls package.ps1 -Folders for the C++, Rust, and C# builds, so each tester build has the same layout as a release
  zip: MagneticBorders.exe, settings/, LICENSE
  It writes <OutDir>/MagneticBorders-<version>-pr<number>-<short sha>[-rust|-dotnet]-<x64|arm64>/ and, on GitHub
  Actions, the step outputs `cpp_x64`, `cpp_arm64`, `rust_x64`, `rust_arm64`, `dotnet_x64`, and `dotnet_arm64` (those
  folder names). Input folders are publish-<cpp|rust|dotnet|settings>-<arch>, as download-artifact creates them.

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
$label = "$version-pr$PullRequest-$($Sha.Substring(0, 7))"

& (Join-Path $PSScriptRoot 'package.ps1') -Version $label -Engine cpp, rust, dotnet -InputDir $InputDir -OutDir $OutDir -Folders
foreach ($engine in 'cpp', 'rust', 'dotnet') {
    foreach ($arch in 'x64', 'arm64') {
        $name = if ($engine -eq 'cpp') { "MagneticBorders-$label-$arch" } else { "MagneticBorders-$label-$engine-$arch" }
        if (-not (Test-Path (Join-Path $OutDir $name 'MagneticBorders.exe'))) { throw "Packaging did not produce $name" }
        if ($env:GITHUB_OUTPUT) { "${engine}_$arch=$name" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
    }
}
