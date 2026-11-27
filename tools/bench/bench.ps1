#Requires -Version 7
<#
.SYNOPSIS
  Runs the C++, Rust, and C# engines through the same scripted workload and records comparable metrics.

.DESCRIPTION
  For every run and every engine, in rotating order (cpp, rust, dotnet; then rust, dotnet, cpp; and so on), the script
  starts the engine, waits for the named pipe to answer {"cmd":"status"}, runs the selected scenarios, and stops the
  engine with {"cmd":"quit"}. The first -Warmups runs are recorded but left out of the summary.

  Scenarios (startup is measured on every launch):
    idle     CPU time, private bytes, working set, handles, threads, GDI and USER objects, sampled every second.
    drag     Scripted drags of the border between test windows with SendInput. Reports engine CPU per drag and the
             gap in pixels between the dragged edge and the edges that must follow it (mean, p50, p95, p99, max, and the
             share of samples with a gap).
    soak     Many short drags; reports the slope of private bytes, handles, GDI and USER objects per 1,000 drags.
    size     Exe and folder size on disk.

  Drag and soak need an interactive, unlocked desktop to create windows. By default (-DragInput auto) the script first
  tries real mouse input (SendInput). When Windows does not deliver it, as on some hosted CI runners, drags are
  simulated: the harness raises the same WinEvents Windows raises for a border drag and resizes the dragged window with
  SetWindowPos. The summary says which input was used. When the windows cannot be created at all, drag and soak report
  "skipped: no interactive desktop" and the other scenarios still run.

  The script replaces %LOCALAPPDATA%\MagneticBorders\settings.json with the defaults for the duration of the run and
  restores the original afterwards (-KeepSettings leaves the file alone). No engine may be running when it starts.

  Output: results.json (every run, every metric) and summary.md (medians and ranges) in -OutDir.

.EXAMPLE
  pwsh tools/ci/stage-local.ps1
  pwsh tools/bench/bench.ps1
.EXAMPLE
  pwsh tools/bench/bench.ps1 -Runs 2 -IdleSeconds 20 -SoakDrags 100 -Drags 5
.EXAMPLE
  pwsh tools/bench/bench.ps1 -Engines cpp,rust -Scenarios idle,size
#>
[CmdletBinding()]
param(
    [string]$CppExe,
    [string]$RustExe,
    [string]$DotnetExe,
    [ValidateSet('cpp', 'rust', 'dotnet')][string[]]$Engines = @('cpp', 'rust', 'dotnet'),
    [ValidateSet('x64', 'arm64')][string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }),
    [ValidateRange(1, 100)][int]$Runs = 5,
    [ValidateRange(0, 10)][int]$Warmups = 1,
    [ValidateSet('startup', 'idle', 'drag', 'soak', 'size')][string[]]$Scenarios = @('startup', 'idle', 'drag', 'soak', 'size'),
    [int]$IdleSeconds = 300,
    [int]$Drags = 20,
    [ValidateSet('pair', 'row3', 'grid')][string[]]$Layouts = @('pair', 'grid'),
    [ValidateSet('auto', 'mouse', 'simulated')][string]$DragInput = 'auto',
    [int]$DragAmplitude = 120,
    [int]$StepPx = 4,
    [int]$StepMs = 8,
    [int]$SoakDrags = 2000,
    [int]$SoakPoints = 20,
    [string]$OutDir,
    [switch]$KeepSettings
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts/bench' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

if (-not ('MagneticBorders.Bench.Rig' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'BenchRig.cs')
}

$engineInfo = [ordered]@{
    cpp    = @{ Dir = 'cpp';    File = 'MagneticBorders.Cpp.exe';  Given = $CppExe }
    rust   = @{ Dir = 'rust';   File = 'magnetic-borders-rs.exe';  Given = $RustExe }
    dotnet = @{ Dir = 'dotnet'; File = 'MagneticBorders.Net.exe';  Given = $DotnetExe }
}

