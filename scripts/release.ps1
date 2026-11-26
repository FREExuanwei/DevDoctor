[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [ValidateSet('1.1.0','1.2.0')][string]$Version = '1.2.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -ne '1.2.0') { throw 'Current source owns only v1.2.0 outputs; use preserved v1.1 source to recreate v1.1 packages.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
$payload = Join-Path $artifactRoot "DevDoctor-v$Version-win-x64"
$portable = Join-Path $artifactRoot "DevDoctor-v$Version-win-x64-portable"
$zipPath = "$portable.zip"
$setupPath = Join-Path $artifactRoot "DevDoctor-v$Version-win-x64-Setup.exe"
$releaseEvidence = Join-Path $artifactRoot "release-evidence-v$Version"
foreach($pin in @(
    @{Path='installer\languages\ChineseSimplified.isl';Hash='6753BE2C5E2740D859900FD902824DB2EC568DA5C5B52486524C9762D778B0B0'},
    @{Path='installer\languages\Inno-Setup-Translation-LICENSE.txt';Hash='2E5346868C2A18434489824E11D65C3031620F792FEFC415D05F19CD441ABF5C'}
)) {
    if((Get-FileHash -LiteralPath (Join-Path $projectRoot $pin.Path) -Algorithm SHA256).Hash -ne $pin.Hash) {throw "Pinned official installer translation or license changed: $($pin.Path)"}
}
New-Item -ItemType Directory -Path $artifactRoot,$releaseEvidence -Force | Out-Null
function Assert-OwnedArtifactPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to modify non-artifact path: $absolute" }
    if ((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing to move a reparse point: $absolute" }
    return $absolute
}
function Archive-GeneratedPath([string]$Path) {
    $absolute = Assert-OwnedArtifactPath $Path
    if (Test-Path -LiteralPath $absolute) {
        $archiveRoot = Join-Path $artifactRoot ('release-archive\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
        New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
        Move-Item -LiteralPath $absolute -Destination (Join-Path $archiveRoot ([IO.Path]::GetFileName($absolute)))
    }
}
if (-not $SkipPublish) {
    $staging = Join-Path $artifactRoot ('.release-staging-' + [Guid]::NewGuid().ToString('N'))
    & "$PSScriptRoot\dotnet.ps1" publish (Join-Path $projectRoot 'src\DevDoctor.App\DevDoctor.App.csproj') --configuration Release --runtime win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:DebugType=none' --output $staging
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed ($LASTEXITCODE); prior delivery is retained." }
    Archive-GeneratedPath $payload
    Move-Item -LiteralPath (Assert-OwnedArtifactPath $staging) -Destination $payload
}
foreach ($required in @('DevDoctor.exe','DevDoctor.dll','DevDoctor.deps.json','DevDoctor.runtimeconfig.json','coreclr.dll','hostfxr.dll','PresentationFramework.dll','e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Self-contained Release payload is missing $required." }
}
if (Test-Path -LiteralPath (Join-Path $payload 'portable.mode')) { throw 'Installed payload must not contain portable.mode.' }
$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $payload 'DevDoctor.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfiguration.runtimeOptions.includedFrameworks) { throw 'Payload is not self-contained.' }
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'DevDoctor.dll')).Version.ToString()
if (-not $assemblyVersion.StartsWith("$Version.")) { throw "Payload version $assemblyVersion does not match $Version." }

# Package licenses for every runtime NuGet library, using exact dependency versions.
$licenseOutput = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
$dependencies = Get-Content -LiteralPath (Join-Path $payload 'DevDoctor.deps.json') -Raw | ConvertFrom-Json
$licenseRows = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $dependencies.libraries.PSObject.Properties | Sort-Object Name) {
    if ($entry.Value.type -notin @('package','runtimepack')) { continue }
    $packageName,$packageVersion = $entry.Name.Split('/',2)
    $packageName = $packageName -replace '^runtimepack\.',''
    $packageDirectory = Join-Path $projectRoot ('.tools\nuget\' + $packageName.ToLowerInvariant() + '\' + $packageVersion.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $packageDirectory)) { throw "License package cache missing: $($entry.Name)" }
    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter *.nuspec -File | Select-Object -First 1
    if (-not $nuspec) { throw "NuGet license metadata missing: $($entry.Name)" }
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $licenseNode = $metadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
    $licenseUrlNode = $metadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="licenseUrl"]')
    $licenseValue = if ($licenseNode) { $licenseNode.InnerText } elseif ($licenseUrlNode) { $licenseUrlNode.InnerText } else { 'NOASSERTION' }
    if ($licenseValue -eq 'NOASSERTION') { throw "Review license metadata for $($entry.Name) before release." }
    $packageLicenseFolder = Join-Path $licenseOutput ($packageName + '-' + $packageVersion)
    New-Item -ItemType Directory -Path $packageLicenseFolder -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $packageLicenseFolder -Force
    $noticeFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -match '^(?i:LICENSE|LICENCE|NOTICE|THIRD[-_ ]?PARTY[-_ ]?NOTICES)(\..*)?$' })
    foreach ($notice in $noticeFiles) {
        $relative = [IO.Path]::GetRelativePath($packageDirectory,$notice.FullName)
        $destination = Join-Path $packageLicenseFolder $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force
    }
    if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
        $declaredLicense = Join-Path $packageDirectory $licenseNode.InnerText
        if (-not (Test-Path -LiteralPath $declaredLicense)) { throw "Declared license file missing: $($entry.Name)" }
        Copy-Item -LiteralPath $declaredLicense -Destination $packageLicenseFolder -Force
    }
    $licenseRows.Add([ordered]@{ package=$packageName; version=$packageVersion; type=$entry.Value.type; license=$licenseValue; noticeFiles=$noticeFiles.Count })
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\MIT-Microsoft.txt') -Destination $licenseOutput -Force
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'installer\licenses') -File | Copy-Item -Destination $licenseOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '.tools\inno-6.7.3\license.txt') -Destination (Join-Path $licenseOutput 'Inno-Setup-License.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\languages\Inno-Setup-Translation-LICENSE.txt') -Destination $licenseOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\languages\PROVENANCE.md') -Destination (Join-Path $licenseOutput 'Inno-Setup-Chinese-PROVENANCE.md') -Force
$notices=Get-Content -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Raw
if ($notices -notmatch 'Microsoft\.Data\.Sqlite' -or $notices -notmatch 'SQLitePCLRaw') { throw 'Update THIRD-PARTY-NOTICES.md for the new SQLite runtime dependencies before packaging.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\package-readme.txt') -Destination (Join-Path $payload 'PACKAGE-README.txt') -Force
$licenseRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'DEPENDENCY-LICENSES.json') -Encoding utf8

