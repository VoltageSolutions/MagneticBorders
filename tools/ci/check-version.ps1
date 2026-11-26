#Requires -Version 7
<#
.SYNOPSIS
  Checks that the four version sources agree, and that a release branch carries its own version.

.DESCRIPTION
  Sources (see CONTRIBUTING.md, Versioning):
    src/engines/cpp/CMakeLists.txt          project(MagneticBorders VERSION x.y.z ...)
    src/engines/rust/Cargo.toml             [workspace.package] version
    src/engines/dotnet/Directory.Build.props <Version>
    src/settings/Directory.Build.props       <Version>
  The workspace crates in src/engines/rust/Cargo.lock must carry the same version, because CI builds with --locked.

  On a branch named release/vX.Y.Z the version must equal X.Y.Z. The branch is -Branch, else GITHUB_HEAD_REF,
  else GITHUB_REF_NAME, else the checked-out branch. A pull request into release/vX.Y.Z (-BaseBranch, else
  GITHUB_BASE_REF) must carry X.Y.Z too, which catches a branch cut from an older release branch.

  On success the version is written to the pipeline and, in GitHub Actions, to GITHUB_OUTPUT as `version`.
  Exit code 1 when a check fails.

.EXAMPLE
  pwsh tools/ci/check-version.ps1
.EXAMPLE
  pwsh tools/ci/check-version.ps1 -Branch release/v0.3.0
#>
[CmdletBinding()]
param(
    [string]$Branch,
    [string]$BaseBranch,
    [switch]$SkipBranchCheck,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)
$ErrorActionPreference = 'Stop'

$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'
$errors = [System.Collections.Generic.List[string]]::new()

function Add-VersionError([string]$Message) {
    $errors.Add($Message)
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=Version check::$Message" }
    else { Write-Host "ERROR: $Message" -ForegroundColor Red }
}

function Read-AllLines([string]$RelativePath) {
    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) { Add-VersionError "$RelativePath not found."; return $null }
    return [IO.File]::ReadAllText($path) -split "`r?`n"
}

function Get-CMakeVersion {
    $rel = 'src/engines/cpp/CMakeLists.txt'
    $lines = Read-AllLines $rel
    if ($null -eq $lines) { return $null }
    foreach ($line in $lines) {
        $m = [regex]::Match($line, '^\s*project\(\s*MagneticBorders\s+VERSION\s+(?<v>[^\s)]+)', 'IgnoreCase')
        if ($m.Success) { return $m.Groups['v'].Value }
    }
    Add-VersionError "$rel has no project(MagneticBorders VERSION x.y.z ...) line."
    return $null
}

function Get-CargoVersion {
    $rel = 'src/engines/rust/Cargo.toml'
    $lines = Read-AllLines $rel
    if ($null -eq $lines) { return $null }
    $section = ''
    foreach ($line in $lines) {
        $s = [regex]::Match($line, '^\s*\[(?<s>[^\[\]]+)\]\s*(#.*)?$')
        if ($s.Success) { $section = $s.Groups['s'].Value.Trim(); continue }
        if ($section -eq 'workspace.package') {
            $m = [regex]::Match($line, '^\s*version\s*=\s*"(?<v>[^"]+)"')
            if ($m.Success) { return $m.Groups['v'].Value }
        }
    }
    Add-VersionError "$rel has no version in [workspace.package]."
    return $null
}

function Get-PropsVersion([string]$rel) {
    $lines = Read-AllLines $rel
    if ($null -eq $lines) { return $null }
    $found = @($lines | ForEach-Object { [regex]::Match($_, '<Version>\s*(?<v>[^<\s]*)\s*</Version>') } | Where-Object Success)
    if ($found.Count -ne 1) { Add-VersionError "$rel must have exactly one <Version> element (found $($found.Count))."; return $null }
    return $found[0].Groups['v'].Value
}