function Resolve-EngineExe([string]$name) {
    $info = $engineInfo[$name]
    if ($info.Given) { return (Resolve-Path -LiteralPath $info.Given).Path }
    foreach ($base in @("artifacts/local/$Arch/$($info.Dir)", "artifacts/$($info.Dir)/$Arch")) {
        $candidate = Join-Path $repo (Join-Path $base $info.File)
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "No $name build found. Run: pwsh tools/ci/run.ps1 -Target $($info.Dir) -Steps restore,build,publish -Arch $Arch (then tools/ci/stage-local.ps1), or pass -$((Get-Culture).TextInfo.ToTitleCase($name))Exe."
}

function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    if ($n -eq 0) { return $null }
    if ($n % 2 -eq 1) { return [double]$sorted[($n - 1) / 2] }
    return ([double]$sorted[($n / 2) - 1] + [double]$sorted[$n / 2]) / 2
}

function Get-Slope([double[]]$xs, [double[]]$ys) {
    $n = $xs.Count
    if ($n -lt 2) { return 0.0 }
    $sx = 0.0; $sy = 0.0; $sxy = 0.0; $sxx = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $sx += $xs[$i]; $sy += $ys[$i]; $sxy += $xs[$i] * $ys[$i]; $sxx += $xs[$i] * $xs[$i]
    }
    $den = ($n * $sxx) - ($sx * $sx)
    if ($den -eq 0) { return 0.0 }
    return (($n * $sxy) - ($sx * $sy)) / $den
}

function Test-RigError($errorRecord) {
    $e = $errorRecord.Exception
    while ($e) {
        if ($e -is [MagneticBorders.Bench.RigException]) { return $true }
        $e = $e.InnerException
    }
    return $false
}

function Get-EngineStatus {
    $line = [MagneticBorders.Bench.Pipe]::Request('{"cmd":"status"}', 1000, 2000)
    if (-not $line) { return $null }
    return $line | ConvertFrom-Json
}

function Get-Sample($proc, $clock) {
    $proc.Refresh()
    return @{
        t          = $clock.Elapsed.TotalSeconds
        cpu_ms     = $proc.TotalProcessorTime.TotalMilliseconds
        private_mb = $proc.PrivateMemorySize64 / 1MB
        ws_mb      = $proc.WorkingSet64 / 1MB
        handles    = $proc.HandleCount
        threads    = $proc.Threads.Count
        gdi        = [MagneticBorders.Bench.Proc]::Gdi($proc.Handle)
        user       = [MagneticBorders.Bench.Proc]::User($proc.Handle)
    }
}

function Get-CpuMs($proc) {
    $proc.Refresh()
    return $proc.TotalProcessorTime.TotalMilliseconds
}

function Start-Engine([string]$exe) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path -Parent $exe
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    while ($clock.Elapsed.TotalSeconds -lt 30) {
        if ($proc.HasExited) { throw "The engine exited during start-up with code $($proc.ExitCode)." }
        $line = [MagneticBorders.Bench.Pipe]::Request('{"cmd":"status"}', 5, 1000)
        if ($line) { return @{ Process = $proc; StartupMs = $clock.Elapsed.TotalMilliseconds } }
    }
    $proc.Kill($true)
    throw 'The engine did not answer on the named pipe within 30 seconds.'
}

function Stop-Engine($proc) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $killed = $false
    if (-not $proc.HasExited) {
        $null = [MagneticBorders.Bench.Pipe]::Request('{"cmd":"quit"}', 1000, 2000)
        if (-not $proc.WaitForExit(10000)) {
            $killed = $true
            $proc.Kill($true)
            $null = $proc.WaitForExit(5000)
        }
    }
    $exitCode = if ($proc.HasExited) { $proc.ExitCode } else { -1 }
    $quitMs = $clock.Elapsed.TotalMilliseconds
    $proc.Dispose()
    # Wait until the pipe is gone so the next engine starts clean.
    $gone = [System.Diagnostics.Stopwatch]::StartNew()
    while ($gone.Elapsed.TotalSeconds -lt 10 -and [MagneticBorders.Bench.Pipe]::Request('{"cmd":"status"}', 5, 200)) { Start-Sleep -Milliseconds 100 }
    return @{ QuitMs = $quitMs; ExitCode = $exitCode; Killed = $killed }
}

