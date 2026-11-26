#Requires -Version 7
<#
.SYNOPSIS
  Local Windows build loop for MagneticBorders.

.DESCRIPTION
  Polls .build/request.json. When a request appears, builds and tests the requested
  targets (cpp, rust, dotnet, compare), then writes a compact .build/status.json
  (per-step pass/fail, durations, first N error lines) plus full logs in .build/logs/.
  Agents write the request and read the status; nothing builds on plain file saves.

  Request example:
    { "id": "a1", "targets": ["cpp","rust","dotnet"], "test": true, "publish": false }

.EXAMPLE
  pwsh tools/build-watcher.ps1            # run the loop
.EXAMPLE
  pwsh tools/build-watcher.ps1 -Check     # report toolchains and exit
.EXAMPLE
  pwsh tools/build-watcher.ps1 -PushOnGreen
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Once,
    [switch]$PushOnGreen,
    [int]$PollSeconds = 2,
    [int]$ErrorLines = 30
)

$ErrorActionPreference = 'Stop'
$Root        = Split-Path -Parent $PSScriptRoot
$BuildDir    = Join-Path $Root '.build'
$LogDir      = Join-Path $BuildDir 'logs'
$RequestPath = Join-Path $BuildDir 'request.json'
$WorkingPath = Join-Path $BuildDir 'processing.json'
$StatusPath  = Join-Path $BuildDir 'status.json'
$Artifacts   = Join-Path $Root 'artifacts'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-Json($path, $obj) {
    $tmp = "$path.tmp"
    $obj | ConvertTo-Json -Depth 8 | Set-Content -Path $tmp -Encoding utf8NoBOM
    Move-Item -Force $tmp $path
}

function Enter-VsDevEnv {
    if ($env:VSCMD_VER) { return $true }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { Write-Warning 'vswhere.exe not found: C++ builds need Visual Studio.'; return $false }
    $vs = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { Write-Warning 'No Visual Studio install with the C++ x64 tools found.'; return $false }
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
    return $true
}

function Get-ToolVersion([string]$exe, [string[]]$toolArgs) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { return $null }
    try {
        $line = & $exe @toolArgs 2>&1 | Where-Object { "$_".Trim() } | Select-Object -First 1
        return "$line".Trim()
    } catch { return 'present (version check failed)' }
}

function Get-Toolchains {
    [ordered]@{
        checkedAt = (Get-Date).ToString('o')
        vsDevEnv  = [bool]$env:VSCMD_VER
        msvc      = Get-ToolVersion 'cl' @()
        cmake     = Get-ToolVersion 'cmake' @('--version')
        ninja     = Get-ToolVersion 'ninja' @('--version')
        rustc     = Get-ToolVersion 'rustc' @('--version')
        cargo     = Get-ToolVersion 'cargo' @('--version')
        dotnet    = Get-ToolVersion 'dotnet' @('--version')
        dotnetSdks = (Get-Command dotnet -ErrorAction SilentlyContinue) ? @(& dotnet --list-sdks) : @()
        git       = Get-ToolVersion 'git' @('--version')
    }
}

function Get-ErrorExcerpt([string]$log, [int]$max) {
    if (-not (Test-Path $log)) { return @() }
    Get-Content $log |
        Where-Object { $_ -match '(?i)\berror\b|FAILED|panicked|\bfailed\b|fatal' -and $_ -notmatch '(?i)\b0 error|\b0 failed|warning' } |
        Select-Object -Unique -First $max
}

