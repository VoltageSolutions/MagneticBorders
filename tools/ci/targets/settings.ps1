# WinUI 3 settings app (src/settings): restore, lint, build, test, coverage, publish.
# Called by tools/ci/run.ps1 with the context from common.ps1. Runs in src/settings.

function Get-SettingsPlatform($ctx) { if ($ctx.Arch -ieq 'arm64') { 'ARM64' } else { 'x64' } }

# MSBuild properties that make a CI build reproducible.
function Get-SettingsCiProps($ctx) {
    if ($ctx.Ci) { return @('-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true') }
    return @()
}

function Invoke-Restore($ctx) {
    $restoreArgs = @('restore', 'MagneticBorders.Settings.slnx')
    if ($ctx.Ci) { $restoreArgs += '--locked-mode' }
    Exec { dotnet @restoreArgs } 'dotnet restore'
    Push-Location $ctx.RepoRoot
    try { Exec { dotnet tool restore } 'dotnet tool restore' } finally { Pop-Location }
}

function Invoke-Lint($ctx) {
    # C#: formatting and analyzer findings at error severity (warnings are errors in the build itself).
    Exec { dotnet format MagneticBorders.Settings.slnx --verify-no-changes --severity error --no-restore } 'dotnet format'

    # XAML: XamlStyler in passive mode reports files that need formatting and never rewrites them.
    Push-Location $ctx.RepoRoot
    try {
        Exec { dotnet tool restore } 'dotnet tool restore'
        $config = Join-Path $ctx.RepoRoot 'Settings.XamlStyler'
        $files = Get-ChildItem (Join-Path $ctx.RepoRoot 'src\settings') -Recurse -Filter '*.xaml' |
            Where-Object { $_.FullName -notmatch '\\(obj|bin|\.vs)\\' }
        $env:DOTNET_ROLL_FORWARD = 'Major'   # the tool may target an older runtime than the pinned SDK provides
        foreach ($f in $files) {
            Exec { dotnet xstyler --passive --config $config --file $f.FullName } "xstyler $($f.Name)"
        }
    } finally { Pop-Location }
}

function Invoke-Build($ctx) {
    $platform = Get-SettingsPlatform $ctx
    $buildArgs = @('build', 'MagneticBorders.Settings.slnx', '-c', 'Release', "-p:Platform=$platform", '--no-restore') + (Get-SettingsCiProps $ctx)
    Exec { dotnet @buildArgs } 'dotnet build'
}

function Invoke-Test($ctx) {
    if ($ctx.Arch -ieq 'arm64') { Write-Host 'Tests run on x64 only; skipped.' -ForegroundColor DarkGray; return }
    Exec { dotnet test tests\MagneticBorders.Settings.Tests.csproj -c Release -p:Platform=x64 --no-restore --results-directory $ctx.TestResults } 'dotnet test'
}

function Invoke-Coverage($ctx) {
    if ($ctx.Arch -ieq 'arm64') { Write-Host 'Coverage runs on x64 only; skipped.' -ForegroundColor DarkGray; return }
    $raw = Join-Path $ctx.TestResults 'coverage-raw'
    if (Test-Path $raw) { Remove-Item $raw -Recurse -Force }
    $testArgs = @('test', 'tests\MagneticBorders.Settings.Tests.csproj', '-c', 'Release', '-p:Platform=x64', '--no-restore',
        '--collect:XPlat Code Coverage', '--settings', 'tests\coverage.runsettings', '--results-directory', $raw)
    Exec { dotnet @testArgs } 'dotnet test (coverage)'

    Push-Location $ctx.RepoRoot
    try {
        Exec { dotnet tool restore } 'dotnet tool restore'
        $merged = Join-Path $ctx.TestResults 'coverage-merged'
        $rgArgs = @("-reports:$raw\**\coverage.cobertura.xml", "-targetdir:$merged", '-reporttypes:Cobertura;TextSummary')
        Exec { dotnet reportgenerator @rgArgs } 'reportgenerator'
        New-Item -ItemType Directory -Force -Path $ctx.CoverageDir | Out-Null
        $out = Join-Path $ctx.CoverageDir 'settings.cobertura.xml'
        Copy-Item (Join-Path $merged 'Cobertura.xml') $out -Force
        Get-Content (Join-Path $merged 'Summary.txt') | Write-Host
        Assert-Coverage $out 40
    } finally { Pop-Location }
}

function Invoke-Publish($ctx) {
    $platform = Get-SettingsPlatform $ctx
    if (Test-Path $ctx.ArtifactsDir) { Remove-Item (Join-Path $ctx.ArtifactsDir '*') -Recurse -Force }
    $publishDir = $ctx.ArtifactsDir.TrimEnd('\') + '\'
    $pubArgs = @('publish', 'app\MagneticBorders.Settings.csproj', '-c', 'Release', "-p:Platform=$platform", '--self-contained',
        '--no-restore', "-p:PublishDir=$publishDir") + (Get-SettingsCiProps $ctx)
    Exec { dotnet @pubArgs } 'dotnet publish'
    # The Native AOT symbols file is ~35 MB and is not needed at run time.
    Get-ChildItem $ctx.ArtifactsDir -Filter '*.pdb' -Recurse | Remove-Item -Force
}
