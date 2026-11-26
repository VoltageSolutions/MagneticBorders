# tools/ci

`run.ps1` is the one way to build, check and package each part of the repo. The local build watcher and the GitHub workflows both call it, so results match.

```powershell
pwsh tools/ci/run.ps1 -Target <cpp|rust|dotnet|settings|docs> -Steps <steps> [-Arch x64|arm64] [-Ci]
```

| Step | Meaning |
|---|---|
| `restore` | Fetch pinned dependencies from lock files (fails if a lock file would change). |
| `lint` | Formatting and static analysis. Read-only: it reports, it never rewrites files. |
| `build` | Release build for `-Arch`, warnings as errors. |
| `test` | Unit tests and every fixture in `tests/fixtures/` (x64 only; ARM64 is cross-built). |
| `coverage` | Tests with coverage, writes `artifacts/coverage/<target>.cobertura.xml`, fails below 40% line coverage. |
| `publish` | Release output into `artifacts/<target>/<arch>/`. |

Each target lives in `targets/<target>.ps1` and defines only the steps that apply to it as `Invoke-<Step>` functions taking the context from `common.ps1`. `-Ci` makes restores locked and builds deterministic (`ContinuousIntegrationBuild`, `/Brepro`, `--locked`).

Run `pwsh tools/setup-dev.ps1` once to install the tools these steps need.
