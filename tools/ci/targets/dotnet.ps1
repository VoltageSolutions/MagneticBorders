# C# engine (src/engines/dotnet): .NET 10, Native AOT host. Dot-sourced by tools/ci/run.ps1; the working
# directory is $ctx.SourceDir. Every step expects `restore` to have run first.

$script:Sln = 'MagneticBorders.slnx'
$script:HostProj = 'host/MagneticBorders.Host.csproj'

function Get-DotnetProps($ctx) {
    $p = @('-c', 'Release', '--nologo')
    if ($ctx.Ci) { $p += '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true', '-p:RestoreLockedMode=true' }
    return $p
}

function Invoke-Restore($ctx) {
    Assert-Tool dotnet 'Run tools/setup-dev.ps1.'
    # Lock files (packages.lock.json) are committed. In CI the restore fails if one would change.
    Exec { dotnet restore $script:Sln --nologo $(if ($ctx.Ci) { '--locked-mode' }) }
    # The host restores both runtime identifiers (RuntimeIdentifiers in its csproj), so ARM64 needs no second restore.
}

function Invoke-Lint($ctx) {
    Exec { dotnet format $script:Sln --no-restore --verify-no-changes --severity error }
    $props = Get-DotnetProps $ctx
    Exec { dotnet build $script:Sln @props --no-restore --no-incremental -warnaserror }
}

function Invoke-Build($ctx) {
    Assert-Tool dotnet 'Run tools/setup-dev.ps1.'
    $props = Get-DotnetProps $ctx
    if ($ctx.Arch -eq 'arm64') {
        Enter-VsDevEnv arm64
        Exec { dotnet build $script:HostProj @props -r $ctx.Rid --no-restore -warnaserror }
    } else {
        Exec { dotnet build $script:Sln @props --no-restore -warnaserror }
    }
}

function Invoke-Test($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'Tests run on x64 only; skipping.'; return }
    $props = Get-DotnetProps $ctx
    Exec { dotnet test $script:Sln @props --no-restore --results-directory $ctx.TestResults --logger trx }
    Exec { dotnet (Join-Path $PWD 'fixture-runner/bin/Release/net10.0/FixtureRunner.dll') $ctx.FixturesDir }
}

function Invoke-Coverage($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'Coverage runs on x64 only; skipping.'; return }
    $props = Get-DotnetProps $ctx
    $raw = Join-Path $ctx.TestResults 'coverage'
    if (Test-Path $raw) { Remove-Item -Recurse -Force $raw }
    Exec { dotnet test $script:Sln @props --no-restore --collect:'XPlat Code Coverage' --settings coverage.runsettings --results-directory $raw }
    $files = @(Get-ChildItem -Path $raw -Recurse -Filter 'coverage.cobertura.xml')
    if ($files.Count -eq 0) { throw "No Cobertura files under $raw" }
    $out = Join-Path $ctx.CoverageDir 'dotnet.cobertura.xml'
    Merge-Cobertura -Path $files.FullName -OutPath $out
    Assert-Coverage $out
}

function Invoke-Publish($ctx) {
    Assert-Tool dotnet 'Run tools/setup-dev.ps1.'
    $props = Get-DotnetProps $ctx
    Enter-VsDevEnv $ctx.Arch   # the Native AOT linker comes from the MSVC tools for the target architecture
    if (Test-Path $ctx.ArtifactsDir) { Get-ChildItem $ctx.ArtifactsDir | Remove-Item -Recurse -Force }
    Exec { dotnet publish $script:HostProj @props -r $ctx.Rid --no-restore -warnaserror -o $ctx.ArtifactsDir }
    $exe = Join-Path $ctx.ArtifactsDir 'MagneticBorders.Net.exe'
    if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
}

# Merges Cobertura files: a line counts as covered when any report covers it. Output is one package per assembly, one class per source file.
function Merge-Cobertura([string[]]$Path, [string]$OutPath) {
    $pkgs = [ordered]@{}   # package -> file -> line number -> hits
    foreach ($f in $Path) {
        [xml]$x = Get-Content -LiteralPath $f
        foreach ($pkg in $x.coverage.packages.package) {
            if (-not $pkgs.Contains($pkg.name)) { $pkgs[$pkg.name] = [ordered]@{} }
            foreach ($cls in $pkg.classes.class) {
                if (-not $pkgs[$pkg.name].Contains($cls.filename)) { $pkgs[$pkg.name][$cls.filename] = @{} }
                $lines = $pkgs[$pkg.name][$cls.filename]
                foreach ($l in $cls.lines.line) {
                    $n = [int]$l.number; $h = [int]$l.hits
                    $lines[$n] = [Math]::Max($h, $(if ($lines.ContainsKey($n)) { $lines[$n] } else { 0 }))
                }
            }
        }
    }
    $doc = [xml]'<?xml version="1.0" encoding="utf-8"?><coverage/>'
    $root = $doc.DocumentElement
    $total = 0; $covered = 0
    $pkgsEl = $root.AppendChild($doc.CreateElement('packages'))
    foreach ($pn in $pkgs.Keys) {
        $pEl = $pkgsEl.AppendChild($doc.CreateElement('package')); $pEl.SetAttribute('name', $pn)
        $clsEl = $pEl.AppendChild($doc.CreateElement('classes'))
        $pt = 0; $pc = 0
        foreach ($fn in $pkgs[$pn].Keys) {
            $lines = $pkgs[$pn][$fn]
            $c = $clsEl.AppendChild($doc.CreateElement('class'))
            $c.SetAttribute('name', [IO.Path]::GetFileNameWithoutExtension($fn)); $c.SetAttribute('filename', $fn)
            $lsEl = $c.AppendChild($doc.CreateElement('lines'))
            $ct = 0; $cc = 0
            foreach ($n in ($lines.Keys | Sort-Object)) {
                $le = $lsEl.AppendChild($doc.CreateElement('line'))
                $le.SetAttribute('number', "$n"); $le.SetAttribute('hits', "$($lines[$n])")
                $ct++; if ($lines[$n] -gt 0) { $cc++ }
            }
            $c.SetAttribute('line-rate', $(if ($ct) { ($cc / $ct).ToString('0.####', [Globalization.CultureInfo]::InvariantCulture) } else { '1' }))
            $pt += $ct; $pc += $cc
        }
        $rate = if ($pt) { $pc / $pt } else { 1 }
        $pEl.SetAttribute('line-rate', $rate.ToString('0.####', [Globalization.CultureInfo]::InvariantCulture))
        Write-Host ("  {0,-32} {1,6:n1}% ({2}/{3} lines)" -f $pn, ($rate * 100), $pc, $pt)
        $total += $pt; $covered += $pc
    }
    $overall = if ($total) { $covered / $total } else { 1 }
    $root.SetAttribute('line-rate', $overall.ToString('0.####', [Globalization.CultureInfo]::InvariantCulture))
    $root.SetAttribute('lines-covered', "$covered"); $root.SetAttribute('lines-valid', "$total")
    $doc.Save($OutPath)
}
