# Rust engine (src/engines/rust). Steps follow tools/ci/README.md; run.ps1 calls them from the Cargo workspace.
# rust-toolchain.toml pins the compiler, Cargo.lock pins the crates, and every cargo call uses --locked.

$script:RustExe = 'magnetic-borders-rs.exe'

# Enters the MSVC environment for the target architecture and sets the compiler flags for this step.
function Initialize-RustEnv($ctx, [switch]$DenyWarnings) {
    Assert-Tool cargo 'Run tools/setup-dev.ps1.'
    Enter-VsDevEnv $ctx.Arch
    $flags = @()
    if ($DenyWarnings) { $flags += @('-D', 'warnings') }
    if ($ctx.Ci) { $flags += @("--remap-path-prefix=$($ctx.RepoRoot)=.", '-C', 'link-arg=/Brepro') }
    if ($flags) { $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]0x1f }
    else { Remove-Item Env:CARGO_ENCODED_RUSTFLAGS -ErrorAction SilentlyContinue }
}

function Get-RustTargetArgs($ctx) {
    if ($ctx.Arch -eq 'arm64') { return @('--target', $ctx.RustTriple) }
    return @()
}

function Get-RustReleaseDir($ctx) {
    if ($ctx.Arch -eq 'arm64') { return Join-Path $ctx.SourceDir "target\$($ctx.RustTriple)\release" }
    return Join-Path $ctx.SourceDir 'target\release'
}

function Invoke-Restore($ctx) {
    Initialize-RustEnv $ctx
    Exec { cargo fetch --locked }
}

function Invoke-Lint($ctx) {
    Initialize-RustEnv $ctx
    Exec { cargo fmt --all -- --check }
    Exec { cargo clippy --workspace --all-targets --locked -- -D warnings }
}

function Invoke-Build($ctx) {
    Initialize-RustEnv $ctx -DenyWarnings
    $targetArgs = Get-RustTargetArgs $ctx
    Exec { cargo build --release --workspace --locked @targetArgs }
}

function Invoke-Test($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'Tests run on x64 only; ARM64 is cross-built.'; return }
    Initialize-RustEnv $ctx -DenyWarnings
    if (Get-Command cargo-nextest -ErrorAction SilentlyContinue) {
        # nextest writes JUnit XML (profile ci in .config/nextest.toml) for the test report; it skips doc tests.
        Exec { cargo nextest run --workspace --locked --profile ci }
        Exec { cargo test --workspace --locked --doc }
        Copy-Item (Join-Path $ctx.SourceDir 'target\nextest\ci\junit.xml') (Join-Path $ctx.TestResults 'rust.xml') -Force
    }
    else {
        Write-Host 'cargo-nextest not found (tools/setup-dev.ps1 installs it); running cargo test without a JUnit report.' -ForegroundColor DarkGray
        Exec { cargo test --workspace --locked }
    }
}

function Invoke-Coverage($ctx) {
    if ($ctx.Arch -ne 'x64') { Write-Host 'Coverage runs on x64 only.'; return }
    Initialize-RustEnv $ctx
    Assert-Tool cargo-llvm-cov 'Run tools/setup-dev.ps1.'
    $out = Join-Path $ctx.CoverageDir 'rust.cobertura.xml'
    # The fixture-runner binary, integration-test files and the build script are tooling, not engine code.
    $ignore = '(fixture-runner[\\/]src[\\/]main\.rs|[\\/]tests[\\/]|[\\/]build\.rs)'
    Exec { cargo llvm-cov --workspace --locked --cobertura --output-path $out --ignore-filename-regex $ignore }
    Show-RustCrateCoverage $out
    Assert-Coverage $out
}

# Prints line coverage per crate (informational; the owner's aim is 80% for core and app).
function Show-RustCrateCoverage([string]$CoberturaPath) {
    [xml]$x = Get-Content $CoberturaPath
    foreach ($crate in 'core', 'app', 'fixture-runner', 'win32', 'host') {
        $lines = @($x.SelectNodes('//class') | Where-Object { $_.filename -match "(^|[\\/])$crate[\\/]src[\\/]" } | ForEach-Object { $_.lines.line })
        if (-not $lines) { continue }
        $hit = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        Write-Host ("  {0,-15} {1,5:n1}%  ({2}/{3} lines)" -f $crate, (100.0 * $hit / $lines.Count), $hit, $lines.Count)
    }
}

function Invoke-Publish($ctx) {
    $exe = Join-Path (Get-RustReleaseDir $ctx) $script:RustExe
    if (-not (Test-Path $exe)) { throw "Missing $exe. Run the build step for $($ctx.Arch) first." }
    New-Item -ItemType Directory -Force -Path $ctx.ArtifactsDir | Out-Null
    Copy-Item $exe $ctx.ArtifactsDir -Force
    Write-Host "Published $script:RustExe to $($ctx.ArtifactsDir)"
}
