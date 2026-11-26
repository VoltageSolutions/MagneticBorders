# Shared helpers for tools/ci/run.ps1 and the target scripts in tools/ci/targets/.

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function New-CiContext([string]$Target, [string]$Arch, [switch]$Ci) {
    $sourceDirs = @{
        cpp = 'src\engines\cpp'; rust = 'src\engines\rust'; dotnet = 'src\engines\dotnet'
        settings = 'src\settings'; docs = '.'
    }
    $ctx = [ordered]@{
        Target       = $Target
        Arch         = $Arch                                   # x64 | arm64
        Rid          = "win-$Arch"                             # .NET runtime identifier
        RustTriple   = if ($Arch -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
        Ci           = [bool]$Ci
        RepoRoot     = $script:RepoRoot
        SourceDir    = Join-Path $script:RepoRoot $sourceDirs[$Target]
        ArtifactsDir = Join-Path $script:RepoRoot "artifacts\$Target\$Arch"   # published exes
        CoverageDir  = Join-Path $script:RepoRoot 'artifacts\coverage'         # <target>.cobertura.xml
        TestResults  = Join-Path $script:RepoRoot "artifacts\test-results\$Target"
        FixturesDir  = Join-Path $script:RepoRoot 'tests\fixtures'
    }
    foreach ($d in 'ArtifactsDir', 'CoverageDir', 'TestResults') { New-Item -ItemType Directory -Force -Path $ctx[$d] | Out-Null }
    return $ctx
}

# Run a native command and fail the step on a non-zero exit code.
function Exec([scriptblock]$Command, [string]$What = $Command.ToString().Trim()) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Failed ($LASTEXITCODE): $What" }
}

# Enter the Visual Studio developer environment for the target architecture (x64 host).
function Enter-VsDevEnv([string]$Arch = 'x64') {
    if ($env:VSCMD_ARG_TGT_ARCH -eq $Arch) { return }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio with the C++ x64 tools was not found. Run tools/setup-dev.ps1.' }
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$Arch -host_arch=x64" | Out-Null
}

function Assert-Tool([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name not found. $Hint" }
}

# Fail when line coverage in a Cobertura file is below the target (GameControllerForZwift's project target is 40%).
function Assert-Coverage([string]$CoberturaPath, [double]$MinPercent = 40) {
    [xml]$x = Get-Content $CoberturaPath
    $rate = [double]$x.coverage.'line-rate' * 100
    Write-Host ("Line coverage {0:n1}% (minimum {1}%)" -f $rate, $MinPercent)
    if ($rate -lt $MinPercent) { throw ("Coverage {0:n1}% is below {1}%" -f $rate, $MinPercent) }
}
