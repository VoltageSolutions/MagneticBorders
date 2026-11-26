---
description: Build and test one or more targets through the Windows build watcher
argument-hint: <cpp|rust|dotnet|settings ...> [publish]
---
Build the targets in `$ARGUMENTS` (default: cpp rust dotnet settings) through `tools/build-watcher.ps1`, as AGENTS.md describes:

1. For each target, write `.build/request-<target>.json` with a fresh id, `"test": true`, and `"publish": true` when `publish` is in the arguments.
2. Poll each `.build/status-<target>.json` until `state` is `done` with that id.
3. Report one line per target: pass or fail, test counts from the log, and the first errors when it failed. Do not paste whole logs.
