# Docs site (Zensical). Steps: restore, lint (strict build), build, publish (site into artifacts/docs/x64).
# Zensical builds into site/ (git-ignored); publish moves it into the artifacts folder.

function Invoke-Restore($ctx) {
    Assert-Tool python 'Install Python 3.12+ (tools/setup-dev.ps1).'
    Exec { python -m pip install --disable-pip-version-check -q -r (Join-Path $ctx.RepoRoot 'docs\requirements.txt') }
}

# A strict build fails on broken links, missing pages, and nav errors, so it doubles as the docs linter.
function Invoke-Lint($ctx) {
    Exec { python -m zensical build --clean --strict }
    Remove-Item -Recurse -Force (Join-Path $ctx.RepoRoot 'site') -ErrorAction SilentlyContinue
}

function Invoke-Build($ctx) { Invoke-Lint $ctx }

function Invoke-Publish($ctx) {
    Exec { python -m zensical build --clean --strict }
    Remove-Item -Recurse -Force $ctx.ArtifactsDir -ErrorAction SilentlyContinue
    Move-Item (Join-Path $ctx.RepoRoot 'site') $ctx.ArtifactsDir
}
