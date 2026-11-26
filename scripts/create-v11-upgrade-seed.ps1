param([string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/v1.2-upgrade-seed'))
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not $OutputRoot.StartsWith(([IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Seed output must be inside workspace artifacts.' }
for ($ancestor = $OutputRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
  if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Seed path contains a reparse point: $ancestor" }
}
if ((Test-Path -LiteralPath (Join-Path $OutputRoot 'user-data')) -or (Test-Path -LiteralPath (Join-Path $OutputRoot 'old-app-verification.json'))) { throw 'Existing old seed retained. Choose a NEW output directory to rerun.' }
$baseline = Join-Path $workspace 'artifacts/baselines/v1.1.0'
foreach ($entry in (Get-Content -LiteralPath (Join-Path $baseline 'SHA256-v1.1.0.json') -Raw | ConvertFrom-Json)) {
  $path = Join-Path $baseline $entry.Name
  if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256 -or (Get-Item -LiteralPath $path).Length -ne $entry.Bytes) { throw "Old official archive differs: $path" }
}
$legacyContainer = Join-Path $OutputRoot 'legacy-runtime'
$legacy = Join-Path $legacyContainer 'DevDoctor-v1.1.0-win-x64-portable'
if (Test-Path -LiteralPath $legacyContainer) { throw 'Existing extracted runtime retained. Choose a NEW seed output directory.' }
Expand-Archive -LiteralPath (Join-Path $baseline 'DevDoctor-v1.1.0-win-x64-portable.zip') -DestinationPath $legacyContainer
$env:DOTNET_ROOT = Join-Path $workspace '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools/nuget'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet build (Join-Path $workspace 'tests/DevDoctor.LegacySeedSmoke/DevDoctor.LegacySeedSmoke.csproj') -c Release "-p:LegacyRuntime=$legacy" -p:UseSharedCompilation=false -nodeReuse:false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Legacy harness build failed.' }
Copy-Item -LiteralPath (Join-Path $workspace 'tests/DevDoctor.LegacySeedSmoke/bin/Release/net10.0-windows/DevDoctor.LegacySeedSmoke.dll') -Destination $legacy
$env:DEVDOCTOR_DATA_ROOT = Join-Path $OutputRoot 'user-data'
& $dotnet exec --runtimeconfig (Join-Path $legacy 'DevDoctor.runtimeconfig.json') --depsfile (Join-Path $legacy 'DevDoctor.deps.json') (Join-Path $legacy 'DevDoctor.LegacySeedSmoke.dll') $OutputRoot 1.1.0
if ($LASTEXITCODE -ne 0) { throw "Old production seed generation failed; inspect $OutputRoot/failure.txt" }
$manifest = Get-Content -LiteralPath (Join-Path $OutputRoot 'old-app-verification.json') -Raw | ConvertFrom-Json
if ($manifest.RecordCount -lt 2) { throw 'Old production scan did not create the required real histories.' }
# Compare the actual loaded old DLLs/executable against the verified official ZIP, not just their version labels.
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $baseline 'DevDoctor-v1.1.0-win-x64-portable.zip'))
try {
  foreach ($binary in @($manifest.LoadedAssemblies) + @($manifest.Executable)) {
    $name = [IO.Path]::GetFileName($binary.Path)
    $entries = @($zip.Entries | Where-Object { [IO.Path]::GetFileName($_.FullName) -eq $name })
    if ($entries.Count -ne 1) { throw "Ambiguous archived binary: $name" }
    $stream = $entries[0].Open()
    try { $archivedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    if ($archivedHash -ne $binary.Sha256 -or (Get-FileHash -LiteralPath $binary.Path).Hash -ne $archivedHash) { throw "Loaded old binary differs from official ZIP: $name" }
  }
} finally { $zip.Dispose() }
if ($manifest.SourceVersion -ne '1.1.0' -or @($manifest.Records | Where-Object {$_.SoftwareVersion -ne '1.1.0' -or $_.RuleVersion -ne 2}).Count) { throw 'Seed version/rule provenance is not genuine v1.1.' }
Write-Output (Join-Path $OutputRoot 'old-app-verification.json')
