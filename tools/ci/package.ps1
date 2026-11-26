#Requires -Version 7
<#
.SYNOPSIS
  Packages published builds into one zip per build per architecture, plus SHA256SUMS.txt.

.DESCRIPTION
  Output: <OutDir>/MagneticBorders-<version>-<x64|arm64>.zip (the C++ build, the primary product),
  <OutDir>/MagneticBorders-<version>-<rust|dotnet>-<x64|arm64>.zip (comparison builds) and <OutDir>/SHA256SUMS.txt.
  Each zip holds:
    engine/<engine exe>     the engine (it finds the settings app in ..\settings)
    settings/               the settings app folder (no .pdb files)
    LICENSE
    README.md
  Inputs come from `pwsh tools/ci/run.ps1 -Target <t> -Steps publish -Arch <a>`: <InputDir>/<t>/<a>. A folder named
  publish-<t>-<a> in <InputDir> (as download-artifact creates) is accepted too.

.EXAMPLE
  pwsh tools/ci/package.ps1
.EXAMPLE
  pwsh tools/ci/package.ps1 -Version 0.3.0 -Arch x64 -Engine cpp
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'),
    [ValidateSet('cpp', 'rust', 'dotnet')][string[]]$Engine = @('cpp', 'rust', 'dotnet'),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path,
    [string]$InputDir,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $InputDir) { $InputDir = Join-Path $RepoRoot 'artifacts' }
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts' 'release' }
if (-not $Version) {
    $global:LASTEXITCODE = 0
    $Version = (& (Join-Path $PSScriptRoot 'check-version.ps1') -RepoRoot $RepoRoot -SkipBranchCheck | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or -not $Version) { throw 'The version sources disagree; see check-version.ps1.' }
}

$engineExe = @{ cpp = 'MagneticBorders.Cpp.exe'; rust = 'magnetic-borders-rs.exe'; dotnet = 'MagneticBorders.Net.exe' }

function Find-Published([string]$Target, [string]$Architecture) {
    foreach ($candidate in (Join-Path $InputDir $Target $Architecture), (Join-Path $InputDir "publish-$Target-$Architecture")) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "No published $Target build for $Architecture under $InputDir. Run: pwsh tools/ci/run.ps1 -Target $Target -Steps publish -Arch $Architecture"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stageRoot = Join-Path $OutDir 'stage'
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }

$zips = @()
foreach ($a in $Arch) {
    $settings = Find-Published 'settings' $a
    if (-not (Test-Path -LiteralPath (Join-Path $settings 'MagneticBorders.Settings.exe'))) { throw "MagneticBorders.Settings.exe is missing in $settings" }
    foreach ($e in $Engine) {
        $exe = Join-Path (Find-Published $e $a) $engineExe[$e]
        if (-not (Test-Path -LiteralPath $exe)) { throw "$($engineExe[$e]) is missing in $(Split-Path $exe -Parent)" }

        $name = if ($e -eq 'cpp') { "MagneticBorders-$Version-$a" } else { "MagneticBorders-$Version-$e-$a" }
        $stage = Join-Path $stageRoot $name
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'engine') | Out-Null
        Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'engine')
        $dest = Join-Path $stage 'settings'
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Get-ChildItem -LiteralPath $settings -Force | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $dest -Recurse -Force
        Get-ChildItem -LiteralPath $dest -Recurse -Filter '*.pdb' -File | Remove-Item -Force
        foreach ($doc in 'LICENSE', 'README.md') { Copy-Item -LiteralPath (Join-Path $RepoRoot $doc) -Destination $stage }

        $zip = Join-Path $OutDir "$name.zip"
        if (Test-Path $zip) { Remove-Item -Force $zip }
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        $zips += Get-Item -LiteralPath $zip
        Write-Host ("{0}  {1:n1} MB" -f $zip, ((Get-Item $zip).Length / 1MB))
    }
}
Remove-Item -Recurse -Force $stageRoot

# Sums for every zip in the folder, so a partial run never drops earlier files.
$sums = Get-ChildItem -LiteralPath $OutDir -Filter 'MagneticBorders-*.zip' -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.ASCIIEncoding]::new())
Write-Host ($sums -join "`n")
Write-Host "Packaged $($zips.Count) zip file(s) for version $Version in $OutDir" -ForegroundColor Green
