# Benchmarks

The same engine exists in C++, Rust, and C#. The benchmark harness runs all three through one scripted workload, so the choice of which build to keep can rest on numbers instead of impressions. The harness lives in `tools/bench/`.

## What is measured

Every run starts one engine, waits until the named pipe answers `{"cmd":"status"}`, runs the scenarios, and ends the engine with `{"cmd":"quit"}`. Engines take turns in a rotating order (cpp, rust, dotnet; then rust, dotnet, cpp; and so on), so drift in the machine, such as thermal throttling or a background update, does not favor one build. The first run is a warm-up and is left out of the summary.

| Scenario | What it records | Why it matters |
|---|---|---|
| Startup | Milliseconds from process start until the pipe answers. Private bytes, working set, handles, and threads two seconds later. | The engine starts at sign-in, so a slow or heavy start is felt on every boot. |
| Idle | CPU time, private bytes, working set, handles, threads, GDI objects, and USER objects, sampled every second (five minutes by default). | The engine is always running. Idle cost is the cost most people pay. |
| Drag | Scripted drags of the border between test windows, sent with `SendInput`. Engine CPU time per drag, and the gap in pixels between the dragged edge and the edges that must follow it. | Lag during a drag is what people see. |
| Soak | Thousands of short drags. The slope of private bytes, handles, GDI objects, and USER objects per 1,000 drags. | A leak that is invisible in one drag shows up here. |
| Size | Exe size and folder size on disk. | Download size and the footprint of the shipped build. |

### How the gap is measured

The harness opens plain top-level windows at exact visible rects (the extended frame bounds, as the engine uses). The `pair` layout has two windows side by side, `row3` has three in a row, and `grid` has four in a 2 by 2 block, where one border has three windows on its line. The harness presses on the resize border of the first window, moves the cursor out and back in steps of 4 px every 8 ms, and releases. While the button is down, a separate thread reads the visible rects of every window as fast as it can. At each reading, the gap is the largest distance between the dragged edge and an edge that should share its line. A gap of 0 means the windows move as one. Reported values are the mean, median (p50), p95, p99, and maximum of all readings, and the share of readings with a gap.

When Windows does not deliver injected mouse input, as on some hosted CI runners, the harness simulates the drag instead (`-DragInput simulated` forces this). It raises the same two window events that Windows raises at the start and the end of a border drag (`EVENT_SYSTEM_MOVESIZESTART` and `EVENT_SYSTEM_MOVESIZEEND`) and resizes the dragged window with `SetWindowPos` in between. The engine sees the same event stream, but Windows' own resize loop does not run. The summary names the input that was used.

The same drags are counted by the engine. The `glued_drags` field of the pipe status proves the engine engaged: the table shows the share of drags that it glued.

The gap includes the time the window system needs to apply a move, which is the same for every engine. Compare engines to each other. Do not read the absolute gap as engine latency.

### What is not measured

Context switches and wake-ups are not recorded, because Windows does not expose them per process without an event trace. Idle CPU time and thread counts stand in for them. Time to the first tray icon is not recorded.

## Run it on your PC

Use a quiet machine. Close other apps, plug in the power, and leave the mouse alone: the drag scenarios take over the cursor.

1. Quit any running Magnetic Borders. The harness refuses to start when an engine is running, because the engines share one pipe name and one single-instance mutex.
2. Build and stage the three engines.

    ```powershell
    foreach ($t in 'cpp', 'rust', 'dotnet') { pwsh tools/ci/run.ps1 -Target $t -Steps restore,build,publish -Arch x64 }
    pwsh tools/ci/stage-local.ps1
    ```

3. Run the benchmark.

    ```powershell
    pwsh tools/bench/bench.ps1
    ```

    The defaults are five measured runs per engine, five minutes of idle, 20 drags in each layout, and a soak of 2,000 drags. That takes about an hour and a half. For a quick check, use the short form, which the build watcher also runs for a `compare` request.

    ```powershell
    pwsh tools/compare.ps1
    ```

The harness uses the builds in `artifacts/local/x64/` first and `artifacts/<target>/x64/` second. Pass `-CppExe`, `-RustExe`, and `-DotnetExe` to name other files. Other useful parameters are `-Engines`, `-Runs`, `-Scenarios`, `-IdleSeconds`, `-Drags`, `-Layouts`, `-SoakDrags`, and `-OutDir`.

For the length of the run, the harness replaces `%LOCALAPPDATA%\MagneticBorders\settings.json` with the default settings and puts the original back at the end. Pass `-KeepSettings` to leave the file alone.

Results go to `artifacts/bench/`: `results.json` has every run and every metric, and `summary.md` has the table.

### On a virtual machine or a second PC

A second PC is a good second opinion, and a virtual machine is useful for repeatable runs, with caveats. Use a signed-in, unlocked desktop session. A locked session or a disconnected remote desktop session has no input desktop, and the harness falls back to simulated drags or, when it cannot create windows, reports `skipped: no interactive desktop`. Give the machine at least two cores and run it at 100 percent display scaling. Copy `tools/bench/`, `tools/compare.ps1`, and the three published engine folders (`cpp`, `rust`, and `dotnet`), then pass the exe paths with `-CppExe`, `-RustExe`, and `-DotnetExe`. PowerShell 7 is the only requirement. There are no packages to install.

## Run it in GitHub Actions

The **Benchmark** workflow (`.github/workflows/bench.yml`) builds the three engines for x64 and ARM64, runs the harness on a hosted x64 runner and on a Windows 11 on ARM runner, and puts the table on the run page. Start it from Actions with **Run workflow**, and pick the number of runs, the idle seconds, and the soak length. A pull request that changes `tools/bench/` runs it with short settings.

Hosted runners are shared virtual machines, so absolute numbers are noisy. Use them for trends between commits and for leaks, which show up as a steady slope in handles, GDI objects, USER objects, or private bytes, whatever the speed of the machine. Use a quiet local machine for the numbers that decide anything. If a runner cannot create windows at all, the drag and soak rows say `skipped: no interactive desktop`, and the rest of the table is still filled in.

## Reading the results

- Each cell is the median of the measured runs, with the lowest and highest run in brackets. A wide bracket means the machine was noisy: run again before drawing a conclusion.
- Lower is better in every row.
- Differences of a few percent in startup, memory, or CPU time are noise. Look for a factor of two.
- A soak slope close to zero is healthy. A steady positive slope in handles, GDI objects, or USER objects is a leak, even a small one, because the engine runs for weeks.
- A drag row with a gap of 0 at p95 and a maximum of a few pixels means the neighbors keep up. A large maximum with a small p95 is a single stall, such as a garbage collection or a busy window of another app.
- `glued` below 100 percent means some drags were not coupled. Check the window layout and the engine log before trusting the lag numbers.

## Other factors in the decision

Speed and size are only part of the choice of which engine to keep. The numbers sit next to these questions.

- **Maintainability.** How easily can a new contributor read, change, and test the code? Which language will the maintainer still want to use in five years?
- **Memory safety.** Rust and C# rule out whole classes of memory bugs by design. The C++ build relies on review, clang-tidy, and tests.
- **PowerToys candidacy.** PowerToys is a C++ and C# code base. A C++ engine fits the existing utilities and their conventions most directly. See [Architecture](architecture.md) for how the engine and the settings app are split.
- **Build complexity.** Toolchain installs, build time, lock files, and how reproducible the build is. See the [CI scripts](https://github.com/VoltageSolutions/MagneticBorders/tree/main/tools/ci).
- **CodeQL findings.** The number and severity of open alerts for each language, and how much the queries for each language can see.