function Measure-Idle($proc, [int]$seconds) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $samples = [System.Collections.Generic.List[hashtable]]::new()
    $tick = 0
    while ($true) {
        $samples.Add((Get-Sample $proc $clock))
        if ($clock.Elapsed.TotalSeconds -ge $seconds) { break }
        $tick++
        $wait = $tick - $clock.Elapsed.TotalSeconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
    }
    $first = $samples[0]
    $last = $samples[$samples.Count - 1]
    $elapsedMs = [Math]::Max(1.0, ($last.t - $first.t) * 1000)
    $cpuMs = $last.cpu_ms - $first.cpu_ms
    return [ordered]@{
        idle_cpu_ms            = $cpuMs
        idle_cpu_pct           = 100.0 * $cpuMs / $elapsedMs
        idle_private_mb        = Get-Median ([double[]]@($samples | ForEach-Object { $_.private_mb }))
        idle_ws_mb             = Get-Median ([double[]]@($samples | ForEach-Object { $_.ws_mb }))
        idle_private_growth_mb = $last.private_mb - $first.private_mb
        idle_handles           = ($samples | ForEach-Object { $_.handles } | Measure-Object -Maximum).Maximum
        idle_threads           = ($samples | ForEach-Object { $_.threads } | Measure-Object -Maximum).Maximum
        idle_gdi               = ($samples | ForEach-Object { $_.gdi } | Measure-Object -Maximum).Maximum
        idle_user              = ($samples | ForEach-Object { $_.user } | Measure-Object -Maximum).Maximum
    }
}

$script:InputMode = 'mouse'
$script:InputNote = $null

function Open-Rig([string]$layout) {
    $rig = [MagneticBorders.Bench.Rig]::Open($layout)
    $rig.Simulated = ($script:InputMode -eq 'simulated')
    return $rig
}

# Decides once whether real mouse input reaches the test windows.
function Resolve-DragInput {
    if ($DragInput -ne 'auto') { $script:InputMode = $DragInput; return }
    $script:InputMode = 'mouse'
    try { $rig = [MagneticBorders.Bench.Rig]::Open('pair') }
    catch { return }   # no windows at all: the scenarios report the skip
    try {
        $rig.Simulated = $false
        $null = $rig.Drag(40, 8, $StepMs, 100, $false)
    }
    catch {
        if (-not (Test-RigError $_)) { throw }
        $script:InputMode = 'simulated'
        $script:InputNote = "Windows did not deliver injected mouse input ($($_.Exception.GetBaseException().Message)), so drags are simulated: the harness raises EVENT_SYSTEM_MOVESIZESTART and EVENT_SYSTEM_MOVESIZEEND and resizes the dragged window with SetWindowPos."
    }
    finally { $rig.Dispose() }
}

function Measure-Drag($proc, [string]$layout, [int]$count) {
    $metrics = [ordered]@{}
    $rig = Open-Rig $layout
    try {
        $statusBefore = Get-EngineStatus
        $cpuBefore = Get-CpuMs $proc
        $pooled = [System.Collections.Generic.List[int]]::new()
        for ($i = 0; $i -lt $count; $i++) {
            $result = $rig.Drag($DragAmplitude, $StepPx, $StepMs, 300, $true)
            $pooled.AddRange($result.Gaps)
        }
        $cpuAfter = Get-CpuMs $proc
        $statusAfter = Get-EngineStatus
    }
    finally {
        $rig.Dispose()
    }
    $metrics["drag_${layout}_cpu_ms"] = ($cpuAfter - $cpuBefore) / $count
    if ($statusBefore -and $statusAfter -and ($null -ne $statusAfter.PSObject.Properties['glued_drags'])) {
        $metrics["drag_${layout}_glued_pct"] = 100.0 * ([int]$statusAfter.glued_drags - [int]$statusBefore.glued_drags) / $count
    }
    if ($pooled.Count -gt 0) {
        $lag = [MagneticBorders.Bench.Rig]::Summarize($pooled.ToArray())
        $metrics["drag_${layout}_lag_mean_px"] = $lag.Mean
        $metrics["drag_${layout}_lag_p50_px"] = $lag.P50
        $metrics["drag_${layout}_lag_p95_px"] = $lag.P95
        $metrics["drag_${layout}_lag_p99_px"] = $lag.P99
        $metrics["drag_${layout}_lag_max_px"] = $lag.Max
        $metrics["drag_${layout}_disagree_pct"] = $lag.DisagreePct
        $metrics["drag_${layout}_lag_samples"] = $lag.Samples
    }
    return $metrics
}

