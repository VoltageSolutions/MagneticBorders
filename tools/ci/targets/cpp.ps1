# C++ engine (src/engines/cpp). Steps follow tools/ci/README.md; run.ps1 calls them from the engine folder.
# x64 builds and tests natively; arm64 is cross-compiled (tests, lint and coverage are x64 only).

function Get-CppPreset($ctx) { if ($ctx.Arch -eq 'arm64') { 'release-arm64' } else { 'release' } }

function Initialize-CppEnv($ctx) {
    Assert-Tool cmake 'Run tools/setup-dev.ps1 (Visual Studio CMake component).'
    Enter-VsDevEnv $ctx.Arch
    Assert-Tool cl 'Run tools/setup-dev.ps1 (Visual Studio C++ tools).'
}

function Invoke-CppConfigure($ctx, [string]$Preset) {
    $det = if ($ctx.Ci) { 'ON' } else { 'OFF' }
    Exec { cmake --preset $Preset "-DMB_DETERMINISTIC=$det" } "cmake --preset $Preset"
}

# Locates a tool of the "C++ Clang tools for Windows" Visual Studio component.
function Find-ClangTool([string]$Name) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -prerelease -products * -property installationPath
    $candidates = @()
    if ($vs) { $candidates += Join-Path $vs "VC\Tools\Llvm\x64\bin\$Name.exe" }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "$Name.exe not found. Install the Visual Studio component 'C++ Clang tools for Windows' (Microsoft.VisualStudio.Component.VC.Llvm.Clang) by running tools/setup-dev.ps1."
}

function Get-CppSources($ctx, [string[]]$Extensions) {
    foreach ($dir in 'core', 'app', 'win32', 'host', 'fixture-runner', 'tests') {
        Get-ChildItem -Path (Join-Path $ctx.SourceDir $dir) -Recurse -File |
            Where-Object { $Extensions -contains $_.Extension } | ForEach-Object FullName
    }
}

function Invoke-Restore($ctx) {
    Initialize-CppEnv $ctx
    Invoke-CppConfigure $ctx (Get-CppPreset $ctx)   # FetchContent downloads are pinned with URL_HASH
}

function Invoke-Lint($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'lint runs on x64 only'; return }
    Initialize-CppEnv $ctx
    $format = Find-ClangTool 'clang-format'
    $tidy = Find-ClangTool 'clang-tidy'
    $files = @(Get-CppSources $ctx '.cpp', '.hpp')
    Exec { & $format --dry-run --Werror @files } 'clang-format --dry-run --Werror'

    $db = Join-Path $ctx.SourceDir 'build\release'
    if (-not (Test-Path (Join-Path $db 'compile_commands.json'))) { Invoke-CppConfigure $ctx 'release' }
    $units = @(Get-CppSources $ctx '.cpp')
    $failed = $units | ForEach-Object -ThrottleLimit 4 -Parallel {
        $out = & $using:tidy -p $using:db --quiet '--extra-arg=-Wno-unknown-argument' '--extra-arg=-Wno-unused-command-line-argument' $_ 2>&1
        if ($LASTEXITCODE -ne 0) { [pscustomobject]@{ File = $_; Output = ($out | Out-String) } }
    }
    foreach ($f in $failed) { Write-Host "clang-tidy: $($f.File)`n$($f.Output)" }
    if ($failed) { throw "clang-tidy reported findings in $(@($failed).Count) file(s)." }
    Write-Host "clang-format and clang-tidy clean ($($units.Count) translation units)"
}

function Invoke-Build($ctx) {
    Initialize-CppEnv $ctx
    $preset = Get-CppPreset $ctx
    if (-not (Test-Path (Join-Path $ctx.SourceDir "build\$preset\CMakeCache.txt"))) { Invoke-CppConfigure $ctx $preset }
    Exec { cmake --build --preset $preset } "cmake --build --preset $preset"
}

function Invoke-Test($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'tests run on x64 only (ARM64 is cross-built)'; return }
    Initialize-CppEnv $ctx
    Exec { ctest --preset release -j 4 --output-junit (Join-Path $ctx.TestResults 'cpp.xml') } 'ctest --preset release'
}

function Get-LineRate($Classes) {
    $total = 0; $hit = 0
    foreach ($c in $Classes) {
        foreach ($l in $c.SelectNodes('lines/line')) { $total++; if ([int]$l.hits -gt 0) { $hit++ } }
    }
    if ($total -eq 0) { return $null }
    return 100.0 * $hit / $total
}

function Invoke-Coverage($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'coverage runs on x64 only'; return }
    Initialize-CppEnv $ctx
    $occ = (Get-Command OpenCppCoverage -ErrorAction SilentlyContinue)?.Source
    if (-not $occ) { $occ = Join-Path $env:ProgramFiles 'OpenCppCoverage\OpenCppCoverage.exe' }
    if (-not (Test-Path $occ)) { throw 'OpenCppCoverage not found. Run tools/setup-dev.ps1.' }

    # Debug binaries keep line information accurate; the release build stays untouched.
    Invoke-CppConfigure $ctx 'debug'
    Exec { cmake --build --preset debug } 'cmake --build --preset debug'

    $src = $ctx.SourceDir
    $bin = Join-Path $src 'build\debug\bin'
    $tmp = Join-Path $ctx.CoverageDir 'cpp-work'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $filters = @('--sources', "$src\*", '--excluded_sources', "$src\tests\*", '--excluded_sources', "$src\fixture-runner\*",
                 '--excluded_sources', "$src\build\*")
    $unit = Join-Path $tmp 'unit.cov'
    $fixt = Join-Path $tmp 'fixtures.cov'
    Exec { & $occ --quiet @filters --export_type "binary:$unit" -- (Join-Path $bin 'mb_tests.exe') } 'OpenCppCoverage mb_tests'
    Exec { & $occ --quiet @filters --export_type "binary:$fixt" -- (Join-Path $bin 'fixture_runner.exe') $ctx.FixturesDir } 'OpenCppCoverage fixture_runner'

    $out = Join-Path $ctx.CoverageDir 'cpp.cobertura.xml'
    Exec { & $occ --quiet @filters --input_coverage $unit --input_coverage $fixt --export_type "cobertura:$out" } 'OpenCppCoverage merge'

    [xml]$x = Get-Content $out
    $classes = @($x.SelectNodes('//class'))
    foreach ($area in 'core', 'app', 'win32', 'host') {
        $rate = Get-LineRate ($classes | Where-Object { $_.filename -match "(^|[\\/])$area[\\/]" })
        if ($null -eq $rate) { continue }
        Write-Host ("  {0,-6} {1,5:n1}%" -f $area, $rate)
        if ($area -in 'core', 'app' -and $rate -lt 80) { Write-Warning "$area line coverage $([math]::Round($rate,1))% is below the 80% goal" }
    }
    Assert-Coverage $out 40
}

function Invoke-Publish($ctx) {
    $exe = Join-Path $ctx.SourceDir "build\$(Get-CppPreset $ctx)\bin\MagneticBorders.Cpp.exe"
    if (-not (Test-Path $exe)) { throw "Missing $exe. Run the build step first." }
    Copy-Item $exe $ctx.ArtifactsDir -Force
    Write-Host "Published $(Join-Path $ctx.ArtifactsDir 'MagneticBorders.Cpp.exe')"
}
