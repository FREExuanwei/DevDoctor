[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][Alias('SeedDirectory')][string]$SeedDataPath,
    [string]$SetupPath,
    [string]$PreviousSetupPath,
    [string]$ExpectedLocalAppData,
    [switch]$OnlyPackageBoundaries,
    [switch]$SkipInterruption,
    [ValidateSet('1.1.0','1.2.0')][string]$Version = '1.1.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if([string]::IsNullOrWhiteSpace($ExpectedLocalAppData)){throw 'Pass an explicitly verified real -ExpectedLocalAppData; redirected sandbox profiles are not suitable for installation validation.'}
$ExpectedLocalAppData=[IO.Path]::GetFullPath($ExpectedLocalAppData).TrimEnd('\')
foreach($actual in @($env:LOCALAPPDATA,[Environment]::GetFolderPath('LocalApplicationData'))) {
    if([IO.Path]::GetFullPath($actual).TrimEnd('\') -ne $ExpectedLocalAppData){throw 'Actual LocalAppData differs from the explicitly verified real profile; no installation performed.'}
}
$PreviousVersion = if ($Version -eq '1.2.0') {'1.1.0'} else {'1.0.0'}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$token = [Security.Principal.WindowsPrincipal]::new($identity)
if ($token.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run lifecycle validation as the explicitly verified ordinary user, never elevated.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $projectRoot 'installer\ExistingInstallationProtection.ps1')
$artifactRoot = Join-Path $projectRoot 'artifacts'
if (-not $SetupPath) { $SetupPath=Join-Path $artifactRoot "DevDoctor-v$Version-win-x64-Setup.exe" }
if (-not $PreviousSetupPath) { $PreviousSetupPath=Join-Path $artifactRoot "baselines\v$PreviousVersion\DevDoctor-v$PreviousVersion-win-x64-Setup.exe" }
$SetupPath=(Resolve-Path -LiteralPath $SetupPath).Path
$PreviousSetupPath=(Resolve-Path -LiteralPath $PreviousSetupPath).Path
$SeedDataPath=(Resolve-Path -LiteralPath $SeedDataPath).Path
$oldManifest=Join-Path $SeedDataPath 'old-app-verification.json'
if(-not (Test-Path -LiteralPath $oldManifest)) {
    $oldManifest=Join-Path (Split-Path -Parent $SeedDataPath) 'old-app-verification.json'
}
# The seed producer keeps provenance outside the closed, copyable user-data root.
if((Test-Path -LiteralPath (Join-Path $SeedDataPath 'old-app-verification.json')) -and
   (Test-Path -LiteralPath (Join-Path $SeedDataPath 'closed-seed'))) {
    $SeedDataPath=(Resolve-Path -LiteralPath (Join-Path $SeedDataPath 'closed-seed')).Path
}
$dataVerifier=Join-Path $PSScriptRoot 'verify-v11-upgrade-data.ps1'
if(-not (Test-Path -LiteralPath $oldManifest) -or -not (Test-Path -LiteralPath $dataVerifier)) {throw 'Genuine old-app verification manifest and application-level upgrade verifier are required; hash-only upgrade validation is insufficient.'}
$baseline=Get-Content -LiteralPath (Join-Path $artifactRoot "baselines\v$PreviousVersion\SHA256-v$PreviousVersion.json") -Raw | ConvertFrom-Json
$oldSetupHash=($baseline | Where-Object Name -eq "DevDoctor-v$PreviousVersion-win-x64-Setup.exe").SHA256
if((Get-FileHash -LiteralPath $PreviousSetupPath -Algorithm SHA256).Hash -ne $oldSetupHash) {throw "The previous Setup does not match archived genuine v$PreviousVersion SHA-256."}
$runId=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
$validationRoot=Join-Path $artifactRoot ("release-evidence-v$Version\install-validation\" + $runId)
$installDirectory=Join-Path $validationRoot 'application'
$dataDirectory=Join-Path $validationRoot 'data'
$outsideDirectory=Join-Path $validationRoot 'unrelated-working-directory'
$groupName="DevDoctor validation $runId"
$groupDirectory=Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
$desktopShortcut=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$groupName.lnk"
$registrationSuffix='Software\Microsoft\Windows\CurrentVersion\Uninstall\{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}_is1'
$registrationPaths=@("HKCU:\$registrationSuffix","HKLM:\$registrationSuffix",
    'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}_is1',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}_is1')
$steps=[System.Collections.Generic.List[object]]::new()
$ownedProcesses=[System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$finished=$false
$preservedApplicationFiles=@()
Assert-UnlinkedPathAncestors @($validationRoot,$installDirectory,$dataDirectory,$outsideDirectory,$groupDirectory,$desktopShortcut,$SeedDataPath)
New-Item -ItemType Directory -Path $validationRoot,$dataDirectory,$outsideDirectory -Force | Out-Null
Add-Type -Path (Join-Path $projectRoot 'installer\InstallerTestInterop.cs')
Add-Type -AssemblyName System.Drawing
. (Join-Path $projectRoot 'installer\PackageApplicationChecks.ps1')
. (Join-Path $projectRoot 'installer\ChineseWizardChecks.ps1')
. (Join-Path $projectRoot 'installer\PackageBoundaryChecks.ps1')
$protectedBefore=$null
$shellPinsBefore=$null
function Save-Evidence {
    [ordered]@{ runId=$runId; finalSetup=$SetupPath; setupSha256=(Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash
        previousSetup=$PreviousSetupPath; previousSetupSha256=$oldSetupHash; upgradeScope="Archived genuine v$PreviousVersion Setup and old application-generated data to v$Version; no registration fixture."
        genuineOldDataManifest=$oldManifest
        installDirectory=$installDirectory; dataDirectory=$dataDirectory; groupDirectory=$groupDirectory
        completed=$finished;startMenuBinaryHashesObservedOnly=$true;startMenuPinLayoutSemanticallyVerified=$false; steps=$steps } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $validationRoot 'lifecycle-results.json') -Encoding utf8
}
function Record([string]$Name,[object]$Details) {
    $steps.Add([ordered]@{step=$Name;passed=$true;utc=[DateTime]::UtcNow.ToString('o');details=$Details})
    Save-Evidence
    Write-Output "PASS: $Name"
}
function Assert-Condition([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Get-Registration {
    $rows=@()
    foreach($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
            $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
            try {
                $key=$base.OpenSubKey($registrationSuffix)
                if($key){try{$rows+=[pscustomobject]@{Path="$hive/$view/$registrationSuffix";Hive=$hive.ToString();View=$view.ToString();InstallLocation=$key.GetValue('InstallLocation');DisplayVersion=$key.GetValue('DisplayVersion');UninstallString=$key.GetValue('UninstallString')}}finally{$key.Dispose()}}
            }finally{$base.Dispose()}
        }
    }
    # HKCU Uninstall is shared between views; retain distinct/conflicting values rather than counting one physical key twice.
    return @($rows | Group-Object {if($_.Hive -eq 'CurrentUser'){"$($_.Hive)|$($_.InstallLocation)|$($_.DisplayVersion)|$($_.UninstallString)"}else{$_.Path}} | ForEach-Object {$_.Group[0]})
}
function Assert-ProtectedExistingPaths([string]$Stage) {
    if($null -ne $protectedBefore -and $null -ne $shellPinsBefore) {
        Assert-ExactSnapshot $protectedBefore (Get-ExactPathSnapshot $protectedPaths) "$Stage protected existing installation/data/shortcuts"
        Assert-ExactSnapshot (Get-TaskbarAndLegacyPinState $shellPinsBefore $ExpectedLocalAppData) (Get-TaskbarAndLegacyPinState (Get-ShellPinSnapshot $ExpectedLocalAppData) $ExpectedLocalAppData) "$Stage taskbar and legacy pinned entries"
    }
}
function Assert-NoForeignProductRegistration {
    foreach($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
            $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
            try {
                $parent=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
                if($parent){try{foreach($name in $parent.GetSubKeyNames()) {
                    $key=$parent.OpenSubKey($name)
                    if($key){try {
                        if([string]$key.GetValue('DisplayName') -like 'DevDoctor*' -or $name -like '*4D627B25*') {
                            $location=[string]$key.GetValue('InstallLocation')
                            if([string]::IsNullOrWhiteSpace($location) -or [IO.Path]::GetFullPath($location).TrimEnd('\') -ne [IO.Path]::GetFullPath($installDirectory).TrimEnd('\')) {throw 'A non-owned DevDoctor registration exists; no installation/uninstallation is allowed.'}
                        }
                    }finally{$key.Dispose()}}
                }}finally{$parent.Dispose()}}
            }finally{$base.Dispose()}
        }
    }
}
function Get-DataHashes([string]$Directory) {
    $hashes=[ordered]@{}
    if (Test-Path -LiteralPath $Directory) {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName) {
            if ($file.Name -eq '.workspace.lock' -or $file.Name -match '(-wal|-shm|\.tmp)$') {continue}
            $hashes[[IO.Path]::GetRelativePath($Directory,$file.FullName)]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $hashes
}
function Assert-DataPreserved([string]$Stage) {
    $current=Get-DataHashes $dataDirectory
    foreach ($name in $script:seedHashes.Keys) {
        Assert-Condition ($current.Contains($name)) "$Stage deleted seeded user data: $name"
        Assert-Condition ($current[$name] -eq $script:seedHashes[$name]) "$Stage changed seeded user data: $name"
    }
    Record "$Stage - user data retained" @{files=$script:seedHashes.Count;sha256Identical=$true}
}
function Start-OwnedProcess([string]$Executable,[string[]]$Arguments,[bool]$Visible=$false) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Executable
    $start.UseShellExecute=$false
    $start.WorkingDirectory=$outsideDirectory
    $start.WindowStyle=if($Visible){[Diagnostics.ProcessWindowStyle]::Normal}else{[Diagnostics.ProcessWindowStyle]::Hidden}
    # Installer descendants cannot accidentally start the app against a default root.
    $start.Environment['DEVDOCTOR_DATA_ROOT']=$dataDirectory
    foreach ($argument in $Arguments) {$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$start
    if (-not $process.Start()) {throw "Failed to start $Executable"}
    $ownedProcesses.Add($process)
    return $process
}
function Invoke-Setup([string]$Executable,[string]$Name,[bool]$Desktop=$false) {
    Assert-ProtectedExistingPaths "$Name before setup"
    Assert-NoForeignProductRegistration
    Assert-UnlinkedPathAncestors @($installDirectory,$groupDirectory,$desktopShortcut)
    Assert-OwnedRegistrationOrAbsent
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',"/DIR=$installDirectory","/GROUP=$groupName",
        "/LOG=$(Join-Path $validationRoot "$Name.log")",$(if($Desktop){'/TASKS=desktopicon'}else{'/TASKS='}))
    $process=Start-OwnedProcess $Executable $arguments
    if (-not $process.WaitForExit(60000)) {$process.Kill($true);throw "$Name exceeded 60 seconds."}
    Assert-Condition ($process.ExitCode -eq 0) "$Name failed with exit code $($process.ExitCode)."
    $registration=@(Get-Registration)
    Assert-Condition ($registration.Count -eq 1) "$Name did not create exactly one product registration."
    Assert-Condition ($registration[0].InstallLocation.TrimEnd('\') -eq $installDirectory.TrimEnd('\')) "$Name registered an unexpected installation directory."
    Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory 'DevDoctor.exe')) "$Name did not install the application."
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'portable.mode'))) "$Name incorrectly installed portable.mode."
    Assert-Condition (Test-Path -LiteralPath (Join-Path $groupDirectory 'DevDoctor.lnk')) "$Name did not create the Start Menu shortcut."
    if ($Desktop) {Assert-Condition (Test-Path -LiteralPath $desktopShortcut) "$Name did not create the requested Desktop shortcut."}
    Assert-ProtectedExistingPaths "$Name after setup"
    Record $Name @{exitCode=$process.ExitCode;registration=$registration;desktopRequested=$Desktop}
}
function Assert-OwnedRegistrationOrAbsent {
    $registrations=@(Get-Registration)
    foreach($item in $registrations) {
        Assert-Condition ($item.InstallLocation -and ([IO.Path]::GetFullPath($item.InstallLocation).TrimEnd('\') -eq [IO.Path]::GetFullPath($installDirectory).TrimEnd('\'))) 'A non-test DevDoctor registration was found. Fixed AppId shares registration; another DIR does not isolate it. Installation stopped.'
    }
    Assert-Condition ($registrations.Count -le 1) 'Multiple product registrations exist; installation stopped.'
}
function Test-GenuineUpgradeData([string]$Stage) {
    $output=Join-Path $validationRoot ($Stage.Replace(' ','-')+'-data-verification.json')
    & $dataVerifier -DataRoot $dataDirectory -ExpectedManifest $oldManifest -OutputPath $output -OldVersion $PreviousVersion -Version $Version
    Assert-Condition ($?) "$Stage application-level verifier failed."
    Assert-Condition (Test-Path -LiteralPath $output) "$Stage produced no application-level data verification evidence."
    $verification=Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    Assert-Condition ($verification.passed -eq $true) "$Stage did not pass application-level historical data, appearance, report and backup compatibility verification."
    Record "$Stage - actual data compatibility" @{evidence=$output;passed=$true}
}
function Invoke-OwnedUninstall([string]$Name,[bool]$Visible=$false) {
    Assert-ProtectedExistingPaths "$Name before uninstall"
    Assert-NoForeignProductRegistration
    Assert-UnlinkedPathAncestors @($installDirectory,$groupDirectory,$desktopShortcut)
    $registration=@(Get-Registration)
    Assert-Condition ($registration.Count -eq 1) "$Name requires exactly one owned registration."
    Assert-Condition ($registration[0].InstallLocation.TrimEnd('\') -eq $installDirectory.TrimEnd('\')) 'Refusing to uninstall an unrelated installation.'
    $registeredCommand=$registration[0].UninstallString
    $match=[regex]::Match($registeredCommand,'^"(?<path>[^"]+\.exe)"(?:\s.*)?$')
    Assert-Condition $match.Success 'The registered uninstaller command is not a supported explicit executable path.'
    $registeredPath=[IO.Path]::GetFullPath($match.Groups['path'].Value)
    $ownedInstallRoot=[IO.Path]::GetFullPath($installDirectory).TrimEnd('\')+'\'
    Assert-Condition ($registeredPath.StartsWith($ownedInstallRoot,[StringComparison]::OrdinalIgnoreCase)) 'Refusing an uninstaller outside the owned validation directory.'
    $uninstaller=Get-Item -LiteralPath $registeredPath
    Assert-Condition ($null -ne $uninstaller) "$Name cannot find the owned uninstaller."
    $arguments=if($Visible){@('/NORESTART',"/LOG=$(Join-Path $validationRoot "$Name.log")")}else{@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$(Join-Path $validationRoot "$Name.log")")}
    $process=Start-OwnedProcess $uninstaller.FullName $arguments $Visible
    if($Visible){Complete-ChineseUninstallWizard $process $Name}
    if (-not $process.WaitForExit(60000)) {$process.Kill($true);throw "$Name exceeded 60 seconds."}
    Assert-Condition ($process.ExitCode -eq 0) "$Name failed with exit code $($process.ExitCode)."
    $childDeadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $remainingChildren=@([InstallerTestInterop]::ProcessTree($process.Id) | Where-Object {$_ -ne $process.Id} | ForEach-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue})
        if($remainingChildren.Count -eq 0){break}
        Start-Sleep -Milliseconds 50
    }while([DateTime]::UtcNow -lt $childDeadline)
    Assert-Condition ($remainingChildren.Count -eq 0) "$Name left an owned uninstaller child process."
    Start-Sleep -Milliseconds 300
    Assert-Condition (@(Get-Registration).Count -eq 0) "$Name left the product registration."
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'DevDoctor.exe'))) "$Name left installed application files."
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $groupDirectory 'DevDoctor.lnk'))) "$Name left its Start Menu shortcut."
    Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcut)) "$Name left its Desktop shortcut."
    $removalDeadline=[DateTime]::UtcNow.AddSeconds(3)
    do {
        $remainingProgramFiles=@(Get-ChildItem -LiteralPath $installDirectory -Recurse -File -ErrorAction SilentlyContinue | Where-Object {$_.FullName -notin $script:preservedApplicationFiles})
        if($remainingProgramFiles.Count -eq 0){break}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $removalDeadline)
    Assert-Condition ($remainingProgramFiles.Count -eq 0) "$Name left installed program files."
    Assert-ProtectedExistingPaths "$Name after uninstall"
    Record $Name @{exitCode=$process.ExitCode;registrationRemoved=$true;shortcutsRemoved=$true;allProgramFilesRemoved=$true}
}
function Test-InstalledApplication([string]$Name) {
    Test-PackageApplication -Name $Name -BinaryDirectory $installDirectory -DataRoot $dataDirectory -ExpectedEdition 'Controlled'
}
function Capture-Wizard([IntPtr]$Handle,[string]$FileName='installer-welcome.png') {
    $previousDpiContext=[InstallerTestInterop]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    $rectangle=[InstallerTestInterop+Rect]::new()
    Assert-Condition ([InstallerTestInterop]::GetWindowRect($Handle,[ref]$rectangle)) 'Cannot read owned wizard rectangle.'
    $bitmap=[Drawing.Bitmap]::new($rectangle.Right-$rectangle.Left,$rectangle.Bottom-$rectangle.Top)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $deviceContext=$graphics.GetHdc()
    try {Assert-Condition ([InstallerTestInterop]::PrintWindow($Handle,$deviceContext,2)) 'Cannot capture owned wizard.'}
    finally {$graphics.ReleaseHdc($deviceContext);$graphics.Dispose()}
    try {$bitmap.Save((Join-Path $validationRoot $FileName),[Drawing.Imaging.ImageFormat]::Png)}
    finally {$bitmap.Dispose();[void][InstallerTestInterop]::SetThreadDpiAwarenessContext($previousDpiContext)}
}
try {
    Assert-NoForeignProductRegistration
    Assert-Condition (@(Get-Registration).Count -eq 0) 'An existing DevDoctor product registration was found; no test installation was performed.'
    Assert-Condition (@(Get-Process -Name DevDoctor -ErrorAction SilentlyContinue).Count -eq 0) 'An existing DevDoctor process is running; no installation performed.'
    $protectedPaths=@((Join-Path $ExpectedLocalAppData 'Programs\DevDoctor'),(Join-Path $ExpectedLocalAppData 'DevDoctor\Installed'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'DevDoctor'),(Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DevDoctor.lnk'))
    foreach($protected in $protectedPaths) {
        $a=[IO.Path]::GetFullPath($protected).TrimEnd('\')+'\';$b=[IO.Path]::GetFullPath($validationRoot).TrimEnd('\')+'\'
        Assert-Condition (-not $a.StartsWith($b,[StringComparison]::OrdinalIgnoreCase) -and -not $b.StartsWith($a,[StringComparison]::OrdinalIgnoreCase)) 'Protected and owned test paths overlap.'
    }
    $protectedBefore=Get-ExactPathSnapshot $protectedPaths
    $shellPinsBefore=Get-ShellPinSnapshot $ExpectedLocalAppData
    [ordered]@{paths=$protectedBefore;shellPins=$shellPinsBefore;realLocalAppData=$ExpectedLocalAppData} | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $validationRoot 'protected-before.json') -Encoding utf8
    Assert-Condition (-not (Test-Path -LiteralPath $groupDirectory)) 'The dedicated test shortcut group already exists.'
    Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcut)) 'The dedicated test Desktop shortcut already exists.'
    $seedFiles=@(Get-ChildItem -LiteralPath $SeedDataPath -Recurse -File)
    Assert-Condition (-not ((Get-Item -LiteralPath $SeedDataPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Seed root must not be a directory link.'
    $seedLinks=@(Get-ChildItem -LiteralPath $SeedDataPath -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint})
    Assert-Condition ($seedLinks.Count -eq 0) 'Seed must not contain file or directory links.'
    Assert-Condition (@($seedFiles | Where-Object {$_.Name -match '(-wal|-shm)$' -and $_.Length -gt 0}).Count -eq 0) 'Seed SQLite database must be closed and checkpointed; live WAL/SHM files are not accepted.'
    Assert-Condition (@($seedFiles | Where-Object {$_.Extension -in '.db','.sqlite','.sqlite3'}).Count -gt 0) 'Seed data must contain a closed genuine SQLite database.'
    Assert-Condition (@($seedFiles | Where-Object {$_.Extension -eq '.json'}).Count -gt 0) 'Seed data must contain genuine JSON user settings.'
    Get-ChildItem -LiteralPath $SeedDataPath -Force | Copy-Item -Destination $dataDirectory -Recurse -Force
    $script:seedHashes=Get-DataHashes $dataDirectory
    $script:seedHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $validationRoot 'seed-sha256.json') -Encoding utf8
    $defaultDataDirectory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DevDoctor\Installed'
    $defaultDataBefore=Get-DataHashes $defaultDataDirectory
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Record 'Conflict preflight and isolated genuine seed' @{seedFiles=$script:seedHashes.Count;testGroup=$groupName;defaultDataFiles=$defaultDataBefore.Count;administratorToken=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);windowsBuild=[Environment]::OSVersion.Version.ToString()}
    if($OnlyPackageBoundaries) {
        Test-PackageBoundaries
        Assert-DataPreserved 'Package boundary checks'
        Assert-ProtectedExistingPaths 'Final boundary isolation'
        Record 'Boundary validation final isolation' @{registrationAbsent=(@(Get-Registration).Count -eq 0);existingProgramDataShortcutsAndTaskbarPreserved=$true;startMenuPinLayoutVerified=$false}
        $finished=$true
        Save-Evidence
        Write-Output "Boundary evidence: $validationRoot"
        return
    }

    # Real production wizard cancellation, before it is allowed to copy files.
    $wizard=Start-OwnedProcess $SetupPath @('/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',"/DIR=$installDirectory","/GROUP=$groupName",'/TASKS=',"/LOG=$(Join-Path $validationRoot 'early-cancel.log')") $true
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    $window=$null
    do {
        $window=[InstallerTestInterop]::TopLevelWindows($wizard.Id) | Where-Object {$_.Text -like '*DevDoctor*'} | Select-Object -First 1
        if($window){break}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    Assert-Condition ($null -ne $window) 'The real installer wizard did not appear.'
    Capture-Wizard $window.Handle
    $cancel=[InstallerTestInterop]::Children($window.Handle) | Where-Object {$_.Text.Replace('&','') -eq '取消' -and $_.ClassName -like '*Button*'} | Select-Object -First 1
    Assert-Condition ($null -ne $cancel) 'The owned wizard has no Cancel button.'
    [InstallerTestInterop]::Click($cancel.Handle)
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        if($wizard.HasExited){break}
        foreach($dialog in [InstallerTestInterop]::TopLevelWindows($wizard.Id)) {
            $yes=[InstallerTestInterop]::Children($dialog.Handle) | Where-Object {($_.ControlId -eq 6 -or $_.Text -like '是*') -and $_.ClassName -like '*Button*'} | Select-Object -First 1
            if($yes){[InstallerTestInterop]::Click($yes.Handle)}
        }
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    Assert-Condition ($wizard.HasExited) 'The owned wizard did not cancel.'
    Assert-Condition (@(Get-Registration).Count -eq 0) 'Early cancellation left product registration.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'DevDoctor.exe'))) 'Early cancellation copied application files.'
    Record 'Real wizard early cancellation' @{exitCode=$wizard.ExitCode;screenshot='installer-welcome.png';productChangesAbsent=$true}
    Assert-DataPreserved 'Early cancellation'

    Invoke-ChineseWizardInstallation 'Fresh installation'
    Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcut)) 'Fresh install created an unchecked Desktop shortcut.'
    Assert-DataPreserved 'Fresh installation'
    Test-NormalInstalledPackage
    Test-InstalledApplication 'Installed app independent startup'
    Assert-DataPreserved 'Installed app startup'
    Invoke-Setup $SetupPath 'Same-version repeat with optional Desktop shortcut' $true
    Assert-DataPreserved 'Same-version repeat'
    Invoke-OwnedUninstall 'First uninstall' $true
    Assert-DataPreserved 'First uninstall'
    Invoke-Setup $SetupPath 'Reinstall after uninstall'
    Assert-DataPreserved 'Reinstall'
    Test-InstalledApplication 'Reinstalled app startup'
    Invoke-OwnedUninstall 'Reinstall cleanup'
    Invoke-Setup $PreviousSetupPath "Genuine archived v$PreviousVersion installation"
    Assert-Condition ((Get-Registration)[0].DisplayVersion -eq $PreviousVersion) "The genuine previous Setup did not register v$PreviousVersion."
    Test-InstalledApplication "Genuine v$PreviousVersion installed app startup"
    Test-GenuineUpgradeData 'Before real upgrade'
    Invoke-ChineseWizardInstallation "Real upgrade from v$PreviousVersion to v$Version" $PreviousVersion
    Assert-Condition ((Get-Registration)[0].DisplayVersion -eq $Version) "Upgrade did not register v$Version."
    Assert-DataPreserved 'Upgrade'
    Test-InstalledApplication 'Upgraded app startup'
    Test-GenuineUpgradeData 'After real upgrade'
    Invoke-OwnedUninstall 'Upgrade cleanup'
    Assert-DataPreserved 'Upgrade uninstall'

    Test-PortablePackages
    Test-PackageBoundaries
    if (-not $SkipInterruption) {
        Assert-OwnedRegistrationOrAbsent
        $interrupted=Start-OwnedProcess $SetupPath @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',"/DIR=$installDirectory","/GROUP=$groupName",'/TASKS=',"/LOG=$(Join-Path $validationRoot 'abrupt-interruption.log')")
        $deadline=[DateTime]::UtcNow.AddSeconds(30)
        $observedFiles=@()
        do {
            if(Test-Path -LiteralPath $installDirectory){$observedFiles=@(Get-ChildItem -LiteralPath $installDirectory -File -ErrorAction SilentlyContinue)}
            if($observedFiles.Count -gt 1 -or $interrupted.HasExited){break}
            Start-Sleep -Milliseconds 10
        }while([DateTime]::UtcNow -lt $deadline)
        Assert-Condition (-not $interrupted.HasExited -and $observedFiles.Count -gt 1) 'Unable to interrupt the real installer during extraction; this case is not verified.'
        $tree=[InstallerTestInterop]::ProcessTree($interrupted.Id)
        $interrupted.Kill($true)
        Assert-Condition ($interrupted.WaitForExit(10000)) 'Interrupted setup did not terminate.'
        Start-Sleep -Milliseconds 300
        $remaining=@(Get-ChildItem -LiteralPath $installDirectory -Recurse -File -ErrorAction SilentlyContinue)
        Record 'Abrupt process-tree termination during real extraction' @{observedFiles=$observedFiles.Count;terminatedProcessIds=$tree;remainingProgramFiles=$remaining.Count;automaticRollbackClaimed=$false}
        Assert-DataPreserved 'Abrupt interruption'
        $unrelatedTemp=Join-Path $installDirectory 'is-USERFILE00.tmp'
        $unrelatedBytes=[Text.Encoding]::UTF8.GetBytes('Unrelated validation content. Repair and uninstall must preserve this file.')
        [IO.File]::WriteAllBytes($unrelatedTemp,$unrelatedBytes)
        $unrelatedHash=(Get-FileHash -LiteralPath $unrelatedTemp -Algorithm SHA256).Hash
        $script:preservedApplicationFiles=@($unrelatedTemp)
        Invoke-Setup $SetupPath 'Repair after abrupt interruption'
        Assert-Condition (Test-Path -LiteralPath $unrelatedTemp) 'Repair removed an unrelated temporary file.'
        Assert-Condition ((Get-FileHash -LiteralPath $unrelatedTemp -Algorithm SHA256).Hash -eq $unrelatedHash) 'Repair modified an unrelated temporary file.'
        $repairLog=Get-Content -LiteralPath (Join-Path $validationRoot 'Repair after abrupt interruption.log') -Raw
        Assert-Condition ($repairLog.Contains('Preserved recognized interrupted payload for owned uninstall cleanup:')) 'The actual interrupted payload was not recognized and quarantined.'
        Record 'Recognized interrupted payload isolated; unknown file preserved' @{unrelatedFile=[IO.Path]::GetFileName($unrelatedTemp);sha256Identical=$true;recognizedInSetupLog=$true}
        $payload=Join-Path $artifactRoot "DevDoctor-v$Version-win-x64"
        $verifiedFiles=0
        foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
            $destination=Join-Path $installDirectory ([IO.Path]::GetRelativePath($payload,$file.FullName))
            Assert-Condition (Test-Path -LiteralPath $destination) "Repair missed $($file.Name)."
            Assert-Condition ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) "Repair changed $($file.Name)."
            $verifiedFiles++
        }
        Record 'Repaired payload integrity' @{matchingFiles=$verifiedFiles;sha256Identical=$true}
        Test-InstalledApplication 'Repaired app startup'
        Invoke-OwnedUninstall 'Interruption repair cleanup'
        Assert-DataPreserved 'Interruption repair uninstall'
        Assert-Condition (Test-Path -LiteralPath $unrelatedTemp) 'Uninstall removed an unrelated temporary file.'
        Assert-Condition ((Get-FileHash -LiteralPath $unrelatedTemp -Algorithm SHA256).Hash -eq $unrelatedHash) 'Uninstall modified an unrelated temporary file.'
        $evidenceDestination=Join-Path $validationRoot 'preserved-unrelated-file.tmp'
        $absoluteSource=[IO.Path]::GetFullPath($unrelatedTemp)
        $absoluteDestination=[IO.Path]::GetFullPath($evidenceDestination)
        $ownedValidationPrefix=[IO.Path]::GetFullPath($validationRoot).TrimEnd('\')+'\'
        Assert-Condition ($absoluteSource.StartsWith($ownedValidationPrefix,[StringComparison]::OrdinalIgnoreCase) -and $absoluteDestination.StartsWith($ownedValidationPrefix,[StringComparison]::OrdinalIgnoreCase)) 'Refusing to move a test sentinel outside its owned evidence directory.'
        Move-Item -LiteralPath $absoluteSource -Destination $absoluteDestination
        Assert-Condition (@(Get-ChildItem -LiteralPath $installDirectory -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0) 'Final program directory still contains files after preserving the unrelated sentinel.'
        Record 'Unrelated file retained after uninstall; final program directory empty' @{sentinelEvidence=$evidenceDestination;sha256=$unrelatedHash;allProgramFilesRemoved=$true}
    }
    $defaultDataAfter=Get-DataHashes $defaultDataDirectory
    Assert-Condition (($defaultDataBefore | ConvertTo-Json -Compress) -eq ($defaultDataAfter | ConvertTo-Json -Compress)) 'Default installed user data changed during isolated validation.'
    Record 'Final isolation and cleanup' @{registrationAbsent=(@(Get-Registration).Count -eq 0);startMenuShortcutAbsent=(-not(Test-Path -LiteralPath (Join-Path $groupDirectory 'DevDoctor.lnk')));desktopShortcutAbsent=(-not(Test-Path -LiteralPath $desktopShortcut));defaultInstalledDataUnchanged=$true;seedDataRetained=$true}
    Assert-ProtectedExistingPaths 'Final lifecycle'
    Record 'Existing unregistered installation, data, shortcuts and taskbar entries preserved' @{exactSnapshotIdentical=$true;realLocalAppData=$ExpectedLocalAppData;protectedRoots=$protectedPaths;defaultAppNotLaunched=$true;startMenuBinaryObservedOnly=$true;startMenuPinLayoutVerified=$false}
    $finished=$true
    Save-Evidence
    Write-Output "Lifecycle evidence: $validationRoot"
} catch {
    $steps.Add([ordered]@{step='Failure';passed=$false;utc=[DateTime]::UtcNow.ToString('o');details=$_.Exception.Message})
    Save-Evidence
    throw
} finally {
    foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null}}catch{}}
    if(-not $finished) {
        $registration=@(Get-Registration)
        if($registration.Count -eq 1 -and $registration[0].InstallLocation.TrimEnd('\') -eq $installDirectory.TrimEnd('\')) {
            try{Invoke-OwnedUninstall 'Failure cleanup'}catch{Write-Warning "Owned installer cleanup requires review: $($_.Exception.Message)"}
        }
    }
    try {
        Assert-ProtectedExistingPaths 'Lifecycle finally'
        if($null -ne $protectedBefore -and $null -ne $shellPinsBefore){[ordered]@{paths=(Get-ExactPathSnapshot $protectedPaths);shellPins=(Get-ShellPinSnapshot $ExpectedLocalAppData)} | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $validationRoot 'protected-after.json') -Encoding utf8}
    }catch {
        $finished=$false
        $steps.Add([ordered]@{step='Protected existing state verification failed';passed=$false;utc=[DateTime]::UtcNow.ToString('o');details=$_.Exception.Message})
        Save-Evidence
        throw
    }
}
