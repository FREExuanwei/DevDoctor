param(
  [Parameter(Mandatory=$true)][string]$DataRoot,
  [Parameter(Mandatory=$true)][string]$ExpectedManifest,
  [Parameter(Mandatory=$true)][string]$OutputPath,
  [ValidateSet('1.0.0','1.1.0')][string]$OldVersion = '1.0.0',
  [ValidateSet('1.1.0','1.2.0')][string]$Version = '1.1.0' 
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($DataRoot, $ExpectedManifest, $OutputPath)) {
  if (-not [IO.Path]::GetFullPath($path).StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) { throw "Upgrade verification paths must be workspace-owned artifacts: $path" }
  for ($ancestor = [IO.Path]::GetFullPath($path); $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Verification path contains a reparse point: $ancestor" }
  }
}
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
$verifier = Join-Path $workspace 'tests/DevDoctor.UpgradeSmoke/bin/Release/net10.0-windows/DevDoctor.UpgradeSmoke.dll'
if (-not (Test-Path -LiteralPath $verifier)) { throw 'Build tests/DevDoctor.UpgradeSmoke in Release before verification (the verifier does not start a competing source build).' }
$env:DOTNET_ROOT = Join-Path $workspace '.tools/dotnet'
$env:DEVDOCTOR_DATA_ROOT = [IO.Path]::GetFullPath($DataRoot)
& $dotnet $verifier ([IO.Path]::GetFullPath($DataRoot)) ([IO.Path]::GetFullPath($ExpectedManifest)) ([IO.Path]::GetFullPath($OutputPath)) $OldVersion $Version
if ($LASTEXITCODE -ne 0) { throw "Upgrade application-level verification failed; see $OutputPath" }
$result = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
if ($result.passed -ne $true) { throw "Upgrade verification did not return passed=true: $OutputPath" }