# Workspace crates are the [[package]] entries without a source. Their version must match Cargo.toml.
function Get-CargoLockMismatch([string]$Expected) {
    $rel = 'src/engines/rust/Cargo.lock'
    $lines = Read-AllLines $rel
    if ($null -eq $lines) { return }
    $name = $null; $version = $null; $hasSource = $false
    $check = {
        if ($name -and -not $hasSource -and $version -and $version -ne $Expected) {
            Add-VersionError "$rel has $name $version, expected $Expected. Run 'cargo update --workspace' in src/engines/rust (or tools/ci/set-version.ps1)."
        }
    }
    foreach ($line in $lines) {
        if ($line -match '^\[\[package\]\]') { & $check; $name = $null; $version = $null; $hasSource = $false; continue }
        if ($line -match '^name\s*=\s*"([^"]+)"') { $name = $Matches[1] }
        elseif ($line -match '^version\s*=\s*"([^"]+)"') { $version = $Matches[1] }
        elseif ($line -match '^source\s*=') { $hasSource = $true }
    }
    & $check
}

$sources = [ordered]@{
    'src/engines/cpp/CMakeLists.txt'           = Get-CMakeVersion
    'src/engines/rust/Cargo.toml'              = Get-CargoVersion
    'src/engines/dotnet/Directory.Build.props' = Get-PropsVersion 'src/engines/dotnet/Directory.Build.props'
    'src/settings/Directory.Build.props'       = Get-PropsVersion 'src/settings/Directory.Build.props'
}

Write-Host 'Version sources:'
foreach ($k in $sources.Keys) { Write-Host ("  {0,-45} {1}" -f $k, $(if ($sources[$k]) { $sources[$k] } else { '(missing)' })) }

$values = @($sources.Values | Where-Object { $_ })
foreach ($k in $sources.Keys) {
    $v = $sources[$k]
    if ($v -and $v -notmatch $semver) { Add-VersionError "$k has '$v', which is not a version of the form X.Y.Z." }
}
$distinct = @($values | Select-Object -Unique)
if ($distinct.Count -gt 1) {
    Add-VersionError ('The four version sources disagree: ' + (($sources.Keys | ForEach-Object { "$_ = $($sources[$_])" }) -join '; ') + '. Run tools/ci/set-version.ps1 -Version X.Y.Z.')
}
$version = if ($distinct.Count -ge 1) { $distinct[0] } else { $null }
if ($version -and $distinct.Count -eq 1) { Get-CargoLockMismatch $version }

# Release branch rule.
if (-not $SkipBranchCheck -and $version) {
    if (-not $PSBoundParameters.ContainsKey('Branch')) {
        $Branch = $env:GITHUB_HEAD_REF
        if (-not $Branch -and $env:GITHUB_REF_TYPE -eq 'branch') { $Branch = $env:GITHUB_REF_NAME }
        if (-not $Branch -and -not $env:GITHUB_ACTIONS -and (Get-Command git -ErrorAction SilentlyContinue)) {
            $Branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
        }
    }
    if (-not $PSBoundParameters.ContainsKey('BaseBranch')) { $BaseBranch = $env:GITHUB_BASE_REF }

    foreach ($pair in @(@('branch', $Branch), @('base branch', $BaseBranch))) {
        $label = $pair[0]; $name = $pair[1]
        if (-not $name -or $name -notlike 'release/*') { continue }
        $m = [regex]::Match($name, '^release/v(?<v>.+)$')
        if (-not $m.Success -or $m.Groups['v'].Value -notmatch $semver) {
            Add-VersionError "The $label '$name' is not named release/vX.Y.Z (for example release/v0.3.0)."
        } elseif ($m.Groups['v'].Value -ne $version) {
            Add-VersionError "The $label '$name' is for $($m.Groups['v'].Value), but the sources have $version. Run tools/ci/set-version.ps1 -Version $($m.Groups['v'].Value)."
        } else {
            Write-Host "The $label '$name' matches version $version."
        }
    }
}

if ($errors.Count -gt 0) {
    if ($env:GITHUB_STEP_SUMMARY) {
        "### Version check failed`n" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
        $errors | ForEach-Object { "- $_" } | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
    exit 1
}

Write-Host "All version sources agree: $version" -ForegroundColor Green
if ($env:GITHUB_OUTPUT) { "version=$version" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
if ($env:GITHUB_STEP_SUMMARY) { "Version $version (all four sources agree)." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
Write-Output $version
