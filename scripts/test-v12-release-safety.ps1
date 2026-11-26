[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/release-evidence-v1.2.0'
$checks = [Collections.Generic.List[object]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Release safety regression: $Name" }
    $checks.Add(@{name=$Name;passed=$true})
}
function Reject([scriptblock]$Action, [string]$Expected, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $_.Exception.Message.Contains($Expected) }
    Check $rejected $Name
}
$baseline = Join-Path $root 'artifacts/baselines/v1.1.0'
$before = @{}
foreach ($entry in (Get-Content (Join-Path $baseline 'SHA256-v1.1.0.json') -Raw | ConvertFrom-Json)) {
    $path = Join-Path $baseline $entry.Name
    $before[$path] = (Get-FileHash -LiteralPath $path).Hash
    Check ($before[$path] -eq $entry.SHA256 -and (Get-Item $path).Length -eq $entry.Bytes) "Archived v1.1 package bytes/hash: $($entry.Name)"
}
Reject { & (Join-Path $PSScriptRoot 'release.ps1') -Version '1.1.0' } 'Current source owns only' 'Current source cannot overwrite old release outputs'
Reject { & (Join-Path $PSScriptRoot 'package-source.ps1') -Version '1.1.0' } 'Current source owns only' 'Current source cannot overwrite old source ZIP'
Reject { & (Join-Path $PSScriptRoot 'validate-installer.ps1') -SeedDataPath (Join-Path $root 'artifacts/v1.2-upgrade-seed') -Version '1.2.0' } 'explicitly verified real -ExpectedLocalAppData' 'Lifecycle rejects omitted real-user profile before any installation'
Reject { & (Join-Path $PSScriptRoot 'verify-v11-upgrade-data.ps1') -DataRoot $root -ExpectedManifest (Join-Path $root 'artifacts/v1.2-upgrade-seed/old-app-verification.json') -OutputPath (Join-Path $evidence 'must-not-exist.json') -OldVersion '1.1.0' -Version '1.2.0' } 'must be workspace-owned artifacts' 'Upgrade verifier rejects data outside controlled artifacts'
$manifest = Join-Path $root 'artifacts/v1.2-upgrade-seed/old-app-verification.json'
$seedHash = (Get-FileHash $manifest).Hash
Reject { & (Join-Path $PSScriptRoot 'create-v11-upgrade-seed.ps1') } 'Existing old seed retained' 'Seed producer refuses to replace genuine old data'
Check ((Get-FileHash $manifest).Hash -eq $seedHash) 'Rejected seed regeneration preserves exact provenance'
$sourceManifest = Get-Content (Join-Path $evidence 'source-manifest.json') -Raw | ConvertFrom-Json
$zip = [IO.Compression.ZipFile]::OpenRead($sourceManifest.zip)
try {
    Check ($zip.Entries.Count -eq $sourceManifest.entries.Count) 'Source ZIP and entry manifest have identical counts'
    foreach ($entry in $zip.Entries) {
        Check ($entry.FullName -notmatch '^docs/v12-work(/|$)|(^|/)(bin|obj|artifacts|user-data|closed-seed|legacy-runtime|TestResults|\.tools|node_modules|__pycache__|cache)(/|$)|\.(dll|exe|pdb|sqlite3?|db|zip|cache|tmp|log)$') "Source exclusion boundary: $($entry.FullName)"
        $expected = @($sourceManifest.entries | Where-Object path -eq $entry.FullName)
        Check ($expected.Count -eq 1 -and $entry.Length -eq $expected[0].bytes) "Source ZIP entry identity/length: $($entry.FullName)"
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        Check ($hash -eq $expected[0].sha256) "Source ZIP independent entry SHA256: $($entry.FullName)"
    }
} finally { $zip.Dispose() }
foreach ($path in $before.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $before[$path]) "Old package unchanged after safety regressions: $([IO.Path]::GetFileName($path))" }
[ordered]@{passed=$true;utc=[DateTime]::UtcNow.ToString('o');checks=$checks} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'release-safety-checks.json') -Encoding utf8
Write-Output "PASS $($checks.Count) release safety/source-entry checks; no installer/current App launched."
