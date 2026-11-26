#Requires -Version 7
<#
.SYNOPSIS
  Checks dependencies for known vulnerabilities and fails on serious ones.

.DESCRIPTION
  -Kind nuget restores the C# engine and the settings app through run.ps1, then lists vulnerable NuGet packages
  (direct and transitive). High and critical findings fail; others are warnings.
  -Kind rust runs cargo-audit against src/engines/rust/Cargo.lock (RustSec advisory database). A vulnerability fails; unmaintained or yanked crates are listed but do not fail.
  Findings go to the GitHub job summary when one exists.

.EXAMPLE
  pwsh tools/ci/check-vulnerabilities.ps1 -Kind nuget
#>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('nuget', 'rust')][string]$Kind)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

function Write-Summary([string]$Text) {
    if ($env:GITHUB_STEP_SUMMARY) { $Text | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
    Write-Host $Text
}

if ($Kind -eq 'rust') {
    Push-Location (Join-Path $root 'src/engines/rust')
    try {
        $out = cargo audit 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    Write-Summary "### Rust crates (cargo-audit)`n"
    Write-Summary ('```' + "`n" + $out.Trim() + "`n" + '```')
    if ($code -ne 0) { Write-Host '::error title=Vulnerable crate::cargo-audit reported advisories; see the job summary.'; exit 1 }
    exit 0
}

$solutions = @(
    @{ Target = 'dotnet'; Path = 'src/engines/dotnet/MagneticBorders.slnx' },
    @{ Target = 'settings'; Path = 'src/settings/MagneticBorders.Settings.slnx' }
)
$rows = [Collections.Generic.List[object]]::new()
foreach ($sln in $solutions) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run.ps1') -Target $sln.Target -Steps restore -Arch x64 -Ci
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $($sln.Target)." }
    $json = dotnet list (Join-Path $root $sln.Path) package --vulnerable --include-transitive --format json --output-version 1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $json; throw "dotnet list package failed for $($sln.Path)." }
    $start = $json.IndexOf('{')
    $data = if ($start -ge 0) { $json.Substring($start) | ConvertFrom-Json } else { $null }
    foreach ($proj in @($data.projects)) {
        foreach ($fw in @($proj.frameworks)) {
            if ($null -eq $fw) { continue }
            foreach ($pkg in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
                if ($null -eq $pkg -or $null -eq $pkg.vulnerabilities) { continue }
                foreach ($v in @($pkg.vulnerabilities)) {
                    $rows.Add([pscustomobject]@{
                            Severity = $v.severity; Package = $pkg.id; Version = $pkg.resolvedVersion
                            Project = Split-Path $proj.path -Leaf; Advisory = $v.advisoryurl
                        })
                }
            }
        }
    }
}
$rows = @($rows | Sort-Object Package, Project -Unique)
Write-Summary "### NuGet packages`n"
if ($rows.Count -eq 0) { Write-Summary 'No vulnerable packages found.'; exit 0 }
Write-Summary "| Severity | Package | Version | Project | Advisory |`n|---|---|---|---|---|"
foreach ($r in $rows) { Write-Summary "| $($r.Severity) | $($r.Package) | $($r.Version) | $($r.Project) | $($r.Advisory) |" }
$blocking = @($rows | Where-Object { $_.Severity -in 'High', 'Critical' })
foreach ($r in $rows) {
    $kind = if ($r.Severity -in 'High', 'Critical') { 'error' } else { 'warning' }
    Write-Host "::${kind} title=Vulnerable package::$($r.Severity): $($r.Package) $($r.Version) ($($r.Project)) $($r.Advisory)"
}
if ($blocking.Count -gt 0) { exit 1 }
