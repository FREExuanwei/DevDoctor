[CmdletBinding()]
param([switch]$SkipCompile)
& (Join-Path $PSScriptRoot 'check-v11-installer.ps1') -Version '1.2.0' -SkipCompile:$SkipCompile