function Invoke-Step($target, $name, $workDir, [string]$exe, [string[]]$stepArgs, $log) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    "`n==== $target/$name : $exe $($stepArgs -join ' ')" | Add-Content -Path $log -Encoding utf8
    Push-Location $workDir
    try {
        & $exe @stepArgs *>> $log
        $code = $LASTEXITCODE
    } catch {
        "$_" | Add-Content -Path $log -Encoding utf8
        $code = -1
    } finally { Pop-Location }
    [ordered]@{ step = $name; ok = ($code -eq 0); exitCode = $code; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

function Get-TargetPlan([string]$target, [bool]$test, [bool]$publish, $req) {
    # Targets with a tools/ci/targets/<target>.ps1 script run through tools/ci/run.ps1, the same
    # entry point the GitHub workflows use. A request may name steps and an architecture:
    #   {"targets":["cpp"],"steps":["lint","build","test","coverage"],"arch":"x64"}
    $ciScript = Join-Path $Root "tools\ci\targets\$target.ps1"
    if ($target -in 'cpp', 'rust', 'dotnet', 'settings', 'docs' -and (Test-Path $ciScript)) {
        $steps = if ($req.steps) { @($req.steps) } else { @('restore', 'build') + ($test ? @('test') : @()) + ($publish ? @('publish') : @()) }
        $arch  = if ($req.arch) { "$($req.arch)" } else { 'x64' }
        $run   = Join-Path $Root 'tools\ci\run.ps1'
        $plan  = @()
        $ciFlag = if ($req.ci) { @('-Ci') } else { @() }   # {"ci":true} runs locked restores and deterministic builds, as in GitHub CI
        foreach ($st in $steps) { $plan += ,(@($st, 'pwsh', (@('-NoProfile', '-File', $run, '-Target', $target, '-Steps', $st, '-Arch', $arch) + $ciFlag))) }
        return @{ dir = $Root; steps = $plan }
    }
    switch ($target) {
        'cpp' {
            $dir = Join-Path $Root 'src\engines\cpp'
            if (-not (Test-Path (Join-Path $dir 'CMakePresets.json'))) { return $null }
            $s = @(
                @('configure', 'cmake', @('--preset', 'release')),
                @('build',     'cmake', @('--build', '--preset', 'release'))
            )
            if ($test)    { $s += ,@('test', 'ctest', @('--preset', 'release', '--output-on-failure')) }
            if ($publish) { $s += ,@('publish', 'cmake', @('--install', 'build/release', '--prefix', (Join-Path $Artifacts 'cpp'))) }
            return @{ dir = $dir; steps = $s }
        }
        'rust' {
            $dir = Join-Path $Root 'src\engines\rust'
            if (-not (Test-Path (Join-Path $dir 'Cargo.toml'))) { return $null }
            $s = @(,@('build', 'cargo', @('build', '--release', '--workspace')))
            if ($test)    { $s += ,@('test', 'cargo', @('test', '--release', '--workspace')) }
            if ($publish) { $s += ,@('publish', 'pwsh', @('-NoProfile', '-Command',
                "New-Item -ItemType Directory -Force '$Artifacts\rust' | Out-Null; Copy-Item target\release\*.exe '$Artifacts\rust' -Force")) }
            return @{ dir = $dir; steps = $s }
        }
        'dotnet' {
            $dir = Join-Path $Root 'src\engines\dotnet'
            $sln = Get-ChildItem $dir -Filter 'MagneticBorders.sln*' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $sln) { return $null }
            $s = @(,@('build', 'dotnet', @('build', $sln.Name, '-c', 'Release')))
            if ($test)    { $s += ,@('test', 'dotnet', @('test', $sln.Name, '-c', 'Release', '--no-build')) }
            if ($publish) { $s += ,@('publish', 'dotnet', @('publish', 'host', '-c', 'Release', '-r', 'win-x64', "-p:PublishDir=$(Join-Path $Artifacts 'dotnet')\\")) }
            return @{ dir = $dir; steps = $s }
        }
        { $_ -in 'settings', 'ui' } {
            $dir = Join-Path $Root 'src\settings'
            $sln = Get-ChildItem $dir -Filter 'MagneticBorders.Settings.sln*' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $sln) { return $null }
            $s = @(,@('build', 'dotnet', @('build', $sln.Name, '-c', 'Release', '-p:Platform=x64')))
            if ($test)    { $s += ,@('test', 'dotnet', @('test', $sln.Name, '-c', 'Release', '-p:Platform=x64', '--no-build')) }
            if ($publish) { $s += ,@('publish', 'dotnet', @('publish', 'app', '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '--self-contained', "-p:PublishDir=$(Join-Path $Artifacts 'settings')\\")) }
            return @{ dir = $dir; steps = $s }
        }
        'commit' {
            # Message comes from .build/commit-msg.txt (written by the requester).
            $msg = Join-Path $BuildDir 'commit-msg.txt'
            if (-not (Test-Path $msg)) { return $null }
            return @{ dir = $Root; steps = @(
                @('add', 'git', @('add', '-A')),
                @('commit', 'git', @('commit', '-F', $msg))
            ) }
        }
        'push' {
            $branch = (& git -C $Root rev-parse --abbrev-ref HEAD).Trim()
            return @{ dir = $Root; steps = @(,@('push', 'git', @('push', '-u', 'origin', $branch))) }
        }
        'compare' {
            $script = Join-Path $Root 'tools\compare.ps1'
            if (-not (Test-Path $script)) { return $null }
            return @{ dir = $Root; steps = @(,@('compare', 'pwsh', @('-NoProfile', '-File', $script))) }
        }
        default { return $null }
    }
}

function Invoke-Request($req, [string]$StatusPath) {
    $id      = if ($req.id) { "$($req.id)" } else { (Get-Date).ToString('yyyyMMdd-HHmmss') }
    $targets = if ($req.targets) { @($req.targets) } else { @('cpp', 'rust', 'dotnet') }
    $test    = if ($null -ne $req.test) { [bool]$req.test } else { $true }
    $publish = [bool]$req.publish
    $started = Get-Date
    Write-Json $StatusPath ([ordered]@{ id = $id; state = 'running'; targets = $targets; startedAt = $started.ToString('o') })
    Write-Host "[$($started.ToString('HH:mm:ss'))] request $id -> $($targets -join ', ')" -ForegroundColor Cyan

    $results = [ordered]@{}
    $allOk = $true
    foreach ($t in $targets) {
        $plan = Get-TargetPlan $t $test $publish $req
        if (-not $plan) { $results[$t] = [ordered]@{ ok = $null; skipped = 'not scaffolded or unknown target' }; continue }
        $log = Join-Path $LogDir "$id-$t.log"
        Remove-Item $log -ErrorAction SilentlyContinue
        $steps = @()
        foreach ($s in $plan.steps) {
            $r = Invoke-Step $t $s[0] $plan.dir $s[1] $s[2] $log
            $steps += $r
            if (-not $r.ok) { break }
        }
        $ok = -not ($steps | Where-Object { -not $_.ok })
        if (-not $ok) { $allOk = $false }
        $results[$t] = [ordered]@{
            ok     = $ok
            steps  = $steps
            log    = [IO.Path]::GetRelativePath($Root, $log)
            errors = $ok ? @() : @(Get-ErrorExcerpt $log $ErrorLines)
        }
        Write-Host ("  {0,-8} {1}" -f $t, ($ok ? 'OK' : 'FAILED')) -ForegroundColor ($ok ? 'Green' : 'Red')
    }

    $pushed = $null
    if ($PushOnGreen -and $allOk) {
        & git -C $Root push 2>&1 | Out-Null
        $pushed = ($LASTEXITCODE -eq 0)
    }
    Write-Json $StatusPath ([ordered]@{
        id = $id; state = 'done'; ok = $allOk
        startedAt = $started.ToString('o'); finishedAt = (Get-Date).ToString('o')
        pushed = $pushed; results = $results
    })
}

# ---- main ----
$haveVs = Enter-VsDevEnv
$tools = Get-Toolchains
Write-Json (Join-Path $BuildDir 'toolchains.json') $tools
if ($Check) { $tools | Format-List; return }

Write-Host "MagneticBorders build watcher: $Root (poll ${PollSeconds}s, VS env: $haveVs). Ctrl+C to stop." -ForegroundColor Cyan
$selfStamp = (Get-Item $PSCommandPath).LastWriteTimeUtc
while ($true) {
    # Restart in place when this script is updated, so changes apply without a manual restart.
    if ((Get-Item $PSCommandPath).LastWriteTimeUtc -ne $selfStamp) {
        Write-Host 'build-watcher.ps1 changed; restarting.' -ForegroundColor Yellow
        $fwd = @('-NoLogo', '-NoProfile', '-File', $PSCommandPath, '-PollSeconds', $PollSeconds, '-ErrorLines', $ErrorLines)
        if ($PushOnGreen) { $fwd += '-PushOnGreen' }
        & pwsh @fwd
        exit $LASTEXITCODE
    }
    # Each agent uses its own pair: request-<name>.json -> status-<name>.json (plain request.json -> status.json).
    $pending = Get-ChildItem $BuildDir -Filter 'request*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc
    foreach ($file in $pending) {
        $suffix  = $file.BaseName.Substring('request'.Length)          # '' or '-cpp'
        $status  = Join-Path $BuildDir "status$suffix.json"
        $working = Join-Path $BuildDir "processing$suffix.json"
        try {
            Move-Item -Force $file.FullName $working
            $req = Get-Content $working -Raw | ConvertFrom-Json
            Invoke-Request $req $status
        } catch {
            Write-Json $status ([ordered]@{ state = 'error'; ok = $false; error = "$_"; at = (Get-Date).ToString('o') })
            Write-Warning "Request $($file.Name) failed: $_"
        } finally {
            Remove-Item $working -ErrorAction SilentlyContinue
        }
        if ($Once) { exit 0 }
    }
    Start-Sleep -Seconds $PollSeconds
}
