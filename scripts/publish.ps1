# Keep the legacy entry point, routing it to the guarded current-version release.
# Never publish the current source into a previous version's delivery directory.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'release.ps1') @args