Archive-GeneratedPath $portable
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $portable -Recurse -Force
Set-Content -LiteralPath (Join-Path $portable 'portable.mode') -Value 'DevDoctor portable edition v1; user data is scoped by executable package path.' -Encoding utf8
Archive-GeneratedPath $zipPath
Compress-Archive -LiteralPath $portable -DestinationPath $zipPath -CompressionLevel Optimal
Archive-GeneratedPath $setupPath
& "$PSScriptRoot\write-installer-fingerprints.ps1" -PayloadDirectory $payload
$compiler = & "$PSScriptRoot\package-inno.ps1"
& $compiler "/DPayloadDir=$payload" "/DOutputDir=$artifactRoot" "/DAppVersion=$Version" (Join-Path $projectRoot 'installer\DevDoctor.iss') 2>&1 | Tee-Object -FilePath (Join-Path $releaseEvidence 'installer-compile.log')
if ($LASTEXITCODE -ne 0) { throw "Installer compile failed: $LASTEXITCODE" }
$hashLines = @($setupPath,$zipPath) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" }
$hashPath=Join-Path $artifactRoot 'SHA256SUMS.txt'
$retainedHashes=if(Test-Path -LiteralPath $hashPath){@(Get-Content -LiteralPath $hashPath | Where-Object {$_ -notmatch ('  DevDoctor-v' + [regex]::Escape($Version) + '-win-x64-(Setup\.exe|portable\.zip)$')})}else{@()}
@($retainedHashes)+@($hashLines) | Set-Content -LiteralPath $hashPath -Encoding ascii
[ordered]@{
    version=$Version; configuration='Release'; runtime='win-x64'; selfContained=$true
    createdUtc=[DateTime]::UtcNow.ToString('o'); assemblyVersion=$assemblyVersion
    installedPayload=$payload; portableFolder=$portable; setup=$setupPath; portableZip=$zipPath
    compiler='Inno Setup 6.7.3 (official portable compiler)'; signed=$false
    runtimePackages=$licenseRows; hashes=$hashLines
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseEvidence 'release-manifest.json') -Encoding utf8
Write-Output "Installer: $setupPath"
Write-Output "Portable:  $zipPath"
