#Requires -Version 7
<#
.SYNOPSIS
  Starts the published settings app with --smoke and fails unless it exits with code 0.

.DESCRIPTION
  The settings app opens its full window and its flyout, lets each load, and exits. An unhandled exception exits
  with 1 and a timeout with 2; the exception text is in settings.log in a temporary folder, which this script prints.
  This catches start-up crashes that only the published Native AOT build has.

.EXAMPLE
  pwsh tools/ci/smoke-settings.ps1 -ExePath artifacts/settings/x64/MagneticBorders.Settings.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExePath,
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'

$exe = (Resolve-Path -LiteralPath $ExePath).Path
$dir = Join-Path ([IO.Path]::GetTempPath()) ("mb-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
try {
    Write-Host "Smoke test: $exe --smoke"
    $p = Start-Process -FilePath $exe -ArgumentList @('--smoke', "`"$dir`"") -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        $p.Kill($true)
        throw "The settings app did not exit within $TimeoutSeconds seconds."
    }
    $log = Join-Path $dir 'settings.log'
    if (Test-Path $log) { Get-Content $log | ForEach-Object { Write-Host $_ } }
    if ($p.ExitCode -ne 0) { throw "The settings app smoke test failed with exit code $($p.ExitCode)." }
    Write-Host 'Smoke test passed: the settings window and the flyout opened and closed.'
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
