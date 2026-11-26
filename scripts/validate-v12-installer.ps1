[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][Alias('SeedDirectory')][string]$SeedDataPath,
    [string]$SetupPath,
    [string]$PreviousSetupPath,
    [Parameter(Mandatory=$true)][string]$ExpectedLocalAppData,
    [switch]$OnlyPackageBoundaries,
    [switch]$SkipInterruption
)
# Exactly the existing guarded real lifecycle; this wrapper selects only the new upgrade pair.
& (Join-Path $PSScriptRoot 'validate-installer.ps1') @PSBoundParameters -Version '1.2.0'
