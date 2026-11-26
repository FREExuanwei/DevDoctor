[CmdletBinding()]
param([switch]$Before)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$suite=Join-Path $workspace 'artifacts/task5-fix-round1'
$sourceScript=if($Before){Join-Path $suite 'before/package-source.ps1'}else{Join-Path $PSScriptRoot 'package-source.ps1'}
$boundaryScript=if($Before){Join-Path $suite 'before/PackageBoundaryChecks.ps1'}else{Join-Path $workspace 'installer/PackageBoundaryChecks.ps1'}
$run=Join-Path $suite ($(if($Before){'red-'}else{'green-'})+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Passed,[string]$Name,[object]$Details) {
    $results.Add([ordered]@{name=$Name;passed=$Passed;details=$Details})
    Write-Output "$(if($Passed){'PASS'}else{'FAIL'}) $Name"
}
function Owned([string]$Path) {
    $absolute=[IO.Path]::GetFullPath($Path)
    if(-not $absolute.StartsWith($run.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Fixture path escaped run root: $absolute"}
    return $absolute
}
function New-SourceFixture([string]$Name) {
    $fixture=Owned (Join-Path $run $Name)
    foreach($dir in @('src','tests','scripts','installer','docs','licenses','artifacts/release-evidence-v1.2.0')){New-Item -ItemType Directory -Path (Join-Path $fixture $dir) -Force|Out-Null}
    foreach($name in @('.gitignore','DevDoctor.cmd','DevDoctor.slnx','Directory.Build.props','global.json','NuGet.Config','README.md','THIRD-PARTY-NOTICES.md')){[IO.File]::WriteAllText((Join-Path $fixture $name),"public fixture $name original")}
    [IO.File]::WriteAllText((Join-Path $fixture 'docs/00-remove.cs'),'// public earlier source file')
    Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $fixture 'scripts/package-source.ps1')
    [IO.File]::WriteAllText((Join-Path $fixture 'artifacts/DevDoctor-v1.2.0-source.zip'),'PRIOR ZIP MUST SURVIVE REJECTION')
    [IO.File]::WriteAllText((Join-Path $fixture 'artifacts/release-evidence-v1.2.0/source-manifest.json'),'{"priorSentinel":true}')
    return $fixture
}
function Start-Pack([string]$Fixture) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in @('-NoProfile','-File',(Join-Path $Fixture 'scripts/package-source.ps1'))){$start.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::new();$p.StartInfo=$start
    [void]$p.Start();return $p
}
function Finish-Pack([Diagnostics.Process]$Process,[string]$Fixture) {
    if(-not $Process.WaitForExit(30000)){$Process.Kill($true);throw 'Fixture packager exceeded 30 seconds'}
    $output=$Process.StandardOutput.ReadToEnd()+$Process.StandardError.ReadToEnd()
    [IO.File]::WriteAllText((Join-Path $Fixture 'packager-output.txt'),$output)
    return [pscustomobject]@{exitCode=$Process.ExitCode;output=$output}
}
function Prior-Preserved([string]$Fixture) {
    return [IO.File]::ReadAllText((Join-Path $Fixture 'artifacts/DevDoctor-v1.2.0-source.zip')) -eq 'PRIOR ZIP MUST SURVIVE REJECTION' -and
        [IO.File]::ReadAllText((Join-Path $Fixture 'artifacts/release-evidence-v1.2.0/source-manifest.json')) -eq '{"priorSentinel":true}'
}

