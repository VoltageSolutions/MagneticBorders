---
description: Commit all changes (and optionally push) through the Windows build watcher
argument-hint: [push]
---
1. Check `git status` and summarise what will be committed. Stop if anything private (logs, local paths, `.build/`, `artifacts/`) would be included.
2. Write `.build/commit-msg.txt`: a short subject line, a blank line, a body that says what changed, and a `Co-Authored-By` trailer. No links to AI sessions.
3. Write `.build/request-git.json` with targets `["commit"]`, or `["commit","push"]` when `push` is in `$ARGUMENTS`, and wait for `.build/status-git.json`.
4. Report the commit hash and whether the push succeeded.
