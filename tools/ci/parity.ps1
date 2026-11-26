#Requires -Version 7
<#
.SYNOPSIS
  Runs every engine's fixture runner with --print over tests/fixtures and fails on any difference.

.DESCRIPTION
  The three engines must behave identically. For each .layout file this prints the final frames from the C++,
  Rust and C# fixture runners and compares them line by line (line endings and trailing whitespace ignored).
  The runners must be built first (pwsh tools/ci/run.ps1 -Target <cpp|rust|dotnet> -Steps restore,build).
  A runner path ending in .dll runs through `dotnet`.

.EXAMPLE
  pwsh tools/ci/parity.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path,
    [string]$FixturesDir,
    [string]$CppRunner,
    [string]$RustRunner,
    [string]$DotnetRunner
)
$ErrorActionPreference = 'Stop'

if (-not $FixturesDir) { $FixturesDir = Join-Path $RepoRoot 'tests' 'fixtures' }
if (-not $CppRunner) { $CppRunner = Join-Path $RepoRoot 'src' 'engines' 'cpp' 'build' 'release' 'bin' 'fixture_runner.exe' }
if (-not $RustRunner) { $RustRunner = Join-Path $RepoRoot 'src' 'engines' 'rust' 'target' 'release' 'fixture-runner.exe' }
if (-not $DotnetRunner) { $DotnetRunner = Join-Path $RepoRoot 'src' 'engines' 'dotnet' 'fixture-runner' 'bin' 'Release' 'net10.0' 'FixtureRunner.dll' }

$runners = [ordered]@{ cpp = $CppRunner; rust = $RustRunner; dotnet = $DotnetRunner }
foreach ($name in $runners.Keys) {
    if (-not (Test-Path -LiteralPath $runners[$name])) {
        throw "The $name fixture runner is missing: $($runners[$name]). Build it with: pwsh tools/ci/run.ps1 -Target $name -Steps restore,build"
    }
}

# Runs one fixture runner with --print and returns its normalized output. Throws when it fails or prints nothing.
function Invoke-Runner([string]$Name, [string]$Runner, [string]$Fixture) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    if ($Runner -like '*.dll') { $psi.FileName = 'dotnet'; $psi.ArgumentList.Add($Runner) } else { $psi.FileName = $Runner }
    $psi.ArgumentList.Add('--print')
    $psi.ArgumentList.Add($Fixture)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $stdout = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "$Name runner exited with $($p.ExitCode) for $(Split-Path $Fixture -Leaf): $($stderrTask.Result.Trim())" }
    $lines = @($stdout -split "`r?`n" | ForEach-Object { $_.TrimEnd() })
    while ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = $lines[0..($lines.Count - 2)] }
    if ($lines.Count -eq 0) { throw "$Name runner printed nothing for $(Split-Path $Fixture -Leaf)" }
    return , $lines
}

$fixtures = @(Get-ChildItem -LiteralPath $FixturesDir -Filter '*.layout' -File | Sort-Object Name)
if ($fixtures.Count -eq 0) { throw "No .layout files in $FixturesDir" }

$failed = 0
foreach ($f in $fixtures) {
    $out = [ordered]@{}
    $problem = $null
    foreach ($name in $runners.Keys) {
        try { $out[$name] = Invoke-Runner $name $runners[$name] $f.FullName }
        catch { $problem = $_.Exception.Message; break }
    }
    if ($problem) {
        $failed++
        Write-Host "FAIL $($f.Name): $problem"
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=Parity::$($f.Name): $problem" }
        continue
    }
    $reference = $out['cpp']
    $diffs = @()
    foreach ($name in 'rust', 'dotnet') {
        $other = $out[$name]
        $same = $reference.Count -eq $other.Count -and -not (0..($reference.Count - 1) | Where-Object { $reference[$_] -cne $other[$_] })
        if (-not $same) {
            $diffs += $name
            Write-Host "FAIL $($f.Name): $name differs from cpp"
            for ($i = 0; $i -lt [Math]::Max($reference.Count, $out[$name].Count); $i++) {
                $a = if ($i -lt $reference.Count) { $reference[$i] } else { '<missing>' }
                $b = if ($i -lt $out[$name].Count) { $out[$name][$i] } else { '<missing>' }
                if ($a -cne $b) { Write-Host ("  line {0}: cpp   | {1}`n  line {0}: {2,-5} | {3}" -f ($i + 1), $a, $name, $b) }
            }
        }
    }
    if ($diffs.Count -gt 0) {
        $failed++
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=Parity::$($f.Name): $($diffs -join ', ') differ from cpp" }
    } else {
        Write-Host "PASS $($f.Name)"
    }
}

$summary = "Parity: $($fixtures.Count - $failed) of $($fixtures.Count) fixtures identical across cpp, rust and dotnet."
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($failed -gt 0) { exit 1 }
