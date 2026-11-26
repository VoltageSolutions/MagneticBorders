#Requires -Version 7
<#
.SYNOPSIS
  Installs or checks every tool needed to build, lint, test and measure coverage for Magnetic Borders.
  Safe to re-run. Visual Studio changes need an elevated prompt and Visual Studio closed.

.EXAMPLE
  pwsh tools/setup-dev.ps1            # install what is missing
.EXAMPLE
  pwsh tools/setup-dev.ps1 -CheckOnly # report only
#>
[CmdletBinding()]
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$missing = [System.Collections.Generic.List[string]]::new()

function Step([string]$Name, [scriptblock]$Test, [scriptblock]$Install) {
    if (& $Test) { Write-Host "  ok       $Name" -ForegroundColor Green; return }
    if ($CheckOnly) { Write-Host "  missing  $Name" -ForegroundColor Yellow; $missing.Add($Name); return }
    Write-Host "  install  $Name" -ForegroundColor Cyan
    & $Install
    if (-not (& $Test)) { Write-Host "  FAILED   $Name" -ForegroundColor Red; $missing.Add($Name) }
}
function Has([string]$cmd) { [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

Write-Host 'Visual Studio components' -ForegroundColor White
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products * -property installationPath
$components = @(
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',   # MSVC x64
    'Microsoft.VisualStudio.Component.VC.Tools.ARM64',     # MSVC ARM64 cross tools (C++, Rust, .NET Native AOT)
    'Microsoft.VisualStudio.Component.VC.CMake.Project',   # CMake + Ninja
    'Microsoft.VisualStudio.Component.VC.Llvm.Clang',      # clang-format, clang-tidy
    'Microsoft.VisualStudio.Workload.ManagedDesktop'       # .NET desktop
)
foreach ($c in $components) {
    Step $c { [bool](& $vswhere -latest -prerelease -products * -requires $c -property installationPath) } {
        Start-Process -Wait -Verb RunAs (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vs_installer.exe') `
            -ArgumentList "modify --installPath `"$vs`" --add $c --passive --norestart"
    }
}

# WinUI 3 builds from NuGet packages (Microsoft.WindowsAppSDK), so the Visual Studio WinUI workload is optional:
# it only adds project templates and XAML tooling. Report it, never fail on it.
$winui = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs -property installationPath
if ($winui) { Write-Host '  ok       WinUI application development (optional)' -ForegroundColor Green }
else { Write-Host '  optional WinUI application development workload (templates and XAML tooling; not needed to build)' -ForegroundColor DarkGray }

Write-Host 'Rust' -ForegroundColor White
Step 'rustup' { Has rustup } { winget install --id Rustlang.Rustup -e --accept-source-agreements --accept-package-agreements }
Push-Location (Join-Path $Root 'src\engines\rust')   # rust-toolchain.toml pins the toolchain here
try {
    Step 'pinned toolchain'        { (rustup show active-toolchain 2>$null) -match 'msvc' } { rustup show active-toolchain | Out-Null }
    Step 'rustfmt + clippy'        { (rustup component list --installed) -match 'clippy' -and (rustup component list --installed) -match 'rustfmt' } { rustup component add rustfmt clippy }
    Step 'llvm-tools-preview'      { (rustup component list --installed) -match 'llvm-tools' } { rustup component add llvm-tools-preview }
    Step 'aarch64-pc-windows-msvc' { (rustup target list --installed) -match 'aarch64-pc-windows-msvc' } { rustup target add aarch64-pc-windows-msvc }
    Step 'cargo-llvm-cov'          { Has cargo-llvm-cov } { cargo install cargo-llvm-cov --locked }
    Step 'cargo-nextest'           { Has cargo-nextest } { cargo install cargo-nextest --locked }
} finally { Pop-Location }

Write-Host 'C++ coverage' -ForegroundColor White
Step 'OpenCppCoverage' { (Has OpenCppCoverage) -or (Test-Path "$env:ProgramFiles\OpenCppCoverage\OpenCppCoverage.exe") } {
    winget install --id OpenCppCoverage.OpenCppCoverage -e --accept-source-agreements --accept-package-agreements
}

Write-Host '.NET' -ForegroundColor White
Step '.NET SDK (global.json)' { Has dotnet } { winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements }
Step 'dotnet local tools'     { Push-Location $Root; try { dotnet tool restore *> $null; $LASTEXITCODE -eq 0 } finally { Pop-Location } } { Push-Location $Root; try { dotnet tool restore } finally { Pop-Location } }

Write-Host 'Docs' -ForegroundColor White
Step 'Python 3.12+' { (Has python) -and ((python -c "import sys;print(sys.version_info>=(3,12))") -eq 'True') } { winget install --id Python.Python.3.12 -e --accept-source-agreements --accept-package-agreements }
Step 'zensical' { python -m zensical --version *> $null; $LASTEXITCODE -eq 0 } { python -m pip install --user -r (Join-Path $Root 'docs\requirements.txt') }

if ($missing.Count) { Write-Host "`nStill missing: $($missing -join ', ')" -ForegroundColor Yellow; exit 1 }
Write-Host "`nAll tools present." -ForegroundColor Green
