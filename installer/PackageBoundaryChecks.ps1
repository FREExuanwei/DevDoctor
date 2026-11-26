# Additional integration checks use only newly created paths under this lifecycle's root.
function Get-PortablePackageIdentity([string]$BinaryDirectory,[string]$ExpectedVersion) {
    Assert-UnlinkedPathAncestors @($BinaryDirectory)
    $executable=Join-Path $BinaryDirectory 'DevDoctor.exe'
    $assembly=Join-Path $BinaryDirectory 'DevDoctor.dll'
    Assert-UnlinkedPathAncestors @($executable,$assembly)
    $name=[Reflection.AssemblyName]::GetAssemblyName($assembly)
    $executableVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    $expected="$ExpectedVersion.0"
    Assert-Condition ($name.Name -eq 'DevDoctor' -and $name.Version.ToString() -eq $expected -and $executableVersion -eq $expected) "Read-only package binary/assembly version does not match expected package version $ExpectedVersion."
    return [ordered]@{expectedPackageVersion=$ExpectedVersion;binaryDirectory=$BinaryDirectory;assemblyVersion=$name.Version.ToString();executableVersion=$executableVersion;assemblySha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash;executableSha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash}
}
function Test-PackageBoundaries {
    Assert-Condition (@(Get-Registration).Count -eq 0) 'Boundary checks require no installed validation product.'
    Assert-NoForeignProductRegistration
    Assert-ProtectedExistingPaths 'Before package boundary checks'
    $guardDirectory=Join-Path $validationRoot 'portable-protection-target'
    Assert-UnlinkedPathAncestors @($guardDirectory)
    Assert-Condition (-not(Test-Path -LiteralPath $guardDirectory)) 'Portable protection target must be new.'
    New-Item -ItemType Directory -Path $guardDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $guardDirectory 'portable.mode'),'Owned negative test portable marker')
    [IO.File]::WriteAllText((Join-Path $guardDirectory 'keep-user-file.txt'),'Installer must preserve these exact test bytes.')
    $before=Get-ExactPathSnapshot @($guardDirectory)
    $process=Start-OwnedProcess $SetupPath @('/SP-','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',"/DIR=$guardDirectory","/GROUP=$groupName",'/TASKS=',"/LOG=$(Join-Path $validationRoot 'portable-protection.log')") $true
    foreach($expected in @('欢迎使用','请在继续安装前阅读','选择目标位置','选择开始菜单文件夹','选择附加任务','准备安装')) {
        $state=Read-OwnedWizard $process $expected
        if($expected -eq '准备安装') {
            $edits=@([InstallerTestInterop]::Children($state.window.Handle) | Where-Object {$_.ClassName -like '*Edit*'})
            Assert-Condition (@($edits | Where-Object {$_.Text.TrimEnd('\') -eq $guardDirectory}).Count -eq 1) 'Guard check is not targeting its owned portable folder.'
            Assert-Condition (@($edits | Where-Object {$_.Text -eq $groupName}).Count -eq 1) 'Guard check is not targeting its owned menu group.'
            Click-OwnedWizardButton $state '安装'
        }else{
            Click-OwnedWizardButton $state '下一步'
            if($expected -eq '选择目标位置') {
                $existing=Read-OwnedWizard $process '文件夹已存在'
                Assert-Condition ($existing.text.Contains($guardDirectory)) 'Existing-folder consent is not for the owned negative-test directory.'
                Save-ChineseWizardPage $existing 'Owned portable target existing-folder confirmation'
                Click-OwnedWizardButton $existing '是'
            }
        }
    }
    $errorState=Read-OwnedWizard $process '此文件夹包含 DevDoctor 便携版'
    Assert-Condition ($errorState.text.Contains('请选择单独的安装文件夹') -and $errorState.text.Contains('用户数据未被更改')) 'The real portable-directory error lacks its Chinese explanation.'
    Save-ChineseWizardPage $errorState 'Portable directory Chinese protection error'
    Click-OwnedWizardButton $errorState '取消'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        if($process.HasExited){break}
        foreach($window in [InstallerTestInterop]::TopLevelWindows($process.Id)) {
            $yes=@([InstallerTestInterop]::Children($window.Handle) | Where-Object {$_.Visible -and $_.Enabled -and $_.ClassName -like '*Button*' -and $_.ControlId -eq 6})
            if($yes.Count -eq 1){[InstallerTestInterop]::Click($yes[0].Handle)}
        }
        Start-Sleep -Milliseconds 50
    }while([DateTime]::UtcNow -lt $deadline)
    Assert-Condition $process.HasExited 'Protected installation did not cancel.'
    Assert-Condition ($process.ExitCode -ne 0) 'A protected portable folder unexpectedly accepted installation.'
    Assert-Condition (@(Get-Registration).Count -eq 0) 'Portable protection failure left a product registration.'
    Assert-ExactSnapshot $before (Get-ExactPathSnapshot @($guardDirectory)) 'Protected portable directory'
    Assert-ProtectedExistingPaths 'After portable protection error'
    Record 'Actual Chinese portable-directory refusal preserves files and leaves no registration' @{exitCode=$process.ExitCode;exactBytesUnchanged=$true;errorScreenshot='Portable-directory-Chinese-protection-error.png'}

    $extraction=Join-Path $validationRoot 'readonly-portable-extracted'
    $data=Join-Path $validationRoot 'readonly-portable-data'
    Assert-UnlinkedPathAncestors @($extraction,$data)
    Assert-Condition (-not(Test-Path -LiteralPath $extraction) -and -not(Test-Path -LiteralPath $data)) 'Readonly package roots must be new.'
    Expand-Archive -LiteralPath (Join-Path $artifactRoot "DevDoctor-v$Version-win-x64-portable.zip") -DestinationPath $extraction
    $binary=Join-Path $extraction "DevDoctor-v$Version-win-x64-portable"
    $packageIdentity=Get-PortablePackageIdentity $binary $Version
    Record 'Read-only package binary and assembly identity before launch' $packageIdentity
    New-Item -ItemType Directory -Path $data | Out-Null
    Get-ChildItem -LiteralPath $SeedDataPath -Force | Copy-Item -Destination $data -Recurse -Force
    $ownedPrefix=[IO.Path]::GetFullPath($validationRoot).TrimEnd('\')+'\'
    Assert-Condition ([IO.Path]::GetFullPath($binary).StartsWith($ownedPrefix,[StringComparison]::OrdinalIgnoreCase)) 'Refusing ACL changes outside the owned validation root.'
    $originalAcl=Get-Acl -LiteralPath $binary
    $originalSddl=$originalAcl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    $deniedAcl=Get-Acl -LiteralPath $binary
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $rights=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    $inherit=[Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny)
    [void]$deniedAcl.AddAccessRule($rule)
    try {
        Set-Acl -LiteralPath $binary -AclObject $deniedAcl
        $denied=$false
        try{[IO.File]::WriteAllText((Join-Path $binary 'write-probe-must-fail.txt'),'Must not be written.')}catch [UnauthorizedAccessException]{$denied=$true}
        Assert-Condition $denied 'The package directory is not actually write-protected for the launching user.'
        Record 'Read-only package ACL rejects a real write by the current user' @{ownedDirectory=$binary;writeDenied=$true;administratorRequired=$false}
        Test-PackageApplication -Name 'Read-only real ZIP package standalone startup' -BinaryDirectory $binary -DataRoot $data -ExpectedEdition 'Controlled' -Screenshot 'readonly-package-workspace.png'
    }finally {
        Set-Acl -LiteralPath $binary -AclObject $originalAcl
        $restored=(Get-Acl -LiteralPath $binary).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
        Assert-Condition ($restored -eq $originalSddl) 'The owned readonly-test directory ACL was not restored.'
    }
    Record 'Read-only test original ACL restored' @{restored=$true;ownedDirectory=$binary}
    Assert-ProtectedExistingPaths 'After package boundary checks'
}
