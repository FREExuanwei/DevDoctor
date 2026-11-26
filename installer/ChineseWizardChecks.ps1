# Dot-sourced by validate-installer.ps1. Never launch except from an owned lifecycle.
function Read-OwnedWizard([Diagnostics.Process]$Process,[string]$ExpectedText,[int]$TimeoutSeconds=20) {
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        foreach($window in [InstallerTestInterop]::TopLevelWindows($Process.Id)) {
            $controls=@([InstallerTestInterop]::Children($window.Handle) | Where-Object Visible)
            $text=(@($window.Text)+@($controls | ForEach-Object Text)) -join "`n"
            if($text.Contains($ExpectedText)){return @{window=$window;controls=$controls;text=$text}}
        }
        if($Process.HasExited -and @([InstallerTestInterop]::ProcessTree($Process.Id)).Count -le 1){break}
        Start-Sleep -Milliseconds 50
    }while([DateTime]::UtcNow -lt $deadline)
    throw "The owned Chinese wizard did not show expected text '$ExpectedText'."
}
function Save-ChineseWizardPage([object]$State,[string]$Name) {
    $safeName=$Name.Replace(' ','-')
    $State.text | Set-Content -LiteralPath (Join-Path $validationRoot "$safeName.txt") -Encoding utf8
    Capture-Wizard $State.window.Handle "$safeName.png"
    Record $Name @{visibleText=$State.text;screenshot="$safeName.png";actualNativeWizard=$true}
}
function Click-OwnedWizardButton([object]$State,[string]$TextPrefix) {
    $button=$State.controls | Where-Object {$_.Enabled -and $_.Visible -and $_.ClassName -like '*Button*' -and $_.Text.Replace('&','').StartsWith($TextPrefix)} | Select-Object -First 1
    Assert-Condition ($null -ne $button) "The owned Chinese wizard has no enabled '$TextPrefix' button."
    [InstallerTestInterop]::Click($button.Handle)
}
function Invoke-ChineseWizardInstallation([string]$Name,[string]$ExpectedPreviousVersion='') {
    Assert-ProtectedExistingPaths "$Name before visible setup"
    Assert-NoForeignProductRegistration
    Assert-UnlinkedPathAncestors @($installDirectory,$groupDirectory,$desktopShortcut)
    Assert-OwnedRegistrationOrAbsent
    $process=Start-OwnedProcess $SetupPath @('/SP-','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',"/DIR=$installDirectory","/GROUP=$groupName",'/TASKS=',"/LOG=$(Join-Path $validationRoot "$Name.log")") $true
    $pages=@(@{name='welcome';text='欢迎使用';button='下一步'})
    if($ExpectedPreviousVersion){$pages+=@{name='upgrade';text="检测到 DevDoctor $ExpectedPreviousVersion";button='下一步'}}
    $pages+=@(
        @{name='information-and-license';text='请在继续安装前阅读';button='下一步'},
        @{name='installation-path';text='选择目标位置';button='下一步'},
        @{name='start-menu';text='选择开始菜单文件夹';button='下一步'},
        @{name='desktop-option';text='选择附加任务';button='下一步'},
        @{name='ready';text='准备安装';button='安装'}
    )
    foreach($page in $pages) {
        $state=Read-OwnedWizard $process $page.text
        if($page.name -eq 'upgrade') {
            Assert-Condition ($state.text.Contains('无需先卸载旧版本') -and $state.text.Contains('继续保留')) 'The real upgrade page omits direct-upgrade or retained-data information.'
        }
        Save-ChineseWizardPage $state "$Name-$($page.name)"
        if($page.name -eq 'upgrade') {
            $memo=@($state.controls | Where-Object {$_.ClassName -like '*Memo*'})
            Assert-Condition ($memo.Count -eq 1 -and $state.text.Contains($installDirectory) -and $state.text.Contains('不保证强制终止后的自动回滚')) 'The scrollable upgrade page omits its complete path or final recovery sentence.'
            [InstallerTestInterop]::ScrollMemoToEnd($memo[0].Handle)
            Start-Sleep -Milliseconds 120
            $bottom=Read-OwnedWizard $process '不保证强制终止后的自动回滚'
            Save-ChineseWizardPage $bottom "$Name-upgrade-scrolled-to-end"
        }
        if($page.name -eq 'ready') {
            $allControls=@([InstallerTestInterop]::Children($state.window.Handle))
            Assert-Condition (@($allControls | Where-Object {$_.ClassName -like '*Edit*' -and $_.Text.TrimEnd('\') -eq $installDirectory.TrimEnd('\')}).Count -ge 1) 'The actual wizard directory edit differs from the owned test directory.'
            Assert-Condition (@($allControls | Where-Object {$_.ClassName -like '*Edit*' -and $_.Text -eq $groupName}).Count -ge 1) 'The actual wizard menu edit differs from the owned test group.'
            Assert-ProtectedExistingPaths 'Before actual install click'
        }
        Click-OwnedWizardButton $state $page.button
    }
    # Installation may complete quickly; capture the actual progress page when observable.
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    $progressCaptured=$false;$finish=$null
    do {
        foreach($window in [InstallerTestInterop]::TopLevelWindows($process.Id)) {
            $controls=@([InstallerTestInterop]::Children($window.Handle) | Where-Object Visible)
            $text=(@($window.Text)+@($controls | ForEach-Object Text)) -join "`n"
            $state=@{window=$window;controls=$controls;text=$text}
            if(-not $progressCaptured -and $text.Contains('正在安装')) {Save-ChineseWizardPage $state "$Name-installing";$progressCaptured=$true}
            if($text.Contains('DevDoctor 已安装到您的计算机')) {$finish=$state;break}
        }
        if($finish -or $process.HasExited){break}
        Start-Sleep -Milliseconds 20
    }while([DateTime]::UtcNow -lt $deadline)
    Assert-Condition ($null -ne $finish) 'The owned installer did not reach its Chinese finished page.'
    Save-ChineseWizardPage $finish "$Name-finished"
    Click-OwnedWizardButton $finish '完成'
    Assert-Condition ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'The owned visible installer did not finish successfully.'
    if(-not $progressCaptured) {
        $steps.Add(@{step="$Name-installing";passed=$null;utc=[DateTime]::UtcNow.ToString('o');details='Progress page completed before capture; no visual progress verification claimed.'})
        Save-Evidence
    }
    $registrations=@(Get-Registration)
    Assert-Condition ($registrations.Count -eq 1 -and $registrations[0].DisplayVersion -eq $Version) "Visible setup did not register v$Version."
    Assert-OwnedRegistrationOrAbsent
    Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory 'DevDoctor.exe')) 'Visible setup did not install application files.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $groupDirectory 'DevDoctor.lnk')) 'Visible setup did not create its Start Menu shortcut.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $groupDirectory '卸载 DevDoctor.lnk')) 'Visible setup did not create its Chinese uninstall shortcut.'
    Assert-ProtectedExistingPaths "$Name after visible setup"
    Record $Name @{exitCode=$process.ExitCode;actualVisibleChinesePages=$pages.Count+1;progressCaptured=$progressCaptured;registration=$registrations}
}
function Complete-ChineseUninstallWizard([Diagnostics.Process]$Process,[string]$Name) {
    $confirmation=Read-OwnedWizard $Process '您确定要卸载'
    Assert-Condition ($confirmation.text.Contains('保留') -and $confirmation.text.Contains('不会删除用户数据')) 'The real uninstall confirmation omits data retention.'
    Save-ChineseWizardPage $confirmation "$Name-confirmation"
    Click-OwnedWizardButton $confirmation '是'
    $complete=Read-OwnedWizard $Process '已成功从您的计算机卸载' 60
    Assert-Condition ($complete.text.Contains('均已保留')) 'The actual uninstall completion omits retained user data.'
    Save-ChineseWizardPage $complete "$Name-completion"
    Click-OwnedWizardButton $complete '确定'
}