# Execute only the actual source statements for read-only ZIP extraction and identity,
# before ACL operations; the installer/WPF portion of Test-PackageBoundaries never runs.
. $boundaryScript
. (Join-Path $workspace 'installer/ExistingInstallationProtection.ps1')
function Assert-Condition([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Record([string]$Name,[object]$Details) { $script:lastIdentityRecord=$Details }
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($boundaryScript,[ref]$tokens,[ref]$parseErrors)
$body=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-PackageBoundaries'},$true)
$start=$body.Body.Extent.Text.IndexOf("`$extraction=Join-Path")
$end=$body.Body.Extent.Text.IndexOf("`$ownedPrefix=")
$extractionCode=[scriptblock]::Create($body.Body.Extent.Text.Substring($start,$end-$start))
function Make-MiniPortable([string]$Zip,[string]$FolderVersion,[string]$Runtime) {
    $archive=[IO.Compression.ZipFile]::Open($Zip,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in @('DevDoctor.exe','DevDoctor.dll')){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $Runtime $file),"DevDoctor-v$FolderVersion-win-x64-portable/$file")}}finally{$archive.Dispose()}
}
function Test-PackageSelection([bool]$Masquerade,[bool]$OldAbsent=$false,[string]$ExpectedVersion='1.2.0') {
    $caseName=if($Masquerade){'wrong-binary'}else{'current-selection'}
    $case=Owned (Join-Path $run ($caseName+"-$ExpectedVersion-$OldAbsent"))
    $artifactRoot=Join-Path $case 'packages';$validationRoot=Join-Path $case 'owned-validation';$SeedDataPath=Join-Path $case 'seed';$Version=$ExpectedVersion
    New-Item -ItemType Directory -Path $artifactRoot,$validationRoot,$SeedDataPath -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $SeedDataPath 'public-fixture.txt'),'not user data')
    $oldRuntime=Join-Path $workspace 'artifacts/v1.2-upgrade-seed/legacy-runtime/DevDoctor-v1.1.0-win-x64-portable'
    $newRuntime=Join-Path $workspace 'src/DevDoctor.App/bin/Release/net10.0-windows'
    if(-not $OldAbsent){Make-MiniPortable (Join-Path $artifactRoot 'DevDoctor-v1.1.0-win-x64-portable.zip') '1.1.0' $oldRuntime}
    Make-MiniPortable (Join-Path $artifactRoot 'DevDoctor-v1.2.0-win-x64-portable.zip') '1.2.0' $(if($Masquerade){$oldRuntime}else{$newRuntime})
    $errorText=$null;$binary=$null;$packageIdentity=$null
    try{. $extractionCode}catch{$errorText=$_.Exception.Message}
    if($Masquerade){Check ($errorText -like '*package*version*') 'Reject old v1.1 binary under v1.2 package name before launch/ACL' @{error=$errorText;binary=$binary}}
    else{Check ($null -eq $errorText -and $binary -eq (Join-Path $validationRoot "readonly-portable-extracted/DevDoctor-v$ExpectedVersion-win-x64-portable") -and $null -ne $packageIdentity -and $packageIdentity.assemblyVersion -eq "$ExpectedVersion.0" -and $packageIdentity.executableVersion -eq "$ExpectedVersion.0") "Read-only branch selects/identifies v$ExpectedVersion; retained-old-ZIP-absent=$OldAbsent" @{error=$errorText;binary=$binary;identity=$packageIdentity}}
}
Test-PackageSelection $false
Test-PackageSelection $true
Test-PackageSelection $false $true
Test-PackageSelection $false $false '1.1.0'

$outside=Owned (Join-Path $run 'outside-source');New-Item -ItemType Directory -Path $outside|Out-Null
[IO.File]::WriteAllText((Join-Path $outside 'README.md'),'outside public link target')
foreach($linkCase in @('root-file-link','source-root-junction','empty-nested-junction','excluded-junction')) {
    $fixture=New-SourceFixture $linkCase
    try {
        switch($linkCase) {
            'root-file-link' {$link=Owned (Join-Path $fixture 'README.md');Remove-Item -LiteralPath $link;New-Item -ItemType SymbolicLink -Path $link -Target (Join-Path $outside 'README.md')|Out-Null}
            'source-root-junction' {$link=Owned (Join-Path $fixture 'src');Remove-Item -LiteralPath $link;New-Item -ItemType Junction -Path $link -Target $outside|Out-Null}
            'empty-nested-junction' {$empty=Owned (Join-Path $run 'empty-target');New-Item -ItemType Directory -Path $empty -Force|Out-Null;New-Item -ItemType Junction -Path (Owned (Join-Path $fixture 'src/empty-link')) -Target $empty|Out-Null}
            'excluded-junction' {New-Item -ItemType Junction -Path (Owned (Join-Path $fixture 'src/bin')) -Target $outside|Out-Null}
        }
    } catch {
        if($linkCase -eq 'root-file-link'){$results.Add(@{name='Root file symlink fixture privilege';passed=$null;details=$_.Exception.Message});Write-Output "SKIP root-file-link: $($_.Exception.Message)";continue}
        throw
    }
    $outcome=Finish-Pack (Start-Pack $fixture) $fixture
    Check ($outcome.exitCode -ne 0 -and $outcome.output -like '*linked*' -and (Prior-Preserved $fixture)) "Reject $linkCase without replacing prior ZIP/manifest" $outcome
}

