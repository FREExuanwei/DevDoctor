# Dot-sourced by validate-installer.ps1; operates only on explicitly owned validation processes.
function Read-PackageWorkspace([Diagnostics.Process]$Process,[string]$BinaryDirectory,[string]$ExpectedRoot,[string]$ExpectedEdition) {
    if (-not ('System.Windows.Automation.AutomationElement' -as [type])) {
        $automationDirectory=Join-Path $validationRoot 'automation-client-libraries'
        New-Item -ItemType Directory -Path $automationDirectory -Force | Out-Null
        foreach($file in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','UIAutomationProvider.dll','UIAutomationClientSideProviders.dll')) {
            $source=Join-Path $BinaryDirectory $file
            if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $automationDirectory -Force}
        }
        Add-Type -Path (Join-Path $automationDirectory 'WindowsBase.dll')
        Add-Type -Path (Join-Path $automationDirectory 'UIAutomationTypes.dll')
        Add-Type -Path (Join-Path $automationDirectory 'UIAutomationClient.dll')
    }
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
    $identity=$root.Current.HelpText
    if($identity -and $identity.Contains('|')) {
        $parts=$identity.Split('|',2)
        Assert-Condition ($parts[0] -eq $ExpectedEdition) "The actual app selected unexpected edition $($parts[0])."
        Assert-Condition ($parts[1].TrimEnd('\') -eq $ExpectedRoot.TrimEnd('\')) "The actual app selected unexpected data root $($parts[1])."
        return [ordered]@{root=$parts[1];edition=$parts[0];readFrom='Production MainWindow accessibility workspace identity'}
    }
    $idProperty=[System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $scope=[System.Windows.Automation.TreeScope]::Descendants
    $pathCondition=[System.Windows.Automation.PropertyCondition]::new($idProperty,'DataWorkspacePath')
    $editionCondition=[System.Windows.Automation.PropertyCondition]::new($idProperty,'DataEdition')
    $pathControl=$root.FindFirst($scope,$pathCondition)
    if (-not $pathControl) {
        $radioCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::RadioButton)
        $settingsRadio=$root.FindAll($scope,$radioCondition) | Where-Object {$_.Current.Name -like '*设置*'} | Select-Object -First 1
        Assert-Condition ($null -ne $settingsRadio) 'The production Settings navigation is not accessible.'
        $selection=$settingsRadio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        do {
            $pathControl=$root.FindFirst($scope,$pathCondition)
            if($pathControl){break}
            Start-Sleep -Milliseconds 100
        }while([DateTime]::UtcNow -lt $deadline)
    }
    $editionControl=$root.FindFirst($scope,$editionCondition)
    Assert-Condition ($null -ne $pathControl -and $null -ne $editionControl) 'The production workspace metadata controls are missing.'
    $pathValue=$pathControl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $editionValue=$editionControl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Assert-Condition ($pathValue.TrimEnd('\') -eq $ExpectedRoot.TrimEnd('\')) "The actual app selected an unexpected data root: $pathValue"
    Assert-Condition ($editionValue -eq $ExpectedEdition) "The actual app selected unexpected edition $editionValue."
    return [ordered]@{root=$pathValue;edition=$editionValue;readFrom='Production Settings page UI Automation'}
}
function Test-PackageApplication {
    param([string]$Name,[string]$BinaryDirectory,[string]$DataRoot,[string]$ExpectedEdition,[string]$Screenshot)
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Join-Path $BinaryDirectory 'DevDoctor.exe'
    $start.UseShellExecute=$false
    $start.WorkingDirectory=$outsideDirectory
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($DataRoot)) 'An explicit controlled data root is required.'
    $start.Environment['DEVDOCTOR_DATA_ROOT']=$DataRoot
    $start.Environment['DOTNET_ROOT']=Join-Path $validationRoot 'no-runtime'
    $start.Environment['DOTNET_ROOT_X64']=Join-Path $validationRoot 'no-runtime'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $start.Environment['PATH']="$env:SystemRoot\System32;$env:SystemRoot"
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$start
    Assert-Condition ($process.Start()) "$Name could not launch its packaged EXE."
    $ownedProcesses.Add($process)
    try {
        [void]$process.WaitForInputIdle(20000)
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        do {$process.Refresh();if($process.MainWindowHandle -ne 0 -or $process.HasExited){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
        Assert-Condition (-not $process.HasExited) "$Name exited before showing its WPF window."
        Assert-Condition ($process.MainWindowHandle -ne 0) "$Name did not show a real WPF window."
        Start-Sleep -Milliseconds 1000
        $runtimeModule=$process.Modules | Where-Object {$_.ModuleName -ieq 'coreclr.dll'} | Select-Object -First 1
        Assert-Condition ($null -ne $runtimeModule) "$Name did not load the .NET runtime."
        Assert-Condition ($runtimeModule.FileName -eq (Join-Path $BinaryDirectory 'coreclr.dll')) "$Name loaded a runtime outside the packaged folder."
        Capture-Wizard $process.MainWindowHandle ($Name.Replace(' ','-') + '-startup.png')
        $workspace=Read-PackageWorkspace $process $BinaryDirectory $DataRoot $ExpectedEdition
        Assert-Condition (Test-Path -LiteralPath (Join-Path $DataRoot 'history.sqlite3')) "$Name did not initialize a real history database."
        Assert-Condition (Test-Path -LiteralPath (Join-Path $DataRoot 'scope.key')) "$Name did not initialize its scope identity."
        if($Screenshot){Capture-Wizard $process.MainWindowHandle $Screenshot}
        $title=$process.MainWindowTitle
        $memory=$process.WorkingSet64
        $runtime=$runtimeModule.FileName
        Assert-Condition ($process.CloseMainWindow()) "$Name did not accept a normal close."
        Assert-Condition ($process.WaitForExit(15000)) "$Name did not close within 15 seconds."
        Record $Name @{windowTitle=$title;workingDirectory=$outsideDirectory;runtime=$runtime;pathContainsSdk=$false;explicitDataOverride=$true;workspace=$workspace;memoryBytes=$memory;normalClose=$true}
    } finally {if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}}
}
function Assert-WorkspaceHashes([string]$Root,[Collections.Specialized.OrderedDictionary]$Before,[string]$Name) {
    $after=Get-DataHashes $Root
    foreach($key in $Before.Keys) {
        Assert-Condition ($after.Contains($key) -and $after[$key] -eq $Before[$key]) "$Name changed another edition's data: $key"
    }
    Record $Name @{root=$Root;files=$Before.Count;sha256Identical=$true}
}
function Test-PortablePackages {
    $zip=Join-Path $artifactRoot "DevDoctor-v$Version-win-x64-portable.zip"
    $firstExtraction=Join-Path $validationRoot 'portable-extracted-A'
    $secondExtraction=Join-Path $validationRoot 'portable-extracted-B'
    Expand-Archive -LiteralPath $zip -DestinationPath $firstExtraction
    Expand-Archive -LiteralPath $zip -DestinationPath $secondExtraction
    $firstBinary=Join-Path $firstExtraction "DevDoctor-v$Version-win-x64-portable"
    $secondBinary=Join-Path $secondExtraction "DevDoctor-v$Version-win-x64-portable"
    $firstRoot=Join-Path $validationRoot 'portable-data-A'
    $secondRoot=Join-Path $validationRoot 'portable-data-B'
    foreach($root in @($firstRoot,$secondRoot)) {
        Assert-Condition (-not(Test-Path -LiteralPath $root)) 'Refusing to use an existing portable test data root.'
        New-Item -ItemType Directory -Path $root | Out-Null
        Get-ChildItem -LiteralPath $SeedDataPath -Force | Copy-Item -Destination $root -Recurse -Force
    }
    foreach($binary in @($firstBinary,$secondBinary)) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $binary 'portable.mode')) 'The real ZIP is missing its portable marker.'
        Assert-Condition (Test-Path -LiteralPath (Join-Path $binary 'DevDoctor.exe')) 'The real ZIP is missing its app executable.'
    }
    $firstBefore=Get-DataHashes $firstRoot
    $secondBefore=Get-DataHashes $secondRoot
    Test-PackageApplication -Name 'Actual ZIP portable standalone startup A' -BinaryDirectory $firstBinary -DataRoot $firstRoot -ExpectedEdition 'Controlled' -Screenshot 'portable-A-workspace.png'
    Assert-WorkspaceHashes $secondRoot $secondBefore 'Portable A leaves second controlled data intact'
    $firstAfter=Get-DataHashes $firstRoot
    Test-PackageApplication -Name 'Actual ZIP portable standalone startup B' -BinaryDirectory $secondBinary -DataRoot $secondRoot -ExpectedEdition 'Controlled' -Screenshot 'portable-B-workspace.png'
    Assert-WorkspaceHashes $firstRoot $firstAfter 'Portable B leaves first controlled data intact'
    Assert-WorkspaceHashes $defaultDataDirectory $defaultDataBefore 'Portable packages leave default installed data intact'
    Assert-DataPreserved 'Portable packages leave controlled lifecycle data intact'
    Record 'Real ZIP extraction and controlled data isolation' @{zipSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash;portableA=$firstRoot;portableB=$secondRoot;installedRoot=$dataDirectory;defaultResolverTested=$false;dataRetainedAfterValidation=$true}
}
function Test-NormalInstalledPackage {
    Test-PackageApplication -Name 'Installed package controlled workspace startup' -BinaryDirectory $installDirectory -DataRoot $dataDirectory -ExpectedEdition 'Controlled' -Screenshot 'installed-controlled-workspace.png'
    Record 'Default resolver scope' @{defaultResolverTested=$false;reason='All lifecycle launches explicitly select owned data roots; default Installed user data is never launched.'}
}
