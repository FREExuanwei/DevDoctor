[CmdletBinding()]
param([switch]$SkipCompile, [ValidateSet('1.1.0','1.2.0')][string]$Version = '1.1.0')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=Split-Path -Parent $PSScriptRoot
$evidence=Join-Path $projectRoot "artifacts\release-evidence-v$Version"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks=[Collections.Generic.List[object]]::new()
function Assert-Check([bool]$Condition,[string]$Name) {
    if(-not $Condition){throw "Installer static check failed: $Name"}
    $checks.Add(@{name=$Name;passed=$true})
}
foreach($relative in @('scripts\release.ps1','scripts\publish.ps1','scripts\run.ps1','scripts\test-v12-release-safety.ps1','scripts\validate-installer.ps1','scripts\check-v11-installer.ps1','scripts\check-v12-installer.ps1','scripts\package-source.ps1','scripts\create-v11-upgrade-seed.ps1','scripts\verify-v11-upgrade-data.ps1','scripts\validate-v12-installer.ps1','installer\PackageApplicationChecks.ps1','installer\ChineseWizardChecks.ps1','installer\ExistingInstallationProtection.ps1','installer\PackageBoundaryChecks.ps1')) {
    $tokens=$null;$parseErrors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot $relative),[ref]$tokens,[ref]$parseErrors)
    Assert-Check ($parseErrors.Count -eq 0) "$relative PowerShell syntax: $parseErrors"
}
$compiler= & (Join-Path $PSScriptRoot 'package-inno.ps1')
$language=Join-Path $projectRoot 'installer\languages\ChineseSimplified.isl'
$license=Join-Path $projectRoot 'installer\languages\Inno-Setup-Translation-LICENSE.txt'
Assert-Check ((Get-FileHash -LiteralPath $language).Hash -eq '6753BE2C5E2740D859900FD902824DB2EC568DA5C5B52486524C9762D778B0B0') 'Official upstream translation hash'
Assert-Check ((Get-FileHash -LiteralPath $license).Hash -eq '2E5346868C2A18434489824E11D65C3031620F792FEFC415D05F19CD441ABF5C') 'Official upstream translation license hash'
$utf8=[Text.UTF8Encoding]::new($false,$true)
[void]$utf8.GetString([IO.File]::ReadAllBytes($language))
Assert-Check $true 'Translation strict UTF-8 decoding'
$defaultKeys=@(Get-Content -LiteralPath (Join-Path (Split-Path -Parent $compiler) 'Default.isl') | Where-Object {$_ -match '^[A-Za-z][A-Za-z0-9]*='} | ForEach-Object {$_.Split('=',2)[0]})
$translatedKeys=@(Get-Content -LiteralPath $language | Where-Object {$_ -match '^[A-Za-z][A-Za-z0-9]*='} | ForEach-Object {$_.Split('=',2)[0]})
Assert-Check (@(Compare-Object $defaultKeys $translatedKeys).Count -eq 0) "All $($defaultKeys.Count) compiler language keys present; no English fallback"
$setup=Get-Content -LiteralPath (Join-Path $projectRoot 'installer\DevDoctor.iss') -Raw
Assert-Check ($setup.Contains('AppId={{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}') -and $setup.Contains('PrivilegesRequired=lowest')) 'Stable product identity and per-user privilege'
Assert-Check ($setup.Contains('Name: chinesesimplified; MessagesFile: languages\ChineseSimplified.isl') -and -not $setup.Contains('Name: english;')) 'Chinese is the only/default language'
Assert-Check ($setup.Contains('postinstall skipifsilent unchecked')) 'Completion launch requires explicit opt-in'
Assert-Check ($setup.Contains('RegQueryStringValue(HKCU64') -and $setup.Contains('DisplayVersion')) 'Upgrade page reads real installed version'
$harness=Get-Content -LiteralPath (Join-Path $projectRoot 'scripts\validate-installer.ps1') -Raw
$packageChecks=Get-Content -LiteralPath (Join-Path $projectRoot 'installer\PackageApplicationChecks.ps1') -Raw
Assert-Check (-not $harness.Contains('0.9.9') -and -not $harness.Contains('UpgradeFixture')) 'Upgrade uses genuine archived previous release, no registration fixture'
Assert-Check ($harness.Contains('Assert-OwnedRegistrationOrAbsent') -and $harness.Contains('old-app-verification.json') -and $harness.Contains('verify-v11-upgrade-data.ps1')) 'Lifecycle guards ownership and requires actual data verifier'
Assert-Check (-not $packageChecks.Contains('UseDefaultWorkspace') -and $packageChecks.Contains("`$start.Environment['DEVDOCTOR_DATA_ROOT']=`$DataRoot")) 'Every package launch has explicit controlled data root'
Add-Type -Path (Join-Path $projectRoot 'installer\InstallerTestInterop.cs')
Assert-Check $true 'Installer test interop C# compiles (no native methods invoked)'
if(-not $SkipCompile) {
    # Read archived payload only to validate script/code/messages; never create an installer.
    $payload=if($Version -eq '1.2.0') {
        Join-Path $projectRoot 'artifacts\v1.2-upgrade-seed\legacy-runtime\DevDoctor-v1.1.0-win-x64-portable'
    } else {Join-Path $projectRoot 'artifacts\DevDoctor-v1.0.0-win-x64'}
    $protectedSetups=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts') -File -Filter 'DevDoctor-v*-win-x64-Setup.exe' | ForEach-Object {Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256})
    # Inno clears matching outputs even with /O-; isolate BOTH directory and basename.
    $scratch=Join-Path $evidence ('static-compile-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    & $compiler /O- "/O$scratch" "/FInstallerStaticCheck-$([Guid]::NewGuid().ToString('N'))" "/DOutputDir=$scratch" "/DPayloadDir=$payload" "/DAppVersion=$Version" (Join-Path $projectRoot 'installer\DevDoctor.iss') 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'installer-static-compile.log')
    Assert-Check ($LASTEXITCODE -eq 0) 'Inno script compilation with output disabled (not final payload verification)'
    foreach($setupHash in $protectedSetups){if(-not(Test-Path -LiteralPath $setupHash.Path) -or (Get-FileHash -LiteralPath $setupHash.Path -Algorithm SHA256).Hash -ne $setupHash.Hash){throw "Static compilation changed an existing delivery: $($setupHash.Path)"}}
    Assert-Check $true 'Existing formal Setup files remain present with identical SHA-256 after static compilation'
}
[ordered]@{version=$Version;passed=$true;utc=[DateTime]::UtcNow.ToString('o');scope='Static source checks and output-disabled compilation only; no app/wizard/installer/uninstaller was launched.';checks=$checks} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'installer-static-checks.json') -Encoding utf8
Write-Output "Static installer evidence: $evidence"
