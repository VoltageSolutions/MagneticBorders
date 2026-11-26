#Requires -Version 7
<#
.SYNOPSIS
  Sets the version in all four sources (and the workspace crates in Cargo.lock), then verifies them.

.DESCRIPTION
  Edits only the version text. Line endings and a UTF-8 BOM stay as they are.
    src/engines/cpp/CMakeLists.txt          project(MagneticBorders VERSION x.y.z ...)
    src/engines/rust/Cargo.toml             [workspace.package] version
    src/engines/rust/Cargo.lock             workspace crates (entries without a source)
    src/engines/dotnet/Directory.Build.props <Version>
    src/settings/Directory.Build.props       <Version>

.EXAMPLE
  pwsh tools/ci/set-version.ps1 -Version 0.3.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)

# Reads a file, lets $Edit rewrite its lines (CR stays at the end of a line), and writes it back when changed.
function Update-File([string]$RelativePath, [scriptblock]$Edit) {
    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) { throw "$RelativePath not found." }
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = $utf8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $lines = $text -split "`n"
    $new = & $Edit $lines
    $out = $new -join "`n"
    if ($out -eq $text) { Write-Host "  unchanged  $RelativePath"; return }
    $body = $utf8.GetBytes($out)
    if ($bom) { $body = [byte[]]@(0xEF, 0xBB, 0xBF) + $body }
    [IO.File]::WriteAllBytes($path, $body)
    Write-Host "  updated    $RelativePath"
}

function Update-Once([string[]]$Lines, [string]$What, [scriptblock]$Match, [scriptblock]$Replace) {
    $count = 0
    $result = foreach ($line in $Lines) {
        if ($count -eq 0 -and (& $Match $line)) { $count++; & $Replace $line } else { $line }
    }
    if ($count -ne 1) { throw "Could not find the $What to update." }
    return , @($result)
}

Write-Host "Setting version $Version"

Update-File 'src/engines/cpp/CMakeLists.txt' {
    param($lines)
    Update-Once $lines 'project(MagneticBorders VERSION ...) line in CMakeLists.txt' `
        { param($l) $l -match '^\s*project\(\s*MagneticBorders\s+VERSION\s+' } `
        { param($l) [regex]::Replace($l, '(?i)(project\(\s*MagneticBorders\s+VERSION\s+)[^\s)]+', "`${1}$Version") }
}

Update-File 'src/engines/rust/Cargo.toml' {
    param($lines)
    $section = ''; $done = $false
    $result = foreach ($line in $lines) {
        $s = [regex]::Match($line, '^\s*\[(?<s>[^\[\]]+)\]\s*(#.*)?$')
        if ($s.Success) { $section = $s.Groups['s'].Value.Trim() }
        if (-not $done -and $section -eq 'workspace.package' -and $line -match '^\s*version\s*=\s*"(?<v>[^"]+)"') {
            $done = $true
            $line -replace '^(\s*version\s*=\s*")[^"]+(")', "`${1}$Version`${2}"
        } else { $line }
    }
    if (-not $done) { throw 'Could not find version in [workspace.package] of Cargo.toml.' }
    , @($result)
}

# Cargo.lock: path crates (no `source` line) carry the workspace version; --locked builds fail if it is stale.
Update-File 'src/engines/rust/Cargo.lock' {
    param($lines)
    $starts = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match '^\[\[package\]\]' })
    $result = [string[]]$lines.Clone()
    for ($i = 0; $i -lt $starts.Count; $i++) {
        $from = $starts[$i]
        $to = if ($i + 1 -lt $starts.Count) { $starts[$i + 1] - 1 } else { $lines.Count - 1 }
        $range = $from..$to
        if (@($range | Where-Object { $lines[$_] -match '^source\s*=' }).Count -gt 0) { continue }
        foreach ($n in $range) {
            if ($lines[$n] -match '^version\s*=\s*"[^"]+"') {
                $result[$n] = $lines[$n] -replace '^(version\s*=\s*")[^"]+(")', "`${1}$Version`${2}"
                break
            }
        }
    }
    , $result
}

foreach ($rel in 'src/engines/dotnet/Directory.Build.props', 'src/settings/Directory.Build.props') {
    Update-File $rel {
        param($lines)
        Update-Once $lines "<Version> element in $rel" `
            { param($l) $l -match '<Version>[^<]*</Version>' } `
            { param($l) $l -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" }
    }
}

$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'check-version.ps1') -RepoRoot $RepoRoot -SkipBranchCheck | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Version check failed after the update.' }
Write-Host "Version is now $Version in all sources." -ForegroundColor Green
