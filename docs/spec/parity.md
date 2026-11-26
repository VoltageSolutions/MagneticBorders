# Parity

The C++, Rust, and C# engines must behave identically. Shared `.layout` fixtures define the geometry, the layer layout is the same in all three, and a comparison measures what differs: memory, speed, and clarity. A behavior change lands in all three engines in the same change.

## Why three engines

Magnetic Borders is built as the same engine in three languages to compare them for clarity, memory use and responsiveness. C++ is the reference build and the PowerToys candidate, since FancyZones and the PowerToys module interface are C++.

## Behavior

| ID | Rule |
|---|---|
| PAR-001 | All three engines must pass every fixture in `tests/fixtures/`. |
| PAR-002 | A behavior change must land in all three engines in the same change, with a fixture when it is geometric. |
| PAR-003 | The three engines must implement the same [contract](contract.md), so the settings app works with each. |
| PAR-004 | The three engines must log the same events with the same shape (see RESIZE-050 to RESIZE-054). |
| PAR-005 | The three engines must use the same mutex name, pipe name, data directory and registry value. |

## Structure

| ID | Rule |
|---|---|
| PAR-010 | Each engine must have the folders `core/`, `app/`, `win32/` and `host/`, plus `fixture-runner/` and unit tests beside the code. |
| PAR-011 | Dependencies must point inward only: core, then app, then win32, then host. |
| PAR-012 | `core/` and `app/` must not use Win32 types. |
| PAR-013 | Ports must have the same names across languages, adapted to the language's casing. |
| PAR-014 | Each engine must have exactly one global slot for routing the hook callback. |

## Fixtures

| ID | Rule |
|---|---|
| PAR-020 | Each engine must ship a `fixture-runner` that accepts a file or a folder and prints `PASS <file>` or `FAIL <file>: <reason>`, and exits 1 on any failure. |
| PAR-021 | `fixture-runner --print <file>` must print the final frames as `id x,y WxH`, sorted by id, so the three outputs can be diffed. |
| PAR-022 | The comparison step must report any difference between the three engines' printed output for any fixture as a failure. |
| PAR-023 | A fixture's expected output must not change without updating the matching spec rule. |

## Quality gates

| ID | Rule |
|---|---|
| PAR-030 | C++ must build with `/W4` and no warnings. |
| PAR-031 | Rust must pass `cargo clippy` with no warnings. |
| PAR-032 | C# must build with no IL2xxx or IL3xxx (Native AOT) warnings. |
| PAR-033 | Each engine's unit tests must cover settings parsing, pipe request handling, and the controller with fake ports (trailing flush, Settle learning a minimum, Verify re-applying a snap-back). |

## Performance comparison

| ID | Rule |
|---|---|
| PAR-040 | Comparisons must run the same scripted workload on all three engines. |
| PAR-041 | A comparison must report at least working set, handle and GDI counts, idle CPU, neighbor lag (p50, p95, max), startup time and exe size. |
| PAR-042 | Neighbor lag should be under 20 ms at p95 for the test apps. |
| PAR-043 | The drag path should not allocate after the first event. |