# Mutate an already-copied public entry while a later, incompressible C# comment
# is being compressed. The signal is real staging ZIP growth, not a fixed delay.
$bytes=[byte[]]::new(12MB);[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$largeComment='// public generated compression fixture, never compiled'+[Environment]::NewLine+'// '+[Convert]::ToBase64String($bytes)
foreach($change in @('mutate','same-length','add','remove','rename')) {
    $fixture=New-SourceFixture ('drift-'+$change)
    [IO.File]::WriteAllText((Join-Path $fixture 'src/zz-large-public.cs'),$largeComment)
    $process=Start-Pack $fixture;$changed=$false;$observedBytes=0
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while(-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $staging=@([IO.Directory]::GetFiles((Join-Path $fixture 'artifacts'),'.source-staging-*.zip'))
        if($staging.Count -eq 1){$observedBytes=[IO.FileInfo]::new($staging[0]).Length}
        if($observedBytes -ge 1MB) {
            switch($change) {
                'mutate' {[IO.File]::WriteAllText((Owned (Join-Path $fixture 'README.md')),'public fixture changed after earlier ZIP entry')}
                'same-length' {$path=Owned (Join-Path $fixture 'README.md');[IO.File]::WriteAllText($path,[IO.File]::ReadAllText($path).Replace('original','modified'))}
                'add' {[IO.File]::WriteAllText((Owned (Join-Path $fixture 'docs/00-added.cs')),'// new eligible public entry')}
                'remove' {Remove-Item -LiteralPath (Owned (Join-Path $fixture 'docs/00-remove.cs'))}
                'rename' {Move-Item -LiteralPath (Owned (Join-Path $fixture 'docs/00-remove.cs')) -Destination (Owned (Join-Path $fixture 'docs/00-renamed.cs'))}
            }
            $changed=$true;break
        }
        Start-Sleep -Milliseconds 5
    }
    $outcome=Finish-Pack $process $fixture
    Check ($changed -and $outcome.exitCode -ne 0 -and $outcome.output -like '*source*changed*' -and (Prior-Preserved $fixture)) "Reject complete-source $change drift and retain prior ZIP/manifest" @{mutatedWhileStagingBytes=$observedBytes;changed=$changed;result=$outcome}
}

$fixture=New-SourceFixture 'unchanged-source'
$outcome=Finish-Pack (Start-Pack $fixture) $fixture
$verified=$false;$entries=0
if($outcome.exitCode -eq 0) {
    $m=Get-Content (Join-Path $fixture 'artifacts/release-evidence-v1.2.0/source-manifest.json') -Raw|ConvertFrom-Json
    $archive=[IO.Compression.ZipFile]::OpenRead($m.zip)
    try {
        $verified=$m.verified -eq $true -and $m.entries.Count -eq $archive.Entries.Count
        foreach($entry in $archive.Entries) {
            $source=Join-Path $fixture $entry.FullName;$row=@($m.entries|Where-Object path -eq $entry.FullName)
            $stream=$entry.Open();try{$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            $verified=$verified -and $row.Count -eq 1 -and $entry.Length -eq (Get-Item -LiteralPath $source).Length -and $row[0].sha256 -eq $sha -and (Get-FileHash -LiteralPath $source).Hash -eq $sha
            $entries++
        }
    } finally {$archive.Dispose()}
}
Check ($verified) 'Unchanged eligible workspace exactly matches published ZIP and every manifest SHA' @{entries=$entries;result=$outcome}
$failed=@($results|Where-Object {$_.passed -eq $false}).Count
[ordered]@{before=$Before.IsPresent;utc=[DateTime]::UtcNow.ToString('o');run=$run;failed=$failed;noAppInstallerBuildOrAclInvocations=$true;results=$results}|ConvertTo-Json -Depth 9|Set-Content (Join-Path $run 'results.json') -Encoding utf8
Write-Output "RESULT $($results.Count) cases, $failed failures: $run/results.json"
if($failed){exit 1}