function Measure-Soak($proc, [int]$total, [int]$points) {
    $metrics = [ordered]@{}
    $rig = Open-Rig 'pair'
    try {
        $warm = [Math]::Min(50, [Math]::Max(5, [int]($total / 10)))
        for ($i = 0; $i -lt $warm; $i++) { $null = $rig.Drag(40, 8, $StepMs, 150, $false) }
        $every = [Math]::Max(1, [int]($total / [Math]::Max(1, $points)))
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $xs = [System.Collections.Generic.List[double]]::new()
        $series = @{ private_mb = [System.Collections.Generic.List[double]]::new(); ws_mb = [System.Collections.Generic.List[double]]::new(); handles = [System.Collections.Generic.List[double]]::new(); gdi = [System.Collections.Generic.List[double]]::new(); user = [System.Collections.Generic.List[double]]::new() }
        $record = {
            param([int]$done)
            $s = Get-Sample $proc $clock
            $xs.Add($done)
            foreach ($key in $series.Keys) { $series[$key].Add([double]$s[$key]) }
        }
        & $record 0
        $cpuBefore = Get-CpuMs $proc
        $statusBefore = Get-EngineStatus
        for ($i = 1; $i -le $total; $i++) {
            $null = $rig.Drag(40, 8, $StepMs, 150, $false)
            if (($i % $every -eq 0) -or ($i -eq $total)) { & $record $i }
        }
        $cpuAfter = Get-CpuMs $proc
        $statusAfter = Get-EngineStatus
    }
    finally {
        $rig.Dispose()
    }
    $xa = $xs.ToArray()
    $metrics.soak_drags = $total
    $metrics.soak_private_kb_per_1k = (Get-Slope $xa $series.private_mb.ToArray()) * 1000 * 1024
    $metrics.soak_ws_kb_per_1k = (Get-Slope $xa $series.ws_mb.ToArray()) * 1000 * 1024
    $metrics.soak_handles_per_1k = (Get-Slope $xa $series.handles.ToArray()) * 1000
    $metrics.soak_gdi_per_1k = (Get-Slope $xa $series.gdi.ToArray()) * 1000
    $metrics.soak_user_per_1k = (Get-Slope $xa $series.user.ToArray()) * 1000
    $metrics.soak_cpu_ms_per_drag = ($cpuAfter - $cpuBefore) / $total
    if ($statusBefore -and $statusAfter -and ($null -ne $statusAfter.PSObject.Properties['glued_drags'])) {
        $metrics.soak_glued_pct = 100.0 * ([int]$statusAfter.glued_drags - [int]$statusBefore.glued_drags) / $total
    }
    return $metrics
}

function Measure-Size([string]$exe) {
    $dir = Split-Path -Parent $exe
    $dirBytes = (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return [ordered]@{
        size_exe_mb = (Get-Item -LiteralPath $exe).Length / 1MB
        size_dir_mb = $dirBytes / 1MB
    }
}

function Invoke-EngineRun([string]$name, [string]$exe, [int]$index, [bool]$warmup, [int]$position) {
    $rec = [ordered]@{
        run      = $index
        warmup   = $warmup
        position = $position
        engine   = $name
        metrics  = [ordered]@{}
        notes    = [System.Collections.Generic.List[string]]::new()
    }
    $m = $rec.metrics
    if ($Scenarios -contains 'size') {
        foreach ($kv in (Measure-Size $exe).GetEnumerator()) { $m[$kv.Key] = $kv.Value }
    }
    $started = Start-Engine $exe
    $proc = $started.Process
    try {
        $m.startup_ms = $started.StartupMs
        Start-Sleep -Seconds 2
        $s = Get-Sample $proc ([System.Diagnostics.Stopwatch]::StartNew())
        $m.startup_private_mb = $s.private_mb
        $m.startup_ws_mb = $s.ws_mb
        $m.startup_handles = $s.handles
        $m.startup_threads = $s.threads
        $m.startup_gdi = $s.gdi
        $m.startup_user = $s.user

        if ($Scenarios -contains 'idle') {
            try { foreach ($kv in (Measure-Idle $proc $IdleSeconds).GetEnumerator()) { $m[$kv.Key] = $kv.Value } }
            catch { $rec.notes.Add("error: idle: $($_.Exception.Message)") }
        }
        $desktopOk = $true
        if ($Scenarios -contains 'drag') {
            foreach ($layout in $Layouts) {
                if (-not $desktopOk) { break }
                try { foreach ($kv in (Measure-Drag $proc $layout $Drags).GetEnumerator()) { $m[$kv.Key] = $kv.Value } }
                catch {
                    if (Test-RigError $_) { $desktopOk = $false; $rec.notes.Add("skipped: no interactive desktop (drag $layout): $($_.Exception.GetBaseException().Message)") }
                    else { $rec.notes.Add("error: drag ${layout}: $($_.Exception.Message)") }
                }
            }
        }
        if (($Scenarios -contains 'soak') -and $desktopOk) {
            try { foreach ($kv in (Measure-Soak $proc $SoakDrags $SoakPoints).GetEnumerator()) { $m[$kv.Key] = $kv.Value } }
            catch {
                if (Test-RigError $_) { $rec.notes.Add("skipped: no interactive desktop (soak): $($_.Exception.GetBaseException().Message)") }
                else { $rec.notes.Add("error: soak: $($_.Exception.Message)") }
            }
        }
        elseif ($Scenarios -contains 'soak') {
            $rec.notes.Add('skipped: no interactive desktop (soak)')
        }
        if ($proc.HasExited) { $rec.notes.Add("error: the engine exited during the run with code $($proc.ExitCode)") }
    }
    finally {
        $stop = Stop-Engine $proc
        $m.quit_ms = $stop.QuitMs
        $rec.exit_code = $stop.ExitCode
        if ($stop.Killed) { $rec.notes.Add('error: the engine did not quit on {"cmd":"quit"} and was killed') }
        elseif ($stop.ExitCode -ne 0) { $rec.notes.Add("error: exit code $($stop.ExitCode) after quit") }
    }
    return $rec
}

function Format-Number($value, [int]$digits) {
    return ([double]$value).ToString("F$digits", [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-Catalog {
    $c = [System.Collections.Generic.List[hashtable]]::new()
    function Add-Row([string]$key, [string]$label, [string]$unit, [int]$digits) { $c.Add(@{ key = $key; label = $label; unit = $unit; digits = $digits }) }
    Add-Row 'size_exe_mb' 'Exe size' 'MB' 2
    Add-Row 'size_dir_mb' 'Folder size' 'MB' 2
    Add-Row 'startup_ms' 'Start to pipe ready' 'ms' 0
    Add-Row 'startup_private_mb' 'Private bytes after start' 'MB' 1
    Add-Row 'startup_ws_mb' 'Working set after start' 'MB' 1
    Add-Row 'startup_handles' 'Handles after start' '' 0
    Add-Row 'startup_threads' 'Threads after start' '' 0
    Add-Row 'idle_cpu_pct' 'Idle CPU (share of one core)' '%' 3
    Add-Row 'idle_cpu_ms' 'Idle CPU time' 'ms' 0
    Add-Row 'idle_private_mb' 'Idle private bytes (median)' 'MB' 1
    Add-Row 'idle_ws_mb' 'Idle working set (median)' 'MB' 1
    Add-Row 'idle_private_growth_mb' 'Idle private bytes growth' 'MB' 2
    Add-Row 'idle_handles' 'Idle handles (max)' '' 0
    Add-Row 'idle_threads' 'Idle threads (max)' '' 0
    Add-Row 'idle_gdi' 'Idle GDI objects (max)' '' 0
    Add-Row 'idle_user' 'Idle USER objects (max)' '' 0
    foreach ($l in $Layouts) {
        Add-Row "drag_${l}_cpu_ms" "Drag ($l): engine CPU per drag" 'ms' 1
        Add-Row "drag_${l}_glued_pct" "Drag ($l): drags the engine glued" '%' 0
        Add-Row "drag_${l}_lag_mean_px" "Drag ($l): gap mean" 'px' 2
        Add-Row "drag_${l}_lag_p50_px" "Drag ($l): gap p50" 'px' 1
        Add-Row "drag_${l}_lag_p95_px" "Drag ($l): gap p95" 'px' 1
        Add-Row "drag_${l}_lag_p99_px" "Drag ($l): gap p99" 'px' 1
        Add-Row "drag_${l}_lag_max_px" "Drag ($l): gap max" 'px' 0
        Add-Row "drag_${l}_disagree_pct" "Drag ($l): samples with a gap" '%' 1
    }
    Add-Row 'soak_private_kb_per_1k' 'Soak: private bytes per 1,000 drags' 'KB' 1
    Add-Row 'soak_ws_kb_per_1k' 'Soak: working set per 1,000 drags' 'KB' 1
    Add-Row 'soak_handles_per_1k' 'Soak: handles per 1,000 drags' '' 2
    Add-Row 'soak_gdi_per_1k' 'Soak: GDI objects per 1,000 drags' '' 2
    Add-Row 'soak_user_per_1k' 'Soak: USER objects per 1,000 drags' '' 2
    Add-Row 'soak_cpu_ms_per_drag' 'Soak: engine CPU per drag' 'ms' 1
    Add-Row 'quit_ms' 'Quit on pipe command' 'ms' 0
    return , $c
}

function New-Summary($records, $meta) {
    $measured = @($records | Where-Object { -not $_.warmup })
    $names = @($Engines)
    $sb = [System.Text.StringBuilder]::new()
    $null = $sb.AppendLine('## Magnetic Borders benchmark')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("$($meta.os), $($meta.arch), $($meta.cpu), $($meta.cores) logical processors, display $($meta.display).")
    $null = $sb.AppendLine("$($meta.runs) measured runs per engine after $($meta.warmups) warm-up, engines interleaved. Each cell is the median with the range of runs in brackets. Lower is better for every row.")
    $null = $sb.AppendLine("Idle $($meta.idle_seconds) s, $($meta.drags) drags per layout, soak $($meta.soak_drags) drags. Drag input: $($meta.drag_input).")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('| Metric | ' + (($names | ForEach-Object { $_ }) -join ' | ') + ' |')
    $null = $sb.AppendLine('|---|' + (($names | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($row in (New-Catalog)) {
        $cells = foreach ($n in $names) {
            $vals = [double[]]@($measured | Where-Object { $_.engine -eq $n -and $_.metrics.Contains($row.key) } | ForEach-Object { [double]$_.metrics[$row.key] })
            if ($vals.Count -eq 0) { 'n/a' }
            else {
                $med = Format-Number (Get-Median $vals) $row.digits
                $lo = Format-Number ($vals | Measure-Object -Minimum).Minimum $row.digits
                $hi = Format-Number ($vals | Measure-Object -Maximum).Maximum $row.digits
                if ($lo -eq $hi) { $med } else { "$med [$lo to $hi]" }
            }
        }
        if (@($cells | Where-Object { $_ -ne 'n/a' }).Count -eq 0) { continue }
        $label = if ($row.unit) { "$($row.label) ($($row.unit))" } else { $row.label }
        $null = $sb.AppendLine("| $label | " + ($cells -join ' | ') + ' |')
    }
    $notes = @()
    if ($script:InputNote) { $notes += $script:InputNote }
    $notes += @($records | ForEach-Object { $e = $_.engine; foreach ($t in $_.notes) { "${e}: $t" } } | Sort-Object -Unique)
    if ($notes.Count -gt 0) {
        $null = $sb.AppendLine()
        $null = $sb.AppendLine('Notes:')
        $null = $sb.AppendLine()
        foreach ($t in $notes) { $null = $sb.AppendLine("- $t") }
    }
    return $sb.ToString()
}

# --- Main ---------------------------------------------------------------------------------------------------------

$exes = [ordered]@{}
foreach ($n in $Engines) { $exes[$n] = Resolve-EngineExe $n }

if ([MagneticBorders.Bench.Pipe]::Request('{"cmd":"status"}', 200, 500)) {
    throw 'A Magnetic Borders engine is already running. Quit it first (tray menu, Quit) so the benchmark controls the only instance.'
}

$dataDir = Join-Path $env:LOCALAPPDATA 'MagneticBorders'
$settingsFile = Join-Path $dataDir 'settings.json'
$originalSettings = $null
if (-not $KeepSettings) {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    if (Test-Path -LiteralPath $settingsFile) { $originalSettings = [System.IO.File]::ReadAllBytes($settingsFile) }
    $defaults = '{"schema":1,"enabled":true,"glue":{"enabled":true,"tolerance_px":12},"snap":{"enabled":true,"distance_px":16,"breakaway_px":24,"max_speed":600,"screen_edges":true,"windows":true},"ui":{"flyout_backdrop":"translucent"},"excluded_apps":[],"start_at_login":false}'
    [System.IO.File]::WriteAllText($settingsFile, $defaults, [System.Text.UTF8Encoding]::new($false))
}

$cpuName = try { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() } catch { 'unknown CPU' }
$meta = [ordered]@{
    date_utc     = (Get-Date).ToUniversalTime().ToString('o')
    os           = [System.Environment]::OSVersion.VersionString
    arch         = $Arch
    cpu          = $cpuName
    cores        = [System.Environment]::ProcessorCount
    display      = [MagneticBorders.Bench.Rig]::Describe()
    runs         = $Runs
    warmups      = $Warmups
    scenarios    = $Scenarios
    idle_seconds = $IdleSeconds
    drags        = $Drags
    layouts      = $Layouts
    soak_drags   = $SoakDrags
    drag_input   = 'n/a'
    exes         = [ordered]@{}
}
$repoPattern = [regex]::Escape($repo)
foreach ($n in $Engines) { $meta.exes[$n] = $exes[$n] -replace $repoPattern, '.' }

$records = [System.Collections.Generic.List[object]]::new()
[MagneticBorders.Bench.Proc]::BeginHighResolutionTimer()
try {
    if (($Scenarios -contains 'drag') -or ($Scenarios -contains 'soak')) {
        Resolve-DragInput
        $meta.drag_input = $script:InputMode
        Write-Host "Drag input: $script:InputMode"
        if ($script:InputNote) { Write-Host $script:InputNote }
    }
    $total = $Warmups + $Runs
    for ($i = 0; $i -lt $total; $i++) {
        $isWarmup = $i -lt $Warmups
        $label = if ($isWarmup) { "warm-up $($i + 1)" } else { "run $($i - $Warmups + 1) of $Runs" }
        $shift = $i % $Engines.Count
        $order = for ($k = 0; $k -lt $Engines.Count; $k++) { $Engines[($k + $shift) % $Engines.Count] }
        Write-Host "== $label, order: $($order -join ', ')" -ForegroundColor Cyan
        $position = 0
        foreach ($n in $order) {
            $position++
            Write-Host "   $n ..." -NoNewline
            try {
                $rec = Invoke-EngineRun $n $exes[$n] ($i - $Warmups + 1) $isWarmup $position
            }
            catch {
                $rec = [ordered]@{ run = ($i - $Warmups + 1); warmup = $isWarmup; position = $position; engine = $n; metrics = [ordered]@{}; notes = [System.Collections.Generic.List[string]]::new() }
                $rec.notes.Add("error: $($_.Exception.Message)")
            }
            $records.Add($rec)
            $state = if (@($rec.notes | Where-Object { $_ -like 'error:*' }).Count -gt 0) { 'errors' } else { 'ok' }
            Write-Host " $state"
            Start-Sleep -Seconds 2
        }
    }
}
finally {
    [MagneticBorders.Bench.Proc]::EndHighResolutionTimer()
    if (-not $KeepSettings) {
        if ($null -ne $originalSettings) { [System.IO.File]::WriteAllBytes($settingsFile, $originalSettings) }
        else { Remove-Item -LiteralPath $settingsFile -Force -ErrorAction SilentlyContinue }
    }
}

$summary = New-Summary $records $meta
[System.IO.File]::WriteAllText((Join-Path $OutDir 'summary.md'), $summary, [System.Text.UTF8Encoding]::new($false))
[ordered]@{ meta = $meta; runs = $records } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir 'results.json') -Encoding utf8
Write-Host ''
Write-Host $summary
Write-Host "Wrote $(Join-Path $OutDir 'results.json') and $(Join-Path $OutDir 'summary.md')"

$failed = @($records | Where-Object { @($_.notes | Where-Object { $_ -like 'error:*' }).Count -gt 0 })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) engine run(s) reported errors; see the notes above." -ForegroundColor Red
    exit 1
}
